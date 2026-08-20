using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Services;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 21: la frontera del relay para el portapapeles.
///
/// Es lo unico de la fase que se puede probar en CI. Lo demas -- LockWorkStation,
/// BlockInput, SendSAS y el portapapeles de Win32 -- exige una estacion de
/// ventanas de verdad delante, y probarlo aqui solo probaria el mock.
/// </summary>
public class RemoteClipboardTests
{
    private static RemotePacket Portapapeles(string texto) => new()
    {
        ProtocolVersion = RemoteSessionProtocol.Version,
        SessionId = "s1",
        Clipboard = new ClipboardText { Text = texto }
    };

    /// <summary>El unico payload aparte de Ping y Close que va en los dos
    /// sentidos: se copia aqui y se pega alla, y al reves.</summary>
    [Theory]
    [InlineData(RemoteRole.Host)]
    [InlineData(RemoteRole.Viewer)]
    public void El_portapapeles_viaja_en_los_dos_sentidos(RemoteRole papel)
        => Assert.Null(RemoteRelayGrpcService.Revisar(Portapapeles("hola"), papel, "s1"));

    /// <summary>
    /// El tope importa porque la sincronizacion es AUTOMATICA: nadie pulsa nada
    /// para que esto viaje. Sin el, copiar un log enorme en cualquiera de los dos
    /// lados lo manda por la red entero y sin avisar.
    /// </summary>
    [Fact]
    public void Un_portapapeles_desorbitado_se_rechaza()
    {
        var enorme = new string('x', RemoteSessionProtocol.MaxClipboardChars + 1);
        var queja = RemoteRelayGrpcService.Revisar(Portapapeles(enorme), RemoteRole.Viewer, "s1");

        Assert.NotNull(queja);
        Assert.Equal(RemoteErrorCode.PayloadTooLarge, queja.Value.Code);
    }

    [Fact]
    public void Justo_en_el_limite_pasa()
    {
        var justo = new string('x', RemoteSessionProtocol.MaxClipboardChars);

        Assert.Null(RemoteRelayGrpcService.Revisar(Portapapeles(justo), RemoteRole.Viewer, "s1"));
    }

    /// <summary>Las acciones sobre el host siguen siendo de una sola direccion: un
    /// host que mandara esto estaria intentando reiniciar la PC del tecnico.</summary>
    [Fact]
    public void Un_host_no_puede_ordenar_acciones()
    {
        var paquete = new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = "s1",
            HostAction = new HostAction { Kind = HostAction.Types.Kind.HostActionReboot }
        };

        Assert.NotNull(RemoteRelayGrpcService.Revisar(paquete, RemoteRole.Host, "s1"));
        Assert.Null(RemoteRelayGrpcService.Revisar(paquete, RemoteRole.Viewer, "s1"));
    }
}
