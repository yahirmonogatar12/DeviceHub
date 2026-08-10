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
