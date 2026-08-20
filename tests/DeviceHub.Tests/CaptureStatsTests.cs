using DeviceHub.RemoteHost.Capture;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// La captura en si necesita GPU y escritorio, asi que se verifica a mano con
/// --capture-test. Lo que si se puede probar aqui es la estadistica, y merece la
/// pena: leyendo un p95 mal calculado se decide que una PC de planta sirve
/// cuando no sirve.
/// </summary>
public class CaptureStatsTests
{
    [Fact]
    public void Empty_measurements_are_zero_not_a_crash()
    {
        // Pasa de verdad: 30 s con la pantalla quieta no dejan ni una muestra.
        Assert.Equal(0, CaptureStats.AverageMs([]));
        Assert.Equal(0, CaptureStats.PercentileMs([], 0.95));
    }

    [Theory]
    [InlineData(0.50, 5)]
    [InlineData(0.95, 10)]
    [InlineData(0.99, 10)]
    [InlineData(1.00, 10)]
    public void Percentile_uses_nearest_rank(double percentile, double expectedMs)
    {
        // 1..10 ms. El p50 por rango mas cercano es el 5o valor, no el promedio
        // entre el 5o y el 6o.
        long[] sorted = [1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000];

        Assert.Equal(expectedMs, CaptureStats.PercentileMs(sorted, percentile));
    }

    [Fact]
    public void A_single_sample_is_every_percentile()
    {
        long[] sorted = [7000];

        Assert.Equal(7, CaptureStats.PercentileMs(sorted, 0.50));
        Assert.Equal(7, CaptureStats.PercentileMs(sorted, 0.95));
    }

    [Fact]
    public void Percentiles_never_walk_off_the_end()
    {
        // El indice del p100 es exactamente Count: sin recorte, esto revienta.
        long[] sorted = [1000, 2000, 3000];

        Assert.Equal(3, CaptureStats.PercentileMs(sorted, 1.0));
    }

    [Fact]
    public void A_heavy_tail_can_push_the_average_above_the_p95()
    {
        // Este caso salio en la primera medida real y parecia un error de
        // calculo: avg 0.31 ms con p95 0.17 ms. No lo era. 95 muestras rapidas
        // y 5 lentisimas bastan para que la media supere al p95, y por eso el
        // test imprime tambien p50, p99 y max: con solo avg y p95 no se
        // distingue una cola pesada de una cuenta mal hecha.
        var sorted = new List<long>();

        for (var i = 0; i < 95; i++)
            sorted.Add(100);

        for (var i = 0; i < 5; i++)
            sorted.Add(50_000);

        var average = CaptureStats.AverageMs(sorted);
        var p95 = CaptureStats.PercentileMs(sorted, 0.95);

        Assert.True(average > p95, $"avg={average} p95={p95}");
        Assert.True(CaptureStats.PercentileMs(sorted, 0.99) > average);
    }
}
