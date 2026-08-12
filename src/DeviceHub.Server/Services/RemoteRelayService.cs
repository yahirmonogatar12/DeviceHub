using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Remote;
using Grpc.Core;

namespace DeviceHub.Server.Services;

/// <summary>
/// El relay. Empareja host y viewer por session_id y reenvia bytes.
///
/// No descodifica, no recodifica, no captura, no renderiza y no escribe video en
/// disco. Solo mira dos cosas: el Hello, para saber quien es cada extremo, y el
/// SessionClose, para cerrar. Todo lo demas pasa sin abrirse.
///
/// LA FASE 6 NO ESTA AQUI. `Hello.ticket` viaja y se le comprueba el tamano,
/// pero no se valida ni se escribe en ningun log: es la credencial que da acceso
/// a ver y controlar una pantalla.
/// </summary>
public sealed class RemoteRelayGrpcService(RemoteSessionRegistry registro, ILogger<RemoteRelayGrpcService> log)
    : RemoteRelayService.RemoteRelayServiceBase
{
    public override Task HostChannel(
        IAsyncStreamReader<RemotePacket> entrada, IServerStreamWriter<RemotePacket> salida,
        ServerCallContext contexto)
        => CanalAsync(RemoteRole.Host, entrada, salida, contexto);

    public override Task ViewerChannel(
        IAsyncStreamReader<RemotePacket> entrada, IServerStreamWriter<RemotePacket> salida,
        ServerCallContext contexto)
        => CanalAsync(RemoteRole.Viewer, entrada, salida, contexto);

    private async Task CanalAsync(
        RemoteRole papel, IAsyncStreamReader<RemotePacket> entrada,
        IServerStreamWriter<RemotePacket> salida, ServerCallContext contexto)
    {
        var cancelacion = contexto.CancellationToken;

        // 1. El Hello tiene que ser lo primero. Un stream que empieza mandando
        //    video es un stream que no ha dicho a que sesion pertenece.
        if (!await entrada.MoveNext(cancelacion))
            return;

        var hola = entrada.Current;

        if (RevisarHola(hola, papel) is { } queja)
        {
            await RechazarAsync(salida, queja.Code, queja.Detalle, cancelacion);
            log.LogWarning("Relay: Hello rechazado ({Papel}): {Motivo}", papel, queja.Detalle);
            return;
        }

        var sesion = registro.GetOrCreate(hola.SessionId);
        using var conexion = new RelayConnection(hola.SessionId, papel);

        if (sesion.TryJoin(conexion) == JoinOutcome.RoleTaken)
        {
            // Se rechaza explicitamente en vez de desalojar al que estaba: si
            // sustituyera en silencio, cualquiera con el session_id echaria al
            // tecnico de su propia sesion.
            await RechazarAsync(
                salida, RemoteErrorCode.RoleAlreadyConnected,
                $"La sesion ya tiene un {papel}.", cancelacion);

            log.LogWarning("Relay: segundo {Papel} rechazado en la sesion {Sesion}", papel, hola.SessionId);
            return;
        }

        log.LogInformation(
            "Relay: {Papel} conectado a la sesion {Sesion} (estado {Estado})",
            papel, sesion.Id, sesion.State);

        var bomba = conexion.PumpAsync(new StreamWriter(salida), cancelacion);
        var motivo = SessionCloseReason.Normal;
        string? detalle = null;

        try
        {
            while (await entrada.MoveNext(cancelacion))
            {
                var paquete = entrada.Current;

                if (Revisar(paquete, papel, sesion.Id) is { } problema)
                {
                    motivo = SessionCloseReason.ProtocolError;
                    detalle = problema.Detalle;
                    break;
                }

                if (paquete.PayloadCase == RemotePacket.PayloadOneofCase.Close)
                {
                    motivo = paquete.Close.Reason;
                    detalle = Recortar(paquete.Close.Detail);
                }

                if (papel == RemoteRole.Host)
                    await sesion.FromHostAsync(paquete, cancelacion);
                else
                    await sesion.FromViewerAsync(paquete, cancelacion);

                if (paquete.PayloadCase == RemotePacket.PayloadOneofCase.Close)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            motivo = SessionCloseReason.Normal;
        }
        catch (RelayBackpressureException ex)
        {
            motivo = SessionCloseReason.Timeout;
            detalle = ex.Message;
        }
        catch (Exception ex)
        {
            motivo = SessionCloseReason.ProtocolError;
            detalle = ex.Message;
            log.LogWarning("Relay: {Papel} de la sesion {Sesion} termino con {Error}", papel, sesion.Id, ex.Message);
        }
        finally
        {
            if (motivo == SessionCloseReason.Normal)
                motivo = papel == RemoteRole.Host
                    ? SessionCloseReason.HostGone
                    : SessionCloseReason.ViewerGone;

            var otro = sesion.Leave(conexion, motivo);

            // Avisar al que queda es lo que evita sesiones huerfanas: sin esto,
            // el viewer se queda mirando una imagen congelada sin saber que la
            // PC controlada ya no esta.
            if (otro is not null)
            {
                try
                {
                    await otro.SendControlAsync(new RemotePacket
                    {
                        Close = new SessionClose { Reason = motivo, Detail = Recortar(detalle) ?? string.Empty }
                    }, CancellationToken.None);
                }
                catch (Exception)
                {
                    // El otro tambien se esta yendo. No cambia nada.
                }
            }

            conexion.Complete();

            try
            {
                await bomba;
            }
            catch (Exception)
            {
                // La bomba muere con el stream; el motivo real ya se registro.
            }

            registro.DropIfEmpty(sesion);

            log.LogInformation(
                "Relay: {Papel} salio de la sesion {Sesion} por {Motivo}. " +
                "frames recibidos {Recibidos}, reenviados {Reenviados}, tirados {Tirados}, " +
                "esperando IDR {Esperando}, cola maxima {Cola}, control maximo {Control}, bytes {Bytes}",
                papel, sesion.Id, motivo,
                sesion.FramesReceived, sesion.FramesForwarded,
                conexion.Video.FramesDropped, conexion.Video.DiscardedWaitingIdr,
                conexion.Video.HighWater, conexion.ControlHighWater, sesion.BytesForwarded);
        }
    }

    // -- Validacion ---------------------------------------------------------

    private readonly record struct Queja(RemoteErrorCode Code, string Detalle);

    private static Queja? RevisarHola(RemotePacket paquete, RemoteRole papel)
    {
        if (paquete.PayloadCase != RemotePacket.PayloadOneofCase.Hello)
            return new Queja(RemoteErrorCode.Unspecified, "El primer paquete tiene que ser Hello.");

        if (paquete.ProtocolVersion != RemoteSessionProtocol.Version)
            return new Queja(
                RemoteErrorCode.UnsupportedVersion,
                $"Protocolo {paquete.ProtocolVersion}; este servidor habla el {RemoteSessionProtocol.Version}.");

        if (string.IsNullOrWhiteSpace(paquete.SessionId))
            return new Queja(RemoteErrorCode.SessionNotFound, "Falta el session_id.");

        if (paquete.SessionId.Length > RemoteSessionProtocol.MaxSessionIdChars)
            return new Queja(RemoteErrorCode.PayloadTooLarge, "session_id demasiado largo.");

        if (paquete.Hello.Role != papel)
            return new Queja(
                RemoteErrorCode.Unspecified,
                $"Hello dice {paquete.Hello.Role} en el canal de {papel}.");

        // El contenido NO se valida todavia -- eso es la Fase 6 -- pero el
        // tamano si: es el primer mensaje de una conexion sin autenticar, y sin
        // tope permitiria mandar megabytes antes de que nadie mire quien es.
        if (paquete.Hello.Ticket.Length > RemoteSessionProtocol.MaxTicketChars)
            return new Queja(RemoteErrorCode.PayloadTooLarge, "Ticket demasiado largo.");

        return null;
    }

    /// <summary>
    /// Forma y DIRECCION. Cada tipo de mensaje solo tiene sentido en un sentido:
    /// un viewer que manda VideoChunk esta intentando inyectar imagen en la
    /// pantalla del tecnico, y un host que manda InputEvent esta intentando
    /// escribir en la PC del propio tecnico.
    /// </summary>
    private static Queja? Revisar(RemotePacket paquete, RemoteRole papel, string sesionId)
    {
        if (paquete.ProtocolVersion != 0 && paquete.ProtocolVersion != RemoteSessionProtocol.Version)
            return new Queja(RemoteErrorCode.UnsupportedVersion, $"Protocolo {paquete.ProtocolVersion}.");

        if (!string.IsNullOrEmpty(paquete.SessionId) && paquete.SessionId != sesionId)
            return new Queja(RemoteErrorCode.SessionNotFound, "session_id distinto del de este canal.");

        var permitido = paquete.PayloadCase switch
        {
            RemotePacket.PayloadOneofCase.VideoConfig or
            RemotePacket.PayloadOneofCase.VideoChunk or
            RemotePacket.PayloadOneofCase.Cursor => papel == RemoteRole.Host,

            RemotePacket.PayloadOneofCase.Input or
            RemotePacket.PayloadOneofCase.KeyframeRequest => papel == RemoteRole.Viewer,

            RemotePacket.PayloadOneofCase.Ping or
            RemotePacket.PayloadOneofCase.Pong or
            RemotePacket.PayloadOneofCase.Close or
            RemotePacket.PayloadOneofCase.Error => true,

            // Un segundo Hello, o un oneof vacio.
            _ => false
        };

        if (!permitido)
            return new Queja(
                RemoteErrorCode.Unspecified,
                $"Un {papel} no puede mandar {paquete.PayloadCase}.");

        if (paquete.PayloadCase == RemotePacket.PayloadOneofCase.VideoChunk)
        {
            var trozo = paquete.VideoChunk;

            if (trozo.Data.Length > RemoteSessionProtocol.MaxChunkBytes)
                return new Queja(
                    RemoteErrorCode.PayloadTooLarge,
                    $"Chunk de {trozo.Data.Length} bytes; el maximo son {RemoteSessionProtocol.MaxChunkBytes}.");

            if (trozo.ChunkCount == 0 || trozo.ChunkCount > RemoteSessionProtocol.MaxChunksPerFrame)
                return new Queja(RemoteErrorCode.PayloadTooLarge, $"chunk_count {trozo.ChunkCount}.");

            if (trozo.ChunkIndex >= trozo.ChunkCount)
                return new Queja(RemoteErrorCode.Unspecified, "chunk_index fuera de rango.");
        }

        return null;
    }

    private static string? Recortar(string? texto)
        => texto is null || texto.Length <= RemoteSessionProtocol.MaxDetailChars
            ? texto
            : texto[..RemoteSessionProtocol.MaxDetailChars];

    private static async Task RechazarAsync(
        IServerStreamWriter<RemotePacket> salida, RemoteErrorCode codigo, string detalle,
        CancellationToken cancellationToken)
    {
        try
        {
            await salida.WriteAsync(new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                Error = new RemoteError { Code = codigo, Detail = detalle }
            }, cancellationToken);

            await salida.WriteAsync(new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                Close = new SessionClose { Reason = SessionCloseReason.ProtocolError, Detail = detalle }
            }, cancellationToken);
        }
        catch (Exception)
        {
            // El cliente puede haberse ido ya.
        }
    }

    /// <summary>Adaptador del stream de gRPC a la bomba de envio.</summary>
    private sealed class StreamWriter(IServerStreamWriter<RemotePacket> salida) : IRemotePacketWriter
    {
        public Task WriteAsync(RemotePacket packet, CancellationToken cancellationToken)
            => salida.WriteAsync(packet, cancellationToken);
    }
}
