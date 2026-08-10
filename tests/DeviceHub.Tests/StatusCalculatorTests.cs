using DeviceHub.Contracts;
using Xunit;

namespace DeviceHub.Tests;

public class StatusCalculatorTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, MachineStatus.Online)]
    [InlineData(30, MachineStatus.Online)]
    [InlineData(89, MachineStatus.Online)]
    [InlineData(90, MachineStatus.Unreachable)]
    [InlineData(200, MachineStatus.Unreachable)]
    [InlineData(299, MachineStatus.Unreachable)]
    [InlineData(300, MachineStatus.Offline)]
    [InlineData(86400, MachineStatus.Offline)]
    public void Thresholds(int secondsAgo, MachineStatus expected)
        => Assert.Equal(expected, StatusCalculator.Compute(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Never_seen_is_offline()
        => Assert.Equal(MachineStatus.Offline, StatusCalculator.Compute(null, Now));

    /// <summary>
    /// Reloj del agente ligeramente adelantado respecto al servidor: no debe
    /// aparecer como si nunca hubiera latido.
    /// </summary>
    [Fact]
    public void Future_timestamp_counts_as_online()
        => Assert.Equal(MachineStatus.Online, StatusCalculator.Compute(Now.AddSeconds(5), Now));

    /// <summary>
    /// MySqlConnector devuelve DateTimeKind.Unspecified. El calculo tiene que dar
    /// lo mismo que con Kind=Utc, o el estado saldria desplazado por el huso.
    /// </summary>
    [Fact]
    public void Unspecified_kind_is_treated_as_utc()
    {
        var unspecified = DateTime.SpecifyKind(Now.AddSeconds(-10), DateTimeKind.Unspecified);
        Assert.Equal(MachineStatus.Online, StatusCalculator.Compute(unspecified, Now));
    }
}
