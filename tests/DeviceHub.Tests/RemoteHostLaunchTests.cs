using System.IO.Pipes;
using System.Text;
using DeviceHub.Remote.Contracts;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 7: el saludo que el agente le pasa al host por el named pipe.
///
/// Lo que NO se puede probar aqui es el interop de Windows -- WTSQueryUserToken
/// necesita SYSTEM y una sesion de consola, y CI no tiene ninguna de las dos.
/// Eso se verifica a mano en una PC de planta. Lo que si se prueba es lo unico
/// que puede romperse en silencio: el formato del saludo, porque si el host lo
/// interpreta mal, el sintoma es "la sesion no arranca" sin nada mas que mirar.
/// </summary>
public class RemoteHostLaunchTests
{
    private static RemoteHostHandshake Saludo() => new()
    {
        SessionId = "abc123",
        Ticket = "secreto-de-256-bits",
        ServerAddress = "https://192.168.1.10:5443",
        MachineId = "3f2b9c14",
        PinnedKeys = ["pin-a", "pin-b"],
        AllowUntrusted = false
    };

    [Fact]
    public void Handshake_survives_the_round_trip()
    {
        var vuelta = RemoteHostHandshake.Parse(Saludo().ToLine());

        Assert.NotNull(vuelta);
        Assert.Equal("abc123", vuelta.SessionId);
        Assert.Equal("secreto-de-256-bits", vuelta.Ticket);
        Assert.Equal("https://192.168.1.10:5443", vuelta.ServerAddress);
        Assert.Equal("3f2b9c14", vuelta.MachineId);
        Assert.Equal(["pin-a", "pin-b"], vuelta.PinnedKeys);
        Assert.False(vuelta.AllowUntrusted);
    }

    /// <summary>Una sola linea, siempre. El canal de control es de lineas y un
    /// salto en medio del saludo partiria el mensaje en dos.</summary>
    [Fact]
    public void Handshake_is_a_single_line()
        => Assert.DoesNotContain('\n', Saludo().ToLine());

    /// <summary>
    /// El ToString() que genera el compilador para un record imprime TODAS las
    /// propiedades. Basta un log descuidado para dejar el ticket en el visor de
    /// eventos, asi que esta redactado a mano -- y este test es lo que impide
    /// que alguien lo borre sin querer.
    /// </summary>
    [Fact]
    public void Handshake_never_prints_the_ticket()
    {
        var texto = Saludo().ToString();

        Assert.DoesNotContain("secreto-de-256-bits", texto);
        Assert.Contains("abc123", texto);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto no es json")]
    [InlineData("{}")]
    [InlineData("""{"SessionId":"a","Ticket":"t","ServerAddress":"https://x","MachineId":""}""")]
    [InlineData("""{"SessionId":"a","Ticket":"","ServerAddress":"https://x","MachineId":"m"}""")]
    public void Incomplete_handshakes_are_rejected(string? linea)
        => Assert.Null(RemoteHostHandshake.Parse(linea));

    /// <summary>
    /// El camino completo del pipe, sin lanzar procesos: el agente escribe el
    /// saludo, el host lo lee, contesta READY y el agente manda STOP.
    ///
    /// Es la coreografia exacta de InteractiveSessionLauncher y HostSession. Si
    /// alguien invierte quien escribe primero, esto se cuelga en vez de pasar.
    /// </summary>
    [Fact]
    public async Task The_pipe_carries_the_handshake_and_the_stop()
    {
        var nombre = $"devicehub-test-{Guid.NewGuid():n}";

        // Los buffers explicitos son lo que hace que esto no se cuelgue, y por
        // eso el test los pone: con 0 -- el valor por defecto de la sobrecarga
        // corta -- Win32 entiende "sin buffer" y cada escritura espera a que el
        // otro lado lea. En este test los dos extremos son el mismo hilo, asi
        // que el primer WriteLine no volveria nunca.
        await using var servidor = new NamedPipeServerStream(
            nombre, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096);

        await using var cliente = new NamedPipeClientStream(".", nombre, PipeDirection.InOut, PipeOptions.Asynchronous);

        var conectando = servidor.WaitForConnectionAsync();
        await cliente.ConnectAsync(5000);
        await conectando;

        // Sin BOM. Encoding.UTF8 escribe el preambulo en la primera linea y el
        // saludo dejaria de ser JSON valido para cualquier lector que no lo
        // detecte -- que es justo lo que hace RemoteHostHandshake.Parse.
        var texto = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await using var agente = new StreamWriter(servidor, texto, leaveOpen: true) { AutoFlush = true };
        using var deAgente = new StreamReader(servidor, texto, leaveOpen: true);

        await using var host = new StreamWriter(cliente, texto, leaveOpen: true) { AutoFlush = true };
        using var deHost = new StreamReader(cliente, texto, leaveOpen: true);

        await agente.WriteLineAsync(Saludo().ToLine());

        var recibido = RemoteHostHandshake.Parse(await deHost.ReadLineAsync());
        Assert.Equal("abc123", recibido?.SessionId);

        await host.WriteLineAsync(RemoteHostPipe.Ready);
        Assert.Equal(RemoteHostPipe.Ready, await deAgente.ReadLineAsync());

        await agente.WriteLineAsync(RemoteHostPipe.Stop);
        Assert.Equal(RemoteHostPipe.Stop, await deHost.ReadLineAsync());

        await host.WriteLineAsync($"{RemoteHostPipe.Ended} 0");
        Assert.Equal("ENDED 0", await deAgente.ReadLineAsync());
    }
}
