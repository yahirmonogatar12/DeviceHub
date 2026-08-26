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

        // EL RELOJ DE WINDOWS, A UN MILISEGUNDO.
        //
        // Por defecto el temporizador del sistema tiene una granularidad de
        // 15.6 ms, y TODAS las esperas se redondean hacia arriba: la de 33 ms
        // de AcquireNextFrame se convierte en 46.8 reales. Medido en una PC
        // Intel: vueltas 20/s con fps 30, que es exactamente 1000/46.8.
        //
        // No era cosmetico. Con 120 capturas habia 107 descartes -- frames que
        // DXGI tenia listos y no recogimos por llegar tarde -- o sea casi la
        // mitad de la pantalla tirada por un redondeo.
        //
        // Se pide 1 ms y se suelta al salir. Desde Windows 10 2004 esto afecta
        // solo al proceso que lo pide, y RemoteHost vive lo que dura una sesion:
        // el coste en energia esta acotado a cuando alguien esta mirando, que es
        // justo cuando la precision hace falta. Si winmm no esta, se sigue sin
        // ella -- se vera en `vueltas`.
        var relojFino = TimeBeginPeriod(1) == 0;

        if (!relojFino)
            opciones.Escribir("No se pudo subir la resolucion del reloj; el ritmo ira a saltos de 15.6 ms.");

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
            // LA VENTANA SE CUENTA DESDE EL CORTE, NO DESDE QUE ARRANCO.
            //
            // Aqui habia `corte = UtcNow` justo antes de conectar, asi que la
            // condicion del `when` medía cuanto llevaba VIVA la sesion. Pasado
            // el primer minuto no volvia a ser cierta nunca y el primer tropiezo
            // de red mataba la sesion sin un solo reintento. Ver VentanaDeReconexion.
            DateTimeOffset? corte = null;
            var espera = TimeSpan.FromMilliseconds(250);
            var codigo = 4;

            while (!cancellationToken.IsCancellationRequested)
            {
                var inicio = DateTimeOffset.UtcNow;

                try
                {
                    codigo = await SesionAsync(opciones, ticket, cancellationToken);
                    break;
                }
                catch (RpcException ex)
                {
                    var ahora = DateTimeOffset.UtcNow;

                    // Una sesion que aguanto un rato empieza de cero: si no, un
                    // microcorte a los diez minutos arrancaria esperando los 5 s
                    // a los que llego el corte anterior.
                    if (ahora - inicio > VentanaDeReconexion.Aguanto)
                        espera = TimeSpan.FromMilliseconds(250);

                    corte = VentanaDeReconexion.Corte(corte, inicio, ahora);

                    // Se sale por `throw` y no por un filtro, porque la marca de
                    // la racha hay que calcularla ANTES de decidir y un `when`
                    // corre antes que el cuerpo.
                    if (!PuedeVolver(corte.Value, cancellationToken))
                        throw;

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
            // LA PANTALLA VIRTUAL NO SE QUEDA. Un monitor fantasma en una PC de
            // planta no es cosmetico: Windows deja mover ventanas ahi y el
            // operador no las vuelve a ver nunca, porque ese monitor no existe
            // fisicamente. Se quita aunque nadie la haya encendido -- si el host
            // anterior murio de golpe, esta es la unica limpieza que va a haber.
            PantallaVirtual.Apagar(out _);

            if (relojFino)
                TimeEndPeriod(1);

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
    /// <summary>
    /// Resolucion del temporizador del sistema, en milisegundos.
    ///
    /// Interop a mano y no una envoltura: son dos funciones y este repositorio
    /// no compila con /unsafe en ningun proyecto. Mismo precedente que el resto
    /// del interop Win32 que ya hay aqui.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milisegundos);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milisegundos);

    private static bool PuedeVolver(DateTimeOffset desdeElCorte, CancellationToken cancellationToken)
        => _tokenReconexion is { Length: > 0 }
           && !cancellationToken.IsCancellationRequested
           && VentanaDeReconexion.Sigue(desdeElCorte, DateTimeOffset.UtcNow);

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
                    Codecs = { VideoCodec.H264, VideoCodec.H265 },
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
    /// <summary>
    /// ARRANCA EN FIEL, no en Equilibrado.
    ///
    /// El overlay de RustDesk en esta misma planta dice "Target Bitrate 3846
    /// kb", por ENCIMA incluso de nuestro Fiel (3109), y ahi la imagen aguanta
    /// el movimiento. Con Equilibrado -- 1388 kbps -- se ablandaba, y el tecnico
    /// tenia que saber que existe un menu para arreglarlo.
    ///
    /// El ancho de banda en una LAN de planta es gratis; la nitidez no. Los
    /// otros dos modos siguen ahi para una red que de verdad no de.
    /// </summary>
    /// <summary>
    /// La calidad de arranque: EQUILIBRADO.
    ///
    /// Fiel al original gasta tres veces mas bitrate para que un escritorio de
    /// planta se vea marginalmente mejor, y De rapida reaccion se ve mal a
    /// 1920x1080: se probo, y a ~1.2 Mbps el texto de una tabla no se lee
    /// comodo. Equilibrado da ~1.8 Mbps ahi, que es donde se ve bien sin gastar
    /// de mas.
    ///
    /// Los dos extremos siguen en el menu para quien los quiera. Lo que no
    /// puede ser es que el defecto obligue a tocarlo: quien entra a arreglar
    /// algo no abre un menu a ver si hay algo mejor, asume que asi es como va.
    /// </summary>
    private static double _calidad = ControlBitrate.CalidadEquilibrada;

    /// <summary>Codec de arranque. H.265 por defecto: misma calidad con un
    /// 30-40 % menos de bits, y es lo que usa RustDesk en esta planta. Si la GPU
    /// no lo tiene, Codificar se cae a H.264 sola y lo dice.</summary>
    private static readonly VideoCodec CodecPorDefecto = VideoCodec.H265;

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

    /// <summary>
    /// Congelar o descongelar la entrada FISICA de la PC remota: -1 nada
    /// pendiente, 0 descongelar, 1 congelar.
    ///
    /// Va por bandera y no por llamada directa por la misma razon que el resto
    /// de la entrada, pero con una consecuencia peor: la exencion de BlockInput
    /// es del HILO que bloqueo. Pedirlo desde el hilo de red dejaba el bloqueo
    /// en poder de un hilo del pool cualquiera, y entonces ni nuestro propio
    /// SendInput entraba ni el desbloqueo -- que casi seguro caia en OTRO hilo
    /// del pool -- servia de nada. Congelar una PC de planta para todos, el
    /// tecnico incluido, y sin forma de deshacerlo.
    /// </summary>
    private static volatile int _congelarPedido = -1;

    /// <summary>Si el bloqueo esta puesto. Lo lee y lo escribe SOLO
    /// devicehub-entrada, que es su dueno.</summary>
    private static bool _congelado;

    private static string Pedir(int estado)
    {
        _congelarPedido = estado;
        return string.Empty;
    }

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

    /// <summary>
    /// LO QUE TARDA EL CODIFICADOR, que es lo unico que no se estaba midiendo.
    ///
    /// `verse` arranca su reloj justo ANTES de escribir en la red, o sea despues
    /// de capturar, convertir y codificar. Mide red + descodificar + pintar, y
    /// nada mas. En una PC con GPU eso da igual porque codificar cuesta menos de
    /// un milisegundo; en un Xeon sin GPU, donde lo hace la CPU, es el termino
    /// que manda -- y era justo el que no salia por ninguna parte.
    ///
    /// El resultado fue una sesion entera leyendo "3.9 ms" mientras el tecnico
    /// decia que iba lento. Tenia razon el tecnico.
    /// </summary>
    private static readonly MedidorRetraso _codificar = new();

    /// <summary>
    /// Lo que tarda la captura en devolver algo, incluida su espera.
    ///
    /// Con el ritmo puesto en esa espera, este numero LEE el estado de la
    /// pantalla: cerca del intervalo de frame significa que casi siempre expira
    /// -- nada cambia -- y cerca de cero significa que hay imagen nueva
    /// esperando en cada vuelta. Sin el, "1.5 FPS" no distingue una pantalla
    /// quieta de una tuberia atascada, que es la duda que costo la tarde.
    /// </summary>
    private static readonly MedidorRetraso _capturar = new();

    /// <summary>
    /// La mitad de "codificar" que es codigo NUESTRO: bajar la textura de la GPU
    /// y pasarla a NV12. La otra mitad es el MFT, que no se puede tocar.
    ///
    /// RustDesk hace esta misma conversion con ARGBToNV12 de libyuv, SIMD
    /// escrito a mano. La nuestra es un bucle escalar pixel a pixel. Este numero
    /// dice si esa diferencia importa aqui o si el trabajo esta en otro sitio.
    /// </summary>
    private static readonly MedidorRetraso _bajar = new();
    private static readonly MedidorRetraso _pasar = new();

    /// <summary>
    /// EL MFT SOLO, sin la conversion. El unico numero que decide si cambiar de
    /// codificador serviria de algo.
    ///
    /// "codificar" lleva dentro bajar la textura y pasarla a NV12, que son
    /// codigo nuestro y no cambian por usar otro codec. Lo que cambiaria al
    /// pasar a un codificador por software pensado para tiempo real -- VP9 de
    /// libvpx en modo REALTIME, que es lo que hace RustDesk cuando no hay
    /// hardware -- es exactamente esta cifra y ninguna otra.
    ///
    /// Si sale por debajo de 5 ms, el MFT no es el problema y portar libvpx
    /// seria trabajo grande para nada. Si sale por encima de 15, lo es.
    /// </summary>
    private static readonly MedidorRetraso _mft = new();

    /// <summary>
    /// DEL PIXEL EN LA MANO AL PAQUETE LISTO: bajar + pasar + MFT, todo junto.
    ///
    /// Es la mitad que faltaba. `verse` arranca su reloj al ESCRIBIR en la red,
    /// asi que mide red + descodificar + pintar y nada mas -- y por eso la linea
    /// decia orgullosa "verse 4.4 ms" mientras la sesion iba claramente atrasada.
    /// Las dos juntas si dan la cuenta honesta:
    ///
    ///     listo   captura -> codificado    lo nuestro
    ///     verse   codificado -> pintado    la red y el visor
    ///
    /// Se mide desde que la captura DEVUELVE un frame, no desde que se le pide:
    /// esperar a que la pantalla cambie no es trabajo, es no tener nada que
    /// hacer, y meterlo aqui haria que un escritorio quieto pareciera lento.
    /// </summary>
    private static readonly MedidorRetraso _listo = new();

    /// <summary>Cuanto se espera un acuse antes de seguir sin el. RustDesk usa
    /// 3 s; aqui menos, porque su espera se corta en cuanto llegan todos y esta
    /// solo salta cuando el acuse se perdio de verdad.</summary>
    private const int EsperaDeAcuseMs = 1000;

    /// <summary>
    /// Cuantas veces seguidas se re-alimenta el codificador con el ultimo frame
    /// cuando la pantalla no cambia. Diez es lo que usa RustDesk, y basta para
    /// vaciar la tuberia del MFT por software sin encender la CPU de una PC que
    /// nadie esta tocando.
    /// </summary>
    private const int RepeticionesMax = 10;

    /// <summary>Repeticiones antes de la PRIMERA salida del codificador. Mas
    /// altas que el cupo normal porque ahi no sobran: son lo unico que entra a un
    /// codificador frio con la pantalla quieta.</summary>
    private const int RepeticionesArranque = 60;

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

    /// <summary>
    /// La version de configuracion, para TODO el proceso y no por corrida.
    ///
    /// Sobrevive a las reconexiones a proposito. Nacia con los contadores, asi
    /// que tras un microcorte el host volvia a estrenar la v1 -- y el visor
    /// descarta una configuracion cuya version ya tiene, porque el relay repite
    /// la vigente cada vez que se recupera de una perdida. Misma version, mismo
    /// tamano, SPS distinto: se seguia descodificando con el del codificador
    /// anterior, que no da error, da imagen corrupta.
    ///
    /// Interlocked porque cada pantalla estrena la suya desde su propia bomba.
    /// </summary>
    private static int _versionDeConfig;

    private static uint SiguienteVersion(Contadores cuenta)
        => cuenta.ConfigVersion = (uint)Interlocked.Increment(ref _versionDeConfig);

    private sealed class Contadores
    {
        public long Capturados, Codificados, Claves, Enviados, Trozos, Bytes;
        public long DescartesEncoder, DescartesCaptura;

        /// <summary>
        /// LAS FRONTERAS ENTRE CODIFICAR Y ESCRIBIR EN EL CABLE.
        ///
        /// Existen porque "codificados 1172, enviados 278" no decia donde se
        /// iban los 894 del medio. Tienen que cerrar esta identidad:
        ///
        ///     Codificados = SinParametros + SinConfig + Encolados
        ///     Encolados   = Sacados + lo que quede en la cola
        ///     Sacados     = Enviados
        ///
        /// Si alguna no cierra, el agujero esta entre esos dos contadores y no
        /// hay que buscarlo en ningun otro sitio.
        /// </summary>
        public long SinParametros, SinConfig, Encolados, Sacados;

        /// <summary>
        /// VUELTAS DEL BUCLE DE CAPTURA. El numero que falta para saber si el
        /// ritmo lo marca la pantalla o nosotros.
        ///
        /// En una Intel con H.265 por hardware salieron 392 timeouts y 80
        /// capturas en 34 segundos: 14 vueltas por segundo cuando el objetivo
        /// son 30 y el trabajo son 3.5 ms. Con `timeouts` solo no se distingue
        /// "la pantalla no cambia" de "el bucle va lento", y las dos dan pocos
        /// frames.
        ///
        /// Si vueltas/s se acerca a fps, el ritmo es correcto y lo que falta es
        /// contenido. Si se queda a la mitad, el tiempo se va en algo que no
        /// esta medido -- y con `espera` al lado se ve si es la captura o el
        /// descanso del final.
        /// </summary>
        public long Vueltas;

        /// <summary>Frames que salieron y nadie confirmo en EsperaDeAcuseMs. Si
        /// esto sube, el visor no esta siguiendo el ritmo.</summary>
        public long AcusesPerdidos;

        /// <summary>La ultima version que se estreno en esta corrida. Solo para
        /// mostrarla: quien las reparte es SiguienteVersion.</summary>
        public uint ConfigVersion;
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
                    Interlocked.Increment(ref cuenta.Sacados);

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
                        // HORAS TOTALES, no mm:ss. Con "mm:ss" un TimeSpan de
                        // 1:03:52 sale como "03:52": las horas no caben en el
                        // formato y se TIRAN. En una corrida larga la linea del
                        // host parece reiniciarse cada hora -- que es justo lo
                        // que esa corrida esta midiendo.
                        $"{(int)reloj.Elapsed.TotalHours:00}:{reloj.Elapsed:mm\\:ss}  " +
                        $"capturados {cuenta.Capturados}  codificados {cuenta.Codificados}  " +
                        $"frames enviados {cuenta.Enviados}  chunks {cuenta.Trozos}  " +
                        $"sin(param {cuenta.SinParametros}/config {cuenta.SinConfig})  " +
                        $"encolados {cuenta.Encolados}  sacados {cuenta.Sacados}  " +
                        $"{cuenta.Bytes * 8 / reloj.Elapsed.TotalSeconds / 1_000_000:0.00} Mbps  " +
                        $"keyframes {cuenta.Claves}  config {cuenta.ConfigVersion}  cola {enCola}  " +
                        $"acuses {(_visorAcusa ? "si" : "no")}/{cuenta.AcusesPerdidos} perdidos  " +
                        $"red {_retraso.Base:0.0} ms + cola {_retraso.Encolado:0.0} ms  " +
                        $"listo {_listo.Percentil(0.50):0.0} ms + " +
                        $"verse {_verse.Ultimo:0.0} ms (min {_verse.Base:0.0})  " +
                        $"capturar p50 {_capturar.Percentil(0.50):0.0} " +
                        $"p95 {_capturar.Percentil(0.95):0.0} ms  " +
                        $"codificar p50 {_codificar.Percentil(0.50):0.0} ms " +
                        $"p95 {_codificar.Percentil(0.95):0.0} ms  " +
                        (_bajar.Ultimo >= 0
                            ? $"(bajar {_bajar.Percentil(0.50):0.0} + " +
                              $"pasar {_pasar.Percentil(0.50):0.0} + " +
                              $"mft {_mft.Percentil(0.50):0.0} ms)  "
                            : "") +
                        $"espera {_esperaCaptura} ms  " +
                        $"vueltas {cuenta.Vueltas / Math.Max(reloj.Elapsed.TotalSeconds, 0.001):0}/s  " +
                        $"timeouts {_timeouts}  gop {_gop}  " +
                        $"fps {_fpsDeseado}  B-frames {Bes()}  " +
                        $"{(_porHardware ? "HW" : "SW")} {_codificadorNombre}  " +
                        (_hilos >= 0 || _prisa >= 0
                            ? $"hilos {_hilos}  prisa {_prisa}  "
                            : "") +
                        $"{(_comoSystem ? "SYSTEM" : "usuario (sin ventanas elevadas)")}  " +
                        $"escritorio {_escritorio}  " +
                        $"objetivo {_bitrateDeseado / 1000} kbps ({_calidad:0.00}x)  " +
                        // Aplicados y rechazados de SendInput. Es lo que dice si
                        // la entrada llega de verdad al otro lado o se la traga
                        // el escritorio equivocado.
                        $"descartes {cuenta.DescartesEncoder}+{cuenta.DescartesCaptura}  " +
                        $"entrada {_aplicadas + (_entrada?.Applied ?? 0)}/" +
                        $"{_rechazadas + (_entrada?.Rejected ?? 0)}");

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

                if (_congelarPedido is var pedido and >= 0)
                {
                    _congelarPedido = -1;

                    if (HostActions.Congelar(pedido == 1, out var queja))
                        _congelado = pedido == 1;

                    Avisar(opciones, queja);
                }

                while (Pendientes.TryDequeue(out var evento))
                    _entrada?.Apply(evento);

                // DESPUES de la entrada: si el tecnico venia arrastrando, sus
                // ultimos movimientos de raton van delante del Ctrl+V.
                while (Pegados.TryDequeue(out var donde))
                {
                    if (_entrada is not { } inyector)
                        continue;

                    var (px, py) = inyector.Pixel(donde.X, donde.Y);

                    Avisar(opciones, PegarEnPunto.Pegar(px, py) switch
                    {
                        PegarEnPunto.Resultado.Pegado => "Archivos pegados en la carpeta de destino.",
                        PegarEnPunto.Resultado.NoEsUnaCarpeta =>
                            "Ahi no hay una carpeta abierta; los archivos estan en el portapapeles.",
                        _ => "No se pudo saber que hay en ese punto de la pantalla."
                    });
                }

                // Se despierta con la entrada, no con el reloj.
                WaitHandle.WaitAny([cancellationToken.WaitHandle, HayEntrada], 20);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            opciones.Escribir($"El hilo de entrada murio: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // EL BLOQUEO MUERE CON SU DUENO, pase lo que pase.
            //
            // Windows tambien lo levanta solo cuando el hilo que bloqueo
            // termina, pero eso deja la PC congelada el rato que tarde en
            // terminar -- y si alguna vez este hilo sobrevive a la sesion,
            // no lo levanta nunca.
            if (_congelado)
            {
                HostActions.Congelar(false, out _);
                _congelado = false;
            }
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

        // SIN NINGUNA PANTALLA, SE PONE UNA.
        //
        // La causa mas frecuente es la mas tonta: el monitor de esa PC esta
        // apagado, o un KVM lo tiene conmutado a otro equipo. Windows conserva
        // el modo de video -- Win32_VideoController sigue diciendo 1920x1080 con
        // Status OK -- pero la salida deja de estar viva, DXGI no enumera nada y
        // Desktop Duplication se queda sin nada que duplicar. Encontrado en
        // PCPROD1, con el monitor en Availability 8 (Off Line).
        //
        // Antes eso era el final del camino: la sesion no abria y el mensaje
        // mandaba a adivinar. Y va a repetirse -- cualquier PC de planta con el
        // monitor apagado por la noche, o compartiendo KVM, da este mismo cuadro.
        //
        // SOLO CUANDO NO HAY NINGUNA. Si hay pantallas y el tecnico eligio una
        // que falla, eso es otra cosa y tiene su propio camino de vuelta. Aqui
        // no hay nada que elegir.
        //
        // Y se quita al cerrar la sesion, como cualquier otra: de eso ya se
        // encarga el Apagar() del finally de RunAsync. Dejar un monitor fantasma
        // en una PC de planta seria peor que no abrir, porque ahi se pierden
        // ventanas que nadie vuelve a ver.
        if (pantallas.Count == 0 && PantallaVirtual.Disponible)
        {
            Avisar(opciones,
                "Esta PC no tiene ninguna pantalla activa -- lo normal es que el monitor este " +
                "apagado. Se anade una virtual para poder trabajar.");

            var puesta = PantallaVirtual.Encender(out var dijo);
            Avisar(opciones, dijo);

            if (puesta >= 0)
            {
                _pantalla = puesta;
                pantallas = Pantallas.Listar();
            }
        }

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
            _codec = opciones.UsarH265 ? VideoCodec.H265 : CodecPorDefecto;

        var codecPedido = _codec;

        // Todas a la vez compone N duplicaciones en una imagen del tamano del
        // escritorio virtual; una sola entrega la textura del duplicador sin
        // copiar nada. La entrada funciona igual con las dos: InputInjector
        // recibe la esquina de lo capturado y traduce a coordenadas virtuales.
        // El escritorio con el que se abrio ESTA captura. Todo lo que venga
        // despues se compara contra el.
        var escritorioCapturado = InputDesktop.NombreDeEntrada();

        // QUE ESCRITORIO SE ESTA CAPTURANDO, en la linea que el tecnico mira.
        //
        // Es la diferencia entre "no se ve el UAC" y "se esta capturando
        // Winlogon y aun asi no se ve": lo primero es que el salto no ocurrio,
        // lo segundo es que ocurrio y la captura no da imagen. Son dos problemas
        // distintos y hasta ahora los dos se veian igual desde el visor.
        _escritorio = escritorioCapturado.Length == 0 ? "?" : escritorioCapturado;

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
        var flujos = Etiquetar(opciones, paso, () => AbrirFlujos(pedida, pantallas, elegida, opciones, cuenta));

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
        // La lista de pantallas NO se descarta: pasa una vez, al abrir y en
        // cada cambio, y si no llega el desplegable del visor se queda sin el
        // monitor nuevo hasta la proxima recaptura. Nada lo reintenta.
        Fiable(salida, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            Displays = ListaDePantallas(pantallas, pedida)
        }, cancellationToken);

        // El inyector trabaja sobre el LIENZO entero, no sobre una pantalla: el
        // visor manda coordenadas normalizadas sobre lo que ve, y lo que ve es la
        // composicion. Con una sola pantalla el lienzo es esa pantalla.
        var lienzo = flujos[0].Lienzo;

        RelevarInyector(new InputInjector(lienzo.Ancho, lienzo.Alto, lienzo.X, lienzo.Y));

        // El hilo de red entrega los acuses por aqui. No toca los flujos
        // directamente por lo mismo de siempre: cada bomba es la unica dueña de
        // lo suyo, y esto solo abre un semaforo.
        _medidas = c =>
        {
            c.DescartesEncoder = flujos.Sum(f => f.Codificador.Dropped);
            c.DescartesCaptura = flujos.Sum(f => f.Captura.Dropped);

            _timeouts = flujos.Sum(f => f.Captura.Timeouts);
            _esperaCaptura = flujos[0].Captura.EsperaMs;
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

        _bframes = flujos[0].Codificador is H264Encoder mft ? mft.BFrames : -1;
        _hilos = flujos[0].Codificador is H264Encoder m2 ? m2.Trabajadores : -1;
        _prisa = flujos[0].Codificador is H264Encoder m3 ? m3.Prisa : -1;
        _gop = flujos[0].Codificador is H264Encoder m4 ? m4.Gop : -1;

        // POR HARDWARE O POR SOFTWARE, en la linea que el tecnico si mira.
        //
        // Es la diferencia entre un bloque dedicado de la GPU y la CPU de la
        // maquina haciendo el trabajo mientras corre lo que sea que corra esa
        // PC. Estaba solo en el log de eventos, que nadie abre, y es el primer
        // dato que hace falta cuando alguien dice que va lento.
        _porHardware = flujos[0].Codificador.Capabilities.Hardware;
        _codificadorNombre = flujos[0].Codificador.Capabilities.Name;

        // Y la ficha entera al log, UNA vez. Aqui y no en la linea de medidas
        // porque no cambia: es la identidad del codificador, no una medida.
        foreach (var flujo in flujos)
        {
            if (flujo.Codificador is H264Encoder ficha)
                opciones.Escribir($"Pantalla {flujo.DisplayId}: {ficha.Ficha}");
        }

        // COMO QUIEN CORRE, en la linea que el tecnico si mira.
        //
        // Decide mas de lo que parece: SendInput no entra en una ventana
        // ELEVADA -- el Administrador de dispositivos, el editor del registro,
        // cualquier dialogo de UAC -- si quien inyecta corre como el usuario.
        // Windows lo bloquea por UIPI y no avisa: el raton se mueve, la ventana
        // no responde, y parece que el control remoto se colgo.
        //
        // Estaba solo en la linea de identidad, que va al registro de la PC de
        // planta. El mismo error de los B-frames, otra vez.
        _comoSystem = System.Security.Principal.WindowsIdentity.GetCurrent()
            .User?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) ?? false;

        opciones.Escribir(
            $"Identidad {System.Security.Principal.WindowsIdentity.GetCurrent().Name}  " +
            $"Escritorio {escritorio.Name}  Flujos {flujos.Count}  " +
            $"Codec {Etiqueta(flujos[0].Codificador.Codec)}  " +
            $"MFT {flujos[0].Codificador.Capabilities.Name}  " +
            $"Hardware {(flujos[0].Codificador.Capabilities.Hardware ? "TRUE" : "FALSE")}  " +
            $"B-frames {Bes()}  " +
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

        // LA BOMBA DE SONIDO, en su propio hilo como cada pantalla.
        //
        // No puede ir en el bucle de captura: ahi una vuelta cuesta entre 4 y 28
        // ms -- capturar y codificar un frame -- y WASAPI entrega paquetes cada
        // 10. Colgarlo de ahi seria perder audio en cada frame lento, y eso no
        // se ve como una imagen mas fea: se oye como chasquidos.
        //
        // Arranca APAGADA y sin abrir el dispositivo. En un servidor sin
        // tarjeta de sonido abrirlo falla, y fallar al abrir una sesion por algo
        // que nadie pidio seria cambiar una funcion nueva por una averia.
        _sonido = new Audio.BombaDeSonido(
            opciones.SesionId,
            paquete => salida.TryWrite(new Enviable(null, null, paquete)),
            aviso => Avisar(opciones, aviso));

        bombas.Add(new Thread(() => _sonido.Bombear(pararBombas.Token))
        {
            IsBackground = true,
            Name = "devicehub-sonido"
        });

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
                        // Lo copiado pasa UNA vez. Perderlo es que el tecnico
                        // pegue lo de antes sin enterarse.
                        Fiable(salida, new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            Clipboard = new ClipboardText { Text = copiado }
                        }, cancellationToken);
                    }

                    // Solo el ANUNCIO: aqui viajan rutas, nunca contenido. Los
                    // bytes salen despues por la transferencia de la Fase 24, y
                    // solo si el tecnico lo pide.
                    if (archivosCopiados.Count > 0)
                    {
                        var aviso = new ClipboardFiles();
                        aviso.Paths.AddRange(archivosCopiados);

                        Fiable(salida, new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = opciones.SesionId,
                            ClipboardFiles = aviso
                        }, cancellationToken);
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

                    // Y la configuracion del sonido con ella. Quien pide un
                    // keyframe acaba de perder el contexto del video; si tenia
                    // sonido, ha perdido tambien su AudioSpecificConfig -- y sin
                    // el, los paquetes que sigan no son descodificables.
                    _sonido?.ReenviarConfig();
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
                        // EL SUELO ES DE LA SESION, NO DE CADA PANTALLA.
                        //
                        // Con ControlBitrate.Minimo aqui, el controlador creia
                        // haber bajado a 400 kbps y con dos monitores salian
                        // 800 reales; con cuatro, 1.6 Mbps. Justo con la red
                        // mala, que es cuando el suelo importa.
                        //
                        // Lo que se reparte ya viene acotado por abajo: aqui
                        // solo se evita el cero, que ningun codificador acepta.
                        flujo.BitrateDeseado = (int)Math.Max(
                            (long)_bitrateDeseado * flujo.BitrateBase / bitrateBaseTotal,
                            ControlBitrate.MinimoPorPantalla);
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
            var ultimoFrame = Stopwatch.GetTimestamp();

            // Cuando se codifico algo por ultima vez, para el suelo de imagen.
            var ultimoCodificado = 0L;
            var bitrateActual = 0;

            // Repeticiones seguidas del ultimo frame, y la marca de tiempo que
            // se les pone. Ver el bloque de "no hay imagen nueva", mas abajo.
            var repetidos = 0;
            var ultimoTimestampUs = 0L;

            // Cuando se pidio el ultimo keyframe, o 0 si ya llego. Ver
            // PlazoDeKeyframe, mas abajo.
            var keyframePedidoEn = 0L;

            // El MFT no admite cambiar el bitrate en marcha. Se descubre al
            // primer rechazo y se deja de pedir.
            var bitrateFijo = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                // EL RITMO LO MARCA LA ESPERA DE LA CAPTURA, NO UN SUEÑO PREVIO.
                //
                // Es como lo hace RustDesk: le pasan a su captura la duracion de
                // un frame como tiempo de espera, y cuando vuelve duermen solo lo
                // que sobro. La vuelta termina en el INSTANTE en que hay imagen
                // nueva.
                //
                // Antes aqui se dormia hasta la siguiente ranura de 30 fps Y
                // ADEMAS se esperaba 100 ms en AcquireNextFrame: las dos cosas.
                // Un cambio que ocurria durante el sueño se quedaba esperando a
                // que terminara, y despues aun podia caer en una espera de 100
                // ms. Hasta 133 ms de retraso en cada interaccion, y ninguno de
                // ellos salia en las medidas porque todas empiezan a contar
                // despues.
                var vuelta = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref cuenta.Vueltas);
                var intervalo = Stopwatch.Frequency /
                    Math.Clamp(_fpsDeseado, ControlFps.Minimo, ControlFps.Maximo);

                flujo.Captura.EsperaMs = (int)Math.Max(1, intervalo * 1000 / Stopwatch.Frequency);

                try
                {

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

                    // Y se reabre el cupo de repeticiones. Sin esto, un visor
                    // que entra a una pantalla que lleva horas quieta pide su
                    // keyframe y no hay nada que se lo lleve al codificador.
                    repetidos = 0;
                    keyframePedidoEn = Stopwatch.GetTimestamp();

                    // La config va DELANTE del IDR: si el visor perdio el SPS, un
                    // keyframe suelto no le sirve de nada.
                    flujo.ConfigEnviada = false;
                }

                // "SE LO PEDI" NO ES "OCURRIO", otra vez.
                //
                // CambiarBitrate devuelve false cuando el MFT rechaza el valor,
                // y aqui se apuntaba como aplicado igualmente: el codificador se
                // quedaba con el bitrate viejo y nadie volvia a intentarlo,
                // porque bitrateActual ya decia que estaba puesto.
                //
                // Si falla NO se apunta, para que el siguiente cambio lo vuelva
                // a intentar. Y si falla el primero se deja de insistir: un MFT
                // que no admite bitrate dinamico no lo va a admitir treinta
                // veces por segundo, y ese reintento es trabajo y ruido.
                if (!bitrateFijo && flujo.BitrateDeseado != bitrateActual && flujo.BitrateDeseado > 0)
                {
                    if (flujo.Codificador.CambiarBitrate(flujo.BitrateDeseado))
                    {
                        opciones.Escribir($"Pantalla {flujo.DisplayId}: bitrate {flujo.BitrateDeseado / 1000} kbps");
                        bitrateActual = flujo.BitrateDeseado;
                    }
                    else
                    {
                        bitrateFijo = true;

                        Avisar(opciones,
                            $"El codificador rechazo {flujo.BitrateDeseado / 1000} kbps: " +
                            $"la pantalla {flujo.DisplayId} se queda con el bitrate de arranque.");
                    }
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
                // UN ESCRITORIO DELANTE NO MATA LA PANTALLA.
                //
                // Cuando aparece un escritorio seguro -- un UAC, el dialogo de
                // credenciales de una carpeta compartida, la pantalla de bloqueo
                // -- DuplicateOutput contesta acceso denegado. Eso salia por el
                // catch de fuera del bucle, se escribia "la pantalla 0 dejo de
                // emitir" y el hilo TERMINABA: esa pantalla no volvia ni cuando
                // el dialogo se cerraba, y la unica salida era reconectar.
                //
                // Se releva por GDI, que es lo mismo que se hace al ABRIR en un
                // escritorio que no es el normal, y si tampoco puede se espera y
                // se reintenta. Un dialogo delante es un estado pasajero.
                VideoFrame? frame;

                // SI EL KEYFRAME PEDIDO NO LLEGA, SE REHACE EL CODIFICADOR.
                //
                // Esta demostrado que este MFT ignora ForzarKeyframe(): los
                // keyframes de una corrida caen a la distancia exacta del GOP,
                // no cuando se piden. Y el respaldo periodico son 900 frames.
                //
                // En una pantalla QUIETA eso no son treinta segundos, son para
                // siempre: las repeticiones estan acotadas a diez, asi que
                // pasadas esas diez se deja de alimentar al codificador y el
                // contador de 900 no avanza NUNCA. Un visor que reconecta a una
                // consola parada se quedaria esperando un IDR que no puede
                // llegar.
                //
                // Rehacer el codificador es determinista: uno nuevo emite un IDR
                // con su SPS en el primer frame que le entra, pase lo que pase
                // con ForceKeyFrame. Y estrena version, asi que el visor recibe
                // configuracion nueva y no depende de la anterior.
                //
                // Medio segundo, y no menos: a treinta cuadros son quince frames
                // de margen para que el MFT haga lo que se le pidio por las
                // buenas. Rehacerlo cuesta una vez; gastarlo en cada peticion
                // seria peor que el problema.
                // MEDIO SEGUNDO PARA UNO CALIENTE; DOS PARA UNO QUE NI HA
                // ARRANCADO.
                //
                // Los 500 ms son para el caso que motivo esto: un codificador
                // que YA entrega y se salta el ForceKeyFrame. Uno recien creado
                // es otra cosa -- un MFT por hardware necesita varias entradas
                // antes de soltar la primera salida, y en una pantalla quieta
                // esas entradas solo llegan por repeticion.
                //
                // Con el mismo plazo para los dos casos pasaba esto, medido en
                // INVENTARIO-IMD-SMD: se agotaban las 10 repeticiones en 470 ms
                // -- la vuelta cuesta unos 47 -- el plazo saltaba a los 500, se
                // rehacia el codificador, y vuelta a empezar. Seis veces antes
                // de que una soltara imagen. El tecnico lo ve como "tarda en
                // abrir", y cada rehecho es trabajo tirado.
                // Y EN FRIO NO MANDA EL RELOJ, MANDA CUANTAS VECES SE LE DIO
                // DE COMER.
                //
                // Poner dos segundos en vez de medio no quito la carrera, solo
                // la aflojo: sesenta repeticiones a unos 47 ms por vuelta son
                // 2.8 s, o sea que el plazo sigue venciendo ANTES de que se
                // agote el cupo. En IMD_D se veia igual -- pantalla negra, el
                // aviso, y al rato entra.
                //
                // El reloj es ademas la medida equivocada: castiga a la PC lenta
                // justo por serlo. Dos maquinas con el mismo codificador
                // deberian darle el mismo numero de oportunidades, tarden lo que
                // tarden en darselas.
                //
                // Asi que mientras no haya salido nada, se rehace cuando se
                // acaban las repeticiones y no antes. Deja de haber carrera: el
                // codificador recibe SIEMPRE sus sesenta frames enteros.
                var vencido = flujo.ConfigEnviada
                    ? keyframePedidoEn != 0
                      && Stopwatch.GetTimestamp() - keyframePedidoEn > Stopwatch.Frequency / 2
                    : repetidos >= RepeticionesArranque;

                if (vencido)
                {
                    keyframePedidoEn = 0;

                    Avisar(opciones, flujo.ConfigEnviada
                        ? $"El codificador no solto el keyframe pedido en medio segundo; " +
                          $"se rehace la pantalla {flujo.DisplayId}."
                        : $"El codificador no solto nada en {RepeticionesArranque} frames; " +
                          $"se rehace la pantalla {flujo.DisplayId}.");

                    RehacerCodificador(flujo, opciones, cuenta);
                    repetidos = 0;
                }

                var antesDeCapturar = Stopwatch.GetTimestamp();

                try
                {
                    frame = flujo.Captura.CaptureAsync(cancellationToken).GetAwaiter().GetResult();
                }
                catch (ScreenCaptureUnavailableException ex)
                {
                    if (!flujo.Tapada)
                    {
                        flujo.Tapada = true;
                        Avisar(opciones,
                            $"La pantalla {flujo.DisplayId} no se puede duplicar ahora mismo " +
                            $"({ex.Message}). Se intenta por GDI.");
                    }

                    if (!flujo.Relevada)
                    {
                        flujo.Relevada = true;

                        if (Relevar(flujo, opciones, cuenta))
                        {
                            flujo.Tapada = false;
                            continue;
                        }
                    }

                    // Ni DXGI ni GDI. Se espera y se vuelve a mirar: cuando el
                    // dialogo se cierre, uno de los dos volvera a funcionar.
                    cancellationToken.WaitHandle.WaitOne(250);
                    continue;
                }

                _capturar.Anotar(
                    (Stopwatch.GetTimestamp() - antesDeCapturar) * 1000.0 / Stopwatch.Frequency);

                if (frame is null)
                {
                    // NO HAY IMAGEN NUEVA. Se re-alimenta el codificador con la
                    // ultima, un numero acotado de veces.
                    //
                    // Es lo que hace RustDesk en su rama de WouldBlock, y por
                    // el mismo motivo: el que retiene frames es el CODIFICADOR,
                    // no la pantalla. En la consola de un servidor quieto se
                    // capturaban dos frames y se codificaban cero -- el MFT por
                    // software no suelta el primero hasta que le entran varios
                    // -- asi que no habia primer keyframe y el visor se quedaba
                    // en "sin config" para siempre.
                    //
                    // Acotado porque un escritorio quieto esta SANO: pasado el
                    // cupo se calla, y no se gasta un Xeon sin GPU codificando
                    // la misma imagen toda la noche. El cupo se reabre cuando
                    // la pantalla cambia o cuando alguien pide un keyframe.
                    // EL CUPO ES PARA UNA PANTALLA QUIETA Y SANA, NO PARA
                    // ARRANCAR.
                    //
                    // Mientras el codificador no haya soltado su primera salida,
                    // estas repeticiones NO son un lujo: son lo unico que le
                    // entra, y sin ellas no hay primer keyframe ni configuracion.
                    // Cortarlas a las diez dejaba al codificador a medio arrancar
                    // justo cuando vencia el plazo del keyframe.
                    //
                    // Sigue acotado, y mas alto: si con sesenta no ha entregado,
                    // ese codificador no va a entregar y lo que toca es rehacerlo
                    // -- de eso se encarga el plazo de arriba.
                    if (repetidos >= (flujo.ConfigEnviada ? RepeticionesMax : RepeticionesArranque))
                        continue;

                    // Y NO SE REPITE SI HACE NADA QUE SE CODIFICO DE VERDAD.
                    //
                    // Deprisa solo mientras el codificador no haya soltado su
                    // primera salida, que es vaciarle la tuberia. Despues, una
                    // vez por segundo.
                    //
                    // Sin esto, en un codificador que no da abasto las
                    // repeticiones COMPITEN con las capturas de verdad y ganan:
                    // en la consola del servidor salian 327 frames por minuto
                    // de los que solo 97 eran imagen nueva, y 85 capturas se
                    // descartaban porque el codificador estaba ocupado
                    // reenviando la anterior. El tecnico veia 1.6 frames
                    // nuevos por segundo y lo llamaba lag, con razon.
                    var ahora = Stopwatch.GetTimestamp();

                    if (flujo.ConfigEnviada && ahora - ultimoCodificado < Stopwatch.Frequency)
                        continue;

                    ultimoCodificado = ahora;
                    repetidos++;
                    ultimoTimestampUs += 1_000_000L / Math.Clamp(
                        _fpsDeseado, ControlFps.Minimo, ControlFps.Maximo);

                    producidos = Cronometrar(() => flujo.Codificador.Repetir(ultimoTimestampUs));

                    if (producidos.Count == 0)
                        continue;
                }
                else
                using (frame)
                {
                    flujo.Tapada = false;

                    // SUELO DE IMAGEN: un frame por segundo aunque no cambie nada.
                    //
                    // "Sin cambios no se manda nada" es correcto y es lo que hace
                    // que un escritorio quieto no gaste ancho de banda. Lo que no
                    // vale es que no se mande NUNCA: en la consola de un servidor,
                    // que puede pasar horas sin que se mueva un pixel, el visor se
                    // queda en "sin config" para siempre -- no hay primer keyframe
                    // que mandar, asi que no hay nada que descodificar.
                    //
                    // Y ademas un codificador por software no arranca con eso:
                    // recibia tres frames en cuarenta y cinco segundos, con las
                    // marcas de tiempo a quince segundos unas de otras, y no
                    // producia una sola salida.
                    //
                    // Es lo que hace RustDesk, y por eso ahi se ve la pantalla
                    // desde el primer segundo. Un frame de un escritorio quieto
                    // cuesta unos pocos cientos de bytes.
                    var ahora = Stopwatch.GetTimestamp();
                    var toca = ahora - ultimoCodificado > Stopwatch.Frequency;

                    if (!frame.DesktopChanged && !toca)
                        continue;

                    ultimoCodificado = ahora;
                    ultimoFrame = Stopwatch.GetTimestamp();
                    flujo.Arranco = true;
                    repetidos = 0;
                    ultimoTimestampUs = frame.TimestampUs;

                    Interlocked.Increment(ref cuenta.Capturados);
                    var enLaMano = Stopwatch.GetTimestamp();

                    producidos = Cronometrar(() => flujo.Codificador.Encode(frame, cancellationToken));

                    if (producidos.Count > 0)
                        _listo.Anotar((Stopwatch.GetTimestamp() - enLaMano) * 1000.0 / Stopwatch.Frequency);

                    // Aqui y no en el informe de cada dos segundos: asi se
                    // anotan TODAS las conversiones y no una de cada sesenta.
                    if (flujo.Codificador is H264Encoder cpu && cpu.BajarMs >= 0)
                    {
                        _bajar.Anotar(cpu.BajarMs);
                        _pasar.Anotar(cpu.PasarMs);

                        // Lo que costo la llamada entera MENOS lo que costo la
                        // conversion. Restar y no cronometrar por dentro porque
                        // un MFT asincrono no hace su trabajo dentro de
                        // ProcessInput: lo hace entre medias y lo entrega por
                        // evento, asi que medir esa llamada daria casi cero y
                        // seria mentira.
                        if (_codificar.Ultimo >= 0)
                            _mft.Anotar(Math.Max(_codificar.Ultimo - cpu.BajarMs - cpu.PasarMs, 0));
                    }
                }

                foreach (var frameCodificado in producidos)
                {
                    Interlocked.Increment(ref cuenta.Codificados);

                    if (frameCodificado.IsKeyFrame)
                    {
                        Interlocked.Increment(ref cuenta.Claves);
                        keyframePedidoEn = 0;   // llego; no hay que rehacer nada
                    }

                    // LA PRIMERA IMAGEN, que es lo que el tecnico llama "abrio".
                    //
                    // Ni la auditoria del servidor ni la linea de medidas cubren
                    // este instante: la primera dice cuando el host se conecto,
                    // la segunda empieza a contar cuando ya hay sesion. Entre
                    // los dos esta lo que se siente como "tarda en abrir".
                    if (!_primeraDicha)
                    {
                        _primeraDicha = true;
                        opciones.Escribir(
                            $"Arranque: PRIMERA IMAGEN a los {DesdeElProceso():0} ms " +
                            $"desde que arranco el proceso");
                    }

                    VideoConfig? config = null;

                    // El SPS/PPS sale dentro del primer IDR. Se saca UNA vez y se
                    // manda en VideoConfig; a partir de ahi el visor lo conserva.
                    if (!flujo.ConfigEnviada && frameCodificado.IsKeyFrame)
                    {
                        var parametros = H264AnnexB.ParameterSets(
                            frameCodificado.Payload, flujo.Codificador.Codec == VideoCodec.H265);

                        // UN IDR SIN PARAMETER SETS SIGUE SIENDO UN IDR.
                        //
                        // Si el visor YA tiene el SPS/PPS de esta version, ese
                        // keyframe le sirve tal cual: no necesitaba
                        // configuracion nueva. Tirarlo era desperdiciar
                        // justamente el frame que alguien acababa de pedir para
                        // recuperarse, y ademas el mas caro de producir.
                        //
                        // Solo hay que esperar cuando el visor NO conoce la
                        // version: ahi un IDR suelto no le sirve de nada.
                        if (parametros.Length == 0)
                        {
                            if (!flujo.ElVisorSabeDescodificar)
                            {
                                Interlocked.Increment(ref cuenta.SinParametros);
                                continue;   // el siguiente IDR los traera
                            }
                        }
                        else
                        {
                            flujo.ConfigEnviada = true;
                            flujo.ConfigConocida = flujo.Version;

                            config = ConfigDe(frameCodificado, flujo, lienzo, parametros, opciones);
                        }
                    }

                    // SIN CONFIGURACION NO HAY NADA DESCODIFICABLE... LA PRIMERA VEZ.
                    //
                    // Aqui estaban los 894 frames que faltaban. ConfigEnviada se
                    // pone en false CADA VEZ que alguien pide un keyframe, y
                    // mientras este en false este continue tira todo. Antes eso
                    // costaba segundo y medio porque el MFT emitia un IDR cada
                    // treinta frames; con gop 900 -- puesto esta misma tarde para
                    // ahorrar ancho de banda -- pasa a costar medio minuto, y en
                    // esta maquina ForzarKeyframe() ni siquiera se respeta.
                    //
                    // Pero un visor que YA recibio la configuracion no la ha
                    // perdido porque alguien pida un IDR: tiene su SPS/PPS y
                    // puede seguir descodificando. Solo hace falta esperar
                    // cuando no la ha tenido NUNCA -- primera conexion o cambio
                    // de resolucion, que estrena version.
                    if (!flujo.ConfigEnviada && !flujo.ElVisorSabeDescodificar)
                    {
                        Interlocked.Increment(ref cuenta.SinConfig);
                        continue;
                    }

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

                    Interlocked.Increment(ref cuenta.Encolados);

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
                finally
                {
                    // SOLO LO QUE SOBRO, y en un finally porque este bucle sale
                    // por `continue` en una docena de sitios. Si la vuelta ya
                    // costo un intervalo entero -- porque la espera de la
                    // captura se agoto, o porque codificar tardo -- no se duerme
                    // nada y la siguiente empieza en el acto.
                    var falta = intervalo - (Stopwatch.GetTimestamp() - vuelta);

                    if (falta > 0)
                        cancellationToken.WaitHandle.WaitOne(
                            (int)(falta * 1000 / Stopwatch.Frequency));
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
    private static IReadOnlyList<EncodedFrame> Cronometrar(Func<IReadOnlyList<EncodedFrame>> codificar)
    {
        var desde = Stopwatch.GetTimestamp();

        try
        {
            return codificar();
        }
        finally
        {
            _codificar.Anotar((Stopwatch.GetTimestamp() - desde) * 1000.0 / Stopwatch.Frequency);
        }
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
    /// <summary>
    /// Envuelve una fase del arranque: etiqueta su excepcion Y LA CRONOMETRA.
    ///
    /// Solo etiquetaba. Cuando el tecnico dice que tarda en conectar, la
    /// auditoria del servidor enseña tres segundos entre pedir la sesion y que
    /// el host aparezca, y despues todavia falta la primera imagen -- que no se
    /// media en ningun sitio. Sin repartir esos segundos por fases, "tarda en
    /// abrir" es una sensacion contra la que no se puede trabajar.
    ///
    /// El reloj arranca en el PROCESO, no aqui: buena parte de esos segundos
    /// puede ser .NET levantandose, y eso se arregla de otra manera -- o no se
    /// arregla -- pero hay que saberlo antes de tocar nada.
    /// </summary>
    private static T Etiquetar<T>(RelayOptions opciones, string paso, Func<T> accion)
    {
        var desde = Stopwatch.GetTimestamp();

        try
        {
            var resultado = accion();

            opciones.Escribir(
                $"Arranque: {paso} en {(Stopwatch.GetTimestamp() - desde) * 1000.0 / Stopwatch.Frequency:0} ms " +
                $"({DesdeElProceso():0} ms desde que arranco el proceso)");

            return resultado;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"al {paso}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Milisegundos desde que Windows creo ESTE proceso, no desde que empezo
    /// nuestro codigo. La diferencia entre los dos es lo que cuesta levantar
    /// .NET, y es justo la parte que no se ve desde dentro.
    /// </summary>
    private static double DesdeElProceso()
    {
        try
        {
            return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        }
        catch (Exception)
        {
            return -1;
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

        /// <summary>Hay un escritorio delante ahora mismo. Solo sirve para no
        /// repetir el aviso en cada vuelta del bucle.</summary>
        public bool Tapada { get; set; }

        public bool ConfigEnviada { get; set; }

        /// <summary>
        /// La VERSION cuya configuracion llego a recibir el visor, o 0 si
        /// ninguna. Los dos conceptos que se habian confundido son:
        ///
        ///     ConfigEnviada    la config acompaña a este frame
        ///     ConfigConocida   el visor tiene el SPS/PPS de ESTA version
        ///
        /// Pedir un keyframe no invalida un SPS. Cambiar de resolucion si, y eso
        /// estrena version -- por eso se guarda el numero y no un booleano: con
        /// un booleano hay que acordarse de apagarlo en cada sitio donde nace
        /// una version nueva, y olvidarlo no da error, da imagen corrupta.
        /// En H.265 vale igual, con VPS delante.
        /// </summary>
        public uint ConfigConocida { get; set; }

        /// <summary>El visor puede descodificar lo que se le mande ahora mismo.</summary>
        public bool ElVisorSabeDescodificar => ConfigConocida == Version;

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

            return
            [
                new Flujo
                {
                    DisplayId = pedida,
                    Info = elegida,
                    Captura = unica,
                    BitrateBase = ControlBitrate.PorResolucion(unica.Width, unica.Height, 1.0),

                    // Con su propia etiqueta: "abrir las capturas" cubria esto
                    // tambien, y se leia un fallo del codificador como si fuera
                    // de la captura -- que para entonces ya funcionaba.
                    Codificador = Etiquetar(opciones, "crear el codificador", () => Codificar(
                        unica.Device, unica.Width, unica.Height,
                        unica.AdapterLuid, unica.AdapterVendorId, opciones)),
                    Version = SiguienteVersion(cuenta),
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
                    Version = SiguienteVersion(cuenta),

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

    /// <summary>La primera linea de un mensaje. Un ResultCode de Media
    /// Foundation trae parrafos y en un aviso solo cabe la cabecera.</summary>
    private static string Primera(string mensaje)
        => mensaje.ReplaceLineEndings("\n").Split('\n')[0].Trim();

    /// <summary>La configuracion que acompaña a un keyframe con parameter sets
    /// nuevos. Sacada del bucle para que la rama de "sin parametros" se lea de
    /// una vez, que es donde estaba el fallo.</summary>
    private static VideoConfig ConfigDe(
        EncodedFrame frame, Flujo flujo, Lienzo lienzo, byte[] parametros, RelayOptions opciones)
        => new()
        {
            ConfigVersion = flujo.Version,
            Codec = flujo.Codificador.Codec,
            Width = (uint)frame.Width,
            Height = (uint)frame.Height,
            FramesPerSecond = (uint)FpsDeclarado(opciones),
            BitrateBitsPerSecond = (uint)flujo.BitrateBase,
            ParameterSets = Google.Protobuf.ByteString.CopyFrom(parametros),
            VisibleWidth = (uint)frame.Width,
            VisibleHeight = (uint)frame.Height,

            DisplayId = (uint)flujo.DisplayId,
            LayoutX = (uint)flujo.LayoutX,
            LayoutY = (uint)flujo.LayoutY,
            CanvasWidth = (uint)lienzo.Ancho,
            CanvasHeight = (uint)lienzo.Alto
        };

    private static H264Encoder Codificar(
        ID3D11Device device, int ancho, int alto, Vortice.Luid luid, uint vendor,
        RelayOptions opciones)
    {
        var bitrate = ControlBitrate.PorResolucion(ancho, alto, _calidad);
        var fps = FpsDeclarado(opciones);

        // LA ESCALERA, Y CADA PELDANO DICHO EN VOZ ALTA.
        //
        //     H.265 por hardware
        //     H.264 por hardware
        //     H.264 por software, a proposito y avisando
        //
        // El ultimo peldano es el que faltaba. Antes se aceptaba cualquier MFT
        // que respondiera, y un codificador de Media Foundation por software
        // esta pensado para transcodificar archivos, no para escritorio remoto
        // en vivo. Asi acabo el Xeon donde acabo: sin que nadie lo eligiera y
        // sin que quedara escrito en ningun sitio.
        //
        // NO se cae a VP9, y no es por descarte. En esa maquina el MFT tarda
        // 3.5 ms de los 28 que cuesta el host: el trabajo esta en bajar la
        // imagen y convertirla -- 18.4 + 6.4 -- y eso no lo toca ningun codec.
        var intentos = _codec == VideoCodec.H265
            ? new[] { (VideoCodec.H265, true), (VideoCodec.H264, true), (VideoCodec.H264, false) }
            : new[] { (VideoCodec.H264, true), (VideoCodec.H264, false) };

        H264Encoder? codificador = null;

        for (var i = 0; i < intentos.Length; i++)
        {
            var (codec, hardware) = intentos[i];
            var ultimo = i == intentos.Length - 1;

            try
            {
                codificador = new H264Encoder(
                    device, ancho, alto, fps, bitrate, luid, vendor,
                    codec: codec, soloHardware: hardware);
            }
            catch (Exception ex) when (!ultimo)
            {
                // El ULTIMO no se atrapa: si tampoco hay codificador por
                // software no queda nada que probar, y la excepcion tiene que
                // salir con su motivo en vez de convertirse en un null.
                Avisar(opciones,
                    $"Sin {(codec == VideoCodec.H265 ? "H.265" : "H.264")} por " +
                    $"{(hardware ? "hardware" : "software")}: {Primera(ex.Message)}");

                continue;
            }

            if (!hardware)
            {
                Avisar(opciones,
                    $"Sin codificador por hardware en esta PC: se usa " +
                    $"{codificador.Capabilities.Name} por SOFTWARE.");
            }

            // El resultado REAL, no el pedido. Si no se corrigiera, el bucle de
            // fuera veria H.265 pedido y H.264 en marcha y reharia la captura
            // cada medio segundo para siempre.
            _codec = codec;
            break;
        }


        // SIN GPU, MENOS PIXELES.
        //
        // En una maquina sin tuberia de video la conversion a NV12 y el
        // codificador los hace la CPU, y el coste va con el AREA. Codificar
        // 1280x1024 ahi cuesta cuatro veces mas que a media resolucion, y era la
        // diferencia entre ver el escritorio y verlo medio segundo tarde.
        //
        // Se decide DESPUES de construirlo porque quien sabe si hay tuberia de
        // video es el propio codificador, y averiguarlo por fuera seria repetir
        // aqui la misma cadena de comprobaciones que el ya hace. Construir dos
        // veces cuesta una vez, al abrir la sesion; el escalado se paga en cada
        // frame durante toda la sesion.
        // El compilador no puede saber que el ultimo intento no atrapa nada y
        // por tanto siempre asigna o lanza. Se lo decimos con una condicion que
        // en la practica nunca es cierta, en vez de con un `!` que la callaria
        // sin comprobar nada.
        if (codificador is null)
            throw new VideoEncoderUnavailableException("Ningun codificador quedo en pie.");

        if (!codificador.PorCpu)
            return codificador;

        var (a, b) = Reducir.Cabe(ancho, alto);

        if (a == ancho && b == alto)
            return codificador;

        Avisar(opciones,
            $"Sin GPU para convertir: se codifica a {a}x{b} en vez de {ancho}x{alto} " +
            $"({100 - a * b * 100L / (ancho * alto)}% menos de pixeles).");

        codificador.Dispose();

        return new H264Encoder(
            device, a, b, fps, ControlBitrate.PorResolucion(a, b, _calidad),
            luid, vendor, ancho, alto);
    }

    /// <summary>
    /// Rehace el codificador conservando la captura, y estrena version.
    ///
    /// Es la salida determinista cuando un MFT ignora el keyframe forzado: uno
    /// recien creado emite un IDR con su SPS en el primer frame que le entra,
    /// haga lo que haga con ForceKeyFrame. Cuesta unos cientos de milisegundos y
    /// solo ocurre cuando la via normal ya fallo.
    ///
    /// Si la creacion falla se deja el que habia: uno que no da keyframes es
    /// mejor que ninguno, y el aviso queda escrito.
    /// </summary>
    private static void RehacerCodificador(Flujo flujo, RelayOptions opciones, Contadores cuenta)
    {
        H264Encoder nuevo;

        try
        {
            // Del capturador vigente y no de la pantalla original: si esto se
            // relevo a GDI antes, el dispositivo bueno es el suyo.
            var captura = flujo.Captura;

            nuevo = Codificar(
                captura.Device, captura.Width, captura.Height,
                captura.AdapterLuid, captura.AdapterVendorId, opciones);
        }
        catch (Exception ex)
        {
            Avisar(opciones, $"No se pudo rehacer el codificador: {Primera(ex.Message)}");
            return;
        }

        var anterior = flujo.Codificador;

        flujo.Codificador = nuevo;
        flujo.Version = SiguienteVersion(cuenta);
        flujo.ConfigEnviada = false;

        // ConfigConocida NO se toca: al estrenar version deja de coincidir sola,
        // y el visor tiene que esperar la configuracion nueva -- que es correcto,
        // porque el SPS que tenia describe al codificador que se acaba de tirar.
        anterior.Dispose();
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
            flujo.Version = SiguienteVersion(cuenta);
            flujo.ConfigEnviada = false;

            // ConfigConocida NO se toca: al estrenar version deja de coincidir
            // sola, que es justo lo que se queria. Una version nueva significa
            // que el SPS que tiene el visor descodifica otra cosa, y ahi esperar
            // es correcto.

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

    /// <summary>
    /// Encola SIN DESCARTAR, esperando si hace falta.
    ///
    /// NO TODO ES DESCARTABLE. El cursor y las medidas se pueden perder sin
    /// consecuencia: viene otro detras en milisegundos. Pero la lista de
    /// pantallas y el portapapeles son ESTADO -- pasan una vez y describen como
    /// esta el mundo -- y con la cola llena TryWrite los tiraba en silencio: el
    /// desplegable del visor se quedaba sin el monitor nuevo, o una copia no
    /// llegaba, y nada lo volvia a intentar.
    ///
    /// La espera es corta y acotada. Esto lo llama el hilo de captura, y
    /// bloquearlo mucho seria peor que perder el mensaje; pero la cola son ocho
    /// frames de video que el hilo de red esta vaciando todo el rato, asi que en
    /// la practica no se espera.
    /// </summary>
    private static void Fiable(
        System.Threading.Channels.ChannelWriter<Enviable> salida, RemotePacket paquete,
        CancellationToken cancellationToken)
    {
        var envoltorio = new Enviable(null, null, paquete);

        if (salida.TryWrite(envoltorio))
            return;

        // EL PLAZO TIENE QUE LLEGARLE AL WriteAsync, no solo al Wait.
        //
        // Con Wait(2s) el que se rendia era el que esperaba: el WriteAsync
        // seguia vivo por dentro, con su token intacto, aguardando hueco. Se
        // creia haber desistido y en realidad quedaba un escritor pendiente, y
        // varios podian acumularse y salir todos tarde y a destiempo.
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        plazo.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            salida.WriteAsync(envoltorio, plazo.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Medir(RelayOptions opciones, string texto)
    {
        opciones.Escribir(texto);
        _medir?.Invoke(texto);
    }

    /// <summary>
    /// El reloj de la sesion. Era un TERCER origen distinto -- contaba desde el
    /// arranque del sistema, no de la sesion -- y lo usan los Ping para medir el
    /// RTT. Que coincida con el de las capturas no cambia esa medida, pero
    /// significa que ya no hay dos ideas de "ahora" en el mismo proceso.
    /// </summary>
    private static long Ahora() => Reloj.Ahora();

    /// <summary>
    /// Lo que el codificador dice sobre los B-frames. Tiene que ser 0:
    /// cualquier otra cosa se ve como movimiento que retrocede.
    ///
    /// Es estatico porque va en la linea de medidas, que se arma en la bomba de
    /// salida y no tiene los flujos a mano. Lo primero que hizo fue irse a la
    /// linea de identidad -- o sea al registro de la PC de planta, o sea a un
    /// sitio donde el tecnico que esta mirando el problema no puede leerlo. Es
    /// el mismo error que ya se corrigio una vez con los avisos de captura.
    /// </summary>
    private static int _bframes = -1;

    /// <summary>
    /// Hilos y punto de la balanza calidad/velocidad que dice el codificador.
    /// -1 = no contesta, y en hardware es lo normal: ahi no significan nada.
    /// </summary>
    private static int _hilos = -1, _prisa = -1;

    /// <summary>Esperas de la captura que expiraron sin novedades, y cuanto se
    /// espera en cada una. Van juntos: 900 timeouts con 33 ms de espera son
    /// treinta segundos de pantalla quieta, y con 100 ms serian noventa.</summary>
    private static long _timeouts;
    private static int _esperaCaptura;

    /// <summary>Frames entre keyframes que dice el codificador. Si sale bajo --
    /// treinta y pico -- es que ignoro el GOP y esta gastando media transmision
    /// en repetir lo que ya se veia.</summary>
    private static int _gop = -1;
    /// <summary>Para decir el instante de la primera imagen una sola vez.</summary>
    private static bool _primeraDicha;

    /// <summary>El sonido de la sesion. Null hasta que se abren los flujos.</summary>
    private static Audio.BombaDeSonido? _sonido;

    private static bool _porHardware;
    private static string _codificadorNombre = "?";

    /// <summary>Si el host corre como SYSTEM. Sin eso, la entrada no entra en
    /// ventanas elevadas.</summary>
    private static bool _comoSystem;

    /// <summary>El escritorio que se esta capturando: Default, Winlogon, o el
    /// que sea.</summary>
    private static string _escritorio = "?";

    private static string Bes()
        => _bframes switch
        {
            0 => "0",
            < 0 => "?",
            var cuantos => $"{cuantos} (!)"
        };

    private static InputInjector? _entrada;

    /// <summary>
    /// Entrada aplicada y rechazada por los inyectores ANTERIORES.
    ///
    /// El inyector se rehace en cada salto de escritorio y en cada cambio de
    /// pantalla, y con el se iban sus cuentas a cero. Esa cifra es la que
    /// responde "la entrada llega de verdad al otro lado", y en una sesion larga
    /// -- donde mas falta hace -- contaba solo desde el ultimo cambio.
    /// </summary>
    private static long _aplicadas, _rechazadas;

    private static void RelevarInyector(InputInjector nuevo)
    {
        if (_entrada is { } viejo)
        {
            _aplicadas += viejo.Applied;
            _rechazadas += viejo.Rejected;
        }

        _entrada = nuevo;
    }

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
        catch (Exception ex)
        {
            // CUALQUIER fallo, no solo el nuestro.
            //
            // Antes aqui solo se atrapaba ScreenCaptureUnavailableException, que
            // es la que se lanza para los HRESULT conocidos -- acceso denegado,
            // sesion desconectada, adaptador sin duplicacion. Un HRESULT que no
            // estuviera en esa lista salia como InvalidOperationException, se
            // saltaba el respaldo entero y mataba la sesion.
            //
            // Paso en el servidor, un Xeon Silver sin grafica: su adaptador de
            // gestion contesta E_INVALIDARG al crear el dispositivo D3D11, que
            // no es ninguno de los casos previstos. El visor solo veia "Fallo al
            // capturar (intento 9)" mientras GDI, que ahi funciona, no llegaba a
            // intentarse nunca.
            //
            // Se dice cual fue el motivo: si el respaldo tambien falla, hacen
            // falta los dos errores para saber que pasa.
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

    /// <summary>Puntos donde el tecnico solto archivos y hay que pegar. Va por
    /// cola y no directo porque quien puede teclear es el hilo de entrada: es el
    /// unico atado al escritorio activo.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<PasteAt> Pegados = new();

    /// <summary>
    /// Lo levanta el hilo de RED en cuanto encola entrada.
    ///
    /// Antes el hilo que aplica dormia 20 ms y volvia a mirar. Cada movimiento y
    /// cada clic esperaban ahi una media de diez milisegundos ANTES de tocar la
    /// maquina remota, y solo entonces empezaba lo demas: capturar, codificar,
    /// mandar, pintar. Diez milisegundos regalados en el tramo donde mas se
    /// notan, porque son los que van por delante de todo el resto.
    ///
    /// Y no salian en ninguna medida. `verse` arranca al escribir en la red y
    /// `capturar` al pedir el frame: los dos empiezan a contar despues de esto.
    /// Por eso la linea decia 3.7 ms mientras el escritorio iba detras del raton.
    ///
    /// Se conserva el tick de 20 ms como TIEMPO DE ESPERA, no como sueño: el
    /// bucle tambien vigila el salto de escritorio, las teclas hundidas y el
    /// bloqueo de entrada, y esos si quieren mirarse cada tanto aunque nadie
    /// toque el raton.
    /// </summary>
    private static readonly AutoResetEvent HayEntrada = new(false);

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
                        HayEntrada.Set();
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

                            // ESTAS DOS TAMPOCO se hacen aqui, aunque no sean
                            // SendInput: la exencion de BlockInput es del hilo
                            // que bloquea, y ese tiene que ser el que inyecta.
                            HostAction.Types.Kind.HostActionBlockInput => Pedir(1),
                            HostAction.Types.Kind.HostActionUnblockInput => Pedir(0),

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

                    case RemotePacket.PayloadOneofCase.SelectAudio:
                        // Se atiende en el hilo de RED, no en el de captura: no
                        // es SendInput, no depende del escritorio, y pasarlo por
                        // la cola de captura lo retrasaria hasta el siguiente
                        // frame -- que con la pantalla quieta puede no llegar.
                        _sonido?.Encender(paquete.SelectAudio.Enabled);
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

                    case RemotePacket.PayloadOneofCase.PasteAt:
                        // Como la entrada: lo atiende el hilo que puede teclear.
                        Pegados.Enqueue(paquete.PasteAt);
                        HayEntrada.Set();
                        break;

                    case RemotePacket.PayloadOneofCase.VirtualDisplay:
                        // EN EL HILO DE RED, como las acciones de la Fase 21.
                        // Instalar un driver tarda segundos y no depende del
                        // escritorio activo; pasarlo por la cola de captura lo
                        // retrasaria hasta el siguiente frame, que con la
                        // pantalla quieta puede no llegar nunca.
                        if (paquete.VirtualDisplay.Enable)
                        {
                            var nueva = PantallaVirtual.Encender(out var dijo);

                            opciones.Escribir(dijo);

                            // Cambiar _pantalla es lo unico que hace falta: el
                            // bucle de captura ya rehace duplicador, codificador
                            // y lista de pantallas cuando ve que no coincide.
                            if (nueva >= 0)
                                _pantalla = nueva;
                        }
                        else
                        {
                            // A la principal ANTES de quitarla: si se quita
                            // mientras se esta capturando, el duplicador se
                            // queda sobre una salida que ya no existe y la
                            // sesion se cae en vez de volver.
                            _pantalla = 0;
                            Thread.Sleep(300);

                            PantallaVirtual.Apagar(out var dijo);
                            opciones.Escribir(dijo);
                        }

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
