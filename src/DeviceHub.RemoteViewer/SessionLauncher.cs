using System.Diagnostics;
using System.IO;
using System.Net.Http;
using DeviceHub.Contracts;
using DeviceHub.Remote.Contracts;
using Grpc.Core;
using Grpc.Net.Client;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Modo --start-session: pide los tickets al servidor y lanza los dos extremos.
///
/// Es el "proceso padre con tuberia de stdin". Existe porque un ticket vive 45
/// segundos y no se puede copiar a mano de una consola a otra: hace falta algo
/// que lo reciba y lo entregue de inmediato.
///
///     usuario + contrasena
///            v
///     AdminService.Login          -> JWT
///            v
///     IssueRemoteTickets          -> session_id + los dos tickets
///            v
///     stdin ->  RemoteHost        (la PC controlada)
///     stdin ->  RemoteViewer      (la del tecnico)
///
/// Los tickets NUNCA tocan disco, ni consola, ni argumentos. Van del canal TLS a
/// la tuberia de stdin del hijo y de ahi a la memoria del proceso.
///
/// En la Fase 7 esto desaparece del lado del host: el agente recibe su ticket por
/// su propio canal autenticado y se lo pasa a RemoteHost por un named pipe con
/// ACL restringida al SID. El lado del viewer lo hereda el dashboard en la Fase 8.
/// </summary>
public static class SessionLauncher
{
    public static async Task<int> RunAsync(
        string servidor, string relayServidor, string machineId, string usuario,
        string? hostExe, bool permitirSinConfianza)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            Console.Error.WriteLine("Falta --machine-id: el identificador DeviceHub de la PC a controlar.");
            return 2;
        }

        // La contrasena tambien sin eco. Vale lo mismo que el ticket.
        Console.Error.Write($"Contrasena de {usuario}: ");
        var clave = LeerSinEco();
        Console.Error.WriteLine();

        var opciones = new GrpcChannelOptions();

        if (permitirSinConfianza)
        {
            opciones.HttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }

        using var canal = GrpcChannel.ForAddress(servidor, opciones);
        var admin = new AdminService.AdminServiceClient(canal);

        LoginReply sesionAdmin;

        try
        {
            sesionAdmin = await admin.LoginAsync(new LoginRequest { Username = usuario, Password = clave });
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"No se pudo entrar: {ex.Status.Detail}");
            return 3;
        }

        var auth = new Metadata { { "authorization", $"Bearer {sesionAdmin.Token}" } };

        IssueRemoteTicketsResponse tickets;

        try
        {
            tickets = await admin.IssueRemoteTicketsAsync(
                new IssueRemoteTicketsRequest
                {
                    TargetMachineId = machineId,
                    ViewerMachineId = Environment.MachineName
                },
                auth);
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"No se pudieron emitir los tickets: {ex.Status.Detail}");
            return 4;
        }

        Console.Error.WriteLine(
            $"Sesion {tickets.SessionId} autorizada por {sesionAdmin.Username} ({sesionAdmin.Role}). " +
            $"Los tickets vencen en {(DateTimeOffset.FromUnixTimeMilliseconds(tickets.ExpiresAtUs / 1000) - DateTimeOffset.UtcNow).TotalSeconds:0} s.");

        // El host primero: es el que tarda en arrancar captura y encoder, y el
        // ticket va contra reloj.
        if (hostExe is not null)
        {
            if (!File.Exists(hostExe))
            {
                Console.Error.WriteLine($"No existe {hostExe}");
                return 5;
            }

            Lanzar(hostExe,
                $"--relay-test --server {relayServidor} --session {tickets.SessionId} " +
                $"--machine-id {machineId} --seconds 600 --fps 60" +
                (permitirSinConfianza ? " --allow-untrusted" : string.Empty),
                tickets.HostTicket);

            Console.Error.WriteLine("RemoteHost lanzado.");
        }
        else
        {
            Console.Error.WriteLine(
                "Sin --host-exe no se lanza la PC controlada. El host_ticket NO se imprime a proposito: " +
                "vence en 45 s y copiarlo a mano no llega a tiempo. Para eso esta la Fase 7.");
        }

        Lanzar(Environment.ProcessPath!,
            $"--relay-test --server {relayServidor} --session {tickets.SessionId} " +
            $"--machine-id {Environment.MachineName}" +
            (permitirSinConfianza ? " --allow-untrusted" : string.Empty),
            tickets.ViewerTicket);

        Console.Error.WriteLine("RemoteViewer lanzado.");
        return 0;
    }

    /// <summary>
    /// Arranca el hijo y le entrega su ticket por stdin. La tuberia se cierra
    /// justo despues: el hijo lee una linea y ya no necesita nada mas.
    /// </summary>
    private static void Lanzar(string ejecutable, string argumentos, string ticket)
    {
        var proceso = Process.Start(new ProcessStartInfo(ejecutable, argumentos)
        {
            UseShellExecute = false,
            RedirectStandardInput = true
        }) ?? throw new InvalidOperationException($"No se pudo lanzar {ejecutable}");

        proceso.StandardInput.WriteLine(ticket);
        proceso.StandardInput.Flush();
        proceso.StandardInput.Close();
    }

    private static string LeerSinEco()
    {
        var texto = new System.Text.StringBuilder();

        while (true)
        {
            var tecla = Console.ReadKey(intercept: true);

            if (tecla.Key == ConsoleKey.Enter)
                return texto.ToString();

            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (texto.Length > 0)
                    texto.Length--;

                continue;
            }

            if (!char.IsControl(tecla.KeyChar))
                texto.Append(tecla.KeyChar);
        }
    }
}
