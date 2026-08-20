using DeviceHub.RemoteHost.Encode;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Separar la red de la cola. Es la correccion que hacia falta para que el
/// control de FPS dejara de estrangularse solo: reaccionaba al RTT crudo, y el
/// RTT crudo lo inflaban nuestras propias colas.
/// </summary>
public class RemoteDelayTests
{
    [Fact]
    public void Without_samples_it_says_it_does_not_know()
    {
        var medidor = new MedidorRetraso();

        // Negativo y no cero: cero significaria "la red esta perfecta" y haria
        // que el controlador subiera los FPS antes de haber medido nada.
        Assert.True(medidor.Base < 0);
        Assert.True(medidor.Encolado < 0);
    }

    [Fact]
    public void The_floor_of_the_window_is_the_network()
    {
        var medidor = new MedidorRetraso();

        foreach (var muestra in new double[] { 40, 32, 95, 31, 120 })
            medidor.Anotar(muestra);

        // 31 es lo que costo la red cuando el cable estaba vacio.
        Assert.Equal(31, medidor.Base);

        // Y la ultima medida, 120, son 31 de red y 89 de cola. Solo sobre esos
        // 89 sirve de algo bajar el ritmo.
        Assert.Equal(89, medidor.Encolado);
    }

    [Fact]
    public void A_link_with_a_high_floor_is_not_congested()
    {
        var medidor = new MedidorRetraso();

        // Un enlace lento pero ESTABLE: 200 ms siempre. Con el RTT crudo esto
        // caia en la banda de "bajar los FPS" para siempre, sin que hubiera
        // nada que corregir.
        for (var i = 0; i < 10; i++)
            medidor.Anotar(200);

        Assert.Equal(200, medidor.Base);
        Assert.Equal(0, medidor.Encolado);

        // Y el controlador ya no lo castiga. Con el RTT crudo, 200 ms caia en
        // la banda de "bajar los FPS" para siempre; con la senal buena ve que no
        // hay cola y deja subir.
        Assert.True(ControlFps.Siguiente(20, medidor.Encolado) >= 20);

        // Que es justo lo contrario de lo que hacia antes con el mismo enlace.
        Assert.True(ControlFps.Siguiente(20, rttMs: 200) < 20);
    }

    [Fact]
    public void The_window_forgets()
    {
        var medidor = new MedidorRetraso();

        // Una racha buena antigua no puede quedarse de suelo para siempre: si
        // la red empeoro de verdad, el suelo tiene que subir con ella.
        medidor.Anotar(5);

        for (var i = 0; i < MedidorRetraso.Ventana; i++)
            medidor.Anotar(80);

        Assert.Equal(80, medidor.Base);
        Assert.Equal(0, medidor.Encolado);
    }
}
