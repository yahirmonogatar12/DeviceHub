using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Contracts;

namespace DeviceHub.Dashboard;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceHubClient _client;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _watch;

    public MainViewModel(DeviceHubClient client)
    {
        _client = client;

        MachinesView = CollectionViewSource.GetDefaultView(Machines);
        MachinesView.Filter = MatchesFilter;

        // El paso del tiempo cambia el estado aunque no llegue ningun mensaje:
        // una PC que se apaga deja de emitir, no emite un "me apague".
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) =>
        {
            foreach (var machine in Machines)
                machine.RefreshDerived();
        };
        _clock.Start();
    }

    public ObservableCollection<MachineViewModel> Machines { get; } = [];

    public ICollectionView MachinesView { get; }

    [ObservableProperty] private string _username = "admin";
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _sessionLabel = string.Empty;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private MachineViewModel? _selectedMachine;
    [ObservableProperty] private MachineDetail? _detail;

    // Campos editables del panel de detalle (renombrar / mover).
    [ObservableProperty] private string _editSiteCode = string.Empty;
    [ObservableProperty] private string _editMachineCode = string.Empty;
    [ObservableProperty] private string _editDisplayName = string.Empty;
    [ObservableProperty] private string _editArea = string.Empty;
    [ObservableProperty] private string _editLine = string.Empty;
    [ObservableProperty] private string _editStation = string.Empty;

    public bool IsAdministrator => _client.IsAdministrator;

    partial void OnFilterChanged(string value) => MachinesView.Refresh();

    partial void OnSelectedMachineChanged(MachineViewModel? value)
    {
        Detail = null;

        if (value is null)
            return;

        EditSiteCode = value.SiteCode;
        EditMachineCode = value.MachineCode;
        EditDisplayName = value.DisplayName;
        EditArea = value.Area;
        EditLine = value.Line;
        EditStation = value.Station;

        _ = LoadDetailAsync(value.MachineId);
    }

    [RelayCommand]
    private async Task LoginAsync(object? passwordBox)
    {
        var password = (passwordBox as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;

        IsBusy = true;
        StatusMessage = "Conectando...";

        try
        {
            await _client.LoginAsync(Username, password, CancellationToken.None);

            IsLoggedIn = true;
            SessionLabel = $"{_client.Username} ({_client.Role})";
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(IsAdministrator));

            _watch = new CancellationTokenSource();
            _ = WatchAsync(_watch.Token);
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Estado inicial + cambios por streaming. Si el stream cae, reintenta: el
    /// servidor puede reiniciarse sin que haya que reabrir el dashboard.
    /// </summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var summary in _client.WatchAsync(string.Empty, ct))
                {
                    var existing = Machines.FirstOrDefault(m => m.MachineId == summary.MachineId);

                    if (existing is null)
                    {
                        Machines.Add(new MachineViewModel(summary));
                        MachinesView.Refresh();
                    }
                    else
                    {
                        existing.Update(summary);

                        if (SelectedMachine?.MachineId == summary.MachineId)
                            _ = LoadDetailAsync(summary.MachineId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Conexion perdida: {Describe(ex)}";
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LoadDetailAsync(string machineId)
    {
        try
        {
            Detail = await _client.GetMachineAsync(machineId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
    }

    [RelayCommand]
    private async Task CreateEnrollmentCodeAsync()
    {
        try
        {
            var reply = await _client.CreateEnrollmentCodeAsync(
                new CreateEnrollmentCodeRequest { MaxUses = 1, ValidMinutes = 30 }, CancellationToken.None);

            Clipboard.SetText(reply.Code);

            MessageBox.Show(
                $"Codigo: {reply.Code}\n\nCopiado al portapapeles.\nVence: {reply.ExpiresAt.ToDateTime().ToLocalTime():HH:mm}\nUsos: {reply.MaxUses}\n\nNo se vuelve a mostrar.",
                "Codigo de enrolamiento", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
    }

    /// <summary>Renombrar y mover: machineId intacto, historial conservado.</summary>
    [RelayCommand]
    private async Task MoveMachineAsync()
    {
        if (SelectedMachine is null)
            return;

        try
        {
            Detail = await _client.MoveMachineAsync(new MoveMachineRequest
            {
                MachineId = SelectedMachine.MachineId,
                SiteCode = EditSiteCode,
                MachineCode = EditMachineCode,
                DisplayName = EditDisplayName,
                Area = EditArea,
                Line = EditLine,
                Station = EditStation
            }, CancellationToken.None);

            StatusMessage = $"Guardado: {EditMachineCode}";
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
    }

    [RelayCommand]
    private Task ApproveHardwareAsync()
        => ResolveAsync(ResolveConflictRequest.Types.Resolution.ApproveNewHardware,
            "Se adoptara el hardware nuevo (cambio legitimo de placa). Continuar?");

    [RelayCommand]
    private Task IssueNewIdentityAsync()
        => ResolveAsync(ResolveConflictRequest.Types.Resolution.IssueNewIdentity,
            "Se invalidara el token: AMBOS agentes necesitaran un recovery code. Continuar?");

    private async Task ResolveAsync(ResolveConflictRequest.Types.Resolution resolution, string confirmation)
    {
        if (SelectedMachine is null)
            return;

        if (MessageBox.Show(confirmation, "Conflicto de identidad",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            Detail = await _client.ResolveConflictAsync(new ResolveConflictRequest
            {
                MachineId = SelectedMachine.MachineId,
                Resolution = resolution
            }, CancellationToken.None);

            StatusMessage = "Conflicto resuelto";
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
    }

    private bool MatchesFilter(object item)
    {
        if (string.IsNullOrWhiteSpace(Filter) || item is not MachineViewModel machine)
            return true;

        var needle = Filter.Trim();

        return Contains(machine.MachineCode, needle)
            || Contains(machine.Hostname, needle)
            || Contains(machine.CurrentIp, needle)
            || Contains(machine.Area, needle)
            || Contains(machine.Line, needle)
            || Contains(machine.StatusText, needle);

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(Exception ex)
        => ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message;

    public void Shutdown() => _watch?.Cancel();
}
