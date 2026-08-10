using Dapper;

namespace DeviceHub.Server.Data;

public sealed class SessionRow
{
    public string Id { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

public sealed class SessionRepository(Db db)
{
    /// <summary>
    /// Una sesion abierta mas tiempo que esto se da por huerfana: el dashboard
    /// se cerro de golpe, la PC del tecnico se apago, o el proceso murio. Sin
    /// esto, la tabla acumularia sesiones eternamente "activas" y la auditoria
    /// diria que alguien lleva tres semanas dentro de una maquina.
    /// </summary>
    public static readonly TimeSpan OrphanTimeout = TimeSpan.FromHours(8);

    public async Task<string> StartAsync(
        string machineId, string userId, string provider, string? deviceId, string? sourceIp, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO machine_sessions (id, machine_id, user_id, provider, device_id, source_ip, started_at)
            VALUES (@id, @machineId, @userId, @provider, @deviceId, @sourceIp, @now)
            """,
            new { id, machineId, userId, provider, deviceId, sourceIp, now = DateTime.UtcNow });

        return id;
    }

    public async Task<SessionRow?> EndAsync(string sessionId, string reason, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE machine_sessions SET ended_at = @now, end_reason = @reason
            WHERE id = @sessionId AND ended_at IS NULL
            """, new { sessionId, reason, now = DateTime.UtcNow });

        return await GetAsync(sessionId, ct);
    }

    public async Task<SessionRow?> GetAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SessionRow>("""
            SELECT id AS Id, machine_id AS MachineId, user_id AS UserId, provider AS Provider,
                   device_id AS DeviceId, started_at AS StartedAt, ended_at AS EndedAt
            FROM machine_sessions WHERE id = @sessionId
            """, new { sessionId });
    }

    /// <summary>Cierre perezoso de huerfanas, igual que el vencimiento de comandos.</summary>
    public async Task<int> CloseOrphansAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE machine_sessions SET ended_at = @cutoff, end_reason = 'timeout'
            WHERE ended_at IS NULL AND started_at < @cutoff
            """, new { cutoff = DateTime.UtcNow - OrphanTimeout });
    }
}
