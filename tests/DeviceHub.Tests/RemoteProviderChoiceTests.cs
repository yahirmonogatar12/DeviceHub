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

/// <summary>
/// El catalogo: lo que traduce el nombre que manda el dashboard al motor que
/// sabe lanzar. Fase 8.
/// </summary>
public class RemoteProviderCatalogTests
{
    private static RemoteProviderCatalog Catalogo(string porDefecto)
        => new([new RustDeskProvider(), new DeviceHubRemoteProvider()], porDefecto);

    [Fact]
    public void Cada_nombre_devuelve_su_motor()
    {
        var catalogo = Catalogo("rustdesk");

        Assert.Equal("rustdesk", catalogo.Resolver("rustdesk").Provider);
        Assert.Equal("devicehub", catalogo.Resolver("devicehub").Provider);
    }

    /// <summary>Sin nombre manda el configurado: es el caso de un cliente viejo
    /// que todavia no manda el campo.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_nombre_manda_el_configurado(string? nombre)
        => Assert.Equal("devicehub", Catalogo("devicehub").Resolver(nombre).Provider);

    /// <summary>
    /// Un motor que no existe se RECHAZA, no cae al por defecto. Si alguien pide
    /// uno concreto y recibe otro sin avisar, el fallo aparece mucho despues y en
    /// otro sitio -- normalmente con un tecnico delante preguntando por que el
    /// boton no hace lo que dice.
    /// </summary>
    [Fact]
    public void Un_motor_inventado_se_rechaza()
        => Assert.Throws<ArgumentException>(() => Catalogo("rustdesk").Resolver("anydesk"));

    /// <summary>Y un valor mal escrito en appsettings tumba el arranque en vez de
    /// dejar el servidor en marcha haciendo algo que nadie pidio.</summary>
    [Fact]
    public void Un_valor_configurado_invalido_no_deja_arrancar()
        => Assert.Throws<InvalidOperationException>(() => Catalogo("devicehun"));

    [Fact]
    public void El_nombre_no_distingue_mayusculas()
        => Assert.Equal("devicehub", Catalogo("rustdesk").Resolver("DeviceHub").Provider);
}
