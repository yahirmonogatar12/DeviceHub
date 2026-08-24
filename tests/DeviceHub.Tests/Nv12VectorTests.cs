using DeviceHub.RemoteHost.Encode;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// EL CAMINO VECTORIZADO TIENE QUE DAR LOS MISMOS BYTES QUE EL ESCALAR.
///
/// No "parecidos": los mismos. Un coeficiente distinto, un desplazamiento de
/// mas o un carril mal colocado no dan error -- dan una imagen con otro tono, o
/// con las columnas barajadas, y eso se descubre mirando la pantalla y dudando
/// de la camara.
///
/// El escalar es la referencia porque lleva meses en produccion y su resultado
/// se ha visto en pantallas reales. Aqui no se comprueba que la conversion sea
/// correcta -- eso lo hace Nv12CpuTests con colores conocidos -- sino que las
/// dos rutas coinciden byte a byte.
/// </summary>
public class Nv12VectorTests
{
    /// <summary>
    /// Fuerza el camino escalar pidiendo una reduccion que no reduce: el rapido
    /// solo entra cuando origen y destino miden lo mismo, asi que pasar unas
    /// medidas de origen distintas -- aunque den el mismo resultado -- lo evita.
    ///
    /// Se compara contra si mismo por construccion: la sobrecarga de siete
    /// argumentos con paso 1:1 recorre el bucle de vecino mas proximo, que con
    /// paso exacto toma cada pixel una vez.
    /// </summary>
    private static byte[] Escalar(byte[] bgra, int stride, int ancho, int alto)
    {
        var salida = new byte[ancho * alto * 3 / 2];

        // Impar para que Vector128.IsHardwareAccelerated no baste: el camino
        // rapido tambien exige ancho >= 16, asi que 15 lo descarta.
        if (ancho >= 16)
        {
            // Se replica el bucle de referencia aqui para no depender de un
            // detalle interno del despachador.
            var croma = ancho * alto;

            for (var y = 0; y < alto; y++)
            {
                var fila = y * stride;
                var destino = y * ancho;

                for (var x = 0; x < ancho; x++)
                {
                    var p = fila + x * 4;

                    int b = bgra[p];
                    int g = bgra[p + 1];
                    int r = bgra[p + 2];

                    salida[destino + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                    if ((y & 1) != 0 || (x & 1) != 0)
                        continue;

                    var uv = croma + y / 2 * ancho + x;

                    salida[uv] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    salida[uv + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }

            return salida;
        }

        Nv12Cpu.Convertir(bgra, stride, salida, ancho, alto);
        return salida;
    }

    private static byte[] Rapido(byte[] bgra, int stride, int ancho, int alto)
    {
        var salida = new byte[ancho * alto * 3 / 2];
        Nv12Cpu.Convertir(bgra, stride, salida, ancho, alto);
        return salida;
    }

    private static byte[] Lienzo(int ancho, int alto, int relleno, Func<int, int, (byte B, byte G, byte R)> color)
    {
        var stride = ancho * 4 + relleno;
        var bgra = new byte[stride * alto];

        for (var y = 0; y < alto; y++)
        {
            for (var x = 0; x < ancho; x++)
            {
                var (b, g, r) = color(x, y);
                var p = y * stride + x * 4;

                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
                bgra[p + 3] = 255;
            }
        }

        return bgra;
    }

    public static TheoryData<int, int, int> Medidas => new()
    {
        // Multiplos de 16: el bucle vectorial cubre la fila entera.
        { 16, 2, 0 },
        { 32, 4, 0 },
        { 1280, 8, 0 },
        { 1920, 4, 0 },

        // NO multiplos de 16: queda cola escalar, que es donde se rompen las
        // vectorizaciones que solo se probaron con medidas redondas.
        { 20, 2, 0 },
        { 30, 4, 0 },
        { 1366, 4, 0 },

        // Con relleno de fila, que es como lo entrega D3D11 al mapear.
        { 16, 2, 64 },
        { 1280, 6, 128 },
        { 1366, 4, 8 },
    };

    [Theory]
    [MemberData(nameof(Medidas))]
    public void Negro(int ancho, int alto, int relleno)
        => Iguales(Lienzo(ancho, alto, relleno, (_, _) => (0, 0, 0)), ancho, alto, relleno);

    [Theory]
    [MemberData(nameof(Medidas))]
    public void Blanco(int ancho, int alto, int relleno)
        => Iguales(Lienzo(ancho, alto, relleno, (_, _) => (255, 255, 255)), ancho, alto, relleno);

    [Theory]
    [MemberData(nameof(Medidas))]
    public void Rojo(int ancho, int alto, int relleno)
        => Iguales(Lienzo(ancho, alto, relleno, (_, _) => (0, 0, 255)), ancho, alto, relleno);

    [Theory]
    [MemberData(nameof(Medidas))]
    public void Verde(int ancho, int alto, int relleno)
        => Iguales(Lienzo(ancho, alto, relleno, (_, _) => (0, 255, 0)), ancho, alto, relleno);

    [Theory]
    [MemberData(nameof(Medidas))]
    public void Azul(int ancho, int alto, int relleno)
        => Iguales(Lienzo(ancho, alto, relleno, (_, _) => (255, 0, 0)), ancho, alto, relleno);

    /// <summary>
    /// Columnas alternadas. Si un carril quedo mal colocado, esto lo delata:
    /// con un color plano cualquier permutacion da el mismo resultado.
    /// </summary>
    [Theory]
    [MemberData(nameof(Medidas))]
    public void Columnas(int ancho, int alto, int relleno)
        => Iguales(
            Lienzo(ancho, alto, relleno, (x, _) => x % 2 == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0)),
            ancho, alto, relleno);

    /// <summary>Un degradado por columna: cada pixel distinto del vecino, que es
    /// lo que rompe un desplazamiento de un solo carril.</summary>
    [Theory]
    [MemberData(nameof(Medidas))]
    public void Degradado(int ancho, int alto, int relleno)
        => Iguales(
            Lienzo(ancho, alto, relleno, (x, y) => ((byte)(x * 7), (byte)(x * 13 + y), (byte)(x * 31 + y * 3))),
            ancho, alto, relleno);

    [Theory]
    [MemberData(nameof(Medidas))]
    public void Aleatorio(int ancho, int alto, int relleno)
    {
        var azar = new Random(20260824);
        var bgra = Lienzo(ancho, alto, relleno,
            (_, _) => ((byte)azar.Next(256), (byte)azar.Next(256), (byte)azar.Next(256)));

        Iguales(bgra, ancho, alto, relleno);
    }

    /// <summary>
    /// Los 256 valores de cada canal, uno por uno. Barre el rango entero en vez
    /// de confiar en que unos cuantos colores representen a los demas.
    /// </summary>
    [Fact]
    public void Todos_los_valores_de_cada_canal()
    {
        const int ancho = 256, alto = 6;

        foreach (var canal in new[] { 0, 1, 2 })
        {
            var bgra = Lienzo(ancho, alto, 0, (x, _) => canal switch
            {
                0 => ((byte)x, (byte)0, (byte)0),
                1 => ((byte)0, (byte)x, (byte)0),
                _ => ((byte)0, (byte)0, (byte)x)
            });

            Iguales(bgra, ancho, alto, 0);
        }
    }

    private static void Iguales(byte[] bgra, int ancho, int alto, int relleno)
    {
        var stride = ancho * 4 + relleno;

        Assert.Equal(
            Escalar(bgra, stride, ancho, alto),
            Rapido(bgra, stride, ancho, alto));
    }
}
