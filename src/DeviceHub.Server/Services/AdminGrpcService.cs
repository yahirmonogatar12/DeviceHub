using System.Security.Claims;
using System.Text;
using DeviceHub.Contracts;
using DeviceHub.Server.Data;
using DeviceHub.Server.Realtime;
using DeviceHub.Server.Remote;
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
    CommandRepository commands,
    SessionRepository sessions,
    TerminalRepository terminal,
    AuditRepository audit,
    RateLimiter limiter,
    RemoteProviderCatalog motores,
    UserRepository users,
    MachineBroadcaster broadcaster,
    ConnectionRegistry registry,
    RemoteTicketRegistry remoteTickets,
    ReenrollmentGrants reenrollment,
    ServerPins pins,
    JwtKeyProvider jwtKey,
    IOptions<ServerOptions> options,
    ILogger<AdminGrpcService> logger) : AdminService.AdminServiceBase
{
    private readonly ServerOptions _options = options.Value;

    [AllowAnonymous]
    public override async Task<LoginReply> Login(LoginRequest request, ServerCallContext context)
    {
        // Registrar los intentos fallidos no los detiene. Se limita por usuario Y
        // por origen: por usuario para que no se le pruebe la cuenta al admin
        // desde mil sitios, por origen para que un solo atacante no barra la
        // lista de usuarios.
        var throttleKey = $"login:{request.Username.ToLowerInvariant()}|{context.Peer}";

        if (!limiter.TryAcquire(throttleKey, RateLimits.LoginAttempts, RateLimits.LoginWindow))
        {
            logger.LogWarning("Login bloqueado por exceso de intentos: {Username} desde {Peer}",
                request.Username, context.Peer);

            await audit.WriteAsync(new AuditEntry(
                UserId: request.Username, UserRole: null, Action: AuditActions.LoginThrottled,
                MachineId: null, MachineCode: null, SiteCode: null,
                RequestId: context.GetHttpContext().TraceIdentifier, SourceIp: context.Peer,
                Outcome: AuditEntry.Denied, Details: null), context.CancellationToken);

            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "Demasiados intentos. Espera unos minutos."));
        }

        var user = await users.FindAsync(request.Username, context.CancellationToken);

        // Mismo mensaje para usuario inexistente y password mala: no se filtra
        // que cuentas existen.
        if (user is null || !user.IsActive || !Secrets.VerifyPassword(request.Password, user.PasswordHash))
        {
            logger.LogWarning("Login fallido para {Username} desde {Peer}", request.Username, context.Peer);

            // Los intentos fallidos se auditan: es como se detecta que alguien
            // esta probando contrasenas contra la consola de administracion.
            await audit.WriteAsync(new AuditEntry(
                UserId: request.Username, UserRole: null, Action: AuditActions.LoginFailed,
                MachineId: null, MachineCode: null, SiteCode: null,
                RequestId: context.GetHttpContext().TraceIdentifier, SourceIp: context.Peer,
                Outcome: AuditEntry.Denied, Details: null), context.CancellationToken);

            throw new RpcException(new Status(StatusCode.Unauthenticated, "Credenciales invalidas"));
        }

        limiter.Reset(throttleKey);
        await users.TouchLoginAsync(user.Username, context.CancellationToken);

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

        await audit.WriteAsync(BuildAudit(context, AuditActions.MachineMoved,
            await machines.GetAsync(request.MachineId, ct), AuditEntry.Allowed,
            $"-> {request.MachineCode} @ {request.Area}/{request.Line}/{request.Station}"), ct);

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
        // Hasta un turno completo: desplegar 80 PCs no cabe en dos horas, y forzar a
        // regenerar el codigo a media instalacion es como se acaba usando uno eterno.
        var minutes = Math.Clamp(request.ValidMinutes > 0 ? request.ValidMinutes : 30, 5, 480);
        var expiresAt = DateTime.UtcNow.AddMinutes(minutes);

        var code = Secrets.NewEnrollmentCode();
        var target = NullIfEmpty(request.TargetMachineId);

        if (target is not null && await machines.GetAsync(target, ct) is null)
            throw new RpcException(new Status(StatusCode.NotFound, "La maquina del recovery code no existe"));

        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";
        await enrollment.CreateAsync(Secrets.Sha256Hex(code), siteId, actor, expiresAt, maxUses, target, ct);

        await audit.WriteAsync(BuildAudit(context, AuditActions.EnrollmentCodeCreated, null, AuditEntry.Allowed,
            $"site={siteCode} usos={maxUses} minutos={minutes} recovery={target is not null}"), ct);

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
    /// Fase 6: autoriza una sesion de control remoto y emite sus dos tickets.
    ///
    /// Esto NO lanza nada ni decide que motor se usa -- eso es StartRemoteSession
    /// y la Fase 8. Aqui solo se responde a "quien puede abrir esta sesion".
    ///
    /// La identidad del tecnico se toma del principal autenticado y nunca del
    /// mensaje: si viniera en el request, cualquiera con permiso para llamar
    /// podria emitir un ticket a nombre de otro y el ticket dejaria de decir
    /// quien pidio la sesion.
    ///
    /// Los dos secretos salen en claro UNA vez. El servidor guarda solo su
    /// SHA-256, y la auditoria guarda metadata: quien, que maquina y cuando.
    /// Nunca el secreto ni su hash -- un hash de un secreto de 256 bits no se
    /// invierte, pero tampoco hace falta en una tabla que consulta gente.
    /// </summary>
    [Authorize(Roles = "administrator,technician")]
    public override async Task<IssueRemoteTicketsResponse> IssueRemoteTickets(
        IssueRemoteTicketsRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var tecnico = context.GetHttpContext().User.Identity?.Name ?? "desconocido";

        if (string.IsNullOrWhiteSpace(request.TargetMachineId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Falta target_machine_id"));

        var maquina = await machines.GetAsync(request.TargetMachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        var sessionId = Guid.NewGuid().ToString("n");

        // El del host se ata a la PC controlada; el del viewer, a la del tecnico
        // Y a su usuario. Asi ninguno de los dos sirve en el lugar del otro.
        var (hostTicket, host) = remoteTickets.Issue(sessionId, DeviceHub.Remote.Contracts.RemoteRole.Host, maquina.Id);

        var (viewerTicket, _) = remoteTickets.Issue(
            sessionId, DeviceHub.Remote.Contracts.RemoteRole.Viewer,
            string.IsNullOrWhiteSpace(request.ViewerMachineId) ? tecnico : request.ViewerMachineId,
            tecnico);

        // Fase 7: se le ordena al agente de esa PC que arranque el host en la
        // sesion interactiva. El ticket viaja por el stream del agente, que ya
        // esta autenticado y con el certificado fijado, y de ahi a un named pipe
        // con ACL. Nunca por linea de comandos.
        //
        // Si la PC esta offline el ticket sigue siendo valido y caduca solo: el
        // tecnico se entera por host_notified, no por un error de autorizacion
        // que no lo es.
        var avisado = registry.TryPush(maquina.Id, new ServerMessage
        {
            RemoteHost = new RemoteHostControl
            {
                Action = RemoteHostControl.Types.Action.Start,
                SessionId = sessionId,
                HostTicket = hostTicket,
                ExpiresAtUs = host.ExpiresAt.ToUnixTimeMilliseconds() * 1000
            }
        });

        await audit.WriteAsync(BuildAudit(
            context, AuditActions.RemoteRequested, maquina, AuditEntry.Allowed,
            $"sesion {sessionId} vence {host.ExpiresAt:O} host={(avisado ? "avisado" : "sin agente")}"), ct);

        logger.LogInformation(
            "{Tecnico} autorizo la sesion remota {Sesion} sobre {MachineId} (agente {Estado})",
            tecnico, sessionId, maquina.Id, avisado ? "avisado" : "desconectado");

        return new IssueRemoteTicketsResponse
        {
            SessionId = sessionId,
            HostTicket = hostTicket,
            ViewerTicket = viewerTicket,
            ExpiresAtUs = host.ExpiresAt.ToUnixTimeMilliseconds() * 1000,
            HostNotified = avisado
        };
    }

    /// <summary>
    /// Autoriza a una maquina a reasociarse sin recovery code durante unos
    /// minutos.
    ///
    /// Es la salida de un callejon real: emitir identidad nueva sobre una PC
    /// DESCONECTADA le invalida el token, y como el agente no vuelve a
    /// registrarse mientras tenga uno guardado, esa PC no regresaba nunca. La
    /// unica cura era ir hasta ella a editar un archivo. Le paso a INPUTM1.
    ///
    /// Un administrador y solo un administrador: el permiso no lleva secreto, y
    /// quien conozca el machineId de esa PC podria aprovechar la ventana. Por eso
    /// dura minutos y se audita.
    /// </summary>
    [Authorize(Roles = Roles.Administrator)]
    public override async Task<MachineDetail> AuthorizeReenrollment(MachineRef request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var maquina = await machines.GetAsync(request.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        var vence = reenrollment.Authorize(maquina.Id);

        await audit.WriteAsync(BuildAudit(
            context, AuditActions.ReenrollmentAuthorized, maquina, AuditEntry.Allowed,
            $"vence {vence:O}"), ct);

        logger.LogWarning(
            "{Actor} autorizo la reasociacion de {MachineCode} ({MachineId}) hasta {Vence:HH:mm:ss}",
            context.GetHttpContext().User.Identity?.Name ?? "desconocido",
            maquina.MachineCode, maquina.Id, vence);

        return await GetMachine(request, context);
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

        await audit.WriteAsync(BuildAudit(context, AuditActions.IdentityResolved,
            await machines.GetAsync(request.MachineId, ct), AuditEntry.Allowed, request.Resolution.ToString()), ct);

        logger.LogWarning("{Actor} resolvio el conflicto de {MachineId} como {Resolution}",
            actor, request.MachineId, request.Resolution);

        return await GetMachine(new MachineRef { MachineId = request.MachineId }, context);
    }

    // ------------------------------------------------- usuarios (Fase 13)

    [Authorize(Roles = Roles.Administrator)]
    public override async Task<UserList> ListUsers(ListUsersRequest request, ServerCallContext context)
    {
        var list = new UserList();
        list.Users.AddRange((await users.ListAsync(context.CancellationToken)).Select(ToRecord));
        return list;
    }

    [Authorize(Roles = Roles.Administrator)]
    public override async Task<UserRecord> CreateUser(CreateUserRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var username = request.Username.Trim().ToLowerInvariant();

        if (username.Length < 3)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Usuario demasiado corto"));

        if (Roles.Rank(request.Role) < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Rol desconocido: {request.Role}"));

        if (!PasswordPolicy.IsValid(request.Password, out var error))
            throw new RpcException(new Status(StatusCode.InvalidArgument, error));

        if (await users.FindAsync(username, ct) is not null)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Ese usuario ya existe"));

        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";
        await users.CreateAsync(username, Secrets.HashPassword(request.Password), request.Role, actor, ct);

        await audit.WriteAsync(BuildAudit(context, AuditActions.UserCreated, null, AuditEntry.Allowed,
            $"{username} rol={request.Role}"), ct);

        return ToRecord((await users.FindAsync(username, ct))!);
    }

    [Authorize(Roles = Roles.Administrator)]
    public override async Task<UserRecord> UpdateUser(UpdateUserRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var username = request.Username.Trim().ToLowerInvariant();

        var target = await users.FindAsync(username, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Usuario desconocido"));

        string? role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role;
        if (role is not null && Roles.Rank(role) < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Rol desconocido: {role}"));

        bool? active = request.SetActive ? request.IsActive : null;

        string? passwordHash = null;
        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            if (!PasswordPolicy.IsValid(request.NewPassword, out var error))
                throw new RpcException(new Status(StatusCode.InvalidArgument, error));

            passwordHash = Secrets.HashPassword(request.NewPassword);
        }

        // Quedarse sin ningun administrador activo dejaria el sistema sin forma
        // de crear usuarios ni resolver conflictos: solo se saldria reescribiendo
        // la base a mano.
        var losesAdmin = target.Role == Roles.Administrator
            && ((role is not null && role != Roles.Administrator) || active == false);

        if (losesAdmin && await users.CountAdministratorsAsync(ct) <= 1)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Es el ultimo administrador activo: crea otro antes de degradarlo o desactivarlo"));

        await users.UpdateAsync(username, role, active, passwordHash, ct);

        await audit.WriteAsync(BuildAudit(context, AuditActions.UserUpdated, null, AuditEntry.Allowed,
            $"{username} rol={role ?? "="} activo={(active?.ToString() ?? "=")} password={(passwordHash is not null)}"), ct);

        return ToRecord((await users.FindAsync(username, ct))!);
    }

    /// <summary>
    /// Cualquiera puede cambiar SU propia contrasena, y hace falta: el admin
    /// inicial nace con una aleatoria que el servidor imprime una sola vez.
    /// Exige la actual, para que una sesion olvidada abierta no baste.
    /// </summary>
    public override async Task<UserRecord> ChangeOwnPassword(ChangeOwnPasswordRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var username = context.GetHttpContext().User.Identity?.Name
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Sin sesion"));

        var user = await users.FindAsync(username, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Usuario desconocido"));

        if (!Secrets.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            await audit.WriteAsync(BuildAudit(context, AuditActions.PasswordChanged, null, AuditEntry.Denied,
                "contrasena actual incorrecta"), ct);

            throw new RpcException(new Status(StatusCode.PermissionDenied, "La contrasena actual no es correcta"));
        }

        if (!PasswordPolicy.IsValid(request.NewPassword, out var error))
            throw new RpcException(new Status(StatusCode.InvalidArgument, error));

        await users.UpdateAsync(username, null, null, Secrets.HashPassword(request.NewPassword), ct);
        await audit.WriteAsync(BuildAudit(context, AuditActions.PasswordChanged, null, AuditEntry.Allowed, username), ct);

        return ToRecord((await users.FindAsync(username, ct))!);
    }

    /// <summary>
    /// Paso 2 del procedimiento de rotacion de certificado: responde si YA es
    /// seguro cambiarlo. Mientras quede una maquina alcanzable sin el pin nuevo,
    /// cambiarlo la desconectaria.
    /// </summary>
    [Authorize(Roles = Roles.Administrator)]
    public override async Task<RotationReadinessReply> GetRotationReadiness(
        RotationReadinessRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.CandidatePin))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Falta el pin candidato"));

        var readiness = await machines.GetPinReadinessAsync(
            request.CandidatePin, StatusCalculator.UnreachableWindow, context.CancellationToken);

        var reply = new RotationReadinessReply
        {
            OnlineMachines = readiness.Count,
            ReadyMachines = readiness.Count(r => r.HasPin)
        };

        reply.PendingMachineCodes.AddRange(readiness.Where(r => !r.HasPin).Select(r => r.MachineCode));
        reply.SafeToRotate = reply.OnlineMachines > 0 && reply.PendingMachineCodes.Count == 0;

        return reply;
    }

    private static UserRecord ToRecord(UserRow row)
    {
        var record = new UserRecord
        {
            Username = row.Username,
            Role = row.Role,
            IsActive = row.IsActive,
            CreatedBy = row.CreatedBy ?? string.Empty
        };

        if (row.LastLoginAt is not null)
            record.LastLoginAt = Timestamp.FromDateTime(Db.AsUtc(row.LastLoginAt.Value));

        return record;
    }

    // -------------------------------------------------- auditoria (Fase 12)

    public override async Task<AuditList> ListAudit(AuditQuery request, ServerCallContext context)
    {
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 50;
        var machineId = string.IsNullOrWhiteSpace(request.MachineId) ? null : request.MachineId;

        var list = new AuditList();

        foreach (var row in await audit.ListAsync(machineId, limit, context.CancellationToken))
        {
            list.Entries.Add(new AuditRecord
            {
                OccurredAt = Timestamp.FromDateTime(Db.AsUtc(row.OccurredAt)),
                UserId = row.UserId,
                UserRole = row.UserRole ?? string.Empty,
                Action = row.Action,
                MachineCode = row.MachineCode ?? string.Empty,
                SiteCode = row.SiteCode ?? string.Empty,
                SourceIp = row.SourceIp ?? string.Empty,
                Outcome = row.Outcome,
                Details = row.Details ?? string.Empty
            });
        }

        return list;
    }

    /// <summary>
    /// Construye la fila de auditoria a partir del contexto de la llamada.
    ///
    /// El request_id sale del TraceIdentifier de ASP.NET: correlaciona todo lo
    /// que ocurrio en una misma peticion, incluida la denegacion y el comando
    /// que si se creo despues.
    /// </summary>
    private static AuditEntry BuildAudit(
        ServerCallContext context, string action, MachineRow? machine, string outcome, string? details = null)
    {
        var user = context.GetHttpContext().User;

        return new AuditEntry(
            UserId: user.Identity?.Name ?? "desconocido",
            UserRole: user.FindFirst(ClaimTypes.Role)?.Value,
            Action: action,
            MachineId: machine?.Id,
            MachineCode: machine?.MachineCode,
            SiteCode: machine?.SiteCode,
            RequestId: context.GetHttpContext().TraceIdentifier,
            SourceIp: context.Peer,
            Outcome: outcome,
            Details: details);
    }

    /// <summary>
    /// Un intento RECHAZADO es tan auditable como uno permitido: que alguien sin
    /// permisos intentara apagar una PC es justo lo que hay que poder ver despues.
    /// </summary>
    private async Task DenyAsync(
        ServerCallContext context, string action, MachineRow? machine, string reason)
    {
        await audit.WriteAsync(
            BuildAudit(context, action, machine, AuditEntry.Denied, reason), context.CancellationToken);

        logger.LogWarning("DENEGADO {Action} sobre {MachineCode}: {Reason}",
            action, machine?.MachineCode ?? "-", reason);

        throw new RpcException(new Status(StatusCode.PermissionDenied, reason));
    }

    // ------------------------------------------------------- terminal (Fase 15)

    [Authorize(Roles = Roles.Engineer + "," + Roles.Administrator)]
    public override async Task<TerminalSessionReply> StartTerminalSession(MachineRef request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var machine = await machines.GetAsync(request.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        if (StatusCalculator.Compute(machine.LastSeen, DateTime.UtcNow) != MachineStatus.Online)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "La maquina no esta en linea: un terminal contra una PC desconectada no ejecuta nada"));

        await terminal.CloseInactiveAsync(ct);

        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";

        var sessionId = await sessions.StartAsync(
            machine.Id, actor, "terminal", null, context.Peer,
            BuildAudit(context, AuditActions.TerminalStart, machine, AuditEntry.Allowed), ct, kind: "terminal");

        logger.LogWarning("{Actor} abrio TERMINAL sobre {MachineCode} desde {Source}",
            actor, machine.MachineCode, context.Peer);

        return new TerminalSessionReply
        {
            SessionId = sessionId,
            MachineCode = machine.MachineCode,
            WorkingDir = @"C:\",
            StartedAt = Timestamp.FromDateTime(DateTime.UtcNow)
        };
    }

    /// <summary>
    /// Ejecuta dentro de una sesion abierta. No existe la variante suelta: sin
    /// sesion no hay ejecucion, y con sesion todo queda con su salida.
    /// </summary>
    [Authorize(Roles = Roles.Engineer + "," + Roles.Administrator)]
    public override async Task<TerminalCommandReply> RunTerminalCommand(
        TerminalCommandRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        await terminal.CloseInactiveAsync(ct);

        var session = await terminal.GetAsync(request.SessionId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Sesion de terminal desconocida"));

        if (session.EndedAt is not null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"La sesion se cerro por inactividad ({TerminalRepository.InactivityTimeout.TotalMinutes:0} min). Abre una nueva."));

        var actor = context.GetHttpContext().User.Identity?.Name ?? "desconocido";

        // La sesion es de quien la abrio. Compartir un session_id no puede servir
        // para ejecutar en nombre de otro y que la auditoria culpe al primero.
        if (!string.Equals(session.UserId, actor, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Esa sesion pertenece a otro usuario"));

        if (string.IsNullOrWhiteSpace(request.Command))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Comando vacio"));

        var machine = await machines.GetAsync(session.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        var workingDir = string.IsNullOrWhiteSpace(session.WorkingDir) ? @"C:\" : session.WorkingDir;
        var started = DateTime.UtcNow;

        var parameters = new Dictionary<string, string>
        {
            ["command"] = request.Command,
            ["cwd"] = workingDir
        };

        var definition = CommandPolicy.Get(CommandType.RunShell);

        var commandId = await commands.CreateAsync(
            session.MachineId, CommandType.RunShell, parameters, actor, definition.Ttl,
            BuildAudit(context, AuditActions.TerminalCommand, machine, AuditEntry.Allowed,
                $"session={session.Id} {request.Command}"), ct);

        var row = await commands.GetAsync(commandId, ct);
        if (row is not null)
            registry.TryPush(session.MachineId, new ServerMessage { Command = SummaryMapper.ToRequest(row) });

        var outcome = await WaitForCommandAsync(commandId, definition.Timeout + TimeSpan.FromSeconds(10), ct);
        var duration = (int)(DateTime.UtcNow - started).TotalMilliseconds;

        var shell = ParseShellResult(outcome, workingDir);

        var sequence = await terminal.RecordAsync(session.Id, request.Command, shell.WorkingDir,
            shell.Output, shell.ExitCode, shell.Truncated, duration, ct);

        return new TerminalCommandReply
        {
            Sequence = sequence,
            Output = shell.Output,
            ExitCode = shell.ExitCode,
            WorkingDir = shell.WorkingDir,
            Truncated = shell.Truncated,
            DurationMs = duration,
            Identity = shell.Identity
        };
    }

    [Authorize(Roles = Roles.Engineer + "," + Roles.Administrator)]
    public override async Task<TerminalSessionReply> EndTerminalSession(
        RemoteSessionRef request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var session = await terminal.GetAsync(request.SessionId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Sesion desconocida"));

        var machine = await machines.GetAsync(session.MachineId, ct);

        await sessions.EndAsync(request.SessionId, "closed",
            BuildAudit(context, AuditActions.TerminalEnd, machine, AuditEntry.Allowed,
                $"{session.CommandCount} comandos"), ct);

        return new TerminalSessionReply
        {
            SessionId = session.Id,
            MachineCode = machine?.MachineCode ?? string.Empty,
            CommandCount = session.CommandCount,
            StartedAt = Timestamp.FromDateTime(Db.AsUtc(session.StartedAt)),
            EndedAt = Timestamp.FromDateTime(DateTime.UtcNow)
        };
    }

    private async Task<CommandRow?> WaitForCommandAsync(string commandId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(300, ct);
            var row = await commands.GetAsync(commandId, ct);

            if (row is not null && row.Status is not ("pending" or "sent" or "running"))
                return row;
        }

        return null;
    }

    private static ShellOutcome ParseShellResult(CommandRow? row, string fallbackDir)
    {
        if (row is null)
            return new ShellOutcome("Sin respuesta del agente", -1, fallbackDir, false);

        if (row.Status != "completed")
            return new ShellOutcome($"{row.Status}: {row.Result} {row.ErrorCode}".Trim(), -1, fallbackDir, false);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(row.Result ?? "{}");
            var root = document.RootElement;

            return new ShellOutcome(
                root.TryGetProperty("Output", out var output) ? output.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("ExitCode", out var code) ? code.GetInt32() : -1,
                root.TryGetProperty("WorkingDir", out var dir) ? dir.GetString() ?? fallbackDir : fallbackDir,
                root.TryGetProperty("Truncated", out var truncated) && truncated.GetBoolean(),
                root.TryGetProperty("Identity", out var identity) ? identity.GetString() ?? string.Empty : string.Empty);
        }
        catch (System.Text.Json.JsonException)
        {
            return new ShellOutcome(row.Result ?? string.Empty, -1, fallbackDir, false);
        }
    }

    private sealed record ShellOutcome(
        string Output, int ExitCode, string WorkingDir, bool Truncated, string Identity = "");

    // -------------------------------------------------- control remoto (Fases 10-11)

    /// <summary>
    /// Autoriza, registra la sesion y devuelve QUE lanzar. El servidor no abre
    /// nada: el cliente remoto corre en la PC del tecnico.
    ///
    /// El device_id viaja porque el cliente local lo necesita, pero el dashboard
    /// no lo muestra: al tecnico le basta el boton.
    /// </summary>
    public override async Task<RemoteSessionReply> StartRemoteSession(
        RemoteSessionRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var user = context.GetHttpContext().User;
        var actor = user.Identity?.Name ?? "desconocido";

        var machine = await machines.GetAsync(request.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        if (!Roles.Satisfies(user.FindFirst(ClaimTypes.Role)?.Value, Roles.Technician))
            await DenyAsync(context, AuditActions.RemoteStart, machine,
                "El control remoto requiere rol technician o superior");

        // El motor que pidio el tecnico, o el configurado si no pidio ninguno.
        IRemoteProvider remote;

        try
        {
            remote = motores.Resolver(request.Provider);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        // Solo los motores que NO arrancan el otro extremo dependen de que haya
        // un producto de terceros instalado alli con su propio identificador. El
        // motor propio direcciona por machine_id, que siempre existe.
        if (!remote.RequiresHostLaunch
            && (!machine.RemoteAvailable || string.IsNullOrWhiteSpace(machine.RemoteDeviceId)))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Esta maquina no tiene motor de control remoto instalado"));
        }

        var sourceIp = context.Peer;

        // Las huerfanas se cierran aqui, sin servicio de fondo.
        await sessions.CloseOrphansAsync(ct);

        // Sesion y auditoria en la misma transaccion. El id que sale de aqui es
        // TAMBIEN el del canal del relay cuando el motor lo usa: asi "alguien
        // controlo esta PC" y el trafico de esa sesion son la misma fila.
        var sessionId = await sessions.StartAsync(
            machine.Id, actor, remote.Provider, machine.RemoteDeviceId ?? string.Empty, sourceIp,
            BuildAudit(context, AuditActions.RemoteStart, machine, AuditEntry.Allowed), ct);

        var viewerTicket = string.Empty;
        var avisado = false;
        var destinatario = string.IsNullOrWhiteSpace(request.ViewerMachineId)
            ? actor
            : request.ViewerMachineId;

        if (remote.RequiresHostLaunch)
        {
            // El del host se ata a la PC controlada; el del viewer, a la del
            // tecnico Y a su usuario. Asi ninguno sirve en el lugar del otro.
            var (hostTicket, host) = remoteTickets.Issue(
                sessionId, DeviceHub.Remote.Contracts.RemoteRole.Host, machine.Id);

            (viewerTicket, _) = remoteTickets.Issue(
                sessionId, DeviceHub.Remote.Contracts.RemoteRole.Viewer, destinatario, actor);

            // Se le ordena al agente que arranque el host en la sesion
            // interactiva. El ticket viaja por el stream del agente, ya
            // autenticado y con el certificado fijado, y de ahi a un named pipe
            // con ACL. Nunca por linea de comandos.
            //
            // Si la PC esta offline el ticket sigue siendo valido y caduca solo:
            // el tecnico se entera por host_notified, no por un error de
            // autorizacion que no lo es.
            avisado = registry.TryPush(machine.Id, new ServerMessage
            {
                RemoteHost = new RemoteHostControl
                {
                    Action = RemoteHostControl.Types.Action.Start,
                    SessionId = sessionId,
                    HostTicket = hostTicket,
                    ExpiresAtUs = host.ExpiresAt.ToUnixTimeMilliseconds() * 1000
                }
            });

            await audit.WriteAsync(BuildAudit(
                context, AuditActions.RemoteRequested, machine, AuditEntry.Allowed,
                $"sesion {sessionId} vence {host.ExpiresAt:O} host={(avisado ? "avisado" : "sin agente")}"), ct);
        }

        var launch = remote.BuildLaunch(new RemoteLaunchContext(
            machine.Id,
            machine.RemoteDeviceId ?? string.Empty,
            sessionId,
            viewerTicket,
            destinatario,
            request.ServerAddress,
            request.ServerPin,
            machine.MachineCode));

        logger.LogWarning("{Actor} abrio sesion remota sobre {MachineCode} desde {Source}",
            actor, machine.MachineCode, sourceIp);

        return new RemoteSessionReply
        {
            SessionId = sessionId,
            Provider = launch.Provider,
            DeviceId = launch.DeviceId,
            LaunchTarget = launch.Target,
            LaunchArguments = launch.Arguments,

            // Va por stdin en el dashboard. No se registra en ningun log.
            ViewerSecret = launch.Secret,
            HostNotified = avisado,
            StartedAt = Timestamp.FromDateTime(DateTime.UtcNow)
        };
    }

    public override async Task<RemoteSessionReply> EndRemoteSession(RemoteSessionRef request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var existing = await sessions.GetAsync(request.SessionId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Sesion desconocida"));

        var machine = await machines.GetAsync(existing.MachineId, ct);

        var session = await sessions.EndAsync(request.SessionId, "closed",
            BuildAudit(context, AuditActions.RemoteEnd, machine, AuditEntry.Allowed), ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Sesion desconocida"));

        var reply = new RemoteSessionReply
        {
            SessionId = session.Id,
            Provider = session.Provider,
            DeviceId = session.DeviceId ?? string.Empty,
            StartedAt = Timestamp.FromDateTime(Db.AsUtc(session.StartedAt))
        };

        if (session.EndedAt is not null)
            reply.EndedAt = Timestamp.FromDateTime(Db.AsUtc(session.EndedAt.Value));

        return reply;
    }

    // ---------------------------------------------------------- comandos (Fase 7)

    /// <summary>
    /// La autorizacion sale de <see cref="CommandPolicy"/>, no de un
    /// [Authorize(Roles=...)] en el metodo: el rol necesario depende del TIPO de
    /// comando, y repartir esa decision en atributos o en `if` por el codigo es
    /// justo lo que hace que un dia alguien pueda apagar 30 PCs siendo Viewer.
    /// </summary>
    public override async Task<CommandEntry> SendCommand(SendCommandRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!CommandPolicy.TryGet(request.Type, out var definition))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Tipo de comando no permitido: {request.Type}"));

        // Sin esto, RunTerminalCommand seria decorativo: bastaria con saltarse la
        // UI y pedir RunShell por aqui para ejecutar sin sesion ni registro.
        if (CommandPolicy.RequiresSession(request.Type))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                $"{request.Type} solo puede emitirse dentro de una sesion de terminal"));

        var user = context.GetHttpContext().User;
        var role = user.FindFirst(ClaimTypes.Role)?.Value;
        var actor = user.Identity?.Name ?? "desconocido";
        var action = AuditActions.ForCommand(request.Type);

        var machine = await machines.GetAsync(request.MachineId, ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Maquina desconocida"));

        // La maquina se resuelve ANTES de comprobar el rol para que la fila de
        // denegacion diga sobre QUE equipo se intento actuar.
        if (!Roles.Satisfies(role, definition.RequiredRole))
            await DenyAsync(context, action, machine,
                $"{request.Type} requiere rol {definition.RequiredRole} o superior");

        var required = CommandPolicy.RequiredParameter(request.Type);
        if (required is not null && !request.Parameters.ContainsKey(required))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Falta el parametro '{required}'"));

        // Acota el dano de un bucle en la UI o de un script mal escrito: nadie
        // encola mil reinicios sobre la misma PC en un minuto.
        if (!limiter.TryAcquire($"cmd:{request.MachineId}", RateLimits.CommandsPerMachine, RateLimits.CommandWindow))
        {
            await audit.WriteAsync(BuildAudit(context, action, machine, AuditEntry.Denied,
                "limite de comandos por minuto excedido"), ct);

            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                $"Maximo {RateLimits.CommandsPerMachine} comandos por minuto y maquina"));
        }

        var details = string.Join(' ', request.Parameters.Select(p => $"{p.Key}={p.Value}"));

        var commandId = await commands.CreateAsync(
            request.MachineId, request.Type, request.Parameters, actor, definition.Ttl,
            BuildAudit(context, action, machine, AuditEntry.Allowed, details), ct);

        var row = await commands.GetAsync(commandId, ct)!;

        // Si el agente esta conectado se entrega ya; si no, queda pendiente y
        // caducara solo. Un RestartMachine no espera dos horas a que vuelva.
        if (registry.TryPush(request.MachineId, new ServerMessage { Command = SummaryMapper.ToRequest(row!) }))
            await commands.MarkSentAsync(commandId, ct);

        logger.LogWarning("{Actor} pidio {Type} sobre {MachineCode} (destructivo: {Destructive})",
            actor, request.Type, machine.MachineCode, definition.IsDestructive);

        return SummaryMapper.ToProto((await commands.GetAsync(commandId, ct))!);
    }

    public override async Task<CommandList> ListCommands(MachineRef request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // Vencimiento perezoso: lo que se muestra tiene que estar al dia.
        await commands.ExpireStaleAsync(request.MachineId, ct);

        var list = new CommandList();
        list.Commands.AddRange((await commands.ListAsync(request.MachineId, 50, ct)).Select(SummaryMapper.ToProto));
        return list;
    }

    public override async Task<CommandEntry> GetCommand(CommandRef request, ServerCallContext context)
    {
        var row = await commands.GetAsync(request.CommandId, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Comando desconocido"));

        return SummaryMapper.ToProto(row);
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
