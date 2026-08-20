using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Remote;
using DeviceHub.Server.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 6: tickets de un solo uso y lease de reconexion.
///
/// El reloj es inyectado para no dormir en los tests. Un test que espera 45
/// segundos reales acaba desactivado, y con el se desactiva la comprobacion de
/// caducidad, que es justo la que importa.
/// </summary>
public class RemoteTicketTests
{
    private const string Sesion = "s-1";
    private const string Maquina = "INPUTM4";

    private static (RemoteTicketRegistry Registro, FakeTimeProvider Reloj) Nuevo()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T08:00:00Z"));
        return (new RemoteTicketRegistry(reloj), reloj);
    }

    [Fact]
    public void A_valid_ticket_is_accepted_once()
    {
        var (registro, _) = Nuevo();
        var (secreto, _) = registro.Issue(Sesion, RemoteRole.Viewer, Maquina, "tecnico1");

        Assert.Equal(
            TicketRejection.Accepted,
            registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out var ticket));

        Assert.NotNull(ticket);
        Assert.Equal("tecnico1", ticket.UserId);

        // Y ya no vale mas. Se distingue de "no existe" a proposito: en la
        // auditoria, un ticket reutilizado no es lo mismo que uno inventado.
        Assert.Equal(
            TicketRejection.AlreadyConsumed,
            registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out _));
    }

    [Fact]
    public void A_ticket_that_never_existed_is_refused()
    {
        var (registro, _) = Nuevo();

        Assert.Equal(
            TicketRejection.NotFound,
            registro.TryConsume("inventado", RemoteRole.Viewer, Sesion, Maquina, out _));

        Assert.Equal(
            TicketRejection.NotFound,
            registro.TryConsume(null, RemoteRole.Viewer, Sesion, Maquina, out _));
    }

    [Fact]
    public void An_expired_ticket_is_refused()
    {
        var (registro, reloj) = Nuevo();
        var (secreto, _) = registro.Issue(Sesion, RemoteRole.Viewer, Maquina);

        reloj.Advance(RemoteTicketRegistry.Vigencia + TimeSpan.FromSeconds(1));

        Assert.Equal(
            TicketRejection.Expired,
            registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out _));
    }

    [Fact]
    public void A_host_ticket_cannot_be_used_as_a_viewer()
    {
        var (registro, _) = Nuevo();
        var (secreto, _) = registro.Issue(Sesion, RemoteRole.Host, Maquina);

        Assert.Equal(
            TicketRejection.WrongRole,
            registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out _));

        // Y NO se ha quemado: si presentarlo en el canal equivocado lo
        // invalidara, cualquiera podria anular tickets ajenos.
        Assert.Equal(
            TicketRejection.Accepted,
            registro.TryConsume(secreto, RemoteRole.Host, Sesion, Maquina, out _));
    }

    [Fact]
    public void A_ticket_is_bound_to_its_session_and_machine()
    {
        var (registro, _) = Nuevo();
        var (secreto, _) = registro.Issue(Sesion, RemoteRole.Viewer, Maquina);

        Assert.Equal(
            TicketRejection.WrongSession,
            registro.TryConsume(secreto, RemoteRole.Viewer, "otra-sesion", Maquina, out _));

        Assert.Equal(
            TicketRejection.WrongMachine,
            registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, "OTRA-PC", out _));

        Assert.Equal(
            TicketRejection.Accepted,
            registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out _));
    }

    [Fact]
    public void Two_simultaneous_consumers_of_the_same_ticket_leave_exactly_one_winner()
    {
        // El caso que hace que la operacion tenga que ser UNA, y no un
        // Validate() seguido de un Consume().
        for (var intento = 0; intento < 200; intento++)
        {
            var (registro, _) = Nuevo();
            var (secreto, _) = registro.Issue(Sesion, RemoteRole.Viewer, Maquina);

            var aceptados = 0;
            using var salida = new Barrier(8);

            Parallel.For(0, 8, indice =>
            {
                salida.SignalAndWait();

                if (registro.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out _) == TicketRejection.Accepted)
                    Interlocked.Increment(ref aceptados);
            });

            Assert.Equal(1, aceptados);
        }
    }

    [Fact]
    public void The_ticket_never_shows_up_in_text()
    {
        var (registro, _) = Nuevo();
        var (secreto, ticket) = registro.Issue(Sesion, RemoteRole.Viewer, Maquina, "tecnico1");

        // ToString acaba en logs y en mensajes de excepcion.
        Assert.DoesNotContain(secreto, ticket.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secreto, ticket.Hash, StringComparison.OrdinalIgnoreCase);

        // Lo que se guarda es el hash, no el secreto.
        Assert.NotEqual(secreto, ticket.Hash);
        Assert.Equal(64, ticket.Hash.Length);   // SHA-256 en hexadecimal
    }

    [Fact]
    public void Every_ticket_is_different()
    {
        var (registro, _) = Nuevo();
        var vistos = new HashSet<string>();

        for (var i = 0; i < 500; i++)
        {
            var (secreto, _) = registro.Issue(Sesion, RemoteRole.Viewer, Maquina);
            Assert.True(vistos.Add(secreto), "un ticket repetido no es una credencial");
        }
    }
}

public class RemoteLeaseTests
{
    private const string Sesion = "s-1";
    private const string Maquina = "INPUTM4";

    private static (RemoteLeaseRegistry Registro, FakeTimeProvider Reloj) Nuevo()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T08:00:00Z"));
        return (new RemoteLeaseRegistry(reloj), reloj);
    }

    [Fact]
    public void A_dropped_viewer_reconnects_inside_the_grace_without_a_new_ticket()
    {
        var (registro, reloj) = Nuevo();
        var primera = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, "tecnico1", primera);

        registro.Detach(Sesion, RemoteRole.Viewer, primera);
        reloj.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(
            LeaseRejection.Accepted,
            registro.TryReconnect(token, RemoteRole.Viewer, Sesion, out var lease));

        Assert.Equal("tecnico1", lease!.UserId);
        Assert.Equal(Maquina, lease.MachineId);
    }

    [Fact]
    public void After_the_grace_the_lease_is_gone()
    {
        var (registro, reloj) = Nuevo();
        var conexion = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, conexion);

        registro.Detach(Sesion, RemoteRole.Viewer, conexion);
        reloj.Advance(RemoteLeaseRegistry.Gracia + TimeSpan.FromSeconds(1));

        Assert.Equal(
            LeaseRejection.Expired,
            registro.TryReconnect(token, RemoteRole.Viewer, Sesion, out _));
    }

    [Fact]
    public void A_lease_cannot_evict_a_connection_that_is_still_alive()
    {
        // La gracia repone una conexion PERDIDA. Si la anterior sigue
        // funcionando, esto no es reconectar: es robar la sesion.
        var (registro, _) = Nuevo();
        var viva = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, viva);

        Assert.Equal(
            LeaseRejection.StillConnected,
            registro.TryReconnect(token, RemoteRole.Viewer, Sesion, out _));
    }

    [Fact]
    public void The_token_is_rotated_on_every_reconnect()
    {
        var (registro, _) = Nuevo();
        var primera = new object();

        var (viejo, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, primera);
        registro.Detach(Sesion, RemoteRole.Viewer, primera);

        Assert.Equal(
            LeaseRejection.Accepted,
            registro.TryReconnect(viejo, RemoteRole.Viewer, Sesion, out _));

        var segunda = new object();
        var (nuevo, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, segunda);

        Assert.NotEqual(viejo, nuevo);

        registro.Detach(Sesion, RemoteRole.Viewer, segunda);

        // El anterior ya no vale: si valiera, la gracia seria una credencial
        // permanente.
        Assert.Equal(
            LeaseRejection.NotFound,
            registro.TryReconnect(viejo, RemoteRole.Viewer, Sesion, out _));

        Assert.Equal(
            LeaseRejection.Accepted,
            registro.TryReconnect(nuevo, RemoteRole.Viewer, Sesion, out _));
    }

    [Fact]
    public void A_viewer_lease_is_no_good_as_a_host()
    {
        var (registro, _) = Nuevo();
        var conexion = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, conexion);
        registro.Detach(Sesion, RemoteRole.Viewer, conexion);

        // Los leases se guardan por sesion Y rol, asi que el del viewer ni
        // siquiera existe del lado del host.
        Assert.Equal(
            LeaseRejection.NotFound,
            registro.TryReconnect(token, RemoteRole.Host, Sesion, out _));
    }

    [Fact]
    public void A_lease_from_another_session_is_no_good()
    {
        var (registro, _) = Nuevo();
        var conexion = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, conexion);
        registro.Detach(Sesion, RemoteRole.Viewer, conexion);

        Assert.Equal(
            LeaseRejection.NotFound,
            registro.TryReconnect(token, RemoteRole.Viewer, "otra-sesion", out _));
    }

    [Fact]
    public void A_clean_close_kills_the_lease()
    {
        var (registro, _) = Nuevo();
        var conexion = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, conexion);

        registro.Revoke(Sesion, RemoteRole.Viewer);

        Assert.Equal(
            LeaseRejection.NotFound,
            registro.TryReconnect(token, RemoteRole.Viewer, Sesion, out _));

        Assert.Equal(0, registro.Count);
    }

    [Fact]
    public void A_late_close_of_an_old_connection_does_not_evict_the_new_one()
    {
        var (registro, _) = Nuevo();
        var vieja = new object();
        var nueva = new object();

        registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, vieja);
        registro.Detach(Sesion, RemoteRole.Viewer, vieja);
        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, nueva);

        // La vieja termina de cerrarse ahora, tarde.
        registro.Detach(Sesion, RemoteRole.Viewer, vieja);

        // La nueva sigue considerandose conectada.
        Assert.Equal(
            LeaseRejection.StillConnected,
            registro.TryReconnect(token, RemoteRole.Viewer, Sesion, out _));
    }

    [Fact]
    public void Restarting_the_server_takes_every_lease_with_it()
    {
        // Los leases viven en memoria a proposito: tras un reinicio hace falta
        // autorizacion nueva, y eso es lo correcto, no una limitacion.
        var (registro, _) = Nuevo();
        var conexion = new object();

        var (token, _) = registro.Establish(Sesion, RemoteRole.Viewer, Maquina, null, conexion);
        registro.Detach(Sesion, RemoteRole.Viewer, conexion);

        var (reiniciado, _) = Nuevo();

        Assert.Equal(
            LeaseRejection.NotFound,
            reiniciado.TryReconnect(token, RemoteRole.Viewer, Sesion, out _));
    }

    [Fact]
    public void The_bootstrap_ticket_never_becomes_renewable()
    {
        // El caso que convierte un ticket de un solo uso en una credencial
        // permanente por la puerta de atras: reconectar con el LEASE no debe
        // resucitar el ticket.
        var reloj = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T08:00:00Z"));
        var tickets = new RemoteTicketRegistry(reloj);
        var leases = new RemoteLeaseRegistry(reloj);

        var (secreto, _) = tickets.Issue(Sesion, RemoteRole.Viewer, Maquina, "tecnico1");

        Assert.Equal(
            TicketRejection.Accepted,
            tickets.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out var ticket));

        var primera = new object();
        var (token, _) = leases.Establish(Sesion, RemoteRole.Viewer, ticket!.TargetMachineId, ticket.UserId, primera);

        leases.Detach(Sesion, RemoteRole.Viewer, primera);

        Assert.Equal(
            LeaseRejection.Accepted,
            leases.TryReconnect(token, RemoteRole.Viewer, Sesion, out _));

        // Y el ticket original sigue muerto.
        Assert.Equal(
            TicketRejection.AlreadyConsumed,
            tickets.TryConsume(secreto, RemoteRole.Viewer, Sesion, Maquina, out _));
    }
}

/// <summary>
/// Un Hello es de arranque O de reconexion, nunca las dos cosas. Aceptar ambas
/// obligaria a decidir cual gana, y esa decision es el hueco por el que se cuela
/// un ticket caducado detras de un token valido.
/// </summary>
public class HelloValidationTests
{
    private static RemotePacket Hola(string ticket, string token) => new()
    {
        ProtocolVersion = RemoteSessionProtocol.Version,
        SessionId = "s-1",
        Hello = new Hello { Role = RemoteRole.Viewer, MachineId = "PC", Ticket = ticket, ReconnectToken = token }
    };

    [Fact]
    public void A_bootstrap_hello_carries_only_a_ticket()
        => Assert.Null(RemoteRelayGrpcService.RevisarHola(Hola("abc", string.Empty), RemoteRole.Viewer));

    [Fact]
    public void A_reconnect_hello_carries_only_a_token()
        => Assert.Null(RemoteRelayGrpcService.RevisarHola(Hola(string.Empty, "xyz"), RemoteRole.Viewer));

    [Fact]
    public void Both_at_once_is_a_protocol_error()
    {
        var queja = RemoteRelayGrpcService.RevisarHola(Hola("abc", "xyz"), RemoteRole.Viewer);

        Assert.NotNull(queja);
        Assert.Equal(RemoteErrorCode.InvalidTicket, queja.Value.Code);
    }

    [Fact]
    public void Neither_is_refused_too()
    {
        var queja = RemoteRelayGrpcService.RevisarHola(Hola(string.Empty, string.Empty), RemoteRole.Viewer);

        Assert.NotNull(queja);
        Assert.Equal(RemoteErrorCode.InvalidTicket, queja.Value.Code);
    }

    [Fact]
    public void An_oversized_credential_is_refused_before_anything_else()
    {
        var largo = new string('a', RemoteSessionProtocol.MaxTicketChars + 1);

        Assert.Equal(
            RemoteErrorCode.PayloadTooLarge,
            RemoteRelayGrpcService.RevisarHola(Hola(largo, string.Empty), RemoteRole.Viewer)!.Value.Code);

        Assert.Equal(
            RemoteErrorCode.PayloadTooLarge,
            RemoteRelayGrpcService.RevisarHola(Hola(string.Empty, largo), RemoteRole.Viewer)!.Value.Code);
    }
}
