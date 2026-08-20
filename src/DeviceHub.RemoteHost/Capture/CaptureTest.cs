using System.Diagnostics;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Modo de diagnostico --capture-test: captura durante un rato y saca los
/// numeros que deciden si la Fase 1 vale.
///
/// Lo que hay que saber para no engañarse: los FPS cuentan SOLO frames con
/// imagen nueva. Con la pantalla quieta, Desktop Duplication no entrega 30
/// imagenes por segundo -- entrega timeouts, y estan en su propia linea. Para
/// medir de verdad hay que mover algo por pantalla mientras corre.
/// </summary>
public static class CaptureTest
{
    public static int Run(int adapterIndex, int outputIndex, int seconds)
    {
        DxgiDesktopCapture capture;

        try
        {
            capture = new DxgiDesktopCapture(adapterIndex, outputIndex);
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            Console.Error.WriteLine($"No se puede capturar: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Lo que hay en esta maquina:");

            foreach (var linea in DxgiDesktopCapture.Enumerate())
                Console.Error.WriteLine($"  {linea}");

            return 2;
        }

        using (capture)
        {
            var proceso = Process.GetCurrentProcess();
            var ramInicio = proceso.PrivateMemorySize64;
            var ramPico = ramInicio;

            var edades = new List<long>(seconds * 200);
            var trazaRam = new List<long> { ramInicio };
            long frames = 0, sinCambio = 0;

            Console.WriteLine($"Capturando {seconds} s en {capture.Output} ({capture.Width}x{capture.Height}).");
            Console.WriteLine("Mueve una ventana o reproduce un video: con la pantalla quieta no hay frames que medir.");
            Console.WriteLine();

            var duracion = TimeSpan.FromSeconds(seconds);
            var reloj = Stopwatch.StartNew();
            var siguienteMuestraRam = TimeSpan.FromSeconds(5);

            while (reloj.Elapsed < duracion)
            {
                using var frame = capture.CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();

                if (frame is not null)
                {
                    if (frame.DesktopChanged)
                    {
                        frames++;
                        edades.Add(capture.LastFrameAgeUs);
                    }
                    else
                    {
                        sinCambio++;
                    }
                }

                // El pico importa mas que el valor final: una fuga que el GC
                // recorta justo al terminar pasaria desapercibida midiendo solo
                // el antes y el despues.
                if (reloj.Elapsed >= siguienteMuestraRam)
                {
                    proceso.Refresh();
                    ramPico = Math.Max(ramPico, proceso.PrivateMemorySize64);
                    trazaRam.Add(proceso.PrivateMemorySize64);
                    siguienteMuestraRam += TimeSpan.FromSeconds(5);
                }
            }

            reloj.Stop();

            proceso.Refresh();
            var ramFin = proceso.PrivateMemorySize64;
            ramPico = Math.Max(ramPico, ramFin);

            edades.Sort();

            Console.WriteLine($"Capture:       DXGI Desktop Duplication");
            Console.WriteLine($"Adapter:       {capture.Adapter}");
            Console.WriteLine($"Output:        {capture.Output}");
            Console.WriteLine($"Resolution:    {capture.Width}x{capture.Height}");
            Console.WriteLine($"Frames:        {frames}");
            Console.WriteLine($"FPS:           {frames / reloj.Elapsed.TotalSeconds:0.00}");
            Console.WriteLine($"Capture avg:   {CaptureStats.AverageMs(edades):0.00} ms");
            Console.WriteLine($"Capture p50:   {CaptureStats.PercentileMs(edades, 0.50):0.00} ms");
            Console.WriteLine($"Capture p95:   {CaptureStats.PercentileMs(edades, 0.95):0.00} ms");
            Console.WriteLine($"Capture p99:   {CaptureStats.PercentileMs(edades, 0.99):0.00} ms");
            Console.WriteLine($"Capture max:   {(edades.Count == 0 ? 0 : edades[^1] / 1000d):0.00} ms");
            Console.WriteLine($"Timeouts:      {capture.Timeouts}");
            Console.WriteLine($"AccessLost:    {capture.AccessLostRecoveries}");
            Console.WriteLine($"Dropped:       {capture.Dropped}");
            Console.WriteLine($"RAM start:     {ramInicio / 1024 / 1024} MB");
            Console.WriteLine($"RAM end:       {ramFin / 1024 / 1024} MB");
            Console.WriteLine($"RAM peak:      {ramPico / 1024 / 1024} MB");

            // La curva, no solo los extremos: dos puntos no distinguen una fuga
            // de un cache nativo que se calienta y se para. Una muestra cada 5 s.
            trazaRam.Add(ramFin);
            Console.WriteLine($"RAM trace:     {string.Join(" ", trazaRam.Select(b => b / 1024 / 1024))} MB");

            if (capture.ResolutionChanges > 0)
                Console.WriteLine($"Resol.changes: {capture.ResolutionChanges}");

            Console.WriteLine();
            Console.WriteLine($"Solo puntero:  {sinCambio}  (movimientos de raton sin imagen nueva; no cuentan como frames)");

            if (frames == 0)
                Console.WriteLine("Cero frames con imagen nueva: la pantalla estuvo quieta todo el rato.");

            return 0;
        }
    }
}
