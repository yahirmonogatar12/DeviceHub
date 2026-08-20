using DeviceHub.RemoteViewer.Render;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// El ritmo de pintado de los ultimos segundos.
///
/// La media de la sesion entera parecia lo mismo y no lo era: en una sesion de
/// 38 minutos con la pantalla remota quieta marcaba 1.74 FPS mientras la imagen
/// iba fina.
/// </summary>
public class RemoteRateTests
{
    [Fact]
    public void Nothing_painted_is_zero()
    {
        var ritmo = new Ritmo();

        Assert.Equal(0, ritmo.Fps(10));
    }

    [Fact]
    public void A_steady_rate_reads_as_that_rate()
    {
        var ritmo = new Ritmo(ventanaSegundos: 2);

        // 10 s a 25 FPS.
        for (var i = 1; i <= 250; i++)
            ritmo.Marcar(i / 25.0);

        Assert.Equal(25, ritmo.Fps(10), tolerance: 1);
    }

    [Fact]
    public void A_screen_that_went_still_falls_to_zero()
    {
        var ritmo = new Ritmo(ventanaSegundos: 2);

        for (var i = 1; i <= 250; i++)
            ritmo.Marcar(i / 25.0);

        // Cinco segundos despues nadie ha tocado esa PC. Cero es la respuesta
        // correcta y NO es un fallo: sin cambios en el escritorio no hay frames
        // que mandar. La media acumulada, en cambio, habria dicho 16.7.
        Assert.Equal(0, ritmo.Fps(15));
    }

    [Fact]
    public void Before_a_full_window_it_uses_what_really_ran()
    {
        var ritmo = new Ritmo(ventanaSegundos: 2);

        // Medio segundo de sesion, 15 frames: son 30 FPS. Dividir por la ventana
        // entera daria 7.5, y esos primeros segundos son justo cuando alguien
        // mira la barra para ver si la sesion arranco bien.
        for (var i = 1; i <= 15; i++)
            ritmo.Marcar(i / 30.0);

        Assert.Equal(30, ritmo.Fps(0.5), tolerance: 1);
    }
}
