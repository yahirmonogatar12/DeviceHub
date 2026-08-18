using DeviceHub.Server.Remote;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 8: los dos motores, vistos desde fuera.
///
/// Lo que se fija aqui es que el nombre que va en appsettings.json se
/// corresponde con lo que devuelve cada proveedor. Si alguien renombra un
/// Provider, el valor de configuracion de las plantas deja de valer sin que
/// nada avise -- y se descubre con el boton delante de un tecnico.
/// </summary>
public class RemoteProviderChoiceTests
{
    [Fact]
    public void Los_nombres_son_los_que_van_en_la_configuracion()
    {
        Assert.Equal("rustdesk", new RustDeskProvider().Provider);
        Assert.Equal("devicehub", new DeviceHubRemoteProvider().Provider);
    }

    /// <summary>Dos motores distintos no pueden llamarse igual: el nombre es lo
    /// que queda escrito en machine_sessions y en la auditoria.</summary>
    [Fact]
    public void Cada_motor_tiene_su_propio_nombre()
        => Assert.NotEqual(new RustDeskProvider().Provider, new DeviceHubRemoteProvider().Provider);
}
