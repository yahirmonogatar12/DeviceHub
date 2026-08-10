using System.Collections.Concurrent;
using System.Threading.Channels;
using DeviceHub.Contracts;

namespace DeviceHub.Server.Realtime;

/// <summary>
/// Streams de agente vivos, uno por machineId.
///
/// Esto es el segundo detector de clonacion, y el que de verdad sostiene todo:
/// dos agentes con el mismo machineId conectados a la vez es imposible de
/// explicar con hardware legitimo. No depende del SMBIOS, ni del serial, ni de
/// la confianza del fingerprint -- funciona incluso con confianza LOW.
///
/// La cola de salida por conexion tambien es el canal servidor -> agente:
/// renombrar una maquina o rotar pines es un TryPush, sin transporte nuevo.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Channel<ServerMessage>> _connections = new();

    public IReadOnlyCollection<string> ConnectedMachineIds => [.. _connections.Keys];

    /// <summary>Devuelve null si ya hay un stream activo para esa maquina.</summary>
    public Channel<ServerMessage>? TryRegister(string machineId)
    {
        var channel = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        return _connections.TryAdd(machineId, channel) ? channel : null;
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

    public bool TryPush(string machineId, ServerMessage message)
        => _connections.TryGetValue(machineId, out var channel) && channel.Writer.TryWrite(message);
}
