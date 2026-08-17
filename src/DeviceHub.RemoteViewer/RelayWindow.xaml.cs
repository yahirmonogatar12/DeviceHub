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
public partial class RelayWindow : Window
{
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

    public RelayWindow(
        string servidor, string sesion, string machineId, bool permitirSinConfianza, string pin = "")
    {
        InitializeComponent();

        _servidor = servidor;
        _sesion = sesion;
        _machineId = machineId;
        _permitirSinConfianza = permitirSinConfianza;
        _pin = pin;

        Title = $"DeviceHub - sesion {sesion}";

        Loaded += (_, _) => new Thread(Ejecutar)
        {
            IsBackground = true,
            Name = "devicehub-relay-viewer"
        }.Start();

        // Cancelar si, disponer NO: el hilo de reconexion puede estar esperando
        // en _cancelacion.Token.WaitHandle, y disponer el origen mientras alguien
        // espera en su handle lanza. El proceso termina detras de esto.
        Closed += (_, _) => _cancelacion.Cancel();

        // Del WndProc de la ventana hija, no de los eventos de WPF: el video se
        // dibuja en una ventana Win32 encima del arbol visual, y los mensajes del
        // raton van a ella. Con los eventos de WPF el video se veia y no se podia
        // controlar nada.
        Video.Raton += RatonRemoto;

        // A nivel de ventana, no del video: el teclado va a donde este el foco, y
        // el control "static" no lo toma.
        PreviewKeyDown += (_, e) => Tecla(e, pulsada: true);
        PreviewKeyUp += (_, e) => Tecla(e, pulsada: false);

        Activated += EnviarPortapapeles;
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
            _ticket = BootstrapTicket.Read();

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
                    Codecs = { VideoCodec.H264 },
                    SupportsCursor = false,
                    SupportsInput = true
                }
            }
        }, intento.Token);

        var latidos = LatirAsync(llamada.RequestStream, intento.Token);

        using var device = VideoPresenter.CreateDevice();

        H264Decoder? decoder = null;
        VideoPresenter? presentador = null;
        VideoConfig? config = null;

        var montador = new VideoFrameAssembler();
        var proceso = Process.GetCurrentProcess();
        var ramInicio = proceso.PrivateMemorySize64;

        var decodificaciones = new List<long>();
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
                        // El relay REPITE la configuracion vigente cada vez que
                        // se recupera de una perdida, para que un viewer que se
                        // la hubiera perdido pueda descodificar el IDR que viene
                        // detras. Repetida no es nueva: rehacer el decodificador
                        // aqui costaba 45 ms, tiraba el presentador y llenaba la
                        // cola del relay, que provocaba otro descarte y otra
                        // repeticion. Un bucle que se alimentaba solo.
                        if (config is not null && paquete.VideoConfig.ConfigVersion == config.ConfigVersion)
                            break;

                        cambiosConfig++;
                        config = paquete.VideoConfig;

                        // La resolucion remota puede cambiar a media sesion, asi
                        // que el tamano del lienzo se recalcula aqui y no una vez
                        // al arrancar.
                        var (ancho, alto) = ((int)config.Width, (int)config.Height);
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            _videoAncho = ancho;
                            _videoAlto = alto;
                            Ajustar();
                        });

                        // Una grabacion en curso no sobrevive a un cambio de
                        // flujo: el SPS que lleva dentro deja de valer y el
                        // archivo quedaria con dos resoluciones pegadas.
                        CerrarGrabacion();

                        // Version nueva SI es flujo nuevo: el decodificador viejo
                        // lleva el SPS anterior dentro.
                        presentador?.Dispose();
                        presentador = null;
                        decoder?.Dispose();

                        decoder = new H264Decoder(device, (int)config.Width, (int)config.Height);

                        if (config.ParameterSets.Length > 0)
                        {
                            var parametros = config.ParameterSets.ToByteArray();

                            foreach (var frame in decoder.Decode(parametros, 0, parametros.Length, 0))
                                frame.Dispose();
                        }

                        break;

                    case RemotePacket.PayloadOneofCase.VideoChunk:
                        chunks++;

                        if (decoder is null)
                            break;   // llego video antes que su configuracion

                        if (paquete.VideoChunk.ConfigVersion != config?.ConfigVersion)
                            break;

                        if (!montador.TryAdd(paquete.VideoChunk, out var completo))
                            break;

                        reconstruidos++;

                        if (completo!.KeyFrame)
                            idr++;

                        Grabar(completo, config);

                        var antes = Stopwatch.GetTimestamp();
                        var salidas = decoder.Decode(completo.Payload, 0, completo.Payload.Length, completo.CaptureTimestampUs);
                        decodificaciones.Add(Micros(Stopwatch.GetTimestamp() - antes));

                        foreach (var imagen in salidas)
                        {
                            using (imagen)
                            {
                                decodificados++;

                                presentador ??= new VideoPresenter(
                                    device, hwnd, decoder.Width, decoder.Height,
                                    decoder.Aperture.X, decoder.Aperture.Y,
                                    decoder.Aperture.Width, decoder.Aperture.Height);

                                // La captura la pide la interfaz y la atiende el
                                // presentador: el frame solo existe convertido a
                                // RGB dentro de Present.
                                var captura = Interlocked.Exchange(ref _captura, null);

                                presentador.Present(imagen.Texture, imagen.Subresource, captura);
                                pintados++;

                                if (captura is not null)
                                    Nota($"Captura guardada en {captura}");
                            }
                        }

                        break;

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

                        // La reconexion funciono: la espera vuelve al principio.
                        // Sin esto, un microcorte a los diez minutos empezaria
                        // esperando los 5 s a los que llego el corte anterior.
                        _espera = TimeSpan.FromMilliseconds(250);

                        break;

                    case RemotePacket.PayloadOneofCase.Pong:
                        _rttUs = NowUs() - paquete.Pong.SentAtUs;
                        break;

                    case RemotePacket.PayloadOneofCase.Clipboard:
                        RecibirPortapapeles(paquete.Clipboard.Text);
                        break;

                    case RemotePacket.PayloadOneofCase.Displays:
                        RecibirPantallas(paquete.Displays);
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

                    Mostrar(
                        $"sesion {_sesion}   {(config is null ? "sin config" : $"{config.Width}x{config.Height} v{config.ConfigVersion}")}   " +
                        $"RTT {(_rttUs < 0 ? "-" : $"{_rttUs / 1000.0:0.0} ms")}\n" +
                        $"chunks {chunks}   frames {reconstruidos}   decodificados {decodificados}   pintados {pintados}   " +
                        $"render {pintados / segundos:0.00} FPS   " +
                        $"decode p50 {Percentil(ordenadas, 0.50):0.00} ms   p95 {Percentil(ordenadas, 0.95):0.00} ms\n" +
                        // La entrada enviada va en la barra a proposito: cuando
                        // el video se ve pero no se puede controlar, esta cifra
                        // dice de un vistazo cual de las dos mitades falla.
                        $"entrada {_entradaEnviada}   " +
                        (_grabacion is null ? string.Empty : $"grabando {_grabados} frames   ") +
                        $"incompletos {montador.Dropped}   invalidos {montador.Rejected}   tardios {montador.Stale}   " +
                        $"IDR {idr}   cambios de config {cambiosConfig}   " +
                        $"RAM {proceso.PrivateMemorySize64 / 1024 / 1024} MB (inicio {ramInicio / 1024 / 1024})   " +
                        $"{reloj.Elapsed:hh\\:mm\\:ss}" +
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
            decoder?.Dispose();

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
    private long _grabados;

    private static string Carpeta(Environment.SpecialFolder donde)
        => Path.Combine(Environment.GetFolderPath(donde), "DeviceHub");

    private string NombreArchivo(string extension)
        => Path.Combine(
            Carpeta(extension == ".png" ? Environment.SpecialFolder.MyPictures : Environment.SpecialFolder.MyVideos),
            $"{_machineId}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");

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

        BotonGrabar.Content = _quiereGrabar ? "Detener" : "Grabar";
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
    private void Grabar(AssembledFrame frame, VideoConfig? config)
    {
        if (!_quiereGrabar)
        {
            CerrarGrabacion();
            return;
        }

        if (_grabacion is null)
        {
            if (config is null)
                return;

            var ruta = NombreArchivo(".h264");
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

    private void CambiarEscala(object sender, SelectionChangedEventArgs e)
    {
        if (Escala.SelectedItem is ComboBoxItem { Tag: string etiqueta })
            _escala = double.Parse(etiqueta, CultureInfo.InvariantCulture);

        Ajustar();
    }

    private void Ajustar(object sender, SizeChangedEventArgs e) => Ajustar();

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

        (Video.Width, Video.Height) = Escalado.Encajar(_videoAncho, _videoAlto, ancho, alto, _escala);
    }

    private void AlternarDatos(object sender, RoutedEventArgs e)
        => BarraEstado.Visibility = BarraEstado.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

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

    // ---------------------------------------------------------------- archivos

    /// <summary>Una fila del panel. `Etiqueta` es lo que se ve.</summary>
    private sealed record Entrada(string Nombre, bool Carpeta, ulong Tamano)
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

        Directory.CreateDirectory(carpeta);

        _destinoFinal = Path.Combine(carpeta, archivo.Nombre);
        var parcial = _destinoFinal + ".parcial";

        // REANUDAR: lo que ya haya en el .parcial es el punto de partida. Nadie
        // lleva un registro aparte -- el propio archivo es el estado.
        var desde = File.Exists(parcial) ? (ulong)new FileInfo(parcial).Length : 0;

        _bajando?.Dispose();
        _bajando = new FileStream(parcial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        _bajando.Seek((long)desde, SeekOrigin.Begin);

        Progreso.Value = 0;
        EstadoArchivos.Text = desde > 0
            ? $"Reanudando {archivo.Nombre} desde {desde / 1024} KB..."
            : $"Descargando {archivo.Nombre}...";

        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            FileDownload = new FileDownloadRequest { Path = Combinar(archivo.Nombre), Offset = desde }
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

        if (dialogo.ShowDialog(this) != true)
            return;

        _subiendo?.Dispose();
        _subiendo = new FileStream(dialogo.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _tamanoSubida = (ulong)_subiendo.Length;
        _destinoRemoto = Path.Combine(_rutaRemota, Path.GetFileName(dialogo.FileName));

        Progreso.Value = 0;
        EstadoArchivos.Text = $"Subiendo {Path.GetFileName(dialogo.FileName)}...";

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

    // --------------------------------------------------------------- pantallas

    /// <summary>Una entrada del selector. `Etiqueta` es lo que se ve y `Id` lo
    /// que viaja; -1 es el escritorio virtual entero.</summary>
    private sealed record Monitor(int Id, string Etiqueta);

    /// <summary>Rellenar el ComboBox dispara SelectionChanged, que pediria al host
    /// la pantalla que el host acaba de decir que ya esta mostrando. Sin esta
    /// bandera, cada lista provoca una recaptura completa.</summary>
    private bool _rellenandoMonitores;

    private void RecibirPantallas(DisplayList lista)
    {
        var monitores = new List<Monitor>();

        foreach (var d in lista.Displays)
        {
            monitores.Add(new Monitor(
                d.Id,
                $"{d.Name.Replace(@"\\.\", string.Empty)}  {d.Width}x{d.Height}" +
                (d.Primary ? "  (principal)" : string.Empty)));
        }

        // Solo tiene sentido ofrecer el escritorio completo si hay mas de una.
        if (monitores.Count > 1)
        {
            var ancho = lista.Displays.Max(d => d.X + d.Width) - lista.Displays.Min(d => d.X);
            var alto = lista.Displays.Max(d => d.Y + d.Height) - lista.Displays.Min(d => d.Y);

            monitores.Insert(0, new Monitor(-1, $"Todas a la vez  {ancho}x{alto}"));
        }

        var actual = lista.Current;

        Dispatcher.BeginInvoke(() =>
        {
            _rellenandoMonitores = true;

            try
            {
                Monitores.ItemsSource = monitores;
                Monitores.SelectedItem = monitores.FirstOrDefault(m => m.Id == actual);
                Monitores.IsEnabled = monitores.Count > 1;
            }
            finally
            {
                _rellenandoMonitores = false;
            }
        });
    }

    private void CambiarPantalla(object sender, SelectionChangedEventArgs e)
    {
        if (_rellenandoMonitores || Monitores.SelectedItem is not Monitor elegido)
            return;

        // El host rehace duplicador y codificador, asi que detras de esto vienen
        // una config nueva y un IDR. Las cifras de la barra no se reinician a
        // proposito: son de la SESION, no de la pantalla.
        Encolar(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = _sesion,
            SelectDisplay = new SelectDisplay { DisplayId = elegido.Id }
        });

        Nota($"Cambiando a {elegido.Etiqueta}...");
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
                var evento = _salida.Reader.WaitToReadAsync(cancellationToken).AsTask();

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

                while (_salida.Reader.TryRead(out var paquete))
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
    private readonly Channel<RemotePacket> _salida =
        Channel.CreateBounded<RemotePacket>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    private long _entradaPerdida;

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

    private void Encolar(RemotePacket paquete)
    {
        if (_salida.Writer.TryWrite(paquete))
            _entradaEnviada++;
        else
            _entradaPerdida++;
    }

    private long _entradaEnviada;

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
    private void RatonRemoto(double x, double y, int mensaje, IntPtr wParam)
    {
        if (x is < 0 or > 1 || y is < 0 or > 1)
            return;

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
