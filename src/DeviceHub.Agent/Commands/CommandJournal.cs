using DeviceHub.Contracts;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace DeviceHub.Agent.Commands;

/// <summary>
/// Resultados de comandos ya ejecutados, en disco.
///
/// Es la mitad del agente de la idempotencia. Si la conexion se cae despues de
/// ejecutar pero antes de reportar, el servidor reenvia el comando al reconectar:
/// sin este journal, un RestartService se ejecutaria dos veces, y un
/// RestartMachine reiniciaria la PC otra vez.
///
/// Va a disco y no a memoria precisamente porque el caso peligroso incluye que
/// el servicio se haya reiniciado en medio.
/// </summary>
public sealed class CommandJournal : IDisposable
{
    /// <summary>Suficiente historia para cubrir cualquier reconexion realista.</summary>
    public const int MaxEntries = 500;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public CommandJournal(string directory)
    {
        Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "commands.db")
        }.ToString());

        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS executed_commands (
                command_id  TEXT PRIMARY KEY,
                executed_at TEXT NOT NULL,
                payload     BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public CommandResult? Find(string commandId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM executed_commands WHERE command_id = $id";
            command.Parameters.AddWithValue("$id", commandId);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                return null;

            using var stream = reader.GetStream(0);
            return CommandResult.Parser.ParseFrom(stream);
        }
    }

    public void Record(CommandResult result)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO executed_commands (command_id, executed_at, payload)
                VALUES ($id, $at, $payload);
                DELETE FROM executed_commands WHERE command_id NOT IN (
                    SELECT command_id FROM executed_commands ORDER BY executed_at DESC LIMIT $keep);
                """;
            command.Parameters.AddWithValue("$id", result.CommandId);
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$payload", result.ToByteArray());
            command.Parameters.AddWithValue("$keep", MaxEntries);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();
}
