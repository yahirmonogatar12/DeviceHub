using System.Collections.Concurrent;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.Server.Remote;

/// <summary>Foto de los contadores de una sesion, leidos todos a la vez.</summary>
public sealed record RemoteSessionSnapshot(
    string SessionId, RemoteSessionState State, bool HostConnected, bool ViewerConnected,
    long FramesReceived, long FramesForwarded, long BytesForwarded, long ControlForwarded,
    long FramesDropped, long DiscardedWaitingIdr, long StaleConfig, long DiscardedNoConfig,
    int QueueHighWater, int QueueDepth, int ControlHighWater, uint ConfigVersion)
{
    /// <summary>Una linea, pensada para reconciliar contra el host y el viewer
    /// durante la prueba de planta.</summary>
    public override string ToString()
        => $"sesion {SessionId} {State} host={(HostConnected ? "si" : "no")} viewer={(ViewerConnected ? "si" : "no")} " +
           $"config=v{ConfigVersion} recibidos={FramesReceived} reenviados={FramesForwarded} " +
           $"tirados={FramesDropped} esperandoIDR={DiscardedWaitingIdr} configVieja={StaleConfig} " +
           $"sinConfig={DiscardedNoConfig} cola={QueueDepth}/{QueueHighWater} control={ControlHighWater} " +
           $"bytes={BytesForwarded} controlReenviado={ControlForwarded}";
}

public enum JoinOutcome
{
    Joined,

    /// <summary>Ya hay alguien con ese papel. Se rechaza en vez de sustituirlo:
    /// una sesion que cambia de dueno en silencio es un secuestro.</summary>
    RoleTaken
}

/// <summary>
/// Una sesion de control remoto en el relay.
///
/// El servidor NO descodifica, NO recodifica, NO guarda video y NO toca el
/// SPS/PPS. Empareja por session_id, valida la forma y reenvia bytes. La unica
/// inteligencia que tiene es la de saber cuando un frame esta completo, porque
/// sin eso no puede descartar por frame -- y descartar por chunk corrompe la
/// imagen del tecnico.
///
/// Si alguna vez hace falta tocar el video aqui, es que algo se diseno mal.
/// </summary>
public sealed class RemoteSession(string id)
{
    private readonly Lock _puerta = new();

    /// <summary>
    /// Agrupa los chunks que llegan del host, UNO POR PANTALLA. No concatena:
    /// reenviar no necesita los bytes seguidos.
    ///
    /// Por pantalla porque el agrupador monta un frame a la vez y recuerda cual
    /// fue el ultimo que completo, para descartar como atrasado todo id menor o
    /// igual. Compartido entre monitores, el frame de una pantalla marcaba como
    /// atrasados los de la otra -- que numeran del mismo contador de sesion pero
    /// no salen en ese orden -- y esos frames se perdian aqui dentro.
    /// </summary>
    private readonly Dictionary<uint, VideoFrameCollector> _agrupadores = [];

    public string Id { get; } = id;

    public RelayConnection? Host { get; private set; }
    public RelayConnection? Viewer { get; private set; }

    public RemoteSessionState State { get; private set; } = RemoteSessionState.Created;

    /// <summary>Ultima configuracion que mando el host DE CADA PANTALLA, para
    /// darsela a un viewer que llegue tarde. Solo en memoria: el servidor no
    /// persiste nada del video, ni siquiera sus parametros.</summary>
    private readonly Dictionary<uint, VideoConfig> _configs = [];

    /// <summary>
    /// Ultima lista de pantallas que mando el host, para dársela a un viewer que
    /// llegue tarde.
    ///
    /// Es el mismo problema que la configuracion de video y se resuelve igual: el
    /// host manda la lista al abrir la captura, y si el viewer conecta despues
    /// -- que pasa siempre que el agente tarda en arrancar el host -- se perdia
    /// el unico mensaje y el desplegable se quedaba vacio toda la sesion.
    ///
    /// RustDesk hace lo mismo: guarda el SwitchDisplay en una instantanea y se la
    /// entrega a los suscriptores que llegan despues. Guardarlo en el RELAY es
    /// mejor que repetirlo desde el host cada pocos segundos, porque tambien
    /// cubre la reconexion del viewer y no gasta trafico cuando no hace falta.
    /// </summary>
    public DisplayList? Displays { get; private set; }

    public long FramesReceived { get; private set; }
    public long FramesForwarded { get; private set; }
    public long BytesForwarded { get; private set; }
    public long ControlForwarded { get; private set; }

    public string? CloseReason { get; private set; }

    public JoinOutcome TryJoin(RelayConnection conexion)
    {
        lock (_puerta)
        {
            if (conexion.Role == RemoteRole.Host)
            {
                if (Host is not null)
                    return JoinOutcome.RoleTaken;

                Host = conexion;
            }
            else
            {
                if (Viewer is not null)
                    return JoinOutcome.RoleTaken;

                Viewer = conexion;

                // Un viewer que acaba de llegar no tiene contexto. Si ya hay
                // configuracion, se le prepara para recibirla por delante del
                // proximo IDR; si no la hay, igualmente espera keyframe.
                // Todas las pantallas, no solo una: con dos monitores, darle la
                // configuracion de uno y dejar al otro sin ella deja media
                // sesion muda hasta el proximo cambio de config, que puede no
                // llegar nunca.
                if (_configs.Count > 0)
                {
                    foreach (var guardada in _configs.Values)
                        conexion.SetVideoConfig(guardada);
                }
                else
                {
                    conexion.RequireKeyframe();
                }

                // La lista de pantallas viaja fuera de la cola de video, asi que
                // se le manda aparte en cuanto entra.
                if (Displays is not null)
                    _pendienteDePantallas = conexion;
            }

            State = (Host, Viewer) switch
            {
                (not null, not null) => RemoteSessionState.Connected,
                (not null, null) => RemoteSessionState.WaitingForViewer,
                (null, not null) => RemoteSessionState.WaitingForHost,
                _ => RemoteSessionState.Created
            };

            return JoinOutcome.Joined;
        }
    }

    /// <summary>
    /// Saca a un extremo y dice a QUIEN hay que cerrarle la sesion.
    ///
    /// Los dos casos NO son simetricos, y tratarlos igual rompe la reconexion:
    ///
    ///   se va el HOST   -> al viewer no le va a llegar un frame mas: se cierra
    ///   se va el VIEWER -> el host sigue capturando y puede volver a conectarse
    ///                      otro; la sesion retrocede a WaitingForViewer
    ///
    /// Mandarle SessionClose al host porque el tecnico cerro su ventana era
    /// justo lo que impedia reconectar: el host se quedaba en una sesion que el
    /// relay ya daba por cerrada.
    /// </summary>
    public RelayConnection? Leave(RelayConnection conexion, SessionCloseReason motivo)
    {
        lock (_puerta)
        {
            if (ReferenceEquals(Host, conexion))
                Host = null;
            else if (ReferenceEquals(Viewer, conexion))
                Viewer = null;
            else
                return null;   // una conexion rechazada que nunca entro

            State = (Host, Viewer) switch
            {
                (not null, null) => RemoteSessionState.WaitingForViewer,
                (null, not null) => RemoteSessionState.Closing,
                _ => RemoteSessionState.Closed
            };

            if (Host is null)
            {
                CloseReason ??= motivo.ToString();
                return Viewer;   // sin host no hay video: al viewer se le cierra
            }

            return null;   // se fue el viewer y el host sigue: no se avisa a nadie
        }
    }

    public bool IsEmpty
    {
        get { lock (_puerta) return Host is null && Viewer is null; }
    }

    /// <summary>
    /// Los contadores del relay en un instante, todos juntos y con el session_id
    /// delante.
    ///
    /// Van juntos a proposito. Comparar el "frames enviados" del host con el
    /// "frames recibidos" del viewer no demuestra nada si cada numero se leyo en
    /// un momento distinto -- que es exactamente el error que se cometio al
    /// reportar la prueba en localhost: dos relojes independientes, cada uno
    /// contando desde el arranque de su proceso, y una diferencia de 16 frames
    /// que solo era el desfase entre dos impresiones.
    ///
    /// Con esto, la cadena se reconcilia en una sola linea:
    ///
    ///   host enviados >= recibidos >= reenviados >= reconstruidos por el viewer
    ///
    /// y cada escalon que baja tiene su contador que lo explica.
    /// </summary>
    public RemoteSessionSnapshot Snapshot()
    {
        lock (_puerta)
        {
            // Sumados de TODAS las pantallas: con dos monitores, mirar solo la
            // primera esconde justo el caso que costo encontrar -- una pantalla
            // sana y la otra descartando el 100 % de sus frames.
            var colas = Viewer?.Colas.ToList() ?? [];

            return new RemoteSessionSnapshot(
                Id, State, Host is not null, Viewer is not null,
                FramesReceived, FramesForwarded, BytesForwarded, ControlForwarded,
                colas.Sum(c => c.FramesDropped), colas.Sum(c => c.DiscardedWaitingIdr),
                colas.Sum(c => c.StaleConfig), colas.Sum(c => c.DiscardedNoConfig),
                colas.Count == 0 ? 0 : colas.Max(c => c.HighWater),
                colas.Sum(c => c.Depth),
                Viewer?.ControlHighWater ?? 0,
                _configs.GetValueOrDefault(0u)?.ConfigVersion ?? 0);
        }
    }

    /// <summary>Lo que manda la PC controlada: video y cursor.</summary>
    public async ValueTask FromHostAsync(RemotePacket paquete, CancellationToken cancellationToken)
    {
        switch (paquete.PayloadCase)
        {
            case RemotePacket.PayloadOneofCase.VideoConfig:
                lock (_puerta)
                    _configs[paquete.VideoConfig.DisplayId] = paquete.VideoConfig;

                Viewer?.SetVideoConfig(paquete.VideoConfig);
                break;

            case RemotePacket.PayloadOneofCase.Displays:
                lock (_puerta)
                    Displays = paquete.Displays;

                await ReenviarControlAsync(Viewer, paquete, cancellationToken);
                break;

            case RemotePacket.PayloadOneofCase.VideoChunk:
                VideoFrameCollector agrupador;

                lock (_puerta)
                {
                    if (!_agrupadores.TryGetValue(paquete.VideoChunk.DisplayId, out agrupador!))
                        _agrupadores[paquete.VideoChunk.DisplayId] = agrupador = new VideoFrameCollector();
                }

                if (agrupador.TryAdd(paquete.VideoChunk, out var frame))
                {
                    FramesReceived++;

                    if (Viewer is { } viewer && viewer.SendVideo(frame!))
                    {
                        FramesForwarded++;
                        BytesForwarded += frame!.PayloadBytes;
                    }
                }

                await PedirIdrSiHaceFaltaAsync(cancellationToken);
                break;

            default:
                await ReenviarControlAsync(Viewer, paquete, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// El viewer que acaba de entrar y todavia no ha recibido la lista guardada.
    ///
    /// No se le manda dentro de TryJoin porque ahi se sostiene el candado de la
    /// sesion, y escribir en un socket con un candado tomado es como se cuelgan
    /// las dos mitades a la vez.
    /// </summary>
    private RelayConnection? _pendienteDePantallas;

    /// <summary>Entrega lo guardado al viewer recien llegado. Lo llama el relay
    /// cuando ya no sostiene ningun candado.</summary>
    public async ValueTask PonerAlDiaAsync(CancellationToken cancellationToken)
    {
        RelayConnection? destino;
        DisplayList? pantallas;

        lock (_puerta)
        {
            destino = _pendienteDePantallas;
            pantallas = Displays;
            _pendienteDePantallas = null;
        }

        // El IDR primero: un viewer que acaba de entrar no tiene con que
        // descodificar nada, y la lista de pantallas puede esperar un mensaje.
        await PedirIdrSiHaceFaltaAsync(cancellationToken);

        if (destino is null || pantallas is null)
            return;

        await destino.SendControlAsync(new RemotePacket { Displays = pantallas }, cancellationToken);
    }

    /// <summary>
    /// Le pide al host un IDR de las pantallas cuya cola se acaba de vaciar.
    ///
    /// EL RELAY PIDE, no solo el visor. Cuando la congestion tira la cola, el
    /// visor no se entera de nada -- para el simplemente dejan de llegar frames
    /// -- asi que quien tiene que pedirlo es quien descarto. Es lo que hace
    /// RustDesk desde su cliente al desbordar la suya: force_push que devuelve
    /// algo, refresh_video inmediato.
    /// </summary>
    private async ValueTask PedirIdrSiHaceFaltaAsync(CancellationToken cancellationToken)
    {
        if (Host is not { } host || Viewer is not { } viewer)
            return;

        foreach (var pantalla in viewer.PantallasQuePidenIdr())
        {
            IdrRequested++;

            await host.SendControlAsync(new RemotePacket
            {
                KeyframeRequest = new KeyframeRequest
                {
                    Reason = KeyframeReason.Congestion,
                    DisplayId = pantalla
                }
            }, cancellationToken);
        }
    }

    /// <summary>Cuantos IDR ha pedido el relay por su cuenta. Si esto sube sin
    /// parar, el problema no es el codificador: es que no cabe.</summary>
    public long IdrRequested { get; private set; }

    /// <summary>Lo que manda la PC del tecnico: entrada y peticiones.</summary>
    public ValueTask FromViewerAsync(RemotePacket paquete, CancellationToken cancellationToken)
        => ReenviarControlAsync(Host, paquete, cancellationToken);

    private async ValueTask ReenviarControlAsync(
        RelayConnection? destino, RemotePacket paquete, CancellationToken cancellationToken)
    {
        if (destino is null)
            return;   // el otro extremo aun no llego o ya se fue

        await destino.SendControlAsync(paquete, cancellationToken);
        ControlForwarded++;
    }
}

/// <summary>
/// Las sesiones vivas. SOLO EN MEMORIA: una sesion de control remoto no
/// sobrevive a un reinicio del servidor, y fingir lo contrario dejaria filas
/// colgadas que nadie cierra.
/// </summary>
public sealed class RemoteSessionRegistry
{
    private readonly ConcurrentDictionary<string, RemoteSession> _sesiones = new();

    public int Count => _sesiones.Count;

    public IReadOnlyCollection<string> SessionIds => [.. _sesiones.Keys];

    public RemoteSession GetOrCreate(string sessionId)
        => _sesiones.GetOrAdd(sessionId, id => new RemoteSession(id));

    public RemoteSession? Find(string sessionId)
        => _sesiones.TryGetValue(sessionId, out var sesion) ? sesion : null;

    public IReadOnlyList<RemoteSessionSnapshot> Snapshot()
        => [.. _sesiones.Values.Select(s => s.Snapshot())];

    /// <summary>Quita la sesion si ya no queda nadie. Sin esto, cada sesion del
    /// dia deja una entrada muerta en el diccionario.</summary>
    public void DropIfEmpty(RemoteSession sesion)
    {
        if (sesion.IsEmpty)
            _sesiones.TryRemove(new KeyValuePair<string, RemoteSession>(sesion.Id, sesion));
    }
}
