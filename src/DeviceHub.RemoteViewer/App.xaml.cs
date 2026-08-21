using System.IO;
using DeviceHub.Remote.Contracts;
using System.Windows;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Visor de control remoto: corre en la PC DEL TECNICO. Lo abre el dashboard al
/// pulsar CONTROLAR PC, y recibe la sesion y su ticket por stdin, nunca por
/// linea de comandos.
///
/// UN PROCESO, VARIAS SESIONES. La primera llega por argumentos mas el ticket
/// por stdin, como siempre. Las siguientes llegan enteras por stdin, y cada una
/// abre su pestaña en la misma ventana. Por eso stdin se queda abierto en vez de
/// cerrarse tras el primer ticket: es el canal por el que el dashboard dice
/// "tambien esta otra PC".
///
/// `--play` es un modo de diagnostico, el equivalente de `--encode-test` en el
/// host: reproduce un archivo H.264 sin red para medir la mitad receptora del
/// sistema por separado.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Lo que precede a una sesion nueva por stdin. Detras van sus argumentos, y
    /// en la linea siguiente su ticket.
    ///
    /// Con marca y no a secas porque la primera linea de stdin sigue siendo el
    /// ticket pelado de la sesion que vino por argumentos: sin algo que las
    /// distinga, un ticket y unos argumentos son las dos cadenas de texto.
    /// </summary>
    public const string Marca = "+sesion ";

    private ConsolaWindow? _consola;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--relay-test"))
        {
            _consola = new ConsolaWindow();
            _consola.Show();

            // El ticket lo lee la consola y no la sesion: con varias pestañas
            // serian varios hilos leyendo la misma tuberia.
            Abrir(e.Args, BootstrapTicket.Read());

            // Y se sigue escuchando. El hilo es de fondo: si nadie manda nada
            // mas, la ventana manda y el proceso vive lo que ella viva.
            new Thread(Escuchar)
            {
                IsBackground = true,
                Name = "devicehub-visor-stdin"
            }.Start();

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

        new PlayerWindow(
            ruta, Numero(e.Args, "--fps", 60), e.Args.Contains("--loop"),
            e.Args.Contains("--h265") ? VideoCodec.H265 : VideoCodec.H264).Show();
    }

    /// <summary>
    /// Espera sesiones nuevas por stdin mientras el dashboard mantenga la
    /// tuberia abierta. Al cerrarse -- o si el dashboard es de una version que no
    /// manda mas de una -- ReadLine devuelve null y esto termina sin ruido.
    /// </summary>
    private void Escuchar()
    {
        try
        {
            while (Console.ReadLine() is { } linea)
            {
                if (!linea.StartsWith(Marca, StringComparison.Ordinal))
                    continue;

                var argumentos = linea[Marca.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ticket = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(ticket))
                    continue;

                Dispatcher.Invoke(() => Abrir(argumentos, ticket.Trim()));
            }
        }
        catch (Exception)
        {
            // La tuberia se cerro. No es un fallo: las sesiones abiertas siguen.
        }
    }

    private void Abrir(string[] argumentos, string? ticket)
        => _consola?.Abrir(new SesionRemota(
            Texto(argumentos, "--server") ?? "https://192.168.1.10:5443",
            Texto(argumentos, "--session") ?? string.Empty,
            Texto(argumentos, "--machine-id") ?? Environment.MachineName,
            argumentos.Contains("--allow-untrusted"),

            // El pin no es secreto: es el hash de una clave publica. Por eso
            // puede viajar por argumento y el ticket no.
            Texto(argumentos, "--pin") ?? string.Empty,
            ticket,
            Texto(argumentos, "--titulo")));

    private static string? Texto(string[] args, string nombre)
    {
        var posicion = Array.IndexOf(args, nombre);

        return posicion >= 0 && posicion + 1 < args.Length ? args[posicion + 1] : null;
    }

    private static int Numero(string[] args, string nombre, int porDefecto)
        => int.TryParse(Texto(args, nombre), out var valor) && valor > 0 ? valor : porDefecto;
}
