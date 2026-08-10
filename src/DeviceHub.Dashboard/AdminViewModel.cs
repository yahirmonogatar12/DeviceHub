using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Contracts;

namespace DeviceHub.Dashboard;

/// <summary>Filas devueltas por GetProcesses / GetServices (JSON en el resultado).</summary>
public sealed record ProcessRow(string Name, int Pid, long MemoryBytes, double CpuPercent)
{
    public string Memory => $"{MemoryBytes / 1024d / 1024d:0} MB";
    public string Cpu => $"{CpuPercent:0.#}%";
}

public sealed record ServiceRow(string Name, string DisplayName, string Status, string StartType);

/// <summary>Una linea de auditoria lista para mostrar (Fase 12).</summary>
public sealed record AuditLine(string When, string Who, string Action, string Outcome, string Details)
{
    public bool IsDenied => Outcome == "denied";

    public static AuditLine From(AuditRecord record) => new(
        When: record.OccurredAt is null
            ? "-"
            : DateTime.SpecifyKind(record.OccurredAt.ToDateTime(), DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm:ss"),
        Who: string.IsNullOrEmpty(record.UserRole) ? record.UserId : $"{record.UserId} ({record.UserRole})",
        Action: record.Action,
        Outcome: record.Outcome,
        Details: string.Join("  ", new[] { record.Details, record.SourceIp }.Where(s => !string.IsNullOrEmpty(s))));
}

/// <summary>
/// Parte de administracion del dashboard (Fases 7-9). Vive aparte del listado de
/// maquinas porque son responsabilidades distintas: una muestra estado, la otra
/// ejecuta acciones sobre PCs reales.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ObservableCollection<ServiceRow> Services { get; } = [];
    public ObservableCollection<AuditLine> Audit { get; } = [];

    /// <summary>Auditoria de la maquina seleccionada (Fase 12).</summary>
    [RelayCommand]
    private async Task RefreshAuditAsync()
    {
        if (SelectedMachine is null)
            return;

        try
        {
            var list = await _client.ListAuditAsync(SelectedMachine.MachineId, CancellationToken.None);

            Audit.Clear();
            foreach (var entry in list.Entries)
                Audit.Add(AuditLine.From(entry));

            CommandFeedback = $"{Audit.Count} eventos auditados";
        }
        catch (Exception ex)
        {
            CommandFeedback = Describe(ex);
        }
    }

    [ObservableProperty]
    private string _commandFeedback = string.Empty;

    [ObservableProperty]
    private ProcessRow? _selectedProcess;

    [ObservableProperty]
    private ServiceRow? _selectedService;

    /// <summary>
    /// Control remoto (Fase 10). El servidor autoriza, registra la sesion y
    /// devuelve QUE ejecutar; aqui solo se lanza el cliente local.
    ///
    /// Nada de esto nombra a RustDesk: si la Fase 18 cambia de motor, este
    /// metodo no se toca.
    /// </summary>
    [RelayCommand]
    private async Task RemoteControlAsync()
    {
        if (SelectedMachine is null)
            return;

        try
        {
            CommandFeedback = "Abriendo sesion remota...";
            var session = await _client.StartRemoteSessionAsync(SelectedMachine.MachineId, CancellationToken.None);

            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(session.LaunchTarget, session.LaunchArguments)
                {
                    UseShellExecute = true
                });

            if (process is null)
            {
                await _client.EndRemoteSessionAsync(session.SessionId, CancellationToken.None);
                CommandFeedback = $"No se pudo lanzar el cliente ({session.LaunchTarget}). Instalalo en esta PC.";
                return;
            }

            _remoteSessionId = session.SessionId;
            CommandFeedback = $"Sesion remota abierta sobre {SelectedMachine.MachineCode}";
        }
        catch (Exception ex)
        {
            CommandFeedback = Describe(ex);
        }
    }

    [RelayCommand]
    private async Task EndRemoteSessionAsync()
    {
        if (_remoteSessionId is null)
            return;

        try
        {
            await _client.EndRemoteSessionAsync(_remoteSessionId, CancellationToken.None);
            CommandFeedback = "Sesion remota cerrada";
        }
        catch (Exception ex)
        {
            CommandFeedback = Describe(ex);
        }
        finally
        {
            _remoteSessionId = null;
        }
    }

    private string? _remoteSessionId;

    [RelayCommand]
    private async Task PingAsync()
    {
        var entry = await RunAsync(CommandType.Ping);

        if (entry is not null)
            CommandFeedback = entry.Result;
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        var entry = await RunAsync(CommandType.GetProcesses);

        if (entry?.Status != CommandStatus.Completed)
            return;

        Processes.Clear();
        foreach (var row in Deserialize<ProcessRow>(entry.Result))
            Processes.Add(row);

        CommandFeedback = $"{Processes.Count} procesos";
    }

    [RelayCommand]
    private async Task RefreshServicesAsync()
    {
        var entry = await RunAsync(CommandType.GetServices);

        if (entry?.Status != CommandStatus.Completed)
            return;

        Services.Clear();
        foreach (var row in Deserialize<ServiceRow>(entry.Result))
            Services.Add(row);

        CommandFeedback = $"{Services.Count} servicios";
    }

    [RelayCommand]
    private async Task KillProcessAsync()
    {
        if (SelectedProcess is null || !Confirm($"Matar {SelectedProcess.Name} (PID {SelectedProcess.Pid})?"))
            return;

        var entry = await RunAsync(CommandType.KillProcess,
            new Dictionary<string, string> { ["pid"] = SelectedProcess.Pid.ToString() });

        if (entry is not null)
            CommandFeedback = entry.Result;

        await RefreshProcessesAsync();
    }

    [RelayCommand]
    private async Task ServiceActionAsync(string action)
    {
        if (SelectedService is null)
            return;

        var type = action switch
        {
            "start" => CommandType.StartService,
            "stop" => CommandType.StopService,
            _ => CommandType.RestartService
        };

        if (type != CommandType.StartService && !Confirm($"{action.ToUpperInvariant()} sobre {SelectedService.DisplayName}?"))
            return;

        var entry = await RunAsync(type, new Dictionary<string, string> { ["service"] = SelectedService.Name });

        if (entry is not null)
            CommandFeedback = entry.Result;

        await RefreshServicesAsync();
    }

    [RelayCommand]
    private Task RestartMachineAsync()
        => PowerActionAsync(CommandType.RestartMachine,
            "Reiniciar esta PC?\n\nSi hay un operador trabajando, pierde lo que no haya guardado.");

    [RelayCommand]
    private Task ShutdownMachineAsync()
        => PowerActionAsync(CommandType.ShutdownMachine,
            "APAGAR esta PC?\n\nNadie podra volver a encenderla en remoto: hace falta ir fisicamente al equipo.");

    private async Task PowerActionAsync(CommandType type, string confirmation)
    {
        if (!Confirm(confirmation))
            return;

        var entry = await RunAsync(type);

        if (entry is not null)
            CommandFeedback = entry.Result;
    }

    /// <summary>
    /// Envia y espera. Se hace polling y no streaming porque un comando dura
    /// segundos y el dashboard ya tiene abierto un stream para el estado: montar
    /// un segundo canal para esto no se paga.
    /// </summary>
    private async Task<CommandEntry?> RunAsync(CommandType type, Dictionary<string, string>? parameters = null)
    {
        if (SelectedMachine is null)
            return null;

        try
        {
            CommandFeedback = $"{type}...";

            var request = new SendCommandRequest { MachineId = SelectedMachine.MachineId, Type = type };

            foreach (var (key, value) in parameters ?? [])
                request.Parameters[key] = value;

            var entry = await _client.SendCommandAsync(request, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(45);

            while (!IsTerminal(entry.Status) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                entry = await _client.GetCommandAsync(entry.CommandId, CancellationToken.None);
            }

            if (!IsTerminal(entry.Status))
                CommandFeedback = $"{type}: sin respuesta (la maquina puede estar desconectada)";
            else if (entry.Status != CommandStatus.Completed)
                CommandFeedback = $"{type}: {entry.Status} {entry.ErrorCode} {entry.Result}".Trim();

            return entry;
        }
        catch (Exception ex)
        {
            CommandFeedback = Describe(ex);
            return null;
        }
    }

    private static bool IsTerminal(CommandStatus status)
        => status is CommandStatus.Completed or CommandStatus.Failed
            or CommandStatus.Expired or CommandStatus.Cancelled;

    private static IEnumerable<T> Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Confirm(string message)
        => MessageBox.Show(message, "Confirmar accion", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
}
