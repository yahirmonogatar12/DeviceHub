using Xunit;
using DeviceHub.RemoteViewer.Render;

namespace DeviceHub.Tests;

/// <summary>
/// El reparto del mosaico.
///
/// El test que manda es el primero, y es una PROPIEDAD, no una tabla de casos:
/// ningun otro reparto puede dejar la imagen mas grande. Escribir a mano lo que
/// deberia salir para cada tamano fue justo como se colaron tres expectativas
/// equivocadas -- daba por bueno que tres pantallas en fila aprovechan mas que
/// 2x2, y es al reves por bastante.
/// </summary>
public class CuadriculaTests
{
    // La ventana del visor maximizada en un monitor 1080p.
    private const double Ancho = 1920, Alto = 1030;

    /// <summary>Lo que mide la imagen encajada con ese reparto. Maximizar esto
    /// ES minimizar el hueco negro: el area util es n * lado^2 / aspecto.</summary>
    private static double Lado(int columnas, int filas, double ancho, double alto, double aspecto)
        => Math.Min(ancho / columnas, alto / filas * aspecto);

    [Fact]
    public void Ningun_otro_reparto_deja_la_imagen_mas_grande()
    {
        foreach (var n in Enumerable.Range(1, 16))
        foreach (var (w, h) in new[] { (1920.0, 1030.0), (1030.0, 1920.0), (800.0, 800.0), (3840.0, 1080.0) })
        foreach (var aspecto in new[] { 16.0 / 9.0, 5.0 / 4.0, 9.0 / 16.0, 32.0 / 9.0 })
        {
            var (columnas, filas) = Cuadricula.Repartir(n, w, h, aspecto);
            var elegido = Lado(columnas, filas, w, h, aspecto);

            for (var otras = 1; otras <= n; otras++)
            {
                var suyas = (int)Math.Ceiling((double)n / otras);

                Assert.True(
                    Lado(otras, suyas, w, h, aspecto) <= elegido + 1e-9,
                    $"{n} pantallas en {w}x{h} aspecto {aspecto:0.00}: " +
                    $"{otras}x{suyas} deja mas imagen que {columnas}x{filas}");
            }
        }
    }

    [Fact]
    public void Siempre_caben_todas_y_sin_filas_de_sobra()
    {
        foreach (var n in Enumerable.Range(1, 16))
        foreach (var (w, h) in new[] { (1920.0, 1030.0), (1030.0, 1920.0), (800.0, 800.0) })
        {
            var (columnas, filas) = Cuadricula.Repartir(n, w, h);

            Assert.True(columnas > 0 && filas > 0);
            Assert.True(columnas * filas >= n, $"{n} no caben en {columnas}x{filas}");
            Assert.True(columnas * (filas - 1) < n, $"{columnas}x{filas} deja una fila vacia con {n}");
        }
    }

    [Fact]
    public void Una_sola_ocupa_todo()
        => Assert.Equal((1, 1), Cuadricula.Repartir(1, Ancho, Alto));

    [Fact]
    public void Seis_en_una_ventana_ancha_van_en_tres_columnas()
        => Assert.Equal((3, 2), Cuadricula.Repartir(6, Ancho, Alto));

    [Fact]
    public void La_forma_de_la_VENTANA_cambia_el_reparto()
    {
        // Lo que la regla de WPF no miraba: con las mismas seis sesiones daba
        // 3x2 tanto tumbada como de pie.
        Assert.NotEqual(
            Cuadricula.Repartir(6, 1920, 1030),
            Cuadricula.Repartir(6, 1030, 1920));
    }

    [Fact]
    public void La_forma_de_las_PANTALLAS_REMOTAS_cambia_el_reparto()
    {
        // Seis monitores de pie no caben como seis apaisados, y la regla de WPF
        // tampoco miraba esto.
        Assert.NotEqual(
            Cuadricula.Repartir(6, Ancho, Alto, 16.0 / 9.0),
            Cuadricula.Repartir(6, Ancho, Alto, 9.0 / 16.0));
    }

    [Fact]
    public void Sin_hueco_medido_todavia_reparte_algo_que_cabe()
    {
        // Antes del primer Measure. No puede devolver 0 columnas ni dejar
        // sesiones fuera de la cuadricula.
        var (columnas, filas) = Cuadricula.Repartir(6, 0, 0);

        Assert.True(columnas > 0 && filas > 0);
        Assert.True(columnas * filas >= 6);
    }
}
