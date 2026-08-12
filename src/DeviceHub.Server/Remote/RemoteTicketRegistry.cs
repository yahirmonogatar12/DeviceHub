using System.Collections.Concurrent;
using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Security;

namespace DeviceHub.Server.Remote;

/// <summary>
/// Por que se rechazo un ticket. Al cliente se le devuelve siempre lo mismo --
/// INVALID_TICKET -- y este detalle solo va a la auditoria: distinguir "expirado"
/// de "no existe" le dice a quien prueba tickets si va por buen camino.
/// </summary>
public enum TicketRejection
{
    Accepted,
    NotFound,
    Expired,
    AlreadyConsumed,
    WrongRole,
    WrongSession,
    WrongMachine
}

/// <summary>
/// Un ticket de arranque. NO guarda el secreto: solo su SHA-256.
///
/// La vinculacion es fuerte a proposito. Un ticket que solo dijera "esta sesion"
/// serviria para entrar como el otro extremo o para apuntar a otra PC; atado
/// ademas al rol y al machine_id, un ticket de viewer no vale como host ni sirve
/// para una maquina distinta.
/// </summary>
public sealed class RemoteTicket
{
    private int _consumido;

    internal RemoteTicket(
        string hash, string sessionId, RemoteRole role, string targetMachineId,
        string? userId, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        Hash = hash;
        SessionId = sessionId;
        Role = role;
        TargetMachineId = targetMachineId;
        UserId = userId;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public string Hash { get; }
    public string SessionId { get; }
    public RemoteRole Role { get; }
    public string TargetMachineId { get; }

    /// <summary>Quien lo pidio. Solo tiene sentido para el VIEWER: el host se
    /// identifica por maquina, no por persona.</summary>
    public string? UserId { get; }

    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool Consumed => Volatile.Read(ref _consumido) != 0;

    /// <summary>
    /// Marca el ticket como usado, y devuelve true SOLO al primero que llegue.
    ///
    /// Esto es lo que hace que dos Hello simultaneos con el mismo ticket no
    /// entren los dos. Un `if (valido) { consumido = true; }` deja una ventana
    /// entre la comprobacion y la marca por la que caben las dos conexiones.
    /// </summary>
    internal bool Reclamar() => Interlocked.Exchange(ref _consumido, 1) == 0;

    /// <summary>Nunca lleva el secreto. Este objeto acaba en mensajes de log y
    /// en excepciones.</summary>
    public override string ToString()
        => $"ticket {Role} sesion {SessionId} maquina {TargetMachineId} vence {ExpiresAt:HH:mm:ss}";
}

/// <summary>
/// Los tickets vivos, en memoria.
///
/// EN MEMORIA A PROPOSITO. Viven entre 30 y 60 segundos; persistirlos solo
/// ampliaria la ventana en la que existe una credencial de acceso a una pantalla
/// y dejaria filas que nadie limpia. Si el servidor se reinicia, el ticket deja
/// de valer -- que es el comportamiento correcto, no una limitacion.
///
/// La base de datos, cuando toque auditar, guarda metadata: quien pidio que y
/// cuando. Nunca el secreto.
/// </summary>
public sealed class RemoteTicketRegistry(TimeProvider? reloj = null)
{
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, RemoteTicket> _tickets = new();

    /// <summary>
    /// Corto a proposito. El ticket solo tiene que sobrevivir al tiempo que
    /// tarda el proceso en arrancar y conectar; todo lo demas lo sostiene el
    /// lease de reconexion.
    /// </summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(45);

    public int Count => _tickets.Count;

    /// <summary>
    /// Emite un ticket y devuelve el secreto EN CLARO una sola vez. Quien llama
    /// se lo entrega a su destinatario y lo olvida; aqui solo queda el hash.
    /// </summary>
    public (string Secreto, RemoteTicket Ticket) Issue(
        string sessionId, RemoteRole role, string targetMachineId, string? userId = null)
    {
        // 256 bits de RandomNumberGenerator. Un Guid no sirve como credencial:
        // no promete ser impredecible.
        var secreto = Secrets.NewMachineToken();
        var ahora = _reloj.GetUtcNow();

        var ticket = new RemoteTicket(
            Secrets.Sha256Hex(secreto), sessionId, role, targetMachineId, userId, ahora, ahora + Vigencia);

        _tickets[ticket.Hash] = ticket;
        Limpiar(ahora);

        return (secreto, ticket);
    }

    /// <summary>
    /// Valida y consume EN UNA SOLA OPERACION.
    ///
    /// No existe un Validate() publico a proposito: dos llamadas separadas
    /// invitan a escribir la carrera, y con tickets de un solo uso esa carrera
    /// significa dos conexiones aceptadas con la misma credencial.
    /// </summary>
    public TicketRejection TryConsume(
        string? secreto, RemoteRole role, string sessionId, string machineId, out RemoteTicket? ticket)
    {
        ticket = null;

        if (string.IsNullOrEmpty(secreto))
            return TicketRejection.NotFound;

        if (!_tickets.TryGetValue(Secrets.Sha256Hex(secreto), out var encontrado))
            return TicketRejection.NotFound;

        // Las comprobaciones de vinculacion van ANTES de reclamar: un ticket de
        // host presentado en el canal del viewer no debe quedar quemado, porque
        // entonces cualquiera podria invalidar tickets ajenos con solo
        // presentarlos en el canal equivocado.
        if (encontrado.Role != role)
            return TicketRejection.WrongRole;

        if (!string.Equals(encontrado.SessionId, sessionId, StringComparison.Ordinal))
            return TicketRejection.WrongSession;

        if (!string.Equals(encontrado.TargetMachineId, machineId, StringComparison.OrdinalIgnoreCase))
            return TicketRejection.WrongMachine;

        if (_reloj.GetUtcNow() >= encontrado.ExpiresAt)
            return TicketRejection.Expired;

        if (!encontrado.Reclamar())
            return TicketRejection.AlreadyConsumed;

        ticket = encontrado;
        return TicketRejection.Accepted;
    }

    /// <summary>Los consumidos y los vencidos no hacen falta para nada: un
    /// ticket ya usado nunca vuelve a valer.</summary>
    private void Limpiar(DateTimeOffset ahora)
    {
        foreach (var (hash, ticket) in _tickets)
        {
            if (ticket.Consumed || ahora >= ticket.ExpiresAt)
                _tickets.TryRemove(new KeyValuePair<string, RemoteTicket>(hash, ticket));
        }
    }
}
