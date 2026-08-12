using System.Threading.Channels;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.Server.Remote;

/// <summary>Escritor del stream. Existe para poder probar la bomba de envio sin
/// levantar un servidor gRPC.</summary>
public interface IRemotePacketWriter
{
    Task WriteAsync(RemotePacket packet, CancellationToken cancellationToken);
}

/// <summary>
/// Un extremo conectado: sus dos colas y el UNICO hilo que escribe en su stream.
///
/// Que el escritor sea uno solo no es estilo: un `IServerStreamWriter` de gRPC
/// no admite dos escrituras a la vez, y hacerlo desde dos sitios rompe el stream
/// con un error que aparece lejos de donde se causo. Video y control tienen
/// colas separadas justamente para poder tener politicas distintas, pero las dos
/// desembocan aqui.
///
/// El control va SIEMPRE por delante del video, y se vacia entero antes de
/// tocar un frame y otra vez entre frame y frame. Un SessionClose o un
/// KeyframeRequest detras de cuatro frames de 1 MB llegaria medio segundo tarde,
/// y son justo los mensajes que no pueden esperar.
/// </summary>
public sealed class RelayConnection : IDisposable
{
    /// <summary>
    /// Acotada y con espera, no con descarte. Perder un KeyDown y entregar su
    /// KeyUp deja una tecla pegada en la maquina remota; perder un SessionClose
    /// deja la sesion colgada. Pero acotada igual: un viewer con un fallo que
    /// emita cien mil eventos no puede hacer crecer la RAM del servidor.
    /// </summary>
    private const int ControlCapacity = 256;

    /// <summary>Si el destino no traga en este tiempo, no esta vivo. Mejor
    /// cerrar con motivo que crecer sin freno.</summary>
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<RemotePacket> _control = Channel.CreateBounded<RemotePacket>(
        new BoundedChannelOptions(ControlCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    private readonly SemaphoreSlim _timbre = new(0);
    private readonly CancellationTokenSource _cierre = new();

    private ulong _secuencia;
    private int _controlHighWater;

    public RelayConnection(string sessionId, RemoteRole role)
    {
        SessionId = sessionId;
        Role = role;
    }

    public string SessionId { get; }
    public RemoteRole Role { get; }

    /// <summary>Solo el viewer recibe video; en el host esta y se queda vacia.</summary>
    public VideoRelayQueue Video { get; } = new();

    public long PacketsWritten { get; private set; }
    public long BytesWritten { get; private set; }
    public long ControlSent { get; private set; }
    public int ControlHighWater => _controlHighWater;

    /// <summary>Escrituras solapadas detectadas. Tiene que quedarse en cero: si
    /// sube, hay mas de un escritor y el stream se va a romper.</summary>
    public long ConcurrentWrites { get; private set; }

    /// <summary>Encola un mensaje de control. Espera si la cola esta llena --
    /// eso es la contrapresion -- y falla si el destino no la vacia.</summary>
    public async ValueTask SendControlAsync(RemotePacket packet, CancellationToken cancellationToken)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cierre.Token);
        limite.CancelAfter(ControlTimeout);

        try
        {
            await _control.Writer.WriteAsync(packet, limite.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_cierre.IsCancellationRequested)
        {
            throw new RelayBackpressureException(
                $"El extremo {Role} de la sesion {SessionId} no vacia su cola de control en {ControlTimeout.TotalSeconds:0} s.");
        }

        var pendientes = _control.Reader.Count;

        if (pendientes > _controlHighWater)
            _controlHighWater = pendientes;

        _timbre.Release();
    }

    /// <summary>No espera nunca: si el video no cabe, se descarta con la
    /// politica de la cola, que es lo correcto para video.</summary>
    public bool SendVideo(VideoFrameChunks frame)
    {
        if (!Video.TryEnqueue(frame))
            return false;

        _timbre.Release();
        return true;
    }

    /// <summary>Le dice al viewer que necesita VideoConfig y un IDR nuevos.</summary>
    public void RequireKeyframe() => Video.RequireKeyframe();

    public void SetVideoConfig(VideoConfig config) => Video.SetConfig(config);

    /// <summary>
    /// El unico escritor. Termina cuando se cancela o cuando se cierra la
    /// conexion y ya no queda nada pendiente.
    /// </summary>
    public async Task PumpAsync(IRemotePacketWriter writer, CancellationToken cancellationToken)
    {
        using var vinculado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (true)
        {
            try
            {
                await _timbre.WaitAsync(TimeSpan.FromMilliseconds(200), vinculado.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool quedaAlgo;

            try
            {
                quedaAlgo = await VaciarAsync(writer, vinculado.Token);
            }
            catch (OperationCanceledException)
            {
                // El stream se fue a mitad de una escritura. No hay nada que
                // vaciar y no es un error: la bomba termina, y el motivo real
                // del cierre lo pone quien cancelo.
                return;
            }

            // Al cerrar se drena lo que quede: un SessionClose ya encolado tiene
            // que salir, es el ultimo mensaje util de la sesion.
            if (_cierre.IsCancellationRequested && !quedaAlgo)
                return;
        }
    }

    private async Task<bool> VaciarAsync(IRemotePacketWriter writer, CancellationToken cancellationToken)
    {
        await EscribirControlAsync(writer, cancellationToken);

        while (Video.TryDequeue(out var config, out var frame))
        {
            if (config is not null)
                await EscribirAsync(writer, new RemotePacket { VideoConfig = config }, cancellationToken);

            foreach (var trozo in frame!.Chunks)
                await EscribirAsync(writer, new RemotePacket { VideoChunk = trozo }, cancellationToken);

            // Entre frame y frame, otra vez el control: no puede esperar detras
            // de megabytes de video.
            await EscribirControlAsync(writer, cancellationToken);
        }

        return _control.Reader.Count > 0 || Video.Depth > 0;
    }

    private async Task EscribirControlAsync(IRemotePacketWriter writer, CancellationToken cancellationToken)
    {
        while (_control.Reader.TryRead(out var paquete))
        {
            await EscribirAsync(writer, paquete, cancellationToken);
            ControlSent++;
        }
    }

    private int _escribiendo;

    private async Task EscribirAsync(IRemotePacketWriter writer, RemotePacket paquete, CancellationToken cancellationToken)
    {
        paquete.ProtocolVersion = RemoteSessionProtocol.Version;
        paquete.SessionId = SessionId;
        paquete.Sequence = ++_secuencia;
        paquete.TimestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        // Comprobacion barata de la invariante que sostiene todo esto. Si algun
        // dia alguien anade un segundo escritor, sale aqui y no como un stream
        // roto en la PC del tecnico.
        if (Interlocked.Exchange(ref _escribiendo, 1) == 1)
            ConcurrentWrites++;

        try
        {
            await writer.WriteAsync(paquete, cancellationToken);
            PacketsWritten++;
            BytesWritten += paquete.CalculateSize();
        }
        finally
        {
            Interlocked.Exchange(ref _escribiendo, 0);
        }
    }

    /// <summary>Deja de aceptar trabajo nuevo y pide a la bomba que termine tras
    /// vaciar lo pendiente.</summary>
    public void Complete()
    {
        _control.Writer.TryComplete();
        _cierre.Cancel();
        _timbre.Release();
    }

    public void Dispose()
    {
        _cierre.Cancel();
        _cierre.Dispose();
        _timbre.Dispose();
    }
}

public sealed class RelayBackpressureException(string message) : Exception(message);
