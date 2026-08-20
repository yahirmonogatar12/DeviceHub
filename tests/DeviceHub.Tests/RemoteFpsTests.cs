using DeviceHub.RemoteHost.Encode;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 13: el control de ritmo de captura.
///
/// Va aparte del bitrate porque son dos problemas distintos: el bitrate arregla
/// "la imagen ocupa demasiado" y los FPS arreglan "genero imagenes mas deprisa
/// de lo que caben". Mezclarlos es lo que hace que un control se pelee consigo
/// mismo.
/// </summary>
public class RemoteFpsTests
{
    /// <summary>Sin medida no se toca nada: mover los FPS sin saber como esta la
    /// red es adivinar, y adivinar aqui se ve como tirones.</summary>
    [Fact]
    public void Sin_medida_de_red_no_se_mueve()
        => Assert.Equal(20, ControlFps.Siguiente(20, rttMs: -1));

    [Fact]
    public void Con_la_red_holgada_sube()
        => Assert.True(ControlFps.Siguiente(20, 10) > 20);

    /// <summary>
    /// La zona de trabajo normal NO se toca, y es deliberado. Un control que se
    /// mueve siempre produce vaiven, y el vaiven en los FPS se ve como tirones
    /// -- peor que un ritmo mas bajo pero estable.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(149)]
    public void En_la_zona_normal_se_queda_quieto(double rtt)
        => Assert.Equal(20, ControlFps.Siguiente(20, rtt));

    [Fact]
    public void Con_la_red_cargada_baja()
        => Assert.True(ControlFps.Siguiente(20, 200) < 20);

    /// <summary>Con la red muy mala se baja FUERTE, no descontando de dos en
    /// dos: llegar tarde al fondo son segundos de sesion inutilizable.</summary>
    [Fact]
    public void Con_la_red_muy_mala_baja_de_golpe()
    {
        var suave = 20 - ControlFps.Siguiente(20, 200);
        var brusco = 20 - ControlFps.Siguiente(20, 800);

        Assert.True(brusco > suave);
    }

    [Fact]
    public void Una_red_pesima_sostenida_para_en_el_minimo()
    {
        var fps = ControlFps.Maximo;

        for (var i = 0; i < 100; i++)
            fps = ControlFps.Siguiente(fps, 900);

        Assert.Equal(ControlFps.Minimo, fps);
    }

    [Fact]
    public void Una_red_perfecta_para_en_el_maximo()
    {
        var fps = ControlFps.Minimo;

        for (var i = 0; i < 200; i++)
            fps = ControlFps.Siguiente(fps, 5);

        Assert.Equal(ControlFps.Maximo, fps);
    }

    /// <summary>El objetivo de arranque no es el maximo: empezar arriba hace que
    /// la primera impresion de la sesion sea una pantalla atascada.</summary>
    [Fact]
    public void Se_arranca_por_debajo_del_maximo()
        => Assert.InRange(ControlFps.Inicial, ControlFps.Minimo + 1, ControlFps.Maximo - 1);

    [Fact]
    public void The_ceiling_matches_what_the_encoder_is_told()
    {
        // EL TECHO NO ES DECORATIVO. El control de tasa del codificador reparte
        // el presupuesto entre los FPS que se le declaran, y se le declara este
        // maximo. Subirlo sin subir el bitrate reparte los mismos bits entre
        // mas frames y la imagen se ablanda al moverse: con 60 declarados y 19
        // reales, 3109 kbps producian 0.25 Mbps.
        //
        // 30 es lo que declara RustDesk en su contexto de codificador.
        Assert.Equal(30, ControlFps.Maximo);
    }

    [Fact]
    public void A_healthy_link_climbs_to_the_ceiling_and_stops()
    {
        var fps = ControlFps.Inicial;

        for (var i = 0; i < 40; i++)
            fps = ControlFps.Siguiente(fps, rttMs: 5);

        Assert.Equal(ControlFps.Maximo, fps);
    }
}
