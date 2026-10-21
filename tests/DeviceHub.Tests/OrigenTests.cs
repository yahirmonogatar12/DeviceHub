using DeviceHub.Server.Security;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// El origen de una llamada, sin el puerto. Con el puerto, cada conexion nueva
/// era otra clave del limite de login y el limite no limitaba nada.
/// </summary>
public class OrigenTests
{
    [Theory]
    [InlineData("ipv4:192.168.1.50:54321", "192.168.1.50")]
    [InlineData("ipv4:192.168.1.50:1", "192.168.1.50")]
    [InlineData("ipv6:[::1]:54321", "::1")]
    [InlineData("ipv6:[fe80::1%3]:443", "fe80::1%3")]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void Se_queda_con_la_ip(string peer, string esperado)
        => Assert.Equal(esperado, Origen.DePeer(peer));

    /// <summary>Lo que importa del arreglo: dos conexiones desde la misma PC son
    /// el MISMO origen.</summary>
    [Fact]
    public void Dos_conexiones_de_la_misma_pc_son_el_mismo_origen()
        => Assert.Equal(Origen.DePeer("ipv4:10.0.0.7:50001"), Origen.DePeer("ipv4:10.0.0.7:61234"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sin_peer_no_hay_origen(string? peer)
        => Assert.Null(Origen.DePeer(peer));

    [Fact]
    public void Lo_que_no_se_entiende_se_devuelve_tal_cual()
        => Assert.Equal("unix:/tmp/socket", Origen.DePeer("unix:/tmp/socket"));
}
