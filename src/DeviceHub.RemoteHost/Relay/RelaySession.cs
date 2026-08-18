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
            using var canal = Conectar(opciones);
            var cliente = new RemoteRelayService.RemoteRelayServiceClient(canal);

            using var llamada = cliente.HostChannel();

            await EscribirAsync(llamada, new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = opciones.SesionId,
                Hello = new Hello
                {
                    MachineId = opciones.MachineId,
                    Role = RemoteRole.Host,
                    Ticket = ticket,
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

    private sealed class Contadores
    {
        public long Capturados, Codificados, Claves, Enviados, Trozos, Bytes;
        public long DescartesEncoder, DescartesCaptura;
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
                SingleWriter = true
            });

        var cuenta = new Contadores();

        var hilo = new Thread(() => Capturar(cola.Writer, cuenta, opciones, cancellationToken))
        {
            IsBackground = true,
            Name = "devicehub-captura"
        };

        hilo.Start();

        var reloj = Stopwatch.StartNew();
        var siguienteAviso = TimeSpan.FromSeconds(2);

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
                    opciones.Escribir(
                        $"{reloj.Elapsed:mm\\:ss}  capturados {cuenta.Capturados}  codificados {cuenta.Codificados}  " +
                        $"frames enviados {cuenta.Enviados}  chunks {cuenta.Trozos}  " +
                        $"{cuenta.Bytes * 8 / reloj.Elapsed.TotalSeconds / 1_000_000:0.00} Mbps  " +
                        $"keyframes {cuenta.Claves}  config {cuenta.ConfigVersion}  cola {cola.Reader.Count}  " +
                        // Aplicados y rechazados de SendInput. Es lo que dice si
                        // la entrada llega de verdad al otro lado o se la traga
                        // el escritorio equivocado.
                        $"entrada {_entrada?.Applied ?? 0}/{_entrada?.Rejected ?? 0}");

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
                    fallosSeguidos = 0;
                    continue;
                }

                // Al saltar a Winlogon el primer DuplicateOutput falla a menudo
                // porque la transicion del escritorio sigue en curso. Antes ese
                // fallo normal y recuperable terminaba la sesion entera.
                fallosSeguidos++;

                opciones.Escribir(
                    $"Fallo al capturar (intento {fallosSeguidos}): {fallo.GetType().Name}: {fallo.Message}");

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

            while (!cancellationToken.IsCancellationRequested)
            {
                var salto = escritorio.SeguirActivo();

                // Se avisa UNA vez por estado, no cada 20 ms: en un bucle asi, un
                // log por vuelta convierte el visor de eventos en el cuello de
                // botella de la sesion.
                var aviso = salto switch
                {
                    Salto.Cambiado => $"La entrada salto a {escritorio.Name}",

                    Salto.NoSePudoAtar =>
                        $"NO se pudo atar la entrada a {escritorio.NombrePedido} " +
                        $"(error {escritorio.UltimoError}). El raton y el teclado van al escritorio viejo.",

                    _ => ultimoAviso
                };

                if (aviso != ultimoAviso)
                {
                    opciones.Escribir(aviso);
                    ultimoAviso = aviso;
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
        var pedida = _pantalla;
        var elegida = pantallas.FirstOrDefault(p => p.Id == pedida);

        // Todas a la vez compone N duplicaciones en una imagen del tamano del
        // escritorio virtual; una sola entrega la textura del duplicador sin
        // copiar nada. La entrada funciona igual con las dos: InputInjector
        // recibe la esquina de lo capturado y traduce a coordenadas virtuales.
        // El escritorio con el que se abrio ESTA captura. Todo lo que venga
        // despues se compara contra el.
        var escritorioCapturado = InputDesktop.NombreDeEntrada();

        using IScreenCapture captura = Abrir(pedida, elegida, opciones);

        // La lista viaja al empezar y en cada cambio de pantalla: es cuando el
        // visor necesita repintar su selector, y cuesta un mensaje.
        salida.TryWrite(new Enviable(null, null, new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = opciones.SesionId,
            Displays = ListaDePantallas(pantallas, pedida)
        }));

        // El inyector necesita el tamano y la esquina de ESTA pantalla, que
        // solo se conocen despues de abrir la captura. Se publica aqui para
        // que el hilo de red pueda aplicar la entrada en cuanto llegue.
        //
        // SendInput no toca la GPU, asi que puede correr en el hilo de red
        // sin la disciplina de un solo hilo que exigen DXGI y el MFT.
        _entrada = new InputInjector(
            captura.Width, captura.Height, captura.DesktopLeft, captura.DesktopTop);

        using var codificador = new H264Encoder(
            captura.Device, captura.Width, captura.Height, opciones.Fps, opciones.Bitrate,
            captura.AdapterLuid, captura.AdapterVendorId);

        // La IDENTIDAD va en la misma linea que el MFT a proposito. Es la unica
        // forma de saber, leyendo el log del agente, si esta sesion corrio como
        // SYSTEM y con que codificador: son las dos cifras que hacen falta para
        // cerrar la duda que dejo abierta el intento anterior de la Fase 19.
        opciones.Escribir(
            $"Identidad {System.Security.Principal.WindowsIdentity.GetCurrent().Name}  " +
            $"Escritorio {escritorio.Name}  Adapter {captura.Adapter}  MFT {codificador.Capabilities.Name}  " +
            $"Hardware {(codificador.Capabilities.Hardware ? "TRUE" : "FALSE")}  " +
            $"Resolution {captura.Width}x{captura.Height}");

        var avisadoDeCeguera = 0;
        var version = ++cuenta.ConfigVersion;
        var configEnviada = false;
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
                if (captura.TomarCursor() is { } puntero)
                {
                    var aviso = new CursorUpdate
                    {
                        X = puntero.X,
                        Y = puntero.Y,
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

                IReadOnlyList<EncodedFrame> producidos;

                // El frame DXGI se suelta ANTES de esperar por nada. Encolar
                // puede bloquear si la red va por detras, y quedarse la
                // superficie duplicada mientras tanto es lo que no se hace.
                using (var frame = captura.CaptureAsync(cancellationToken).GetAwaiter().GetResult())
                {
                    if (frame is null || !frame.DesktopChanged)
                        continue;

                    cuenta.Capturados++;
                    producidos = codificador.Encode(frame, cancellationToken);
                }

                foreach (var frameCodificado in producidos)
                {
                    cuenta.Codificados++;

                    if (frameCodificado.IsKeyFrame)
                        cuenta.Claves++;

                    VideoConfig? config = null;

                    // El SPS/PPS sale dentro del primer IDR. Se saca UNA vez, se
                    // manda en VideoConfig y a partir de ahi el viewer lo
                    // conserva: reenviarlo con cada keyframe es ancho de banda
                    // que no aporta nada a quien ya lo tiene.
                    if (!configEnviada && frameCodificado.IsKeyFrame)
                    {
                        var parametros = H264AnnexB.ParameterSets(frameCodificado.Payload);

                        if (parametros.Length == 0)
                            continue;   // todavia no; el siguiente IDR los traera

                        configEnviada = true;

                        config = new VideoConfig
                        {
                            ConfigVersion = version,
                            Codec = VideoCodec.H264,
                            Width = (uint)frameCodificado.Width,
                            Height = (uint)frameCodificado.Height,
                            FramesPerSecond = (uint)opciones.Fps,
                            BitrateBitsPerSecond = (uint)opciones.Bitrate,
                            ParameterSets = Google.Protobuf.ByteString.CopyFrom(parametros),
                            VisibleWidth = (uint)frameCodificado.Width,
                            VisibleHeight = (uint)frameCodificado.Height
                        };
                    }

                    if (!configEnviada)
                        continue;   // sin configuracion no hay nada descodificable

                    var grupo = VideoFraming.Split(
                        frameCodificado.Sequence, frameCodificado.IsKeyFrame, version,
                        frameCodificado.TimestampUs, frameCodificado.Payload);

                    salida.WriteAsync(new Enviable(config, grupo), cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                }
            }

            cuenta.DescartesEncoder = codificador.Dropped;
            cuenta.DescartesCaptura = captura.Dropped;
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
            opciones.Escribir($"DXGI no puede capturar aqui ({ex.Message}); se pasa al respaldo GDI");

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
                        // La Fase 13 forzara un IDR con ICodecAPI. Aqui basta con
                        // el que el codificador genera por su cuenta.
                        opciones.Escribir("El viewer pidio un keyframe.");
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
