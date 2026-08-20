using System.IO.Pipes;
using System.Text;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Relay;

/// <summary>
/// Fase 7: el modo de produccion. El agente lanza este proceso dentro de la
/// sesion interactiva y le pasa SOLO el nombre de un named pipe; la sesion, el
/// ticket, la direccion del servidor y los pines llegan por ahi.
///
/// El pipe no se cierra despues del saludo: es el canal de control. Por el llega
/// el STOP del agente, y por el se reporta el estado -- este proceso se lanza
/// con CREATE_NO_WINDOW, asi que lo que escriba en su consola no lo lee nadie.
///
/// El host muere cuando: el relay cierra la sesion, se cae el servidor, el
/// agente manda STOP, o el agente se muere y el pipe se rompe. El codificador no
/// queda corriendo entre sesiones porque el proceso entero se va con el.
/// </summary>
public static class HostSession
{
    /// <summary>Sin BOM: el agente escribe el saludo asi y el preambulo lo
    /// dejaria de ser JSON valido.</summary>
    private static readonly UTF8Encoding Texto = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(string tuberia, int adapter, int output, int fps, int bitrate)
    {
        using var pipe = new NamedPipeClientStream(".", tuberia, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(15_000);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Nadie escucha en el pipe {tuberia}");
            return 7;
        }

        using var lector = new StreamReader(pipe, Texto, leaveOpen: true);
        var escritor = new StreamWriter(pipe, Texto, leaveOpen: true) { AutoFlush = true };
        var pluma = new Lock();

        var saludo = RemoteHostHandshake.Parse(await lector.ReadLineAsync());

        if (saludo is null)
        {
            Console.Error.WriteLine("El saludo del agente no es valido");
            return 8;
        }

        using var cancelacion = new CancellationTokenSource();

        // Vigila el canal de control. Si llega STOP se corta; si el pipe se
        // rompe -- el agente murio o lo mataron -- tambien: un host capturando
        // la pantalla sin nadie que lo gobierne es exactamente lo que no puede
        // quedarse por ahi.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await lector.ReadLineAsync() is { } linea)
                {
                    if (linea.Trim().Equals(RemoteHostPipe.Stop, StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
            catch (IOException)
            {
            }

            await cancelacion.CancelAsync();
        });

        Reportar(escritor, pluma, RemoteHostPipe.Ready);

        var codigo = await RelaySession.RunAsync(new RelayOptions
        {
            Servidor = saludo.ServerAddress,
            SesionId = saludo.SessionId,
            MachineId = saludo.MachineId,
            Ticket = saludo.Ticket,
            PinnedKeys = saludo.PinnedKeys,
            AllowUntrusted = saludo.AllowUntrusted,
            UsarH265 = saludo.UseH265,
            Adapter = adapter,
            Output = output,
            Seconds = 0,                 // hasta que la sesion termine
            Fps = fps,
            Bitrate = bitrate,
            Escribir = texto => Reportar(escritor, pluma, $"{RemoteHostPipe.Status} {texto}")
        }, cancelacion.Token);

        Reportar(escritor, pluma, $"{RemoteHostPipe.Ended} {codigo}");
        return codigo;
    }

    /// <summary>
    /// El candado no es decorativo: al pipe escriben el hilo de captura y el de
    /// red, y dos WriteLine entrelazados producen una linea que el agente no
    /// puede interpretar. Que el pipe ya no exista no es un error -- si el agente
    /// se fue, el reporte no le importa a nadie.
    /// </summary>
    private static void Reportar(StreamWriter escritor, Lock pluma, string linea)
    {
        try
        {
            lock (pluma)
                escritor.WriteLine(linea);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
