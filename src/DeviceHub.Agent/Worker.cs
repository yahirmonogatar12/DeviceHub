using DeviceHub.Agent.Identity;
using DeviceHub.Agent.Network;
using DeviceHub.Agent.Security;
using DeviceHub.Contracts;
using Grpc.Core;
using Grpc.Net.Client;

namespace DeviceHub.Agent;

public sealed class Worker(
    IOptions<AgentOptions> options,
    MachineIdentity identityStore,
    PinnedChannelFactory channelFactory,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly string AgentVersion =
        typeof(Worker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly AgentOptions _options = options.Value;
    private MachineIdentityFile _identity = new();

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
    }

    private async Task EnsureRegisteredAsync(GrpcChannel channel, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_identity.ProtectedToken))
            return;

        if (string.IsNullOrWhiteSpace(_options.EnrollmentCode))
            throw new InvalidOperationException(
                "Sin token y sin codigo de enrolamiento. Genera uno en el dashboard (Admin -> Create enrollment code).");

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
        var token = MachineIdentity.Unprotect(_identity.ProtectedToken)
            ?? throw new InvalidOperationException(
                "Token ilegible (DPAPI). Hace falta un recovery code emitido por un administrador.");

        var client = new AgentService.AgentServiceClient(channel);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {token}" },
            { "x-machine-id", _identity.MachineId }
        };

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var call = client.Connect(headers, cancellationToken: session.Token);

        logger.LogInformation("Stream abierto contra {Address}", _options.ServerAddress);

        var reader = ReadServerMessagesAsync(call.ResponseStream, session.Token);
        var writer = SendHeartbeatsAsync(call.RequestStream, session.Token);

        // El primero que termine (o falle) cierra la sesion; el bucle exterior reconecta.
        var finished = await Task.WhenAny(reader, writer);
        await session.CancelAsync();
        await finished; // propaga la excepcion real, no una de cancelacion
    }

    private async Task SendHeartbeatsAsync(IClientStreamWriter<AgentMessage> stream, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.HeartbeatSeconds);

        while (!ct.IsCancellationRequested)
        {
            await stream.WriteAsync(new AgentMessage { Heartbeat = BuildHeartbeat() }, ct);
            await Task.Delay(interval, ct);
        }
    }

    private async Task ReadServerMessagesAsync(IAsyncStreamReader<ServerMessage> stream, CancellationToken ct)
    {
        await foreach (var message in stream.ReadAllAsync(ct))
        {
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

    private Heartbeat BuildHeartbeat()
    {
        var heartbeat = new Heartbeat
        {
            Hostname = Environment.MachineName,
            LoggedUser = LoggedUser(),
            UptimeSeconds = NetworkInfo.UptimeSeconds(),
            AgentVersion = AgentVersion,
            Fingerprint = Fingerprint.Collect()
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
