using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DeviceHub.Contracts;
using Grpc.Core;
using Grpc.Net.Client;

namespace DeviceHub.Dashboard;

public sealed class DashboardSettings
{
    public string ServerHost { get; set; } = "localhost";
    public int ServerPort { get; set; } = 5443;
    /// <summary>Pin SPKI del servidor. Vacio = se acepta el primero visto (TOFU).</summary>
    public string ServerPin { get; set; } = string.Empty;

    public static DashboardSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(path))
            return new DashboardSettings();

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        return document.RootElement.TryGetProperty("DeviceHub", out var section)
            ? section.Deserialize<DashboardSettings>() ?? new DashboardSettings()
            : new DashboardSettings();
    }
}

/// <summary>
/// Cliente gRPC del dashboard. Valida al servidor por pin de clave publica,
/// igual que el agente: sin CA interna que mantener.
/// </summary>
public sealed class DeviceHubClient : IDisposable
{
    private readonly DashboardSettings _settings;
    private readonly GrpcChannel _channel;
    private readonly AdminService.AdminServiceClient _client;
    private Metadata _auth = [];

    public DeviceHubClient(DashboardSettings settings)
    {
        _settings = settings;

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                        return false;

                    if (string.IsNullOrWhiteSpace(_settings.ServerPin))
                        return true; // TOFU: el operador confia en la primera conexion

                    using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    return PublicKeyPin.Compute(cert) == _settings.ServerPin;
                }
            }
        };

        _channel = GrpcChannel.ForAddress(
            $"https://{settings.ServerHost}:{settings.ServerPort}",
            new GrpcChannelOptions { HttpHandler = handler });

        _client = new AdminService.AdminServiceClient(_channel);
    }

    public string Username { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public bool IsAdministrator => Role == "administrator";

    public async Task LoginAsync(string username, string password, CancellationToken ct)
    {
        var reply = await _client.LoginAsync(
            new LoginRequest { Username = username, Password = password }, cancellationToken: ct);

        Username = reply.Username;
        Role = reply.Role;
        _auth = new Metadata { { "authorization", $"Bearer {reply.Token}" } };
    }

    public async IAsyncEnumerable<MachineSummary> WatchAsync(
        string siteCode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _client.WatchMachines(new WatchRequest { SiteCode = siteCode }, _auth, cancellationToken: ct);

        await foreach (var summary in call.ResponseStream.ReadAllAsync(ct))
            yield return summary;
    }

    public Task<MachineDetail> GetMachineAsync(string machineId, CancellationToken ct)
        => _client.GetMachineAsync(new MachineRef { MachineId = machineId }, _auth, cancellationToken: ct).ResponseAsync;

    public Task<MachineDetail> MoveMachineAsync(MoveMachineRequest request, CancellationToken ct)
        => _client.MoveMachineAsync(request, _auth, cancellationToken: ct).ResponseAsync;

    public Task<EnrollmentCodeReply> CreateEnrollmentCodeAsync(CreateEnrollmentCodeRequest request, CancellationToken ct)
        => _client.CreateEnrollmentCodeAsync(request, _auth, cancellationToken: ct).ResponseAsync;

    public Task<MachineDetail> ResolveConflictAsync(ResolveConflictRequest request, CancellationToken ct)
        => _client.ResolveIdentityConflictAsync(request, _auth, cancellationToken: ct).ResponseAsync;

    public Task<CommandEntry> SendCommandAsync(SendCommandRequest request, CancellationToken ct)
        => _client.SendCommandAsync(request, _auth, cancellationToken: ct).ResponseAsync;

    public Task<CommandEntry> GetCommandAsync(string commandId, CancellationToken ct)
        => _client.GetCommandAsync(new CommandRef { CommandId = commandId }, _auth, cancellationToken: ct).ResponseAsync;

    public Task<CommandList> ListCommandsAsync(string machineId, CancellationToken ct)
        => _client.ListCommandsAsync(new MachineRef { MachineId = machineId }, _auth, cancellationToken: ct).ResponseAsync;

    public void Dispose() => _channel.Dispose();
}
