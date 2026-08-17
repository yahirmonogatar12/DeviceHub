using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Contracts;
using DeviceHub.Dashboard.Views;

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

            // Los contadores se recalculan en el mismo tick y no al recibir
            // mensajes: el estado se deriva de last_seen, asi que una PC pasa a
            // OFFLINE sin que llegue nada.
            RefreshCounts();
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

    // ================= Navegacion =================

    /// <summary>Pagina activa del menu lateral: "machines" o "audit".</summary>
    [ObservableProperty] private string _page = "machines";

    /// <summary>Filtro por estado que activan las tarjetas de KPI. Vacio = todos.</summary>
    [ObservableProperty] private string _statusFilter = string.Empty;

    // Al seleccionar una maquina se entra en su ficha a pantalla completa: en un
    // panel lateral de 420 px las pestanas no caben y todo compite por el espacio.
    public bool ShowList => Page == "machines" && SelectedMachine is null;
    public bool ShowDetail => Page == "machines" && SelectedMachine is not null;
    public bool ShowAudit => Page == "audit";

    public int TotalCount => Machines.Count;
    public int OnlineCount => Machines.Count(m => m.Status == MachineStatus.Online);
    public int UnreachableCount => Machines.Count(m => m.Status == MachineStatus.Unreachable);
    public int OfflineCount => Machines.Count(m => m.Status == MachineStatus.Offline);
    public int ConflictCount => Machines.Count(m => m.HasConflict);

    /// <summary>Indicador de la barra superior.</summary>
    public bool ServerOk => IsLoggedIn && string.IsNullOrEmpty(StatusMessage);

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(UnreachableCount));
        OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(ConflictCount));
    }

    /// <summary>El historial se deriva de Detail: cuando llega otro, se repinta.</summary>
    partial void OnDetailChanged(MachineDetail? value)
    {
        OnPropertyChanged(nameof(IpHistoryFiltered));
        OnPropertyChanged(nameof(PlacementHistoryFiltered));
    }

    private void RefreshPages()
    {
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowAudit));
    }

    partial void OnPageChanged(string value)
    {
        RefreshPages();

        if (value == "audit")
            _ = RefreshGlobalAuditAsync();
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(ServerOk));

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(ServerOk));

    /// <summary>Vuelve de la ficha de un equipo al listado.</summary>
    [RelayCommand]
    private void BackToList() => SelectedMachine = null;

    [RelayCommand]
    private void Navigate(string pagina) => Page = pagina;

    /// <summary>
    /// Las tarjetas de KPI SON el filtro por estado: pulsar "Offline" deja solo
    /// las offline y volver a pulsarla las devuelve todas. No hacen falta cuatro
    /// ComboBox que preguntan lo que ya contesta la barra de busqueda.
    /// </summary>
    [RelayCommand]
    private void FilterByStatus(string estado)
        => StatusFilter = StatusFilter == estado ? string.Empty : estado;

    partial void OnStatusFilterChanged(string value) => MachinesView.Refresh();

    partial void OnFilterChanged(string value) => MachinesView.Refresh();

    partial void OnSelectedMachineChanged(MachineViewModel? value)
    {
        Detail = null;
        RefreshPages();

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

                    RefreshCounts();
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

    /// <summary>
    /// Renombrar / mover en un dialogo. Antes eran seis TextBox siempre visibles
    /// en el panel de detalle: ocupaban mas que los datos del equipo para algo
    /// que se hace una vez en la vida de la PC.
    /// </summary>
    [RelayCommand]
    private async Task EditMachineAsync()
    {
        if (SelectedMachine is null)
            return;

        var dialogo = new EditMachineWindow
        {
            DataContext = this,
            Owner = Application.Current?.MainWindow
        };

        if (dialogo.ShowDialog() == true)
            await MoveMachineAsync();
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

    /// <summary>
    /// La salida cuando una PC dejo de conectar porque su token ya no vale.
    ///
    /// Antes esto obligaba a ir fisicamente hasta la maquina a editar
    /// machine.json. El agente lo intenta cada minuto por su cuenta, asi que
    /// entra sola en cuanto se pulsa aqui.
    /// </summary>
    [RelayCommand]
    private async Task AuthorizeReenrollmentAsync()
    {
        if (SelectedMachine is null)
            return;

        if (MessageBox.Show(
                $"{SelectedMachine.MachineCode} podra reasociarse sin recovery code durante 10 minutos.\n\n" +
                "Solo para una PC que dejo de conectar. Continuar?",
                "Reasociar maquina", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Detail = await _client.AuthorizeReenrollmentAsync(SelectedMachine.MachineId, CancellationToken.None);
            StatusMessage = $"{SelectedMachine.MachineCode} puede reasociarse durante 10 minutos";
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
    }

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
        if (item is not MachineViewModel machine)
            return true;

        if (StatusFilter.Length > 0)
        {
            var coincide = StatusFilter == "CONFLICT"
                ? machine.HasConflict
                : machine.StatusText == StatusFilter;

            if (!coincide)
                return false;
        }

        if (string.IsNullOrWhiteSpace(Filter))
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
