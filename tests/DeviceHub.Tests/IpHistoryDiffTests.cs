using DeviceHub.Server.Domain;
using Xunit;

namespace DeviceHub.Tests;

public class IpHistoryDiffTests
{
    /// <summary>
    /// El test que protege la tabla: con heartbeat cada 30 s, escribir cuando no
    /// cambio nada serian 2.880 filas de basura por dia y por PC.
    /// </summary>
    [Fact]
    public void Same_ip_produces_no_writes()
    {
        var change = IpHistoryDiff.Compute(
            [new IpEntry("192.168.10.51", "00-1A-2B")],
            [new IpEntry("192.168.10.51", "00-1A-2B")]);

        Assert.False(change.HasChanges);
        Assert.Empty(change.ToClose);
        Assert.Empty(change.ToOpen);
    }

    [Fact]
    public void Changed_ip_closes_the_old_row_and_opens_a_new_one()
    {
        var change = IpHistoryDiff.Compute(
            [new IpEntry("192.168.10.32", "00-1A-2B")],
            [new IpEntry("192.168.10.51", "00-1A-2B")]);

        Assert.True(change.HasChanges);
        Assert.Equal("192.168.10.32", Assert.Single(change.ToClose).Ip);
        Assert.Equal("192.168.10.51", Assert.Single(change.ToOpen).Ip);
    }

    [Fact]
    public void Order_does_not_matter()
    {
        var change = IpHistoryDiff.Compute(
            [new IpEntry("10.0.0.1", "A"), new IpEntry("10.0.0.2", "B")],
            [new IpEntry("10.0.0.2", "B"), new IpEntry("10.0.0.1", "A")]);

        Assert.False(change.HasChanges);
    }

    [Fact]
    public void New_nic_only_opens_a_row()
    {
        var change = IpHistoryDiff.Compute(
            [new IpEntry("10.0.0.1", "A")],
            [new IpEntry("10.0.0.1", "A"), new IpEntry("10.0.0.9", "C")]);

        Assert.Empty(change.ToClose);
        Assert.Equal("10.0.0.9", Assert.Single(change.ToOpen).Ip);
    }

    [Fact]
    public void Replacing_the_nic_keeping_the_ip_is_a_change()
    {
        var change = IpHistoryDiff.Compute(
            [new IpEntry("10.0.0.1", "00-AA")],
            [new IpEntry("10.0.0.1", "00-BB")]);

        Assert.True(change.HasChanges);
    }

    [Fact]
    public void First_heartbeat_opens_everything()
    {
        var change = IpHistoryDiff.Compute([], [new IpEntry("10.0.0.1", "A")]);

        Assert.Empty(change.ToClose);
        Assert.Single(change.ToOpen);
    }
}
