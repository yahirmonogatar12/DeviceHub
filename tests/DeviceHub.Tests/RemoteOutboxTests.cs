using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteViewer.Input;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// La cola de salida del visor.
///
/// Aqui han fallado tres cosas seguidas -- coalescencia que no existia,
/// recursion al saturarse, entrada vieja reproducida tras reconectar -- y
/// ninguna se podia cubrir mientras la logica vivia dentro de una Window: el
/// proyecto de pruebas tendria que activar UseWPF entero.
/// </summary>
public class RemoteOutboxTests
{
    private const string Sesion = "s";

    private static RemotePacket Mover(double x, double y)
        => new() { Input = new InputEvent { MouseMove = new MouseMove { X = x, Y = y } } };

    private static RemotePacket Tecla(uint vk, bool pulsada)
        => new() { Input = new InputEvent { Key = new KeyEvent { VirtualKey = vk, Pressed = pulsada } } };

    private static List<RemotePacket> Vaciar(BuzonDeSalida buzon)
    {
        var todo = new List<RemotePacket>();

        while (buzon.TryTomar(Sesion, out var paquete))
            todo.Add(paquete);

        return todo;
    }

    [Fact]
    public void A_thousand_moves_leave_one()
    {
        var buzon = new BuzonDeSalida();

        for (var i = 0; i < 1000; i++)
            buzon.Encolar(Mover(i / 1000.0, 0.5));

        var salieron = Vaciar(buzon);

        // Uno, y el ULTIMO: las coordenadas son absolutas, asi que reproducir el
        // caminito no aporta nada. Y son 999 huecos que no se le quitan a un
        // KeyUp.
        Assert.Single(salieron);
        Assert.Equal(0.999, salieron[0].Input.MouseMove.X, precision: 5);
        Assert.Equal(999, buzon.Fundidos);
    }

    [Fact]
    public void Keys_are_never_coalesced()
    {
        var buzon = new BuzonDeSalida();

        // Ctrl abajo, A abajo, A arriba, Ctrl arriba. Fundir cualquiera de los
        // cuatro cambia lo que la PC remota entiende.
        buzon.Encolar(Tecla(0x11, true));
        buzon.Encolar(Tecla(0x41, true));
        buzon.Encolar(Tecla(0x41, false));
        buzon.Encolar(Tecla(0x11, false));

        var salieron = Vaciar(buzon);

        Assert.Equal(4, salieron.Count);
        Assert.Equal([0x11u, 0x41u, 0x41u, 0x11u], salieron.Select(p => p.Input.Key.VirtualKey));
        Assert.Equal([true, true, false, false], salieron.Select(p => p.Input.Key.Pressed));
    }

    [Fact]
    public void A_full_queue_does_not_recurse_and_asks_for_a_release()
    {
        // Antes, al fallar TryWrite se pedia el rescate por la MISMA cola: eso
        // volvia a fallar, que volvia a pedirlo... hasta desbordar la pila.
        var buzon = new BuzonDeSalida(capacidad: 4);

        for (var i = 0; i < 50; i++)
            buzon.Encolar(Tecla(0x11, i % 2 == 0));

        Assert.True(buzon.Perdidos > 0);

        // Y lo primero que sale es soltar lo hundido: puede haberse caido un
        // KeyUp por el camino.
        Assert.True(buzon.TryTomar(Sesion, out var primero));
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, primero.HostAction.Kind);
    }

    [Fact]
    public void The_release_jumps_the_queue()
    {
        var buzon = new BuzonDeSalida();

        buzon.Encolar(Tecla(0x41, true));
        buzon.Encolar(Mover(0.5, 0.5));
        buzon.PedirSoltar();

        var salieron = Vaciar(buzon);

        // Rescate, movimiento, y despues lo demas. Lo que viene detras puede ser
        // mas entrada sobre un estado que todavia esta sucio.
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, salieron[0].HostAction.Kind);
        Assert.Equal(InputEvent.EventOneofCase.MouseMove, salieron[1].Input.EventCase);
        Assert.Equal(InputEvent.EventOneofCase.Key, salieron[2].Input.EventCase);
    }

    [Fact]
    public void Only_one_release_is_scheduled_no_matter_how_many_ask()
    {
        var buzon = new BuzonDeSalida();

        for (var i = 0; i < 20; i++)
            buzon.PedirSoltar();

        var salieron = Vaciar(buzon);

        Assert.Single(salieron);
    }

    [Fact]
    public void A_new_connection_does_not_replay_the_old_input()
    {
        var buzon = new BuzonDeSalida();

        // Se cayo la red con esto dentro, sin salir.
        buzon.Encolar(Tecla(0x41, true));
        buzon.Encolar(Mover(0.1, 0.1));
        buzon.Encolar(Tecla(0x41, false));

        buzon.Reiniciar();

        var salieron = Vaciar(buzon);

        // Solo el rescate. Aplicar en la PC remota clics y teclas de hace medio
        // minuto, contra una pantalla que ya no es la que el tecnico mira, es
        // peor que perderlos: el relay nunca los recibio y nadie los espera.
        Assert.Single(salieron);
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, salieron[0].HostAction.Kind);
    }
}
