using DeviceHub.Contracts;
using DeviceHub.Server.Domain;
using Xunit;

namespace DeviceHub.Tests;

public class IdentityGuardTests
{
    private const string HashA = "aaaa";
    private const string HashB = "bbbb";

    [Fact]
    public void Different_hardware_with_high_confidence_on_both_sides_is_a_conflict()
        => Assert.True(IdentityGuard.IsHardwareConflict(
            HashA, FingerprintConfidence.High, HashB, FingerprintConfidence.High));

    [Fact]
    public void Same_hardware_is_never_a_conflict()
        => Assert.False(IdentityGuard.IsHardwareConflict(
            HashA, FingerprintConfidence.High, HashA, FingerprintConfidence.High));

    /// <summary>
    /// La regla que evita el ruido: si el valor guardado era poco fiable, la
    /// diferencia puede venir de un WMI que fallo, no de un clon.
    /// </summary>
    [Theory]
    [InlineData(FingerprintConfidence.Low, FingerprintConfidence.High)]
    [InlineData(FingerprintConfidence.High, FingerprintConfidence.Low)]
    [InlineData(FingerprintConfidence.Medium, FingerprintConfidence.High)]
    [InlineData(FingerprintConfidence.High, FingerprintConfidence.Medium)]
    [InlineData(FingerprintConfidence.Low, FingerprintConfidence.Low)]
    public void Anything_below_high_on_either_side_is_not_a_conflict(
        FingerprintConfidence stored, FingerprintConfidence incoming)
        => Assert.False(IdentityGuard.IsHardwareConflict(HashA, stored, HashB, incoming));

    [Fact]
    public void Missing_fingerprint_is_not_a_conflict()
    {
        Assert.False(IdentityGuard.IsHardwareConflict(null, FingerprintConfidence.High, HashB, FingerprintConfidence.High));
        Assert.False(IdentityGuard.IsHardwareConflict(HashA, FingerprintConfidence.High, null, FingerprintConfidence.High));
    }

    [Theory]
    [InlineData(1, FingerprintConfidence.High)]
    [InlineData(2, FingerprintConfidence.High)]
    [InlineData(3, FingerprintConfidence.Low)]
    [InlineData(9, FingerprintConfidence.Low)]
    public void Fingerprint_shared_by_three_machines_stops_discriminating(int shared, FingerprintConfidence expected)
        => Assert.Equal(expected, IdentityGuard.Degrade(FingerprintConfidence.High, shared));
}
