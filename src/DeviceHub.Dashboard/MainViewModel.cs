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

    /// <summary>
    /// Area y linea, en dos listas que salen de los equipos que HAY.
    ///
    /// No de un catalogo: si alguien mueve una PC a un area nueva, la lista
    /// tiene que ofrecerla sin que nadie la de de alta en ningun sitio.
    /// </summary>
    public const string Todas = "Todas";

    public ObservableCollection<string> Areas { get; } = [Todas];
    public ObservableCollection<string> Lineas { get; } = [Todas];

    [ObservableProperty] private string _areaFilter = Todas;
    [ObservableProperty] private string _lineFilter = Todas;

    // DOS PASOS, no uno. Al elegir una maquina se abre un panel al lado con lo
    // que se pregunta el 90 % de las veces -- si esta viva, que IP tiene, quien
    // la esta usando, y conectarse -- SIN perder la lista de vista. La ficha
    // entera, con sus seis pestanas de hardware, procesos, servicios y
    // auditoria, se abre solo si alguien la pide.
    //
    // Antes un clic te sacaba de la lista, y volver costaba otro clic y una
    // recarga. Con veinte PCs y una ronda de revision eso son cuarenta clics
    // para mirar veinte IPs.
    public bool ShowList => Page == "machines" && !DetailOpen;
    public bool ShowQuickPanel => Page == "machines" && SelectedMachine is not null && !DetailOpen;
    public bool ShowDetail => Page == "machines" && SelectedMachine is not null && DetailOpen;
    public bool ShowAudit => Page == "audit";

    /// <summary>Si se pidio la ficha entera. Se apaga sola al cambiar de maquina:
    /// elegir otra fila vuelve al panel rapido, no a la pestaña de la anterior.</summary>
    [ObservableProperty] private bool _detailOpen;

    partial void OnDetailOpenChanged(bool value) => RefreshPages();

    [RelayCommand]
    private void OpenDetail() => DetailOpen = SelectedMachine is not null;

    [RelayCommand]
    private void CloseQuickPanel() => SelectedMachine = null;

    /// <summary>
    /// Las de baja NO cuentan como equipos.
    ///
    /// Siguen en la lista de dentro para poder reactivarlas, pero un "6
    /// registrados" que incluya tres PCs que ya no existen es un numero que no
    /// sirve para nada.
    /// </summary>
    public int TotalCount => Machines.Count(m => !m.Retired);

    public int RetiredCount => Machines.Count(m => m.Retired);

    /// <summary>Solo un administrador, y solo sobre una que siga en servicio.</summary>
    public bool PuedeDarDeBaja => IsAdministrator && SelectedMachine is { Retired: false };
    public int OnlineCount => Machines.Count(m => !m.Retired && m.Status == MachineStatus.Online);
    public int UnreachableCount => Machines.Count(m => !m.Retired && m.Status == MachineStatus.Unreachable);
    public int OfflineCount => Machines.Count(m => !m.Retired && m.Status == MachineStatus.Offline);
    public int ConflictCount => Machines.Count(m => m.HasConflict);

    /// <summary>Indicador de la barra superior.</summary>
    /// <summary>
    /// El punto de arriba dice si el SERVIDOR responde, no si hubo un error.
    ///
    /// Estaba atado a que StatusMessage estuviera vacio, asi que cualquier
    /// mensaje lo ponia en rojo -- incluido un fallo del portapapeles, que pasa
    /// entero en esta PC y no dice nada del servidor. Se miraba el indicador y
    /// se buscaba una caida que no existia.
    /// </summary>
    public bool ServerOk => IsLoggedIn && !_servidorCaido;

    private bool _servidorCaido;

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(UnreachableCount));
        OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(RetiredCount));
    }

    /// <summary>
    /// Mete en la lista lo que acaba de contestar el servidor.
    ///
    /// HACE FALTA porque el stream de novedades lo alimentan los AGENTES: manda
    /// un resumen cuando una PC informa de algo. Una PC dada de baja
    /// precisamente deja de informar -- se le corta el stream en el acto -- asi
    /// que la novedad mas importante de todas era justo la que no iba a llegar
    /// nunca, y la fila se quedaba en pantalla como si nada hubiera pasado.
    /// </summary>
    private void Aplicar(MachineSummary resumen)
    {
        var existente = Machines.FirstOrDefault(m => m.MachineId == resumen.MachineId);

        if (existente is null)
            return;

        existente.Update(resumen);

        RefrescarAreasYLineas();
        RefreshCounts();
        MachinesView.Refresh();
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
        OnPropertyChanged(nameof(ShowQuickPanel));
        OnPropertyChanged(nameof(PuedeDarDeBaja));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowAudit));
    }

    partial void OnPageChanged(string value)
    {
        RefreshPages();

        if (value == "audit")
            _ = RefreshGlobalAuditAsync();
    }

    partial void OnStatusMessageChanged(string value)
    {
        // Sin mensaje no hay nada de que preocuparse; con mensaje, ya decidio
        // Reportar si el culpable era el servidor.
        if (value.Length == 0)
            _servidorCaido = false;

        OnPropertyChanged(nameof(ServerOk));
    }

    /// <summary>Un error a la vista, diciendo ademas de quien fue la culpa.</summary>
    private void Reportar(Exception ex)
    {
        _servidorCaido = ex is Grpc.Core.RpcException;
        StatusMessage = Describe(ex);
    }

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(ServerOk));

    /// <summary>Vuelve de la ficha de un equipo al listado.</summary>
    [RelayCommand]
    private void BackToList()
    {
        // Vuelve a la LISTA, no al panel rapido: quien cierra la ficha entera
        // quiere la tabla, no otro panel encima de ella.
        DetailOpen = false;
        SelectedMachine = null;
    }

    [RelayCommand]
    private void Navigate(string pagina) => Page = pagina;

    /// <summary>
    /// Da de baja la maquina elegida. Pregunta antes: le quita el token, asi que
    /// para volver hay que reenrolarla.
    /// </summary>
    [RelayCommand]
    private async Task RetireMachineAsync()
    {
        if (SelectedMachine is not { } maquina)
            return;

        var respuesta = MessageBox.Show(
            $"Dar de baja {maquina.MachineCode}?\n\n" +
            "Se le quita el token: esa PC no vuelve a conectarse y sale de la lista.\n" +
            "El historial se conserva entero, y se puede reactivar.\n\n" +
            "Para que vuelva a conectarse habra que reenrolarla.",
            "Dar de baja", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (respuesta != MessageBoxResult.Yes)
            return;

        try
        {
            var detalle = await _client.RetireMachineAsync(
                new MachineRef { MachineId = maquina.MachineId }, CancellationToken.None);

            Aplicar(detalle.Summary);

            CommandFeedback = $"{maquina.MachineCode} dada de baja.";
            SelectedMachine = null;
        }
        catch (Exception ex)
        {
            Reportar(ex);
        }
    }

    [RelayCommand]
    private async Task RestoreMachineAsync()
    {
        if (SelectedMachine is not { } maquina)
            return;

        try
        {
            var detalle = await _client.RestoreMachineAsync(
                new MachineRef { MachineId = maquina.MachineId }, CancellationToken.None);

            Aplicar(detalle.Summary);

            CommandFeedback =
                $"{maquina.MachineCode} reactivada. Sigue sin token: para que conecte, reenrolala.";
        }
        catch (Exception ex)
        {
            Reportar(ex);
        }
    }

    /// <summary>
    /// Las fichas de estado SON el filtro: pulsar "Offline" deja solo las
    /// offline y volver a pulsarla las devuelve todas.
    /// </summary>
    [RelayCommand]
    private void FilterByStatus(string estado)
        => StatusFilter = StatusFilter == estado ? string.Empty : estado;

    partial void OnStatusFilterChanged(string value) => MachinesView.Refresh();

    partial void OnFilterChanged(string value) => MachinesView.Refresh();

    partial void OnAreaFilterChanged(string value) => MachinesView.Refresh();

    partial void OnLineFilterChanged(string value) => MachinesView.Refresh();

    /// <summary>Rehace las dos listas conservando lo que el tecnico tenia
    /// elegido: que llegue un equipo nuevo no puede deshacerle el filtro.</summary>
    private void RefrescarAreasYLineas()
    {
        // Asignar la PROPIEDAD y no el campo: el setter generado ya avisa y
        // refresca la vista, y solo cuando el valor cambia de verdad.
        AreaFilter = Rellenar(Areas, Machines.Select(m => m.Area), AreaFilter);
        LineFilter = Rellenar(Lineas, Machines.Select(m => m.Line), LineFilter);

        static string Rellenar(
            ObservableCollection<string> destino, IEnumerable<string> valores, string elegido)
        {
            var nuevas = valores
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Prepend(Todas)
                .ToList();

            if (nuevas.SequenceEqual(destino, StringComparer.Ordinal))
                return elegido;

            destino.Clear();

            foreach (var valor in nuevas)
                destino.Add(valor);

            // Si lo elegido ya no existe -- se movio la ultima PC de esa linea --
            // se vuelve a Todas, en vez de dejar la lista filtrada por algo que
            // ya no aparece en el desplegable.
            return destino.Contains(elegido, StringComparer.Ordinal) ? elegido : Todas;
        }
    }

    partial void OnSelectedMachineChanged(MachineViewModel? value)
    {
        Detail = null;
        DetailOpen = false;
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
            Reportar(ex);
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
                        RefrescarAreasYLineas();
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
            Reportar(ex);
        }
    }

    [RelayCommand]
    private async Task CreateEnrollmentCodeAsync()
    {
        try
        {
            var reply = await _client.CreateEnrollmentCodeAsync(
                new CreateEnrollmentCodeRequest { MaxUses = 1, ValidMinutes = 30 }, CancellationToken.None);

            // PRIMERO SE COPIA, PERO SI FALLA SE ENSENA IGUAL.
            //
            // El codigo ya existe en el servidor en cuanto vuelve esta llamada:
            // es una fila de enrollment_codes con su caducidad y sus usos. Antes,
            // un fallo del portapapeles saltaba al catch y el codigo no se
            // llegaba a mostrar NUNCA -- se quedaba emitido, sin usar, y el
            // tecnico volvia a pulsar y gastaba otro. Cada hipo del portapapeles
            // quemaba un codigo en silencio.
            var copiado = Copiar(reply.Code);

            MessageBox.Show(
                $"Codigo: {reply.Code}\n\n" +
                (copiado
                    ? "Copiado al portapapeles."
                    : "NO se pudo copiar al portapapeles: copialo de aqui a mano.") +
                $"\nVence: {reply.ExpiresAt.ToDateTime().ToLocalTime():HH:mm}\nUsos: {reply.MaxUses}\n\nNo se vuelve a mostrar.",
                "Codigo de enrolamiento", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Reportar(ex);
        }
    }

    /// <summary>
    /// Copia con reintentos. Devuelve false si no se pudo.
    ///
    /// El portapapeles de Windows es un recurso EXCLUSIVO de toda la sesion: lo
    /// abre un proceso a la vez, y cualquiera puede tenerlo tomado medio
    /// segundo -- el historial del portapapeles, un gestor de contrasenas, otra
    /// aplicacion copiando. De ahi sale CLIPBRD_E_CANT_OPEN, que no significa
    /// que algo este roto sino que hay que volver a intentarlo.
    ///
    /// Diez intentos separados 60 ms: medio segundo largo, que es mas de lo que
    /// dura cualquiera de esas retenciones, y sigue siendo imperceptible.
    /// </summary>
    private static bool Copiar(string texto)
    {
        for (var intento = 0; intento < 10; intento++)
        {
            try
            {
                // SetDataObject con copy:true y no SetText: deja el texto en el
                // portapapeles despues de que este proceso termine, que es lo
                // que espera cualquiera que copie algo.
                Clipboard.SetDataObject(texto, copy: true);
                return true;
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(60);
            }
        }

        return false;
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
            Reportar(ex);
        }
    }

    /// <summary>
    /// Fase 23. Los RPC de terminal existian desde la Fase 15 y no habia forma de
    /// usarlos: no habia terminal en ninguna interfaz.
    ///
    /// Va en el dashboard y no en el visor remoto porque la terminal se autentica
    /// con el JWT del tecnico contra AdminService, y el visor solo habla con el
    /// relay -- darle credenciales de administrador seria ampliar lo que puede
    /// hacer un proceso que ya recibe datos de la PC controlada.
    /// </summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        if (SelectedMachine is null)
            return;

        new Views.TerminalWindow(_client, SelectedMachine.MachineId, SelectedMachine.MachineCode)
        {
            Owner = Application.Current.MainWindow
        }.Show();
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
            Reportar(ex);
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

            // La fila local, al dia. El stream traera lo mismo cuando el agente
            // vuelva a conectar, pero hasta entonces el aviso seguiria en rojo.
            SelectedMachine.ConflictoResuelto();
            RefreshCounts();

            StatusMessage = resolution == ResolveConflictRequest.Types.Resolution.IssueNewIdentity
                ? "Identidad nueva emitida. Esa PC necesita un recovery code para volver."
                : "Hardware aprobado. El agente entra solo en su proximo intento, hasta un minuto.";
        }
        catch (Exception ex)
        {
            Reportar(ex);
        }
    }

    private bool MatchesFilter(object item)
    {
        if (item is not MachineViewModel machine)
            return true;

        // LAS DE BAJA SOLO CUANDO SE PIDEN. Es el motivo de la baja: que dejen
        // de estorbar en la lista sin perder lo que hicieron.
        if (machine.Retired != (StatusFilter == "RETIRED"))
            return false;

        if (StatusFilter.Length > 0 && StatusFilter != "RETIRED")
        {
            var coincide = StatusFilter == "CONFLICT"
                ? machine.HasConflict
                : machine.StatusText == StatusFilter;

            if (!coincide)
                return false;
        }

        if (AreaFilter != Todas && !string.Equals(machine.Area, AreaFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (LineFilter != Todas && !string.Equals(machine.Line, LineFilter, StringComparison.OrdinalIgnoreCase))
            return false;

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
