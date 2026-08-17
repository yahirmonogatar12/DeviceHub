using DeviceHub.Server.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Permisos de reasociacion.
///
/// El reloj es inyectado para no dormir: un test que espera 10 minutos reales
/// acaba desactivado, y con el se desactiva la comprobacion de caducidad, que es
/// justo la que importa.
/// </summary>
public class ReenrollmentTests
{
    private const string Maquina = "1def4b54-346d-488e-8036-a74d7db8828f";

    [Fact]
    public void Without_authorization_nobody_reassociates()
        => Assert.False(new ReenrollmentGrants().TryConsume(Maquina));

    [Fact]
    public void An_authorized_machine_reassociates_once()
    {
        var permisos = new ReenrollmentGrants();
        permisos.Authorize(Maquina);

        Assert.True(permisos.TryConsume(Maquina));

        // De un solo uso: si el registro falla despues, el administrador vuelve a
        // pulsar. Es preferible a dejar la puerta puesta tras un intento raro.
        Assert.False(permisos.TryConsume(Maquina));
    }

    /// <summary>El permiso vale para UNA maquina. Autorizar una no abre la de al
    /// lado, que es la diferencia entre una excepcion puntual y un agujero.</summary>
    [Fact]
    public void The_grant_does_not_travel_to_another_machine()
    {
        var permisos = new ReenrollmentGrants();
        permisos.Authorize(Maquina);

        Assert.False(permisos.TryConsume("otra-maquina"));
        Assert.True(permisos.TryConsume(Maquina));
    }

    [Fact]
    public void The_grant_expires()
    {
        var reloj = new FakeTimeProvider();
        var permisos = new ReenrollmentGrants(reloj);

        permisos.Authorize(Maquina);
        reloj.Advance(ReenrollmentGrants.Vigencia + TimeSpan.FromSeconds(1));

        Assert.False(permisos.IsAuthorized(Maquina));
        Assert.False(permisos.TryConsume(Maquina));
    }

    [Fact]
    public void Just_before_expiring_it_still_works()
    {
        var reloj = new FakeTimeProvider();
        var permisos = new ReenrollmentGrants(reloj);

        permisos.Authorize(Maquina);
        reloj.Advance(ReenrollmentGrants.Vigencia - TimeSpan.FromSeconds(1));

        Assert.True(permisos.TryConsume(Maquina));
    }

    [Fact]
    public void Revoking_closes_the_window_early()
    {
        var permisos = new ReenrollmentGrants();

        permisos.Authorize(Maquina);
        permisos.Revoke(Maquina);

        Assert.False(permisos.TryConsume(Maquina));
    }

    /// <summary>
    /// Sin limpieza, cada permiso que nadie uso se queda en memoria hasta que
    /// reinicien el servidor. Son pocos bytes, pero tambien son puertas que
    /// figuran abiertas en un volcado y no lo estan.
    /// </summary>
    [Fact]
    public void Unused_grants_do_not_pile_up()
    {
        var reloj = new FakeTimeProvider();
        var permisos = new ReenrollmentGrants(reloj);

        for (var i = 0; i < 50; i++)
            permisos.Authorize($"maquina-{i}");

        Assert.Equal(50, permisos.Count);

        reloj.Advance(ReenrollmentGrants.Vigencia + TimeSpan.FromSeconds(1));
        permisos.Authorize(Maquina);

        Assert.Equal(1, permisos.Count);
    }
}
