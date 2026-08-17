using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
using System.Windows;
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

        Closed += (_, _) =>
        {
            _cancelacion.Cancel();
            _cancelacion.Dispose();
        };

        // Del WndProc de la ventana hija, no de los eventos de WPF: el video se
        // dibuja en una ventana Win32 encima del arbol visual, y los mensajes del
        // raton van a ella. Con los eventos de WPF el video se veia y no se podia
        // controlar nada.
        Video.Raton += RatonRemoto;

        // A nivel de ventana, no del video: el teclado va a donde este el foco, y
        // el control "static" no lo toma.
        PreviewKeyDown += (_, e) => Tecla(e, pulsada: true);
        PreviewKeyUp += (_, e) => Tecla(e, pulsada: false);
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
            while (!_cancelacion.IsCancellationRequested)
            {
                try
                {
                    RecibirAsync().GetAwaiter().GetResult();
                }
                catch (RpcException ex) when (_token is not null && !_cancelacion.IsCancellationRequested)
                {
                    Mostrar($"Conexion perdida ({ex.StatusCode}). Reconectando con el token...");
                    Thread.Sleep(500);
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

    private async Task RecibirAsync()
    {
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

        using var llamada = cliente.ViewerChannel(cancellationToken: _cancelacion.Token);

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
        }, _cancelacion.Token);

        var latidos = LatirAsync(llamada.RequestStream, _cancelacion.Token);

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
            while (await llamada.ResponseStream.MoveNext(_cancelacion.Token))
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

                                presentador.Present(imagen.Texture, imagen.Subresource);
                                pintados++;
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

                        break;

                    case RemotePacket.PayloadOneofCase.Pong:
                        _rttUs = NowUs() - paquete.Pong.SentAtUs;
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
            presentador?.Dispose();
            decoder?.Dispose();

            await _cancelacion.CancelAsync();
            try { await latidos; } catch (Exception) { /* cerrando */ }
        }
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
