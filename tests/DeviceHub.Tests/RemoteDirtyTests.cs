using DeviceHub.RemoteHost.Capture;
using Vortice;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// La caja que envuelve lo que cambio en un frame.
///
/// Se prueba con cuidado porque los fallos aqui NO dan error: dan pixeles
/// viejos pegados en pantalla, o una franja de color raro en el borde de la
/// zona actualizada, y las dos cosas aparecen mucho despues y en otro sitio.
/// </summary>
public class RemoteDirtyTests
{
    private const int Ancho = 1920;
    private const int Alto = 1080;

    [Fact]
    public void No_rectangles_means_convert_everything()
    {
        // Null NO es "no cambio nada" -- para eso esta DesktopChanged -- es "no
        // se sabe". Sin informacion se convierte todo, que es la respuesta
        // segura: convertir de mas cuesta tiempo, convertir de menos deja
        // pixeles viejos.
        Assert.Null(ZonaSucia.Caja([], Ancho, Alto));
    }

    [Fact]
    public void It_wraps_every_rectangle()
    {
        RawRect[] cambios =
        [
            new(100, 200, 140, 240),
            new(800, 50, 900, 100),
            new(300, 700, 320, 720)
        ];

        var caja = ZonaSucia.Caja(cambios, Ancho, Alto);

        Assert.Equal(new RawRect(100, 50, 900, 720), caja);
    }

    [Fact]
    public void The_box_is_even_on_every_side()
    {
        // NV12 submuestrea el color 2x2. Un origen impar desplaza medio pixel de
        // croma y tine el borde; un ancho impar deja media columna sin
        // actualizar.
        var caja = ZonaSucia.Caja([new RawRect(101, 203, 305, 407)], Ancho, Alto);

        Assert.NotNull(caja);
        Assert.Equal(0, caja!.Value.Left % 2);
        Assert.Equal(0, caja.Value.Top % 2);
        Assert.Equal(0, caja.Value.Right % 2);
        Assert.Equal(0, caja.Value.Bottom % 2);

        // Y redondea HACIA FUERA: hacia dentro recortaria pixeles que si
        // cambiaron, y esos se quedarian con la imagen anterior.
        Assert.True(caja.Value.Left <= 101);
        Assert.True(caja.Value.Top <= 203);
        Assert.True(caja.Value.Right >= 305);
        Assert.True(caja.Value.Bottom >= 407);
    }

    [Fact]
    public void It_never_leaves_the_screen()
    {
        // Redondear hacia fuera en el borde derecho se saldria de la textura, y
        // un blt fuera de la superficie no recorta: falla.
        var caja = ZonaSucia.Caja([new RawRect(1900, 1070, 1920, 1080)], Ancho, Alto);

        Assert.NotNull(caja);
        Assert.True(caja!.Value.Right <= Ancho);
        Assert.True(caja.Value.Bottom <= Alto);
    }

    [Fact]
    public void An_almost_full_screen_change_asks_for_everything()
    {
        // Acotar el blt tiene su propio coste en cambios de estado. Cuando ya
        // cambio casi todo, no compensa.
        Assert.Null(ZonaSucia.Caja([new RawRect(0, 0, Ancho, Alto)], Ancho, Alto));
        Assert.Null(ZonaSucia.Caja([new RawRect(0, 0, Ancho, Alto - 10)], Ancho, Alto));

        // Media pantalla si compensa.
        Assert.NotNull(ZonaSucia.Caja([new RawRect(0, 0, Ancho / 2, Alto)], Ancho, Alto));
    }

    [Fact]
    public void A_backwards_rectangle_does_not_swallow_the_screen()
    {
        // Un rectangulo con los lados al reves sumado tal cual daria una caja
        // enorme y se convertiria todo en silencio para siempre.
        var caja = ZonaSucia.Caja([new RawRect(300, 400, 100, 200)], Ancho, Alto);

        Assert.Equal(new RawRect(100, 200, 300, 400), caja);
    }

    [Fact]
    public void An_empty_rectangle_is_not_a_box()
    {
        Assert.Null(ZonaSucia.Caja([new RawRect(500, 500, 500, 500)], Ancho, Alto));
    }
}
