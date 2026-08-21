using DeviceHub.Server.Realtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// El registro de agentes conectados, que es tambien el detector de clones.
///
/// Aqui hubo un fallo que costo una PC de planta: INPUT-M2 se quedaba marcada
/// como clon y bloqueada hasta que un administrador la liberara, y volvia a
/// bloquearse un minuto despues. No era un clon -- era su PROPIA conexion
/// anterior, todavia sin enterrar del lado del servidor, ocupandole el sitio.
/// </summary>
public class ConnectionRegistryTests
{
    private const string Maquina = "d06942c6";

    [Fact]
    public void A_reconnection_takes_over_instead_of_being_refused()
    {
        // ESTO ES LO QUE FALLABA. A la PC se le va la red de golpe: el agente
        // reintenta en segundos, pero el servidor no se entera de que el stream
        // viejo murio hasta que TCP se rinde, que puede ser minutos. El que
        // volvia se encontraba su propio cadaver y se le llamaba clon.
        var registro = new ConnectionRegistry();

        var (primera, _) = registro.Registrar(Maquina);
        var (segunda, desalojos) = registro.Registrar(Maquina);

        Assert.NotSame(primera, segunda);
        Assert.Equal(1, desalojos);
        Assert.True(desalojos < ConnectionRegistry.DesalojosParaConflicto);
    }

    [Fact]
    public void The_displaced_stream_is_closed()
    {
        // O el agente viejo se quedaria colgado ocupando un hilo y una conexion,
        // y el servidor le seguiria escribiendo a nadie.
        var registro = new ConnectionRegistry();

        var (primera, _) = registro.Registrar(Maquina);
        registro.Registrar(Maquina);

        Assert.True(primera.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void The_new_connection_is_the_one_that_receives()
    {
        var registro = new ConnectionRegistry();

        registro.Registrar(Maquina);
        var (segunda, _) = registro.Registrar(Maquina);

        Assert.True(registro.TryPush(Maquina, new DeviceHub.Contracts.ServerMessage()));
        Assert.Equal(1, segunda.Reader.Count);
    }

    [Fact]
    public void A_late_close_does_not_evict_the_one_that_replaced_it()
    {
        // El cierre de la conexion vieja llega DESPUES de que la nueva ocupara su
        // sitio: es lo normal, porque lo que la cierra es justo el desalojo.
        var registro = new ConnectionRegistry();

        var (primera, _) = registro.Registrar(Maquina);
        var (segunda, _) = registro.Registrar(Maquina);

        registro.Unregister(Maquina, primera);

        Assert.Contains(Maquina, registro.ConnectedMachineIds);
        Assert.True(registro.TryPush(Maquina, new DeviceHub.Contracts.ServerMessage()));
        Assert.Equal(1, segunda.Reader.Count);
    }

    [Fact]
    public void Enough_takeovers_in_the_window_is_a_clone()
    {
        // Dos agentes con el mismo machineId no se turnan: se echan el uno al
        // otro sin parar, porque cada uno reconecta en cuanto el otro lo saca.
        var registro = new ConnectionRegistry();

        var desalojos = 0;

        // Una conexion mas que desalojos: la primera no echa a nadie.
        for (var i = 0; i <= ConnectionRegistry.DesalojosParaConflicto; i++)
            (_, desalojos) = registro.Registrar(Maquina);

        Assert.Equal(ConnectionRegistry.DesalojosParaConflicto, desalojos);
    }

    [Fact]
    public void The_same_takeovers_spread_out_are_just_a_bad_network()
    {
        // La diferencia entre un clon y una PC con mal wifi es el RITMO. Cuatro
        // reconexiones en un turno son mala red; cuatro en dos minutos son dos
        // agentes peleandose.
        var reloj = new FakeTimeProvider();
        var registro = new ConnectionRegistry(reloj);

        var desalojos = 0;

        for (var i = 0; i < 10; i++)
        {
            (_, desalojos) = registro.Registrar(Maquina);
            reloj.Advance(ConnectionRegistry.Ventana + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, desalojos);
    }

    [Fact]
    public void Machines_are_counted_apart()
    {
        var registro = new ConnectionRegistry();

        registro.Registrar("una");
        registro.Registrar("una");

        var (_, desalojos) = registro.Registrar("otra");

        Assert.Equal(0, desalojos);
    }
}
