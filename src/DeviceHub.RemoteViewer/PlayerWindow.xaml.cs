using System.Diagnostics;
using System.IO;
using System.Windows;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteViewer.Decode;
using DeviceHub.RemoteViewer.Render;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Fase 3: reproduce en local el H.264 que genera `RemoteHost --encode-test`.
///
/// Sin red todavia. La cadena que se prueba aqui es la mitad que falta del
/// sistema, y probarla contra un archivo la deja medible sin depender del relay:
///
///     Annex-B -> decodificador MF (DXVA) -> textura NV12 -> swapchain -> ventana
///
/// Nunca se convierte un frame a mapa de bits ni baja a RAM.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly string _ruta;
    private readonly int _fps;
    private readonly bool _bucle;

    private readonly CancellationTokenSource _cancelacion = new();
    private Thread? _hilo;

    public PlayerWindow(string ruta, int fps, bool bucle)
    {
        InitializeComponent();

        _ruta = ruta;
        _fps = fps;
        _bucle = bucle;

        Title = $"DeviceHub - {Path.GetFileName(ruta)}";

        Loaded += (_, _) =>
        {
            _hilo = new Thread(Reproducir) { IsBackground = true, Name = "devicehub-player" };
            _hilo.Start();
        };

        Closed += (_, _) =>
        {
            _cancelacion.Cancel();
            _hilo?.Join(TimeSpan.FromSeconds(2));
            _cancelacion.Dispose();
        };
    }

    private void Reproducir()
    {
        try
        {
            MediaFactory.MFStartup(true).CheckError();
        }
        catch (Exception ex)
        {
            Mostrar($"Media Foundation no arranco: {ex.Message}");
            return;
        }

        try
        {
            // Se espera desde ESTE hilo, no con Dispatcher.Invoke: quien crea la
            // ventana hija es el de la interfaz, y bloquearlo esperando su propio
            // trabajo es un interbloqueo garantizado.
            var hwnd = Video.WaitForWindow(TimeSpan.FromSeconds(5));

            if (hwnd == IntPtr.Zero)
            {
                Mostrar("La superficie de video no llego a crearse.");
                return;
            }

            var flujo = File.ReadAllBytes(_ruta);
            var unidades = H264AnnexB.Split(flujo);

            if (unidades.Count == 0)
            {
                Mostrar($"{Path.GetFileName(_ruta)} no contiene ninguna imagen H.264 Annex-B.");
                return;
            }

            Bucle(hwnd, flujo, unidades);
        }
        catch (OperationCanceledException)
        {
            // Ventana cerrada.
        }
        catch (Exception ex)
        {
            Mostrar($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private void Bucle(IntPtr hwnd, byte[] flujo, List<AccessUnit> unidades)
    {
        using var device = VideoPresenter.CreateDevice();
        using var decoder = new H264Decoder(device, 1920, 1080);

        VideoPresenter? presentador = null;

        var proceso = Process.GetCurrentProcess();
        var ramInicio = proceso.PrivateMemorySize64;

        var latencias = new List<long>();
        var intervaloUs = 1_000_000L / _fps;

        long presentados = 0, tarde = 0, vueltas = 0;
        var reloj = Stopwatch.StartNew();
        var siguienteAviso = TimeSpan.Zero;

        try
        {
            do
            {
                for (var i = 0; i < unidades.Count; i++)
                {
                    _cancelacion.Token.ThrowIfCancellationRequested();

                    var unidad = unidades[i];
                    var antes = Stopwatch.GetTimestamp();

                    var frames = decoder.Decode(
                        flujo, unidad.Offset, unidad.Length, presentados * intervaloUs);

                    latencias.Add(Micros(Stopwatch.GetTimestamp() - antes));

                    foreach (var frame in frames)
                    {
                        using (frame)
                        {
                            // El presentador se crea con el tamano REAL, que sale
                            // del SPS y solo se conoce con el primer frame.
                            presentador ??= new VideoPresenter(
                                device, hwnd, decoder.Width, decoder.Height,
                                decoder.Aperture.X, decoder.Aperture.Y,
                                decoder.Aperture.Width, decoder.Aperture.Height);

                            // Ritmo por reloj de pared y no por Sleep acumulado:
                            // sumar esperas arrastra el error de cada una y el
                            // video se va quedando atras.
                            var objetivo = TimeSpan.FromMicroseconds(presentados * intervaloUs);
                            var espera = objetivo - reloj.Elapsed;

                            if (espera > TimeSpan.Zero)
                                Thread.Sleep(espera);
                            else if (espera < TimeSpan.FromMicroseconds(-intervaloUs))
                                tarde++;

                            presentador.Present(frame.Texture, frame.Subresource);
                            presentados++;
                        }
                    }

                    if (reloj.Elapsed >= siguienteAviso)
                    {
                        proceso.Refresh();

                        Informe(
                            decoder, presentador, latencias, presentados, tarde, vueltas,
                            reloj.Elapsed, ramInicio, proceso.PrivateMemorySize64);

                        siguienteAviso += TimeSpan.FromMilliseconds(500);
                    }
                }

                vueltas++;

                foreach (var frame in decoder.Drain())
                    frame.Dispose();
            }
            while (_bucle && !_cancelacion.IsCancellationRequested);

            proceso.Refresh();

            Informe(
                decoder, presentador, latencias, presentados, tarde, vueltas,
                reloj.Elapsed, ramInicio, proceso.PrivateMemorySize64, terminado: true);
        }
        finally
        {
            presentador?.Dispose();
        }
    }

    private void Informe(
        H264Decoder decoder, VideoPresenter? presentador, List<long> latencias,
        long presentados, long tarde, long vueltas, TimeSpan transcurrido,
        long ramInicio, long ramAhora, bool terminado = false)
    {
        var ordenadas = latencias.Order().ToList();
        var segundos = Math.Max(transcurrido.TotalSeconds, 0.001);

        var texto =
            $"{decoder.Capabilities.Name}   {(decoder.Capabilities.Hardware ? "hardware" : "software")}   " +
            $"{presentador?.Width ?? decoder.Width}x{presentador?.Height ?? decoder.Height}\n" +
            $"FPS {presentados / segundos:0.00}   objetivo {_fps}   " +
            $"decode p50 {Percentil(ordenadas, 0.50):0.00} ms   p95 {Percentil(ordenadas, 0.95):0.00} ms   " +
            $"frames {presentados}   tarde {tarde}   vueltas {vueltas}   " +
            $"RAM {ramAhora / 1024 / 1024} MB (inicio {ramInicio / 1024 / 1024})   " +
            $"{transcurrido:hh\\:mm\\:ss}" +
            (decoder.StreamChanges > 0 ? $"   renegociaciones {decoder.StreamChanges}" : string.Empty) +
            (decoder.LastIssue is null ? string.Empty : $"\nMFT: {decoder.LastIssue}") +
            (terminado ? "\nFin del archivo." : string.Empty);

        Mostrar(texto);
    }

    private static double Percentil(List<long> ordenadas, double p)
    {
        if (ordenadas.Count == 0)
            return 0;

        var indice = Math.Clamp((int)Math.Ceiling(p * ordenadas.Count) - 1, 0, ordenadas.Count - 1);
        return ordenadas[indice] / 1000.0;
    }

    private static long Micros(long ticks) => ticks * 1_000_000L / Stopwatch.Frequency;

    private void Mostrar(string texto)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => Estado.Text = texto);
    }
}
