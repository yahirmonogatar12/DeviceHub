using Dapper;

namespace DeviceHub.Server.Data;

public sealed class TerminalSessionRow
{
    public string Id { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public string? WorkingDir { get; set; }
    public int CommandCount { get; set; }
}

public sealed class TerminalRepository(Db db)
{
    /// <summary>
    /// Una sesion de terminal sin actividad se cierra sola.
    ///
    /// Es mas corta que el timeout de control remoto (8 h) a proposito: en una
    /// sesion remota se ve la pantalla y hay una persona delante; un terminal
    /// olvidado abierto es una consola con permisos de SYSTEM esperando a que
    /// alguien pase por ahi.
    /// </summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Estado de la sesion, con su ultima actividad y el directorio actual.
    ///
    /// El directorio sale del ultimo comando ejecutado, no de una columna propia:
    /// ya esta guardado ahi y duplicarlo abriria la puerta a que discrepen.
    /// </summary>
    public async Task<TerminalSessionRow?> GetAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<TerminalSessionRow>("""
            SELECT s.id AS Id, s.machine_id AS MachineId, s.user_id AS UserId,
                   s.started_at AS StartedAt, s.ended_at AS EndedAt,
                   COALESCE(MAX(c.started_at), s.started_at) AS LastActivityAt,
                   SUBSTRING_INDEX(GROUP_CONCAT(c.working_dir ORDER BY c.sequence DESC), ',', 1) AS WorkingDir,
                   COUNT(c.id) AS CommandCount
            FROM machine_sessions s
            LEFT JOIN terminal_commands c ON c.session_id = s.id
            WHERE s.id = @sessionId AND s.kind = 'terminal'
            GROUP BY s.id, s.machine_id, s.user_id, s.started_at, s.ended_at
            """, new { sessionId });
    }

    public async Task<int> RecordAsync(
        string sessionId, string command, string? workingDir, string? output,
        int exitCode, bool truncated, int durationMs, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        var sequence = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(sequence), 0) + 1 FROM terminal_commands WHERE session_id = @sessionId",
            new { sessionId });

        await conn.ExecuteAsync("""
            INSERT INTO terminal_commands (session_id, sequence, command, working_dir, output,
                                           exit_code, truncated, started_at, duration_ms)
            VALUES (@sessionId, @sequence, @command, @workingDir, @output,
                    @exitCode, @truncated, @now, @durationMs)
            """,
            new { sessionId, sequence, command, workingDir, output, exitCode, truncated, now = DateTime.UtcNow, durationMs });

        return sequence;
    }

    /// <summary>Cierre perezoso de sesiones inactivas, sin servicio de fondo.</summary>
    public async Task<int> CloseInactiveAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        return await conn.ExecuteAsync("""
            UPDATE machine_sessions s
            LEFT JOIN (
                SELECT session_id, MAX(started_at) AS last_at
                FROM terminal_commands GROUP BY session_id
            ) c ON c.session_id = s.id
            SET s.ended_at = @now, s.end_reason = 'inactivity'
            WHERE s.kind = 'terminal' AND s.ended_at IS NULL
              AND COALESCE(c.last_at, s.started_at) < @cutoff
            """, new { now = DateTime.UtcNow, cutoff = DateTime.UtcNow - InactivityTimeout });
    }
}
