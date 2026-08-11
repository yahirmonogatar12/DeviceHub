using System.Diagnostics;
using DeviceHub.RemoteHost.Capture;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Modo --encode-test: captura, codifica y mide la cadena entera.
///
/// La Fase 2 no termina porque salga H.264. Termina cuando esta cadena sostiene
/// el ritmo con latencia conocida, bitrate controlado y sin crecer en memoria:
///
///   captura 1080p -> encoder -> 30/60 FPS sostenidos -> sin drops -> RAM estable
///
/// El escenario es una etiqueta: quien mueve la pantalla es quien ejecuta el
/// test. Un encoder se ve espectacular con el escritorio quieto y se cae cuando
/// cambia la pantalla entera, asi que el numero sin el escenario no dice nada.
/// </summary>
public static class EncodeTest
{
    public static int Run(int adapterIndex, int outputIndex, int seconds, int fps, int bitrate, string scenario, string? output)
    {
        MediaFactory.MFStartup(true).CheckError();

        try
        {
            using var capture = new DxgiDesktopCapture(adapterIndex, outputIndex);
            using var encoder = new H264Encoder(
                capture.Device, capture.Width, capture.Height, fps, bitrate, capture.AdapterVendorId);
            using var gpu = new GpuCounters(adapterIndex);

            var caps = encoder.Capabilities;

            Console.WriteLine($"Codificando {seconds} s con {caps.Name} ({(caps.Hardware ? "hardware" : "software")}).");
            Console.WriteLine("Genera el movimiento del escenario ahora.");
            Console.WriteLine();

            var proceso = Process.GetCurrentProcess();
            var ramInicio = proceso.PrivateMemorySize64;
            var cpuInicio = proceso.TotalProcessorTime;
            var trazaRam = new List<long> { ramInicio };
            var usoGpu = new List<double>();

            var latenciasEncode = new List<long>();
            var latenciasCadena = new List<long>();
            long framesCapturados = 0, framesCodificados = 0, keyframes = 0, bytes = 0;

            FileStream? archivo = output is null ? null : File.Create(output);

            var duracion = TimeSpan.FromSeconds(seconds);
            var reloj = Stopwatch.StartNew();
            var siguienteMuestra = TimeSpan.FromSeconds(5);

            while (reloj.Elapsed < duracion)
            {
                using var frame = capture.CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();

                if (frame is null || !frame.DesktopChanged)
                    continue;

                framesCapturados++;
                var edadCaptura = capture.LastFrameAgeUs;

                var antes = Stopwatch.GetTimestamp();
                var codificados = encoder.Encode(frame, CancellationToken.None);
                var msEncode = Micros(Stopwatch.GetTimestamp() - antes);

                latenciasEncode.Add(msEncode);

                foreach (var salida in codificados)
                {
                    framesCodificados++;
                    bytes += salida.Payload.Length;

                    if (salida.IsKeyFrame)
                        keyframes++;

                    // Cadena completa: desde que el escritorio presento el frame
                    // hasta que sale codificado. Es la latencia que va a sentir
                    // el tecnico, no la del encoder aislado.
                    latenciasCadena.Add(edadCaptura + msEncode);

                    archivo?.Write(salida.Payload);
                }

                if (reloj.Elapsed >= siguienteMuestra)
                {
                    proceso.Refresh();
                    trazaRam.Add(proceso.PrivateMemorySize64);

                    if (gpu.VideoEncodePercent() is { } uso)
                        usoGpu.Add(uso);

                    siguienteMuestra += TimeSpan.FromSeconds(5);
                }
            }

            reloj.Stop();
            archivo?.Dispose();

            proceso.Refresh();
            var ramFin = proceso.PrivateMemorySize64;
            var cpu = (proceso.TotalProcessorTime - cpuInicio).TotalSeconds
                      / reloj.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;

            latenciasEncode.Sort();
            latenciasCadena.Sort();

            var segundos = reloj.Elapsed.TotalSeconds;

            Console.WriteLine($"Scenario:      {scenario}");
            Console.WriteLine($"Capture:       DXGI Desktop Duplication");
            Console.WriteLine($"Adapter:       {capture.Adapter}");
            Console.WriteLine($"Encoder:       H264");
            Console.WriteLine($"Hardware:      {(caps.Hardware ? "TRUE" : "FALSE")}");
            Console.WriteLine($"MFT:           {caps.Name}");
            Console.WriteLine($"Async:         {(caps.Asynchronous ? "TRUE" : "FALSE")}");
            Console.WriteLine($"Input:         D3D11 Texture / {caps.InputFormat}");
            Console.WriteLine($"Resolution:    {capture.Width}x{capture.Height}");
            Console.WriteLine($"Target FPS:    {fps}");
            Console.WriteLine($"Captured:      {framesCapturados}");
            Console.WriteLine($"Encoded:       {framesCodificados}");
            Console.WriteLine($"FPS:           {framesCodificados / segundos:0.00}");
            Console.WriteLine($"Encode avg:    {CaptureStats.AverageMs(latenciasEncode):0.00} ms");
            Console.WriteLine($"Encode p50:    {CaptureStats.PercentileMs(latenciasEncode, 0.50):0.00} ms");
            Console.WriteLine($"Encode p95:    {CaptureStats.PercentileMs(latenciasEncode, 0.95):0.00} ms");
            Console.WriteLine($"Encode p99:    {CaptureStats.PercentileMs(latenciasEncode, 0.99):0.00} ms");
            Console.WriteLine($"Cap->Enc p50:  {CaptureStats.PercentileMs(latenciasCadena, 0.50):0.00} ms");
            Console.WriteLine($"Cap->Enc p95:  {CaptureStats.PercentileMs(latenciasCadena, 0.95):0.00} ms");
            Console.WriteLine($"Bitrate:       {bytes * 8 / segundos / 1_000_000:0.00} Mbps  (objetivo {bitrate / 1_000_000.0:0.0})");
            Console.WriteLine($"Frame avg:     {(framesCodificados == 0 ? 0 : bytes / framesCodificados / 1024.0):0.0} KB");
            Console.WriteLine($"Keyframes:     {keyframes}");
            Console.WriteLine($"CPU:           {cpu:0.0}%");
            Console.WriteLine($"GPU Encode:    {(usoGpu.Count == 0 ? "n/d" : $"{usoGpu.Average():0.0}%")}");
            Console.WriteLine($"Dropped:       {encoder.Dropped}  (captura {capture.Dropped})");
            Console.WriteLine($"RAM start:     {ramInicio / 1024 / 1024} MB");
            Console.WriteLine($"RAM end:       {ramFin / 1024 / 1024} MB");
            Console.WriteLine($"VRAM:          {(gpu.VideoMemoryBytes() is { } v ? $"{v / 1024 / 1024} MB" : "n/d")}");
            Console.WriteLine($"RAM trace:     {string.Join(" ", trazaRam.Select(b => b / 1024 / 1024))} MB");

            if (output is not null)
                Console.WriteLine($"\nH.264 en {output} ({new FileInfo(output).Length / 1024 / 1024} MB). Abrelo en un reproductor.");

            return 0;
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            Console.Error.WriteLine($"No se puede capturar: {ex.Message}");
            return 2;
        }
        catch (VideoEncoderUnavailableException ex)
        {
            Console.Error.WriteLine($"No se puede codificar: {ex.Message}");
            return 3;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static long Micros(long ticks) => ticks * 1_000_000L / Stopwatch.Frequency;
}
