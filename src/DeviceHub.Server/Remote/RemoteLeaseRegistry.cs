using System.Collections.Concurrent;
using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Security;

namespace DeviceHub.Server.Remote;

public enum LeaseRejection
{
    Accepted,
    NotFound,
    Expired,

    /// <summary>El extremo anterior sigue vivo. La gracia repone una conexion
    /// perdida; no reemplaza una que funciona.</summary>
    StillConnected,

    WrongRole,
    WrongSession
}

/// <summary>
/// El derecho de un extremo a volver a la MISMA sesion tras un microcorte, sin
/// gastar un ticket nuevo.
///
/// Del token de reconexion se guarda solo el hash, igual que con el ticket. Y el
/// token se ROTA en cada aceptacion: uno que sirviera siempre convertiria la
/// gracia en una credencial permanente, que es justo lo que el ticket de un solo
/// uso trata de evitar.
/// </summary>
public sealed class RemoteConnectionLease
{
    internal RemoteConnectionLease(
        string sessionId, RemoteRole role, string machineId, string? userId, DateTimeOffset issuedAt)
    {
        SessionId = sessionId;
        Role = role;
        MachineId = machineId;
        UserId = userId;
        IssuedAt = issuedAt;
    }

    public string SessionId { get; }
    public RemoteRole Role { get; }
    public string MachineId { get; }
    public string? UserId { get; }
    public DateTimeOffset IssuedAt { get; }

    internal string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ReconnectUntil { get; internal set; }

    /// <summary>La conexion que lo esta usando ahora mismo, o null si se perdio.
    /// Es lo que distingue "reconectar" de "robar la sesion".</summary>
    public object? ActiveConnection { get; internal set; }

    public override string ToString()
        => $"lease {Role} sesion {SessionId} maquina {MachineId} gracia hasta {ReconnectUntil:HH:mm:ss}";
}

/// <summary>
/// Los leases vivos. SOLO EN MEMORIA, igual que las sesiones del relay: si el
/// servidor se reinicia hay que volver a pedir autorizacion, y eso es correcto.
/// </summary>
public sealed class RemoteLeaseRegistry(TimeProvider? reloj = null)
{
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, RemoteConnectionLease> _leases = new();
    private readonly Lock _puerta = new();

    /// <summary>
    /// Suficiente para un microcorte de red o para que el tecnico cierre y
    /// vuelva a abrir la ventana. No para dejar una puerta abierta: pasado esto,
    /// autorizacion nueva.
    /// </summary>
    public static readonly TimeSpan Gracia = TimeSpan.FromSeconds(30);

    public int Count => _leases.Count;

    private static string Clave(string sessionId, RemoteRole role) => $"{sessionId}|{role}";

    /// <summary>
    /// Crea el lease tras consumir un ticket, o lo renueva tras una reconexion.
    /// Devuelve el token NUEVO en claro; aqui solo queda su hash.
    /// </summary>
    public (string Token, RemoteConnectionLease Lease) Establish(
        string sessionId, RemoteRole role, string machineId, string? userId, object conexion)
    {
        var token = Secrets.NewMachineToken();
        var ahora = _reloj.GetUtcNow();

        lock (_puerta)
        {
            var lease = _leases.TryGetValue(Clave(sessionId, role), out var existente)
                ? existente
                : new RemoteConnectionLease(sessionId, role, machineId, userId, ahora);

            lease.TokenHash = Secrets.Sha256Hex(token);
            lease.ReconnectUntil = ahora + Gracia;
            lease.ActiveConnection = conexion;

            _leases[Clave(sessionId, role)] = lease;
            return (token, lease);
        }
    }

    /// <summary>
    /// Comprueba un token de reconexion. NO lo consume: si se acepta, quien
    /// llama tiene que llamar a Establish para rotarlo.
    /// </summary>
    public LeaseRejection TryReconnect(
        string? token, RemoteRole role, string sessionId, out RemoteConnectionLease? lease)
    {
        lease = null;

        if (string.IsNullOrEmpty(token))
            return LeaseRejection.NotFound;

        lock (_puerta)
        {
            if (!_leases.TryGetValue(Clave(sessionId, role), out var encontrado))
                return LeaseRejection.NotFound;

            // Comparacion en tiempo fijo: el token es una credencial y comparar
            // hashes con == filtra informacion por el tiempo de respuesta.
            if (!Secrets.FixedTimeEqualsHex(encontrado.TokenHash, Secrets.Sha256Hex(token)))
                return LeaseRejection.NotFound;

            if (encontrado.Role != role)
                return LeaseRejection.WrongRole;

            if (!string.Equals(encontrado.SessionId, sessionId, StringComparison.Ordinal))
                return LeaseRejection.WrongSession;

            // ORDEN IMPORTANTE: primero se mira si el anterior sigue vivo.
            //
            // La gracia existe para recuperar una conexion PERDIDA. Si la que
            // habia sigue funcionando, un segundo extremo con un token valido
            // estaria echando al primero de su propia sesion, y eso no es
            // reconectar: es robar.
            if (encontrado.ActiveConnection is not null)
                return LeaseRejection.StillConnected;

            if (_reloj.GetUtcNow() >= encontrado.ReconnectUntil)
            {
                _leases.TryRemove(new KeyValuePair<string, RemoteConnectionLease>(
                    Clave(sessionId, role), encontrado));

                return LeaseRejection.Expired;
            }

            lease = encontrado;
            return LeaseRejection.Accepted;
        }
    }

    /// <summary>
    /// La conexion se fue. El lease sigue vivo durante la gracia, contada desde
    /// AHORA: es cuando empieza el microcorte, no cuando se emitio.
    /// </summary>
    public void Detach(string sessionId, RemoteRole role, object conexion)
    {
        lock (_puerta)
        {
            if (!_leases.TryGetValue(Clave(sessionId, role), out var lease))
                return;

            // Solo si sigue siendo ESTA conexion: el cierre tardio de una vieja
            // no puede desalojar a la que ya ocupo su lugar.
            if (!ReferenceEquals(lease.ActiveConnection, conexion))
                return;

            lease.ActiveConnection = null;
            lease.ReconnectUntil = _reloj.GetUtcNow() + Gracia;
        }
    }

    /// <summary>Cierre ordenado: el lease muere con la sesion. Sin esto, un
    /// SessionClose dejaria media hora de gracia sobre una sesion terminada.</summary>
    public void Revoke(string sessionId, RemoteRole role)
    {
        lock (_puerta)
            _leases.TryRemove(Clave(sessionId, role), out _);
    }

    public void RevokeSession(string sessionId)
    {
        Revoke(sessionId, RemoteRole.Host);
        Revoke(sessionId, RemoteRole.Viewer);
    }
}
