using System.IO;
using System.Runtime.InteropServices;
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

        // Lanzar una sesion: pide los tickets y arranca los dos extremos. Es una
        // consola, no una ventana, asi que se hace y se sale.
        if (e.Args.Contains("--start-session"))
        {
            AttachConsole(-1);

            var codigo = SessionLauncher.RunAsync(
                Texto(e.Args, "--admin") ?? "https://192.168.1.10:5443",
                Texto(e.Args, "--server") ?? "https://192.168.1.10:5443",
                Texto(e.Args, "--machine-id") ?? string.Empty,
                Texto(e.Args, "--user") ?? "admin",
                Texto(e.Args, "--host-exe"),
                e.Args.Contains("--allow-untrusted")).GetAwaiter().GetResult();

            Shutdown(codigo);
            return;
        }

        if (e.Args.Contains("--relay-test"))
        {
            new RelayWindow(
                Texto(e.Args, "--server") ?? "https://192.168.1.10:5443",
                Texto(e.Args, "--session") ?? string.Empty,
                Texto(e.Args, "--machine-id") ?? Environment.MachineName,
                e.Args.Contains("--allow-untrusted")).Show();

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

    /// <summary>Un WinExe no trae consola. Sin esto, el launcher no puede pedir
    /// la contrasena ni informar de nada.</summary>
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static string? Texto(string[] args, string nombre)
    {
        var posicion = Array.IndexOf(args, nombre);

        return posicion >= 0 && posicion + 1 < args.Length ? args[posicion + 1] : null;
    }

    private static int Numero(string[] args, string nombre, int porDefecto)
        => int.TryParse(Texto(args, nombre), out var valor) && valor > 0 ? valor : porDefecto;
}
