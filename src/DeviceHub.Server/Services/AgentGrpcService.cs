using System.Threading.Channels;
using DeviceHub.Contracts;
using DeviceHub.Server.Data;
using DeviceHub.Server.Domain;
using DeviceHub.Server.Realtime;
using DeviceHub.Server.Security;
using Grpc.Core;
using MySqlConnector;

namespace DeviceHub.Server.Services;

public sealed class AgentGrpcService(
    MachineRepository machines,
    EnrollmentRepository enrollment,
    CommandRepository commands,
    ConnectionRegistry registry,
    MachineBroadcaster broadcaster,
    ServerPins pins,
    ReenrollmentGrants reenrollment,
    ILogger<AgentGrpcService> logger) : AgentService.AgentServiceBase
{
    // ------------------------------------------------------------- enrolamiento

    public override async Task<RegisterReply> Register(RegisterRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!Guid.TryParse(request.MachineId, out _))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "machineId no es un GUID"));

        var existing = await machines.GetForAuthAsync(request.MachineId, ct);

        // Sin codigo: solo se admite si un administrador autorizo la reasociacion
        // de ESTA maquina hace pocos minutos, y solo para una que ya existe.
        //
        // Es el camino de vuelta de una PC cuyo token se invalido estando
        // desconectada. No crea maquinas nuevas -- para eso sigue haciendo falta
        // un codigo de enrolamiento -- y el permiso es de un solo uso.
        EnrollmentCodeRow consumed;

        if (string.IsNullOrWhiteSpace(request.EnrollmentCode))
        {
            if (existing is null || !reenrollment.TryConsume(request.MachineId))
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "Sin codigo de enrolamiento y sin autorizacion de reasociacion"));

            // SiteId no se usa: solo hace falta para CREAR una maquina, y aqui
            // la maquina existe por construccion. TargetMachineId se rellena
            // porque el permiso apunta a esta y a ninguna otra.
            consumed = new EnrollmentCodeRow { TargetMachineId = request.MachineId };
            logger.LogWarning("Reasociacion autorizada consumida por {MachineId}", request.MachineId);
        }
        else
        {
            var codeHash = Secrets.Sha256Hex(request.EnrollmentCode.Trim().ToUpperInvariant());

            consumed = await enrollment.TryConsumeAsync(codeHash, request.MachineId, ct)
                ?? throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "Codigo de enrolamiento invalido, vencido o ya consumido"));
        }

        // Un recovery code apunta a UNA maquina: no sirve para adoptar otra identidad.
        if (!string.IsNullOrEmpty(consumed.TargetMachineId) && consumed.TargetMachineId != request.MachineId)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "El recovery code no corresponde a esta maquina"));

        var token = Secrets.NewMachineToken();
        var tokenHash = Secrets.Sha256Hex(token);
        var confidence = await EffectiveConfidenceAsync(request.Fingerprint, ct);
        string machineCode;

        if (existing is null)
        {
            if (!string.IsNullOrEmpty(consumed.TargetMachineId))
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Recovery code de una maquina que ya no existe"));

            machineCode = await CreateMachineAsync(request, consumed.SiteId, tokenHash, confidence, ct);
            logger.LogInformation("Maquina registrada {MachineCode} ({MachineId})", machineCode, request.MachineId);
        }
        else
        {
            // Re-registro de una maquina conocida. Aunque el codigo sea valido, el
            // hardware se revisa igual: un clon con un codigo en la mano no debe
            // poder quedarse con la identidad de la original.
            if (IdentityGuard.IsHardwareConflict(
                    existing.HardwareFingerprint, Map.Confidence(existing.FingerprintConfidence),
                    request.Fingerprint?.Hash, confidence))
            {
                await machines.MarkConflictAsync(request.MachineId,
                    "Register con fingerprint distinto y confianza HIGH en ambos lados", PeerIp(context), ct);

                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Conflicto de identidad: requiere resolucion de un administrador"));
            }

            await machines.UpdateTokenAsync(request.MachineId, tokenHash, ct);
            machineCode = existing.MachineCode;
            logger.LogInformation("Token repuesto para {MachineCode} ({MachineId})", machineCode, request.MachineId);
        }

        var reply = new RegisterReply { Token = token, MachineCode = machineCode };
        reply.PinnedKeys.AddRange(pins.Current);
        return reply;
    }

    private async Task<string> CreateMachineAsync(
        RegisterRequest request, int siteId, string tokenHash, FingerprintConfidence confidence, CancellationToken ct)
    {
        var hostname = string.IsNullOrWhiteSpace(request.Hostname) ? "PC" : request.Hostname.Trim();
        var candidate = hostname.Length > 60 ? hostname[..60] : hostname;

        try
        {
            await machines.CreateAsync(request.MachineId, siteId, candidate, hostname, tokenHash,
                request.Fingerprint?.Hash ?? string.Empty, confidence, ct);
            return candidate;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Dos PCs con el mismo hostname en el mismo sitio. El administrador
            // renombrara despues; aqui solo hace falta un codigo unico y legible.
            var unique = $"{candidate}-{request.MachineId[..4].ToUpperInvariant()}";
            await machines.CreateAsync(request.MachineId, siteId, unique, hostname, tokenHash,
                request.Fingerprint?.Hash ?? string.Empty, confidence, ct);
            return unique;
        }
    }

    // ------------------------------------------------------------------ stream

    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var machineId = context.RequestHeaders.GetValue("x-machine-id") ?? string.Empty;
        var token = ExtractBearer(context);

        var auth = await machines.GetForAuthAsync(machineId, ct);

        if (auth is null || string.IsNullOrEmpty(auth.TokenHash) || token is null
            || !Secrets.FixedTimeEqualsHex(Secrets.Sha256Hex(token), auth.TokenHash))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Token de maquina invalido"));
        }

        if (Map.Identity(auth.IdentityState) == IdentityState.Conflict)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Maquina en conflicto de identidad: requiere resolucion de un administrador"));

        // Detector de clonacion que no depende del hardware: dos streams vivos
        // con el mismo machineId no tienen explicacion legitima.
        var outbound = registry.TryRegister(machineId);

        if (outbound is null)
        {
            await machines.MarkConflictAsync(machineId,
                "Segundo stream Connect simultaneo con el mismo machineId", PeerIp(context), ct);

            logger.LogError("Conflicto de identidad en {MachineId}: ya hay un agente conectado", machineId);

            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Ya hay un agente conectado con este machineId"));
        }

        logger.LogInformation("Agente conectado: {MachineCode} ({MachineId})", auth.MachineCode, machineId);

        try
        {
            await responseStream.WriteAsync(BuildConfig(auth.MachineCode), ct);
            await DeliverPendingCommandsAsync(machineId, outbound, ct);

            var pump = PumpOutboundAsync(outbound.Reader, responseStream, ct);
            var ingest = IngestAsync(requestStream, machineId, auth, outbound, context);

            await await Task.WhenAny(pump, ingest);
        }
        finally
        {
            registry.Unregister(machineId, outbound);
            logger.LogInformation("Agente desconectado: {MachineId}", machineId);
        }
    }

    private ServerMessage BuildConfig(string machineCode)
    {
        var config = new ConfigUpdate { MachineCode = machineCode };
        config.PinnedKeys.AddRange(pins.Current);
        return new ServerMessage { Config = config };
    }

    private static async Task PumpOutboundAsync(
        ChannelReader<ServerMessage> reader, IServerStreamWriter<ServerMessage> stream, CancellationToken ct)
    {
        await foreach (var message in reader.ReadAllAsync(ct))
            await stream.WriteAsync(message, ct);
    }

    private async Task IngestAsync(
        IAsyncStreamReader<AgentMessage> requestStream, string machineId, MachineAuthRow auth,
        Channel<ServerMessage> outbound, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // Se arrastra el estado del fingerprint por conexion: el de `auth` queda
        // obsoleto en cuanto se aplica el primer heartbeat.
        var storedHash = auth.HardwareFingerprint;
        var storedConfidence = Map.Confidence(auth.FingerprintConfidence);
        FingerprintConfidence? cachedConfidence = null;
        string? cachedHash = null;

        await foreach (var message in requestStream.ReadAllAsync(ct))
        {
            if (message.PayloadCase == AgentMessage.PayloadOneofCase.Hardware)
            {
                await SaveInventoryAsync(machineId, message.Hardware, ct);
                continue;
            }

            if (message.PayloadCase == AgentMessage.PayloadOneofCase.CommandResult)
            {
                var result = message.CommandResult;
                await commands.ApplyResultAsync(result, machineId, ct);

                logger.LogInformation("{MachineId}: comando {CommandId} -> {Status} {Error}",
                    machineId, result.CommandId, result.Status, result.ErrorCode);

                continue;
            }

            if (message.PayloadCase == AgentMessage.PayloadOneofCase.Metrics)
            {
                await machines.SaveMetricsAsync(machineId, message.Metrics.Samples, ct);

                if (await machines.GetAsync(machineId, ct) is { } updated)
                    broadcaster.Publish(SummaryMapper.ToSummary(updated, DateTime.UtcNow));

                continue;
            }

            if (message.PayloadCase != AgentMessage.PayloadOneofCase.Heartbeat)
                continue;

            var heartbeat = message.Heartbeat;
            var hash = heartbeat.Fingerprint?.Hash;

            // El COUNT solo se repite cuando cambia el hash: el heartbeat es cada
            // 30 s y el hardware no se mueve.
            if (cachedConfidence is null || cachedHash != hash)
            {
                cachedConfidence = await EffectiveConfidenceAsync(heartbeat.Fingerprint, ct);
                cachedHash = hash;
            }

            var confidence = cachedConfidence.Value;

            if (IdentityGuard.IsHardwareConflict(storedHash, storedConfidence, hash, confidence))
            {
                await machines.MarkConflictAsync(machineId,
                    "Heartbeat con fingerprint distinto y confianza HIGH en ambos lados", PeerIp(context), ct);

                logger.LogError("Conflicto de identidad en {MachineId}: fingerprint cambio de {Old} a {New}",
                    machineId, storedHash, hash);

                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Conflicto de identidad: requiere resolucion de un administrador"));
            }

            await machines.ApplyHeartbeatAsync(machineId, heartbeat, confidence, ct);

            storedHash = hash;
            storedConfidence = confidence;

            var row = await machines.GetAsync(machineId, ct);
            if (row is not null)
                broadcaster.Publish(SummaryMapper.ToSummary(row, DateTime.UtcNow));

            // Red de seguridad: SendCommand empuja al conectar, pero ese TryPush
            // puede fallar si la cola de la conexion esta llena, y entonces el
            // comando quedaria pendiente para siempre. Cada latido reintenta.
            await DeliverPendingCommandsAsync(machineId, outbound, ct);
        }
    }

    /// <summary>
    /// Al reconectar se entrega lo que quedo pendiente -- pero primero se caducan
    /// los vencidos. Una PC que estuvo apagada dos horas no debe recibir el
    /// reinicio que alguien pidio al principio de ese rato.
    /// </summary>
    private async Task DeliverPendingCommandsAsync(
        string machineId, Channel<ServerMessage> outbound, CancellationToken ct)
    {
        await commands.ExpireStaleAsync(machineId, ct);

        foreach (var row in await commands.GetDeliverableAsync(machineId, ct))
        {
            if (!outbound.Writer.TryWrite(new ServerMessage { Command = SummaryMapper.ToRequest(row) }))
                break;

            await commands.MarkSentAsync(row.Id, ct);
            logger.LogInformation("{MachineId}: reentregando comando pendiente {CommandId}", machineId, row.Id);
        }
    }

    private async Task SaveInventoryAsync(string machineId, HardwareInventory inventory, CancellationToken ct)
    {
        var change = await machines.SaveHardwareAsync(machineId, inventory, DiskJson.Serialize(inventory), ct);

        if (change is not null)
            logger.LogWarning("{MachineId}: {Change}", machineId, change);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<FingerprintConfidence> EffectiveConfidenceAsync(HardwareFingerprint? fingerprint, CancellationToken ct)
    {
        if (fingerprint is null || string.IsNullOrWhiteSpace(fingerprint.Hash))
            return FingerprintConfidence.Low;

        if (fingerprint.Confidence == FingerprintConfidence.Low)
            return FingerprintConfidence.Low;

        var shared = await machines.CountSharingFingerprintAsync(fingerprint.Hash, ct);
        return IdentityGuard.Degrade(fingerprint.Confidence, shared);
    }

    private static string? ExtractBearer(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue("authorization");
        return header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private static string? PeerIp(ServerCallContext context) => context.Peer;
}
