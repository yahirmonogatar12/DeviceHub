using System.Collections.Concurrent;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.Server.Remote;

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

    /// <summary>Agrupa los chunks que llegan del host. No concatena: reenviar no
    /// necesita los bytes seguidos.</summary>
    private readonly VideoFrameCollector _agrupador = new();

    public string Id { get; } = id;

    public RelayConnection? Host { get; private set; }
    public RelayConnection? Viewer { get; private set; }

    public RemoteSessionState State { get; private set; } = RemoteSessionState.Created;

    /// <summary>Ultima configuracion que mando el host. Solo en memoria: el
    /// servidor no persiste nada del video, ni siquiera sus parametros.</summary>
    public VideoConfig? Config { get; private set; }

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
                if (Config is not null)
                    conexion.SetVideoConfig(Config);
                else
                    conexion.RequireKeyframe();
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

    /// <summary>Lo que manda la PC controlada: video y cursor.</summary>
    public async ValueTask FromHostAsync(RemotePacket paquete, CancellationToken cancellationToken)
    {
        switch (paquete.PayloadCase)
        {
            case RemotePacket.PayloadOneofCase.VideoConfig:
                lock (_puerta)
                    Config = paquete.VideoConfig;

                Viewer?.SetVideoConfig(paquete.VideoConfig);
                break;

            case RemotePacket.PayloadOneofCase.VideoChunk:
                if (_agrupador.TryAdd(paquete.VideoChunk, out var frame))
                {
                    FramesReceived++;

                    if (Viewer is { } viewer && viewer.SendVideo(frame!))
                    {
                        FramesForwarded++;
                        BytesForwarded += frame!.PayloadBytes;
                    }
                }

                break;

            default:
                await ReenviarControlAsync(Viewer, paquete, cancellationToken);
                break;
        }
    }

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

    /// <summary>Quita la sesion si ya no queda nadie. Sin esto, cada sesion del
    /// dia deja una entrada muerta en el diccionario.</summary>
    public void DropIfEmpty(RemoteSession sesion)
    {
        if (sesion.IsEmpty)
            _sesiones.TryRemove(new KeyValuePair<string, RemoteSession>(sesion.Id, sesion));
    }
}
