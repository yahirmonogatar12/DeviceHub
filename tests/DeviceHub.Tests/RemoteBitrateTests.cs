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
}
