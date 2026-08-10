using System.Security.Claims;
using System.Text;
using DeviceHub.Contracts;
using DeviceHub.Server.Data;
using DeviceHub.Server.Realtime;
using DeviceHub.Server.Security;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DeviceHub.Server.Services;

/// <summary>
/// Superficie del dashboard. Autenticacion por JWT con rol.
///
/// La autorizacion es declarativa con [Authorize(Roles=...)] -- un solo lugar
/// por metodo, verificada por el pipeline antes de entrar al handler. La matriz
/// completa de la Fase 13 se expresa aqui sin escribir un interceptor propio.
/// </summary>
[Authorize]
public sealed class AdminGrpcService(
    MachineRepository machines,
    EnrollmentRepository enrollment,
    UserRepository users,
    MachineBroadcaster broadcaster,
    ConnectionRegistry registry,
    ServerPins pins,
    JwtKeyProvider jwtKey,
    IOptions<ServerOptions> options,
    ILogger<AdminGrpcService> logger) : AdminService.AdminServiceBase
{
    private readonly ServerOptions _options = options.Value;

    [AllowAnonymous]
    public override async Task<LoginReply> Login(LoginRequest request, ServerCallContext context)
    {
        var user = await users.FindAsync(request.Username, context.CancellationToken);

        // Mismo mensaje para usuario inexistente y password mala: no se filtra
        // que cuentas existen.
        if (user is null || !user.IsActive || !Secrets.VerifyPassword(request.Password, user.PasswordHash))
        {
            logger.LogWarning("Login fallido para {Username} desde {Peer}", request.Username, context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Credenciales invalidas"));
        }

        return new LoginReply
        {
            Token = IssueJwt(user),
            Username = user.Username,
            Role = user.Role
        };
    }

    public override async Task<ListMachinesReply> ListMachines(ListMachinesRequest request, ServerCallContext context)
    {
        var rows = await machines.ListAsync(request.SiteCode, request.Area, request.Line, context.CancellationToken);
        var now = DateTime.UtcNow;

        var reply = new ListMachinesReply();
        reply.Machines.AddRange(rows.Select(row => SummaryMapper.ToSummary(row, now)));
        return reply;
    }

    public override async Task<MachineDetail> GetMachine(MachineRef request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var row = await machines.GetAsync(request.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        var detail = new MachineDetail
        {
            Summary = SummaryMapper.ToSummary(row, DateTime.UtcNow),
            Fingerprint = new HardwareFingerprint
            {
                Hash = row.HardwareFingerprint ?? string.Empty,
                Confidence = Map.Confidence(row.FingerprintConfidence)
            }
        };

        detail.IpHistory.AddRange((await machines.GetIpHistoryAsync(request.MachineId, ct)).Select(SummaryMapper.ToProto));
        detail.PlacementHistory.AddRange((await machines.GetPlacementHistoryAsync(request.MachineId, ct)).Select(SummaryMapper.ToProto));

        if (await machines.GetHardwareAsync(request.MachineId, ct) is { } hardware)
        {
            detail.Hardware = SummaryMapper.ToProto(hardware);
            detail.HardwareCollectedAt = Timestamp.FromDateTime(Db.AsUtc(hardware.CollectedAt));
        }

        return detail;
    }

    /// <summary>
    /// Empuja el estado inicial y luego solo los cambios. El DataGrid del WPF no
    /// hace polling.
    /// </summary>
    public override async Task WatchMachines(
        WatchRequest request, IServerStreamWriter<MachineSummary> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var (id, reader) = broadcaster.Subscribe();

        try
        {
            var now = DateTime.UtcNow;
            foreach (var row in await machines.ListAsync(request.SiteCode, string.Empty, string.Empty, ct))
                await responseStream.WriteAsync(SummaryMapper.ToSummary(row, now), ct);

            await foreach (var summary in reader.ReadAllAsync(ct))
            {
                if (request.SiteCode.Length == 0 || summary.SiteCode == request.SiteCode)
                    await responseStream.WriteAsync(summary, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // el dashboard cerro la ventana
        }
        finally
        {
            broadcaster.Unsubscribe(id);
        }
    }

    /// <summary>
    /// Renombrar y mover: machineId inmutable, historial conservado, y el agente
    /// adopta el nombre por el stream sin que nadie toque la PC.
    /// </summary>
    [Authorize(Roles = "administrator")]
    public override async Task<MachineDetail> MoveMachine(MoveMachineRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        _ = await machines.GetAsync(request.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        if (string.IsNullOrWhiteSpace(request.MachineCode))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "machine_code no puede quedar vacio"));

        var siteId = await machines.GetSiteIdAsync(request.SiteCode, ct)
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"Sitio desconocido: {request.SiteCode}"));

        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";

        await machines.MoveAsync(request.MachineId, siteId, request.MachineCode.Trim(),
            NullIfEmpty(request.DisplayName), NullIfEmpty(request.Area), NullIfEmpty(request.Line),
            NullIfEmpty(request.Station), actor, ct);

        var config = new ConfigUpdate { MachineCode = request.MachineCode.Trim() };
        config.PinnedKeys.AddRange(pins.Current);
        registry.TryPush(request.MachineId, new ServerMessage { Config = config });

        logger.LogInformation("{Actor} movio {MachineId} a {MachineCode}", actor, request.MachineId, request.MachineCode);

        var updated = await GetMachine(new MachineRef { MachineId = request.MachineId }, context);
        broadcaster.Publish(updated.Summary);
        return updated;
    }

    [Authorize(Roles = "administrator")]
    public override async Task<EnrollmentCodeReply> CreateEnrollmentCode(
        CreateEnrollmentCodeRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var siteCode = string.IsNullOrWhiteSpace(request.SiteCode) ? _options.DefaultSiteCode : request.SiteCode;
        var siteId = await machines.GetSiteIdAsync(siteCode, ct)
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"Sitio desconocido: {siteCode}"));

        // Ventana corta y un solo uso por defecto: un codigo filtrado sirve de poco.
        var maxUses = request.MaxUses > 0 ? request.MaxUses : 1;
        var minutes = Math.Clamp(request.ValidMinutes > 0 ? request.ValidMinutes : 30, 5, 120);
        var expiresAt = DateTime.UtcNow.AddMinutes(minutes);

        var code = Secrets.NewEnrollmentCode();
        var target = NullIfEmpty(request.TargetMachineId);

        if (target is not null && await machines.GetAsync(target, ct) is null)
            throw new RpcException(new Status(StatusCode.NotFound, "La maquina del recovery code no existe"));

        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";
        await enrollment.CreateAsync(Secrets.Sha256Hex(code), siteId, actor, expiresAt, maxUses, target, ct);

        logger.LogInformation("{Actor} genero un codigo {Kind} para {Site}, {Uses} uso(s), {Minutes} min",
            actor, target is null ? "de enrolamiento" : "de recovery", siteCode, maxUses, minutes);

        return new EnrollmentCodeReply
        {
            Code = code, // unica vez que existe en claro
            ExpiresAt = Timestamp.FromDateTime(expiresAt),
            MaxUses = maxUses
        };
    }

    /// <summary>
    /// Un conflicto de identidad lo resuelve un humano. El servidor no adivina si
    /// fue un cambio de placa o un clon.
    /// </summary>
    [Authorize(Roles = "administrator")]
    public override async Task<MachineDetail> ResolveIdentityConflict(
        ResolveConflictRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";

        switch (request.Resolution)
        {
            case ResolveConflictRequest.Types.Resolution.ApproveNewHardware:
                await machines.ApproveNewHardwareAsync(request.MachineId, actor, ct);
                break;

            case ResolveConflictRequest.Types.Resolution.IssueNewIdentity:
                await machines.IssueNewIdentityAsync(request.MachineId, actor, ct);
                break;

            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Resolucion no especificada"));
        }

        logger.LogWarning("{Actor} resolvio el conflicto de {MachineId} como {Resolution}",
            actor, request.MachineId, request.Resolution);

        return await GetMachine(new MachineRef { MachineId = request.MachineId }, context);
    }

    private string IssueJwt(UserRow user)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.JwtIssuer,
            Audience = _options.JwtIssuer,
            Expires = DateTime.UtcNow.AddHours(_options.JwtHours),
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            ]),
            SigningCredentials = new SigningCredentials(jwtKey.SigningKey, SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
