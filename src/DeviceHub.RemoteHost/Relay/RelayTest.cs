using System.Diagnostics;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Capture;
using DeviceHub.RemoteHost.Encode;
using Grpc.Core;
using Grpc.Net.Client;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Relay;

/// <summary>
/// Modo --relay-test: la cadena de las Fases 1 y 2, pero con la salida enchufada
/// al relay en vez de a un archivo.
///
///   DXGI -> H264 -> VideoFrameChunks -> RemoteRelayService.HostChannel
///
/// Es un modo de DIAGNOSTICO. En produccion a este proceso lo lanza el agente
/// dentro de la sesion interactiva y le pasa la sesion y el ticket por un named
/// pipe: eso es la Fase 7, y el ticket nunca viaja por linea de comandos.
/// </summary>
public static class RelayTest
{
    public static async Task<int> RunAsync(
        string servidor, string sesionId, int adapterIndex, int outputIndex,
        int seconds, int fps, int bitrate, bool permitirSinConfianza)
    {
        MediaFactory.MFStartup(true).CheckError();

        try
        {
            using var canal = Conectar(servidor, permitirSinConfianza);
            var cliente = new RemoteRelayService.RemoteRelayServiceClient(canal);

            using var llamada = cliente.HostChannel();

            await EscribirAsync(llamada, new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = sesionId,
                Hello = new Hello
                {
                    Role = RemoteRole.Host,
                    MachineId = Environment.MachineName,

                    // Vacio a proposito: la validacion es la Fase 6, y aqui no
                    // hay ningun sitio de donde sacarlo que no sea la linea de
                    // comandos, que es justo lo que no se hace.
                    Ticket = string.Empty,
                    Capabilities = new RemoteCapabilities
                    {
                        MaxProtocolVersion = RemoteSessionProtocol.Version,
                        Codecs = { VideoCodec.H264 },
                        SupportsCursor = false,
                        SupportsInput = false
                    }
                }
            }, CancellationToken.None);

            using var cancelacion = new CancellationTokenSource();
            var entrante = LeerAsync(llamada, sesionId, cancelacion.Token);

            var codigo = await EmitirAsync(
                llamada, sesionId, adapterIndex, outputIndex, seconds, fps, bitrate, cancelacion.Token);

            await EscribirAsync(llamada, new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = sesionId,
                Close = new SessionClose { Reason = SessionCloseReason.Normal, Detail = "fin de --relay-test" }
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

    /// <summary>Lo que el hilo de captura le pasa al de red: bytes ya
    /// codificados. Ni texturas, ni muestras, ni nada de GPU.</summary>
    private sealed record Enviable(VideoConfig? Config, VideoFrameChunks? Grupo);

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
        AsyncDuplexStreamingCall<RemotePacket, RemotePacket> llamada,
        string sesionId, int adapterIndex, int outputIndex, int seconds, int fps, int bitrate,
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

        var hilo = new Thread(() => Capturar(
            cola.Writer, cuenta, sesionId, adapterIndex, outputIndex, seconds, fps, bitrate, cancellationToken))
        {
            IsBackground = true,
            Name = "devicehub-captura"
        };

        hilo.Start();

        var reloj = Stopwatch.StartNew();
        var siguienteAviso = TimeSpan.FromSeconds(2);

        await foreach (var pieza in cola.Reader.ReadAllAsync(cancellationToken))
        {
            if (pieza.Config is not null)
            {
                await EscribirAsync(llamada, new RemotePacket
                {
                    ProtocolVersion = RemoteSessionProtocol.Version,
                    SessionId = sesionId,
                    VideoConfig = pieza.Config
                }, cancellationToken);
            }

            if (pieza.Grupo is not null)
            {
                foreach (var trozo in pieza.Grupo.Chunks)
                {
                    await EscribirAsync(llamada, new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = sesionId,
                        VideoChunk = trozo
                    }, cancellationToken);

                    cuenta.Trozos++;
                }

                cuenta.Enviados++;
                cuenta.Bytes += pieza.Grupo.PayloadBytes;
            }

            if (reloj.Elapsed >= siguienteAviso)
            {
                Console.WriteLine(
                    $"{reloj.Elapsed:mm\\:ss}  capturados {cuenta.Capturados}  codificados {cuenta.Codificados}  " +
                    $"frames enviados {cuenta.Enviados}  chunks {cuenta.Trozos}  " +
                    $"{cuenta.Bytes * 8 / reloj.Elapsed.TotalSeconds / 1_000_000:0.00} Mbps  " +
                    $"keyframes {cuenta.Claves}  config {cuenta.ConfigVersion}  cola {cola.Reader.Count}");

                Console.Out.Flush();
                siguienteAviso += TimeSpan.FromSeconds(2);
            }
        }

        hilo.Join(TimeSpan.FromSeconds(3));

        if (cuenta.Fallo is not null)
            throw cuenta.Fallo;

        var segundos = Math.Max(reloj.Elapsed.TotalSeconds, 0.001);

        Console.WriteLine();
        Console.WriteLine($"Captured:      {cuenta.Capturados}");
        Console.WriteLine($"Encoded:       {cuenta.Codificados}");
        Console.WriteLine($"Frames sent:   {cuenta.Enviados}");
        Console.WriteLine($"Chunks sent:   {cuenta.Trozos}");
        Console.WriteLine($"Mbps:          {cuenta.Bytes * 8 / segundos / 1_000_000:0.00}");
        Console.WriteLine($"Keyframes:     {cuenta.Claves}");
        Console.WriteLine($"Config:        v{cuenta.ConfigVersion}");
        Console.WriteLine($"Encode drops:  {cuenta.DescartesEncoder}  (captura {cuenta.DescartesCaptura})");
        Console.Out.Flush();

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
        string sesionId, int adapterIndex, int outputIndex, int seconds, int fps, int bitrate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var captura = new DxgiDesktopCapture(adapterIndex, outputIndex);
            using var codificador = new H264Encoder(
                captura.Device, captura.Width, captura.Height, fps, bitrate,
                captura.AdapterLuid, captura.AdapterVendorId);

            Console.WriteLine($"Sesion:        {sesionId}");
            Console.WriteLine($"Adapter:       {captura.Adapter}");
            Console.WriteLine($"MFT:           {codificador.Capabilities.Name}");
            Console.WriteLine($"Hardware:      {(codificador.Capabilities.Hardware ? "TRUE" : "FALSE")}");
            Console.WriteLine($"Resolution:    {captura.Width}x{captura.Height}");
            Console.WriteLine();
            Console.Out.Flush();

            var reloj = Stopwatch.StartNew();
            var duracion = TimeSpan.FromSeconds(seconds);

            while (reloj.Elapsed < duracion && !cancellationToken.IsCancellationRequested)
            {
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
                    if (cuenta.ConfigVersion == 0 && frameCodificado.IsKeyFrame)
                    {
                        var parametros = H264AnnexB.ParameterSets(frameCodificado.Payload);

                        if (parametros.Length == 0)
                            continue;   // todavia no; el siguiente IDR los traera

                        cuenta.ConfigVersion = 1;

                        config = new VideoConfig
                        {
                            ConfigVersion = cuenta.ConfigVersion,
                            Codec = VideoCodec.H264,
                            Width = (uint)frameCodificado.Width,
                            Height = (uint)frameCodificado.Height,
                            FramesPerSecond = (uint)fps,
                            BitrateBitsPerSecond = (uint)bitrate,
                            ParameterSets = Google.Protobuf.ByteString.CopyFrom(parametros),
                            VisibleWidth = (uint)frameCodificado.Width,
                            VisibleHeight = (uint)frameCodificado.Height
                        };
                    }

                    if (cuenta.ConfigVersion == 0)
                        continue;   // sin configuracion no hay nada descodificable

                    var grupo = VideoFraming.Split(
                        frameCodificado.Sequence, frameCodificado.IsKeyFrame, cuenta.ConfigVersion,
                        frameCodificado.TimestampUs, frameCodificado.Payload);

                    salida.WriteAsync(new Enviable(config, grupo), cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                }
            }

            cuenta.DescartesEncoder = codificador.Dropped;
            cuenta.DescartesCaptura = captura.Dropped;
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

    /// <summary>Lo que manda el relay de vuelta. En la Fase 5 solo interesa
    /// responder al Ping y enterarse de que la sesion se cerro.</summary>
    private static async Task LeerAsync(
        AsyncDuplexStreamingCall<RemotePacket, RemotePacket> llamada, string sesionId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await llamada.ResponseStream.MoveNext(cancellationToken))
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
                            SessionId = sesionId,
                            Pong = new Pong { SentAtUs = paquete.Ping.SentAtUs }
                        }, cancellationToken);

                        break;

                    case RemotePacket.PayloadOneofCase.Close:
                        Console.WriteLine($"\nEl relay cerro la sesion: {paquete.Close.Reason} {paquete.Close.Detail}");
                        break;

                    case RemotePacket.PayloadOneofCase.Error:
                        Console.Error.WriteLine($"\nRelay: {paquete.Error.Code} {paquete.Error.Detail}");
                        break;

                    case RemotePacket.PayloadOneofCase.KeyframeRequest:
                        // La Fase 13 forzara un IDR con ICodecAPI. Aqui basta con
                        // el que el codificador genera por su cuenta.
                        Console.WriteLine("\nEl viewer pidio un keyframe.");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException)
        {
        }
    }

    internal static GrpcChannel Conectar(string servidor, bool permitirSinConfianza)
    {
        var opciones = new GrpcChannelOptions();

        if (permitirSinConfianza)
        {
            // SOLO para el checkpoint de la Fase 5, y hay que pedirlo a mano. El
            // servidor usa un certificado autofirmado que todavia no se puede
            // fijar desde aqui; la Fase 17 lo sustituye por el mismo pin de clave
            // publica que ya usa el agente.
            Console.Error.WriteLine("AVISO: no se valida el certificado del servidor (--allow-untrusted).");

            opciones.HttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }

        return GrpcChannel.ForAddress(servidor, opciones);
    }
}
