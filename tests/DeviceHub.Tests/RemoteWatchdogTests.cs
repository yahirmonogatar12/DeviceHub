using DeviceHub.RemoteViewer.Render;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// El vigia del visor: cuando una imagen quieta es una pantalla quieta y
/// cuando es un video atascado que necesita un IDR.
/// </summary>
public class RemoteWatchdogTests
{
    [Fact]
    public void Trozos_sin_pintar_es_un_atasco()
    {
        var vigia = new Vigia(atascoMaximo: 3);

        for (var t = 0.0; t <= 3; t += 0.1)
            vigia.Trozo(0, t);

        Assert.Equal(new[] { 0u }, vigia.Atascadas(3.1));
    }

    [Fact]
    public void Trozos_que_se_pintan_no_son_atasco()
    {
        var vigia = new Vigia(atascoMaximo: 3);

        for (var t = 0.0; t <= 10; t += 0.1)
        {
            vigia.Trozo(0, t);
            vigia.Pintado(0);
        }

        Assert.Empty(vigia.Atascadas(10));
    }

    /// <summary>La pantalla remota quieta no manda video, y eso no es un
    /// atasco: lo seria pedir un IDR cada pocos segundos a una PC en reposo.</summary>
    [Fact]
    public void Una_pantalla_quieta_no_es_un_atasco()
    {
        var vigia = new Vigia(atascoMaximo: 3);

        // Un frame que el descodificador se quedo dentro, y luego silencio.
        vigia.Trozo(0, 0);

        Assert.Empty(vigia.Atascadas(60));
    }

    /// <summary>Ese mismo frame retenido no puede contar como atasco cuando la
    /// pantalla vuelve a moverse minutos despues.</summary>
    [Fact]
    public void Un_frame_retenido_no_cuenta_cuando_vuelve_el_movimiento()
    {
        var vigia = new Vigia(atascoMaximo: 3);

        vigia.Trozo(0, 0);
        vigia.Trozo(0, 120);

        Assert.Empty(vigia.Atascadas(120.5));
    }

    /// <summary>Se avisa una vez por periodo, no en cada paquete.</summary>
    [Fact]
    public void El_atasco_se_avisa_una_vez_por_periodo()
    {
        var vigia = new Vigia(atascoMaximo: 3);

        for (var t = 0.0; t <= 3; t += 0.1)
            vigia.Trozo(0, t);

        Assert.Single(vigia.Atascadas(3.1));
        Assert.Empty(vigia.Atascadas(3.2));
    }

    /// <summary>Con dos monitores, la que se mueve no tapa a la que se atasco.</summary>
    [Fact]
    public void Las_pantallas_se_vigilan_por_separado()
    {
        var vigia = new Vigia(atascoMaximo: 3);

        for (var t = 0.0; t <= 4; t += 0.1)
        {
            vigia.Trozo(0, t);
            vigia.Pintado(0);
            vigia.Trozo(1, t);
        }

        Assert.Equal(new[] { 1u }, vigia.Atascadas(4));
    }
}
