using System.Collections.Concurrent;
using System.Threading.Channels;
using DeviceHub.Contracts;

namespace DeviceHub.Server.Realtime;

/// <summary>
/// Empuja cambios de maquina a los dashboards suscritos (WatchMachines), para
/// que el DataGrid no haga polling.
///
/// Cola acotada con DropOldest: un dashboard lento o colgado no puede hacer
/// crecer la memoria del servidor, solo pierde cuadros intermedios -- que para
/// un estado "ultimo valor gana" no importa.
/// </summary>
public sealed class MachineBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<MachineSummary>> _watchers = new();

    public (Guid Id, ChannelReader<MachineSummary> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<MachineSummary>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        _watchers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_watchers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    public void Publish(MachineSummary summary)
    {
        foreach (var watcher in _watchers.Values)
            watcher.Writer.TryWrite(summary);
    }
}
