using DeviceHub.Remote.Contracts;

namespace DeviceHub.Server.Remote;

/// <summary>
/// Cola de video hacia UN destino, con la politica de descarte del relay.
///
/// DESCARTAR UN FRAME NO BASTA. Un P-frame se codifica como diferencia contra
/// los anteriores, asi que en cuanto se pierde uno, todos los que vienen detras
/// se descodifican contra una imagen que el viewer no tiene:
///
///     se descarta el frame 100
///       -> el 101 se descodifica contra un 100 que no existe
///       -> el 102 contra un 101 ya corrupto
///       -> la corrupcion no se arregla sola, se acumula
///
/// Por eso, ante cualquier descarte, esto entra en `AwaitingKeyframe` y tira
/// TODO lo que no sea IDR hasta que aparezca uno. La imagen se congela unos
/// cientos de milisegundos -- lo que tarde el siguiente keyframe -- en vez de
/// convertirse en manchas que ya no se van.
///
/// Al descartar por congestion se vacia la cola entera, no solo el frame mas
/// viejo: lo que quedaba dentro dependia justo de lo que se acaba de tirar.
/// </summary>
public sealed class VideoRelayQueue
{
    private readonly int _capacidad;
    private readonly Lock _puerta = new();
    private readonly Queue<VideoFrameChunks> _cola = new();

    /// <summary>
    /// Pocos frames a proposito. Una cola larga no evita el descarte, solo lo
    /// retrasa y lo convierte en retraso acumulado: un frame que espera su turno
    /// ya llega tarde a una sesion interactiva.
    /// </summary>
    public VideoRelayQueue(int capacidad = 4) => _capacidad = capacidad;

    /// <summary>Arranca en true: sin config y sin IDR no hay nada descodificable
    /// que mandar.</summary>
    public bool AwaitingKeyframe { get; private set; } = true;

    /// <summary>Hay que mandar VideoConfig por delante del proximo IDR.</summary>
    public bool ConfigPending { get; private set; } = true;

    public VideoConfig? Config { get; private set; }
    public uint ConfigVersion => Config?.ConfigVersion ?? 0;

    public long FramesOffered { get; private set; }
    public long FramesQueued { get; private set; }

    /// <summary>Frames tirados por congestion.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>Frames tirados por no ser el IDR que se esperaba. Es la medida
    /// de cuanto dura el congelado tras una perdida.</summary>
    public long DiscardedWaitingIdr { get; private set; }

    /// <summary>Frames de una configuracion que ya no esta vigente.</summary>
    public long StaleConfig { get; private set; }

    /// <summary>Frames llegados antes de que hubiera VideoConfig.</summary>
    public long DiscardedNoConfig { get; private set; }

    /// <summary>
    /// Hay que PEDIRLE UN IDR AL HOST, y hasta entonces esta pantalla no manda
    /// nada.
    ///
    /// Faltaba, y era el agujero mas caro del relay: al entrar en
    /// AwaitingKeyframe se descarta todo lo que no sea IDR, pero nadie le decia
    /// al host que emitiera uno. Habia que esperar a que el codificador lo
    /// sacara por su cuenta -- y el GOP no se configura en ningun sitio, asi que
    /// eso podian ser segundos de imagen congelada por cada atasco.
    /// </summary>
    private bool _pedirIdr;

    /// <summary>Devuelve si hay peticion pendiente y la consume. La consume
    /// para que no se pida un IDR por frame mientras dura el atasco.</summary>
    public bool TomarPeticionDeIdr()
    {
        lock (_puerta)
        {
            var pedir = _pedirIdr;
            _pedirIdr = false;
            return pedir;
        }
    }

    public int HighWater { get; private set; }
    public int Depth { get { lock (_puerta) return _cola.Count; } }

    /// <summary>
    /// Configuracion nueva del host. Lo encolado se codifico con la anterior:
    /// descodificarlo con parametros nuevos no da error, da imagen corrupta.
    /// </summary>
    public void SetConfig(VideoConfig config)
    {
        lock (_puerta)
        {
            StaleConfig += _cola.Count;
            _cola.Clear();

            Config = config;
            AwaitingKeyframe = true;
            ConfigPending = true;
        }
    }

    /// <summary>Un viewer que acaba de conectar no tiene contexto: necesita
    /// VideoConfig y un IDR antes que nada.</summary>
    public void RequireKeyframe()
    {
        lock (_puerta)
        {
            DiscardedWaitingIdr += _cola.Count;
            _cola.Clear();

            AwaitingKeyframe = true;
            ConfigPending = true;
            _pedirIdr = true;
        }
    }

    /// <summary>Devuelve false si el frame no se encola, con el contador
    /// correspondiente ya sumado.</summary>
    public bool TryEnqueue(VideoFrameChunks frame)
    {
        lock (_puerta)
        {
            FramesOffered++;

            if (Config is null)
            {
                DiscardedNoConfig++;
                return false;
            }

            if (frame.ConfigVersion != Config.ConfigVersion)
            {
                StaleConfig++;
                return false;
            }

            if (_cola.Count >= _capacidad)
            {
                // Congestion. Se va la cola ENTERA, no solo el frame mas viejo:
                // lo que quedaba dentro se descodifica contra lo que se tira.
                FramesDropped += _cola.Count;
                _cola.Clear();

                AwaitingKeyframe = true;
                ConfigPending = true;
                _pedirIdr = true;
            }

            if (AwaitingKeyframe && !frame.KeyFrame)
            {
                DiscardedWaitingIdr++;
                return false;
            }

            _cola.Enqueue(frame);
            FramesQueued++;
            HighWater = Math.Max(HighWater, _cola.Count);

            // El IDR reabre la cadena: a partir de aqui los P-frames vuelven a
            // tener contra que descodificarse.
            if (frame.KeyFrame)
                AwaitingKeyframe = false;

            return true;
        }
    }

    /// <summary>
    /// Saca el siguiente frame. `config` viene relleno cuando hay que mandarlo
    /// por delante -- viewer nuevo, cambio de configuracion o recuperacion de
    /// una perdida -- porque un keyframe suelto no le sirve de nada a un viewer
    /// que no sabe con que parametros descodificarlo.
    /// </summary>
    public bool TryDequeue(out VideoConfig? config, out VideoFrameChunks? frame)
    {
        lock (_puerta)
        {
            config = null;
            frame = null;

            if (_cola.Count == 0)
                return false;

            frame = _cola.Dequeue();

            if (ConfigPending)
            {
                config = Config;
                ConfigPending = false;
            }

            return true;
        }
    }
}
