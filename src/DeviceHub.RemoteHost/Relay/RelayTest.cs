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

    private static async Task<int> EmitirAsync(
        AsyncDuplexStreamingCall<RemotePacket, RemotePacket> llamada,
        string sesionId, int adapterIndex, int outputIndex, int seconds, int fps, int bitrate,
        CancellationToken cancellationToken)
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

        long capturados = 0, codificados = 0, enviados = 0, trozos = 0, claves = 0, bytes = 0;
        uint configVersion = 0;

        var reloj = Stopwatch.StartNew();
        var siguienteAviso = TimeSpan.FromSeconds(2);
        var duracion = TimeSpan.FromSeconds(seconds);

        while (reloj.Elapsed < duracion && !cancellationToken.IsCancellationRequested)
        {
            using var frame = await captura.CaptureAsync(cancellationToken);

            if (frame is null || !frame.DesktopChanged)
                continue;

            capturados++;

            foreach (var salida in codificador.Encode(frame, cancellationToken))
            {
                codificados++;

                if (salida.IsKeyFrame)
                    claves++;

                // El SPS/PPS sale dentro del primer IDR. Se saca UNA vez, se
                // manda en VideoConfig y a partir de ahi el viewer lo conserva:
                // reenviarlo con cada keyframe es ancho de banda que no aporta
                // nada a quien ya lo tiene.
                if (configVersion == 0 && salida.IsKeyFrame)
                {
                    var parametros = H264AnnexB.ParameterSets(salida.Payload);

                    if (parametros.Length == 0)
                        continue;   // todavia no; el siguiente IDR los traera

                    configVersion = 1;

                    await EscribirAsync(llamada, new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = sesionId,
                        VideoConfig = new VideoConfig
                        {
                            ConfigVersion = configVersion,
                            Codec = VideoCodec.H264,
                            Width = (uint)salida.Width,
                            Height = (uint)salida.Height,
                            FramesPerSecond = (uint)fps,
                            BitrateBitsPerSecond = (uint)bitrate,
                            ParameterSets = Google.Protobuf.ByteString.CopyFrom(parametros),
                            VisibleWidth = (uint)salida.Width,
                            VisibleHeight = (uint)salida.Height
                        }
                    }, cancellationToken);
                }

                if (configVersion == 0)
                    continue;   // sin configuracion no hay nada descodificable que mandar

                var grupo = VideoFraming.Split(
                    salida.Sequence, salida.IsKeyFrame, configVersion, salida.TimestampUs, salida.Payload);

                foreach (var trozo in grupo.Chunks)
                {
                    await EscribirAsync(llamada, new RemotePacket
                    {
                        ProtocolVersion = RemoteSessionProtocol.Version,
                        SessionId = sesionId,
                        VideoChunk = trozo
                    }, cancellationToken);

                    trozos++;
                }

                enviados++;
                bytes += grupo.PayloadBytes;
            }

            if (reloj.Elapsed >= siguienteAviso)
            {
                Console.WriteLine(
                    $"{reloj.Elapsed:mm\\:ss}  capturados {capturados}  codificados {codificados}  " +
                    $"frames enviados {enviados}  chunks {trozos}  " +
                    $"{bytes * 8 / reloj.Elapsed.TotalSeconds / 1_000_000:0.00} Mbps  " +
                    $"keyframes {claves}  config {configVersion}");

                siguienteAviso += TimeSpan.FromSeconds(2);
            }
        }

        var segundos = Math.Max(reloj.Elapsed.TotalSeconds, 0.001);

        Console.WriteLine();
        Console.WriteLine($"Captured:      {capturados}");
        Console.WriteLine($"Encoded:       {codificados}");
        Console.WriteLine($"Frames sent:   {enviados}");
        Console.WriteLine($"Chunks sent:   {trozos}");
        Console.WriteLine($"Mbps:          {bytes * 8 / segundos / 1_000_000:0.00}");
        Console.WriteLine($"Keyframes:     {claves}");
        Console.WriteLine($"Config:        v{configVersion}");
        Console.WriteLine($"Encode drops:  {codificador.Dropped}  (captura {captura.Dropped})");

        return enviados > 0 ? 0 : 5;
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
