using System.Threading.Channels;
using DeviceHub.Agent.Commands;
using DeviceHub.Agent.Identity;
using DeviceHub.Agent.Inventory;
using DeviceHub.Agent.Monitoring;
using DeviceHub.Agent.Network;
using DeviceHub.Agent.Remote;
using DeviceHub.Agent.Security;
using DeviceHub.Agent.Updater;
using DeviceHub.Contracts;
using DeviceHub.Remote.Contracts;
using Grpc.Core;
using Grpc.Net.Client;

namespace DeviceHub.Agent;

public sealed class Worker(
    IOptions<AgentOptions> options,
    MachineIdentity identityStore,
    PinnedChannelFactory channelFactory,
    CommandRunner runner,
    IRemoteAgentDetector remoteDetector,
    InteractiveSessionLauncher hostLauncher,
    UpdateService updates,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly string AgentVersion =
        typeof(Worker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly AgentOptions _options = options.Value;
    private MachineIdentityFile _identity = new();
    private MetricStore _metrics = null!;
    private CommandJournal _commands = null!;

    /// <summary>Se cachea: detectar lanza un proceso, y el motor remoto no se
    /// instala ni desinstala cada 30 segundos.</summary>
    private RemoteAgentInfo _remote = new();
    private DateTime _nextRemoteScan = DateTime.MinValue;
    private DateTime _nextUpdateCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _identity = identityStore.Load();

        // El instalador puede traer el pin del servidor. Solo siembra: una vez que
        // la maquina tiene los suyos, machine.json manda -- si no, una rotacion
        // quedaria revertida en cada reinicio del servicio.
        if (_identity.PinnedKeys.Count == 0 && _options.PinnedKeys.Count > 0)
        {
            _identity.PinnedKeys = [.. _options.PinnedKeys];
            identityStore.Save(_identity);
            logger.LogInformation("Pines sembrados desde la configuracion del instalador: {Count}", _identity.PinnedKeys.Count);
        }

        channelFactory.SetPins(_identity.PinnedKeys);

        logger.LogInformation("DeviceHub Agent {Version} | machineId {MachineId} | servidor {Address}",
            AgentVersion, _identity.MachineId, _options.ServerAddress);

        using var metrics = new MetricStore(_options.DataDirectory);
        _metrics = metrics;

        using var journal = new CommandJournal(_options.DataDirectory);
        _commands = journal;

        // El muestreo corre al margen de la conexion: precisamente los minutos en
        // que el servidor esta caido son los que hay que conservar.
        var sampling = SampleLoopAsync(stoppingToken);

        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var channel = channelFactory.Create(_options.ServerAddress);
                await EnsureRegisteredAsync(channel, stoppingToken);
                await RunSessionAsync(channel, stoppingToken);

                backoff = TimeSpan.FromSeconds(1); // sesion sana: se resetea el backoff
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                // El servidor no reconoce el token. Pasa cuando un administrador
                // emite identidad nueva desde el dashboard: la fila se queda con
                // token_hash NULL y esta PC no volvia sola NUNCA, porque
                // EnsureRegisteredAsync se salta el registro en cuanto hay un
                // token guardado -- aunque sea uno muerto. La unica salida era ir
                // fisicamente a la maquina a editar machine.json.
                var espera = DescartarToken($"El servidor rechazo el token ({ex.Status.Detail})")
                    ? Jitter(TimeSpan.FromSeconds(5))
                    : TimeSpan.FromMinutes(5);

                await SafeDelay(espera, stoppingToken);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                // Registro rechazado: sin codigo valido y sin permiso vivo. Se
                // reintenta cada minuto -- ni mas, porque solo lo arregla una
                // persona, ni menos, porque en cuanto esa persona pulse el boton
                // del dashboard esta PC tiene que volver sin que nadie la toque.
                logger.LogError(
                    "Registro rechazado: {Detail}. Autoriza la reasociacion de {MachineId} en el dashboard",
                    ex.Status.Detail, _identity.MachineId);

                await SafeDelay(Jitter(TimeSpan.FromMinutes(1)), stoppingToken);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
            {
                // Conflicto de identidad: lo resuelve un administrador, no el reintento.
                // Machacar al servidor cada segundo no arregla nada.
                logger.LogError("El servidor rechazo esta identidad: {Detail}. Requiere resolucion manual.", ex.Status.Detail);
                await SafeDelay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Sesion interrumpida ({Message}); reintento en {Seconds:F0} s",
                    ex.Message, backoff.TotalSeconds);
                await SafeDelay(Jitter(backoff), stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }

        await sampling;
    }

    /// <summary>
    /// Muestrea cada 5 s y vuelca un agregado por minuto al buffer local. No
    /// depende del stream: si el servidor esta caido, los minutos se acumulan y
    /// se drenan al reconectar.
    /// </summary>
    private async Task SampleLoopAsync(CancellationToken ct)
    {
        var sampler = new SystemSampler();
        var buffer = new List<RawSample>();
        var currentMinute = MetricAggregation.MinuteOf(DateTime.UtcNow);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MetricAggregation.SampleInterval, ct);

                var minute = MetricAggregation.MinuteOf(DateTime.UtcNow);

                if (minute != currentMinute && buffer.Count > 0)
                {
                    _metrics.Append(MetricAggregation.Aggregate(currentMinute, buffer));
                    buffer.Clear();
                }

                currentMinute = minute;
                buffer.Add(sampler.Sample());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // El monitoreo nunca debe tumbar al agente: si falla una muestra,
                // la maquina tiene que seguir apareciendo ONLINE.
                logger.LogWarning(ex, "Fallo al muestrear metricas");
            }
        }
    }

    /// <summary>
    /// Tira el token guardado para que el siguiente intento vuelva a registrarse.
    /// Devuelve si merece la pena reintentar pronto.
    ///
    /// EL machineId NO SE TOCA. Es lo que ata esta PC a su historial, a su
    /// ubicacion y al recovery code que el administrador emitio apuntando a ella.
    /// El arreglo manual que se venia haciendo -- borrar machine.json entero --
    /// genera un GUID nuevo, y entonces el recovery code sale rechazado y la
    /// maquina vuelve como duplicada.
    ///
    /// Esto no debilita nada: registrarse sigue exigiendo un codigo valido y sin
    /// consumir. Un agente sin token no entra a ningun sitio, asi que descartarlo
    /// no le da acceso a nada que no tuviera.
    /// </summary>
    private bool DescartarToken(string motivo)
    {
        if (string.IsNullOrWhiteSpace(_identity.ProtectedToken))
            return false;   // ya se descarto; lo que falla ahora es el registro

        logger.LogWarning(
            "{Motivo}. Se descarta y se reintenta el registro. Si no hay codigo configurado, hace falta " +
            "autorizar la reasociacion de {MachineId} desde el dashboard", motivo, _identity.MachineId);

        _identity.ProtectedToken = null;
        identityStore.Save(_identity);
        return true;
    }

    private async Task EnsureRegisteredAsync(GrpcChannel channel, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_identity.ProtectedToken))
            return;

        // Sin codigo se intenta IGUAL, con el campo vacio. El servidor lo acepta
        // solo si un administrador autorizo la reasociacion de esta maquina hace
        // pocos minutos; si no, contesta PermissionDenied y se reintenta luego.
        //
        // Esa es la diferencia entre una PC que vuelve sola en cuanto alguien
        // pulsa un boton y una a la que hay que ir fisicamente.
        var fingerprint = Fingerprint.Collect();
        var client = new AgentService.AgentServiceClient(channel);

        var reply = await client.RegisterAsync(new RegisterRequest
        {
            EnrollmentCode = _options.EnrollmentCode,
            MachineId = _identity.MachineId,
            Hostname = Environment.MachineName,
            AgentVersion = AgentVersion,
            Fingerprint = fingerprint
        }, cancellationToken: ct);

        _identity.ProtectedToken = MachineIdentity.Protect(reply.Token);
        _identity.MachineCode = reply.MachineCode;
        _identity.HardwareFingerprint = fingerprint.Hash;
        _identity.PinnedKeys = reply.PinnedKeys.Count > 0
            ? [.. reply.PinnedKeys]
            : [.. new[] { channelFactory.ObservedPin }.Where(p => p is not null).Cast<string>()];

        identityStore.Save(_identity);
        channelFactory.SetPins(_identity.PinnedKeys);

        logger.LogInformation("Registrado como {MachineCode}", reply.MachineCode);
    }

    private async Task RunSessionAsync(GrpcChannel channel, CancellationToken stoppingToken)
    {
        var token = MachineIdentity.Unprotect(_identity.ProtectedToken);

        if (token is null)
        {
            // DPAPI con ambito LocalMachine no descifra lo que cifro otra
            // instalacion de Windows: pasa al restaurar una imagen o al
            // reinstalar el sistema conservando ProgramData. El token es
            // irrecuperable, y guardarlo solo sirve para no volver nunca.
            DescartarToken("El token guardado no se puede descifrar (DPAPI)");

            throw new InvalidOperationException(
                "Token ilegible (DPAPI). Hace falta un recovery code emitido por un administrador.");
        }

        var client = new AgentService.AgentServiceClient(channel);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {token}" },
            { "x-machine-id", _identity.MachineId }
        };

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var call = client.Connect(headers, cancellationToken: session.Token);

        logger.LogInformation("Stream abierto contra {Address}", _options.ServerAddress);

        // Marca de salud para el actualizador: se escribe al CONECTAR, no al
        // arrancar. Un agente que arranca pero no llega al servidor esta tan roto
        // como uno que no arranca, y el rollback tiene que dispararse igual.
        updates.WriteHealthMarker(System.Version.Parse(AgentVersion));

        // Cola de salida por sesion. Los streams gRPC de cliente no admiten
        // escrituras concurrentes, asi que hay UN solo escritor y todo lo demas
        // encola. Antes el heartbeat escribia directo y no habia hueco para meter
        // resultados de comando sin esperar hasta 30 s -- inaceptable para algo
        // que un tecnico pidio y esta mirando.
        var outbound = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });

        var writer = WriteLoopAsync(call.RequestStream, outbound.Reader, session.Token);
        var producer = ProduceLoopAsync(outbound.Writer, session.Token);
        var reader = ReadServerMessagesAsync(call.ResponseStream, outbound.Writer, session.Token);

        // El primero que termine (o falle) cierra la sesion; el bucle exterior reconecta.
        var finished = await Task.WhenAny(writer, producer, reader);
        await session.CancelAsync();
        await finished; // propaga la excepcion real, no una de cancelacion
    }

    /// <summary>Unico escritor del stream.</summary>
    private static async Task WriteLoopAsync(
        IClientStreamWriter<AgentMessage> stream, ChannelReader<AgentMessage> outbound, CancellationToken ct)
    {
        await foreach (var message in outbound.ReadAllAsync(ct))
            await stream.WriteAsync(message, ct);
    }

    /// <summary>Encola lo periodico: heartbeat, inventario y metricas.</summary>
    private async Task ProduceLoopAsync(ChannelWriter<AgentMessage> outbound, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.HeartbeatSeconds);
        var nextInventoryScan = DateTime.UtcNow; // al conectar, de inmediato
        var nextMetricsFlush = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            await outbound.WriteAsync(new AgentMessage { Heartbeat = BuildHeartbeat() }, ct);

            if (DateTime.UtcNow >= nextInventoryScan)
            {
                await MaybeSendInventoryAsync(outbound, ct);
                nextInventoryScan = DateTime.UtcNow.Add(InventoryCadence.ScanInterval);
            }

            if (DateTime.UtcNow >= nextMetricsFlush)
            {
                await FlushMetricsAsync(outbound, ct);
                nextMetricsFlush = DateTime.UtcNow.AddMinutes(1);
            }

            if (DateTime.UtcNow >= _nextUpdateCheck)
            {
                _nextUpdateCheck = DateTime.UtcNow.AddHours(_options.UpdateCheckHours);

                // Si encuentra algo, el actualizador detendra este servicio en
                // segundos: no hace falta hacer nada mas aqui.
                updates.TryCheckAndStage(System.Version.Parse(AgentVersion));
            }

            await Task.Delay(interval, ct);
        }
    }

    private async Task FlushMetricsAsync(ChannelWriter<AgentMessage> outbound, CancellationToken ct)
    {
        var pending = _metrics.Take(500);

        if (pending.Count == 0)
            return;

        var batch = new MetricsBatch();
        batch.Samples.AddRange(pending);

        await outbound.WriteAsync(new AgentMessage { Metrics = batch }, ct);

        // ponytail: se borra tras escribir, sin esperar confirmacion del servidor.
        // Si la conexion cae justo despues se pierde ese lote -- aceptable para
        // metricas. El servidor hace upsert por (machine_id, minute), asi que el
        // dia que haga falta se puede pasar a at-least-once sin duplicar nada.
        _metrics.Remove(pending);

        if (pending.Count > 1)
            logger.LogInformation("Metricas enviadas: {Count} minutos acumulados", pending.Count);
    }

    private async Task MaybeSendInventoryAsync(ChannelWriter<AgentMessage> outbound, CancellationToken ct)
    {
        var inventory = HardwareCollector.Collect();

        if (!InventoryCadence.ShouldSend(
                inventory.Hash, _identity.LastInventoryHash, _identity.LastInventoryUtc, DateTime.UtcNow))
        {
            return;
        }

        var changed = _identity.LastInventoryHash is not null && _identity.LastInventoryHash != inventory.Hash;

        await outbound.WriteAsync(new AgentMessage { Hardware = inventory }, ct);

        _identity.LastInventoryHash = inventory.Hash;
        _identity.LastInventoryUtc = DateTime.UtcNow;
        identityStore.Save(_identity);

        logger.LogInformation(changed
            ? "Inventario enviado: el hardware cambio"
            : "Inventario enviado");
    }

    private async Task ReadServerMessagesAsync(
        IAsyncStreamReader<ServerMessage> stream, ChannelWriter<AgentMessage> outbound, CancellationToken ct)
    {
        await foreach (var message in stream.ReadAllAsync(ct))
        {
            if (message.PayloadCase == ServerMessage.PayloadOneofCase.Command)
            {
                await HandleCommandAsync(message.Command, outbound, ct);
                continue;
            }

            if (message.PayloadCase == ServerMessage.PayloadOneofCase.RemoteHost)
            {
                await HandleRemoteHostAsync(message.RemoteHost, ct);
                continue;
            }

            if (message.PayloadCase != ServerMessage.PayloadOneofCase.Config)
                continue;

            var config = message.Config;
            var changed = false;

            // El machine_code es autoritativo del servidor: renombrar desde el
            // dashboard no exige tocar la PC.
            if (!string.IsNullOrWhiteSpace(config.MachineCode) && config.MachineCode != _identity.MachineCode)
            {
                logger.LogInformation("Renombrada: {Old} -> {New}", _identity.MachineCode, config.MachineCode);
                _identity.MachineCode = config.MachineCode;
                changed = true;
            }

            // Paso 1 de la rotacion de certificado: cargar el pin nuevo sin
            // soltar el viejo. El servidor solo cambia de cert cuando todos lo
            // confirman por heartbeat.
            if (config.PinnedKeys.Count > 0 && !config.PinnedKeys.ToHashSet().SetEquals(_identity.PinnedKeys))
            {
                _identity.PinnedKeys = [.. config.PinnedKeys];
                channelFactory.SetPins(_identity.PinnedKeys);
                logger.LogInformation("Pines actualizados: {Count} aceptados", _identity.PinnedKeys.Count);
                changed = true;
            }

            if (changed)
                identityStore.Save(_identity);
        }
    }

    /// <summary>
    /// Mitad del agente de la idempotencia.
    ///
    /// Si la conexion cae despues de ejecutar pero antes de reportar, el servidor
    /// reenvia el comando al reconectar. Aqui se responde con el resultado ya
    /// guardado en vez de ejecutar otra vez: sin esto, una reconexion en mal
    /// momento provoca dos reinicios.
    /// </summary>
    private async Task HandleCommandAsync(
        CommandRequest request, ChannelWriter<AgentMessage> outbound, CancellationToken ct)
    {
        if (_commands.Find(request.CommandId) is { } already)
        {
            logger.LogInformation("Comando {CommandId} ya ejecutado; se reenvia el resultado guardado", request.CommandId);
            await outbound.WriteAsync(new AgentMessage { CommandResult = already }, ct);
            return;
        }

        logger.LogInformation("Ejecutando {Type} ({CommandId})", request.Type, request.CommandId);

        var result = await runner.ExecuteAsync(request, AgentVersion, ct);

        // Primero al journal y despues al cable: si el orden fuera al reves y el
        // proceso muriera en medio, el comando se ejecutaria dos veces.
        _commands.Record(result);
        await outbound.WriteAsync(new AgentMessage { CommandResult = result }, ct);
    }

    /// <summary>
    /// Fase 7. El servidor pide arrancar el host de control remoto en la sesion
    /// interactiva.
    ///
    /// Ni el ticket ni la direccion se registran en el log: la direccion es la
    /// del propio agente y el ticket es la credencial que da acceso a la
    /// pantalla. Del ticket solo se sabe que llego.
    /// </summary>
    private async Task HandleRemoteHostAsync(RemoteHostControl control, CancellationToken ct)
    {
        if (control.Action == RemoteHostControl.Types.Action.Stop)
        {
            hostLauncher.Stop($"lo pidio el servidor (sesion {control.SessionId})");
            return;
        }

        if (control.Action != RemoteHostControl.Types.Action.Start)
            return;

        if (string.IsNullOrWhiteSpace(control.SessionId) || string.IsNullOrWhiteSpace(control.HostTicket))
        {
            logger.LogWarning("Orden de arranque remoto incompleta; se ignora");
            return;
        }

        await hostLauncher.StartAsync(new RemoteHostHandshake
        {
            SessionId = control.SessionId,
            Ticket = control.HostTicket,
            ServerAddress = _options.ServerAddress,
            MachineId = _identity.MachineId,

            // El host valida el certificado del relay con los MISMOS pines que
            // el agente. Son dos canales gRPC distintos contra el mismo
            // servidor: no hay razon para que uno confie menos que el otro.
            PinnedKeys = [.. _identity.PinnedKeys],
            AllowUntrusted = _options.RemoteAllowUntrusted
        }, ct);
    }

    private Heartbeat BuildHeartbeat()
    {
        var heartbeat = new Heartbeat
        {
            Hostname = Environment.MachineName,
            LoggedUser = LoggedUser(),
            UptimeSeconds = NetworkInfo.UptimeSeconds(),
            AgentVersion = AgentVersion,
            Fingerprint = Fingerprint.Collect(),
            Remote = DetectRemote()
        };

        heartbeat.PinnedKeys.AddRange(_identity.PinnedKeys);
        heartbeat.Interfaces.AddRange(NetworkInfo.Collect(_options.ServerHost).Select(nic => new NetworkInterfaceInfo
        {
            Name = nic.Name,
            Ip = nic.Ip,
            Mac = nic.Mac,
            IsPrimary = nic.IsPrimary
        }));

        return heartbeat;
    }

    /// <summary>
    /// Identificador de la maquina en el motor remoto. Que NO este instalado no
    /// es un error: la maquina aparece sin control remoto disponible y ya.
    /// </summary>
    private RemoteAgentInfo DetectRemote()
    {
        if (DateTime.UtcNow < _nextRemoteScan)
            return _remote;

        _nextRemoteScan = DateTime.UtcNow.Add(InventoryCadence.ScanInterval);

        try
        {
            var deviceId = remoteDetector.DetectDeviceId();

            var detected = new RemoteAgentInfo
            {
                Provider = remoteDetector.Provider,
                DeviceId = deviceId ?? string.Empty,
                Available = deviceId is not null
            };

            if (detected.Available != _remote.Available)
            {
                logger.LogInformation("Control remoto ({Provider}): {Estado}",
                    detected.Provider, detected.Available ? "disponible" : "no instalado");
            }

            _remote = detected;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallo la deteccion del motor remoto");
        }

        return _remote;
    }

    /// <summary>
    /// Usuario con sesion interactiva. El servicio corre como SYSTEM, asi que
    /// Environment.UserName devolveria "SYSTEM": hay que preguntarle a WMI.
    /// </summary>
    private string LoggedUser()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT UserName FROM Win32_ComputerSystem");

            foreach (var item in searcher.Get())
            {
                using (item)
                    return item["UserName"]?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo leer el usuario con sesion iniciada");
        }

        return string.Empty;
    }

    private static TimeSpan Jitter(TimeSpan value)
        => value * (0.8 + Random.Shared.NextDouble() * 0.4);

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // apagado normal del servicio
        }
    }
}
