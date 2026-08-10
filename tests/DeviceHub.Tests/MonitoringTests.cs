using DeviceHub.Agent.Monitoring;
using DeviceHub.Contracts;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace DeviceHub.Tests;

public class CpuUsageTests
{
    /// <summary>
    /// El error clasico de esta formula: el tiempo de kernel YA INCLUYE el idle.
    /// Tratarlos como sumandos separados da porcentajes inflados.
    ///
    /// Aqui: kernel 800 (de los cuales 600 son idle) + user 200 = 1000 totales,
    /// 400 ocupados => 40%, no 62.5%.
    /// </summary>
    [Fact]
    public void Kernel_time_already_includes_idle()
        => Assert.Equal(40d, SystemSampler.ComputeCpuPercent(idleDelta: 600, kernelDelta: 800, userDelta: 200), 3);

    [Fact]
    public void Fully_idle_is_zero()
        => Assert.Equal(0d, SystemSampler.ComputeCpuPercent(1000, 1000, 0), 3);

    [Fact]
    public void No_idle_at_all_is_one_hundred()
        => Assert.Equal(100d, SystemSampler.ComputeCpuPercent(0, 500, 500), 3);

    [Fact]
    public void Zero_elapsed_time_does_not_divide_by_zero()
        => Assert.Equal(0d, SystemSampler.ComputeCpuPercent(0, 0, 0));

    [Fact]
    public void Result_is_always_a_valid_percentage()
    {
        // Contadores incoherentes (suspension, ajuste de reloj) no deben producir
        // -300% ni 4000% en el dashboard.
        Assert.InRange(SystemSampler.ComputeCpuPercent(5000, 100, 100), 0, 100);
        Assert.InRange(SystemSampler.ComputeCpuPercent(-50, 100, 100), 0, 100);
    }
}

public class MetricAggregationTests
{
    private static readonly DateTime Minute = new(2026, 8, 10, 12, 34, 0, DateTimeKind.Utc);

    [Fact]
    public void Truncates_to_the_minute()
    {
        var truncated = MetricAggregation.MinuteOf(new DateTime(2026, 8, 10, 12, 34, 56, 789, DateTimeKind.Utc));

        Assert.Equal(Minute, truncated);
        Assert.Equal(DateTimeKind.Utc, truncated.Kind);
    }

    [Fact]
    public void Keeps_average_and_peak()
    {
        // Un promedio del 40% puede esconder un pico al 100%: por eso se guardan
        // los dos. Es la diferencia entre "va bien" y "algo la satura a ratos".
        var sample = MetricAggregation.Aggregate(Minute,
        [
            new RawSample(10, 50, 80, 100, 10),
            new RawSample(100, 60, 75, 300, 30),
            new RawSample(10, 55, 70, 200, 20)
        ]);

        Assert.Equal(40f, sample.CpuAvg, 3);
        Assert.Equal(100f, sample.CpuMax, 3);
        Assert.Equal(55f, sample.MemoryAvg, 3);
        Assert.Equal(60f, sample.MemoryMax, 3);
        Assert.Equal(200, sample.NetRxBytesPerSec);
        Assert.Equal(20, sample.NetTxBytesPerSec);
    }

    /// <summary>
    /// El disco se reporta como el MINIMO libre: un C: al 5% no puede quedar
    /// escondido tras el promedio con un D: al 90%.
    /// </summary>
    [Fact]
    public void Disk_reports_the_tightest_drive()
    {
        var sample = MetricAggregation.Aggregate(Minute,
        [
            new RawSample(0, 0, 90, 0, 0),
            new RawSample(0, 0, 5, 0, 0)
        ]);

        Assert.Equal(5f, sample.DiskMinFreePercent, 3);
    }

    [Fact]
    public void A_minute_without_samples_is_rejected()
        => Assert.Throws<ArgumentException>(() => MetricAggregation.Aggregate(Minute, []));
}

public class MetricStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhtest-" + Guid.NewGuid().ToString("N"));

    private static MetricSample SampleAt(DateTime minute)
        => new() { Minute = Timestamp.FromDateTime(minute), CpuAvg = 10, CpuMax = 20 };

    [Fact]
    public void Round_trips_through_sqlite()
    {
        using var store = new MetricStore(_directory);
        var minute = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        store.Append(SampleAt(minute));

        var taken = Assert.Single(store.Take(10));
        Assert.Equal(minute, taken.Minute.ToDateTime());
        Assert.Equal(20f, taken.CpuMax);
    }

    [Fact]
    public void Survives_reopening()
    {
        var minute = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        using (var store = new MetricStore(_directory))
            store.Append(SampleAt(minute));

        // Es el punto de SQLite aqui: reiniciar el servicio (o actualizarlo) no
        // puede tirar los minutos que no alcanzaron a enviarse.
        using var reopened = new MetricStore(_directory);
        Assert.Single(reopened.Take(10));
    }

    [Fact]
    public void The_same_minute_twice_does_not_duplicate()
    {
        using var store = new MetricStore(_directory);
        var minute = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        store.Append(SampleAt(minute));
        store.Append(SampleAt(minute));

        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void Removing_only_drops_what_was_sent()
    {
        using var store = new MetricStore(_directory);
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5; i++)
            store.Append(SampleAt(start.AddMinutes(i)));

        var sent = store.Take(3);
        store.Remove(sent);

        Assert.Equal(2, store.Count());
        Assert.Equal(start.AddMinutes(3), store.Take(1)[0].Minute.ToDateTime());
    }

    /// <summary>
    /// Un agente desconectado una semana no debe llenar el disco de la PC.
    /// </summary>
    [Fact]
    public void The_buffer_is_capped_and_keeps_the_newest()
    {
        using var store = new MetricStore(_directory);
        var start = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricStore.MaxBufferedMinutes + 60; i++)
            store.Append(SampleAt(start.AddMinutes(i)));

        Assert.Equal(MetricStore.MaxBufferedMinutes, store.Count());
        Assert.Equal(start.AddMinutes(60), store.Take(1)[0].Minute.ToDateTime());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // El archivo de SQLite puede tardar en liberarse; es un temporal.
        }
    }
}
