using System.Collections.Concurrent;
using System.Threading.Channels;
using DeviceHub.Contracts;

namespace DeviceHub.Server.Realtime;

/// <summary>
/// Streams de agente vivos, uno por machineId.
///
/// Esto es el segundo detector de clonacion, y el que de verdad sostiene todo:
/// dos agentes con el mismo machineId CONECTADOS A LA VEZ es imposible de
/// explicar con hardware legitimo. No depende del SMBIOS, ni del serial, ni de
/// la confianza del fingerprint -- funciona incluso con confianza LOW.
///
/// La cola de salida por conexion tambien es el canal servidor -> agente:
/// renombrar una maquina o rotar pines es un TryPush, sin transporte nuevo.
///
/// EL PROBLEMA ESTABA EN "A LA VEZ". Antes, la conexion nueva se rechazaba en
/// cuanto hubiera una entrada en el diccionario -- y ahi dentro no solo hay
/// streams vivos: hay CADAVERES. Cuando a una PC se le va la red de golpe (se
/// apaga, cambia de subred, se cae el wifi) el servidor no se entera hasta que
/// TCP se rinde, que puede ser minutos. El agente, que reintenta en segundos,
/// vuelve mucho antes -- y se encontraba su propio cadaver ocupando el sitio.
///
/// El resultado era una PC marcada como clon y bloqueada hasta que un
/// administrador la desbloqueara a mano, para volver a bloquearse en el
/// siguiente microcorte. Le paso a INPUT-M2, que ademas iba cambiando de subred:
/// 192.168.0.220, 192.168.1.23, 192.168.2.118.
///
/// Ahora la conexion nueva DESALOJA a la vieja, que es lo unico que tiene
/// sentido -- si las dos fueran la misma PC, la que sabe cual esta viva es la
/// que acaba de hablar. Y el detector de clones no se pierde: se mide la
/// FRECUENCIA. Una reconexion desaloja una vez; dos agentes de verdad peleando
/// por el mismo machineId se desalojan sin parar, porque cada uno reconecta en
/// cuanto el otro lo echa.
/// </summary>
public sealed class ConnectionRegistry(TimeProvider? reloj = null)
{
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Channel<ServerMessage>> _connections = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _desalojos = [];
    private readonly Lock _puerta = new();

    /// <summary>En cuanto tiempo se cuentan los desalojos.</summary>
    public static readonly TimeSpan Ventana = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cuantos desalojos seguidos dejan de ser mala suerte.
    ///
    /// Una reconexion normal desaloja UNA vez y despues se queda. Una red mala
    /// puede desalojar varias en un dia, pero no cuatro en dos minutos. Dos
    /// agentes con el mismo machineId si: cada uno reconecta en cuanto el otro
    /// lo echa, y eso no para nunca.
    /// </summary>
    public const int DesalojosParaConflicto = 4;

    public IReadOnlyCollection<string> ConnectedMachineIds => [.. _connections.Keys];

    /// <summary>
    /// Registra el stream nuevo y desaloja al anterior si lo habia.
    ///
    /// Devuelve el canal y CUANTOS desalojos lleva esa maquina en la ventana.
    /// Quien decide que hacer con ese numero es el servicio: aqui no se marca
    /// nada ni se rechaza a nadie.
    /// </summary>
    public (Channel<ServerMessage> Canal, int Desalojos) Registrar(string machineId)
    {
        var canal = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        lock (_puerta)
        {
            var desalojos = 0;

            if (_connections.TryGetValue(machineId, out var anterior))
            {
                // Cerrar el canal termina la bomba de la conexion vieja, y con
                // ella su stream: si estaba viva se entera ahora, y si era un
                // cadaver no le importa.
                anterior.Writer.TryComplete();
                desalojos = Anotar(machineId);
            }

            _connections[machineId] = canal;

            return (canal, desalojos);
        }
    }

    /// <summary>
    /// Solo quita la entrada si sigue siendo ESTA conexion: si una reconexion ya
    /// ocupo el lugar, el cierre tardio de la vieja no debe desalojarla.
    /// </summary>
    public void Unregister(string machineId, Channel<ServerMessage> channel)
    {
        _connections.TryRemove(new KeyValuePair<string, Channel<ServerMessage>>(machineId, channel));
        channel.Writer.TryComplete();
    }

    /// <summary>
    /// Echa a una maquina AHORA. Cerrar su canal termina la bomba y con ella su
    /// stream.
    ///
    /// Hace falta porque la autenticacion se comprueba al CONECTAR: quitarle el
    /// token a una PC que ya esta dentro no la saca, solo le impide volver. Sin
    /// esto, una PC dada de baja seguiria mandando metricas hasta que se cayera
    /// sola o alguien la apagara.
    /// </summary>
    public void Close(string machineId)
    {
        lock (_puerta)
        {
            if (_connections.TryRemove(machineId, out var canal))
                canal.Writer.TryComplete();
        }
    }

    public bool TryPush(string machineId, ServerMessage message)
        => _connections.TryGetValue(machineId, out var channel) && channel.Writer.TryWrite(message);

    /// <summary>Apunta un desalojo y devuelve cuantos quedan dentro de la
    /// ventana. Se llama con el candado tomado.</summary>
    private int Anotar(string machineId)
    {
        var ahora = _reloj.GetUtcNow();

        if (!_desalojos.TryGetValue(machineId, out var marcas))
            _desalojos[machineId] = marcas = new Queue<DateTimeOffset>();

        while (marcas.Count > 0 && ahora - marcas.Peek() > Ventana)
            marcas.Dequeue();

        marcas.Enqueue(ahora);

        return marcas.Count;
    }
}
