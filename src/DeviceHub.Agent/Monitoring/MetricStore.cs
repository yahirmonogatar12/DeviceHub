using DeviceHub.Contracts;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace DeviceHub.Agent.Monitoring;

/// <summary>
/// Buffer local de metricas agregadas.
///
/// Aqui es donde SQLite se gana el puesto: el agente sigue muestreando aunque el
/// servidor este caido o la red cortada, y esos minutos no se pueden perder solo
/// porque el servicio se reinicie -- cosa que ademas va a pasar sola en cada
/// actualizacion del agente (Fase 16).
///
/// La muestra se guarda como el propio mensaje protobuf serializado: cero mapeo
/// columna a columna, y un campo nuevo en el contrato no obliga a migrar nada
/// aqui.
/// </summary>
public sealed class MetricStore : IDisposable
{
    /// <summary>24 h de minutos. Un agente desconectado una semana no debe llenar el disco.</summary>
    public const int MaxBufferedMinutes = 1440;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public MetricStore(string directory)
    {
        Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "metrics.db")
        }.ToString());

        _connection.Open();

        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS pending_metrics (
                minute_utc TEXT PRIMARY KEY,
                payload    BLOB NOT NULL
            );
            """);
    }

    public void Append(MetricSample sample)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            // INSERT OR REPLACE: reagregar el mismo minuto lo pisa en vez de duplicarlo.
            command.CommandText = """
                INSERT OR REPLACE INTO pending_metrics (minute_utc, payload) VALUES ($minute, $payload);
                DELETE FROM pending_metrics WHERE minute_utc NOT IN (
                    SELECT minute_utc FROM pending_metrics ORDER BY minute_utc DESC LIMIT $keep);
                """;
            command.Parameters.AddWithValue("$minute", Key(sample));
            command.Parameters.AddWithValue("$payload", sample.ToByteArray());
            command.Parameters.AddWithValue("$keep", MaxBufferedMinutes);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<MetricSample> Take(int max)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM pending_metrics ORDER BY minute_utc LIMIT $max";
            command.Parameters.AddWithValue("$max", max);

            var samples = new List<MetricSample>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                using var stream = reader.GetStream(0);
                samples.Add(MetricSample.Parser.ParseFrom(stream));
            }

            return samples;
        }
    }

    public void Remove(IEnumerable<MetricSample> samples)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM pending_metrics WHERE minute_utc = $minute";
            var parameter = command.Parameters.Add("$minute", SqliteType.Text);

            foreach (var sample in samples)
            {
                parameter.Value = Key(sample);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pending_metrics";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private static string Key(MetricSample sample)
        => sample.Minute.ToDateTime().ToString("O");

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
