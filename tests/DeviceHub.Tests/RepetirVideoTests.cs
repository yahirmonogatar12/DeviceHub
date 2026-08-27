using Xunit;
using DeviceHub.RemoteHost.Relay;

namespace DeviceHub.Tests;

/// <summary>
/// La espera entre rehechos de la cadena de video.
///
/// Lo que se prueba aqui no es que doble bonito: es que un fallo PERMANENTE no
/// se pueda convertir en un bucle. Rehacer sin esperar cuando el MFT devuelve
/// siempre E_INVALIDARG quema CPU en una PC de planta y llena el registro de
/// eventos, y las dos cosas se notan en produccion antes que en el visor.
/// </summary>
public class RepetirVideoTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void El_primero_casi_no_espera()
    {
        // La mayoria de los rehechos son por un cambio de escritorio o de
        // pantalla, no por una averia. Ahi retrasar es empeorar.
        Assert.Equal(RepetirVideo.Minima, RepetirVideo.Espera(1));
        Assert.Equal(RepetirVideo.Minima, RepetirVideo.Espera(0));
    }

    [Fact]
    public void Cada_fallo_seguido_espera_mas_que_el_anterior()
    {
        var anterior = TimeSpan.Zero;

        foreach (var n in new[] { 1, 2, 3, 4 })
        {
            var espera = RepetirVideo.Espera(n);

            Assert.True(espera > anterior, $"el intento {n} no espera mas que el anterior");
            anterior = espera;
        }
    }

    [Fact]
    public void La_espera_tiene_techo_y_no_se_desborda()
    {
        // Con muchos fallos seguidos, doblar sin techo se sale de la cuenta y
        // vuelve en negativo -- que seria no esperar nada justo cuando mas hace
        // falta.
        foreach (var n in new[] { 5, 10, 50, 1000, int.MaxValue })
        {
            Assert.Equal(RepetirVideo.Maxima, RepetirVideo.Espera(n));
            Assert.True(RepetirVideo.Espera(n) > TimeSpan.Zero);
        }
    }

    [Fact]
    public void Una_sesion_que_lleva_rato_sana_vuelve_a_empezar_de_cero()
    {
        // Dos tropiezos sin relacion en una sesion de ocho horas no pueden
        // dejarla esperando el maximo para siempre.
        var viejo = Ahora - RepetirVideo.Olvido - TimeSpan.FromSeconds(1);

        Assert.Equal(1, RepetirVideo.Seguidos(previos: 7, ultimo: viejo, ahora: Ahora));
        Assert.Equal(1, RepetirVideo.Seguidos(previos: 7, ultimo: null, ahora: Ahora));
    }

    [Fact]
    public void Dos_fallos_pegados_si_cuentan_como_seguidos()
    {
        var hacePoco = Ahora - TimeSpan.FromSeconds(1);

        Assert.Equal(8, RepetirVideo.Seguidos(previos: 7, ultimo: hacePoco, ahora: Ahora));
    }
}
