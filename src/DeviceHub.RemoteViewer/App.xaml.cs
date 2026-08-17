using System.IO;
using System.Windows;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Visor de control remoto: corre en la PC DEL TECNICO. Lo abre el dashboard al
/// pulsar CONTROLAR PC, y recibe la sesion y su ticket por stdin, nunca por
/// linea de comandos.
///
/// `--play` es un modo de diagnostico, el equivalente de `--encode-test` en el
/// host: reproduce un archivo H.264 sin red para medir la mitad receptora del
/// sistema por separado.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--relay-test"))
        {
            new RelayWindow(
                Texto(e.Args, "--server") ?? "https://192.168.1.10:5443",
                Texto(e.Args, "--session") ?? string.Empty,
                Texto(e.Args, "--machine-id") ?? Environment.MachineName,
                e.Args.Contains("--allow-untrusted"),
                // El pin no es secreto: es el hash de una clave publica. Por eso
                // puede viajar por argumento y el ticket no.
                Texto(e.Args, "--pin") ?? string.Empty).Show();

            return;
        }

        var ruta = Texto(e.Args, "--play");

        if (ruta is null)
        {
            new MainWindow().Show();
            return;
        }

        if (!File.Exists(ruta))
        {
            MessageBox.Show(
                $"No existe {ruta}.\n\nSe genera con:\n  DeviceHub.RemoteHost --encode-test --save salida.h264",
                "DeviceHub", MessageBoxButton.OK, MessageBoxImage.Warning);

            Shutdown(2);
            return;
        }

        new PlayerWindow(ruta, Numero(e.Args, "--fps", 60), e.Args.Contains("--loop")).Show();
    }

    private static string? Texto(string[] args, string nombre)
    {
        var posicion = Array.IndexOf(args, nombre);

        return posicion >= 0 && posicion + 1 < args.Length ? args[posicion + 1] : null;
    }

    private static int Numero(string[] args, string nombre, int porDefecto)
        => int.TryParse(Texto(args, nombre), out var valor) && valor > 0 ? valor : porDefecto;
}
