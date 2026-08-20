namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Estadistica de las medidas de captura.
///
/// Vive aparte y es publica porque el indice de un percentil se equivoca solo:
/// leer un p95 mal calculado da confianza falsa justo donde hay que decidir si
/// una PC de planta sirve.
/// </summary>
public static class CaptureStats
{
    public static double AverageMs(IReadOnlyList<long> microseconds)
    {
        if (microseconds.Count == 0)
            return 0;

        // long y no double en el acumulador: 30 s de captura son decenas de miles
        // de muestras de microsegundos, muy lejos de desbordar.
        long total = 0;

        foreach (var sample in microseconds)
            total += sample;

        return total / (double)microseconds.Count / 1000d;
    }

    /// <summary>
    /// Percentil sobre una lista YA ordenada de menor a mayor.
    ///
    /// Metodo del rango mas cercano: el percentil p es el valor en la posicion
    /// ceil(p * n), contada desde 1. Con n pequeno el indice se recorta al
    /// ultimo elemento en vez de salirse.
    /// </summary>
    public static double PercentileMs(IReadOnlyList<long> sortedMicroseconds, double percentile)
    {
        if (sortedMicroseconds.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * sortedMicroseconds.Count) - 1;

        return sortedMicroseconds[Math.Clamp(index, 0, sortedMicroseconds.Count - 1)] / 1000d;
    }
}
