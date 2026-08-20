using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Capture;
using DeviceHub.Server.Services;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 22: multi-monitor.
///
/// Se prueba la geometria del escritorio virtual y la direccion de los dos
/// mensajes nuevos. Duplicar salidas de verdad exige monitores de verdad, y en
/// CI no los hay.
/// </summary>
public class RemoteDisplayTests
{
    /// <summary>El caso de siempre, y el unico en el que cualquier formula
    /// equivocada acierta.</summary>
    [Fact]
    public void Un_solo_monitor_es_el_escritorio_entero()
        => Assert.Equal((0, 0, 1920, 1080), Pantallas.Envolvente([(0, 0, 1920, 1080)]));

    /// <summary>
    /// El segundo monitor a la IZQUIERDA. Es el que rompe las formulas ingenuas:
    /// el origen del escritorio virtual es negativo, y sin restarlo el raton se
    /// va a la pantalla de al lado.
    /// </summary>
    [Fact]
    public void El_monitor_de_la_izquierda_mueve_el_origen()
    {
        var caja = Pantallas.Envolvente([(0, 0, 1920, 1080), (-1280, 0, 1280, 1024)]);

        Assert.Equal((-1280, 0, 3200, 1080), caja);
    }

    /// <summary>Uno encima de otro, y con alturas distintas: manda el borde
    /// inferior mas bajo, no la suma de los altos.</summary>
    [Fact]
    public void Apilados_en_vertical()
    {
        var caja = Pantallas.Envolvente([(0, 0, 1920, 1080), (0, -768, 1024, 768)]);

        Assert.Equal((0, -768, 1920, 1848), caja);
    }

    /// <summary>Uno metido dentro del otro no ensancha nada. No pasa con
    /// monitores reales, pero la formula no debe inventarse pixeles.</summary>
    [Fact]
    public void Una_caja_contenida_no_cambia_la_envolvente()
        => Assert.Equal(
            (0, 0, 1920, 1080),
            Pantallas.Envolvente([(0, 0, 1920, 1080), (100, 100, 200, 200)]));

    [Fact]
    public void Sin_pantallas_no_hay_envolvente()
        => Assert.Throws<ArgumentException>(() => Pantallas.Envolvente([]));

    /// <summary>La lista la manda el host; elegir es cosa del tecnico. Al reves
    /// serian dos formas de que cada extremo mandara en el otro.</summary>
    [Fact]
    public void Cada_mensaje_va_en_su_direccion()
    {
        var lista = new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = "s1",
            Displays = new DisplayList { Current = 0 }
        };

        var eleccion = new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = "s1",
            SelectDisplay = new SelectDisplay { DisplayId = -1 }
        };

        Assert.Null(RemoteRelayGrpcService.Revisar(lista, RemoteRole.Host, "s1"));
        Assert.NotNull(RemoteRelayGrpcService.Revisar(lista, RemoteRole.Viewer, "s1"));

        Assert.Null(RemoteRelayGrpcService.Revisar(eleccion, RemoteRole.Viewer, "s1"));
        Assert.NotNull(RemoteRelayGrpcService.Revisar(eleccion, RemoteRole.Host, "s1"));
    }
}
