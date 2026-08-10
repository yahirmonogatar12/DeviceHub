using System.Net.Sockets;
using DeviceHub.Agent.Network;
using Xunit;

namespace DeviceHub.Tests;

public class NetworkInfoTests
{
    /// <summary>
    /// La regla que reemplaza la lista negra de adaptadores virtuales: como
    /// mucho una interfaz es la primaria, la que tiene la ruta al servidor.
    /// </summary>
    [Fact]
    public void At_most_one_interface_is_primary()
    {
        var nics = NetworkInfo.Collect("localhost");
        Assert.True(nics.Count(n => n.IsPrimary) <= 1);
    }

    [Fact]
    public void Loopback_is_never_reported()
    {
        var nics = NetworkInfo.Collect("localhost");
        Assert.DoesNotContain(nics, n => n.Ip == "127.0.0.1");
    }

    [Fact]
    public void Every_reported_address_is_a_valid_ipv4()
    {
        foreach (var nic in NetworkInfo.Collect("localhost"))
        {
            Assert.True(System.Net.IPAddress.TryParse(nic.Ip, out var parsed));
            Assert.Equal(AddressFamily.InterNetwork, parsed!.AddressFamily);
        }
    }

    [Fact]
    public void Unresolvable_host_does_not_throw()
    {
        // Sin servidor accesible no hay primaria, pero el agente debe seguir
        // reportando sus interfaces igual.
        var ip = NetworkInfo.ResolvePrimaryIp("no-existe.invalid");
        Assert.Null(ip);
    }

    /// <summary>
    /// Con el servidor en localhost la ruta resuelve a loopback, que esta
    /// filtrado, asi que ninguna interfaz queda marcada. Y NO se inventa una.
    ///
    /// Este test existe porque las dos heuristicas que se probaron fallaron
    /// contra hardware real: "la primera de la lista" eligio Tailscale (100.x) y
    /// "la primera con MAC" eligio VirtualBox (192.168.56.1). Una IP equivocada
    /// es peor que ninguna en un sistema cuyo proposito es saber que PC es cual.
    /// </summary>
    [Fact]
    public void Nothing_is_marked_primary_when_the_route_gives_nothing()
        => Assert.DoesNotContain(NetworkInfo.Collect("localhost"), n => n.IsPrimary);

    /// <summary>Con un destino ruteable de verdad si debe salir una primaria.</summary>
    [Fact]
    public void A_routable_target_resolves_a_primary()
    {
        // No envia nada: solo consulta la tabla de rutas.
        if (NetworkInfo.ResolvePrimaryIp("192.0.2.1") is null)
            return; // maquina sin ruta por defecto

        Assert.Single(NetworkInfo.Collect("192.0.2.1").Where(n => n.IsPrimary));
    }

    [Fact]
    public void Uptime_is_positive()
        => Assert.True(NetworkInfo.UptimeSeconds() >= 0);
}
