using DeviceHub.Contracts;
using DeviceHub.Server;
using DeviceHub.Server.Data;
using DeviceHub.Server.Realtime;
using DeviceHub.Server.Security;
using DeviceHub.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Regla 12: corre como servicio, sin sesion iniciada.
builder.Services.AddWindowsService(options => options.ServiceName = "DeviceHubServer");

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
var options = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();

// Regla 4: la cadena de conexion no vive en un archivo versionado.
var connectionString = Environment.GetEnvironmentVariable("DEVICEHUB_DB_CONNECTION")
    ?? (string.IsNullOrWhiteSpace(options.ConnectionString) ? null : options.ConnectionString)
    ?? throw new InvalidOperationException(
        "Falta la cadena de conexion. Define DEVICEHUB_DB_CONNECTION o DeviceHub:ConnectionString.");

using var bootstrapLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("DeviceHub.Bootstrap");

// Bootstrap sin GUI: generar un codigo de enrolamiento exigia abrir el dashboard,
// lo que impide guionizar un despliegue. Instalar en 80 PCs no puede depender de
// que alguien copie y pegue de una ventana.
if (args.Contains("--enrollment-code"))
{
    var db = new Db(connectionString);
    var repository = new MachineRepository(db);
    var siteCode = ArgValue(args, "--site") ?? options.DefaultSiteCode;

    var siteId = await repository.GetSiteIdAsync(siteCode, CancellationToken.None);

    if (siteId is null)
    {
        bootstrapLogger.LogError("Sitio desconocido: {SiteCode}. Arranca el servidor una vez para aplicar las migraciones.", siteCode);
        return 1;
    }

    var uses = int.TryParse(ArgValue(args, "--uses"), out var parsedUses) ? Math.Max(1, parsedUses) : 1;
    var minutes = int.TryParse(ArgValue(args, "--minutes"), out var parsedMinutes) ? Math.Clamp(parsedMinutes, 5, 120) : 30;
    var code = Secrets.NewEnrollmentCode();

    await new EnrollmentRepository(db).CreateAsync(
        Secrets.Sha256Hex(code), siteId.Value, "cli", DateTime.UtcNow.AddMinutes(minutes), uses, null, CancellationToken.None);

    Console.WriteLine(code);
    bootstrapLogger.LogInformation("Codigo para {SiteCode}: {Uses} uso(s), vence en {Minutes} min", siteCode, uses, minutes);
    return 0;
}

// Diagnostico de campo: "esta PC responde a comandos?" sin abrir el dashboard.
// El servidor en marcha lo entrega en el siguiente latido.
if (args.Contains("--command"))
{
    var db = new Db(connectionString);
    var repository = new MachineRepository(db);
    var commandRepository = new CommandRepository(db);

    if (!System.Enum.TryParse<CommandType>(ArgValue(args, "--command"), ignoreCase: true, out var type)
        || !CommandPolicy.TryGet(type, out var definition))
    {
        bootstrapLogger.LogError("Tipo invalido. Permitidos: {Types}",
            string.Join(", ", CommandPolicy.All.Select(d => d.Type)));
        return 1;
    }

    var machineCode = ArgValue(args, "--machine");
    var machine = machineCode is null ? null : await repository.GetByCodeAsync(machineCode, CancellationToken.None);

    if (machine is null)
    {
        bootstrapLogger.LogError("Maquina desconocida: {MachineCode}", machineCode);
        return 1;
    }

    var parameters = new Dictionary<string, string>();
    if (ArgValue(args, "--param") is { } raw && raw.Split('=', 2) is [var key, var value])
        parameters[key] = value;

    var id = await commandRepository.CreateAsync(
        machine.Id, type, parameters, "cli", definition.Ttl, CancellationToken.None);

    bootstrapLogger.LogInformation("{Type} encolado para {MachineCode} ({CommandId})", type, machine.MachineCode, id);

    var deadline = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(1000);
        var row = await commandRepository.GetAsync(id, CancellationToken.None);

        if (row is null || row.Status is "pending" or "sent" or "running")
            continue;

        Console.WriteLine($"{row.Status.ToUpperInvariant()}: {row.Result} {row.ErrorCode}".Trim());
        return row.Status == "completed" ? 0 : 1;
    }

    bootstrapLogger.LogError("Sin respuesta en 90 s");
    return 1;
}

static string? ArgValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var certificate = CertificateProvider.LoadOrCreate(options.DataDirectory, bootstrapLogger);
var jwtKeyProvider = new JwtKeyProvider(options.DataDirectory);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(options.Port, listen =>
{
    listen.Protocols = HttpProtocols.Http2; // gRPC
    listen.UseHttps(certificate);
}));

builder.Services.AddGrpc();

builder.Services.AddSingleton(new Db(connectionString));
builder.Services.AddSingleton<MachineRepository>();
builder.Services.AddSingleton<EnrollmentRepository>();
builder.Services.AddSingleton<CommandRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<MachineBroadcaster>();
builder.Services.AddSingleton(jwtKeyProvider);
builder.Services.AddSingleton(new ServerPins([PublicKeyPin.Compute(certificate)]));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt => jwt.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = options.JwtIssuer,
        ValidAudience = options.JwtIssuer,
        IssuerSigningKey = jwtKeyProvider.SigningKey,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    });

builder.Services.AddAuthorization();

builder.Services.AddHostedService<MetricsRetentionService>();

var app = builder.Build();

// Regla 9: el esquema se pone al dia solo, antes de aceptar una sola conexion.
try
{
    Migrator.Run(connectionString, app.Logger);
    await Bootstrap.EnsureAdminUserAsync(app.Services.GetRequiredService<UserRepository>(), app.Logger);
}
catch (Exception ex)
{
    // Un servicio de Windows que muere con un stack trace deja al operador sin
    // pista. Casi siempre es la cadena de conexion.
    app.Logger.LogCritical("No se pudo preparar la base de datos: {Message}", ex.Message);
    app.Logger.LogCritical("Revisa DEVICEHUB_DB_CONNECTION (host, puerto, usuario y permisos para crear el schema).");
    return 1;
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<AgentGrpcService>();
app.MapGrpcService<AdminGrpcService>();

app.Logger.LogInformation("DeviceHub Server escuchando en https://0.0.0.0:{Port} (HTTP/2)", options.Port);

app.Run();
return 0;
