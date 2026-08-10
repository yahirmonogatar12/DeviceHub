using Dapper;

namespace DeviceHub.Server.Data;

public sealed class UserRow
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "viewer";
    public bool IsActive { get; set; }
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
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO users (username, password_hash, role, is_active, created_at)
            VALUES (@username, @passwordHash, @role, 1, @now)
            """, new { username, passwordHash, role, now = DateTime.UtcNow });
    }
}
