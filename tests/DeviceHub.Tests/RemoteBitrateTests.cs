using DeviceHub.RemoteHost.Encode;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 13: el controlador de bitrate.
///
/// Es funcion pura justamente para poder probarlo asi. Un controlador de
/// congestion no se valida mirando una sesion: se le dan series de medidas y se
/// mira a donde converge, que es lo que no se puede hacer si la logica vive
/// dentro del bucle de red.
/// </summary>
public class RemoteBitrateTests
{
    private const int Capacidad = 8;

    [Fact]
    public void Con_la_cola_llena_baja()
        => Assert.True(ControlBitrate.Siguiente(6_000_000, Capacidad, Capacidad) < 6_000_000);

    [Fact]
    public void Con_la_cola_vacia_sube()
        => Assert.True(ControlBitrate.Siguiente(6_000_000, 0, Capacidad) > 6_000_000);

    /// <summary>
    /// Con algo de cola NO se toca. Subir en cuanto queda un hueco es lo que
    /// produce el vaiven de bajar y subir cada dos segundos, que se ve como
    /// pulsos de nitidez y molesta mas que un bitrate bajo estable.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Con_algo_de_cola_se_queda_quieto(int ocupacion)
        => Assert.Equal(6_000_000, ControlBitrate.Siguiente(6_000_000, ocupacion, Capacidad));

    /// <summary>
    /// Baja deprisa y sube despacio. Pasarse por abajo cuesta un poco de nitidez
    /// unos segundos; pasarse por arriba congela la imagen.
    /// </summary>
    [Fact]
    public void Baja_mas_deprisa_de_lo_que_sube()
    {
        var caida = 6_000_000 - ControlBitrate.Siguiente(6_000_000, Capacidad, Capacidad);
        var subida = ControlBitrate.Siguiente(6_000_000, 0, Capacidad) - 6_000_000;

        Assert.True(caida > subida);
    }

    /// <summary>Una red mala sostenida converge al minimo y se queda ahi: por
    /// debajo la imagen deja de servir para trabajar.</summary>
    [Fact]
    public void Una_red_mala_sostenida_para_en_el_minimo()
    {
        var bitrate = 15_000_000;

        for (var i = 0; i < 200; i++)
            bitrate = ControlBitrate.Siguiente(bitrate, Capacidad, Capacidad);

        Assert.Equal(ControlBitrate.Minimo, bitrate);
    }

    /// <summary>Y una red sana no se dispara sin freno.</summary>
    [Fact]
    public void Una_red_sana_para_en_el_maximo()
    {
        var bitrate = ControlBitrate.Minimo;

        for (var i = 0; i < 500; i++)
            bitrate = ControlBitrate.Siguiente(bitrate, 0, Capacidad);

        Assert.Equal(ControlBitrate.Maximo, bitrate);
    }

    /// <summary>Capacidad cero es una cola que aun no existe. No puede dividir
    /// por cero ni devolver algo fuera de rango.</summary>
    [Fact]
    public void Una_cola_sin_capacidad_no_rompe_nada()
    {
        var bitrate = ControlBitrate.Siguiente(6_000_000, 0, 0);

        Assert.InRange(bitrate, ControlBitrate.Minimo, ControlBitrate.Maximo);
    }

    // -- De donde se arranca --------------------------------------------------

    [Fact]
    public void A_1080p_screen_starts_around_one_and_a_half_megabits()
    {
        // 2073 kbps del preset de RustDesk por 0.67 de calidad equilibrada. El
        // valor viejo era 6 Mbps FIJOS para cualquier resolucion: cuatro veces
        // esto, y el bitrate no es solo ancho de banda -- es tamano de frame, y
        // un frame cuatro veces mas gordo tarda cuatro veces mas en cruzar.
        var bitrate = ControlBitrate.PorResolucion(1920, 1080);

        Assert.InRange(bitrate, 1_300_000, 1_450_000);
    }

    [Fact]
    public void A_smaller_screen_asks_for_less()
    {
        // Es lo que hace que repartir por tamano tenga sentido con dos
        // monitores: dividir un total a partes iguales le daba lo mismo a un
        // 1280x1024 que a un 4K.
        var pequena = ControlBitrate.PorResolucion(1280, 1024);
        var grande = ControlBitrate.PorResolucion(1920, 1080);

        Assert.True(pequena < grande);
    }

    [Fact]
    public void Four_k_does_not_ask_for_four_times_1080p()
    {
        // Por eso es una tabla y no una multiplicacion: la curva es sublineal.
        var mil = ControlBitrate.PorResolucion(1920, 1080);
        var cuatroK = ControlBitrate.PorResolucion(3840, 2160);

        Assert.True(cuatroK > mil);
        Assert.True(cuatroK < mil * 4);
    }

    [Fact]
    public void Odd_sizes_stay_inside_the_limits()
    {
        Assert.InRange(ControlBitrate.PorResolucion(0, 0), ControlBitrate.Minimo, ControlBitrate.Maximo);
        Assert.InRange(ControlBitrate.PorResolucion(15360, 8640), ControlBitrate.Minimo, ControlBitrate.Maximo);
    }

    [Fact]
    public void A_still_screen_does_not_earn_more_bitrate()
    {
        // LA COLA VACIA NO DEMUESTRA NADA si no se esta codificando. Antes se
        // subia igual, asi que con el escritorio quieto trepaba hasta el techo
        // y en cuanto alguien movia una ventana salia a 15 Mbps contra una red
        // que nunca se habia medido.
        Assert.Equal(
            2_000_000,
            ControlBitrate.Siguiente(2_000_000, 0, Capacidad, pantallaViva: false));

        Assert.True(
            ControlBitrate.Siguiente(2_000_000, 0, Capacidad, pantallaViva: true) > 2_000_000);
    }

    [Fact]
    public void A_still_screen_still_gives_way_when_it_does_not_fit()
    {
        // Bajar SI se hace siempre: si la cola esta llena da igual por que --
        // lo que hay dentro ya no cabe.
        Assert.True(
            ControlBitrate.Siguiente(2_000_000, Capacidad, Capacidad, pantallaViva: false) < 2_000_000);
    }

    // -- Calidad ---------------------------------------------------------------

    [Fact]
    public void Quality_multiplies_the_budget()
    {
        var fiel = ControlBitrate.PorResolucion(1920, 1080, ControlBitrate.CalidadFiel);
        var media = ControlBitrate.PorResolucion(1920, 1080, ControlBitrate.CalidadEquilibrada);
        var rapida = ControlBitrate.PorResolucion(1920, 1080, ControlBitrate.CalidadRapida);

        Assert.True(fiel > media);
        Assert.True(media > rapida);

        // Fiel al original tiene que acercarse a lo que gasta RustDesk en esta
        // misma PC (3846 kbps medidos). Con Equilibrado estabamos en 1.4 Mbps y
        // por eso la imagen se ablandaba al mover una ventana.
        Assert.InRange(fiel, 2_800_000, 3_300_000);
    }

    [Fact]
    public void It_climbs_fast_enough_to_matter()
    {
        // De Equilibrado a lo que gasta RustDesk, con la red holgada y la
        // pantalla moviendose. Al 10 % por paso hacian falta 11 pasos -- 22
        // segundos, y un arrastre de ventana dura dos.
        var bitrate = ControlBitrate.PorResolucion(1920, 1080);
        var pasos = 0;

        while (bitrate < 3_800_000 && pasos < 20)
        {
            bitrate = ControlBitrate.Siguiente(bitrate, 0, Capacidad, pantallaViva: true);
            pasos++;
        }

        Assert.True(bitrate >= 3_800_000);
        Assert.True(pasos <= 8, $"tardo {pasos} pasos en llegar");
    }
}
