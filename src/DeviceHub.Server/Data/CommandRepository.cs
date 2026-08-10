using System.Text.Json;
using Dapper;
using DeviceHub.Contracts;

namespace DeviceHub.Server.Data;

public sealed class CommandRow
{
    public string Id { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string? ParametersJson { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "pending";
    public string? Result { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class CommandRepository(Db db)
{
    private const string Select = """
        SELECT id AS Id, machine_id AS MachineId, command_type AS CommandType,
               parameters_json AS ParametersJson, requested_by AS RequestedBy,
               requested_at AS RequestedAt, expires_at AS ExpiresAt,
               completed_at AS CompletedAt, status AS Status,
               result AS Result, error_code AS ErrorCode
        FROM machine_commands
        """;

    /// <summary>
    /// Crea el comando y su fila de auditoria EN LA MISMA TRANSACCION.
    ///
    /// Es "si no se audita, no se ejecuta" hecho codigo: si el INSERT de
    /// auditoria falla, el rollback se lleva tambien el comando, y no queda forma
    /// de haber pedido un reinicio sin rastro de quien lo pidio.
    /// </summary>
    public async Task<string> CreateAsync(
        string machineId, CommandType type, IDictionary<string, string> parameters,
        string requestedBy, TimeSpan ttl, AuditEntry audit, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            INSERT INTO machine_commands (id, machine_id, command_type, parameters_json,
                                          requested_by, requested_at, expires_at, status)
            VALUES (@id, @machineId, @type, @parameters, @requestedBy, @now, @expiresAt, 'pending')
            """,
            new
            {
                id,
                machineId,
                type = type.ToString(),
                parameters = parameters.Count == 0 ? null : JsonSerializer.Serialize(parameters),
                requestedBy,
                now,
                expiresAt = now.Add(ttl)
            }, tx);

        await AuditRepository.WriteAsync(conn, tx, audit with { Details = $"{audit.Details} commandId={id}".Trim() });

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task MarkSentAsync(string commandId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE machine_commands SET status = 'sent', sent_at = @now WHERE id = @commandId AND status = 'pending'",
            new { commandId, now = DateTime.UtcNow });
    }

    /// <summary>
    /// Guarda el resultado que reporto el agente.
    ///
    /// La guarda `status NOT IN ('completed','failed','expired')` hace inocuo un
    /// resultado repetido: si el agente reenvia por una reconexion, no se pisa lo
    /// que ya quedo registrado.
    /// </summary>
    public async Task ApplyResultAsync(CommandResult result, string machineId, CancellationToken ct)
    {
        var status = result.Status.ToString().ToLowerInvariant();
        var terminal = result.Status is CommandStatus.Completed or CommandStatus.Failed or CommandStatus.Expired;

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync($"""
            UPDATE machine_commands
            SET status = @status,
                result = @resultText,
                error_code = @errorCode,
                agent_version = @agentVersion,
                started_at = COALESCE(started_at, @now),
                completed_at = {(terminal ? "@now" : "completed_at")}
            WHERE id = @commandId
              AND machine_id = @machineId
              AND status NOT IN ('completed', 'failed', 'expired')
            """,
            new
            {
                commandId = result.CommandId,
                machineId,
                status,
                resultText = string.IsNullOrEmpty(result.Result) ? null : result.Result,
                errorCode = string.IsNullOrEmpty(result.ErrorCode) ? null : result.ErrorCode,
                agentVersion = string.IsNullOrEmpty(result.AgentVersion) ? null : result.AgentVersion,
                now = DateTime.UtcNow
            });
    }

    /// <summary>
    /// Vencimiento perezoso: no hay servicio de fondo barriendo cada segundo. Se
    /// invoca cuando importa -- al conectar un agente y al consultar el dashboard
    /// -- porque la proteccion de verdad la hace el agente comprobando expires_at
    /// antes de ejecutar. Esto solo mantiene honesto el reporte.
    /// </summary>
    public async Task<int> ExpireStaleAsync(string? machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE machine_commands
            SET status = 'expired', completed_at = @now
            WHERE status IN ('pending', 'sent')
              AND expires_at < @now
              AND (@machineId IS NULL OR machine_id = @machineId)
            """, new { machineId, now = DateTime.UtcNow });
    }

    /// <summary>Comandos aun entregables. Se llama tras ExpireStaleAsync.</summary>
    public async Task<IReadOnlyList<CommandRow>> GetDeliverableAsync(string machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<CommandRow>($"""
            {Select}
            WHERE machine_id = @machineId AND status IN ('pending', 'sent') AND expires_at > @now
            ORDER BY requested_at
            """, new { machineId, now = DateTime.UtcNow });

        return [.. rows];
    }

    public async Task<IReadOnlyList<CommandRow>> ListAsync(string machineId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<CommandRow>(
            $"{Select} WHERE machine_id = @machineId ORDER BY requested_at DESC LIMIT @limit",
            new { machineId, limit });

        return [.. rows];
    }

    public async Task<CommandRow?> GetAsync(string commandId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CommandRow>(
            $"{Select} WHERE id = @commandId", new { commandId });
    }

    public static IDictionary<string, string> ParseParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
