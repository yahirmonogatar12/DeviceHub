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

    /// <summary>
    /// El SONIDO, en su propia cola y con su propia politica. Tercera de tres.
    ///
    /// NO puede ir por la de control. Son unos cuarenta y ocho paquetes por
    /// segundo, y esa cola espera cuando se llena: un visor que se atasque
    /// llenaria los 256 huecos en cinco segundos y bloquearia con ellos el
    /// TECLADO y el RATON, que comparten esa cola. El sonido no puede tener
    /// rehen a la entrada.
    ///
    /// Y NO puede ir por la de video, que descarta por frame completo. Aqui no
    /// hay frames: cada paquete lleva veintiun milisegundos de sonido distintos
    /// y ninguno sustituye a otro.
    ///
    /// Asi que: acotada, y DESCARTANDO EL MAS VIEJO. Un paquete de sonido
    /// atrasado no sirve -- se reproduciria despues de lo que ya se oyo -- y
    /// tirarlo cuesta un chasquido, mientras esperar cuesta la sesion entera.
    /// Cien paquetes son unos dos segundos: mucho mas de lo que cualquier
    /// atasco razonable necesita, y un techo claro si algo va mal.
    /// </summary>
    private const int AudioCapacity = 100;

    private readonly Channel<RemotePacket> _audio = Channel.CreateBounded<RemotePacket>(
        new BoundedChannelOptions(AudioCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private int _audioDescartado;

    /// <summary>Paquetes de sonido que se tiraron por cola llena. Si sube, el
    /// visor no esta consumiendo y se va a oir.</summary>
    public int AudioDescartado => _audioDescartado;

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

    /// <summary>
    /// UNA COLA POR PANTALLA. Solo el viewer recibe video; en el host se queda
    /// vacia.
    ///
    /// Compartir una sola cola entre monitores no era una simplificacion, era un
    /// filtro: la cola guarda UNA VideoConfig, y `TryEnqueue` tira como
    /// `StaleConfig` todo frame cuya version no sea la suya. Con dos pantallas,
    /// cada VideoConfig sustituia a la de la otra -- y ademas vaciaba la cola y
    /// rearmaba AwaitingKeyframe -- asi que la pantalla que no hubiera mandado la
    /// ultima configuracion perdia el 100 % de sus frames AQUI, en el servidor,
    /// sin salir nunca al cable.
    ///
    /// El host mandaba bien. El visor habria pintado bien. Se congelaba en medio.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, VideoRelayQueue> _video = new();

    private VideoRelayQueue Cola(uint pantalla) => _video.GetOrAdd(pantalla, _ => new VideoRelayQueue());

    /// <summary>La cola de la pantalla principal. Es lo que mira un extremo de un
    /// solo monitor, que es el caso normal.</summary>
    public VideoRelayQueue Video => Cola(0);

    /// <summary>Todas las colas vivas, para sumar sus contadores.</summary>
    public IEnumerable<VideoRelayQueue> Colas => _video.Values;

    /// <summary>Pantallas cuya cola se quedo esperando un IDR. Consume la
    /// peticion: quien lo llama tiene que mandarla.</summary>
    public List<uint> PantallasQuePidenIdr()
    {
        List<uint>? pendientes = null;

        foreach (var (pantalla, cola) in _video)
        {
            if (cola.TomarPeticionDeIdr())
                (pendientes ??= []).Add(pantalla);
        }

        return pendientes ?? [];
    }

    public long PacketsWritten { get; private set; }
    public long BytesWritten { get; private set; }
    public long ControlSent { get; private set; }
    public int ControlHighWater => _controlHighWater;

    /// <summary>Mensajes de control esperando salir. Lo critico -- teclas,
    /// botones, keyframes, cierre -- no se descarta nunca, asi que esta cifra
    /// tiene que coincidir con lo que se encolo y no ha salido.</summary>
    public int PendingControl => _control.Reader.Count;

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
        // La pantalla la dice el propio frame: todos sus chunks la llevan y el
        // agrupador ya comprobo que son del mismo frame.
        if (!Cola(frame.Chunks[0].DisplayId).TryEnqueue(frame))
            return false;

        _timbre.Release();
        return true;
    }

    /// <summary>Le dice al viewer que necesita VideoConfig y un IDR nuevos. De
    /// TODAS las pantallas: quien lo pide acaba de entrar o de perder
    /// sincronia, y eso vale para el escritorio entero.</summary>
    public void RequireKeyframe()
    {
        // Al menos la principal, aunque todavia no haya llegado ningun frame:
        // asi un viewer recien entrado queda esperando IDR y no se le cuela un
        // P-frame suelto.
        Cola(0).RequireKeyframe();

        foreach (var cola in _video.Values)
            cola.RequireKeyframe();
    }

    public void SetVideoConfig(VideoConfig config) => Cola(config.DisplayId).SetConfig(config);

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

    /// <summary>
    /// Encola sonido. Nunca bloquea y nunca falla: si la cola esta llena, el
    /// canal tira el mas viejo. Devuelve false solo si la conexion ya cerro.
    /// </summary>
    public bool SendAudio(RemotePacket paquete)
    {
        if (_cierre.IsCancellationRequested)
            return false;

        // Antes de escribir, para saber si esta a punto de tirar algo: el canal
        // no avisa de lo que descarta.
        if (_audio.Reader.Count >= AudioCapacity)
            Interlocked.Increment(ref _audioDescartado);

        if (!_audio.Writer.TryWrite(paquete))
            return false;

        _timbre.Release();
        return true;
    }

    private async Task<bool> VaciarAsync(IRemotePacketWriter writer, CancellationToken cancellationToken)
    {
        await EscribirControlAsync(writer, cancellationToken);

        // EL SONIDO ANTES QUE EL VIDEO. Un frame tarde se ve como una imagen
        // vieja; un paquete de sonido tarde se oye como un corte, y el oido lo
        // nota mucho antes que el ojo. Son 250 bytes contra decenas de miles,
        // asi que adelantarlo no retrasa el video de forma medible.
        while (_audio.Reader.TryRead(out var sonido))
            await writer.WriteAsync(sonido, cancellationToken);

        // Por turnos entre pantallas, un frame de cada una por vuelta. Vaciar una
        // cola entera antes de mirar la siguiente dejaria al segundo monitor
        // detras de todo lo del primero cada vez.
        bool salioAlguno;

        do
        {
            salioAlguno = false;

            foreach (var cola in _video.Values)
            {
                if (!cola.TryDequeue(out var config, out var frame))
                    continue;

                salioAlguno = true;

                if (config is not null)
                    await EscribirAsync(writer, new RemotePacket { VideoConfig = config }, cancellationToken);

                foreach (var trozo in frame!.Chunks)
                    await EscribirAsync(writer, new RemotePacket { VideoChunk = trozo }, cancellationToken);

                // Entre frame y frame, otra vez el control: no puede esperar
                // detras de megabytes de video.
                await EscribirControlAsync(writer, cancellationToken);
            }
        }
        while (salioAlguno);

        return _control.Reader.Count > 0 || _video.Values.Any(c => c.Depth > 0);
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
