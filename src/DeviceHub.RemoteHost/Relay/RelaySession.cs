using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Capture;
using DeviceHub.RemoteHost.Encode;
using DeviceHub.RemoteHost.Files;
using DeviceHub.RemoteHost.Input;
using Grpc.Core;
using Grpc.Net.Client;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Relay;

/// <summary>Lo que hace falta para sostener una sesion contra el relay.</summary>
public sealed record RelayOptions
{
    public required string Servidor { get; init; }
    public required string SesionId { get; init; }

    /// <summary>El identificador de DEVICEHUB, no el hostname de Windows. El
    /// ticket se ata a este, y son cosas distintas: mandar el nombre de la
    /// maquina hacia que todo saliera rechazado por WrongMachine sin que el
    /// mensaje dijera por que.</summary>
    public required string MachineId { get; init; }

    /// <summary>Null = se lee de stdin. En produccion lo entrega el agente por
    /// el named pipe; nunca llega por linea de comandos.</summary>
    public string? Ticket { get; init; }

    /// <summary>Pines SPKI del servidor. Vacio = validacion normal de TLS.</summary>
    public IReadOnlyList<string> PinnedKeys { get; init; } = [];

    public bool AllowUntrusted { get; init; }

    public int Adapter { get; init; }
    public int Output { get; init; }

    /// <summary>
    /// H.265 en vez de H.264. Interruptor y no constante, por lo mismo que
    /// SecureDesktop: la pregunta -- si la iGPU de planta lo codifica mas
    /// rapido, y si la PC del tecnico lo descodifica -- solo se responde en ESE
    /// hardware, y probarlo tiene que ser cambiar una linea y reiniciar, no
    /// volver a compilar.
    ///
    /// Si la maquina no tiene codificador HEVC se cae solo a H.264 y lo dice.
    /// </summary>
    public bool UsarH265 { get; init; }

    /// <summary>Cero o menos = hasta que alguien la corte, que es como corre una
    /// sesion de verdad.</summary>
    public int Seconds { get; init; }

    public int Fps { get; init; } = 60;
    public int Bitrate { get; init; } = 6_000_000;

    /// <summary>A donde va el progreso. En --relay-test, a la consola; lanzado
    /// por el agente, al named pipe, porque el proceso no tiene consola.</summary>
    public Action<string> Escribir { get; init; } = Console.WriteLine;
}

/// <summary>
/// La cadena de las Fases 1 y 2 con la salida enchufada al relay:
///
///   DXGI -> H264 -> VideoFrameChunks -> RemoteRelayService.HostChannel
///
/// Corre en la PC CONTROLADA. En produccion la lanza el agente dentro de la
/// sesion interactiva (Fase 7); --relay-test es la misma cadena a mano.
/// </summary>
public static class RelaySession
{
    public static async Task<int> RunAsync(RelayOptions opciones, CancellationToken cancellationToken)
    {
        // El ticket se usa una vez y se suelta: a partir del HelloAccepted quien
        // sostiene la sesion es el token de reconexion, que vive solo en memoria.
        var ticket = opciones.Ticket ?? BootstrapTicket.Read();

        if (ticket is null)
        {
            Console.Error.WriteLine("""
                Falta el ticket. Se pasa por stdin, nunca por linea de comandos:

                  $t | .\DeviceHub.RemoteHost.exe --relay-test --server ... --session ...

                Los argumentos de un proceso los lee cualquier usuario de la maquina.
                """);

            return 6;
        }

        MediaFactory.MFStartup(true).CheckError();

        try
        {
            // BUCLE DE RECONEXION DEL HOST. Fase 14.
            //
            // El visor ya volvia solo; este no, y sin host no hay nada al otro
            // lado -- el tecnico se quedaba con una ventana viva y una pantalla
            // muerta. En una planta con wifi regular es lo que mas se nota.
            //
            // Se vuelve a la MISMA sesion con el token de reconexion, sin gastar
            // un ticket nuevo: el ticket es de un solo uso y ya se consumio.
            var corte = DateTimeOffset.UtcNow;
            var espera = TimeSpan.FromMilliseconds(250);
            var codigo = 4;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    corte = DateTimeOffset.UtcNow;
                    codigo = await SesionAsync(opciones, ticket, cancellationToken);
                    break;
                }
                catch (RpcException ex) when (PuedeVolver(corte, cancellationToken))
                {
                    // Una sesion que aguanto un rato empieza de cero: si no, un
                    // microcorte a los diez minutos arrancaria esperando los 5 s
                    // a los que llego el corte anterior.
                    if (DateTimeOffset.UtcNow - corte > TimeSpan.FromSeconds(10))
                        espera = TimeSpan.FromMilliseconds(250);

                    opciones.Escribir(
                        $"Conexion con el relay perdida ({ex.StatusCode}). " +
                        $"Volviendo a la sesion en {espera.TotalSeconds:0.0} s...");

                    // Espera CANCELABLE: con Thread.Sleep, un STOP del agente se
                    // quedaria esperando a que terminara la siesta.
                    if (cancellationToken.WaitHandle.WaitOne(espera))
                        break;

                    espera = espera < TimeSpan.FromSeconds(5)
                        ? espera + espera
                        : TimeSpan.FromSeconds(5);
                }
            }

            return codigo;
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            Console.Error.WriteLine($"No se puede capturar: {ex.Message}");
            return 2;
        }
        catch (VideoEncoderUnavailableException ex)
        {
            Console.Error.WriteLine($"No se puede codificar: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Relay: {ex.Message}");
            return 4;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>
    /// Se reintenta durante un minuto con el RELOJ DE AQUI, no con el
    /// `reconnect_until` del servidor: esa marca viene de otra maquina y si
    /// llegara mal -- o no llegara -- la comparacion diria que la gracia ya paso
    /// y no se reintentaria nunca. Quien manda de verdad es el relay rechazando
    /// el token.
    ///
    /// Sin token no se vuelve: o la sesion se cerro en orden, o el relay ya la
    /// dio por muerta. Insistir solo alargaria un proceso que ya no pinta nada.
    /// </summary>
    private static bool PuedeVolver(DateTimeOffset desde, CancellationToken cancellationToken)
        => _tokenReconexion is { Length: > 0 }
           && !cancellationToken.IsCancellationRequested
           && DateTimeOffset.UtcNow - desde < TimeSpan.FromMinutes(1);

    /// <summary>UNA conexion, de principio a fin. Si el cable se corta, lanza y
    /// el bucle de arriba decide si vuelve.</summary>
    private static async Task<int> SesionAsync(
        RelayOptions opciones, string ticket, CancellationToken cancellationToken)
    {
        using var canal = Conectar(opciones);
        var cliente = new RemoteRelayService.RemoteRelayServiceClient(canal);

        using var llamada = cliente.HostChannel();

        // Exclusivos: arranque con ticket, vuelta con token. Mandar los dos es
        // un error de protocolo y el relay lo rechaza.
        var volviendo = _tokenReconexion is { Length: > 0 };

        await EscribirAsync(llamada, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            Hello = new Hello
            {
                MachineId = opciones.MachineId,
                Role = RemoteRole.Host,
                Ticket = volviendo ? string.Empty : ticket,
                ReconnectToken = volviendo ? _tokenReconexion! : string.Empty,
                Capabilities = new RemoteCapabilities
                {
                    MaxProtocolVersion = RemoteSessionProtocol.Version,
                    Codecs = { VideoCodec.H264 },
                    SupportsCursor = true,
                    SupportsInput = true
                }
            }
        }, CancellationToken.None);

        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var entrante = LeerAsync(llamada, opciones, cancelacion);

        var codigo = await EmitirAsync(llamada, opciones, cancelacion.Token);

        await EscribirAsync(llamada, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            Close = new SessionClose { Reason = SessionCloseReason.Normal, Detail = "fin de la sesion" }
        }, CancellationToken.None);

        await llamada.RequestStream.CompleteAsync();
        await cancelacion.CancelAsync();

        try { await entrante; } catch (Exception) { /* cerrando */ }

        return codigo;
    }

    /// <summary>
    /// Lo que el hilo de captura le pasa al de red: bytes ya codificados, ni
    /// texturas ni nada de GPU. `Otro` es cualquier paquete
    /// ya montado -- portapapeles, lista de pantallas -- para no ir anadiendo un
    /// campo por cada mensaje nuevo del protocolo.
    /// </summary>
    private sealed record Enviable(
        VideoConfig? Config, VideoFrameChunks? Grupo, RemotePacket? Otro = null);

    /// <summary>Lo que el tecnico copio en SU PC, camino del portapapeles de
    /// esta. Lo aplica el hilo de captura, igual que la entrada.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> PortapapelesEntrante = new();

    /// <summary>
    /// Pantalla que el tecnico quiere ver. La escribe el hilo de red y la lee el
    /// de captura, que al verla cambiar rehace la cadena entera: duplicador,
    /// codificador y config_version.
    /// </summary>
    private static int _pantalla;

    /// <summary>
    /// Codec que el tecnico pidio desde el visor. Arranca en lo que diga el
    /// appsettings del agente y se puede cambiar en caliente.
    ///
    /// Lo lee el hilo de captura y lo escribe el de red, igual que la pantalla:
    /// tocar el MFT desde fuera de su hilo es la familia de cuelgues que costo
    /// dos fases entender.
    /// </summary>
    private static VideoCodec _codec = VideoCodec.Unspecified;

    /// <summary>
    /// Cuantos bits se le dan a la imagen, sobre la base que pide la
    /// resolucion. Lo cambia el tecnico desde el visor y NO rehace nada: el
    /// bitrate se toca sobre el codificador en marcha.
    /// </summary>
    private static double _calidad = ControlBitrate.CalidadEquilibrada;

    /// <summary>Numeracion de frames de toda la SESION. No se reinicia al rehacer
    /// la captura: ver el comentario en la llamada a Split.</summary>
    private static ulong _frameDeLaSesion;

    /// <summary>
    /// Fase 13. Los dos los PIDE el hilo de red y los APLICA el de captura, que
    /// es el unico dueno del MFT. Tocar el codificador desde fuera de su hilo es
    /// la familia de cuelgues que costo dos fases entender.
    /// </summary>
    private static volatile bool _keyframePedido;

    /// <summary>El visor pidio soltar todo lo que quedo hundido. Lo atiende el
    /// hilo de captura, que es el atado al escritorio activo.</summary>
    private static volatile bool _soltarEntrada;

    private static string Anotar(ref bool bandera)
    {
        bandera = true;
        return string.Empty;
    }

    /// <summary>
    /// Pantallas para las que se pidio un IDR. Por pantalla y no una bandera
    /// global: con dos monitores, una perdida en uno no justifica gastar el
    /// frame mas caro que existe en el otro -- y menos cuando lo que la causo
    /// fue que la red no daba abasto.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _keyframePorPantalla = new();

    /// <summary>
    /// EL VISOR CONFIRMA FRAMES.
    ///
    /// Arranca en false y se enciende con el primer VideoAck que llegue. Un
    /// visor viejo no manda ninguno, y quedarse esperandolo dejaria la sesion a
    /// un frame por segundo: el freno se enciende solo cuando el otro extremo
    /// demuestra que sabe soltarlo.
    /// </summary>
    private static volatile bool _visorAcusa;

    /// <summary>Entrega un acuse a la bomba de su pantalla. Lo pone Escritorio,
    /// igual que _avisar.</summary>
    private static Action<uint, ulong>? _acusar;

    /// <summary>Entrega un acuse de MOSTRADO. Separado del otro porque no suelta
    /// el freno: solo alimenta la medida.</summary>
    private static Action<uint, ulong>? _mostrado;

    /// <summary>
    /// Cuanto tarda un frame desde que sale de aqui hasta que esta en la
    /// pantalla del tecnico.
    ///
    /// Es la unica cifra de la sesion medida de punta a punta con UN solo
    /// reloj. Todo lo demas mide un tramo: el RTT mide la red, decode p50 mide
    /// el descodificador, render FPS mide el pintado. Ninguna decia donde
    /// estaban los milisegundos.
    /// </summary>
    private static readonly MedidorRetraso _verse = new();

    /// <summary>Cuanto se espera un acuse antes de seguir sin el. RustDesk usa
    /// 3 s; aqui menos, porque su espera se corta en cuanto llegan todos y esta
    /// solo salta cuando el acuse se perdio de verdad.</summary>
    private const int EsperaDeAcuseMs = 1000;

    private static int _bitrateDeseado;

    /// <summary>Frames por segundo a los que capturar. Lo decide el hilo de red
    /// mirando el RTT y lo obedece el de captura.</summary>
    private static int _fpsDeseado = ControlFps.Inicial;

    /// <summary>
    /// RTT medido POR EL HOST, con su propio reloj de ida y vuelta, y separado
    /// en red pura y cola. Lo que gobierna los FPS es la parte de COLA: la red
    /// no la podemos cambiar y bajar el ritmo por su culpa es tirar calidad
    /// para arreglar algo que no estaba roto.
    /// </summary>
    private static readonly MedidorRetraso _retraso = new();

    private sealed class Contadores
    {
        public long Capturados, Codificados, Claves, Enviados, Trozos, Bytes;
        public long DescartesEncoder, DescartesCaptura;

        /// <summary>Frames que salieron y nadie confirmo en EsperaDeAcuseMs. Si
        /// esto sube, el visor no esta siguiendo el ritmo.</summary>
        public long AcusesPerdidos;
        public uint ConfigVersion;

        /// <summary>La misma cuenta, en un campo que Interlocked puede tocar: al
        /// relevar una pantalla se estrena version desde su propia bomba.</summary>
        public int ConfigVersionCompartida;
        public Exception? Fallo;
    }

    /// <summary>
    /// EmitirAsync no captura. Solo lee de la cola y escribe en el cable.
    ///
    /// POR QUE ESTA PARTIDO EN DOS HILOS. La primera version hacia `await` de las
    /// escrituras de red DENTRO del bucle de captura. Cada `await` devuelve el
    /// control y la continuacion reanuda en un hilo cualquiera del pool, asi que
    /// `AcquireNextFrame` acababa llamandose desde hilos que iban cambiando.
    /// Desktop Duplication no lo aguanta: la prueba corria unos 12 s y despues se
    /// quedaba bloqueada dentro de la captura, con el proceso vivo, sin CPU y sin
    /// un solo error. En `--encode-test` no pasaba porque ese bucle es sincrono
    /// de principio a fin.
    ///
    /// Es la misma familia del cuelgue del FramePipeline de la Fase 2, que quedo
    /// sin explicar: DXGI, D3D11 y el MFT quieren UN hilo, y hay que darselo.
    /// </summary>
    private static async Task<int> EmitirAsync(
        AsyncDuplexStreamingCall<RemotePacket, RemotePacket> llamada, RelayOptions opciones,
        CancellationToken cancellationToken)
    {
        // Acotada y CON ESPERA, no con descarte: tirar un frame ya codificado
        // rompe la cadena H.264 en el emisor, que es justo lo que el relay se
        // esfuerza en no hacer. Si la red no da abasto, la captura se para un
        // momento y DXGI simplemente entrega la pantalla mas reciente despues.
        var cola = System.Threading.Channels.Channel.CreateBounded<Enviable>(
            new System.Threading.Channels.BoundedChannelOptions(8)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true,
                // YA NO. Desde que hay un hilo por pantalla, a esta cola escriben
                // varios productores a la vez, y dejar SingleWriter puesto es
                // prometerle al canal algo que no se cumple: la optimizacion que
                // habilita da por hecho que nadie compite, y con dos hilos eso se
                // paga en corrupcion silenciosa, no en excepcion.
                SingleWriter = false
            });

        var cuenta = new Contadores();

        _avisar = texto => cola.Writer.TryWrite(new Enviable(null, null, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            HostStatus = new HostStatus { Text = texto }
        }));

        _medir = texto => cola.Writer.TryWrite(new Enviable(null, null, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            HostStatus = new HostStatus { Text = texto, Measurements = true }
        }));

        var hilo = new Thread(() => Capturar(cola.Writer, cuenta, opciones, cancellationToken))
        {
            IsBackground = true,
            Name = "devicehub-captura"
        };

        hilo.Start();

        var reloj = Stopwatch.StartNew();
        var siguienteAviso = TimeSpan.FromSeconds(2);
        var acusesAlMirar = 0L;
        var codificadosAlMirar = 0L;

        try
        {
            await foreach (var pieza in cola.Reader.ReadAllAsync(cancellationToken))
            {
                if (pieza.Config is not null)
                {
                    await EscribirAsync(llamada, new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = opciones.SesionId,
                        VideoConfig = pieza.Config
                    }, cancellationToken);
                }

                if (pieza.Otro is not null)
                    await EscribirAsync(llamada, pieza.Otro, cancellationToken);

                if (pieza.Grupo is not null)
                {
                    foreach (var trozo in pieza.Grupo.Chunks)
                    {
                        await EscribirAsync(llamada, new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            VideoChunk = trozo
                        }, cancellationToken);

                        cuenta.Trozos++;
                    }

                    cuenta.Enviados++;
                    cuenta.Bytes += pieza.Grupo.PayloadBytes;
                }

                if (reloj.Elapsed >= siguienteAviso)
                {
                    // LA OCUPACION SE MIDE ANTES DE ESCRIBIR NADA.
                    //
                    // Medir() encola el mensaje de estado EN ESTA MISMA COLA, y
                    // Avisar() igual. Leyendola despues, el controlador de
                    // bitrate encontraba siempre al menos un elemento -- el
                    // nuestro -- y caia en la rama de "con algo de cola no se
                    // toca". No subia NUNCA: la sesion se quedaba clavada en el
                    // bitrate de arranque, que en Equilibrado son 1388 kbps, y
                    // por eso la imagen se ablandaba con movimiento mientras en
                    // Fiel no.
                    //
                    // La linea de estado imprimia "cola 0" y era verdad: se
                    // evalua antes de encolarse a si misma. El que veia 1 era el
                    // controlador.
                    var enCola = cola.Reader.Count;

                    _medidas?.Invoke(cuenta);

                    Medir(opciones,
                        $"{reloj.Elapsed:mm\\:ss}  capturados {cuenta.Capturados}  codificados {cuenta.Codificados}  " +
                        $"frames enviados {cuenta.Enviados}  chunks {cuenta.Trozos}  " +
                        $"{cuenta.Bytes * 8 / reloj.Elapsed.TotalSeconds / 1_000_000:0.00} Mbps  " +
                        $"keyframes {cuenta.Claves}  config {cuenta.ConfigVersion}  cola {enCola}  " +
                        $"acuses {(_visorAcusa ? "si" : "no")}/{cuenta.AcusesPerdidos} perdidos  " +
                        $"red {_retraso.Base:0.0} ms + cola {_retraso.Encolado:0.0} ms  " +
                        $"verse {_verse.Ultimo:0.0} ms (min {_verse.Base:0.0})  " +
                        $"fps {_fpsDeseado}  " +
                        $"objetivo {_bitrateDeseado / 1000} kbps ({_calidad:0.00}x)  " +
                        // Aplicados y rechazados de SendInput. Es lo que dice si
                        // la entrada llega de verdad al otro lado o se la traga
                        // el escritorio equivocado.
                        $"descartes {cuenta.DescartesEncoder}+{cuenta.DescartesCaptura}  " +
                        $"entrada {_entrada?.Applied ?? 0}/{_entrada?.Rejected ?? 0}");

                    // El bitrate se DECIDE aqui, donde se ve la cola, y se
                    // APLICA en el hilo de captura. Cada 2 s y no por frame: un
                    // controlador que reacciona a cada hipo produce vaiven.
                    // CON EL FRENO PUESTO, LA COLA YA NO DICE NADA.
                    //
                    // Antes se llenaba cuando la red no daba abasto y esa era la
                    // senal. Ahora la captura se para en el acuse, asi que la
                    // cola vive vacia y el bitrate subiria hasta el maximo
                    // aunque no cupiera. Un acuse perdido pasa a contar como
                    // cola llena, que es exactamente lo que significa.
                    var acusesPerdidos = Interlocked.Read(ref cuenta.AcusesPerdidos);
                    var ocupacion = acusesPerdidos > acusesAlMirar ? 8 : enCola;

                    acusesAlMirar = acusesPerdidos;

                    // Y si la pantalla se movio de verdad en estos 2 s. Con el
                    // escritorio quieto no se codifica casi nada y la cola vive
                    // vacia: leer eso como "cabe mas" es subir hasta el techo
                    // sin una sola prueba.
                    var codificados = Interlocked.Read(ref cuenta.Codificados);
                    var viva = codificados - codificadosAlMirar >= _fpsDeseado;

                    codificadosAlMirar = codificados;

                    _bitrateDeseado = ControlBitrate.Siguiente(_bitrateDeseado, ocupacion, 8, viva);

                    // Y el ritmo, por su cuenta y con otra senal. La cola dice
                    // que algo va lento; el RTT dice que es la RED.
                    _fpsDeseado = ControlFps.Siguiente(_fpsDeseado, _retraso.Encolado);

                    // El host tambien pregunta. Hasta ahora solo contestaba, asi
                    // que el RTT lo conocia el visor y aqui se controlaba a
                    // ciegas.
                    await EscribirAsync(llamada, new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = opciones.SesionId,
                        Ping = new Ping { SentAtUs = Ahora() }
                    }, cancellationToken);

                    siguienteAviso += TimeSpan.FromSeconds(2);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // La sesion se corto: el relay la cerro o el agente mando STOP. Se
            // sale a informar, no a propagar.
        }

        hilo.Join(TimeSpan.FromSeconds(3));

        if (cuenta.Fallo is not null)
            throw cuenta.Fallo;

        var segundos = Math.Max(reloj.Elapsed.TotalSeconds, 0.001);

        opciones.Escribir(
            $"Captured {cuenta.Capturados}  Encoded {cuenta.Codificados}  Frames sent {cuenta.Enviados}  " +
            $"Chunks {cuenta.Trozos}  {cuenta.Bytes * 8 / segundos / 1_000_000:0.00} Mbps  " +
            $"Keyframes {cuenta.Claves}  Config v{cuenta.ConfigVersion}  " +
            $"Encode drops {cuenta.DescartesEncoder} (captura {cuenta.DescartesCaptura})");

        return cuenta.Enviados > 0 ? 0 : 5;
    }

    /// <summary>
    /// Captura y codifica, y NADA MAS. Un solo hilo de principio a fin: DXGI, el
    /// dispositivo D3D11 y el MFT se quedan aqui dentro y no ven otro.
    ///
    /// Lo unico que sale por la cola son bytes en memoria administrada, asi que
    /// el hilo de red no toca la GPU ni de lejos.
    /// </summary>
    private static void Capturar(
        System.Threading.Channels.ChannelWriter<Enviable> salida, Contadores cuenta,
        RelayOptions opciones, CancellationToken cancellationToken)
    {
        try
        {
            // Fase 19. La estacion primero, los escritorios despues: los
            // escritorios cuelgan de una estacion, y un proceso lanzado desde un
            // servicio puede arrancar en una que no es la interactiva.
            InputDesktop.UsarEstacionInteractiva();

            // HILO DE ENTRADA APARTE, y con motivo medido.
            //
            // SetThreadDesktop falla si el hilo tiene ventanas, y D3D11 y Media
            // Foundation crean ventanas ocultas en cuanto se codifica el primer
            // frame. Este hilo no toca la GPU jamas, asi que si puede atarse.
            //
            // Tiene su PROPIO InputDesktop: el intento de la 1.4.1 fallo porque
            // dos hilos abrian y CERRABAN handles del mismo, y el CloseDesktop de
            // uno tiraba el escritorio bajo los pies del otro.
            using var pararEntrada = new CancellationTokenSource();
            using var enlazado = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, pararEntrada.Token);

            var hiloEntrada = new Thread(() => Teclear(opciones, enlazado.Token))
            {
                IsBackground = true,
                Name = "devicehub-entrada"
            };

            hiloEntrada.Start();

            var reloj = Stopwatch.StartNew();
            var duracion = opciones.Seconds > 0 ? TimeSpan.FromSeconds(opciones.Seconds) : TimeSpan.MaxValue;

            // Fallos SEGUIDOS, no totales. Uno suelto al saltar de escritorio es
            // lo normal; lo que no puede es no recuperarse nunca.
            var fallosSeguidos = 0;

            // La ultima pantalla que se pudo capturar de verdad.
            var pantallaBuena = _pantalla;

            while (reloj.Elapsed < duracion && !cancellationToken.IsCancellationRequested)
            {
                // UN HILO NUEVO POR ESCRITORIO.
                //
                // Aqui esta la razon de que la pantalla de bloqueo se viera "a
                // veces si y a veces no". SetThreadDesktop solo funciona en un
                // hilo SIN ventanas, y este las tiene en cuanto codifica un
                // frame: o sea que un hilo que ya ha capturado no puede mudarse a
                // Winlogon nunca mas. Lo unico que rehacia la captura era que
                // Windows tuviera a bien mandar un ACCESS_LOST, y eso llega o no
                // llega segun el momento. Esa era la moneda al aire.
                //
                // Un hilo recien creado es virgen: se ata PRIMERO al escritorio
                // activo y crea el dispositivo D3D despues, ya dentro de el. Es
                // lo mismo que hacen RustDesk y AnyDesk lanzando un proceso por
                // escritorio, a escala de hilo y sin romper la sesion del relay.
                Exception? fallo = null;

                var hilo = new Thread(() =>
                {
                    try
                    {
                        using var escritorio = new InputDesktop();
                        escritorio.SeguirActivo();

                        Escritorio(salida, cuenta, opciones, escritorio, reloj, duracion, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        fallo = ex;
                    }
                })
                {
                    IsBackground = true,
                    Name = "devicehub-captura"
                };

                hilo.Start();
                hilo.Join();

                if (fallo is null)
                {
                    // Esta pantalla si se pudo capturar: es a la que se vuelve si
                    // la proxima eleccion sale mal.
                    pantallaBuena = _pantalla;
                    fallosSeguidos = 0;
                    continue;
                }

                // Al saltar a Winlogon el primer DuplicateOutput falla a menudo
                // porque la transicion del escritorio sigue en curso. Antes ese
                // fallo normal y recuperable terminaba la sesion entera.
                fallosSeguidos++;

                Avisar(opciones,
                    $"Fallo al capturar (intento {fallosSeguidos}): {fallo.GetType().Name}: {fallo.Message}");

                // SI LO QUE FALLO FUE UN CAMBIO DE PANTALLA, SE VUELVE ATRAS.
                //
                // Insistir 60 veces sobre una eleccion imposible deja al tecnico
                // dos minutos con la imagen congelada y sin saber por que. El
                // caso real: "todas a la vez" son 3840x1080, y un codificador que
                // no traga esa resolucion lanza en cada intento.
                //
                // Volver a la pantalla que SI funcionaba es informacion, ademas
                // de un arreglo: la imagen reaparece donde estaba, y eso dice
                // que la eleccion nueva no se pudo.
                if (fallosSeguidos >= 2 && _pantalla != pantallaBuena)
                {
                    Avisar(opciones,
                        $"No se pudo capturar la pantalla {_pantalla}; se vuelve a la {pantallaBuena}");

                    _pantalla = pantallaBuena;
                    fallosSeguidos = 0;
                    continue;
                }

                // Un minuto largo de reintentos, no cinco segundos. Rendirse
                // pronto convierte un escritorio que tarda en montarse en una
                // sesion muerta, y quedarse con la ultima imagen congelada es
                // mejor que cerrarle la sesion al tecnico.
                if (fallosSeguidos >= 60)
                {
                    cuenta.Fallo = fallo;
                    break;
                }

                // Medio segundo al principio y hasta dos: los primeros fallos son
                // la transicion del escritorio, que va rapido; a partir de ahi es
                // algo que no se va a arreglar insistiendo mas deprisa.
                var espera = Math.Min(500 * fallosSeguidos, 2000);

                if (cancellationToken.WaitHandle.WaitOne(espera))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            cuenta.Fallo = ex;
        }
        finally
        {
            salida.Complete();
        }
    }

    /// <summary>
    /// Aplica la entrada del tecnico, y nada mas.
    ///
    /// Hilo propio porque es el UNICO que puede seguir al escritorio activo: no
    /// crea ventanas, asi que SetThreadDesktop no le falla. Cada 20 ms en vez de
    /// los 100 del bucle de captura, que ademas con la pantalla quieta se pasa el
    /// rato bloqueado en AcquireNextFrame.
    /// </summary>
    private static void Teclear(RelayOptions opciones, CancellationToken cancellationToken)
    {
        try
        {
            InputDesktop.UsarEstacionInteractiva();

            using var escritorio = new InputDesktop();
            var ultimoAviso = string.Empty;
            var avisadoSinInyector = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var salto = escritorio.SeguirActivo();

                // Se avisa UNA vez por estado, no cada 20 ms: en un bucle asi, un
                // log por vuelta convierte el visor de eventos en el cuello de
                // botella de la sesion.
                var aviso = salto switch
                {
                    Salto.Cambiado => escritorio.EscrituraConcedida
                        ? $"La entrada salto a {escritorio.Name}"
                        : $"La entrada salto a {escritorio.Name} SOLO PARA LEER: " +
                          "el raton y el teclado no van a entrar ahi",

                    Salto.NoSePudoAtar =>
                        $"NO se pudo atar la entrada a {escritorio.NombrePedido} " +
                        $"(error {escritorio.UltimoError}). El raton y el teclado van al escritorio viejo.",

                    _ => ultimoAviso
                };

                if (aviso != ultimoAviso)
                {
                    // AL TECNICO tambien. "Veo la pantalla pero no puedo mover
                    // nada" tiene su explicacion escrita aqui desde hace dos
                    // versiones, y estaba yendo a un log que nadie abre.
                    Avisar(opciones, aviso);
                    ultimoAviso = aviso;
                }

                // Sin inyector no se aplica nada, y hasta ahora eso pasaba en
                // silencio: la cola se vaciaba y los eventos se perdian. Es la
                // diferencia entre "la entrada no llega" y "la entrada llega y no
                // hay quien la aplique".
                if (_entrada is null && !Pendientes.IsEmpty && !avisadoSinInyector)
                {
                    avisadoSinInyector = true;
                    Avisar(opciones, "Llega entrada pero la captura todavia no publico el inyector.");
                }

                // ANTES de la entrada nueva: si el visor acaba de reconectar,
                // lo primero es despegar lo que quedo hundido y despues aplicar
                // lo que venga.
                if (_soltarEntrada)
                {
                    _soltarEntrada = false;

                    if (_entrada?.SoltarTodo() is > 0 and var cuantos)
                        Avisar(opciones, $"Se soltaron {cuantos} teclas o botones que quedaron pegados.");
                }

                while (Pendientes.TryDequeue(out var evento))
                    _entrada?.Apply(evento);

                cancellationToken.WaitHandle.WaitOne(20);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            opciones.Escribir($"El hilo de entrada murio: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Captura UN escritorio hasta que la entrada salte a otro.
    ///
    /// Es una funcion aparte porque al saltar hay que rehacer la cadena entera:
    /// la duplicacion DXGI queda invalidada, y con ella el codificador, porque el
    /// SPS/PPS que el visor tiene descodifica lo de antes. Por eso cada escritorio
    /// estrena su propia config_version -- que es exactamente para lo que la Fase
    /// 4 la puso en cada chunk.
    /// </summary>
    private static void Escritorio(
        System.Threading.Channels.ChannelWriter<Enviable> salida, Contadores cuenta,
        RelayOptions opciones, InputDesktop escritorio, Stopwatch reloj, TimeSpan duracion,
        CancellationToken cancellationToken)
    {
        var pantallas = Pantallas.Listar();

        // Lo que DXGI enumero, tal cual, en el log del agente. Si el visor
        // ensena el desplegable vacio, esta linea dice si el problema es que
        // aqui no se ven o que el mensaje no llego.
        Avisar(opciones, pantallas.Count == 0
            ? $"DXGI no enumero NINGUNA pantalla. {Pantallas.Diagnostico()}"
            : $"Pantallas: {string.Join(" | ", pantallas.Select(p => $"{p.Id}:{p.Nombre} {p.Ancho}x{p.Alto} @{p.X},{p.Y} [{p.Adaptador}]"))}");

        var pedida = _pantalla;
        var elegida = pantallas.FirstOrDefault(p => p.Id == pedida);

        // Sin peticion del tecnico manda el appsettings del agente. La primera
        // vuelta fija _codec para que la comparacion de mas abajo no se dispare
        // sola en el primer medio segundo.
        if (_codec == VideoCodec.Unspecified)
            _codec = opciones.UsarH265 ? VideoCodec.H265 : VideoCodec.H264;

        var codecPedido = _codec;

        // Todas a la vez compone N duplicaciones en una imagen del tamano del
        // escritorio virtual; una sola entrega la textura del duplicador sin
        // copiar nada. La entrada funciona igual con las dos: InputInjector
        // recibe la esquina de lo capturado y traduce a coordenadas virtuales.
        // El escritorio con el que se abrio ESTA captura. Todo lo que venga
        // despues se compara contra el.
        var escritorioCapturado = InputDesktop.NombreDeEntrada();

        // CADA PASO CON SU NOMBRE. Un HRESULT suelto no dice de donde sale.
        var paso = "abrir las capturas";

        // UN FLUJO POR PANTALLA, y el visor los coloca.
        //
        // Antes el modo "todas a la vez" componia los monitores en UNA imagen y
        // se la daba al codificador: 3840x1080 con dos pantallas, que una Intel
        // UHD rechaza con E_INVALIDARG mientras acepta cada una por separado sin
        // pestanear.
        //
        // RustDesk no compone (src/server/display_service.rs): transmite un flujo
        // por monitor con sus coordenadas y los coloca el cliente. Asi su
        // codificador nunca ve mas de una pantalla, y por eso alli funciona.
        //
        // Con una sola pantalla la lista tiene un elemento, asi que el camino de
        // siempre no se bifurca: es el mismo bucle con N=1.
        var flujos = Etiquetar(paso, () => AbrirFlujos(pedida, pantallas, elegida, opciones, cuenta));

        // LO QUE SALIO, no lo que se pidio.
        //
        // Si no habia codificador H.265, Codificar se cayo a H.264. Comparar
        // contra el deseo original dejaria _codec en H.264 y codecPedido en
        // H.265 para siempre, y el bucle de abajo reharia la captura cada medio
        // segundo sin que nada cambiara nunca.
        codecPedido = flujos[0].Codificador.Codec;
        _codec = codecPedido;

        using var pararBombas = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bombas = new List<Thread>();

        try
        {

        // La lista viaja al empezar y en cada cambio de pantalla: es cuando el
        // visor necesita repintar su selector, y cuesta un mensaje.
        salida.TryWrite(new Enviable(null, null, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            Displays = ListaDePantallas(pantallas, pedida)
        }));

        // El inyector trabaja sobre el LIENZO entero, no sobre una pantalla: el
        // visor manda coordenadas normalizadas sobre lo que ve, y lo que ve es la
        // composicion. Con una sola pantalla el lienzo es esa pantalla.
        var lienzo = flujos[0].Lienzo;

        _entrada = new InputInjector(lienzo.Ancho, lienzo.Alto, lienzo.X, lienzo.Y);

        // El hilo de red entrega los acuses por aqui. No toca los flujos
        // directamente por lo mismo de siempre: cada bomba es la unica dueña de
        // lo suyo, y esto solo abre un semaforo.
        _medidas = c =>
        {
            c.DescartesEncoder = flujos.Sum(f => f.Codificador.Dropped);
            c.DescartesCaptura = flujos.Sum(f => f.Captura.Dropped);
        };

        _mostrado = (pantalla, frame) =>
        {
            foreach (var flujo in flujos)
            {
                if (flujo.DisplayId == (int)pantalla && flujo.Desde(frame) is >= 0 and var ms)
                    _verse.Anotar(ms);
            }
        };

        _acusar = (pantalla, frame) =>
        {
            foreach (var flujo in flujos)
            {
                if (flujo.DisplayId == (int)pantalla)
                    flujo.Confirmar(frame);
            }
        };

        opciones.Escribir(
            $"Identidad {System.Security.Principal.WindowsIdentity.GetCurrent().Name}  " +
            $"Escritorio {escritorio.Name}  Flujos {flujos.Count}  " +
            $"Codec {Etiqueta(flujos[0].Codificador.Codec)}  " +
            $"MFT {flujos[0].Codificador.Capabilities.Name}  " +
            $"Hardware {(flujos[0].Codificador.Capabilities.Hardware ? "TRUE" : "FALSE")}  " +
            $"Lienzo {lienzo.Ancho}x{lienzo.Alto}");

        // UN HILO POR PANTALLA, que es como lo hace RustDesk.
        //
        // Sondear las dos duplicaciones en el mismo bucle no funciona, y el dato
        // lo dijo el visor: (p0:403 p1:6). Da igual que la espera sea 0 -- una
        // vuelta cuesta lo que cuesta capturar Y CODIFICAR la primera pantalla,
        // y mientras tanto la segunda no se pide. Con el codificador picando a
        // decenas de milisegundos, la segunda recibe las sobras.
        //
        // Cada display es un productor independiente: su captura, su
        // codificador, su ritmo. DXGI y el MFT quieren un hilo para ellos solos,
        // y asi lo tienen -- que es la misma leccion de la Fase 2, ahora por
        // duplicado.
        bombas.AddRange(flujos.Select(flujo =>
            new Thread(() => Bombear(flujo, salida, cuenta, opciones, lienzo, pararBombas.Token))
            {
                IsBackground = true,
                Name = $"devicehub-pantalla-{flujo.DisplayId}"
            }));

        foreach (var bomba in bombas)
            bomba.Start();

        var avisadoDeCeguera = 0;
        // El bitrate vigente. Arranca en el configurado y de ahi lo mueve el
        // controlador segun lo que aguante la red.
        var bitrateActual = opciones.Bitrate;

        // La semilla es la SUMA de lo que pide cada pantalla por su tamano, no
        // un numero fijo para todo. Antes eran 6 Mbps para cualquier cosa.
        // SIN la calidad, que se aplica al usarla. Guardarla ya multiplicada
        // ataria el reparto a la calidad que hubiera al ABRIR los flujos, y
        // cambiarla desde el visor no llegaria a una sesion en marcha -- que es
        // justo lo que se quiere poder hacer sin rehacer nada.
        var bitrateBaseTotal = Math.Max(flujos.Sum(f => f.BitrateBase), 1);

        if (_bitrateDeseado == 0)
            _bitrateDeseado = Objetivo(bitrateBaseTotal);

        var siguienteRevision = reloj.Elapsed;

        {
            while (reloj.Elapsed < duracion && !cancellationToken.IsCancellationRequested)
            {
                // Cada medio segundo, no cada frame: OpenInputDesktop es una
                // llamada al sistema y a 60 FPS serian 60 por segundo para
                // detectar algo que pasa dos veces al dia.
                if (reloj.Elapsed >= siguienteRevision)
                {
                    siguienteRevision = reloj.Elapsed + TimeSpan.FromMilliseconds(500);

                    // SE COMPARA EL NOMBRE, no el resultado de intentar atarse.
                    //
                    // Esto es lo que fallaba: la decision colgaba de SeguirActivo,
                    // y ese metodo devuelve "no cambio" tanto cuando de verdad no
                    // cambio como cuando no pudo ni ABRIR el escritorio. Con la
                    // PC bloqueada, la captura se quedaba clavada entregando el
                    // ultimo frame del escritorio viejo -- DXGI no da error, sigue
                    // devolviendo lo de antes -- y el bucle no se enteraba nunca.
                    // Se veia el fondo congelado, el raton llegaba, y al
                    // desbloquear seguia congelado.
                    //
                    // Leer el nombre no exige atarse a nada y funciona igual en
                    // Winlogon, asi que cubre la ida Y la vuelta.
                    var entrada = InputDesktop.NombreDeEntrada();

                    if (entrada.Length == 0 && InputDesktop.ErrorAlMirar != avisadoDeCeguera)
                    {
                        avisadoDeCeguera = InputDesktop.ErrorAlMirar;

                        opciones.Escribir(
                            $"No se puede leer el escritorio de entrada (error {avisadoDeCeguera}); " +
                            "la captura no podra seguir los cambios de escritorio");
                    }

                    if (entrada.Length > 0
                        && !entrada.Equals(escritorioCapturado, StringComparison.OrdinalIgnoreCase))
                    {
                        opciones.Escribir(
                            $"El escritorio de entrada paso de {escritorioCapturado} a {entrada}; " +
                            "se rehace la captura");

                        return;
                    }

                    // Cambiar de pantalla invalida el duplicador Y el codificador:
                    // el SPS que el visor tiene descodifica la pantalla anterior.
                    // Se sale y el bucle de fuera rehace la cadena entera con una
                    // config_version nueva, que es justo para lo que existe.
                    if (_pantalla != pedida)
                    {
                        opciones.Escribir($"El tecnico pidio la pantalla {_pantalla}; se rehace la captura");
                        return;
                    }

                    // Igual que la pantalla, y por lo mismo: el SPS que el visor
                    // tiene descodifica el codec anterior.
                    if (_codec != codecPedido)
                    {
                        Avisar(opciones, $"El tecnico pidio {Etiqueta(_codec)}; se rehace la captura");
                        return;
                    }

                    // El portapapeles, en la MISMA cadencia de medio segundo: no
                    // hay evento que avisar y sondearlo mas a menudo seria
                    // pelearse con el resto de la PC por un recurso exclusivo.
                    while (PortapapelesEntrante.TryDequeue(out var pegado))
                        ClipboardBridge.Escribir(pegado);

                    var (copiado, archivosCopiados) = ClipboardBridge.LeerSiCambio();

                    if (copiado is not null)
                    {
                        salida.TryWrite(new Enviable(null, null, new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            Clipboard = new ClipboardText { Text = copiado }
                        }));
                    }

                    // Solo el ANUNCIO: aqui viajan rutas, nunca contenido. Los
                    // bytes salen despues por la transferencia de la Fase 24, y
                    // solo si el tecnico lo pide.
                    if (archivosCopiados.Count > 0)
                    {
                        var aviso = new ClipboardFiles();
                        aviso.Paths.AddRange(archivosCopiados);

                        salida.TryWrite(new Enviable(null, null, new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            ClipboardFiles = aviso
                        }));
                    }
                }

                // El CURSOR, antes de decidir si hay imagen nueva. Mover el
                // raton no cambia el escritorio, asi que este frame se descarta
                // dos lineas mas abajo -- y con el se iria el unico aviso de que
                // el puntero se movio.
                //
                // Va suelto y no dentro del video a proposito: son dos numeros y
                // pueden salir a 60-120 por segundo contra los 20-30 de la
                // imagen. Es lo que hace que el control se SIENTA inmediato
                // aunque la pantalla no lo sea.
                // Lo que el hilo de RED pide va a cada bomba, que es la unica
                // dueña de su codificador. Tocar un MFT desde fuera de su hilo es
                // la familia de cuelgues que costo dos fases entender.
                if (_keyframePedido)
                {
                    _keyframePedido = false;

                    foreach (var flujo in flujos)
                        flujo.KeyframePedido = true;
                }

                if (!_keyframePorPantalla.IsEmpty)
                {
                    // Se vacia entera aunque alguna pantalla ya no exista: una
                    // peticion para un monitor que se desconecto no puede
                    // quedarse ahi para siempre.
                    var pedidas = _keyframePorPantalla.Keys.ToArray();

                    foreach (var quienPide in pedidas)
                        _keyframePorPantalla.TryRemove(quienPide, out _);

                    foreach (var flujo in flujos)
                    {
                        if (Array.IndexOf(pedidas, flujo.DisplayId) >= 0)
                            flujo.KeyframePedido = true;
                    }
                }

                // La calidad puede haber cambiado a media sesion. Reseteando el
                // objetivo a cero, el visor pide que se recalcule con la base
                // nueva sin rehacer el codificador.
                if (_bitrateDeseado == 0)
                    _bitrateDeseado = Objetivo(bitrateBaseTotal);

                if (_bitrateDeseado > 0)
                {
                    // Repartido POR TAMANO y no a partes iguales: dos pantallas
                    // comparten el mismo cable, pero una de 1280x1024 y una 4K
                    // no piden lo mismo.
                    foreach (var flujo in flujos)
                    {
                        flujo.BitrateDeseado = (int)Math.Max(
                            (long)_bitrateDeseado * flujo.BitrateBase / bitrateBaseTotal,
                            ControlBitrate.Minimo);
                    }
                }

                // El video ya no se produce aqui: cada flujo tiene su hilo. Este
                // se queda con lo COMPARTIDO -- escritorio, portapapeles,
                // pantalla elegida -- y no tiene prisa.
                cancellationToken.WaitHandle.WaitOne(50);
            }

            cuenta.DescartesEncoder = flujos.Sum(f => f.Codificador.Dropped);
            cuenta.DescartesCaptura = flujos.Sum(f => f.Captura.Dropped);
        }
        }
        finally
        {
            // Nadie se queda con una tecla hundida porque la sesion terminara.
            _entrada?.SoltarTodo();

            _acusar = null;
            _mostrado = null;
            _medidas = null;

            // Primero se paran las bombas y se espera: disponer una captura
            // mientras su hilo esta dentro de AcquireNextFrame es como se cuelga
            // DXGI sin dejar rastro.
            pararBombas.Cancel();

            foreach (var bomba in bombas)
                bomba.Join(TimeSpan.FromSeconds(3));

            foreach (var flujo in flujos)
                flujo.Dispose();
        }
    }

    /// <summary>
    /// Produce el video de UNA pantalla, de principio a fin.
    ///
    /// Hilo propio y no una vuelta de un bucle compartido: capturar y codificar
    /// cuesta decenas de milisegundos, y en un solo hilo la segunda pantalla solo
    /// recibe lo que sobra de la primera. Con dos monitores eso se veia como una
    /// imagen congelada, no como una imagen lenta.
    /// </summary>
    private static void Bombear(
        Flujo flujo, System.Threading.Channels.ChannelWriter<Enviable> salida, Contadores cuenta,
        RelayOptions opciones, Lienzo lienzo, CancellationToken cancellationToken)
    {
        try
        {
            var siguienteFrame = Stopwatch.GetTimestamp();
            var ultimoFrame = Stopwatch.GetTimestamp();
            var bitrateActual = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                Marcar(ref siguienteFrame, cancellationToken);

                // El CURSOR, antes de decidir si hay imagen nueva. Mover el raton
                // no cambia el escritorio, asi que ese frame se descarta -- y con
                // el se iria el unico aviso de que el puntero se movio.
                if (flujo.Captura.TomarCursor() is { } puntero)
                {
                    var aviso = new CursorUpdate
                    {
                        X = (flujo.LayoutX + puntero.X * flujo.Captura.Width) / lienzo.Ancho,
                        Y = (flujo.LayoutY + puntero.Y * flujo.Captura.Height) / lienzo.Alto,
                        Visible = puntero.Visible
                    };

                    // La forma solo viaja cuando cambio. Reenviarla en cada
                    // movimiento serian kilobytes por cada pixel recorrido.
                    if (puntero.Bgra is not null)
                    {
                        aviso.Shape = new CursorShape
                        {
                            ShapeId = puntero.FormaId,
                            Width = (uint)puntero.Ancho,
                            Height = (uint)puntero.Alto,
                            HotspotX = (uint)puntero.HotspotX,
                            HotspotY = (uint)puntero.HotspotY,
                            Bgra = Google.Protobuf.ByteString.CopyFrom(puntero.Bgra)
                        };
                    }

                    salida.TryWrite(new Enviable(null, null, new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = opciones.SesionId,
                        Cursor = aviso
                    }));
                }

                if (flujo.KeyframePedido)
                {
                    flujo.KeyframePedido = false;
                    flujo.Codificador.ForzarKeyframe();

                    // La config va DELANTE del IDR: si el visor perdio el SPS, un
                    // keyframe suelto no le sirve de nada.
                    flujo.ConfigEnviada = false;
                }

                if (flujo.BitrateDeseado != bitrateActual && flujo.BitrateDeseado > 0)
                {
                    if (flujo.Codificador.CambiarBitrate(flujo.BitrateDeseado))
                        opciones.Escribir($"Pantalla {flujo.DisplayId}: bitrate {flujo.BitrateDeseado / 1000} kbps");

                    bitrateActual = flujo.BitrateDeseado;
                }

                IReadOnlyList<EncodedFrame> producidos;

                // SI DXGI NO ARRANCA, RELEVO POR GDI.
                //
                // Es lo que hace RustDesk: cuenta los WouldBlock seguidos -- que
                // es nuestro WAIT_TIMEOUT -- y a la tercera cambia ESE capturador
                // a GDI ("No image, fall back to gdi").
                //
                // SOLO ANTES DEL PRIMER FRAME, que es el detalle que importa y
                // que RustDesk tambien respeta: en cuanto llega uno, ponen el
                // contador a cero y no vuelven a mirarlo. Una pantalla quieta no
                // entrega frames durante minutos y esta perfectamente sana --
                // degradarla a GDI ahi seria cambiar la GPU por copias de CPU a
                // 10 FPS sin que nada estuviera roto.
                //
                // Por tiempo y no por cuenta porque con la espera a 0 los nulos
                // son constantes: tres seguidos no significan nada, tres
                // segundos sin haber arrancado nunca si.
                if (!flujo.Relevada && !flujo.Arranco
                    && Stopwatch.GetTimestamp() - ultimoFrame > Stopwatch.Frequency * 3)
                {
                    flujo.Relevada = true;

                    Avisar(opciones,
                        $"DXGI no ha entregado ni un frame de la pantalla {flujo.DisplayId} en 3 s; se releva por GDI");

                    if (Relevar(flujo, opciones, cuenta))
                    {
                        ultimoFrame = Stopwatch.GetTimestamp();
                        continue;
                    }
                }

                // El frame DXGI se suelta ANTES de esperar por nada. Encolar
                // puede bloquear si la red va por detras, y quedarse la
                // superficie duplicada mientras tanto es lo que no se hace.
                using (var frame = flujo.Captura.CaptureAsync(cancellationToken).GetAwaiter().GetResult())
                {
                    if (frame is null || !frame.DesktopChanged)
                        continue;

                    ultimoFrame = Stopwatch.GetTimestamp();
                    flujo.Arranco = true;

                    Interlocked.Increment(ref cuenta.Capturados);
                    producidos = flujo.Codificador.Encode(frame, cancellationToken);
                }

                foreach (var frameCodificado in producidos)
                {
                    Interlocked.Increment(ref cuenta.Codificados);

                    if (frameCodificado.IsKeyFrame)
                        Interlocked.Increment(ref cuenta.Claves);

                    VideoConfig? config = null;

                    // El SPS/PPS sale dentro del primer IDR. Se saca UNA vez y se
                    // manda en VideoConfig; a partir de ahi el visor lo conserva.
                    if (!flujo.ConfigEnviada && frameCodificado.IsKeyFrame)
                    {
                        var parametros = H264AnnexB.ParameterSets(
                            frameCodificado.Payload, flujo.Codificador.Codec == VideoCodec.H265);

                        if (parametros.Length == 0)
                            continue;   // todavia no; el siguiente IDR los traera

                        flujo.ConfigEnviada = true;

                        config = new VideoConfig
                        {
                            ConfigVersion = flujo.Version,
                            Codec = flujo.Codificador.Codec,
                            Width = (uint)frameCodificado.Width,
                            Height = (uint)frameCodificado.Height,
                            FramesPerSecond = (uint)FpsDeclarado(opciones),
                            BitrateBitsPerSecond = (uint)flujo.BitrateBase,
                            ParameterSets = Google.Protobuf.ByteString.CopyFrom(parametros),
                            VisibleWidth = (uint)frameCodificado.Width,
                            VisibleHeight = (uint)frameCodificado.Height,

                            DisplayId = (uint)flujo.DisplayId,
                            LayoutX = (uint)flujo.LayoutX,
                            LayoutY = (uint)flujo.LayoutY,
                            CanvasWidth = (uint)lienzo.Ancho,
                            CanvasHeight = (uint)lienzo.Alto
                        };
                    }

                    if (!flujo.ConfigEnviada)
                        continue;   // sin configuracion no hay nada descodificable

                    // El numero de frame es de la SESION y lo comparten los
                    // hilos: los reensambladores descartan como atrasado todo id
                    // menor o igual al ultimo completado, asi que dos productores
                    // numerando por su cuenta se anularian entre ellos.
                    var numero = (ulong)Interlocked.Increment(ref _frameDeLaSesion);

                    var grupo = VideoFraming.Split(
                        numero, frameCodificado.IsKeyFrame, flujo.Version,
                        frameCodificado.TimestampUs, frameCodificado.Payload);

                    foreach (var trozo in grupo.Chunks)
                        trozo.DisplayId = (uint)flujo.DisplayId;

                    // EL EMISOR NO SE ADELANTA AL RECEPTOR.
                    //
                    // Se arma el freno ANTES de escribir, no despues: el acuse
                    // puede llegar mientras la escritura esta en curso, y
                    // rearmarlo despues lo borraria y nos dejaria esperando un
                    // acuse que ya vino.
                    var frenar = _visorAcusa;

                    if (frenar)
                    {
                        Volatile.Write(ref flujo.EnVuelo, (long)numero);
                        flujo.Acuse.Reset();
                    }

                    // Siempre, haya freno o no: la medida de cuanto tarda en
                    // verse no depende de si el visor sabe frenar.
                    flujo.Apuntar(numero);

                    salida.WriteAsync(new Enviable(config, grupo), cancellationToken)
                        .AsTask().GetAwaiter().GetResult();

                    // Y aqui se espera. Parece que frena y hace lo contrario:
                    // sin esto caben doce frames entre la cola del host y la del
                    // relay -- 600 ms de escritorio viejo a 20 FPS -- y un frame
                    // que espera turno ya llega tarde.
                    //
                    // Es el VideoFrameController de RustDesk: alli el bucle de
                    // captura no coge el siguiente hasta que TODOS los clientes
                    // confirman el anterior, y por eso su latencia esta acotada
                    // por construccion en vez de por ajuste.
                    if (frenar && !flujo.Acuse.Wait(EsperaDeAcuseMs, cancellationToken))
                        Interlocked.Increment(ref cuenta.AcusesPerdidos);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Avisar(opciones, $"La pantalla {flujo.DisplayId} dejo de emitir: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Token de reconexion vigente. Solo en RAM: ni disco, ni log, ni
    /// argumento.</summary>
    private static string? _tokenReconexion;

    /// <summary>
    /// Entrada remota. Estatico como el resto del estado de esta clase: por
    /// diseno hay UNA sesion por proceso, y el agente lanza un RemoteHost nuevo
    /// para cada una.
    /// </summary>
    /// <summary>
    /// Duerme hasta que toque el siguiente frame.
    ///
    /// Si se va TARDE no se acumula deuda: se reengancha al reloj actual. Sin
    /// eso, una racha lenta dejaria el objetivo tan atrasado que despues
    /// capturaria a rafagas para "recuperar" frames que ya no le interesan a
    /// nadie.
    /// </summary>
    private static void Marcar(ref long siguiente, CancellationToken cancellationToken)
    {
        var intervalo = Stopwatch.Frequency / Math.Clamp(_fpsDeseado, ControlFps.Minimo, ControlFps.Maximo);
        var falta = siguiente - Stopwatch.GetTimestamp();

        if (falta > 0)
            cancellationToken.WaitHandle.WaitOne((int)(falta * 1000 / Stopwatch.Frequency));

        siguiente = Math.Max(siguiente + intervalo, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Cuenta algo AL TECNICO, no solo al log de la maquina.
    ///
    /// Lo importante va por los dos sitios: el log queda para despues y esto se
    /// ve ahora. Llevabamos un dia entero diagnosticando a ciegas porque todo lo
    /// que el host sabia se quedaba en un visor de eventos que nadie iba a abrir.
    /// </summary>
    private static Action<string>? _avisar;

    private static void Avisar(RelayOptions opciones, string texto)
    {
        opciones.Escribir(texto);
        _avisar?.Invoke(texto);
    }

    /// <summary>Las medidas periodicas, al visor y por su propio carril.
    ///
    /// Lo que sabe el host -- cuanto captura, cuanto codifica, cuanto descarta
    /// el codificador -- decide donde esta el techo de una sesion, y hasta ahora
    /// solo acababa en el visor de eventos de la PC de planta. O sea que para
    /// saber si el cuello era la iGPU habia que ir hasta la maquina.</summary>
    private static Action<string>? _medir;

    /// <summary>
    /// Refresca los contadores que viven dentro de los flujos.
    ///
    /// Existe porque sin esto los descartes se leian UNA sola vez, al terminar
    /// la captura, y la linea de cada 2 s imprimia el cero con el que nacieron.
    /// O sea que "descartes 0+0" no era una medida: era un contador sin
    /// rellenar, y con el se concluyo que el codificador de la PC de planta no
    /// era el cuello de botella. No habia datos para decir eso.
    /// </summary>
    private static Action<Contadores>? _medidas;

    /// <summary>
    /// Ejecuta un paso y, si falla, le pone NOMBRE a la excepcion.
    ///
    /// Un HRESULT suelto no es un diagnostico: E_INVALIDARG puede venir de la
    /// duplicacion, del convertidor a NV12, del tipo de entrada del codificador o
    /// del de salida, y cada uno se arregla de una forma. La etiqueta convierte
    /// "algo esta mal" en "esto esta mal".
    /// </summary>
    private static T Etiquetar<T>(string paso, Func<T> accion)
    {
        try
        {
            return accion();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"al {paso}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reduce a un ancho maximo conservando la proporcion, con las dos medidas
    /// PARES: H.264 codifica en macrobloques y no traga dimensiones impares.
    /// </summary>
    private static (int Ancho, int Alto) Encajar(int ancho, int alto, int maximo)
    {
        if (ancho <= maximo)
            return (ancho, alto);

        var escala = maximo / (double)ancho;

        return (Par(maximo), Par((int)Math.Round(alto * escala)));
    }

    private static int Par(int valor) => valor % 2 == 0 ? valor : valor + 1;

    /// <summary>La caja que ocupan TODAS las pantallas del envio, en coordenadas
    /// de Windows. Es sobre esto sobre lo que el visor manda el raton.</summary>
    private readonly record struct Lienzo(int X, int Y, int Ancho, int Alto);

    /// <summary>
    /// Una pantalla en camino: su captura, su codificador y donde va colocada.
    ///
    /// Cada flujo lleva su PROPIA config_version. Con varias pantallas cada una
    /// tiene su SPS, y compartir la version haria que el visor descodificara los
    /// frames de una con los parametros de la otra -- que no da error, da imagen
    /// corrupta.
    /// </summary>
    private sealed class Flujo : IDisposable
    {
        public required int DisplayId { get; init; }
        public required Pantalla? Info { get; init; }

        // Captura, codificador y version dejan de ser fijos: si DXGI se queda
        // mudo en esta pantalla, se releva por GDI y eso son los tres a la vez.
        public required IScreenCapture Captura { get; set; }
        public required H264Encoder Codificador { get; set; }
        public required uint Version { get; set; }

        public required int LayoutX { get; init; }
        public required int LayoutY { get; init; }
        public required Lienzo Lienzo { get; init; }

        /// <summary>Ya se relevo una vez. No se vuelve: GDI es el ultimo
        /// recurso, y girar entre los dos seria peor que quedarse en uno.</summary>
        public bool Relevada { get; set; }

        /// <summary>Esta duplicacion ha entregado al menos un frame. A partir de
        /// ahi DXGI funciona y el silencio solo significa que nadie toca esa
        /// pantalla.</summary>
        public bool Arranco { get; set; }

        public bool ConfigEnviada { get; set; }

        /// <summary>Lo pide el hilo de red y lo atiende la bomba de ESTA
        /// pantalla, que es la unica dueña de su codificador.</summary>
        public volatile bool KeyframePedido;

        public int BitrateDeseado;

        /// <summary>Lo que le toca a ESTA pantalla por su tamano. Es el reparto
        /// justo: un monitor de 1280x1024 no necesita lo mismo que uno 4K, y
        /// dividir un total entre el numero de pantallas les daba igual.</summary>
        public required int BitrateBase { get; init; }

        /// <summary>Se abre cuando el visor confirma el frame en vuelo.</summary>
        public readonly ManualResetEventSlim Acuse = new(false);

        /// <summary>Frame mandado y sin confirmar. 0 = ninguno.</summary>
        public long EnVuelo;

        /// <summary>
        /// Cuando salio cada uno de los ultimos frames.
        ///
        /// Hacen falta VARIOS y no uno: el acuse de MOSTRADO llega despues del
        /// de recibido, y para entonces el freno ya solto y el frame siguiente
        /// va de camino. Con un solo hueco, la medida que interesa -- la que
        /// incluye descodificar y pintar -- seria justo la que nunca encuentra
        /// su marca.
        /// </summary>
        private readonly (ulong Frame, long Ticks)[] _salidas = new (ulong, long)[8];
        private int _siguienteSalida;

        public void Apuntar(ulong frame)
        {
            lock (_salidas)
            {
                _salidas[_siguienteSalida] = (frame, Stopwatch.GetTimestamp());
                _siguienteSalida = (_siguienteSalida + 1) % _salidas.Length;
            }
        }

        /// <summary>Milisegundos desde que salio ese frame, o negativo si ya no
        /// se recuerda.</summary>
        public double Desde(ulong frame)
        {
            lock (_salidas)
            {
                foreach (var (id, ticks) in _salidas)
                {
                    if (id == frame)
                        return (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
                }
            }

            return -1;
        }

        /// <summary>Un acuse ATRASADO no abre el freno del frame actual: si el
        /// del 100 llega cuando ya se espera el 102, abrirlo seria adelantarse
        /// justo lo que este freno existe para impedir.</summary>
        public void Confirmar(ulong frame)
        {
            if ((long)frame >= Volatile.Read(ref EnVuelo))
                Acuse.Set();
        }

        public void Dispose()
        {
            Acuse.Dispose();
            Codificador.Dispose();
            Captura.Dispose();
        }
    }

    /// <summary>
    /// Abre un flujo por pantalla, o uno solo si se pidio una concreta.
    ///
    /// El modo "todas a la vez" ya NO compone: cada monitor va por su cuenta y
    /// el visor los coloca con layout_x/y. Es lo que hace RustDesk, y la razon
    /// es que componiendo el codificador recibe la suma de los anchos y una iGPU
    /// lo rechaza -- mientras acepta cada pantalla por separado.
    ///
    /// Si al abrir una de las pantallas falla, se sigue con las que si: media
    /// composicion es mejor que ninguna sesion, y el aviso dice cual falto.
    /// </summary>
    private static List<Flujo> AbrirFlujos(
        int pedida, IReadOnlyList<Pantalla> pantallas, Pantalla? elegida,
        RelayOptions opciones, Contadores cuenta)
    {
        if (pedida != Pantallas.Todas || pantallas.Count <= 1)
        {
            var unica = Abrir(pedida, elegida, opciones);

            cuenta.ConfigVersionCompartida = (int)cuenta.ConfigVersion + 1;

            return
            [
                new Flujo
                {
                    DisplayId = pedida,
                    Info = elegida,
                    Captura = unica,
                    BitrateBase = ControlBitrate.PorResolucion(unica.Width, unica.Height, 1.0),
                    Codificador = Codificar(
                        unica.Device, unica.Width, unica.Height,
                        unica.AdapterLuid, unica.AdapterVendorId, opciones),
                    Version = ++cuenta.ConfigVersion,
                    LayoutX = 0,
                    LayoutY = 0,
                    Lienzo = new Lienzo(unica.DesktopLeft, unica.DesktopTop, unica.Width, unica.Height)
                }
            ];
        }

        var (x, y, ancho, alto) = Pantallas.Envolvente(
            [.. pantallas.Select(p => (p.X, p.Y, p.Ancho, p.Alto))]);

        var lienzo = new Lienzo(x, y, ancho, alto);
        var flujos = new List<Flujo>(pantallas.Count);

        foreach (var pantalla in pantallas)
        {
            IScreenCapture? captura = null;

            try
            {
                captura = new DxgiDesktopCapture(pantalla.AdapterIndex, pantalla.OutputIndex);

                flujos.Add(new Flujo
                {
                    DisplayId = pantalla.Id,
                    Info = pantalla,
                    Captura = captura,
                    BitrateBase = ControlBitrate.PorResolucion(captura.Width, captura.Height, 1.0),
                    Codificador = Codificar(
                        captura.Device, captura.Width, captura.Height,
                        captura.AdapterLuid, captura.AdapterVendorId, opciones),
                    Version = ++cuenta.ConfigVersion,

                    // Coordenadas RELATIVAS al lienzo: el monitor de la izquierda
                    // tiene X negativa en Windows y aqui tiene que caer en 0.
                    LayoutX = pantalla.X - x,
                    LayoutY = pantalla.Y - y,
                    Lienzo = lienzo
                });
            }
            catch (Exception ex)
            {
                captura?.Dispose();

                Avisar(opciones,
                    $"No se pudo abrir {pantalla.Nombre}: {ex.Message}. Se sigue sin esa pantalla.");
            }
        }

        if (flujos.Count == 0)
            throw new ScreenCaptureUnavailableException("No se pudo abrir ninguna de las pantallas.");

        // La cuenta compartida arranca donde va la normal. Sin esto, el primer
        // relevo estrenaria una version que otra pantalla ya esta usando, y el
        // visor descodificaria sus frames con el SPS equivocado -- que no da
        // error, da imagen corrupta.
        cuenta.ConfigVersionCompartida = (int)cuenta.ConfigVersion;

        // Con varias pantallas NADIE espera: se sondean todas en el mismo hilo,
        // asi que una quieta se llevaria 100 ms de cada vuelta y dejaria a las
        // demas a 5 FPS aunque fueran las unicas moviendose. El freno del ritmo
        // ya evita el bucle ocupado.
        foreach (var flujo in flujos)
        {
            if (flujo.Captura is DxgiDesktopCapture dxgi)
                dxgi.EsperaMs = 0;
        }

        return flujos;
    }

    /// <summary>
    /// Cambia una pantalla de DXGI a GDI sin cortar la sesion.
    ///
    /// Se rehace TAMBIEN el codificador porque el capturador nuevo trae su propio
    /// dispositivo D3D, y un MFT atado al dispositivo viejo no puede recibir sus
    /// texturas. Y con codificador nuevo hay SPS nuevo, asi que estrena
    /// config_version: el visor tirara su decodificador de esa pantalla y montara
    /// otro, que es justo para lo que la version existe.
    /// </summary>
    /// <summary>
    /// Abre el codificador del codec pedido, y si no lo hay se cae a H.264.
    ///
    /// EL RESPALDO NO ES OPCIONAL. Un Windows Server sin Media Foundation, una
    /// GPU vieja sin HEVC o una iGPU que lo tenga solo para descodificar son
    /// casos reales, y con el interruptor puesto por descuido dejarian sin
    /// control remoto a una PC de planta. Mejor H.264 y un aviso.
    /// </summary>
    /// <summary>
    /// Los FPS que se le DECLARAN al codificador.
    ///
    /// Tienen que ser los que de verdad va a recibir, no los que a alguien le
    /// gustaria. El control de tasa divide el presupuesto entre este numero, y
    /// declarar 60 mientras le llegan 19 reparte los bits entre cuarenta y un
    /// frames que no existen: el resultado eran 0.25 Mbps de un objetivo de
    /// 3109, o sea el 8 %, y una imagen blanda en cuanto algo se movia.
    ///
    /// Es el techo del control de ritmo y no el ritmo actual: cambiar
    /// MF_MT_FRAME_RATE obliga a reconfigurar la salida -- SPS nuevo,
    /// config_version nueva, el visor tirando su decodificador -- y eso no se
    /// puede hacer cada vez que la red respira.
    /// </summary>
    private static int FpsDeclarado(RelayOptions opciones)
        => Math.Clamp(opciones.Fps, ControlFps.Minimo, ControlFps.Maximo);

    /// <summary>La base de todas las pantallas por la calidad pedida, acotada a
    /// lo que el codificador acepta.</summary>
    private static int Objetivo(int baseTotal)
        => (int)Math.Clamp(baseTotal * _calidad, ControlBitrate.Minimo, ControlBitrate.Maximo);

    internal static string Etiqueta(VideoCodec codec)
        => codec == VideoCodec.H265 ? "H.265" : "H.264";

    private static H264Encoder Codificar(
        ID3D11Device device, int ancho, int alto, Vortice.Luid luid, uint vendor,
        RelayOptions opciones)
    {
        var bitrate = ControlBitrate.PorResolucion(ancho, alto, _calidad);
        var fps = FpsDeclarado(opciones);

        if (_codec == VideoCodec.H265)
        {
            try
            {
                return new H264Encoder(
                    device, ancho, alto, fps, bitrate, luid, vendor,
                    codec: VideoCodec.H265);
            }
            catch (Exception ex)
            {
                Avisar(opciones, $"Sin codificador H.265 ({ex.Message.Split('\n')[0]}); se sigue en H.264.");

                // Se apunta el resultado REAL. Si no se corrigiera, el bucle de
                // fuera veria H.265 pedido y H.264 en marcha y rehariam la
                // captura cada medio segundo para siempre.
                _codec = VideoCodec.H264;
            }
        }

        return new H264Encoder(device, ancho, alto, fps, bitrate, luid, vendor);
    }

    private static bool Relevar(Flujo flujo, RelayOptions opciones, Contadores cuenta)
    {
        if (flujo.Info is not { } info)
            return false;

        try
        {
            var gdi = new GdiDesktopCapture(info.X, info.Y, info.Ancho, info.Alto);

            var codificador = new H264Encoder(
                gdi.Device, gdi.Width, gdi.Height, FpsDeclarado(opciones),
                Math.Max(flujo.BitrateDeseado, ControlBitrate.Minimo),
                gdi.AdapterLuid, gdi.AdapterVendorId,
                codec: flujo.Codificador.Codec);

            // El viejo se suelta DESPUES de que el nuevo exista: si crear el
            // nuevo falla, la pantalla sigue con lo que tenia en vez de quedarse
            // sin nada.
            var anterior = flujo.Captura;
            var anteriorCodificador = flujo.Codificador;

            flujo.Captura = gdi;
            flujo.Codificador = codificador;
            flujo.Version = (uint)Interlocked.Increment(ref cuenta.ConfigVersionCompartida);
            flujo.ConfigEnviada = false;

            anteriorCodificador.Dispose();
            anterior.Dispose();

            Avisar(opciones,
                $"Pantalla {flujo.DisplayId} en GDI: {gdi.Width}x{gdi.Height} desde @{info.X},{info.Y}");

            return true;
        }
        catch (Exception ex)
        {
            Avisar(opciones, $"El respaldo GDI de la pantalla {flujo.DisplayId} tampoco pudo: {ex.Message}");
            return false;
        }
    }

    private static void Medir(RelayOptions opciones, string texto)
    {
        opciones.Escribir(texto);
        _medir?.Invoke(texto);
    }

    private static long Ahora()
        => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    private static InputInjector? _entrada;

    /// <summary>
    /// Abre la captura, con GDI de respaldo cuando DXGI se niega.
    ///
    /// DXGI ata el dispositivo D3D al escritorio al CREARLO, asi que en el
    /// escritorio seguro -- la pantalla de bloqueo, el login, UAC -- no esta
    /// disponible y no hay forma de convencerlo desde dentro del proceso. GDI no
    /// ata nada: se re-atacha el hilo y BitBlt captura lo que haya.
    ///
    /// Es la misma pareja que usa Chrome Remote Desktop, ruta rapida y respaldo.
    /// El respaldo copia por CPU y sube a la GPU -- caro -- pero solo se usa
    /// mientras alguien escribe una contrasena en una pantalla quieta.
    /// </summary>
    private static IScreenCapture Abrir(int pedida, Pantalla? elegida, RelayOptions opciones)
    {
        // SE DECIDE POR EL NOMBRE DEL ESCRITORIO, no esperando a que DXGI falle.
        //
        // Esperar al fallo no sirve: en Winlogon la duplicacion NO da error, se
        // queda entregando el ultimo frame del escritorio anterior. El visor
        // ensenaba el fondo de escritorio congelado mientras la PC pedia la
        // contrasena, y el respaldo no llegaba a entrar nunca porque nada habia
        // fallado formalmente.
        var escritorio = InputDesktop.NombreDeEntrada();

        if (!string.IsNullOrEmpty(escritorio)
            && !escritorio.Equals(InputDesktop.Normal, StringComparison.OrdinalIgnoreCase))
        {
            opciones.Escribir($"El escritorio de entrada es {escritorio}; se captura con el respaldo GDI");

            var seguro = new GdiDesktopCapture();
            opciones.Escribir($"Respaldo GDI activo sobre {seguro.Output}  {seguro.Width}x{seguro.Height}");

            return seguro;
        }

        try
        {
            return pedida == Pantallas.Todas
                ? new VirtualDesktopCapture(opciones.Adapter)
                : new DxgiDesktopCapture(
                    elegida?.AdapterIndex ?? opciones.Adapter,
                    elegida?.OutputIndex ?? opciones.Output);
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            // Que DXGI no pueda NO es el final: en el escritorio seguro es lo
            // esperado. Se dice cual fue el motivo -- si el respaldo tambien
            // falla, hacen falta los dos errores para saber que pasa.
            Avisar(opciones, $"DXGI no puede capturar aqui ({ex.Message}); se pasa al respaldo GDI");

            var gdi = new GdiDesktopCapture();

            opciones.Escribir(
                $"Respaldo GDI activo sobre {gdi.Output}  {gdi.Width}x{gdi.Height}");

            return gdi;
        }
    }

    /// <summary>Subidas en curso. Una por sesion, y la sesion es el proceso.</summary>
    private static readonly Files.FileService _archivos = new();

    private static DisplayList ListaDePantallas(IReadOnlyList<Pantalla> pantallas, int actual)
    {
        var lista = new DisplayList { Current = actual };

        foreach (var p in pantallas)
        {
            lista.Displays.Add(new DisplayInfo
            {
                Id = p.Id,
                Name = p.Nombre,
                Adapter = p.Adaptador,
                X = p.X,
                Y = p.Y,
                Width = p.Ancho,
                Height = p.Alto,
                Primary = p.Primaria
            });
        }

        return lista;
    }

    /// <summary>
    /// Entrada recibida y todavia sin aplicar. La llena el hilo de red y la vacia
    /// el de captura, que es el unico atado al escritorio activo.
    ///
    /// Sin tope y sin descarte: se drena ENTERA en cada vuelta del bucle de
    /// captura -- 60 veces por segundo -- asi que no crece. Y descartar aqui
    /// dejaria un KeyUp sin su KeyDown, que es como se queda una tecla pegada al
    /// otro lado.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<InputEvent> Pendientes = new();

    /// <summary>
    /// El mismo escritor para todo. gRPC no admite dos escrituras a la vez en el
    /// stream de peticion, y aqui escriben dos sitios: el bucle de video y la
    /// respuesta al Ping.
    /// </summary>
    private static readonly SemaphoreSlim Pluma = new(1, 1);

    private static async Task EscribirAsync(
        AsyncDuplexStreamingCall<RemotePacket, RemotePacket> llamada, RemotePacket paquete,
        CancellationToken cancellationToken)
    {
        await Pluma.WaitAsync(cancellationToken);

        try
        {
            await llamada.RequestStream.WriteAsync(paquete, cancellationToken);
        }
        finally
        {
            Pluma.Release();
        }
    }

    /// <summary>
    /// Lo que manda el relay de vuelta.
    ///
    /// Que un SessionClose CORTE la captura es media Fase 7: el host no puede
    /// quedarse codificando la pantalla de alguien despues de que su sesion
    /// termine. La otra media es que el proceso muera cuando el stream se rompe,
    /// y de eso se encarga el bucle de fuera al ver que la lectura acabo.
    /// </summary>
    private static async Task LeerAsync(
        AsyncDuplexStreamingCall<RemotePacket, RemotePacket> llamada, RelayOptions opciones,
        CancellationTokenSource cancelacion)
    {
        try
        {
            // Un paquete suelto, con la escritura ya serializada por Pluma.
            //
            // BLOQUEA a proposito. Los trozos de un archivo tienen que salir en
            // orden, y el semaforo garantiza exclusion pero no turno: si el
            // emisor no espera a que el suyo salga, dos trozos consecutivos
            // pueden cruzarse y el archivo llega corrupto sin que nadie lo note.
            //
            // ponytail: mientras un trozo espera hueco de flujo, este hilo deja
            // de leer. Es contrapresion, no un cuelgue -- las dos direcciones de
            // HTTP/2 son independientes. Si algun dia estorba, la salida es una
            // cola propia para archivos, no quitar la espera.
            void Suelto(RemotePacket salida)
                => EscribirAsync(llamada, salida, cancelacion.Token).GetAwaiter().GetResult();

            while (await llamada.ResponseStream.MoveNext(cancelacion.Token))
            {
                var paquete = llamada.ResponseStream.Current;

                switch (paquete.PayloadCase)
                {
                    case RemotePacket.PayloadOneofCase.Pong:
                        // NUESTRO reloj de ida y vuelta. Restar marcas de dos PCs
                        // distintas da un numero inventado: sus relojes
                        // monotonicos no son comparables.
                        _retraso.Anotar((Ahora() - paquete.Pong.SentAtUs) / 1000.0);
                        break;

                    case RemotePacket.PayloadOneofCase.Ping:
                        // Se devuelve la marca TAL CUAL. El RTT lo calcula quien
                        // pregunto, con su propio reloj: restar tiempos de dos
                        // PCs distintas da un numero inventado.
                        await EscribirAsync(llamada, new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            Pong = new Pong { SentAtUs = paquete.Ping.SentAtUs }
                        }, cancelacion.Token);

                        break;

                    case RemotePacket.PayloadOneofCase.Close:
                        opciones.Escribir($"El relay cerro la sesion: {paquete.Close.Reason} {paquete.Close.Detail}");

                        // Se suelta el token: esto es un cierre en orden, no un
                        // corte. Sin esto el host volveria a llamar a una puerta
                        // que acaban de cerrarle en la cara.
                        _tokenReconexion = null;

                        await cancelacion.CancelAsync();
                        return;

                    case RemotePacket.PayloadOneofCase.Error:
                        opciones.Escribir($"Relay: {paquete.Error.Code} {paquete.Error.Detail}");
                        break;

                    case RemotePacket.PayloadOneofCase.HelloAccepted:
                        // El token se guarda SOLO en memoria y no se imprime. Es
                        // lo que permitiria volver a la misma sesion tras un
                        // microcorte sin gastar un ticket nuevo.
                        _tokenReconexion = paquete.HelloAccepted.ReconnectToken;

                        opciones.Escribir(
                            "Sesion autenticada. Reconexion admitida hasta " +
                            $"{DateTimeOffset.FromUnixTimeMilliseconds(paquete.HelloAccepted.ReconnectUntilUs / 1000):HH:mm:ss}.");

                        break;

                    case RemotePacket.PayloadOneofCase.Input:
                        // NO se aplica aqui. Este es el hilo de red, y SendInput
                        // inyecta en el escritorio al que esta atado el hilo que
                        // llama. Se encola y lo aplica el hilo de captura, que ya
                        // esta atado al escritorio activo.
                        //
                        // Silencioso a proposito: cada movimiento del raton
                        // pasaria por aqui, y un log por evento convierte el
                        // visor de eventos en el cuello de botella de la sesion.
                        Pendientes.Enqueue(paquete.Input);
                        break;

                    case RemotePacket.PayloadOneofCase.HostAction:
                        // Estas si se registran, al contrario que el raton: no son
                        // trafico continuo y son de las pocas cosas que el tecnico
                        // hace y quiere ver confirmadas.
                        //
                        // Se atienden en el hilo de RED y no en el de captura como
                        // la entrada: ninguna es SendInput, asi que ninguna
                        // depende del escritorio al que este atado el hilo. Y
                        // pasarlas por la cola las retrasaria hasta el proximo
                        // frame, que con el escritorio quieto puede no llegar.
                        opciones.Escribir(paquete.HostAction.Kind switch
                        {
                            HostAction.Types.Kind.HostActionCtrlAltDel =>
                                SecureAttention.Enviar(out var detalle)
                                    ? detalle
                                    : $"No se pudo enviar Ctrl+Alt+Supr: {detalle}",

                            HostAction.Types.Kind.HostActionLock => HostActions.Bloquear(),
                            HostAction.Types.Kind.HostActionBlockInput => HostActions.Congelar(true),
                            HostAction.Types.Kind.HostActionUnblockInput => HostActions.Congelar(false),
                            HostAction.Types.Kind.HostActionReboot => HostActions.Reiniciar(),

                            // NO se suelta aqui. Este es el hilo de red, y
                            // SendInput inyecta en el escritorio al que esta
                            // atado el hilo que llama: se anota y lo hace el de
                            // captura, igual que el resto de la entrada.
                            HostAction.Types.Kind.HostActionReleaseInput => Anotar(ref _soltarEntrada),

                            _ => $"Accion desconocida: {paquete.HostAction.Kind}"
                        });

                        break;

                    case RemotePacket.PayloadOneofCase.FileListRequest:
                        Suelto(new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            FileList = FileService.Listar(paquete.FileListRequest.Path)
                        });

                        break;

                    case RemotePacket.PayloadOneofCase.FileDownload:
                        // En un hilo aparte: leer medio giga aqui dejaria la
                        // sesion sin atender pings, entrada ni portapapeles
                        // mientras dure la descarga.
                        var peticion = paquete.FileDownload;

                        _ = Task.Run(() => FileService.Leer(
                            peticion.Path, peticion.Offset,
                            trozo => Suelto(new RemotePacket
                            {
                                ProtocolVersion = RemoteSessionProtocol.Version,
                                SessionId = opciones.SesionId,
                                FileChunk = trozo
                            }),
                            cancelacion.Token), cancelacion.Token);

                        break;

                    case RemotePacket.PayloadOneofCase.FileChunk:
                        // Una subida. Se escribe aqui mismo: es E/S con bufer y
                        // el acuse tiene que salir pegado al trozo, porque es lo
                        // que gobierna el ritmo del que sube.
                        Suelto(new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            FileAck = _archivos.Escribir(paquete.FileChunk)
                        });

                        break;

                    case RemotePacket.PayloadOneofCase.SelectQuality:
                        // Acotada aqui y no en el visor: lo que llega por el
                        // cable no se cree, se comprueba. Un ratio de 500
                        // pondria el bitrate en el techo sin que nadie lo
                        // pidiera de verdad.
                        _calidad = Math.Clamp(paquete.SelectQuality.Ratio, 0.2, 4.0);
                        _bitrateDeseado = 0;   // se recalcula con la base nueva

                        opciones.Escribir($"Calidad puesta en {_calidad:0.00}x");
                        break;

                    case RemotePacket.PayloadOneofCase.SelectCodec:
                        // Solo se anota, igual que la pantalla. Quien rehace la
                        // cadena es el hilo que la tiene.
                        _codec = paquete.SelectCodec.Codec;
                        break;

                    case RemotePacket.PayloadOneofCase.SelectDisplay:
                        // Solo se anota. Quien rehace la captura es el hilo que la
                        // tiene, cuando le viene bien: tocar DXGI desde aqui es
                        // exactamente el error que colgo el pipeline en la Fase 2.
                        _pantalla = paquete.SelectDisplay.DisplayId;
                        break;

                    case RemotePacket.PayloadOneofCase.ClipboardFiles
                        when paquete.ClipboardFiles.Apply:

                        // El tecnico ya subio los bytes y estas rutas existen en
                        // ESTA maquina. CF_HDROP son referencias: ponerlo con
                        // rutas que no existen da un error del Explorador al
                        // pegar y ninguna pista de por que.
                        // Las variables se expanden AQUI: el visor mando
                        // %TEMP%\... porque no sabe donde tiene el temporal esta
                        // maquina, y CF_HDROP no admite nada sin resolver.
                        var pegar = paquete.ClipboardFiles.Paths
                            .Select(Environment.ExpandEnvironmentVariables)
                            .ToList();

                        opciones.Escribir(
                            ClipboardBridge.EscribirArchivos(pegar)
                                ? $"{pegar.Count} archivos en el portapapeles."
                                : "No se pudo poner los archivos en el portapapeles.");

                        break;

                    case RemotePacket.PayloadOneofCase.ClipboardFiles:
                        // Anuncio del visor: el tecnico copio archivos en SU PC.
                        // No se hace nada hasta que decida traerlos.
                        break;

                    case RemotePacket.PayloadOneofCase.Clipboard:
                        // Como la entrada: se encola y lo aplica el hilo de
                        // captura. El portapapeles pertenece a la estacion de
                        // ventanas y ese es el hilo que esta atado a ella.
                        PortapapelesEntrante.Enqueue(paquete.Clipboard.Text);
                        break;

                    case RemotePacket.PayloadOneofCase.KeyframeRequest:
                        // Se anota y lo atiende la bomba de esa pantalla. Hasta
                        // la Fase 13 esto se registraba y se tiraba; hasta hoy
                        // nadie lo mandaba nunca.
                        _keyframePorPantalla[(int)paquete.KeyframeRequest.DisplayId] = 0;

                        opciones.Escribir(
                            $"Piden keyframe de la pantalla {paquete.KeyframeRequest.DisplayId} " +
                            $"({paquete.KeyframeRequest.Reason}).");
                        break;

                    case RemotePacket.PayloadOneofCase.VideoAck:
                        if (paquete.VideoAck.Presented)
                        {
                            // MOSTRADO. No suelta nada: solo dice cuanto tardo en
                            // llegar a los ojos del tecnico. El de recibido mide
                            // el transporte; este mide el transporte MAS
                            // descodificar y pintar, que es lo unico que el
                            // usuario ve de verdad.
                            _mostrado?.Invoke(paquete.VideoAck.DisplayId, paquete.VideoAck.FrameId);
                            break;
                        }

                        // El primero enciende el freno para toda la sesion.
                        _visorAcusa = true;

                        _acusar?.Invoke(paquete.VideoAck.DisplayId, paquete.VideoAck.FrameId);
                        break;
                }
            }

            // El stream se acabo sin Close: el servidor se cayo o la red murio.
            // Tampoco aqui se sigue capturando.
            await cancelacion.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException)
        {
            await cancelacion.CancelAsync();
        }
    }

    internal static GrpcChannel Conectar(RelayOptions opciones)
    {
        var canal = new GrpcChannelOptions();

        if (opciones.AllowUntrusted)
        {
            // Escotilla de laboratorio, y hay que pedirla a mano.
            Console.Error.WriteLine("AVISO: no se valida el certificado del servidor (--allow-untrusted).");

            canal.HttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
        else if (opciones.PinnedKeys.Count > 0)
        {
            // Los mismos pines SPKI que usa el agente, que se los pasa por el
            // named pipe. Contra un certificado autofirmado la cadena de CA no
            // dice nada y el pin lo dice todo.
            var pines = opciones.PinnedKeys.ToHashSet(StringComparer.Ordinal);

            canal.HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificado, _, _) => Fijado(certificado, pines)
                }
            };
        }

        return GrpcChannel.ForAddress(opciones.Servidor, canal);
    }

    private static bool Fijado(X509Certificate? certificado, HashSet<string> pines)
    {
        if (certificado is null)
            return false;

        using var cert = X509CertificateLoader.LoadCertificate(certificado.GetRawCertData());

        return pines.Contains(Convert.ToBase64String(
            SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())));
    }
}
