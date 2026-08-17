using DeviceHub.RemoteViewer.Render;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 20: el tamano con el que se pinta la pantalla remota.
///
/// Se prueba porque cuando esto se equivoca no falla: pinta, y el tecnico ve una
/// pantalla deformada sin saber que lo esta. El resto de la barra del visor
/// -- captura y grabacion -- es E/S de archivos, y probarlo exigiria abstraer el
/// FileStream detras de una interfaz con una sola implementacion.
/// </summary>
public class RemoteScaleTests
{
    /// <summary>Hueco mas ancho que el video: manda el alto y sobra a los lados.</summary>
    [Fact]
    public void Adaptar_conserva_la_relacion_de_aspecto()
    {
        var (ancho, alto) = Escalado.Encajar(1920, 1080, 1600, 600, escala: 0);

        Assert.Equal(600, alto, 3);
        Assert.Equal(1920.0 / 1080.0, ancho / alto, 3);
        Assert.True(ancho <= 1600);
    }

    /// <summary>Y al reves: hueco estrecho y alto, manda el ancho.</summary>
    [Fact]
    public void Adaptar_cabe_tambien_cuando_manda_el_ancho()
    {
        var (ancho, alto) = Escalado.Encajar(1920, 1080, 800, 2000, escala: 0);

        Assert.Equal(800, ancho, 3);
        Assert.Equal(450, alto, 3);
    }

    /// <summary>Escala fija: pixeles reales, sin mirar el hueco. Lo normal es que
    /// no quepa -- por eso el lienzo tiene barras de desplazamiento.</summary>
    [Theory]
    [InlineData(1.0, 1920, 1080)]
    [InlineData(0.5, 960, 540)]
    [InlineData(2.0, 3840, 2160)]
    public void La_escala_fija_ignora_el_hueco(double escala, double esperadoAncho, double esperadoAlto)
    {
        var (ancho, alto) = Escalado.Encajar(1920, 1080, 640, 480, escala);

        Assert.Equal(esperadoAncho, ancho, 3);
        Assert.Equal(esperadoAlto, alto, 3);
    }

    /// <summary>Un hueco degenerado no puede devolver 0: WPF trata Width=0 como
    /// "sin asignar" y el video desaparece hasta el siguiente redimensionado.</summary>
    [Fact]
    public void Nunca_devuelve_cero()
    {
        var (ancho, alto) = Escalado.Encajar(1920, 1080, 1, 1, escala: 0);

        Assert.True(ancho >= 1);
        Assert.True(alto >= 1);
    }
}
