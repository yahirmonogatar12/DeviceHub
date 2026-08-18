using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Remote;
using Grpc.Core;

namespace DeviceHub.Server.Services;

/// <summary>
/// El relay. Empareja host y viewer por session_id y reenvia bytes.
///
/// No descodifica, no recodifica, no captura, no renderiza y no escribe video en
/// disco, y no toca el SPS/PPS.
///
/// Lo que SI hace con todos los paquetes es mirarles la estructura: limites de
/// tamano, direccion permitida segun quien los manda, y agrupar los chunks de un
/// frame para poder descartarlo entero. Eso no es abrir el contenido, pero
/// tampoco es reenviar a ciegas, y conviene no describirlo como tal.
///
/// Del Hello y del SessionClose ademas lee los campos: son los que deciden a que
/// sesion pertenece cada extremo y cuando termina.
///
/// AUTENTICACION (Fase 6). Ningun stream pasa del Hello sin presentar o un
/// ticket de un solo uso o el token de reconexion que este servidor emitio. Ni
/// el ticket ni el token se escriben jamas en un log: son la credencial que da
/// acceso a ver y controlar una pantalla.
/// </summary>
public sealed class RemoteRelayGrpcService(
    RemoteSessionRegistry registro, RemoteTicketRegistry tickets, RemoteLeaseRegistry leases,
    ILogger<RemoteRelayGrpcService> log)
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

        using var conexion = new RelayConnection(hola.SessionId, papel);

        // AUTENTICACION. O un ticket de un solo uso, o el token de reconexion que
        // este servidor emitio antes. Nada mas entra.
        var (autorizado, motivoAuth, token, hasta) = Autenticar(hola, papel, conexion);

        if (!autorizado)
        {
            // Al cliente siempre el mismo codigo: distinguir "expirado" de "no
            // existe" le dice a quien prueba tickets si va por buen camino. El
            // detalle se queda en el log del servidor, y NUNCA el secreto.
            await RechazarAsync(
                salida, RemoteErrorCode.InvalidTicket, "Credencial no valida para esta sesion.", cancelacion);

            log.LogWarning(
                "Relay: {Papel} rechazado en la sesion {Sesion} por {Motivo}",
                papel, hola.SessionId, motivoAuth);

            return;
        }

        var sesion = registro.GetOrCreate(hola.SessionId);

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

        // El token de reconexion sale por AQUI y por ningun otro sitio.
        await conexion.SendControlAsync(new RemotePacket
        {
            HelloAccepted = new HelloAccepted
            {
                State = (RemoteSessionStateProto)(int)sesion.State,
                ReconnectToken = token,
                ReconnectUntilUs = hasta.ToUnixTimeMilliseconds() * 1000
            }
        }, cancelacion);

        var motivo = SessionCloseReason.Normal;
        string? detalle = null;
        var cerroOrdenado = false;

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
                    cerroOrdenado = true;
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

            // Un cierre ORDENADO mata el lease; una caida lo deja en gracia.
            // Mantenerlo tras un SessionClose dejaria una sesion terminada
            // reabrible durante 30 s sin autorizacion nueva.
            //
            // Se mira `cerroOrdenado` y no `motivo`, porque justo arriba Normal
            // se convierte en HostGone/ViewerGone y la comparacion no volveria a
            // ser cierta nunca.
            if (cerroOrdenado || motivo == SessionCloseReason.ProtocolError)
                leases.Revoke(sesion.Id, papel);
            else
                leases.Detach(sesion.Id, papel, conexion);

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

    // -- Autenticacion ------------------------------------------------------

    /// <summary>
    /// Dos caminos, y solo dos: un ticket de arranque de un solo uso, o el token
    /// de reconexion que este mismo servidor emitio.
    ///
    /// `session_id` + `machine_id` NO bastan por si solos: los dos son
    /// adivinables, y aceptarlos convertiria la reconexion en una puerta sin
    /// llave.
    /// </summary>
    private (bool Ok, string Motivo, string Token, DateTimeOffset Hasta) Autenticar(
        RemotePacket paquete, RemoteRole papel, RelayConnection conexion)
    {
        var hola = paquete.Hello;

        if (!string.IsNullOrEmpty(hola.ReconnectToken))
        {
            var veredicto = leases.TryReconnect(hola.ReconnectToken, papel, paquete.SessionId, out var lease);

            if (veredicto != LeaseRejection.Accepted)
                return (false, veredicto.ToString(), string.Empty, default);

            // Se ROTA: el token que acaba de usarse deja de valer aqui mismo.
            var (nuevo, renovado) = leases.Establish(
                lease!.SessionId, papel, lease.MachineId, lease.UserId, conexion);

            return (true, "reconexion", nuevo, renovado.ReconnectUntil);
        }

        var rechazo = tickets.TryConsume(
            hola.Ticket, papel, paquete.SessionId, hola.MachineId, out var ticket);

        if (rechazo != TicketRejection.Accepted)
            return (false, rechazo.ToString(), string.Empty, default);

        // El ticket queda consumido para siempre. A partir de aqui la
        // reconexion la sostiene el lease: convertir el ticket de arranque en
        // algo reutilizable seria deshacer que sea de un solo uso.
        var (emitido, primero) = leases.Establish(
            ticket!.SessionId, papel, ticket.TargetMachineId, ticket.UserId, conexion);

        return (true, "ticket", emitido, primero.ReconnectUntil);
    }

    // -- Validacion ---------------------------------------------------------

    public readonly record struct Queja(RemoteErrorCode Code, string Detalle);

    public static Queja? RevisarHola(RemotePacket paquete, RemoteRole papel)
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

        // El tamano se comprueba antes que nada: es el primer mensaje de una
        // conexion sin autenticar, y sin tope permitiria mandar megabytes antes
        // de que nadie mire quien es.
        if (paquete.Hello.Ticket.Length > RemoteSessionProtocol.MaxTicketChars ||
            paquete.Hello.ReconnectToken.Length > RemoteSessionProtocol.MaxTicketChars)
            return new Queja(RemoteErrorCode.PayloadTooLarge, "Credencial demasiado larga.");

        // EXCLUSIVIDAD MUTUA. Un Hello es de arranque o de reconexion, nunca las
        // dos cosas:
        //
        //   arranque    ticket != vacio   reconnect_token == vacio
        //   reconexion  ticket == vacio   reconnect_token != vacio
        //
        // Aceptar los dos obligaria a decidir cual gana, y esa decision es
        // exactamente el hueco por el que se cuela un ticket caducado detras de
        // un token valido, o al reves. Sin ninguno, no hay nada que validar.
        var conTicket = !string.IsNullOrEmpty(paquete.Hello.Ticket);
        var conToken = !string.IsNullOrEmpty(paquete.Hello.ReconnectToken);

        if (conTicket == conToken)
            return new Queja(
                RemoteErrorCode.InvalidTicket,
                conTicket
                    ? "Hello con ticket y reconnect_token a la vez."
                    : "Hello sin credencial.");

        return null;
    }

    /// <summary>
    /// Forma y DIRECCION. Cada tipo de mensaje solo tiene sentido en un sentido:
    /// un viewer que manda VideoChunk esta intentando inyectar imagen en la
    /// pantalla del tecnico, y un host que manda InputEvent esta intentando
    /// escribir en la PC del propio tecnico.
    /// </summary>
    /// <summary>Publico por la misma razon que RevisarHola: es una funcion pura y
    /// es la frontera que decide que se reenvia, asi que se prueba directa.</summary>
    public static Queja? Revisar(RemotePacket paquete, RemoteRole papel, string sesionId)
    {
        if (paquete.ProtocolVersion != 0 && paquete.ProtocolVersion != RemoteSessionProtocol.Version)
            return new Queja(RemoteErrorCode.UnsupportedVersion, $"Protocolo {paquete.ProtocolVersion}.");

        if (!string.IsNullOrEmpty(paquete.SessionId) && paquete.SessionId != sesionId)
            return new Queja(RemoteErrorCode.SessionNotFound, "session_id distinto del de este canal.");

        var permitido = paquete.PayloadCase switch
        {
            RemotePacket.PayloadOneofCase.VideoConfig or
            RemotePacket.PayloadOneofCase.VideoChunk or
            RemotePacket.PayloadOneofCase.Cursor or
            RemotePacket.PayloadOneofCase.Displays or
            RemotePacket.PayloadOneofCase.FileList => papel == RemoteRole.Host,

            RemotePacket.PayloadOneofCase.Input or
            RemotePacket.PayloadOneofCase.HostAction or
            RemotePacket.PayloadOneofCase.SelectDisplay or
            RemotePacket.PayloadOneofCase.FileListRequest or
            RemotePacket.PayloadOneofCase.FileDownload or
            RemotePacket.PayloadOneofCase.KeyframeRequest => papel == RemoteRole.Viewer,

            RemotePacket.PayloadOneofCase.Ping or
            RemotePacket.PayloadOneofCase.Pong or
            RemotePacket.PayloadOneofCase.Close or
            RemotePacket.PayloadOneofCase.Error or

            // El portapapeles va en los dos sentidos: se copia aqui y se pega
            // alla, y al reves.
            //
            // Los trozos de archivo tambien: host -> viewer es una descarga y
            // viewer -> host una subida. El acuse siempre va del que recibe.
            RemotePacket.PayloadOneofCase.Clipboard or
            RemotePacket.PayloadOneofCase.ClipboardFiles or
            RemotePacket.PayloadOneofCase.FileChunk or
            RemotePacket.PayloadOneofCase.FileAck => true,

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

        // Un trozo de archivo no puede pasar del tope de chunk. No hay limite de
        // TAMANO DE ARCHIVO -- para eso esta la fase -- pero si de mensaje: el
        // troceado es cosa del emisor, y uno que mande 40 MB de golpe esta
        // pidiendole al receptor que reserve 40 MB porque si.
        if (paquete.PayloadCase == RemotePacket.PayloadOneofCase.FileChunk
            && paquete.FileChunk.Data.Length > RemoteSessionProtocol.MaxChunkBytes)
        {
            return new Queja(
                RemoteErrorCode.PayloadTooLarge,
                $"Trozo de archivo de {paquete.FileChunk.Data.Length} bytes; " +
                $"el maximo son {RemoteSessionProtocol.MaxChunkBytes}.");
        }

        // El portapapeles se sincroniza SOLO, sin que nadie lo pida. Sin tope,
        // copiar un log de 40 MB en cualquiera de los dos lados lo mandaria por
        // la red entero y sin avisar.
        if (paquete.PayloadCase == RemotePacket.PayloadOneofCase.Clipboard
            && paquete.Clipboard.Text.Length > RemoteSessionProtocol.MaxClipboardChars)
        {
            return new Queja(
                RemoteErrorCode.PayloadTooLarge,
                $"Portapapeles de {paquete.Clipboard.Text.Length} caracteres; " +
                $"el maximo son {RemoteSessionProtocol.MaxClipboardChars}.");
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
