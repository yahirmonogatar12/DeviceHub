using DeviceHub.RemoteHost.Encode;
using DeviceHub.Server.Updates;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Cuanto se encoge la pantalla cuando codifica la CPU, y el escalado en si.
///
/// Se prueba aparte porque los dos fallos que importan aqui NO dan error: un
/// lado impar lo rechaza el codificador mucho despues y por un motivo que no se
/// parece a este, y un paso mal calculado da una imagen torcida que solo se ve
/// mirando la pantalla.
/// </summary>
public class ReducirTests
{
    [Theory]
    [InlineData(1280, 1024, 1280, 1024)]   // la consola del servidor: NO se toca
    [InlineData(1920, 1080, 1920, 1080)]   // lo normal en planta: tampoco
    [InlineData(2560, 1440, 1280, 720)]    // grande: a la mitad exacta
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(800, 600, 800, 600)]
    public void Solo_se_reduce_lo_que_no_cabe(int ancho, int alto, int esperadoAncho, int esperadoAlto)
    {
        Assert.Equal((esperadoAncho, esperadoAlto), Reducir.Cabe(ancho, alto));
    }

    /// <summary>
    /// Cuando hay que reducir es a la MITAD y no a una razon cualquiera: a 0.75
    /// el patron de pixeles que se tira cambia cada cuatro y el texto tiembla.
    /// </summary>
    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(4096, 2160)]
    public void Reducir_es_siempre_una_potencia_de_dos(int ancho, int alto)
    {
        var (a, b) = Reducir.Cabe(ancho, alto);

        Assert.Equal(0, ancho % a);
        Assert.Equal(0, alto % b);
        Assert.Equal(ancho / a, alto / b);
    }

    [Theory]
    [InlineData(1365, 767)]
    [InlineData(1001, 999)]
    [InlineData(3, 3)]
    public void Siempre_devuelve_medidas_pares(int ancho, int alto)
    {
        var (a, b) = Reducir.Cabe(ancho, alto);

        Assert.Equal(0, a % 2);
        Assert.Equal(0, b % 2);
        Assert.True(a >= 2 && b >= 2);
    }

    /// <summary>
    /// Reducir a la mitad tiene que tomar UNA columna de cada dos, no promediar
    /// ni desplazarse. Se pinta un patron de columnas y se comprueba cual salio.
    /// </summary>
    [Fact]
    public void Escalar_a_la_mitad_toma_una_columna_de_cada_dos()
    {
        const int ancho = 8, alto = 4;
        var stride = ancho * 4 + 16;   // con relleno, como lo mapea D3D11
        var bgra = new byte[stride * alto];

        // Columnas pares blancas, impares negras.
        for (var y = 0; y < alto; y++)
        {
            for (var x = 0; x < ancho; x++)
            {
                var v = (byte)(x % 2 == 0 ? 255 : 0);
                var p = y * stride + x * 4;
                bgra[p] = bgra[p + 1] = bgra[p + 2] = v;
                bgra[p + 3] = 255;
            }
        }

        var nv12 = new byte[ancho / 2 * (alto / 2) * 3 / 2];
        Nv12Cpu.Convertir(bgra, stride, nv12, ancho, alto, ancho / 2, alto / 2);

        // Cada pixel de salida cayo en una columna PAR, o sea blanca. En estudio
        // (16-235) el blanco es 235.
        for (var i = 0; i < ancho / 2 * (alto / 2); i++)
            Assert.InRange(nv12[i], 230, 240);
    }

    [Fact]
    public void Sin_escalar_da_lo_mismo_que_la_sobrecarga_de_siempre()
    {
        const int ancho = 4, alto = 2;
        var stride = ancho * 4;
        var bgra = new byte[stride * alto];

        for (var i = 0; i < bgra.Length; i++)
            bgra[i] = (byte)(i * 7 % 251);

        var uno = new byte[ancho * alto * 3 / 2];
        var otro = new byte[ancho * alto * 3 / 2];

        Nv12Cpu.Convertir(bgra, stride, uno, ancho, alto);
        Nv12Cpu.Convertir(bgra, stride, otro, ancho, alto, ancho, alto);

        Assert.Equal(uno, otro);
    }

    /// <summary>
    /// El endpoint de actualizaciones sirve archivos por nombre. Un nombre que
    /// se escape de la carpeta seria lectura arbitraria del disco del servidor.
    /// </summary>
    [Theory]
    [InlineData("production", true)]
    [InlineData("DeviceHub.Agent-1.76.0.zip", true)]
    [InlineData("update.json", true)]
    [InlineData("..", false)]
    [InlineData("../etc", false)]
    [InlineData("..\\..\\Windows", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("", false)]
    [InlineData(".", false)]
    public void Solo_pasan_nombres_sin_ruta(string valor, bool aceptado)
    {
        Assert.Equal(aceptado, UpdateEndpoints.EsNombre(valor));
    }
}
