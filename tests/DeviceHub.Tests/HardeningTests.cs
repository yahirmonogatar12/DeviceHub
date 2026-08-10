using DeviceHub.Contracts;
using DeviceHub.Server.Security;
using Xunit;

namespace DeviceHub.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corta")]
    [InlineData("ILSANMES2026")]      // contiene "ilsan"
    [InlineData("password1234")]
    [InlineData("devicehub123")]
    [InlineData("admin1234567")]
    public void Weak_passwords_are_rejected(string? password)
        => Assert.False(PasswordPolicy.IsValid(password, out _));

    /// <summary>
    /// Se exige longitud y no un zoo de simbolos: "Ilsan2026!" cumple cualquier
    /// regla de mayusculas-numeros-simbolos y es adivinable.
    /// </summary>
    [Theory]
    [InlineData("caballo grapadora correcto")]
    [InlineData("mantenimiento-fct-2026")]
    public void Long_phrases_are_accepted(string password)
        => Assert.True(PasswordPolicy.IsValid(password, out _));

    [Fact]
    public void The_error_explains_what_falta()
    {
        PasswordPolicy.IsValid("abc", out var error);
        Assert.Contains(PasswordPolicy.MinimumLength.ToString(), error);
    }
}

public class RateLimiterTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void It_allows_up_to_the_limit_and_then_blocks()
    {
        var limiter = new RateLimiter();

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire("k", 5, TimeSpan.FromMinutes(5), Now));

        Assert.False(limiter.TryAcquire("k", 5, TimeSpan.FromMinutes(5), Now));
    }

    /// <summary>Ventana deslizante: al salir del periodo se vuelve a permitir.</summary>
    [Fact]
    public void The_window_slides()
    {
        var limiter = new RateLimiter();
        var period = TimeSpan.FromMinutes(5);

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire("k", 5, period, Now);

        Assert.False(limiter.TryAcquire("k", 5, period, Now.AddMinutes(4)));
        Assert.True(limiter.TryAcquire("k", 5, period, Now.AddMinutes(5)));
    }

    /// <summary>
    /// Bloquear a un usuario no puede bloquear a los demas: si no, cualquiera
    /// deja fuera al admin fallando cinco veces con su nombre.
    /// </summary>
    [Fact]
    public void Keys_are_independent()
    {
        var limiter = new RateLimiter();

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire("usuario-a", 5, TimeSpan.FromMinutes(5), Now);

        Assert.False(limiter.TryAcquire("usuario-a", 5, TimeSpan.FromMinutes(5), Now));
        Assert.True(limiter.TryAcquire("usuario-b", 5, TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public void A_successful_login_clears_the_counter()
    {
        var limiter = new RateLimiter();

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire("k", 5, TimeSpan.FromMinutes(5), Now);

        limiter.Reset("k");

        Assert.True(limiter.TryAcquire("k", 5, TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public void Configured_limits_are_sane()
    {
        Assert.InRange(RateLimits.LoginAttempts, 3, 10);
        Assert.InRange(RateLimits.CommandsPerMachine, 10, 120);
    }
}
