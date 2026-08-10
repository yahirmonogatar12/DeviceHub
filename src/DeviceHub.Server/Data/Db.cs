using MySqlConnector;

namespace DeviceHub.Server.Data;

public sealed class Db
{
    public Db(string connectionString)
    {
        // GuidFormat=None es obligatorio, no una preferencia.
        //
        // Por defecto MySqlConnector lee las columnas CHAR(36) como Guid, y como
        // machine_id se maneja como string en todo el sistema, Dapper reventaba
        // con "Object must implement IConvertible" en CADA lectura de maquina.
        //
        // Se fija aqui y no en la cadena de conexion a proposito: es una
        // invariante del codigo, y dejarla en manos de quien despliega significa
        // que un dia alguien escriba la cadena a mano y todo falle en runtime.
        ConnectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.None
        }.ConnectionString;
    }

    public string ConnectionString { get; }

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
