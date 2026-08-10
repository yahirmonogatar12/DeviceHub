using Dapper;
using DeviceHub.Contracts;
using DeviceHub.Server.Domain;
using MySqlConnector;

namespace DeviceHub.Server.Data;

public sealed class MachineRow
{
    public string Id { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Hostname { get; set; }
    public string? Area { get; set; }
    public string? Line { get; set; }
    public string? Station { get; set; }
    public string? CurrentIp { get; set; }
    public string? PrimaryMac { get; set; }
    public string? LoggedUser { get; set; }
    public string? AgentVersion { get; set; }
    public long? UptimeSeconds { get; set; }
    public DateTime? LastSeen { get; set; }
    public float? CpuPercent { get; set; }
    public float? MemoryPercent { get; set; }
    public float? DiskFreePercent { get; set; }
    public DateTime? MetricsAt { get; set; }
    public string IdentityState { get; set; } = "ok";
    public string? HardwareFingerprint { get; set; }
    public string FingerprintConfidence { get; set; } = "low";
}

public sealed class MachineAuthRow
{
    public string Id { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public string? TokenHash { get; set; }
    public string IdentityState { get; set; } = "ok";
    public string? HardwareFingerprint { get; set; }
    public string FingerprintConfidence { get; set; } = "low";
}

public sealed class HistoryRow
{
    public string Ip { get; set; } = string.Empty;
    public string? Mac { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
}

public sealed class HardwareRow
{
    public string Hash { get; set; } = string.Empty;
    public string? CpuModel { get; set; }
    public int? CpuCores { get; set; }
    public int? CpuThreads { get; set; }
    public long? TotalMemoryBytes { get; set; }
    public string? GpuModel { get; set; }
    public string? Motherboard { get; set; }
    public string? BiosVersion { get; set; }
    public string? BiosSerial { get; set; }
    public string? OsCaption { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? Disks { get; set; }
    public DateTime CollectedAt { get; set; }
}

public sealed class PlacementRow
{
    public string SiteCode { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public string? Area { get; set; }
    public string? Line { get; set; }
    public string? Station { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
}

public sealed class MachineRepository(Db db)
{
    private const string SummarySelect = """
        SELECT m.id AS Id, s.code AS SiteCode, m.machine_code AS MachineCode,
               m.display_name AS DisplayName, m.hostname AS Hostname,
               m.area AS Area, m.line AS Line, m.station AS Station,
               m.current_ip AS CurrentIp, m.primary_mac AS PrimaryMac,
               m.logged_user AS LoggedUser, m.agent_version AS AgentVersion,
               m.uptime_seconds AS UptimeSeconds, m.last_seen AS LastSeen,
               m.cpu_percent AS CpuPercent, m.memory_percent AS MemoryPercent,
               m.disk_free_percent AS DiskFreePercent, m.metrics_at AS MetricsAt,
               m.identity_state AS IdentityState,
               m.hardware_fingerprint AS HardwareFingerprint,
               m.fingerprint_confidence AS FingerprintConfidence
        FROM machines m
        JOIN sites s ON s.id = m.site_id
        """;

    // ---------------------------------------------------------------- lecturas

    public async Task<IReadOnlyList<MachineRow>> ListAsync(string siteCode, string area, string line, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<MachineRow>($"""
            {SummarySelect}
            WHERE (@site = '' OR s.code = @site)
              AND (@area = '' OR m.area = @area)
              AND (@line = '' OR m.line = @line)
            ORDER BY s.code, m.machine_code
            """, new { site = siteCode, area, line });

        return [.. rows];
    }

    public async Task<MachineRow?> GetAsync(string machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MachineRow>(
            $"{SummarySelect} WHERE m.id = @machineId", new { machineId });
    }

    public async Task<MachineAuthRow?> GetForAuthAsync(string machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MachineAuthRow>("""
            SELECT id AS Id, machine_code AS MachineCode, token_hash AS TokenHash,
                   identity_state AS IdentityState, hardware_fingerprint AS HardwareFingerprint,
                   fingerprint_confidence AS FingerprintConfidence
            FROM machines WHERE id = @machineId
            """, new { machineId });
    }

    public async Task<IReadOnlyList<HistoryRow>> GetIpHistoryAsync(string machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<HistoryRow>("""
            SELECT ip AS Ip, mac AS Mac, valid_from AS ValidFrom, valid_to AS ValidTo
            FROM machine_ip_history WHERE machine_id = @machineId
            ORDER BY valid_from DESC
            """, new { machineId });

        return [.. rows];
    }

    public async Task<IReadOnlyList<PlacementRow>> GetPlacementHistoryAsync(string machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlacementRow>("""
            SELECT s.code AS SiteCode, p.machine_code AS MachineCode, p.area AS Area,
                   p.line AS Line, p.station AS Station, p.valid_from AS ValidFrom,
                   p.valid_to AS ValidTo, p.changed_by AS ChangedBy
            FROM machine_placement_history p
            JOIN sites s ON s.id = p.site_id
            WHERE p.machine_id = @machineId
            ORDER BY p.valid_from DESC
            """, new { machineId });

        return [.. rows];
    }

    /// <summary>
    /// Cuantas OTRAS maquinas comparten este fingerprint. Alimenta la degradacion
    /// aprendida: >=3 significa que el valor no discrimina y se trata como LOW.
    /// </summary>
    public async Task<int> CountSharingFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM machines WHERE hardware_fingerprint = @fingerprint",
            new { fingerprint });
    }

    // -------------------------------------------------------------- escrituras

    public async Task<int?> GetSiteIdAsync(string siteCode, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(
            "SELECT id FROM sites WHERE code = @siteCode", new { siteCode });
    }

    public async Task CreateAsync(
        string machineId, int siteId, string machineCode, string hostname,
        string tokenHash, string fingerprint, FingerprintConfidence confidence, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            INSERT INTO machines (id, site_id, machine_code, hostname, token_hash,
                                  hardware_fingerprint, fingerprint_confidence,
                                  identity_state, created_at, updated_at)
            VALUES (@machineId, @siteId, @machineCode, @hostname, @tokenHash,
                    @fingerprint, @confidence, 'ok', @now, @now)
            """,
            new { machineId, siteId, machineCode, hostname, tokenHash, fingerprint, confidence = Map.ToDb(confidence), now },
            tx);

        await conn.ExecuteAsync("""
            INSERT INTO machine_placement_history (machine_id, site_id, machine_code, valid_from, changed_by)
            VALUES (@machineId, @siteId, @machineCode, @now, 'enrollment')
            """,
            new { machineId, siteId, machineCode, now }, tx);

        await tx.CommitAsync(ct);
    }

    public async Task UpdateTokenAsync(string machineId, string tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE machines SET token_hash = @tokenHash, updated_at = @now WHERE id = @machineId",
            new { machineId, tokenHash, now = DateTime.UtcNow });
    }

    /// <summary>
    /// Aplica un heartbeat.
    ///
    /// Solo el UPDATE de last_seen/usuario/uptime ocurre siempre. Interfaces e
    /// historial de IP se tocan UNICAMENTE cuando el conjunto de IPs cambia: con
    /// un latido cada 30 s, reescribir siempre serian 2.880 escrituras diarias
    /// por PC sin informacion nueva.
    /// </summary>
    public async Task ApplyHeartbeatAsync(string machineId, Heartbeat heartbeat, FingerprintConfidence confidence, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var incoming = heartbeat.Interfaces
            .Select(i => new IpEntry(i.Ip, string.IsNullOrWhiteSpace(i.Mac) ? null : i.Mac))
            .Distinct()
            .ToList();

        // Sin `?? FirstOrDefault()`: si el agente no pudo determinar la primaria por
        // ruta, current_ip queda NULL. Mostrar la IP de un adaptador virtual seria
        // peor que no mostrar ninguna. El historial si guarda todas las IPs.
        var primary = heartbeat.Interfaces.FirstOrDefault(i => i.IsPrimary);

        await using var conn = await db.OpenAsync(ct);

        var open = await conn.QueryAsync<HistoryRow>(
            "SELECT ip AS Ip, mac AS Mac FROM machine_ip_history WHERE machine_id = @machineId AND valid_to IS NULL",
            new { machineId });

        var change = IpHistoryDiff.Compute(open.Select(o => new IpEntry(o.Ip, o.Mac)), incoming);

        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE machines
            SET hostname = @hostname, logged_user = @loggedUser, uptime_seconds = @uptime,
                agent_version = @agentVersion, hardware_fingerprint = @fingerprint,
                fingerprint_confidence = @confidence, last_seen = @now, updated_at = @now
            WHERE id = @machineId
            """,
            new
            {
                machineId,
                hostname = heartbeat.Hostname,
                loggedUser = heartbeat.LoggedUser,
                uptime = heartbeat.UptimeSeconds,
                agentVersion = heartbeat.AgentVersion,
                fingerprint = heartbeat.Fingerprint?.Hash,
                confidence = Map.ToDb(confidence),
                now
            }, tx);

        if (change.HasChanges)
        {
            foreach (var entry in change.ToClose)
            {
                await conn.ExecuteAsync("""
                    UPDATE machine_ip_history SET valid_to = @now
                    WHERE machine_id = @machineId AND valid_to IS NULL AND ip = @ip AND mac <=> @mac
                    """, new { machineId, entry.Ip, entry.Mac, now }, tx);
            }

            foreach (var entry in change.ToOpen)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO machine_ip_history (machine_id, ip, mac, valid_from)
                    VALUES (@machineId, @ip, @mac, @now)
                    """, new { machineId, entry.Ip, entry.Mac, now }, tx);
            }

            await conn.ExecuteAsync(
                "DELETE FROM machine_interfaces WHERE machine_id = @machineId", new { machineId }, tx);

            foreach (var nic in heartbeat.Interfaces)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO machine_interfaces (machine_id, name, ip, mac, is_primary, updated_at)
                    VALUES (@machineId, @name, @ip, @mac, @isPrimary, @now)
                    """,
                    new { machineId, name = nic.Name, ip = nic.Ip, mac = nic.Mac, isPrimary = nic.IsPrimary, now }, tx);
            }

            await conn.ExecuteAsync(
                "UPDATE machines SET current_ip = @ip, primary_mac = @mac WHERE id = @machineId",
                new { machineId, ip = primary?.Ip, mac = primary?.Mac }, tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task MarkConflictAsync(string machineId, string reason, string? sourceIp, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE machines SET identity_state = 'identity_conflict', conflict_detected_at = @now, updated_at = @now
            WHERE id = @machineId
            """, new { machineId, now }, tx);

        await LogEventAsync(conn, tx, machineId, "IDENTITY_CONFLICT", reason, sourceIp, now);

        await tx.CommitAsync(ct);
    }

    /// <summary>Cambio legitimo de placa: se adopta el hardware nuevo y se limpia el conflicto.</summary>
    public async Task ApproveNewHardwareAsync(string machineId, string resolvedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE machines SET identity_state = 'ok', conflict_detected_at = NULL, hardware_fingerprint = NULL, updated_at = @now
            WHERE id = @machineId
            """, new { machineId, now }, tx);

        await LogEventAsync(conn, tx, machineId, "IDENTITY_CONFLICT_APPROVED", $"resuelto por {resolvedBy}", null, now);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Era un clon: se invalida el token para que el agente intruso no pueda
    /// reconectar. La maquina original conserva id, historial y auditoria.
    /// </summary>
    public async Task IssueNewIdentityAsync(string machineId, string resolvedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE machines SET identity_state = 'ok', conflict_detected_at = NULL, token_hash = NULL, updated_at = @now
            WHERE id = @machineId
            """, new { machineId, now }, tx);

        await LogEventAsync(conn, tx, machineId, "IDENTITY_REISSUED",
            $"token invalidado por {resolvedBy}; ambos agentes requieren recovery code", null, now);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Renombrar y mover. machineId es inmutable; todo lo demas es editable y
    /// queda en machine_placement_history como un unico evento.
    /// </summary>
    public async Task MoveAsync(
        string machineId, int siteId, string machineCode, string? displayName,
        string? area, string? line, string? station, string changedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE machine_placement_history SET valid_to = @now
            WHERE machine_id = @machineId AND valid_to IS NULL
            """, new { machineId, now }, tx);

        await conn.ExecuteAsync("""
            INSERT INTO machine_placement_history (machine_id, site_id, machine_code, area, line, station, valid_from, changed_by)
            VALUES (@machineId, @siteId, @machineCode, @area, @line, @station, @now, @changedBy)
            """, new { machineId, siteId, machineCode, area, line, station, now, changedBy }, tx);

        await conn.ExecuteAsync("""
            UPDATE machines
            SET site_id = @siteId, machine_code = @machineCode, display_name = @displayName,
                area = @area, line = @line, station = @station, updated_at = @now
            WHERE id = @machineId
            """, new { machineId, siteId, machineCode, displayName, area, line, station, now }, tx);

        await LogEventAsync(conn, tx, machineId, "MACHINE_MOVED",
            $"{machineCode} @ {area}/{line}/{station} por {changedBy}", null, now);

        await tx.CommitAsync(ct);
    }

    // --------------------------------------------------------- metricas (Fase 6)

    /// <summary>
    /// Guarda un lote de minutos y refresca la ultima medicion en `machines`.
    ///
    /// El upsert por (machine_id, minute_utc) hace el reenvio inocuo: un agente
    /// que reconecta y repite minutos ya guardados los pisa, no los duplica.
    /// </summary>
    public async Task SaveMetricsAsync(string machineId, IReadOnlyList<MetricSample> samples, CancellationToken ct)
    {
        if (samples.Count == 0)
            return;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var sample in samples)
        {
            await conn.ExecuteAsync("""
                INSERT INTO machine_metrics (
                    machine_id, minute_utc, cpu_avg, cpu_max, memory_avg, memory_max,
                    disk_min_free_percent, net_rx_bytes_per_sec, net_tx_bytes_per_sec)
                VALUES (@machineId, @minute, @cpuAvg, @cpuMax, @memAvg, @memMax, @disk, @rx, @tx)
                ON DUPLICATE KEY UPDATE
                    cpu_avg = @cpuAvg, cpu_max = @cpuMax, memory_avg = @memAvg,
                    memory_max = @memMax, disk_min_free_percent = @disk,
                    net_rx_bytes_per_sec = @rx, net_tx_bytes_per_sec = @tx
                """,
                new
                {
                    machineId,
                    minute = sample.Minute.ToDateTime(),
                    cpuAvg = sample.CpuAvg,
                    cpuMax = sample.CpuMax,
                    memAvg = sample.MemoryAvg,
                    memMax = sample.MemoryMax,
                    disk = sample.DiskMinFreePercent,
                    rx = sample.NetRxBytesPerSec,
                    tx = sample.NetTxBytesPerSec
                }, tx);
        }

        var latest = samples.MaxBy(s => s.Minute.Seconds)!;

        // La guarda por fecha evita que el drenado de un backlog deje en `machines`
        // una medicion mas vieja que la que ya habia.
        await conn.ExecuteAsync("""
            UPDATE machines
            SET cpu_percent = @cpu, memory_percent = @memory,
                disk_free_percent = @disk, metrics_at = @minute, updated_at = @now
            WHERE id = @machineId AND (metrics_at IS NULL OR metrics_at < @minute)
            """,
            new
            {
                machineId,
                cpu = latest.CpuAvg,
                memory = latest.MemoryAvg,
                disk = latest.DiskMinFreePercent,
                minute = latest.Minute.ToDateTime(),
                now = DateTime.UtcNow
            }, tx);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Purga por antiguedad, en tandas. Un DELETE de millones de filas de golpe
    /// bloquearia la tabla varios segundos.
    /// </summary>
    public async Task<int> PurgeMetricsOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM machine_metrics WHERE minute_utc < @cutoffUtc LIMIT @batchSize",
                new { cutoffUtc, batchSize });

            total += deleted;

            if (deleted < batchSize)
                break;
        }

        return total;
    }

    // ------------------------------------------------------- inventario (Fase 5)

    public async Task<HardwareRow?> GetHardwareAsync(string machineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<HardwareRow>("""
            SELECT hash AS Hash, cpu_model AS CpuModel, cpu_cores AS CpuCores,
                   cpu_threads AS CpuThreads, total_memory_bytes AS TotalMemoryBytes,
                   gpu_model AS GpuModel, motherboard AS Motherboard,
                   bios_version AS BiosVersion, bios_serial AS BiosSerial,
                   os_caption AS OsCaption, os_version AS OsVersion, os_build AS OsBuild,
                   disks AS Disks, collected_at AS CollectedAt
            FROM machine_hardware WHERE machine_id = @machineId
            """, new { machineId });
    }

    /// <summary>
    /// Guarda el inventario. Devuelve la descripcion del cambio si el hardware es
    /// distinto al que habia, o null si es el reenvio periodico de siempre.
    /// </summary>
    public async Task<string?> SaveHardwareAsync(
        string machineId, HardwareInventory inventory, string disksJson, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);

        var previous = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT hash FROM machine_hardware WHERE machine_id = @machineId", new { machineId });

        var change = previous is not null && previous != inventory.Hash
            ? $"hardware modificado (hash {previous[..8]} -> {inventory.Hash[..8]})"
            : null;

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Parametros con nombre en el UPDATE: evita VALUES(), deprecado desde
        // MySQL 8.0.20, sin atarse a la sintaxis de alias de 8.0.19+.
        await conn.ExecuteAsync("""
            INSERT INTO machine_hardware (
                machine_id, hash, cpu_model, cpu_cores, cpu_threads, total_memory_bytes,
                gpu_model, motherboard, bios_version, bios_serial,
                os_caption, os_version, os_build, disks, collected_at, updated_at)
            VALUES (
                @machineId, @hash, @cpuModel, @cpuCores, @cpuThreads, @totalMemory,
                @gpuModel, @motherboard, @biosVersion, @biosSerial,
                @osCaption, @osVersion, @osBuild, @disks, @now, @now)
            ON DUPLICATE KEY UPDATE
                hash = @hash, cpu_model = @cpuModel, cpu_cores = @cpuCores,
                cpu_threads = @cpuThreads, total_memory_bytes = @totalMemory,
                gpu_model = @gpuModel, motherboard = @motherboard,
                bios_version = @biosVersion, bios_serial = @biosSerial,
                os_caption = @osCaption, os_version = @osVersion, os_build = @osBuild,
                disks = @disks, collected_at = @now, updated_at = @now
            """,
            new
            {
                machineId,
                hash = inventory.Hash,
                cpuModel = Trim(inventory.CpuModel, 160),
                cpuCores = inventory.CpuCores,
                cpuThreads = inventory.CpuThreads,
                totalMemory = inventory.TotalMemoryBytes,
                gpuModel = Trim(inventory.GpuModel, 160),
                motherboard = Trim(inventory.Motherboard, 160),
                biosVersion = Trim(inventory.BiosVersion, 120),
                biosSerial = Trim(inventory.BiosSerial, 120),
                osCaption = Trim(inventory.OsCaption, 160),
                osVersion = Trim(inventory.OsVersion, 60),
                osBuild = Trim(inventory.OsBuild, 60),
                disks = disksJson,
                now
            }, tx);

        if (change is not null)
            await LogEventAsync(conn, tx, machineId, "HARDWARE_CHANGED", change, null, now);

        await tx.CommitAsync(ct);
        return change;
    }

    /// <summary>Un modelo de GPU absurdamente largo no debe tumbar el inventario entero.</summary>
    private static string? Trim(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    public async Task LogEventAsync(string? machineId, string type, string? details, string? sourceIp, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await LogEventAsync(conn, null, machineId, type, details, sourceIp, DateTime.UtcNow);
    }

    private static Task LogEventAsync(
        MySqlConnection conn, MySqlTransaction? tx, string? machineId,
        string type, string? details, string? sourceIp, DateTime now)
        => conn.ExecuteAsync("""
            INSERT INTO machine_events (machine_id, event_type, details, source_ip, created_at)
            VALUES (@machineId, @type, @details, @sourceIp, @now)
            """, new { machineId, type, details, sourceIp, now }, tx);
}
