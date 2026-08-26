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
    DeviceHub.Server.Data.AuditRepository auditoria, ILogger<RemoteRelayGrpcService> log)
    : RemoteRelayService.RemoteRelayServiceBase
{
    /// <summary>
    /// Escribe en la auditoria sin poder tumbar la sesion. Fase 17.
    ///
    /// El relay NO esta en la transaccion de nadie: aqui no vale la regla de "si
    /// no se audita, no se ejecuta" que sigue el resto del servidor, porque lo
    /// que se registra ya ocurrio. Que la base de datos falle no puede cortarle
    /// la pantalla al tecnico, asi que se traga el error y se deja en el log.
    /// </summary>
    private async Task AuditarAsync(
        string accion, string maquina, string usuario, string detalle, string ip)
    {
        try
        {
            await auditoria.WriteAsync(new DeviceHub.Server.Data.AuditEntry(
                usuario, null, accion, maquina, null, null, null, ip,
                DeviceHub.Server.Data.AuditEntry.Allowed, detalle), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudo auditar {Accion} de la sesion remota", accion);
        }
    }

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
        var (autorizado, motivoAuth, credencial) = Comprobar(hola, papel);

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

        // EL try/finally EMPIEZA AQUI, no sesenta lineas mas abajo.
        //
        // Entre el TryJoin y el bucle hay una escritura de auditoria a MySQL y
        // dos escrituras de red. Cualquiera de ellas puede lanzar -- una
        // desconexion justo durante el HelloAccepted basta -- y hasta ahora eso
        // salia del metodo SIN pasar por Leave(): el papel quedaba ocupado por
        // una conexion muerta y el siguiente intento se llevaba un
        // RoleAlreadyConnected sin que hubiera nadie al otro lado.
        var motivo = SessionCloseReason.Normal;
        string? detalle = null;
        var cerroOrdenado = false;
        var usuario = credencial.UserId ?? string.Empty;
        Task? bomba = null;

        try
        {
        // EL LEASE SE ESTABLECE AQUI, con el sitio ya ocupado, y no antes.
        //
        // Antes se hacia dentro de la autenticacion, o sea antes del TryJoin, y
        // Establish PISA el TokenHash y la conexion activa del lease que ya
        // hubiera. Como se puede emitir mas de un ticket para la misma sesion y
        // el mismo papel, un segundo intento con un ticket valido rotaba el
        // lease del tecnico que estaba conectado, se llevaba un RoleTaken, y
        // dejaba al primero sin token con el que reconectar. Perdia su sesion
        // quien no habia hecho nada.
        var (token, lease) = leases.Establish(
            credencial.SessionId, papel, credencial.MachineId, credencial.UserId, conexion);

        var hasta = lease.ReconnectUntil;

        log.LogInformation(
            "Relay: {Papel} conectado a la sesion {Sesion} (estado {Estado})",
            papel, sesion.Id, sesion.State);

        // La linea que de verdad prueba que alguien vio esa pantalla. Las de
        // REQUESTED y STARTED solo dicen que se pidio permiso.
        await AuditarAsync(
            DeviceHub.Server.Data.AuditActions.RemoteConnected,
            hola.Hello.MachineId, usuario,
            $"sesion {sesion.Id} papel {papel} estado {sesion.State}", contexto.Peer);

        bomba = conexion.PumpAsync(new StreamWriter(salida), cancelacion);

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

        // Lo que la sesion ya sabia y este recien llegado no. Va DESPUES del
        // HelloAccepted y fuera de cualquier candado.
        await sesion.PonerAlDiaAsync(cancelacion);

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
        catch (IOException ex)
        {
            // UN CABLE QUE SE CAE NO ES UNA VIOLACION DE PROTOCOLO.
            //
            // Kestrel avisa de un stream roto con IOException -- "The client
            // reset the request stream", "The request stream was aborted" -- y
            // esto lo metia en el saco de ProtocolError. Ahi abajo, ProtocolError
            // REVOCA el lease y pone `esperaAlHost` en false: o sea que el unico
            // caso para el que existen los 30 s de gracia era justamente el que
            // los saltaba. El tecnico veia "ProtocolError" y la sesion cerrada.
            //
            // Protocolo roto es lo que devuelve Revisar(): un paquete mal
            // formado, un papel que no toca, otra sesion. Eso sigue revocando.
            motivo = SessionCloseReason.Normal;
            detalle = Recortar(ex.Message);
            log.LogWarning(
                "Relay: se corto el transporte del {Papel} de la sesion {Sesion}: {Error}",
                papel, sesion.Id, ex.Message);
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

            // FAILED solo cuando NO cerro en orden. Sin distinguirlo, en la
            // auditoria una sesion que se corto sola y una que el tecnico cerro
            // a mano son la misma linea, y la diferencia es justo la que se mira
            // cuando alguien pregunta que paso.
            await AuditarAsync(
                cerroOrdenado
                    ? DeviceHub.Server.Data.AuditActions.RemoteEnd
                    : DeviceHub.Server.Data.AuditActions.RemoteFailed,
                hola.Hello.MachineId, usuario,
                $"sesion {sesion.Id} papel {papel} motivo {motivo} {detalle}".TrimEnd(),
                contexto.Peer);

            // EL HOST QUE SE CAE NO ES EL HOST QUE CIERRA.
            //
            // Si cerro en orden o rompio el protocolo, su lease se acaba de
            // revocar ahi arriba y no va a volver: al tecnico se le cierra. Si
            // solo se le fue la red, tiene 30 s de gracia y todo un bucle de
            // reconexion para usarlos -- y hasta ahora volvia a una sesion de la
            // que ya habiamos echado al tecnico.
            var esperaAlHost = papel == RemoteRole.Host
                               && !cerroOrdenado
                               && motivo != SessionCloseReason.ProtocolError;

            var otro = sesion.Leave(conexion, motivo, esperaAlHost);

            if (esperaAlHost && sesion.State == RemoteSessionState.WaitingForHost)
            {
                await AvisarAsync(
                    sesion,
                    $"Se perdio la PC controlada. Esperandola {RemoteLeaseRegistry.Gracia.TotalSeconds:0} s...");

                // Sin await: aqui todavia se esta cerrando el stream del host, y
                // esto tiene que sobrevivirlo.
                _ = VigilarRegresoAsync(sesion);
            }

            // EL VIEWER QUE SE VA DEJA COSAS PULSADAS AL OTRO LADO.
            //
            // Si el proceso del tecnico murio despues de un KeyDown, con un
            // boton hundido o con la entrada bloqueada, su KeyUp y su
            // UnblockInput no van a llegar nunca. El rescate que hay al
            // reconectar no sirve para un cierre definitivo, y desde la PC de
            // planta nadie puede soltar una tecla que el host cree pulsada:
            // el host no ve el teclado fisico del tecnico, solo los eventos que
            // le llegaron.
            //
            // El relay es el ultimo que sabe que el viewer se fue, asi que es el
            // que tiene que decirlo. Va antes del vigilante para que llegue
            // aunque el host se cierre a continuacion.
            if (papel == RemoteRole.Viewer && otro is null && sesion.HostConectado is { } anfitrion)
                await SoltarLoQueQuedoPulsadoAsync(anfitrion, sesion);

            // Y EL ESPEJO DEL VIGILANTE, que solo existia en un sentido.
            //
            // Leave() deja al host en WaitingForViewer a proposito, para que el
            // tecnico pueda volver. Pero si no vuelve nunca, el host seguia
            // conectado capturando, codificando y mandando video para nadie, y
            // la sesion no quedaba vacia jamas. El lease tampoco lo salvaba: su
            // caducidad se mira cuando alguien intenta reconectar, y aqui nadie
            // lo intenta.
            if (papel == RemoteRole.Viewer && sesion.State == RemoteSessionState.WaitingForViewer)
                _ = VigilarViewerAsync(sesion, sesion.GeneracionSinViewer);

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
                if (bomba is not null)
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

    /// <summary>Quien dice ser el que llama, ya comprobado.</summary>
    /// <param name="UserId">
    /// QUIEN abrio la sesion, no contra que maquina.
    ///
    /// Salia de aqui y se tiraba, y la auditoria acababa registrando el
    /// machine_id en la columna del usuario: una fila que dice que la maquina se
    /// conecto a si misma. En un registro que existe para responder "quien vio
    /// esa pantalla", eso es peor que no tener la fila.
    ///
    /// Vacio en el host: la sesion la abre un tecnico, y el host es la PC
    /// controlada, que no es nadie.
    /// </param>
    private readonly record struct Credencial(string SessionId, string MachineId, string? UserId);

    /// <summary>
    /// Dos caminos, y solo dos: un ticket de arranque de un solo uso, o el token
    /// de reconexion que este mismo servidor emitio.
    ///
    /// `session_id` + `machine_id` NO bastan por si solos: los dos son
    /// adivinables, y aceptarlos convertiria la reconexion en una puerta sin
    /// llave.
    ///
    /// COMPRUEBA Y NO REPARTE NADA. El lease -- el derecho a volver -- se
    /// establece fuera, cuando ya se sabe que el sitio estaba libre. Consumir el
    /// ticket si es un efecto, y tiene que serlo: es de un solo uso, y un
    /// intento fallido lo gasta igual.
    /// </summary>
    private (bool Ok, string Motivo, Credencial Cred) Comprobar(RemotePacket paquete, RemoteRole papel)
    {
        var hola = paquete.Hello;

        if (!string.IsNullOrEmpty(hola.ReconnectToken))
        {
            var veredicto = leases.TryReconnect(hola.ReconnectToken, papel, paquete.SessionId, out var lease);

            if (veredicto != LeaseRejection.Accepted)
                return (false, veredicto.ToString(), default);

            // El token se rota en el Establish de mas arriba: el que acaba de
            // usarse deja de valer en cuanto el que vuelve tiene su sitio.
            return (true, "reconexion", new Credencial(lease!.SessionId, lease.MachineId, lease.UserId));
        }

        var rechazo = tickets.TryConsume(
            hola.Ticket, papel, paquete.SessionId, hola.MachineId, out var ticket);

        if (rechazo != TicketRejection.Accepted)
            return (false, rechazo.ToString(), default);

        // El ticket queda consumido para siempre. A partir de aqui la
        // reconexion la sostiene el lease: convertir el ticket de arranque en
        // algo reutilizable seria deshacer que sea de un solo uso.
        return (true, "ticket", new Credencial(ticket!.SessionId, ticket.TargetMachineId, ticket.UserId));
    }

    /// <summary>Un aviso de texto para el tecnico, por el mismo hueco que usa el
    /// host. No hay que inventar mensaje: el visor ya sabe enseñarlo.</summary>
    private static async Task AvisarAsync(RemoteSession sesion, string texto)
    {
        if (sesion.Viewer is not { } viewer)
            return;

        try
        {
            await viewer.SendControlAsync(
                new RemotePacket { HostStatus = new HostStatus { Text = texto } }, CancellationToken.None);
        }
        catch (Exception)
        {
            // El tecnico tambien se esta yendo. No cambia nada.
        }
    }

    /// <summary>
    /// Cierra al viewer si el host no volvio dentro de la gracia.
    ///
    /// La espera es la MISMA que la del lease a proposito: pasada esa, el host
    /// que vuelva necesita autorizacion nueva, asi que sostener al tecnico mas
    /// tiempo seria sostenerlo mirando una imagen congelada que ya no se va a
    /// mover nunca.
    /// </summary>
    /// <summary>
    /// Suelta teclas, botones y el bloqueo de entrada en la PC controlada.
    ///
    /// Los dos, y en este orden: una tecla hundida se nota y se arregla; una PC
    /// con la entrada bloqueada y sin nadie que la desbloquee hay que ir a
    /// reiniciarla.
    /// </summary>
    private async Task SoltarLoQueQuedoPulsadoAsync(RelayConnection host, RemoteSession sesion)
    {
        foreach (var accion in new[]
                 {
                     HostAction.Types.Kind.HostActionReleaseInput,
                     HostAction.Types.Kind.HostActionUnblockInput
                 })
        {
            try
            {
                await host.SendControlAsync(
                    new RemotePacket { HostAction = new HostAction { Kind = accion } },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    "Relay: no se pudo mandar {Accion} al host de la sesion {Sesion}: {Error}",
                    accion, sesion.Id, ex.Message);
            }
        }
    }

    /// <summary>
    /// El espejo de VigilarRegresoAsync: si el viewer no vuelve, se cierra al
    /// host en vez de dejarlo transmitiendo para nadie.
    /// </summary>
    private async Task VigilarViewerAsync(RemoteSession sesion, long generacion)
    {
        await Task.Delay(RemoteLeaseRegistry.Gracia);

        // La generacion con la que arranco: si el viewer volvio y se fue otra
        // vez, de esa se encarga el vigilante que nacio con ella.
        if (sesion.HostSiElViewerNoVolvio(SessionCloseReason.ViewerGone, generacion) is not { } host)
        {
            log.LogInformation("Relay: la sesion {Sesion} no necesito el cierre por viewer perdido", sesion.Id);
            return;
        }

        log.LogWarning("Relay: el viewer de la sesion {Sesion} no volvio; se cierra al host", sesion.Id);

        try
        {
            await host.SendControlAsync(new RemotePacket
            {
                Close = new SessionClose
                {
                    Reason = SessionCloseReason.ViewerGone,
                    Detail = "El tecnico no volvio a conectarse."
                }
            }, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private async Task VigilarRegresoAsync(RemoteSession sesion)
    {
        await Task.Delay(RemoteLeaseRegistry.Gracia);

        if (sesion.ViewerSiElHostNoVolvio(SessionCloseReason.HostGone) is not { } viewer)
        {
            log.LogInformation("Relay: la sesion {Sesion} no necesito el cierre por host perdido", sesion.Id);
            return;
        }

        log.LogWarning("Relay: el host de la sesion {Sesion} no volvio; se cierra al viewer", sesion.Id);

        try
        {
            await viewer.SendControlAsync(new RemotePacket
            {
                Close = new SessionClose
                {
                    Reason = SessionCloseReason.HostGone,
                    Detail = "La PC controlada no volvio a conectarse."
                }
            }, CancellationToken.None);
        }
        catch (Exception)
        {
        }
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
            RemotePacket.PayloadOneofCase.HostStatus or
            RemotePacket.PayloadOneofCase.FileList or

            // El sonido de la PC controlada. Solo del host: un viewer que
            // mandara audio estaria hablandole a la PC de planta, y eso no es
            // esta fase ni esta pedido.
            RemotePacket.PayloadOneofCase.AudioConfig or
            RemotePacket.PayloadOneofCase.AudioChunk => papel == RemoteRole.Host,

            RemotePacket.PayloadOneofCase.Input or
            RemotePacket.PayloadOneofCase.HostAction or
            RemotePacket.PayloadOneofCase.SelectDisplay or
            RemotePacket.PayloadOneofCase.SelectCodec or
            RemotePacket.PayloadOneofCase.SelectQuality or
            RemotePacket.PayloadOneofCase.FileListRequest or
            RemotePacket.PayloadOneofCase.FileDownload or
            RemotePacket.PayloadOneofCase.KeyframeRequest or
            RemotePacket.PayloadOneofCase.SelectAudio or

            // La segunda mitad de arrastrar y soltar: el visor pide el Ctrl+V en
            // un punto de la pantalla de alla. Solo en ese sentido -- un host
            // que lo mandara estaria pidiendo teclas en la PC del tecnico.
            RemotePacket.PayloadOneofCase.PasteAt or

            // Anadir o quitar el monitor virtual: instala y activa un driver en
            // la PC de planta. Solo del visor, y por la misma razon que todo lo
            // demas de este bloque -- al reves seria el host tocando el hardware
            // del tecnico.
            RemotePacket.PayloadOneofCase.VirtualDisplay or

            // El acuse de frame. Va del visor al host y es lo que impide que el
            // host se adelante: sin reenviarlo, el freno no existe.
            RemotePacket.PayloadOneofCase.VideoAck => papel == RemoteRole.Viewer,

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
