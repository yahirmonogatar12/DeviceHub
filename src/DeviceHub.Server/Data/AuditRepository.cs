using Dapper;
using DeviceHub.Contracts;
using MySqlConnector;

namespace DeviceHub.Server.Data;

public static class AuditActions
{
    public const string CommandRequested = "COMMAND_REQUESTED";
    public const string ProcessKill = "PROCESS_KILL";
    public const string ServiceStart = "SERVICE_START";
    public const string ServiceStop = "SERVICE_STOP";
    public const string ServiceRestart = "SERVICE_RESTART";
    public const string MachineReboot = "MACHINE_REBOOT";
    public const string MachineShutdown = "MACHINE_SHUTDOWN";
    public const string RemoteRequested = "REMOTE_SESSION_REQUESTED";
    public const string RemoteStart = "REMOTE_SESSION_STARTED";
    public const string RemoteEnd = "REMOTE_SESSION_ENDED";
    public const string MachineMoved = "MACHINE_MOVED";
    public const string IdentityResolved = "IDENTITY_CONFLICT_RESOLVED";
    public const string ReenrollmentAuthorized = "REENROLLMENT_AUTHORIZED";
    public const string EnrollmentCodeCreated = "ENROLLMENT_CODE_CREATED";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string LoginThrottled = "LOGIN_THROTTLED";
    public const string UserCreated = "USER_CREATED";
    public const string UserUpdated = "USER_UPDATED";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string FileDelete = "FILE_DELETE";
    public const string FileWrite = "FILE_WRITE";
    public const string FileRename = "FILE_RENAME";
    public const string FileRead = "FILE_READ";
    public const string TerminalStart = "TERMINAL_START";
    public const string TerminalCommand = "TERMINAL_COMMAND";
    public const string TerminalEnd = "TERMINAL_END";

    /// <summary>
    /// Cada comando peligroso tiene su propia accion auditable. Registrar
    /// "COMMAND_REQUESTED" para un apagado seria tecnicamente cierto e inutil:
    /// buscar despues "quien apago maquinas" no puede exigir abrir el detalle de
    /// cada fila.
    /// </summary>
    public static string ForCommand(CommandType type) => type switch
    {
        CommandType.KillProcess => ProcessKill,
        CommandType.StartService => ServiceStart,
        CommandType.StopService => ServiceStop,
        CommandType.RestartService => ServiceRestart,
        CommandType.RestartMachine => MachineReboot,
        CommandType.ShutdownMachine => MachineShutdown,
        CommandType.DeletePath => FileDelete,
        CommandType.WriteFile => FileWrite,
        CommandType.RenamePath => FileRename,
        CommandType.ReadFile => FileRead,
        CommandType.RunShell => TerminalCommand,
        _ => CommandRequested
    };
}

public sealed record AuditEntry(
    string UserId,
    string? UserRole,
    string Action,
    string? MachineId,
    string? MachineCode,
    string? SiteCode,
    string? RequestId,
    string? SourceIp,
    string Outcome,
    string? Details)
{
    public const string Allowed = "allowed";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

public sealed class AuditRepository(Db db)
{
    private const string Insert = """
        INSERT INTO machine_audit (occurred_at, user_id, user_role, action, machine_id,
                                   machine_code, site_code, request_id, source_ip, outcome, details)
        VALUES (@now, @UserId, @UserRole, @Action, @MachineId,
                @MachineCode, @SiteCode, @RequestId, @SourceIp, @Outcome, @Details)
        """;

    /// <summary>
    /// Escribe la auditoria DENTRO de la transaccion de la accion.
    ///
    /// Es la mitad tecnica de "si no se audita, no se ejecuta": si este INSERT
    /// falla, el rollback se lleva tambien la accion. No hay forma de dejar
    /// ejecutado algo sin rastro.
    /// </summary>
    public static Task WriteAsync(MySqlConnection conn, MySqlTransaction tx, AuditEntry entry)
        => conn.ExecuteAsync(Insert, Parameters(entry), tx);

    /// <summary>Para lo que no tiene transaccion propia: intentos denegados y fallos.</summary>
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(Insert, Parameters(entry));
    }

    public async Task<IReadOnlyList<AuditRow>> ListAsync(string? machineId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AuditRow>("""
            SELECT occurred_at AS OccurredAt, user_id AS UserId, user_role AS UserRole,
                   action AS Action, machine_code AS MachineCode, site_code AS SiteCode,
                   source_ip AS SourceIp, outcome AS Outcome, details AS Details
            FROM machine_audit
            WHERE (@machineId IS NULL OR machine_id = @machineId)
            ORDER BY occurred_at DESC
            LIMIT @limit
            """, new { machineId, limit });

        return [.. rows];
    }

    private static object Parameters(AuditEntry entry) => new
    {
        now = DateTime.UtcNow,
        entry.UserId,
        entry.UserRole,
        entry.Action,
        entry.MachineId,
        entry.MachineCode,
        entry.SiteCode,
        entry.RequestId,
        entry.SourceIp,
        entry.Outcome,
        entry.Details
    };
}

public sealed class AuditRow
{
    public DateTime OccurredAt { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? UserRole { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? MachineCode { get; set; }
    public string? SiteCode { get; set; }
    public string? SourceIp { get; set; }
    public string Outcome { get; set; } = AuditEntry.Allowed;
    public string? Details { get; set; }
}
