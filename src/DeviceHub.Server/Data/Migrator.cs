using DbUp;
using DbUp.Engine.Output;

namespace DeviceHub.Server.Data;

/// <summary>
/// Regla 9: cada cambio de BD pasa por una migracion. Los .sql viven en
/// database/migrations y se embeben en este ensamblado; DbUp los aplica en orden
/// alfabetico y lleva la cuenta en la tabla `schemaversions`.
/// </summary>
public static class Migrator
{
    private const string ResourcePrefix = "DeviceHub.Migrations.";

    public static void Run(string connectionString, ILogger logger)
    {
        // Best-effort a proposito. En produccion el schema lo crea un administrador
        // UNA vez, y el usuario de DeviceHub solo tiene GRANT sobre `devicehub.*`
        // -- sin permisos globales, que es justo lo que se quiere: un error de
        // migracion no puede tocar mes_production.
        //
        // Si esto falla y la base de verdad no existe, DbUp revienta dos lineas
        // mas abajo con un mensaje claro. No se le dan permisos globales al
        // usuario solo para satisfacer a EnsureDatabase.
        try
        {
            EnsureDatabase.For.MySqlDatabase(connectionString);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "EnsureDatabase no pudo crear el schema; se asume que ya existe");
        }

        var upgrader = DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(Migrator).Assembly,
                name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .LogTo(new DbUpLogger(logger))
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
            throw new InvalidOperationException("Fallo la migracion de la base de datos", result.Error);

        logger.LogInformation("Migraciones aplicadas: {Count}", result.Scripts.Count());
    }

    private sealed class DbUpLogger(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);
        public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);
        public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);
        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);
        public void LogError(string format, params object[] args) => logger.LogError(format, args);
        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}
