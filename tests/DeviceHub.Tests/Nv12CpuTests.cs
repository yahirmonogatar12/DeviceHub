using DeviceHub.RemoteHost.Encode;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// BGRA a NV12 por CPU, la ruta de las maquinas sin tuberia de video.
///
/// Esta es la mitad que se puede probar sin hardware: la otra -- bajar la
/// textura y entregarsela al MFT -- solo se demuestra en una maquina asi. Y es
/// la mitad donde los errores no dan error: un rango equivocado o los planos
/// cruzados no lanzan nada, se ven como una imagen lavada o con los colores
/// intercambiados, mucho despues y en otro sitio.
/// </summary>
public class Nv12CpuTests
{
    /// <summary>Un bloque de 2x2 de un solo color, con el relleno de fila que
    /// pone D3D11 al mapear.</summary>
    private static byte[] Bgra(byte b, byte g, byte r, int stride)
    {
        var pixeles = new byte[stride * 2];

        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var p = y * stride + x * 4;

                pixeles[p] = b;
                pixeles[p + 1] = g;
                pixeles[p + 2] = r;
                pixeles[p + 3] = 255;
            }
        }

        return pixeles;
    }

    private static byte[] Convertir(byte b, byte g, byte r, int stride = 8)
    {
        var nv12 = new byte[2 * 2 * 3 / 2];

        Nv12Cpu.Convertir(Bgra(b, g, r, stride), stride, nv12, 2, 2);

        return nv12;
    }

    [Fact]
    public void El_negro_es_16_y_el_blanco_235()
    {
        // RANGO DE ESTUDIO, no completo. Con 0-255 la imagen sale lavada al
        // descodificar, y eso se ve mirando la pantalla y dudando de todo lo
        // demas: no hay ningun error por ningun lado.
        Assert.Equal(16, Convertir(0, 0, 0)[0]);
        Assert.Equal(235, Convertir(255, 255, 255)[0]);
    }

    [Fact]
    public void El_gris_no_tiene_color()
    {
        // Sin color, las dos diferencias de croma valen su punto medio. Si los
        // planos estuvieran cruzados o el signo mal, esto se iria de 128.
        var nv12 = Convertir(128, 128, 128);

        Assert.Equal(128, nv12[4]);   // U
        Assert.Equal(128, nv12[5]);   // V
    }

    [Fact]
    public void El_azul_sube_U_y_el_rojo_sube_V()
    {
        // Es la prueba que caza los planos intercambiados, que es el error mas
        // facil de cometer aqui y el mas dificil de ver leyendo el codigo: la
        // imagen sale con los colores cambiados y todo lo demas correcto.
        var azul = Convertir(255, 0, 0);
        var rojo = Convertir(0, 0, 255);

        Assert.True(azul[4] > 200, $"el azul tiene que subir U y dio {azul[4]}");
        Assert.True(azul[5] < 128, $"el azul tiene que bajar V y dio {azul[5]}");

        Assert.True(rojo[5] > 200, $"el rojo tiene que subir V y dio {rojo[5]}");
        Assert.True(rojo[4] < 128, $"el rojo tiene que bajar U y dio {rojo[4]}");
    }

    [Fact]
    public void El_verde_es_mas_claro_que_el_azul()
    {
        // La luma no es el promedio de los tres canales: el ojo ve el verde
        // mucho mas que el azul, y por eso pesa 129 contra 25.
        var verde = Convertir(0, 255, 0)[0];
        var azul = Convertir(255, 0, 0)[0];

        Assert.True(verde > azul * 3, $"verde {verde} contra azul {azul}");
    }

    [Fact]
    public void El_relleno_de_fila_no_se_cuela_en_la_imagen()
    {
        // AQUI ESTA EL FALLO QUE NO SE VE HASTA QUE SE VE. D3D11 mapea con el
        // paso que le conviene -- 1920 pixeles pueden llegar como 2048 -- y
        // leer la fila siguiente a partir del ancho, en vez de a partir del
        // paso, inclina la imagen entera.
        //
        // Se pinta la fila 0 de blanco y la 1 de negro, con relleno de basura
        // entre medias: si el paso se respeta, salen 235 y 16.
        const int stride = 64;

        var bgra = new byte[stride * 2];

        for (var x = 0; x < 2; x++)
        {
            bgra[x * 4] = bgra[x * 4 + 1] = bgra[x * 4 + 2] = 255;
        }

        // Basura en el relleno de la primera fila.
        for (var i = 8; i < stride; i++)
            bgra[i] = 0x7F;

        var nv12 = new byte[2 * 2 * 3 / 2];

        Nv12Cpu.Convertir(bgra, stride, nv12, 2, 2);

        Assert.Equal(235, nv12[0]);   // fila 0, blanca
        Assert.Equal(235, nv12[1]);
        Assert.Equal(16, nv12[2]);    // fila 1, negra
        Assert.Equal(16, nv12[3]);
    }

    [Fact]
    public void El_plano_de_croma_tiene_un_par_por_cada_bloque_de_2x2()
    {
        // 4x4 pixeles son 16 bytes de luma y 8 de croma: cuatro bloques, dos
        // bytes cada uno. Si el indice del croma se calculara con el alto entero
        // en vez de la mitad, esto se saldria del bufer.
        const int stride = 4 * 4;

        var nv12 = new byte[4 * 4 * 3 / 2];

        Nv12Cpu.Convertir(new byte[stride * 4], stride, nv12, 4, 4);

        Assert.Equal(24, nv12.Length);

        // Negro puro: luma a 16 y croma a 128 en los cuatro bloques.
        Assert.All(nv12[..16], v => Assert.Equal(16, v));
        Assert.All(nv12[16..], v => Assert.Equal(128, v));
    }
}
