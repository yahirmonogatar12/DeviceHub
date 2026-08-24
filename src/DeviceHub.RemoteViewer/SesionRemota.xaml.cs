using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Contracts = DeviceHub.Remote.Contracts;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteViewer.Decode;
using DeviceHub.RemoteViewer.Input;
using DeviceHub.RemoteViewer.Render;
using Grpc.Core;
using Grpc.Net.Client;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Fase 5: la mitad receptora, ahora por el relay en vez de desde un archivo.
///
///   ViewerChannel -> reensamblado -> decodificador H.264 -> D3D11 -> swapchain
///
/// El decodificador y el presentador son EXACTAMENTE los de la Fase 3. Lo unico
/// que cambia es de donde salen los bytes, y eso es a proposito: si el video se
/// ve mal, el sospechoso es el transporte y no el visor.
///
/// Fases 9 y 10: de vuelta viajan Ping y la ENTRADA del tecnico -- raton y
/// teclado -- con coordenadas normalizadas 0..1, nunca en pixeles.
/// </summary>
public partial class SesionRemota : UserControl
{
    /// <summary>Lo que va en la pestaña: contra QUE maquina se esta actuando.
    /// Con cuatro sesiones abiertas es lo unico que importa antes de tocar una
    /// tecla.</summary>
    public string Titulo { get; }

    /// <summary>Sin nombre queda el uuid de la sesion, y entero no cabe en una
    /// pestaña.</summary>
    private static string Corto(string sesion)
        => sesion.Length <= 8 ? sesion : sesion[..8];

    private readonly string _servidor;
    private readonly string _sesion;

    /// <summary>El identificador al que se ato el ticket. No es el hostname de
    /// Windows, y confundirlos hace que todo salga rechazado por WrongMachine.</summary>
    private readonly string _machineId;
    private readonly bool _permitirSinConfianza;

    /// <summary>Pin SPKI del servidor. No es secreto -- es el hash de una clave
    /// publica -- asi que puede llegar por argumento, al contrario que el
    /// ticket.</summary>
    private readonly string _pin;

    private readonly CancellationTokenSource _cancelacion = new();

    /// <summary>SHA-256 de la SubjectPublicKeyInfo, igual que
    /// DeviceHub.Contracts.PublicKeyPin. Son cuatro lineas y evita que el visor
    /// arrastre todo el ensamblado de contratos del agente.</summary>
    private static string PinSpki(System.Security.Cryptography.X509Certificates.X509Certificate certificado)
    {
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
            .LoadCertificate(certificado.GetRawCertData());

        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            cert.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <param name="ticket">
    /// LO LEE LA CONSOLA, no esta sesion.
    ///
    /// Antes cada sesion llamaba a BootstrapTicket.Read() por su cuenta, que lee
    /// de stdin. Con una sesion por proceso daba igual; con varias pestañas en el
    /// mismo proceso serian dos hilos leyendo la misma tuberia, y el ticket de
    /// una acabaria en la otra.
    /// </param>
    public SesionRemota(
        string servidor, string sesion, string machineId, bool permitirSinConfianza,
        string pin = "", string? ticket = null, string? titulo = null)
    {
        InitializeComponent();

        _servidor = servidor;
        _sesion = sesion;
        _machineId = machineId;
        _permitirSinConfianza = permitirSinConfianza;
        _pin = pin;
        _ticket = ticket;

        // La maquina CONTROLADA, que llega aparte. Ni machineId ni el hostname
        // sirven: el primero es la PC del tecnico -- el mismo para todas sus
        // pestañas -- y el segundo no se sabe hasta que el host conteste.
        Titulo = string.IsNullOrWhiteSpace(titulo) ? Corto(sesion) : titulo;

        Loaded += (_, _) => new Thread(Ejecutar)
        {
            IsBackground = true,
            Name = "devicehub-relay-viewer"
        }.Start();

        // Del WndProc de la ventana hija, no de los eventos de WPF: el video se
        // dibuja en una ventana Win32 encima del arbol visual, y los mensajes del
        // raton van a ella. Con los eventos de WPF el video se veia y no se podia
        // controlar nada.
        Video.Raton += RatonRemoto;


    }

    /// <summary>
    /// Esta sesion pasa a ser la de delante: la pestaña elegida, y su ventana
    /// con el foco.
    ///
    /// LAS DOS COSAS TIENEN QUE DARSE. Con varias pestañas abiertas, engancharse
    /// el teclado por tener el foco de la ventana enviaria la tecla Windows a la
    /// PC de la pestaña que NO se esta mirando.
    /// </summary>
    public void Activar()
    {
        EnviarPortapapeles(this, EventArgs.Empty);

        // El gancho SOLO mientras esta sesion esta delante. Instalado a secas se
        // tragaria la tecla Windows del tecnico tambien cuando esta en sus
        // propias aplicaciones, que es lo contrario de lo que se quiere.
        EngancharTeclado();
    }

    /// <summary>
    /// Deja de ser la de delante: se cambio de pestaña, o la ventana perdio el
    /// foco.
    ///
    /// SE SUELTA EN LOS DOS LADOS. Soltar solo el gancho local dejaba teclas
    /// pegadas en la PC remota: mantienes Ctrl, haces clic en otra ventana tuya,
    /// sueltas Ctrl -- y ese KeyUp ya no lo ve el visor. El host recibio el Down
    /// y nunca recibe el Up, y como la conexion sigue viva no entra nada de la
    /// logica de reconexion. Ese Ctrl se queda hundido hasta que alguien lo note.
    ///
    /// Con pestañas pasa lo mismo cambiando de una a otra, y ahi es peor: la PC
    /// que se queda con el Ctrl hundido ya no es la que el tecnico esta mirando.
    /// </summary>
    public void Desactivar()
    {
        SoltarTeclado();
        PedirSoltarEntrada();
    }

    /// <summary>
    /// Se cierra la pestaña o la consola entera.
    ///
    /// Cancelar si, disponer NO: el hilo de reconexion puede estar esperando en
    /// _cancelacion.Token.WaitHandle, y disponer el origen mientras alguien
    /// espera en su handle lanza.
    /// </summary>
    public void Cerrar()
    {
        Desactivar();
        _cancelacion.Cancel();
    }

    /// <summary>
    /// El gancho de bajo nivel, puesto solo mientras el visor tiene el foco.
    ///
    /// Sin el, la tecla Windows y Alt+Tab actuan en la PC DEL TECNICO: el shell
    /// las atiende antes que la aplicacion con el foco, asi que WPF no llega a
    /// verlas y no hay nada que reenviar.
    /// </summary>
    private GanchoTeclado? _ganchoTeclado;

    private void EngancharTeclado()
    {
        if (_ganchoTeclado is not null || !MenuTeclas.IsChecked)
            return;

        try
        {
            _ganchoTeclado = new GanchoTeclado(TeclaDeWindows);
        }
        catch (Exception ex)
        {
            // Una directiva de grupo puede prohibirlo. Se sigue sin el: todo lo
            // demas del teclado ya funciona por PreviewKeyDown.
            Nota($"No se pudo capturar el teclado: {ex.Message}");
        }
    }

    private void SoltarTeclado()
    {
        _ganchoTeclado?.Dispose();
        _ganchoTeclado = null;
    }

    private void AlternarTeclas(object sender, RoutedEventArgs e)
    {
        if (MenuTeclas.IsChecked)
            EngancharTeclado();
        else
            SoltarTeclado();
    }

    /// <summary>Decide si una pulsacion se queda aqui y viaja a la PC remota. La
    /// regla vive en <see cref="TeclasDeWindows"/>; esto solo la aplica.</summary>
    private bool TeclaDeWindows(TeclaCruda tecla)
    {
        var nuestra = TeclasDeWindows.LaAtiendeElShell(
            tecla.VirtualKey,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));

        if (!nuestra)
            return false;

        // El scan code REAL, que aqui se conoce. Tecla() manda 0 y deja que el
        // host lo deduzca con MapVirtualKey, que para estas no siempre acierta.
        Enviar(new InputEvent
        {
            Key = new KeyEvent
            {
                VirtualKey = tecla.VirtualKey,
                ScanCode = tecla.ScanCode,
                Pressed = tecla.Pulsada,
                Extended = tecla.Extendida
            }
        });

        return true;
    }

    /// <summary>Token de reconexion vigente. SOLO en RAM: ni disco, ni log, ni
    /// argumento. Se rota en cada HelloAccepted.</summary>
    private string? _token;

    /// <summary>El ticket de arranque. Se usa UNA vez y se borra: a partir de
    /// ahi quien sostiene la sesion es el token.</summary>
    private string? _ticket;

    private void Ejecutar()
    {
        try
        {
            if (_ticket is null)
            {
                Mostrar("Falta el ticket. Se pasa por stdin, nunca por linea de comandos.");
                return;
            }

            MediaFactory.MFStartup(true).CheckError();

            // Bucle de reconexion. Mientras haya token vigente se vuelve a la
            // MISMA sesion sin gastar un ticket nuevo; cuando el servidor lo
            // rechaza -- porque paso la gracia -- se termina.
            var corte = DateTimeOffset.UtcNow;

            while (!_cancelacion.IsCancellationRequested)
            {
                try
                {
                    corte = DateTimeOffset.UtcNow;
                    RecibirAsync().GetAwaiter().GetResult();
                }
                catch (RpcException ex) when (PuedeReconectar(corte))
                {
                    Mostrar(
                        $"Conexion perdida ({ex.StatusCode}). Reintentando en {_espera.TotalSeconds:0.0} s...\n" +
                        $"{_cierre ?? string.Empty}");

                    // Espera CANCELABLE: con Thread.Sleep, cerrar la ventana
                    // dejaba el hilo dormido y el proceso sin terminar.
                    if (_cancelacion.Token.WaitHandle.WaitOne(_espera))
                        break;

                    _espera = _espera < TimeSpan.FromSeconds(5)
                        ? _espera + _espera
                        : TimeSpan.FromSeconds(5);

                    continue;
                }

                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Mostrar($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>
    /// Espera antes del proximo intento: 250 ms y doblando hasta 5 s. Se
    /// reinicia en cada HelloAccepted, o sea cada vez que la reconexion funciona
    /// de verdad -- si no, un microcorte a los diez minutos empezaria esperando
    /// los 5 s del corte anterior.
    /// </summary>
    private TimeSpan _espera = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Se reintenta durante un minuto con el RELOJ DE AQUI, no con el
    /// `reconnect_until` del servidor: esa marca viene de otra maquina y si
    /// llegara mal -- o no llegara -- la comparacion diria que la gracia ya paso
    /// y no se reintentaria nunca. El servidor rechaza el token cuando toca, y
    /// esa es la autoridad de verdad.
    /// </summary>
    private bool PuedeReconectar(DateTimeOffset desde)
        => _token is not null
           && !_cancelacion.IsCancellationRequested
           && DateTimeOffset.UtcNow - desde < TimeSpan.FromMinutes(1);

    private async Task RecibirAsync()
    {
        // UN token por intento, enlazado al de la ventana.
        //
        // Antes esto usaba el de la ventana directamente y el `finally` de abajo
        // lo cancelaba al terminar. Efecto: despues del PRIMER corte el bucle de
        // reconexion quedaba muerto para siempre -- su condicion mira ese mismo
        // token -- y la sesion se congelaba con la excepcion cruda en la barra en
        // vez de reintentar. La Fase 14 figuraba como hecha en el visor y no lo
        // estaba.
        using var intento = CancellationTokenSource.CreateLinkedTokenSource(_cancelacion.Token);
        var hwnd = Video.WaitForWindow(TimeSpan.FromSeconds(5));

        if (hwnd == IntPtr.Zero)
        {
            Mostrar("La superficie de video no llego a crearse.");
            return;
        }

        var opciones = new GrpcChannelOptions();

        if (!string.IsNullOrWhiteSpace(_pin))
        {
            // El MISMO pin SPKI con el que el dashboard habla con el servidor. Se
            // adelanto desde la Fase 17 porque la 19 da acceso a la pantalla de
            // login: conceder eso sobre un canal que no valida al servidor seria
            // descuidado.
            opciones.HttpHandler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificado, _, _) =>
                        certificado is not null
                        && PinSpki(certificado) == _pin
                }
            };
        }
        else if (_permitirSinConfianza)
        {
            // Escotilla de laboratorio. Sin pin configurado en el dashboard no
            // hay con que validar, y se avisa en la barra de estado en vez de
            // fingir que la sesion es segura.
            Mostrar("AVISO: no se valida el certificado del servidor. Configura ServerPin en el dashboard.");

            opciones.HttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }

        using var canal = GrpcChannel.ForAddress(_servidor, opciones);
        var cliente = new RemoteRelayService.RemoteRelayServiceClient(canal);

        using var llamada = cliente.ViewerChannel(cancellationToken: intento.Token);

        await llamada.RequestStream.WriteAsync(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            Hello = new Hello
            {
                Role = RemoteRole.Viewer,
                MachineId = _machineId,

                // Exclusivos: arranque con ticket, reconexion con token. Mandar
                // los dos es un error de protocolo y el relay lo rechaza.
                Ticket = _token is null ? _ticket : string.Empty,
                ReconnectToken = _token ?? string.Empty,
                Capabilities = new RemoteCapabilities
                {
                    MaxProtocolVersion = RemoteSessionProtocol.Version,
                    // LOS DOS, y ahora es verdad: el visor monta el
                    // descodificador que diga VideoConfig. Anunciar solo H.264
                    // mientras se descodifica H.265 es un contrato que miente, y
                    // muerde el dia que el relay negocie de verdad con esto.
                    Codecs = { VideoCodec.H264, VideoCodec.H265 },
                    SupportsCursor = true,
                    SupportsInput = true
                }
            }
        }, intento.Token);

        // LO QUE QUEDO DE LA CONEXION ANTERIOR NO SE REPRODUCE.
        //
        // _salida es de la VENTANA y no de la conexion, asi que al caerse la red
        // se queda dentro lo que no llego a salir. Reproducirlo ahora seria
        // aplicar en la PC remota clics y teclas de hace medio minuto, contra
        // una pantalla que ya no es la que el tecnico estaba mirando -- y encima
        // por delante del ReleaseInput, que quedaria al final de la fila.
        //
        // Nada de lo pendiente sigue valiendo: el relay nunca lo recibio, los
        // acuses son de un stream muerto y los frames que confirmaban ya no
        // existen.
        _salida.Reiniciar();

        var latidos = LatirAsync(llamada.RequestStream, intento.Token);

        using var device = VideoPresenter.CreateDevice();

        // UN DECODIFICADOR Y UNA CONFIG POR PANTALLA.
        //
        // Desde que el host manda un flujo por monitor aqui llegan N flujos
        // independientes, cada uno con su SPS. Compartir decodificador haria que
        // los frames de una pantalla se descodificaran con los parametros de la
        // otra, y eso no da error: da imagen corrupta.
        var decodificadores = new Dictionary<uint, H264Decoder>();
        var configs = new Dictionary<uint, VideoConfig>();

        VideoPresenter? presentador = null;

        // Y UN REENSAMBLADOR POR PANTALLA, por el mismo motivo y con mas
        // consecuencias.
        //
        // El reensamblador monta UN frame a la vez y descarta como atrasado todo
        // id menor o igual al ultimo completado. Con dos hilos de captura
        // escribiendo en el mismo canal, los trozos llegan entrelazados:
        //
        //   p0/frame100 chunk0 | p1/frame101 chunk0 | p0/frame100 chunk1 | ...
        //
        // Compartido, el chunk0 del 101 abandona el 100 a medias y el chunk1 del
        // 100 llega ya como atrasado. La pantalla con frames de varios trozos --
        // la que mas se mueve, o la que acaba de mandar su IDR -- pierde
        // practicamente todo, y eso se ve como una imagen que carga una vez y se
        // queda quieta.
        //
        // Es la separacion que RustDesk tiene de serie: cada display lleva su
        // manejador y su estado, y nada de una pantalla toca el de la otra.
        var montadores = new Dictionary<uint, VideoFrameAssembler>();

        var proceso = Process.GetCurrentProcess();
        var ramInicio = proceso.PrivateMemorySize64;

        // Trozos por pantalla. Con varios flujos, "chunks 534" no dice si los
        // dos estan llegando o si uno se quedo mudo, que es exactamente la
        // diferencia entre un problema de red y uno de captura.
        var porPantalla = new Dictionary<uint, long>();

        // Cuantos frames llevaba abandonados cada reensamblador la ultima vez
        // que se miro. Lo que dispara la peticion de IDR es que ESTE numero
        // suba, no su valor.
        var perdidasVistas = new Dictionary<uint, long>();

        // LAS MEDIDAS SON DE AHORA, LOS CONTADORES SON DE LA SESION.
        //
        // Un contador -- frames, chunks, perdidas -- cuenta desde el principio y
        // asi tiene que ser: sirve para saber que ha pasado. Una MEDIDA de
        // rendimiento promediada desde el principio no sirve para nada, porque
        // la pantalla remota se pasa la mayor parte del tiempo quieta y arrastra
        // la media al suelo mientras la imagen va fina.
        var ritmo = new Ritmo();

        // Lo ultimo que conto el host de si mismo. Es la mitad que falta para
        // decidir donde esta el techo: aqui se ve la red y el descodificador,
        // pero no cuanto se queda el codificador de la PC de planta por el
        // camino.
        var medidasDelHost = string.Empty;

        // Lo ultimo que dijo el host: por que capturador tiro, si tuvo que
        // relevarse, si le falta algo. No se pisa con las notas de aqui.
        var avisoDelHost = string.Empty;

        // Y lo mismo con los tiempos de descodificado: los ultimos 600, que a 30
        // FPS son los ultimos 20 s. De paso deja de crecer sin limite -- en una
        // sesion de ocho horas eran cien mil entradas para calcular dos
        // percentiles.
        const int MuestrasDeDecode = 600;
        var decodificaciones = new Queue<long>();
        long chunks = 0, reconstruidos = 0, decodificados = 0, pintados = 0;
        long cambiosConfig = 0, idr = 0;

        var reloj = Stopwatch.StartNew();
        var siguienteAviso = TimeSpan.Zero;

        try
        {
            while (await llamada.ResponseStream.MoveNext(intento.Token))
            {
                var paquete = llamada.ResponseStream.Current;

                switch (paquete.PayloadCase)
                {
                    case RemotePacket.PayloadOneofCase.VideoConfig:
                    {
                        var nueva = paquete.VideoConfig;
                        var pantalla = nueva.DisplayId;

                        // El relay REPITE la configuracion vigente cada vez que
                        // se recupera de una perdida, para que un viewer que se
                        // la hubiera perdido pueda descodificar el IDR que viene
                        // detras. Repetida no es nueva: rehacer el decodificador
                        // aqui costaba 45 ms y llenaba la cola del relay, que
                        // provocaba otro descarte y otra repeticion. Un bucle que
                        // se alimentaba solo.
                        if (configs.TryGetValue(pantalla, out var previa)
                            && previa.ConfigVersion == nueva.ConfigVersion)
                        {
                            break;
                        }

                        cambiosConfig++;
                        configs[pantalla] = nueva;

                        // El cambio se completo: se limpia el "Cambiando a..."
                        // que si no se queda pegado aunque ya no sea verdad.
                        Nota(configs.Count > 1
                            ? $"{configs.Count} pantallas en {nueva.CanvasWidth}x{nueva.CanvasHeight}"
                            : $"{nueva.Width}x{nueva.Height}");

                        // EL REENSAMBLADOR SE TIRA CON EL FLUJO VIEJO.
                        //
                        // Guarda cual fue el ultimo frame completado y descarta
                        // como atrasado todo id menor o igual. Un host que
                        // reinicie su numeracion -- uno viejo, o cualquier caso
                        // que no hayamos previsto -- dejaria la imagen congelada
                        // sin un solo error. Una config nueva ES un flujo nuevo:
                        // no hay nada del anterior que conservar.
                        montadores[pantalla] = new VideoFrameAssembler();

                        // El LIENZO, que con varias pantallas no es el tamano de
                        // ninguna. El host lo manda para que aqui se reserve el
                        // sitio antes de que hayan llegado todos los flujos.
                        var ancho = (int)(nueva.CanvasWidth > 0 ? nueva.CanvasWidth : nueva.Width);
                        var alto = (int)(nueva.CanvasHeight > 0 ? nueva.CanvasHeight : nueva.Height);

                        // PRIORIDAD Loaded, no la normal.
                        //
                        // Cambiar de una pantalla al escritorio entero cambia la
                        // forma del video (16:9 -> 2.96:1). Con prioridad normal
                        // esto corria ANTES del pase de medida de WPF, asi que
                        // Ajustar leia el hueco de la forma anterior -- con la
                        // barra de desplazamiento todavia puesta -- y dejaba el
                        // video de un tamano que ya no correspondia. Se quedaba
                        // asi hasta el siguiente cambio de configuracion, que es
                        // justo lo que conseguia el rodeo de "abrir la segunda
                        // pantalla y luego todas a la vez".
                        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                        {
                            _videoAncho = ancho;
                            _videoAlto = alto;
                            MarcarCodec(nueva.Codec);
                            Ajustar();
                        });

                        // Una grabacion en curso no sobrevive a un cambio de
                        // flujo: el SPS que lleva dentro deja de valer y el
                        // archivo quedaria con dos resoluciones pegadas.
                        CerrarGrabacion();

                        // Version nueva SI es flujo nuevo: el decodificador viejo
                        // lleva el SPS anterior dentro. Solo el de ESA pantalla.
                        if (decodificadores.Remove(pantalla, out var viejo))
                            viejo.Dispose();

                        // EL PRESENTADOR NO SE TIRA NUNCA, ni aunque cambie la
                        // resolucion. Un swapchain NUEVO sobre el mismo HWND no
                        // lo compone el DWM hasta que llega un WM_SIZE, y al
                        // cambiar de monitor la ventana no cambia de tamano: el
                        // WM_SIZE no llega y la imagen se queda clavada.
                        if (presentador is null)
                        {
                            presentador = new VideoPresenter(device, hwnd, ancho, alto)
                            {
                                // Lo que el tecnico eligiera antes de que
                                // existiera la cadena.
                                Vsync = _vsync
                            };

                            _presentador = presentador;
                            Nota(presentador.Diagnostico);
                        }

                        presentador.Redimensionar(ancho, alto);

                        H264Decoder nuevoDecoder;

                        try
                        {
                            // EL CODEC LO DICE EL HOST. Alimentar HEVC a un
                            // descodificador H.264 no falla al crearlo: falla
                            // en el primer frame y lejos de aqui.
                            nuevoDecoder = new H264Decoder(
                                device, (int)nueva.Width, (int)nueva.Height, nueva.Codec);
                        }
                        catch (Exception ex)
                        {
                            // Y si esta PC no sabe ese codec, se dice. En negro y
                            // callado es como se pierde media hora buscando en el
                            // sitio equivocado.
                            Nota($"Esta PC no puede descodificar {nueva.Codec}: {ex.Message}");
                            break;
                        }

                        decodificadores[pantalla] = nuevoDecoder;

                        if (nueva.ParameterSets.Length > 0)
                        {
                            var parametros = nueva.ParameterSets.ToByteArray();

                            foreach (var frame in nuevoDecoder.Decode(parametros, 0, parametros.Length, 0))
                                frame.Dispose();
                        }

                        break;
                    }

                    case RemotePacket.PayloadOneofCase.VideoChunk:
                    {
                        chunks++;

                        var pantalla = paquete.VideoChunk.DisplayId;
                        porPantalla[pantalla] = porPantalla.GetValueOrDefault(pantalla) + 1;

                        if (!decodificadores.TryGetValue(pantalla, out var decoder)
                            || !configs.TryGetValue(pantalla, out var config))
                        {
                            break;   // llego video antes que su configuracion
                        }

                        if (paquete.VideoChunk.ConfigVersion != config.ConfigVersion)
                            break;

                        if (!montadores.TryGetValue(pantalla, out var montador))
                        {
                            montador = new VideoFrameAssembler();
                            montadores[pantalla] = montador;
                        }

                        var completado = montador.TryAdd(paquete.VideoChunk, out var completo);

                        // SE PIDE UN IDR AL PERDER. Esto faltaba ENTERO: el
                        // mensaje existe desde la Fase 4, el host lo atiende y
                        // el relay lo deja pasar, y no habia una sola linea que
                        // construyera uno. Un frame perdido dejaba la imagen
                        // rota hasta que el codificador emitiera un IDR por su
                        // cuenta -- y el GOP no se configura en ningun sitio.
                        var perdidos = montador.Dropped + montador.Rejected;

                        if (perdidos != perdidasVistas.GetValueOrDefault(pantalla))
                        {
                            perdidasVistas[pantalla] = perdidos;
                            PedirKeyframe(pantalla, montador.LastGoodFrameId, KeyframeReason.LostChunk);
                        }

                        if (!completado)
                            break;

                        // EL ACUSE, en cuanto el frame esta entero y ANTES de
                        // descodificarlo. Es lo que le permite al host capturar
                        // el siguiente mientras este se pinta: acusar despues
                        // sumaria el tiempo de descodificado a cada vuelta.
                        Acusar(pantalla, completo!.FrameId);

                        reconstruidos++;

                        if (completo!.KeyFrame)
                            idr++;

                        Grabar(pantalla, completo, config);

                        var antes = Stopwatch.GetTimestamp();
                        var salidas = decoder.Decode(completo.Payload, 0, completo.Payload.Length, completo.CaptureTimestampUs);
                        decodificaciones.Enqueue(Micros(Stopwatch.GetTimestamp() - antes));

                        while (decodificaciones.Count > MuestrasDeDecode)
                            decodificaciones.Dequeue();

                        foreach (var imagen in salidas)
                        {
                            using (imagen)
                            {
                                decodificados++;

                                // Donde va ESTA pantalla dentro del lienzo. Se
                                // declara aqui y no al llegar la config porque la
                                // apertura real -- el trozo visible, sin las filas
                                // de relleno de los macrobloques -- solo se conoce
                                // despues de descodificar el primer frame.
                                presentador?.Colocar(
                                    pantalla, decoder.Width, decoder.Height,
                                    decoder.Aperture.X, decoder.Aperture.Y,
                                    decoder.Aperture.Width, decoder.Aperture.Height,
                                    (int)config.LayoutX, (int)config.LayoutY);

                                // La captura la pide la interfaz y la atiende el
                                // presentador: el frame solo existe convertido a
                                // RGB dentro de Present.
                                var captura = Interlocked.Exchange(ref _captura, null);

                                presentador?.Present(pantalla, imagen.Texture, imagen.Subresource, captura);
                                pintados++;

                                // El segundo acuse, el que NO suelta nada. Con
                                // el, el host puede medir lo que tarda un frame
                                // en llegar a la pantalla del tecnico, que es lo
                                // unico que el usuario ve de verdad.
                                Acusar(pantalla, completo.FrameId, pintado: true);
                                ritmo.Marcar(reloj.Elapsed.TotalSeconds);

                                if (captura is not null)
                                    Nota($"Captura guardada en {captura}");
                            }
                        }

                        break;
                    }

                    case RemotePacket.PayloadOneofCase.HelloAccepted:
                        // Token NUEVO: el anterior acaba de dejar de valer en el
                        // servidor, asi que la referencia local vieja se pisa.
                        _token = paquete.HelloAccepted.ReconnectToken;

                        // Y el ticket de arranque se suelta. Ya esta consumido y
                        // no vuelve; conservarlo solo alarga la vida de una
                        // credencial que ya no sirve.
                        _ticket = null;

                        _reconectarHasta = DateTimeOffset.FromUnixTimeMilliseconds(
                            paquete.HelloAccepted.ReconnectUntilUs / 1000);

                        // AL ENTRAR SE SUELTA LO PEGADO. Si la sesion anterior se
                        // corto entre un KeyDown y su KeyUp, esa tecla sigue
                        // hundida al otro lado y desde la PC de planta no hay
                        // forma de despegarla. Cuesta un mensaje y solo hace algo
                        // cuando de verdad quedo algo.
                        PedirSoltarEntrada();

                        // La reconexion funciono: la espera vuelve al principio.
                        // Sin esto, un microcorte a los diez minutos empezaria
                        // esperando los 5 s a los que llego el corte anterior.
                        _espera = TimeSpan.FromMilliseconds(250);

                        break;

                    case RemotePacket.PayloadOneofCase.Ping:
                        // Se devuelve la marca TAL CUAL. El RTT lo calcula quien
                        // pregunto, con su reloj. Hasta la Fase 13 el visor no
                        // contestaba, asi que el host no podia medir la red.
                        Encolar(new RemotePacket
                        {
                            ProtocolVersion = RemoteSessionProtocol.Version,
                            SessionId = _sesion,
                            Pong = new Pong { SentAtUs = paquete.Ping.SentAtUs }
                        });

                        break;

                    case RemotePacket.PayloadOneofCase.Pong:
                        _rttUs = NowUs() - paquete.Pong.SentAtUs;
                        break;

                    case RemotePacket.PayloadOneofCase.Clipboard:
                        RecibirPortapapeles(paquete.Clipboard.Text);
                        break;

                    case RemotePacket.PayloadOneofCase.HostStatus:
                        // Lo que pasa en la PC controlada, en la barra del
                        // tecnico. Antes solo acababa en el visor de eventos de
                        // esa maquina, o sea en ningun sitio util.
                        //
                        // Las medidas periodicas van a su propia linea: por el
                        // hueco de los avisos borrarian cada 2 s cualquier cosa
                        // que hubiera que leer.
                        if (paquete.HostStatus.Measurements)
                        {
                            medidasDelHost = paquete.HostStatus.Text;
                        }
                        else
                        {
                            // EN SU PROPIA LINEA, no en la de los avisos de aqui.
                            //
                            // Compartian hueco, y el ultimo en hablar borraba al
                            // otro: encender el vsync desde el menu tapaba el
                            // "DXGI no puede capturar aqui; se pasa al respaldo
                            // GDI" que el host acababa de mandar. Justo la linea
                            // que explica por que no se ve nada.
                            avisoDelHost = paquete.HostStatus.Text;
                            Nota(paquete.HostStatus.Text);
                        }

                        break;

                    case RemotePacket.PayloadOneofCase.Displays:
                        RecibirPantallas(paquete.Displays);
                        break;

                    case RemotePacket.PayloadOneofCase.Cursor:
                        RecibirCursor(paquete.Cursor);
                        break;

                    case RemotePacket.PayloadOneofCase.ClipboardFiles:
                        RecibirArchivosCopiados(paquete.ClipboardFiles);
                        break;

                    case RemotePacket.PayloadOneofCase.FileList:
                        RecibirLista(paquete.FileList);
                        break;

                    case RemotePacket.PayloadOneofCase.FileChunk:
                        RecibirTrozo(paquete.FileChunk);
                        break;

                    case RemotePacket.PayloadOneofCase.FileAck:
                        RecibirAcuse(paquete.FileAck);
                        break;

                    // El motivo se AÑADE al informe, no lo sustituye. Reemplazarlo
                    // borraba las cifras justo cuando terminaba la sesion, que es
                    // cuando hacen falta: en el checkpoint de la Fase 5 el host
                    // cerro limpiamente y con el se llevo por delante los unicos
                    // contadores del viewer que habia.
                    case RemotePacket.PayloadOneofCase.Close:
                        _cierre = $"El relay cerro la sesion: {paquete.Close.Reason} {paquete.Close.Detail}";
                        _token = null;   // cierre ordenado: no hay a donde volver
                        break;

                    case RemotePacket.PayloadOneofCase.Error:
                        _cierre = $"Relay: {paquete.Error.Code} {paquete.Error.Detail}";
                        _token = null;
                        break;
                }

                void Informar()
                {
                    proceso.Refresh();

                    var ordenadas = decodificaciones.Order().ToList();
                    var segundos = Math.Max(reloj.Elapsed.TotalSeconds, 0.001);

                    // SI NO HAY IMAGEN, LAS MEDIDAS SE ENCIENDEN SOLAS.
                    //
                    // Estan apagadas de fabrica porque quien entra a arreglar
                    // algo quiere ver el escritorio, no percentiles. Pero cuando
                    // no hay escritorio que ver, esa linea es LO UNICO que
                    // distingue tres fallos que en pantalla son identicos --
                    // negro: no llega video, llega y no se descodifica, o ni
                    // siquiera hay sesion.
                    //
                    // Pasaron diez minutos preguntando por una captura de esa
                    // linea que estaba a un clic de distancia y apagada por mi.
                    if (pintados == 0 && segundos > 5 && !_medidasForzadas)
                    {
                        _medidasForzadas = true;

                        Dispatcher.BeginInvoke(() =>
                        {
                            BarraEstado.Visibility = Visibility.Visible;
                            Nota("Sin imagen: se encienden las medidas para ver por que.");
                        });
                    }

                    Mostrar(
                        $"sesion {_sesion}   {Resumen(configs)}   " +
                        $"RTT {(_rttUs < 0 ? "-" : $"{_rttUs / 1000.0:0.0} ms")}\n" +
                        $"chunks {chunks}{PorPantalla(porPantalla)}   frames {reconstruidos}   decodificados {decodificados}   pintados {pintados}   " +
                        $"render {ritmo.Fps(reloj.Elapsed.TotalSeconds):0.0} FPS   " +
                        $"decode p50 {Percentil(ordenadas, 0.50):0.00} ms   p95 {Percentil(ordenadas, 0.95):0.00} ms\n" +
                        // La entrada enviada va en la barra a proposito: cuando
                        // el video se ve pero no se puede controlar, esta cifra
                        // dice de un vistazo cual de las dos mitades falla.
                        $"entrada {_salida.Entrada}" +
                        (_salida.Perdidos == 0 ? string.Empty : $" ({_salida.Perdidos} PERDIDOS)") +
                        (_salida.Caducados == 0 ? string.Empty : $" ({_salida.Caducados} caducados)") +
                        $"   paquetes {_salida.Enviados}   movimientos fundidos {_salida.Fundidos}   " +
                        $"acuses {_acuses}/{_acusesPintados}   IDR pedidos {_idrPedidos}   " +
                        (_grabacion is null ? string.Empty : $"grabando {_grabados} frames   ") +
                        $"incompletos {montadores.Values.Sum(m => m.Dropped)}   " +
                        $"invalidos {montadores.Values.Sum(m => m.Rejected)}   " +
                        $"tardios {montadores.Values.Sum(m => m.Stale)}   " +
                        $"IDR {idr}   cursor {_cursoresRecibidos}   cambios de config {cambiosConfig}   " +
                        $"RAM {proceso.PrivateMemorySize64 / 1024 / 1024} MB (inicio {ramInicio / 1024 / 1024})   " +
                        $"{reloj.Elapsed:hh\\:mm\\:ss}" +
                        (medidasDelHost.Length == 0 ? string.Empty : $"\nhost  {medidasDelHost}") +
                        (_cierre is null ? string.Empty : $"\n{_cierre}"));
                }

                if (reloj.Elapsed >= siguienteAviso)
                {
                    Informar();
                    siguienteAviso += TimeSpan.FromMilliseconds(500);
                }

                // La sesion termino. Se informa ANTES de salir, con las cifras y
                // el motivo juntos: en el checkpoint de la Fase 5 el cierre llego
                // limpio y se llevo por delante los unicos contadores que habia
                // del viewer.
                if (_cierre is not null)
                {
                    Informar();
                    break;
                }
            }
        }
        finally
        {
            CerrarGrabacion();
            presentador?.Dispose();

            foreach (var abierto in decodificadores.Values)
                abierto.Dispose();

            await intento.CancelAsync();
            try { await latidos; } catch (Exception) { /* cerrando */ }
        }
    }

    // ------------------------------------------------------- barra del visor

    /// <summary>
    /// Ruta pedida por la interfaz para la proxima captura. Se lee con
    /// Interlocked desde el hilo de reproduccion: son dos hilos y una referencia.
    /// </summary>
    private string? _captura;

    /// <summary>Lo que quiere la interfaz. Abrir y cerrar el archivo lo hace SIEMPRE
    /// el hilo de reproduccion, que es el unico que escribe en el.</summary>
    private volatile bool _quiereGrabar;

    private FileStream? _grabacion;
    private bool _esperandoIdr;
    private uint _pantallaGrabada;
    private long _grabados;

    private static string Carpeta(Environment.SpecialFolder donde)
        => Path.Combine(Environment.GetFolderPath(donde), "DeviceHub");

    private string NombreArchivo(string extension)
        => Path.Combine(
            Carpeta(extension == ".png" ? Environment.SpecialFolder.MyPictures : Environment.SpecialFolder.MyVideos),
            // UtcNow.ToLocalTime() y no la hora local a secas: la regla del
            // proyecto prohibe esa llamada en src entero, y con razon -- pero un
            // archivo que va a la carpeta Videos DEL TECNICO se nombra con SU
            // hora, no con UTC, o buscar "el de las tres" no encuentra nada.
            $"{_machineId}-{DateTime.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}{extension}");

    private void Capturar(object sender, RoutedEventArgs e)
    {
        Interlocked.Exchange(ref _captura, NombreArchivo(".png"));

        // La captura sale del proximo frame PINTADO. Con el escritorio remoto
        // quieto no hay frames nuevos, asi que puede tardar: se avisa en vez de
        // dejar el boton sin respuesta aparente.
        Nota("Captura pedida: se guarda en el proximo frame.");
    }

    private void AlternarGrabacion(object sender, RoutedEventArgs e)
    {
        _quiereGrabar = !_quiereGrabar;

        BotonGrabar.Content = _quiereGrabar ? "● Detener" : "Grabar";

        // Rojo mientras graba, como el punto de AnyDesk. Es el unico estado del
        // visor que sigue corriendo cuando nadie lo mira, y el unico que escribe
        // en el disco del tecnico sin volver a preguntar.
        BotonGrabar.Foreground = _quiereGrabar
            ? (System.Windows.Media.Brush)FindResource("Acento")
            : (System.Windows.Media.Brush)FindResource("Letra");
        Nota(_quiereGrabar ? "Grabando desde el proximo fotograma clave..." : "Grabacion detenida.");
    }

    /// <summary>
    /// Escribe el H.264 tal y como llega, SIN recodificar. Es lo que hace que
    /// grabar sea casi gratis: los bytes ya vienen comprimidos y en Annex-B, que
    /// es justo el formato que un reproductor externo espera en un .h264.
    ///
    /// Empieza siempre en un IDR. Arrancar en mitad de un GOP produce un archivo
    /// que abre en verde y se va corrigiendo, y no hay forma de arreglarlo luego.
    /// </summary>
    private void Grabar(uint pantalla, AssembledFrame frame, VideoConfig? config)
    {
        if (!_quiereGrabar)
        {
            CerrarGrabacion();
            return;
        }

        // UNA SOLA PANTALLA POR ARCHIVO. Un .h264 es un flujo, no un contenedor:
        // dos pantallas intercaladas en el mismo archivo no dan dos videos, dan
        // uno roto. Se graba la primera que entregue un frame.
        if (_grabacion is not null && pantalla != _pantallaGrabada)
            return;

        if (_grabacion is null)
        {
            if (config is null)
                return;

            _pantallaGrabada = pantalla;

            // LA EXTENSION LA DICE EL CODEC, no el nombre de la variable. Con
            // H.265 por defecto, un .h264 con HEVC dentro es un archivo que la
            // mitad de los reproductores abre en negro sin decir por que.
            var ruta = NombreArchivo(config.Codec == VideoCodec.H265 ? ".h265" : ".h264");
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);

            _grabacion = File.Create(ruta);
            _grabados = 0;
            _esperandoIdr = true;

            // SPS y PPS delante del primer IDR: en transporte viajan aparte, pero
            // un archivo tiene que abrir sin ningun contexto previo.
            var parametros = config.ParameterSets.ToByteArray();
            _grabacion.Write(parametros, 0, parametros.Length);

            Nota($"Grabando en {ruta}");
        }

        if (_esperandoIdr)
        {
            if (!frame.KeyFrame)
                return;

            _esperandoIdr = false;
        }

        _grabacion.Write(frame.Payload, 0, frame.Payload.Length);
        _grabados++;
    }

    private void CerrarGrabacion()
    {
        if (_grabacion is null)
            return;

        _grabacion.Dispose();
        _grabacion = null;
    }

    /// <summary>0 = adaptar al tamano de la ventana. Cualquier otro valor es el
    /// factor sobre los pixeles reales de la pantalla remota.</summary>
    private double _escala;

    private int _videoAncho, _videoAlto;

    private void ElegirEscala(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem elegido || elegido.Tag is not string etiqueta)
            return;

        _escala = double.Parse(etiqueta, CultureInfo.InvariantCulture);

        // IsCheckable pone y quita la marca sola, pero no sabe que estas cinco
        // son la MISMA decision: sin esto se quedarian varias marcadas a la vez.
        foreach (var otro in MenuEscala.Items.OfType<MenuItem>())
            otro.IsChecked = ReferenceEquals(otro, elegido);

        // La cabecera es el icono, asi que lo elegido se dice en el ToolTip. Es
        // lo unico que se echaba de menos al quitar el desplegable, y AnyDesk lo
        // resuelve igual: el icono no cambia, la marca esta dentro.
        MenuEscala.ToolTip = $"Vista: {elegido.Header}";

        Ajustar();
    }

    /// <summary>
    /// Pide otro codec y rehace la cadena, sin tocar ningun json.
    ///
    /// Es la unica forma honesta de comparar los dos: la misma PC, la misma
    /// pantalla y el mismo contenido con segundos de diferencia. Editar el
    /// appsettings del agente y reiniciar el servicio mete minutos y un
    /// reinicio en medio, que es justo lo que docs/benchmark.md prohibe.
    /// </summary>
    private void ElegirCodec(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem elegido || elegido.Tag is not string etiqueta)
            return;

        var codec = etiqueta == "h265" ? VideoCodec.H265 : VideoCodec.H264;

        // Las marcas NO se tocan aqui. Las pone MarcarCodec cuando llegue la
        // VideoConfig con el codec que de verdad salio: si esa GPU no puede con
        // H.265, marcarlo ahora seria enseñar un estado que no existe.
        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            SelectCodec = new SelectCodec { Codec = codec }
        });

        Nota($"Cambiando a {(codec == VideoCodec.H265 ? "H.265" : "H.264")}...");
    }

    /// <summary>
    /// Cuantos bits se le dan a la imagen.
    ///
    /// A diferencia del codec, esto NO rehace nada: el bitrate se cambia sobre
    /// el codificador en marcha, asi que no hay config_version nueva ni
    /// parpadeo. Por eso la marca si se pone aqui -- no hay nada que esperar
    /// de vuelta que pueda contradecirla.
    /// </summary>
    private void ElegirCalidad(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem elegido || elegido.Tag is not string etiqueta)
            return;

        var ratio = double.Parse(etiqueta, CultureInfo.InvariantCulture);

        foreach (var otro in new[] { MenuCalidadFiel, MenuCalidadMedia, MenuCalidadRapida })
            otro.IsChecked = ReferenceEquals(otro, elegido);

        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            SelectQuality = new SelectQuality { Ratio = ratio }
        });

        Nota($"Calidad: {elegido.Header}");
    }

    /// <summary>
    /// Presentar en cuanto llega el frame, o esperar al monitor.
    ///
    /// No pasa por el host ni por el protocolo: es del lado del tecnico entero.
    /// </summary>
    private void AlternarVsync(object sender, RoutedEventArgs e)
    {
        _vsync = MenuVsync.IsChecked;

        if (_presentador is not null)
            _presentador.Vsync = _vsync;

        Nota(_vsync
            ? "Esperando al monitor: sin desgarro, hasta un refresco mas de retraso."
            : "Presentando en cuanto llega: minimo retraso, puede haber desgarro.");
    }

    /// <summary>Sobrevive al presentador: la cadena se rehace en cada cambio de
    /// configuracion, y la eleccion del tecnico no puede irse con ella.</summary>
    private bool _vsync;

    /// <summary>El presentador vivo, solo para que el menu pueda alcanzarlo. Su
    /// dueño sigue siendo el bucle de reproduccion.</summary>
    private VideoPresenter? _presentador;

    /// <summary>La marca sigue al codec REAL, el que vuelve en VideoConfig.</summary>
    private void MarcarCodec(VideoCodec codec)
    {
        MenuH264.IsChecked = codec != VideoCodec.H265;
        MenuH265.IsChecked = codec == VideoCodec.H265;
    }

    private void Ajustar(object sender, SizeChangedEventArgs e) => Ajustar();

    private void Ajustar(object sender, ScrollChangedEventArgs e) => Ajustar();

    /// <summary>
    /// El tamano del video lo decide el LIENZO, no el swapchain: DXGI se creo con
    /// Scaling.Stretch a la resolucion remota, asi que escalar es solo cambiar el
    /// tamano de la ventana hija. Redimensionar el swapchain seria rehacer el
    /// presentador, que es como se deja el visor en negro.
    /// </summary>
    private void Ajustar()
    {
        if (_videoAncho <= 0 || _videoAlto <= 0)
            return;

        var ancho = Lienzo.ViewportWidth > 0 ? Lienzo.ViewportWidth : Lienzo.ActualWidth;
        var alto = Lienzo.ViewportHeight > 0 ? Lienzo.ViewportHeight : Lienzo.ActualHeight;

        if (ancho <= 0 || alto <= 0)
            return;

        var (nuevoAncho, nuevoAlto) = Escalado.Encajar(_videoAncho, _videoAlto, ancho, alto, _escala);

        // Solo si CAMBIA. Reasignar el mismo valor no dispara otra medida de WPF,
        // pero esto es lo que garantiza que la pareja Ajustar/ScrollChanged pare:
        // se recalcula hasta que el hueco y el video se ponen de acuerdo, y en la
        // vuelta siguiente ya no hay nada que tocar.
        if (Math.Abs(Video.Width - nuevoAncho) < 0.5 && Math.Abs(Video.Height - nuevoAlto) < 0.5)
            return;

        (Video.Width, Video.Height) = (nuevoAncho, nuevoAlto);
    }

    private void AlternarDatos(object sender, RoutedEventArgs e)
        => BarraEstado.Visibility = BarraEstado.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>
    /// Pide al host que suelte todo lo que tenga hundido.
    ///
    /// Se manda al CONECTAR y al RECONECTAR, no solo cuando alguien lo pulsa: un
    /// corte de red entre un KeyDown y su KeyUp deja esa tecla pegada al otro
    /// lado, y desde la PC de planta no hay forma de despegarla.
    /// </summary>
    /// <summary>1 = hay que pedir al host que suelte lo hundido. Lo levanta
    /// quien lo necesite y lo baja el hilo de envio.</summary>
    private void PedirSoltarEntrada() => _salida.PedirSoltar();

    private void SoltarEntradaRemota(object sender, RoutedEventArgs e)
    {
        PedirSoltarEntrada();
        Nota("Pedido soltar teclas y botones pegados.");
    }

    private void EnviarCtrlAltSupr(object sender, RoutedEventArgs e)
        => Enviar(new HostAction { Kind = HostAction.Types.Kind.HostActionCtrlAltDel });

    private void BloquearRemota(object sender, RoutedEventArgs e)
        => Enviar(new HostAction { Kind = HostAction.Types.Kind.HostActionLock });

    private void CongelarEntrada(object sender, RoutedEventArgs e)
        => Enviar(new HostAction
        {
            Kind = MenuCongelar.IsChecked
                ? HostAction.Types.Kind.HostActionBlockInput
                : HostAction.Types.Kind.HostActionUnblockInput
        });

    /// <summary>
    /// Con confirmacion, y es la unica de la barra que la lleva. Las demas se
    /// deshacen solas; esta se lleva por delante lo que alguien tuviera abierto
    /// en una PC de planta, y el menu esta a dos milimetros de "Bloquear".
    /// </summary>
    private void ReiniciarRemota(object sender, RoutedEventArgs e)
    {
        var respuesta = MessageBox.Show(
            $"Se va a reiniciar {_machineId}.\n\n" +
            "Si hay alguien trabajando en esa PC, perdera lo que no haya guardado.",
            "Reiniciar la PC remota", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (respuesta == MessageBoxResult.OK)
            Enviar(new HostAction { Kind = HostAction.Types.Kind.HostActionReboot });
    }

    // ------------------------------------------------------------ cursor (11)

    /// <summary>Ultima forma pintada. El host manda un id creciente para no tener
    /// que reconstruir el cursor de Windows cuando repite la misma.</summary>
    private ulong _formaCursor;

    private long _cursoresRecibidos;

    /// <summary>
    /// El cursor de la PC REMOTA sobre el video.
    ///
    /// Hasta esta fase el puntero que se veia era el LOCAL: Desktop Duplication
    /// no compone el cursor en la imagen, asi que el escritorio remoto llegaba
    /// sin raton y coincidian porque el tecnico movia el suyo. Se notaba en que
    /// la forma no cambiaba nunca -- ni barra de texto, ni reloj de ocupado.
    ///
    /// ponytail: se usa la FORMA y la visibilidad, no la posicion. Dibujar un
    /// segundo puntero donde diga el host exigiria componerlo dentro del
    /// swapchain -- por el airspace del HwndHost, ningun XAML puede ir encima --
    /// y eso es un quad texturizado en D3D. Mientras el tecnico conduce, su
    /// propio cursor ya esta en el sitio correcto; lo que hoy NO se ve es a
    /// alguien moviendo el raton fisico al otro lado.
    /// </summary>
    private void RecibirCursor(CursorUpdate aviso)
    {
        _cursoresRecibidos++;

        var forma = aviso.Shape;

        Dispatcher.BeginInvoke(() =>
        {
            if (forma is null || forma.ShapeId == _formaCursor)
            {
                Video.UsarCursor(IntPtr.Zero, aviso.Visible);
                return;
            }

            _formaCursor = forma.ShapeId;

            var cursor = CursorRemoto.Crear(
                forma.Bgra.ToByteArray(), (int)forma.Width, (int)forma.Height,
                (int)forma.HotspotX, (int)forma.HotspotY);

            Video.UsarCursor(cursor, aviso.Visible);
        });
    }

    // ---------------------------------------------------------------- archivos

    /// <summary>
    /// Una fila del panel de archivos. `Etiqueta` es lo que se ve.
    ///
    /// PUBLICO, y no por gusto: el motor de binding de WPF llega a las
    /// propiedades por reflexion y no puede leer las de un tipo no publico.
    /// Siendo private no lanza nada -- pinta las filas VACIAS, que es como se
    /// descubrio.
    /// </summary>
    public sealed record Entrada(string Nombre, bool Carpeta, ulong Tamano)
    {
        public string Etiqueta => Carpeta ? $"[{Nombre}]" : $"{Nombre}   {Legible(Tamano)}";

        private static string Legible(ulong bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
            < 1024UL * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
        };
    }

    private string _rutaRemota = string.Empty;

    /// <summary>
    /// Descarga en curso. El archivo se escribe con extension .parcial y solo se
    /// renombra al recibir el ultimo trozo: un archivo a medias con su nombre
    /// definitivo es peor que no tenerlo, porque parece completo.
    /// </summary>
    private FileStream? _bajando;
    private string _destinoFinal = string.Empty;

    /// <summary>Subida en curso. La gobiernan los acuses del host: cada FileAck
    /// dice por que byte va y el siguiente trozo sale de ahi.</summary>
    private FileStream? _subiendo;
    private string _destinoRemoto = string.Empty;
    private ulong _tamanoSubida;

    private void AlternarArchivos(object sender, RoutedEventArgs e)
    {
        var visible = PanelArchivos.Visibility != Visibility.Visible;
        PanelArchivos.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible && Archivos.Items.Count == 0)
            PedirLista(string.Empty);
    }

    private void PedirLista(string ruta)
        => Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            FileListRequest = new FileListRequest { Path = ruta }
        });

    private void RutaEscrita(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        PedirLista(RutaRemota.Text.Trim());
    }

    private void SubirCarpeta(object sender, RoutedEventArgs e)
    {
        // Sin carpeta superior se vuelve a las unidades, no a un error.
        var padre = string.IsNullOrEmpty(_rutaRemota)
            ? string.Empty
            : Path.GetDirectoryName(_rutaRemota.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;

        PedirLista(padre);
    }

    private void AbrirEntrada(object sender, MouseButtonEventArgs e)
    {
        if (Archivos.SelectedItem is Entrada { Carpeta: true } carpeta)
            PedirLista(Combinar(carpeta.Nombre));
    }

    /// <summary>Las unidades llegan como "C:\", que ya es una ruta completa.</summary>
    private string Combinar(string nombre)
        => string.IsNullOrEmpty(_rutaRemota) ? nombre : Path.Combine(_rutaRemota, nombre);

    private void RecibirLista(FileList lista)
        => Dispatcher.BeginInvoke(() =>
        {
            if (lista.Error.Length > 0)
            {
                EstadoArchivos.Text = lista.Error;
                return;
            }

            _rutaRemota = lista.Path;
            RutaRemota.Text = lista.Path;

            Archivos.ItemsSource = lista.Entries
                .Select(x => new Entrada(x.Name, x.Directory, x.Size))
                .ToList();

            EstadoArchivos.Text = $"{lista.Entries.Count} elementos";
        });

    // ------------------------------------------------------------- descarga

    private void Descargar(object sender, RoutedEventArgs e)
    {
        if (Archivos.SelectedItem is not Entrada { Carpeta: false } archivo)
        {
            EstadoArchivos.Text = "Elige un archivo.";
            return;
        }

        var carpeta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DeviceHub");

        IniciarBajada(Combinar(archivo.Nombre), Path.Combine(carpeta, archivo.Nombre));
    }

    private void IniciarBajada(string remoto, string local)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);

        _destinoFinal = local;
        var parcial = _destinoFinal + ".parcial";

        // REANUDAR: lo que ya haya en el .parcial es el punto de partida. Nadie
        // lleva un registro aparte -- el propio archivo es el estado.
        var desde = File.Exists(parcial) ? (ulong)new FileInfo(parcial).Length : 0;

        _bajando?.Dispose();
        _bajando = new FileStream(parcial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        _bajando.Seek((long)desde, SeekOrigin.Begin);

        var nombre = Path.GetFileName(local);

        Progreso.Value = 0;
        EstadoArchivos.Text = desde > 0
            ? $"Reanudando {nombre} desde {desde / 1024} KB..."
            : $"Descargando {nombre}...";

        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            FileDownload = new FileDownloadRequest { Path = remoto, Offset = desde }
        });
    }

    private void RecibirTrozo(FileChunk trozo)
        => Dispatcher.BeginInvoke(() =>
        {
            if (_bajando is null)
                return;

            if (trozo.Error.Length > 0)
            {
                // El .parcial NO se borra: es lo que permite reintentar sin
                // volver a bajar lo que ya llego.
                _bajando.Dispose();
                _bajando = null;
                EstadoArchivos.Text = $"Fallo: {trozo.Error}";
                return;
            }

            if (trozo.Data.Length > 0)
            {
                _bajando.Seek((long)trozo.Offset, SeekOrigin.Begin);
                trozo.Data.WriteTo(_bajando);
            }

            if (trozo.Total > 0)
                Progreso.Value = (trozo.Offset + (ulong)trozo.Data.Length) * 100.0 / trozo.Total;

            if (!trozo.Last)
                return;

            _bajando.Dispose();
            _bajando = null;

            var parcial = _destinoFinal + ".parcial";

            File.Move(parcial, _destinoFinal, overwrite: true);

            Progreso.Value = 100;
            EstadoArchivos.Text = $"Guardado en {_destinoFinal}";

            SiguienteDeLaCola();
        });

    // ---------------------------------------------------------------- subida

    private void Subir(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rutaRemota))
        {
            EstadoArchivos.Text = "Entra primero en una carpeta de la PC remota.";
            return;
        }

        var dialogo = new Microsoft.Win32.OpenFileDialog { Title = "Subir a la PC remota" };

        if (dialogo.ShowDialog(Window.GetWindow(this)) != true)
            return;

        IniciarSubida(dialogo.FileName, Path.Combine(_rutaRemota, Path.GetFileName(dialogo.FileName)));
    }

    private void IniciarSubida(string local, string remoto)
    {
        _subiendo?.Dispose();
        _subiendo = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _tamanoSubida = (ulong)_subiendo.Length;
        _destinoRemoto = remoto;

        Progreso.Value = 0;
        EstadoArchivos.Text = $"Subiendo {Path.GetFileName(local)}...";

        // EL SONDEO. Un trozo vacio no escribe nada: solo pregunta cuanto hay ya
        // en el destino. El host contesta con un FileAck y de ahi sale el offset
        // por el que seguir, que es como se reanuda sin adivinar.
        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            FileChunk = new FileChunk { Path = _destinoRemoto, Total = _tamanoSubida }
        });
    }

    private void RecibirAcuse(FileAck acuse)
        => Dispatcher.BeginInvoke(() =>
        {
            if (_subiendo is null)
                return;

            if (acuse.Error.Length > 0)
            {
                _subiendo.Dispose();
                _subiendo = null;
                EstadoArchivos.Text = $"Fallo: {acuse.Error}";
                return;
            }

            if (acuse.Received >= _tamanoSubida)
            {
                _subiendo.Dispose();
                _subiendo = null;
                Progreso.Value = 100;
                EstadoArchivos.Text = $"Subido a {_destinoRemoto}";

                SiguienteDeLaCola();
                return;
            }

            // El host manda un acuse por trozo, asi que esto es la vuelta del
            // bucle: se envia el siguiente y se espera al siguiente acuse. El
            // ritmo lo marca el receptor, no el emisor -- por eso no hace falta
            // control de flujo propio.
            var bufer = new byte[60 * 1024];

            _subiendo.Seek((long)acuse.Received, SeekOrigin.Begin);
            var leidos = _subiendo.Read(bufer, 0, bufer.Length);

            Progreso.Value = acuse.Received * 100.0 / Math.Max(_tamanoSubida, 1);

            Encolar(new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = _sesion,
                FileChunk = new FileChunk
                {
                    Path = _destinoRemoto,
                    Offset = acuse.Received,
                    Total = _tamanoSubida,
                    Data = Google.Protobuf.ByteString.CopyFrom(bufer, 0, leidos),
                    Last = acuse.Received + (ulong)leidos >= _tamanoSubida
                }
            });
        });

    // -------------------------------------------- portapapeles de archivos (25)

    /// <summary>
    /// Lo que queda por transferir. Una a la vez, en orden.
    ///
    /// En serie y no en paralelo a proposito: el stream es el mismo que lleva el
    /// video, y tres transferencias compitiendo por el solo consiguen que las
    /// tres vayan lentas y que la pantalla se congele mientras tanto.
    /// </summary>
    private readonly Queue<(string Remoto, string Local, bool Bajando)> _cola = new();

    /// <summary>Rutas ya transferidas de la tanda en curso. Al vaciarse la cola,
    /// son las que van al portapapeles del destino.</summary>
    private readonly List<string> _transferidos = [];

    /// <summary>Lo que la PC remota anuncio tener copiado. Rutas, no bytes.</summary>
    private IReadOnlyList<string> _copiadoAlla = [];

    private static string Deposito(string donde)
        => Path.Combine(Path.GetTempPath(), "DeviceHub", "portapapeles", donde);

    private void RecibirArchivosCopiados(ClipboardFiles aviso)
        => Dispatcher.BeginInvoke(() =>
        {
            _copiadoAlla = [.. aviso.Paths];

            PanelArchivos.Visibility = Visibility.Visible;
            BotonTraer.IsEnabled = _copiadoAlla.Count > 0;
            EstadoArchivos.Text = $"{_copiadoAlla.Count} archivos copiados en la PC remota. Pulsa Traer.";
        });

    /// <summary>
    /// Baja lo que copiaron alla y lo deja en el portapapeles de aqui.
    ///
    /// Un boton y no automatico: copiar 4 GB con Ctrl+C es un gesto de un
    /// segundo, y mandarlos por la red de planta sin que nadie lo pida seria una
    /// sorpresa cara. RustDesk tampoco lo esconde -- ensena su ventana de
    /// progreso.
    /// </summary>
    private void Traer(object sender, RoutedEventArgs e)
    {
        if (_copiadoAlla.Count == 0)
            return;

        var deposito = Deposito("desde-remoto");

        // Se vacia entre tandas: si no, pegar dos veces seguidas arrastraria los
        // archivos de la copia anterior junto con los nuevos.
        try { if (Directory.Exists(deposito)) Directory.Delete(deposito, recursive: true); }
        catch (IOException) { }

        _cola.Clear();
        _transferidos.Clear();

        foreach (var remoto in _copiadoAlla)
            _cola.Enqueue((remoto, Path.Combine(deposito, Path.GetFileName(remoto)), true));

        SiguienteDeLaCola();
    }

    /// <summary>Sube lo que hay copiado aqui y lo deja en el portapapeles de
    /// alla.</summary>
    private void Llevar(object sender, RoutedEventArgs e)
    {
        List<string> locales;

        try
        {
            locales = [.. Clipboard.GetFileDropList().Cast<string>().Where(File.Exists)];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            EstadoArchivos.Text = "El portapapeles esta ocupado. Reintenta.";
            return;
        }

        if (locales.Count == 0)
        {
            EstadoArchivos.Text = "No hay archivos copiados en esta PC.";
            return;
        }

        _cola.Clear();
        _transferidos.Clear();

        // El deposito es del temporal de ALLA, no de la carpeta que este abierta:
        // pegar no deberia ensuciar el sitio donde el tecnico estuviera mirando.
        foreach (var local in locales)
        {
            var remoto = $@"%TEMP%\DeviceHub\portapapeles\hacia-remoto\{Path.GetFileName(local)}";
            _cola.Enqueue((remoto, local, false));
        }

        SiguienteDeLaCola();
    }

    /// <summary>
    /// Arranca lo siguiente, o cierra la tanda poniendo el portapapeles.
    ///
    /// El portapapeles se toca SOLO al final. Ponerlo archivo a archivo dejaria
    /// al tecnico pegando una copia incompleta si se adelanta.
    /// </summary>
    private void SiguienteDeLaCola()
    {
        if (_cola.Count > 0)
        {
            var (remoto, local, bajando) = _cola.Dequeue();

            _transferidos.Add(bajando ? local : remoto);

            if (bajando)
                IniciarBajada(remoto, local);
            else
                IniciarSubida(local, remoto);

            EstadoArchivos.Text += $"   ({_cola.Count} en cola)";
            return;
        }

        if (_transferidos.Count == 0)
            return;

        var rutas = _transferidos.ToList();
        _transferidos.Clear();

        // Bajando: las rutas son de AQUI y el portapapeles es el de aqui.
        // Subiendo: son de ALLA y hay que pedirle al host que lo ponga el.
        if (rutas[0].StartsWith(Deposito("desde-remoto"), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var lista = new System.Collections.Specialized.StringCollection();
                lista.AddRange([.. rutas]);

                Clipboard.SetFileDropList(lista);
                EstadoArchivos.Text = $"{rutas.Count} archivos listos para pegar aqui.";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                EstadoArchivos.Text = "Los archivos llegaron, pero el portapapeles estaba ocupado.";
            }

            return;
        }

        var orden = new ClipboardFiles { Apply = true };
        orden.Paths.AddRange(rutas);

        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            ClipboardFiles = orden
        });

        EstadoArchivos.Text = $"{rutas.Count} archivos listos para pegar en la PC remota.";
    }

    // --------------------------------------------------------------- pantallas

    /// <summary>
    /// Una entrada del selector de pantalla. `Etiqueta` es lo que se ve y `Id` lo
    /// que viaja; -1 es el escritorio virtual entero.
    ///
    /// Publico por lo mismo que <see cref="Entrada"/>: con un tipo private, WPF
    /// deja el desplegable en blanco sin decir por que.
    /// </summary>
    /// <summary>Lo que se guarda en el Tag de cada entrada del menu: el id que
    /// viaja al host y el nombre corto que se queda en el titulo.</summary>
    public sealed record Monitor(int Id, string Etiqueta);

    private void RecibirPantallas(DisplayList lista)
    {
        var opciones = new List<(Monitor Corto, string Largo)>();

        foreach (var d in lista.Displays)
        {
            var nombre = d.Name.Replace(@"\.\", string.Empty);

            opciones.Add((
                new Monitor(d.Id, nombre),
                $"{nombre}  {d.Width}x{d.Height} @{d.X},{d.Y}" +
                (d.Primary ? "  (principal)" : string.Empty)));
        }

        // Solo tiene sentido ofrecer el escritorio completo si hay mas de una.
        if (opciones.Count > 1)
        {
            var ancho = lista.Displays.Max(d => d.X + d.Width) - lista.Displays.Min(d => d.X);
            var alto = lista.Displays.Max(d => d.Y + d.Height) - lista.Displays.Min(d => d.Y);

            opciones.Insert(0, (new Monitor(-1, "Todas"), $"Todas a la vez  {ancho}x{alto}"));
        }

        var actual = lista.Current;

        Dispatcher.BeginInvoke(() =>
        {
            // Rellenar un menu no dispara Click, asi que aqui no hace falta la
            // bandera que si necesitaba el desplegable: cada lista que llegaba
            // disparaba SelectionChanged y pedia al host la pantalla que el host
            // acababa de decir que ya estaba mostrando.
            MenuPantallas.Items.Clear();

            foreach (var (corto, largo) in opciones)
            {
                var entrada = new MenuItem
                {
                    Header = largo,
                    Tag = corto,
                    IsCheckable = true,
                    IsChecked = corto.Id == actual
                };

                entrada.Click += ElegirPantalla;
                MenuPantallas.Items.Add(entrada);
            }

            MenuPantallas.IsEnabled = opciones.Count > 1;

            MenuPantallas.ToolTip = opciones
                .FirstOrDefault(o => o.Corto.Id == actual).Largo is { Length: > 0 } vigente
                ? $"Pantalla: {vigente}"
                : "Pantalla remota";
        });
    }

    private void ElegirPantalla(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem elegido || elegido.Tag is not Monitor monitor)
            return;

        foreach (var otro in MenuPantallas.Items.OfType<MenuItem>())
            otro.IsChecked = ReferenceEquals(otro, elegido);

        MenuPantallas.ToolTip = $"Pantalla: {elegido.Header}";

        // El host rehace duplicador y codificador, asi que detras de esto vienen
        // una config nueva y un IDR. Las cifras de la barra no se reinician a
        // proposito: son de la SESION, no de la pantalla.
        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            SelectDisplay = new SelectDisplay { DisplayId = monitor.Id }
        });

        Nota($"Cambiando a {elegido.Header}...");
    }

    // ------------------------------------------------------------ portapapeles

    /// <summary>Lo ultimo que cruzo, en cualquiera de los dos sentidos. Sin esto,
    /// lo que llega de la PC remota se detecta como copia local y se le devuelve
    /// en cuanto la ventana recupera el foco.</summary>
    private string? _ultimoPortapapeles;

    /// <summary>
    /// Se sincroniza al ACTIVARSE la ventana, no continuamente: el tecnico copia
    /// algo en su PC, vuelve al visor, y en ese momento ya lo tiene alla. Sondear
    /// el portapapeles en bucle seria pelearse con el resto de sus aplicaciones
    /// por un recurso exclusivo para nada.
    /// </summary>
    private void EnviarPortapapeles(object? sender, EventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
                return;

            var texto = Clipboard.GetText();

            if (string.IsNullOrEmpty(texto)
                || texto == _ultimoPortapapeles
                || texto.Length > RemoteSessionProtocol.MaxClipboardChars)
            {
                return;
            }

            _ultimoPortapapeles = texto;

            Encolar(new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = _sesion,
                Clipboard = new ClipboardText { Text = texto }
            });
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // El portapapeles lo tiene otra aplicacion abierto. No es un fallo de
            // la sesion: se reintenta la proxima vez que la ventana se active.
        }
    }

    /// <summary>Lo que copiaron en la PC remota, en el portapapeles de aqui.</summary>
    private void RecibirPortapapeles(string texto)
    {
        if (string.IsNullOrEmpty(texto) || texto == _ultimoPortapapeles)
            return;

        _ultimoPortapapeles = texto;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Clipboard.SetText(texto);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        });
    }

    private void Nota(string texto)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => Aviso.Text = texto);
    }

    private long _rttUs = -1;
    private DateTimeOffset _reconectarHasta;

    /// <summary>Motivo del cierre, si ya llego. Se muestra DEBAJO de las cifras.</summary>
    private string? _cierre;

    /// <summary>
    /// Ping cada segundo. El RTT se calcula aqui, con NUESTRO reloj de ida y
    /// vuelta: restar marcas de tiempo de dos PCs distintas da un numero
    /// inventado, porque sus relojes monotonicos no son comparables.
    /// </summary>
    /// <summary>
    /// UNICO escritor del stream. gRPC no admite dos escrituras a la vez, y aqui
    /// escriben dos sitios: el latido y la entrada del tecnico, que llega desde
    /// el hilo de la interfaz.
    /// </summary>
    private async Task LatirAsync(IClientStreamWriter<RemotePacket> salida, CancellationToken cancellationToken)
    {
        try
        {
            var latido = Task.Delay(1000, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var evento = _salida.EsperarAsync(cancellationToken).AsTask();

                if (await Task.WhenAny(latido, evento) == latido)
                {
                    await salida.WriteAsync(new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = _sesion,
                        Ping = new Ping { SentAtUs = NowUs() }
                    }, cancellationToken);

                    latido = Task.Delay(1000, cancellationToken);
                    continue;
                }

                // El orden lo decide el buzon: rescate, movimiento, y despues lo
                // demas. Aqui solo se escribe.
                while (_salida.TryTomar(_sesion, out var paquete))
                    await salida.WriteAsync(paquete, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException)
        {
        }
    }

    // ---------------------------------------------------------- entrada remota

    /// <summary>
    /// Lo que el tecnico hace aqui, camino de la PC remota.
    ///
    /// Acotada y sin descarte: perder un KeyUp deja la tecla pegada al otro lado
    /// y el tecnico se encuentra un Ctrl invisible pulsado. Si se llenara -- 512
    /// eventos pendientes es una red que ya no funciona -- se cuenta y se ve en
    /// la barra de estado, en vez de fingir que no paso nada.
    ///
    /// ponytail: los movimientos del raton se mandan todos, sin fundirlos. A 60
    /// por segundo son unos pocos kB/s; si algun dia estorban, se coalescen aqui
    /// quedandose con el ultimo, que es correcto porque son posiciones
    /// absolutas, no incrementos.
    /// </summary>
    /// <summary>
    /// Lo que sale hacia el host, con sus prioridades. Vive fuera de la ventana
    /// porque ahi se puede probar: la coalescencia del raton, la saturacion y el
    /// vaciado al reconectar ya han fallado tres veces y ninguna se podia cubrir
    /// desde una Window.
    /// </summary>
    private readonly BuzonDeSalida _salida = new();



    private void Enviar(InputEvent evento)
        => Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            Input = evento
        });

    private void Enviar(HostAction accion)
        => Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            HostAction = accion
        });

    /// <summary>
    /// Confirma un frame. El host no captura el siguiente de esa pantalla hasta
    /// recibir esto.
    ///
    /// No pasa por Encolar a proposito: ahi se cuenta la ENTRADA, y meter un
    /// acuse por frame convertiria esa cifra -- que sirve para saber si el
    /// teclado y el raton estan viajando -- en ruido.
    /// </summary>
    private void Acusar(uint pantalla, ulong frame, bool pintado = false)
    {
        var ok = _salida.Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            VideoAck = new VideoAck
            {
                FrameId = frame,
                DisplayId = pantalla,
                Presented = pintado
            }
        });

        if (!ok)
            return;

        if (pintado)
            _acusesPintados++;
        else
            _acuses++;
    }

    private long _acuses;
    private long _acusesPintados;
    private long _idrPedidos;

    /// <summary>Cuando se pidio el ultimo IDR de cada pantalla.</summary>
    private readonly Dictionary<uint, long> _ultimaPeticionIdr = [];

    /// <summary>
    /// Pide un fotograma clave, COMO MUCHO UNO POR SEGUNDO Y PANTALLA.
    ///
    /// El limite no es prudencia: una perdida suele venir de que la red no da
    /// abasto, y un IDR es el frame mas caro que existe. Pedir uno por cada
    /// frame perdido convierte un atasco en una tormenta de keyframes que lo
    /// empeora. RustDesk limita el suyo igual, por cuenta y por tiempo.
    /// </summary>
    private void PedirKeyframe(uint pantalla, ulong ultimoBueno, KeyframeReason motivo)
    {
        var ahora = Stopwatch.GetTimestamp();

        if (_ultimaPeticionIdr.TryGetValue(pantalla, out var previa)
            && ahora - previa < Stopwatch.Frequency)
        {
            return;
        }

        _ultimaPeticionIdr[pantalla] = ahora;
        _idrPedidos++;

        _salida.Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            KeyframeRequest = new KeyframeRequest
            {
                Reason = motivo,
                LastGoodFrameId = ultimoBueno,
                DisplayId = pantalla
            }
        });
    }

    private void Encolar(RemotePacket paquete) => _salida.Encolar(paquete);

    /// <summary>
    /// De pixeles de la ventana a 0..1 sobre la pantalla remota.
    ///
    /// Normalizado y no en pixeles a proposito: el escritorio remoto puede
    /// cambiar de resolucion a media sesion, y unos pixeles calculados antes del
    /// cambio apuntarian a otro sitio. Ademas la ventana del visor casi nunca
    /// mide lo mismo que la pantalla que muestra.
    /// </summary>
    /// <summary>
    /// Traduce los mensajes crudos de Win32. Es feo comparado con los eventos de
    /// WPF y es lo unico que funciona: sobre un HwndHost, WPF no ve el raton.
    /// </summary>
    /// <summary>
    /// Si esta sesion se maneja, o solo se mira.
    ///
    /// En mosaico solo se mira. Cuatro escritorios a la vez son una pared de
    /// camaras, y ahi un clic significa "quiero esta grande", no "pulsa en esa
    /// PC": sin esto, acercarse a leer una pantalla moveria el raton de una PC de
    /// planta, y con cuatro abiertas ni siquiera estaria claro cual.
    ///
    /// Y se va con ella TODO EL MARCO. Una miniatura de un cuarto de pantalla con
    /// su barra de botones, su menu y sus estadisticas encima no deja ver el
    /// escritorio, que es lo unico que se estaba mirando. Al volver, la barra de
    /// datos vuelve como estaba: es una eleccion del tecnico, no del modo.
    /// </summary>
    public bool Interactiva
    {
        get;

        set
        {
            if (field == value)
                return;

            field = value;

            if (!value)
                _datosAntes = BarraEstado.Visibility;

            BarraSuperior.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            BarraEstado.Visibility = value ? _datosAntes : Visibility.Collapsed;

            // El panel de archivos no se restaura solo: se abre a mano y se
            // cierra a mano, y volver del mosaico no es ninguna de las dos.
            if (!value)
                PanelArchivos.Visibility = Visibility.Collapsed;
        }
    } = true;

    /// <summary>Como estaba la barra de datos antes de entrar en mosaico. Nace
    /// apagada, igual que ella.</summary>
    private Visibility _datosAntes = Visibility.Collapsed;

    /// <summary>Ya se encendieron solas por falta de imagen. Una vez y no cada
    /// medio segundo: el tecnico puede volver a apagarlas.</summary>
    private bool _medidasForzadas;

    /// <summary>Alguien pulso encima mientras NO era interactiva. Lo escucha la
    /// consola para sacarla del mosaico.</summary>
    public event EventHandler? Pulsada;

    private void RatonRemoto(double x, double y, int mensaje, IntPtr wParam)
    {
        if (x is < 0 or > 1 || y is < 0 or > 1)
            return;

        if (!Interactiva)
        {
            // Solo el clic, y solo al soltar: mover el raton por encima de un
            // mosaico no puede ir cambiando de pantalla sola.
            if (mensaje == VideoSurface.WmLButtonUp)
                Pulsada?.Invoke(this, EventArgs.Empty);

            return;
        }

        if (mensaje == VideoSurface.WmMouseWheel)
        {
            // El delta va en la palabra alta del wParam, con signo.
            Enviar(new InputEvent
            {
                MouseWheel = new MouseWheel { Delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF) }
            });

            return;
        }

        if (mensaje == WmMouseMove)
        {
            Enviar(new InputEvent { MouseMove = new MouseMove { X = x, Y = y } });
            return;
        }

        var (boton, pulsado) = mensaje switch
        {
            0x0201 => (MouseButtonId.MouseButtonLeft, true),
            0x0202 => (MouseButtonId.MouseButtonLeft, false),
            0x0204 => (MouseButtonId.MouseButtonRight, true),
            0x0205 => (MouseButtonId.MouseButtonRight, false),
            0x0207 => (MouseButtonId.MouseButtonMiddle, true),
            0x0208 => (MouseButtonId.MouseButtonMiddle, false),
            _ => (MouseButtonId.MouseButtonUnspecified, false)
        };

        if (boton == MouseButtonId.MouseButtonUnspecified)
            return;

        // El foco vuelve a la ventana WPF en cada clic: sin el, las teclas se las
        // queda otra ventana y el tecnico escribe en su propia PC sin darse
        // cuenta -- que es bastante peor que no escribir en ningun sitio.
        if (pulsado)
            Dispatcher.BeginInvoke(Focus);

        Enviar(new InputEvent
        {
            MouseButton = new Contracts.MouseButton
            {
                Button = boton, Pressed = pulsado, X = x, Y = y
            }
        });
    }

    private const int WmMouseMove = 0x0200;

    /// <summary>
    /// VK + scan code + extendida, nunca caracteres: sin scan code, media tecla
    /// no funciona en las aplicaciones que leen el teclado a bajo nivel.
    ///
    /// Preview* y no los eventos normales porque el Tab, las flechas y el Alt los
    /// consume WPF para navegar entre controles antes de que lleguen.
    /// </summary>
    /// <summary>
    /// El teclado LO REPARTE LA CONSOLA, no cada sesion.
    ///
    /// Antes esto estaba suscrito a la ventana, porque el teclado va a donde
    /// este el foco y el "static" del video no lo toma: la ventana se quedaba
    /// con el y lo tunelaba. Convertido en control eso deja de funcionar -- WPF
    /// tunela desde la raiz hasta el elemento CON FOCO, y si el foco se queda en
    /// la ventana, el control no lo ve pasar. La sesion se veria y no se podria
    /// escribir en ella.
    ///
    /// Y con pestañas es ademas lo correcto: hay un solo teclado y varias PCs, y
    /// quien sabe a cual va es la consola.
    /// </summary>
    public void Teclear(KeyEventArgs e, bool pulsada) => Tecla(e, pulsada);

    private void Tecla(KeyEventArgs e, bool pulsada)
    {
        // SystemKey es lo que trae la tecla real cuando Alt esta pulsado.
        var tecla = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl+Alt+Fin, igual que en Escritorio remoto de Windows. No se puede
        // reenviar el Ctrl+Alt+Supr de verdad -- lo intercepta Windows aqui y
        // nunca llega a la aplicacion -- asi que se usa un sustituto y el host lo
        // convierte en la secuencia real con SendSAS.
        if (pulsada && tecla == Key.End
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Enviar(new HostAction { Kind = HostAction.Types.Kind.HostActionCtrlAltDel });
            e.Handled = true;
            return;
        }

        // Cierra la Fase 12: las medidas ya se calculaban, faltaba el interruptor.
        if (pulsada && tecla == Key.F12
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            AlternarDatos(this, e);
            e.Handled = true;
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(tecla);

        if (vk == 0)
            return;

        Enviar(new InputEvent
        {
            Key = new KeyEvent
            {
                VirtualKey = (uint)vk,
                ScanCode = 0,                 // lo resuelve el host con MapVirtualKey
                Pressed = pulsada,
                Extended = Extendida(tecla)
            }
        });

        // Sin esto, Tab y las flechas mueven el foco DENTRO del visor en vez de
        // viajar a la PC remota.
        e.Handled = true;
    }

    /// <summary>
    /// Teclas que comparten scan code con el teclado numerico. Sin la marca de
    /// extendida, Windows resuelve la ambiguedad al reves de lo que se pulso: la
    /// flecha arriba se convierte en un 8.
    /// </summary>
    private static bool Extendida(Key tecla) => tecla is
        Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown or
        Key.Left or Key.Right or Key.Up or Key.Down or
        Key.NumLock or Key.PrintScreen or Key.Divide or
        Key.RightAlt or Key.RightCtrl or Key.LWin or Key.RWin or Key.Apps;

    private static long NowUs() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    private static long Micros(long ticks) => ticks * 1_000_000L / Stopwatch.Frequency;

    /// <summary>
    /// Lo que se esta viendo, en una linea. Con varias pantallas interesa el
    /// LIENZO y cuantos flujos lo componen, no la resolucion de una de ellas.
    /// </summary>
    private static string Resumen(Dictionary<uint, VideoConfig> configs)
    {
        if (configs.Count == 0)
            return "sin config";

        var primera = configs.Values.First();

        return configs.Count == 1
            ? $"{primera.Width}x{primera.Height} v{primera.ConfigVersion}"
            : $"{configs.Count} pantallas {primera.CanvasWidth}x{primera.CanvasHeight}";
    }

    /// <summary>Desglose por pantalla, solo cuando hay mas de una.</summary>
    private static string PorPantalla(Dictionary<uint, long> cuenta)
        => cuenta.Count <= 1
            ? string.Empty
            : $" ({string.Join(" ", cuenta.OrderBy(x => x.Key).Select(x => $"p{x.Key}:{x.Value}"))})";

    private static double Percentil(List<long> ordenadas, double p)
    {
        if (ordenadas.Count == 0)
            return 0;

        var indice = Math.Clamp((int)Math.Ceiling(p * ordenadas.Count) - 1, 0, ordenadas.Count - 1);
        return ordenadas[indice] / 1000.0;
    }

    private void Mostrar(string texto)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => Estado.Text = texto);
    }
}
