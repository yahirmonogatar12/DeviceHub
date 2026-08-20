namespace DeviceHub.Server.Remote;

/// <summary>
/// Escribe los contadores de cada sesion viva cada pocos segundos.
///
/// Existe para el checkpoint de planta. Hasta ahora el relay solo publicaba sus
/// numeros al desconectarse un extremo, y eso obliga a terminar la prueba para
/// saber como fue: si el video se ve mal a los tres minutos, hay que cortar para
/// enterarse de si el relay estaba descartando.
///
/// Cada linea lleva el session_id delante para poder cruzarla con lo que
/// imprimen el host y el viewer de la misma sesion. Sin ese identificador, tres
/// numeros de tres maquinas distintas no se reconcilian: se comparan a ojo, que
/// es como se llega a "el viewer recibio mas frames de los que mando el host".
///
/// Callado cuando no hay sesiones: esto convive con los logs de un servidor que
/// ya esta en produccion.
/// </summary>
public sealed class RemoteSessionReporter(RemoteSessionRegistry registro, ILogger<RemoteSessionReporter> log)
    : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var reloj = new PeriodicTimer(Intervalo);

        while (await reloj.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var foto in registro.Snapshot())
                log.LogInformation("Relay: {Sesion}", foto.ToString());
        }
    }
}
