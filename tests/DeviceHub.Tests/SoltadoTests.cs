using Xunit;
using DeviceHub.RemoteViewer.Transferencia;
using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Services;
using DeviceHub.RemoteHost.Capture;
using Grpc.Core;

namespace DeviceHub.Tests;

/// <summary>
/// Las reglas de arrastrar y soltar sobre la ventana del visor.
/// </summary>
public class SoltadoTests
{
    private const string Carpeta = @"D:\Datos";

    [Fact]
    public void Va_a_la_carpeta_abierta_y_solo_con_el_NOMBRE()
    {
        var plan = Soltado.Preparar([@"C:\Users\yahir\informe.pdf"], false, Carpeta);

        Assert.Null(plan.Queja);
        var (local, remoto) = Assert.Single(plan.Subidas);

        Assert.Equal(@"C:\Users\yahir\informe.pdf", local);
        Assert.Equal(@"D:\Datos\informe.pdf", remoto);
    }

    [Fact]
    public void Varios_a_la_vez()
    {
        var plan = Soltado.Preparar([@"C:\a.txt", @"C:\b\c.log"], false, Carpeta);

        Assert.Null(plan.Queja);
        Assert.Equal([@"D:\Datos\a.txt", @"D:\Datos\c.log"], plan.Subidas.Select(s => s.Remoto));
    }

    [Fact]
    public void Sin_carpeta_elegida_no_se_inventa_un_destino()
    {
        // El host corre como SYSTEM: cualquier destino "por defecto" acabaria en
        // el perfil de SYSTEM, donde nadie va a buscar nada.
        var plan = Soltado.Preparar([@"C:\a.txt"], false, "");

        Assert.Empty(plan.Subidas);
        Assert.Contains("carpeta", plan.Queja!);
    }

    [Fact]
    public void Una_carpeta_soltada_lo_dice_en_vez_de_callarse()
    {
        var plan = Soltado.Preparar([], habiaCarpetas: true, Carpeta);

        Assert.Empty(plan.Subidas);
        Assert.Contains("Carpetas", plan.Queja!);
    }

    [Fact]
    public void Nada_utilizable_tambien_lo_dice()
    {
        var plan = Soltado.Preparar([], habiaCarpetas: false, Carpeta);

        Assert.Empty(plan.Subidas);
        Assert.NotNull(plan.Queja);
    }

    [Fact]
    public void No_se_puede_escapar_de_la_carpeta_elegida()
    {
        // GetFileName deja ".." en nada, asi que estas no producen subida. Lo que
        // NO puede pasar es que alguna acabe fuera de D:\Datos.
        var plan = Soltado.Preparar([@"C:\x\..", @"C:\y\"], false, Carpeta);

        foreach (var (_, remoto) in plan.Subidas)
            Assert.StartsWith(Carpeta + @"\", remoto);
    }
}

/// <summary>
/// La orden de pegar donde se solto. Es la unica del protocolo que hace teclear
/// a la PC de planta sin que nadie pulse una tecla, asi que la direccion importa.
/// </summary>
public class PasteAtTests
{
    private static RemotePacket Pegar() => new()
    {
        ProtocolVersion = RemoteSessionProtocol.Version,
        SessionId = "s1",
        PasteAt = new PasteAt { X = 0.5, Y = 0.5 }
    };

    [Fact]
    public void El_visor_puede_pedir_pegar()
        => Assert.Null(RemoteRelayGrpcService.Revisar(Pegar(), RemoteRole.Viewer, "s1"));

    [Fact]
    public void El_host_NO_puede()
    {
        // Un host mandando esto estaria pidiendo teclas en la PC del tecnico.
        Assert.NotNull(RemoteRelayGrpcService.Revisar(Pegar(), RemoteRole.Host, "s1"));
    }
}

/// <summary>
/// La orden de anadir o quitar el monitor virtual. Instala un driver en la PC
/// de planta, asi que la direccion es lo primero que hay que sujetar.
/// </summary>
public class VirtualDisplayTests
{
    private static RemotePacket Orden(bool encender) => new()
    {
        ProtocolVersion = RemoteSessionProtocol.Version,
        SessionId = "s1",
        VirtualDisplay = new VirtualDisplay { Enable = encender }
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_visor_puede_pedirla(bool encender)
        => Assert.Null(RemoteRelayGrpcService.Revisar(Orden(encender), RemoteRole.Viewer, "s1"));

    [Fact]
    public void El_host_NO_puede()
    {
        // Un host mandandolo estaria pidiendo instalar un driver en la PC del
        // tecnico.
        Assert.NotNull(RemoteRelayGrpcService.Revisar(Orden(true), RemoteRole.Host, "s1"));
    }

    [Fact]
    public void El_refugio_del_driver_sobrevive_a_una_actualizacion()
    {
        // El driver viaja en el paquete, pero el sitio para copiarlo a mano en
        // una PC suelta tiene que estar FUERA de la carpeta de instalacion: el
        // actualizador la mueve entera y solo rescata appsettings.json.
        Assert.StartsWith(@"C:\ProgramData\", PantallaVirtual.Refugio);
        Assert.DoesNotContain("Program Files", PantallaVirtual.Refugio);
    }

    [Fact]
    public void Sin_driver_no_revienta_ni_deja_la_sesion_a_medias()
    {
        // En CI no hay driver, y esa es justamente la comprobacion: pedirla
        // tiene que contestar que no se puede, no lanzar.
        var id = PantallaVirtual.Encender(out var queja);

        Assert.Equal(-1, id);
        Assert.NotEmpty(queja);

        // Y apagarla sin que haya nada tiene que ser inofensivo: se llama al
        // cerrar CADA sesion, la haya encendido alguien o no.
        Assert.True(PantallaVirtual.Apagar(out _));
    }
}
