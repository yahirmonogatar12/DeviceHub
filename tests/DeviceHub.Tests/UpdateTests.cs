using DeviceHub.Agent.Updater;
using Xunit;

namespace DeviceHub.Tests;

public class UpdateDecisionTests
{
    private static readonly Version Current = new(1, 2, 0);

    private static UpdateManifest Manifest(string version) => new()
    {
        Version = version,
        File = $"DeviceHub.Agent-{version}.zip",
        Sha256 = new string('a', 64)
    };

    [Fact]
    public void A_newer_version_is_applied()
    {
        Assert.True(UpdateDecision.ShouldApply(Current, Manifest("1.3.0"), out var reason));
        Assert.Contains("1.3.0", reason);
    }

    [Fact]
    public void The_same_version_is_not_reapplied()
        => Assert.False(UpdateDecision.ShouldApply(Current, Manifest("1.2.0"), out _));

    /// <summary>
    /// Bajar de version convertiria el recurso compartido en una forma de
    /// reintroducir una vulnerabilidad ya corregida en TODA la planta.
    /// </summary>
    [Fact]
    public void Downgrades_are_refused()
    {
        Assert.False(UpdateDecision.ShouldApply(Current, Manifest("1.1.9"), out var reason));
        Assert.Contains("1.1.9", reason);
    }

    /// <summary>
    /// El nombre del archivo sale del manifiesto, que esta en un recurso de red:
    /// si aceptara ruta, `..\..\algo.zip` saldria del recurso compartido.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\evil.zip")]
    [InlineData(@"sub\paquete.zip")]
    [InlineData("/etc/passwd")]
    public void A_filename_with_a_path_is_refused(string file)
    {
        var manifest = Manifest("9.9.9");
        manifest.File = file;

        Assert.False(UpdateDecision.ShouldApply(Current, manifest, out _));
    }

    [Fact]
    public void An_unreadable_manifest_is_refused()
    {
        Assert.False(UpdateDecision.ShouldApply(Current, null, out _));
        Assert.Null(UpdateManifest.Parse("{ esto no es json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ultima")]
    [InlineData("v1.3")]
    public void An_invalid_version_is_refused(string version)
        => Assert.False(UpdateDecision.ShouldApply(Current, Manifest(version), out _));

    [Fact]
    public void A_manifest_without_hash_is_refused()
    {
        var manifest = Manifest("1.3.0");
        manifest.Sha256 = string.Empty;

        Assert.False(UpdateDecision.ShouldApply(Current, manifest, out _));
    }

    [Fact]
    public void The_manifest_round_trips()
    {
        var parsed = UpdateManifest.Parse(
            """{"version":"1.4.0","file":"DeviceHub.Agent-1.4.0.zip","sha256":"abc","notes":"correcciones"}""");

        Assert.NotNull(parsed);
        Assert.Equal("1.4.0", parsed.Version);
        Assert.Equal("abc", parsed.Sha256);
    }

    /// <summary>
    /// El rollback tiene que dispararse antes de que a nadie le de tiempo a
    /// notar la PC caida, pero despues de dar margen a un arranque lento.
    /// </summary>
    [Fact]
    public void The_health_deadline_is_sane()
        => Assert.InRange(UpdateService.HealthDeadline, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));
}
