using Dapper;

namespace DeviceHub.Server.Data;

public sealed class UserRow
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "viewer";
    public bool IsActive { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public sealed class UserRepository(Db db)
{
    public async Task<UserRow?> FindAsync(string username, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRow>("""
            SELECT id AS Id, username AS Username, password_hash AS PasswordHash,
                   role AS Role, is_active AS IsActive
            FROM users WHERE username = @username
            """, new { username });
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users");
    }

    public async Task CreateAsync(string username, string passwordHash, string role, CancellationToken ct)
        => await CreateAsync(username, passwordHash, role, null, ct);

    public async Task CreateAsync(string username, string passwordHash, string role, string? createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO users (username, password_hash, role, created_by, is_active, created_at, updated_at)
            VALUES (@username, @passwordHash, @role, @createdBy, 1, @now, @now)
            """, new { username, passwordHash, role, createdBy, now = DateTime.UtcNow });
    }

    public async Task<IReadOnlyList<UserRow>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserRow>("""
            SELECT id AS Id, username AS Username, password_hash AS PasswordHash, role AS Role,
                   is_active AS IsActive, created_by AS CreatedBy, last_login_at AS LastLoginAt
            FROM users ORDER BY username
            """);

        return [.. rows];
    }

    public async Task UpdateAsync(string username, string? role, bool? isActive, string? passwordHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE users
            SET role = COALESCE(@role, role),
                is_active = COALESCE(@isActive, is_active),
                password_hash = COALESCE(@passwordHash, password_hash),
                updated_at = @now
            WHERE username = @username
            """, new { username, role, isActive, passwordHash, now = DateTime.UtcNow });
    }

    public async Task TouchLoginAsync(string username, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE users SET last_login_at = @now WHERE username = @username",
            new { username, now = DateTime.UtcNow });
    }

    public async Task<int> CountAdministratorsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE role = 'administrator' AND is_active = 1");
    }
}
