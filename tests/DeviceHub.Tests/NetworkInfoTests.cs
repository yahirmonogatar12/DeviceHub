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

    [Fact]
    public void Uptime_is_positive()
        => Assert.True(NetworkInfo.UptimeSeconds() >= 0);
}
