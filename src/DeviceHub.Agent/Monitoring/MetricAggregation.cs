using DeviceHub.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace DeviceHub.Agent.Monitoring;

/// <summary>Una muestra cruda, tomada cada 5 s.</summary>
public readonly record struct RawSample(
    double CpuPercent,
    double MemoryPercent,
    double DiskFreePercent,
    long NetRxBytesPerSec,
    long NetTxBytesPerSec);

public static class MetricAggregation
{
    /// <summary>Cada cuanto se toma una muestra local.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>El minuto al que pertenece una marca de tiempo. Clave del agregado.</summary>
    public static DateTime MinuteOf(DateTime utc)
        => new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

    /// <summary>
    /// Resume las muestras de un minuto.
    ///
    /// Se guardan promedio Y maximo a proposito: un promedio del 40% puede
    /// esconder un pico sostenido al 100%, que es justo lo que se busca cuando
    /// una estacion va lenta.
    ///
    /// El disco se reporta como el MINIMO de libre entre los discos: lo que
    /// importa es el mas apretado, no la media de todos.
    /// </summary>
    public static MetricSample Aggregate(DateTime minuteUtc, IReadOnlyList<RawSample> samples)
    {
        if (samples.Count == 0)
            throw new ArgumentException("No se puede agregar un minuto sin muestras", nameof(samples));

        return new MetricSample
        {
            Minute = Timestamp.FromDateTime(MinuteOf(minuteUtc)),
            CpuAvg = (float)samples.Average(s => s.CpuPercent),
            CpuMax = (float)samples.Max(s => s.CpuPercent),
            MemoryAvg = (float)samples.Average(s => s.MemoryPercent),
            MemoryMax = (float)samples.Max(s => s.MemoryPercent),
            DiskMinFreePercent = (float)samples.Min(s => s.DiskFreePercent),
            NetRxBytesPerSec = (long)samples.Average(s => s.NetRxBytesPerSec),
            NetTxBytesPerSec = (long)samples.Average(s => s.NetTxBytesPerSec)
        };
    }
}
