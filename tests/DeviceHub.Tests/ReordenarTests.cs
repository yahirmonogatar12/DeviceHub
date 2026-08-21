using DeviceHub.RemoteViewer;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Arrastrar una pestaña a otro sitio. Es aritmetica, y tiene una trampa que
/// solo se ve arrastrando -- por eso esta fuera de la ventana.
/// </summary>
public class ReordenarTests
{
    // Tres fichas: la del medio mucho mas ancha, que es el caso donde falla.
    private static readonly double[] Franja = [100, 300, 100];

    [Fact]
    public void Sobre_la_primera_mitad_de_una_ficha_va_delante_de_ella()
    {
        Assert.Equal(0, Reordenar.IndiceEn(Franja, 10));
        Assert.Equal(1, Reordenar.IndiceEn(Franja, 120));
        Assert.Equal(2, Reordenar.IndiceEn(Franja, 410));
    }

    [Fact]
    public void Pasado_el_centro_ya_cuenta_como_la_siguiente()
    {
        // 100 + 300/2 = 250: justo el centro de la ficha ancha.
        Assert.Equal(1, Reordenar.IndiceEn(Franja, 249));
        Assert.Equal(2, Reordenar.IndiceEn(Franja, 251));
    }

    [Fact]
    public void Una_ficha_ancha_sobre_una_estrecha_no_rebota()
    {
        // AQUI ESTA LA TRAMPA. Comparando contra el BORDE en vez de contra el
        // centro, arrastrar la ficha ancha (300) sobre la estrecha (100) las
        // intercambia, el cursor vuelve a caer sobre la otra, y se intercambian
        // otra vez: la franja tiembla mientras se arrastra.
        //
        // Con el centro, despues de intercambiar el cursor queda del lado bueno.
        double[] despues = [300, 100, 100];   // ya intercambiadas

        // El cursor estaba en 120, dentro de la primera mitad de la ficha ancha
        // que ahora ocupa el sitio 0: se queda donde esta.
        Assert.Equal(0, Reordenar.IndiceEn(despues, 120));
    }

    [Fact]
    public void Mas_alla_del_final_es_la_ultima()
    {
        Assert.Equal(2, Reordenar.IndiceEn(Franja, 5000));
    }

    [Fact]
    public void Sin_pestañas_no_hay_destino()
    {
        Assert.Equal(-1, Reordenar.IndiceEn([], 10));
    }
}
