using Dapper;

namespace DeviceHub.Server.Data;

public sealed class EnrollmentCodeRow
{
    public long Id { get; set; }
    public int SiteId { get; set; }
    public string? TargetMachineId { get; set; }
}

public sealed class EnrollmentRepository(Db db)
{
    public async Task CreateAsync(
        string codeHash, int siteId, string createdBy, DateTime expiresAtUtc,
        int maxUses, string? targetMachineId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO enrollment_codes (code_hash, site_id, created_by, created_at, expires_at, max_uses, target_machine_id)
            VALUES (@codeHash, @siteId, @createdBy, @now, @expiresAtUtc, @maxUses, @targetMachineId)
            """,
            new { codeHash, siteId, createdBy, now = DateTime.UtcNow, expiresAtUtc, maxUses, targetMachineId });
    }

    /// <summary>
    /// Consume un codigo. Devuelve null si no existe, vencio o se agoto.
    ///
    /// El UPDATE condicional resuelve las tres cosas y ademas la carrera: dos
    /// agentes usando el mismo codigo de un uso en paralelo se serializan en la
    /// fila, y solo uno ve rows == 1.
    /// </summary>
    public async Task<EnrollmentCodeRow?> TryConsumeAsync(string codeHash, string machineId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var conn = await db.OpenAsync(ct);

        var affected = await conn.ExecuteAsync("""
            UPDATE enrollment_codes
            SET used_count = used_count + 1, used_by_machine_id = @machineId, used_at = @now
            WHERE code_hash = @codeHash
              AND expires_at > @now
              AND used_count < max_uses
            """, new { codeHash, machineId, now });

        if (affected != 1)
            return null;

        return await conn.QuerySingleOrDefaultAsync<EnrollmentCodeRow>("""
            SELECT id AS Id, site_id AS SiteId, target_machine_id AS TargetMachineId
            FROM enrollment_codes WHERE code_hash = @codeHash
            """, new { codeHash });
    }
}
