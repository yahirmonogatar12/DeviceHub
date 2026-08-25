using Xunit;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.Tests;

/// <summary>
/// La aritmetica que dejo morir tres sesiones de planta el 25/08/2026.
///
/// El primero de estos tests es el que habria hecho falta: una sesion que
/// aguanta horas y luego se cae TIENE que reintentar. El codigo viejo contaba la
/// ventana desde el arranque, asi que a las tres horas ya estaba cerrada.
/// </summary>
public class VentanaDeReconexionTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 25, 7, 32, 0, TimeSpan.Zero);

    [Fact]
    public void Una_sesion_de_horas_que_se_cae_todavia_reintenta()
    {
        var inicio = T0;
        var ahora = T0.AddHours(5).AddMinutes(57);

        var corte = VentanaDeReconexion.Corte(null, inicio, ahora);

        Assert.Equal(ahora, corte);
        Assert.True(VentanaDeReconexion.Sigue(corte, ahora));
        Assert.True(VentanaDeReconexion.Sigue(corte, ahora.AddSeconds(59)));
    }

    [Fact]
    public void La_ventana_se_agota_un_minuto_despues_del_corte()
    {
        var corte = VentanaDeReconexion.Corte(null, T0, T0.AddHours(3));

        Assert.False(VentanaDeReconexion.Sigue(corte, T0.AddHours(3).AddSeconds(61)));
    }

    [Fact]
    public void Los_fallos_seguidos_comparten_la_misma_marca()
    {
        // Tres intentos que fallan enseguida: la ventana no se renueva con cada
        // uno, o se reintentaria para siempre.
        var primero = VentanaDeReconexion.Corte(null, T0, T0);
        var segundo = VentanaDeReconexion.Corte(primero, T0.AddSeconds(1), T0.AddSeconds(2));
        var tercero = VentanaDeReconexion.Corte(segundo, T0.AddSeconds(3), T0.AddSeconds(6));

        Assert.Equal(primero, tercero);
        Assert.False(VentanaDeReconexion.Sigue(tercero, T0.AddSeconds(61)));
    }

    [Fact]
    public void Un_intento_que_aguanto_cuenta_como_reconexion_conseguida()
    {
        // Se corto, volvio, aguanto un rato y se volvio a cortar. Eso NO es la
        // misma racha: la ventana empieza de cero.
        var racha = VentanaDeReconexion.Corte(null, T0, T0);
        var inicio = T0.AddSeconds(2);
        var ahora = inicio + VentanaDeReconexion.Aguanto + TimeSpan.FromSeconds(1);

        var nuevo = VentanaDeReconexion.Corte(racha, inicio, ahora);

        Assert.Equal(ahora, nuevo);
        Assert.True(VentanaDeReconexion.Sigue(nuevo, ahora.AddSeconds(30)));
    }

    [Fact]
    public void Un_intento_corto_no_reinicia_la_racha()
    {
        var racha = VentanaDeReconexion.Corte(null, T0, T0);
        var inicio = T0.AddSeconds(2);
        var ahora = inicio + VentanaDeReconexion.Aguanto - TimeSpan.FromSeconds(1);

        Assert.Equal(racha, VentanaDeReconexion.Corte(racha, inicio, ahora));
    }
}
