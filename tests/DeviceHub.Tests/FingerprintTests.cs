using DeviceHub.Agent.Identity;
using DeviceHub.Contracts;
using Xunit;

namespace DeviceHub.Tests;

public class FingerprintTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("to be filled by o.e.m.")]
    [InlineData("Default string")]
    [InlineData("System Serial Number")]
    [InlineData("Not Specified")]
    [InlineData("None")]
    [InlineData("0123456789")]
    [InlineData("000")]
    public void Bogus_values_are_rejected(string? value)
        => Assert.True(Fingerprint.IsBogus(value));

    [Theory]
    [InlineData("4C4C4544-0037-5A10-8046-C4C04F345432")]
    [InlineData("PF2K9L3M")]
    public void Real_values_are_accepted(string value)
        => Assert.False(Fingerprint.IsBogus(value));

    [Fact]
    public void Both_components_valid_gives_high_confidence()
    {
        var result = Fingerprint.Evaluate("4C4C4544-0037-5A10-8046-C4C04F345432", "PF2K9L3M");
        Assert.Equal(FingerprintConfidence.High, result.Confidence);
    }

    [Fact]
    public void One_component_valid_gives_medium_confidence()
    {
        var result = Fingerprint.Evaluate("4C4C4544-0037-5A10-8046-C4C04F345432", "To Be Filled By O.E.M.");
        Assert.Equal(FingerprintConfidence.Medium, result.Confidence);
    }

    /// <summary>
    /// El caso que motiva todo esto: una placa industrial que no reporta nada
    /// util. El fingerprint no debe habilitar la deteccion de clonacion.
    /// </summary>
    [Fact]
    public void Empty_bios_gives_low_confidence()
    {
        var result = Fingerprint.Evaluate("00000000-0000-0000-0000-000000000000", "Default string");
        Assert.Equal(FingerprintConfidence.Low, result.Confidence);
    }

    [Fact]
    public void Hash_is_deterministic_and_case_insensitive()
    {
        var a = Fingerprint.Evaluate("4C4C4544-0037-5A10", "PF2K9L3M");
        var b = Fingerprint.Evaluate("4c4c4544-0037-5a10", " pf2k9l3m ");

        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Different_hardware_gives_different_hash()
    {
        var a = Fingerprint.Evaluate("4C4C4544-0037-5A10", "PF2K9L3M");
        var b = Fingerprint.Evaluate("4C4C4544-0037-5A11", "PF2K9L3M");

        Assert.NotEqual(a.Hash, b.Hash);
    }
}
