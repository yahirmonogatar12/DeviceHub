using System.Text.RegularExpressions;
using DeviceHub.Server.Security;
using Xunit;

namespace DeviceHub.Tests;

public class SecretsTests
{
    [Fact]
    public void Password_round_trips()
    {
        var hash = Secrets.HashPassword("correct horse battery staple");

        Assert.True(Secrets.VerifyPassword("correct horse battery staple", hash));
        Assert.False(Secrets.VerifyPassword("Correct horse battery staple", hash));
        Assert.False(Secrets.VerifyPassword(string.Empty, hash));
    }

    [Fact]
    public void Same_password_gets_a_different_hash_each_time()
    {
        // Salt distinto: dos usuarios con la misma password no son detectables
        // comparando la tabla.
        Assert.NotEqual(Secrets.HashPassword("misma"), Secrets.HashPassword("misma"));
    }

    [Fact]
    public void Malformed_hash_never_verifies()
    {
        Assert.False(Secrets.VerifyPassword("x", "no-es-un-hash"));
        Assert.False(Secrets.VerifyPassword("x", "pbkdf2-sha256$abc$!!$!!"));
        Assert.False(Secrets.VerifyPassword("x", string.Empty));
    }

    [Fact]
    public void Enrollment_code_has_no_ambiguous_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = Secrets.NewEnrollmentCode();

            Assert.Matches(new Regex("^ENROLL-[A-Z2-9]{4}-[A-Z2-9]{4}$"), code);
            // I, L, O, 0 y 1 se confunden al teclear.
            Assert.DoesNotContain(code["ENROLL-".Length..], c => c is 'I' or 'L' or 'O' or '0' or '1');
        }
    }

    [Fact]
    public void Machine_tokens_are_unique()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => Secrets.NewMachineToken()).ToHashSet();
        Assert.Equal(500, tokens.Count);
    }

    [Fact]
    public void Hex_comparison_rejects_different_lengths_and_values()
    {
        var hash = Secrets.Sha256Hex("token");

        Assert.True(Secrets.FixedTimeEqualsHex(hash, Secrets.Sha256Hex("token")));
        Assert.False(Secrets.FixedTimeEqualsHex(hash, Secrets.Sha256Hex("otro")));
        Assert.False(Secrets.FixedTimeEqualsHex(hash, "abc"));
        Assert.False(Secrets.FixedTimeEqualsHex(hash, null));
    }
}
