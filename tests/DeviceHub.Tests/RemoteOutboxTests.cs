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

    private static RemotePacket Acuse(ulong frame)
        => new() { VideoAck = new VideoAck { FrameId = frame } };

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
    public void A_burst_of_moves_does_not_eat_the_queue()
    {
        // ESTO NO LO COGIA A_thousand_moves_leave_one: ese solo mira lo que sale
        // de TryTomar, y lo que fallaba era cuanto ESPACIO se habia comido por
        // el camino. Cada movimiento metia su propio marcador para despertar al
        // hilo, asi que mil movimientos dejaban un movimiento... y 512
        // marcadores llenando el canal. El KeyUp que llegara detras no cabia.
        var buzon = new BuzonDeSalida(capacidad: 512);

        for (var i = 0; i < 1000; i++)
            buzon.Encolar(Mover(i / 1000.0, 0.5));

        for (var i = 0; i < 500; i++)
            buzon.Encolar(Tecla(0x41, i % 2 == 0));

        Assert.Equal(0, buzon.Perdidos);

        var teclas = Vaciar(buzon).Count(p => p.Input?.EventCase == InputEvent.EventOneofCase.Key);

        Assert.Equal(500, teclas);
    }

    [Fact]
    public void Acks_are_not_input()
    {
        // La cifra de la barra existe para responder "se ve pero no se puede
        // controlar: cual de las dos mitades falla". Con los acuses dentro no
        // podia: son dos por frame, o sea 226 000 en una hora, y los 5 000
        // eventos del tecnico se perdian ahi dentro. Subia sola con el raton
        // quieto.
        var buzon = new BuzonDeSalida();

        for (var i = 0; i < 100; i++)
            buzon.Encolar(Acuse((ulong)i));

        buzon.Encolar(Tecla(0x41, true));
        buzon.Encolar(Mover(0.5, 0.5));

        Assert.Equal(2, buzon.Entrada);

        // 101 y no 102: el movimiento se cuenta como enviado cuando SALE, que es
        // lo unico honesto -- hasta entonces todavia puede fundirse con otro.
        Assert.Equal(101, buzon.Enviados);
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
    public void The_release_jumps_the_queue_and_buries_what_was_behind_it()
    {
        var buzon = new BuzonDeSalida();

        buzon.Encolar(Tecla(0x11, true));
        buzon.Encolar(Mover(0.5, 0.5));
        buzon.PedirSoltar();

        var salieron = Vaciar(buzon);

        // SOLO el rescate. Antes salia el rescate y DETRAS el Ctrl DOWN viejo,
        // asi que la tecla volvia a quedarse hundida justo despues de haberla
        // despegado -- y su KeyUp ocurrio fuera del visor, con el foco perdido,
        // asi que no iba a llegar jamas.
        Assert.Single(salieron);
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, salieron[0].HostAction.Kind);
        Assert.Equal(2, buzon.Caducados);
    }

    [Fact]
    public void Input_after_the_release_is_normal_input()
    {
        // La barrera es un instante, no un modo: lo que el tecnico teclee al
        // volver a la ventana tiene que pasar.
        var buzon = new BuzonDeSalida();

        buzon.Encolar(Tecla(0x11, true));
        buzon.PedirSoltar();
        buzon.Encolar(Tecla(0x41, true));

        var salieron = Vaciar(buzon);

        Assert.Equal(2, salieron.Count);
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, salieron[0].HostAction.Kind);
        Assert.Equal(0x41u, salieron[1].Input.Key.VirtualKey);
    }

    [Fact]
    public void The_release_does_not_touch_anything_that_is_not_input()
    {
        // Un acuse, el portapapeles o un trozo de archivo no dependen de que
        // haya teclas hundidas ni las dejan. Tirarlos por perder el foco de la
        // ventana romperia una transferencia a medias.
        var buzon = new BuzonDeSalida();

        buzon.Encolar(Acuse(7));
        buzon.Encolar(Tecla(0x11, true));
        buzon.PedirSoltar();

        var salieron = Vaciar(buzon);

        Assert.Equal(2, salieron.Count);
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, salieron[0].HostAction.Kind);
        Assert.Equal(7ul, salieron[1].VideoAck.FrameId);
        Assert.Equal(1, buzon.Caducados);
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

    [Fact]
    public async Task Asking_for_a_release_wakes_the_sender()
    {
        // ESTO NO LO COGIAN LAS OTRAS PRUEBAS porque llaman a TryTomar
        // directamente, y el hilo de envio real espera en EsperarAsync.
        //
        // PedirSoltar solo levantaba una bandera, y EsperarAsync solo mira la
        // cola: el rescate se quedaba dormido hasta el siguiente latido, o sea
        // hasta un segundo. Y se notaba justo en Reiniciar, que deja la cola
        // vacia antes de pedirlo.
        var buzon = new BuzonDeSalida();

        buzon.PedirSoltar();

        var espera = buzon.EsperarAsync(CancellationToken.None).AsTask();

        Assert.True(espera.IsCompleted, "PedirSoltar tiene que despertar al hilo de envio");
        Assert.True(await espera);
    }

    [Fact]
    public async Task Restarting_also_wakes_the_sender()
    {
        var buzon = new BuzonDeSalida();

        buzon.Encolar(Tecla(0x41, true));
        buzon.Reiniciar();

        var espera = buzon.EsperarAsync(CancellationToken.None).AsTask();

        Assert.True(espera.IsCompleted);
        Assert.True(await espera);

        // Y lo que sale es el rescate, no lo viejo.
        Assert.True(buzon.TryTomar(Sesion, out var primero));
        Assert.Equal(HostAction.Types.Kind.HostActionReleaseInput, primero.HostAction.Kind);
    }
}
