using DeviceHub.Server.Data;

namespace DeviceHub.Server;

/// <summary>
/// Purga diaria de metricas viejas.
///
/// A 200 PCs son 288.000 filas al dia. Sin retencion, la tabla crece para
/// siempre y nadie va a consultar el CPU de una estacion hace ocho meses.
/// </summary>
public sealed class MetricsRetentionService(
    MachineRepository machines,
    IOptions<ServerOptions> options,
    ILogger<MetricsRetentionService> logger) : BackgroundService
{
    private readonly ServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-_options.MetricsRetentionDays);
                var deleted = await machines.PurgeMetricsOlderThanAsync(cutoff, batchSize: 5000, stoppingToken);

                if (deleted > 0)
                    logger.LogInformation("Purga de metricas: {Deleted} filas anteriores a {Cutoff:u}", deleted, cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Que falle la purga no debe tumbar el servidor: los agentes
                // siguen reportando y se reintenta mañana.
                logger.LogError(ex, "Fallo la purga de metricas");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
