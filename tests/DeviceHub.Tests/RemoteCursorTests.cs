using DeviceHub.RemoteHost.Capture;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 11: la conversion de la forma del puntero a BGRA.
///
/// Es donde se falla, y cuando se falla no hay error: sale un cursor invertido,
/// desplazado media imagen o dentro de un rectangulo negro. Las llamadas a DXGI
/// necesitan un escritorio y no se prueban aqui.
/// </summary>
public class RemoteCursorTests
{
    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int ancho, int x, int y)
    {
        var o = (y * ancho + x) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2], bgra[o + 3]);
    }

    /// <summary>
    /// Monocromo: el bufer trae DOS mascaras apiladas y el alto real es la mitad.
    /// Leerlo como una sola imagen da un cursor del doble de alto con basura
    /// debajo -- y es el error natural, porque DXGI declara el alto del bufer.
    /// </summary>
    [Fact]
    public void El_monocromo_mide_la_mitad_de_lo_que_dice_el_bufer()
    {
        // 8x4 en el bufer = cursor de 8x2. Un byte por fila.
        var datos = new byte[] { 0xFF, 0xFF, 0x00, 0x00 };

        var bgra = CursorShapes.ABgra(CursorShapes.Monocromo, 8, 4, 1, datos, out var alto);

        Assert.Equal(2, alto);
        Assert.Equal(8 * 2 * 4, bgra.Length);
    }

    /// <summary>Las cuatro combinaciones AND/XOR, que es toda la semantica del
    /// formato monocromo.</summary>
    [Fact]
    public void Las_cuatro_combinaciones_de_las_mascaras()
    {
        // Fila AND = 1100 0000, fila XOR = 1010 0000. Cursor de 8x1.
        //   x=0: AND=1 XOR=1 -> invertir, aproximado a negro opaco
        //   x=1: AND=1 XOR=0 -> transparente
        //   x=2: AND=0 XOR=1 -> blanco opaco
        //   x=3: AND=0 XOR=0 -> negro opaco
        var datos = new byte[] { 0b1100_0000, 0b1010_0000 };

        var bgra = CursorShapes.ABgra(CursorShapes.Monocromo, 8, 2, 1, datos, out _);

        Assert.Equal((0, 0, 0, 255), Pixel(bgra, 8, 0, 0));
        Assert.Equal((0, 0, 0, 0), Pixel(bgra, 8, 1, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(bgra, 8, 2, 0));
        Assert.Equal((0, 0, 0, 255), Pixel(bgra, 8, 3, 0));
    }

    /// <summary>El color de verdad pasa tal cual, alfa incluido.</summary>
    [Fact]
    public void El_color_conserva_su_alfa()
    {
        byte[] datos = [10, 20, 30, 128, 1, 2, 3, 255];

        var bgra = CursorShapes.ABgra(CursorShapes.Color, 2, 1, 8, datos, out var alto);

        Assert.Equal(1, alto);
        Assert.Equal((10, 20, 30, 128), Pixel(bgra, 2, 0, 0));
        Assert.Equal((1, 2, 3, 255), Pixel(bgra, 2, 1, 0));
    }

    /// <summary>
    /// En color enmascarado el alfa NO es alfa: mascara puesta y color negro es
    /// XOR contra cero, o sea la pantalla sin tocar -- transparente. Sin esa
    /// regla el cursor sale dentro de un rectangulo negro.
    /// </summary>
    [Fact]
    public void El_color_enmascarado_traduce_la_mascara_a_transparencia()
    {
        byte[] datos =
        [
            0, 0, 0, 0xFF,        // mascara + negro  -> transparente
            9, 9, 9, 0x00,        // sin mascara      -> opaco
            5, 6, 7, 0xFF         // mascara + color  -> invertir, opaco
        ];

        var bgra = CursorShapes.ABgra(CursorShapes.ColorEnmascarado, 3, 1, 12, datos, out _);

        Assert.Equal(0, Pixel(bgra, 3, 0, 0).A);
        Assert.Equal((9, 9, 9, 255), Pixel(bgra, 3, 1, 0));
        Assert.Equal((5, 6, 7, 255), Pixel(bgra, 3, 2, 0));
    }

    /// <summary>El pitch no es ancho*4: la GPU alinea las filas, y usar el ancho
    /// desplaza la imagen un poco mas en cada fila.</summary>
    [Fact]
    public void El_pitch_no_se_confunde_con_el_ancho()
    {
        // 1 pixel de ancho pero 8 bytes de pitch: 4 de datos y 4 de relleno.
        byte[] datos = [1, 2, 3, 255, 0, 0, 0, 0, 4, 5, 6, 255, 0, 0, 0, 0];

        var bgra = CursorShapes.ABgra(CursorShapes.Color, 1, 2, 8, datos, out _);

        Assert.Equal((1, 2, 3, 255), Pixel(bgra, 1, 0, 0));
        Assert.Equal((4, 5, 6, 255), Pixel(bgra, 1, 0, 1));
    }
}
