using MySqlConnector;

namespace DeviceHub.Server.Data;

public sealed class Db(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    /// MySqlConnector devuelve DATETIME con Kind=Unspecified. Todo lo que hay en
    /// la base esta en UTC (regla global), asi que se re-etiqueta explicitamente:
    /// Timestamp.FromDateTime de protobuf rechaza cualquier otro Kind.
    /// </summary>
    public static DateTime AsUtc(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? AsUtc(DateTime? value)
        => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
