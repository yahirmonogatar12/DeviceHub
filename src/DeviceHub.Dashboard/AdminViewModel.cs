using System.Collections.ObjectModel;
using System.IO;
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

/// <summary>
/// Una fila del historial de IP o de ubicacion, ya con la fecha en hora local.
///
/// Antes se enlazaba directo al Timestamp del protobuf y se pintaba su ToString:
/// un ISO en UTC. En planta eso son seis horas de diferencia leidas como si nada.
/// </summary>
public sealed record HistoryLine(string Que, string Donde, string Desde, string Hasta)
{
    public bool EsVigente => Hasta.Length == 0;

    public static HistoryLine From(IpHistoryEntry entry) => new(
        Que: entry.Ip,
        Donde: entry.Mac,
        Desde: Local(entry.ValidFrom),
        Hasta: Local(entry.ValidTo));

    public static HistoryLine From(PlacementHistoryEntry entry) => new(
        Que: entry.MachineCode,
        Donde: string.Join("/", new[] { entry.Area, entry.Line, entry.Station }.Where(s => s.Length > 0)),
        Desde: Local(entry.ValidFrom),
        Hasta: Local(entry.ValidTo));

    private static string Local(Google.Protobuf.WellKnownTypes.Timestamp? stamp) => stamp is null
        ? string.Empty
        : DateTime.SpecifyKind(stamp.ToDateTime(), DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>Una linea de auditoria lista para mostrar (Fase 12).</summary>
public sealed record AuditLine(string When, string Who, string Action, string Machine, string Outcome, string Details)
{
    public bool IsDenied => Outcome == "denied";

    public static AuditLine From(AuditRecord record) => new(
        When: record.OccurredAt is null
            ? "-"
            : DateTime.SpecifyKind(record.OccurredAt.ToDateTime(), DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm:ss"),
        Who: string.IsNullOrEmpty(record.UserRole) ? record.UserId : $"{record.UserId} ({record.UserRole})",
        Action: record.Action,
        Machine: record.MachineCode,
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

    // ================= Buscadores de las pestanas =================
    //
    // Proyecciones filtradas en vez de ICollectionView: las listas se rellenan de
    // golpe al pulsar Actualizar, asi que basta con recalcular y avisar. Un
    // CollectionView aqui solo anadiria plumbing para el mismo resultado.

    [ObservableProperty] private string _processFilter = string.Empty;
    [ObservableProperty] private string _serviceFilter = string.Empty;
    [ObservableProperty] private string _auditFilter = string.Empty;
    [ObservableProperty] private string _historyFilter = string.Empty;

    public IEnumerable<ProcessRow> ProcessesFiltered
        => Processes.Where(p => Coincide(ProcessFilter, p.Name, p.Pid.ToString()));

    public IEnumerable<ServiceRow> ServicesFiltered
        => Services.Where(s => Coincide(ServiceFilter, s.Name, s.DisplayName, s.Status, s.StartType));

    public IEnumerable<AuditLine> AuditFiltered
        => Audit.Where(a => Coincide(AuditFilter, a.When, a.Who, a.Action, a.Machine, a.Details));

    public IEnumerable<HistoryLine> IpHistoryFiltered
        => (Detail?.IpHistory ?? []).Select(HistoryLine.From)
            .Where(h => Coincide(HistoryFilter, h.Que, h.Donde, h.Desde, h.Hasta));

    public IEnumerable<HistoryLine> PlacementHistoryFiltered
        => (Detail?.PlacementHistory ?? []).Select(HistoryLine.From)
            .Where(h => Coincide(HistoryFilter, h.Que, h.Donde, h.Desde, h.Hasta));

    private static bool Coincide(string filtro, params string?[] campos)
        => string.IsNullOrWhiteSpace(filtro)
            || campos.Any(campo => campo is not null
                && campo.Contains(filtro.Trim(), StringComparison.OrdinalIgnoreCase));

    partial void OnProcessFilterChanged(string value) => OnPropertyChanged(nameof(ProcessesFiltered));
    partial void OnServiceFilterChanged(string value) => OnPropertyChanged(nameof(ServicesFiltered));
    partial void OnAuditFilterChanged(string value) => OnPropertyChanged(nameof(AuditFiltered));

    partial void OnHistoryFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IpHistoryFiltered));
        OnPropertyChanged(nameof(PlacementHistoryFiltered));
    }

    /// <summary>Auditoria de la maquina seleccionada (Fase 12).</summary>
    [RelayCommand]
    private Task RefreshAuditAsync()
        => SelectedMachine is null ? Task.CompletedTask : CargarAuditoriaAsync(SelectedMachine.MachineId);

    /// <summary>Auditoria de toda la planta: el servidor la devuelve sin filtro
    /// de maquina cuando machineId va vacio.</summary>
    [RelayCommand]
    private Task RefreshGlobalAuditAsync() => CargarAuditoriaAsync(string.Empty);

    // Una sola coleccion para las dos vistas: la ficha de un equipo y la pagina
    // de auditoria nunca estan visibles a la vez.
    private async Task CargarAuditoriaAsync(string machineId)
    {
        try
        {
            var list = await _client.ListAuditAsync(machineId, CancellationToken.None);

            Audit.Clear();
            foreach (var entry in list.Entries)
                Audit.Add(AuditLine.From(entry));

            OnPropertyChanged(nameof(AuditFiltered));
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
    /// UN SOLO camino, aunque haya dos botones. Fase 8.
    ///
    /// La frontera no era cuantos botones hay: era que el dashboard no montara
    /// los argumentos de cada motor a mano, como hacia antes. `motor` es un
    /// NOMBRE opaco que viaja al servidor; de ahi vuelve QUE ejecutar y, si hace
    /// falta, que escribirle por stdin. Aqui no se sabe que hace ninguno de los
    /// dos, solo se ofrece elegir.
    ///
    /// Vacio = el que el servidor tenga configurado.
    /// </summary>
    [RelayCommand]
    private async Task<bool> RemoteControlAsync(string? motor)
    {
        if (SelectedMachine is null)
            return false;

        RemoteSessionReply? session = null;

        try
        {
            CommandFeedback = "Abriendo sesion remota...";
            session = await _client.StartRemoteSessionAsync(
                SelectedMachine.MachineId, motor ?? string.Empty, CancellationToken.None);

            var ejecutable = ResolverCliente(session.LaunchTarget)
                ?? throw new FileNotFoundException(
                    $"No se encontro {session.LaunchTarget} en esta PC." + Environment.NewLine +
                    Environment.NewLine +
                    "El cliente de control remoto se ejecuta AQUI, no en la maquina controlada:" +
                    Environment.NewLine + "instalalo en este equipo y vuelve a intentarlo.");

            // Solo se redirige stdin cuando hay algo que mandar por ahi. Con
            // UseShellExecute=true no se puede escribir al proceso, asi que los
            // motores sin secreto conservan el lanzamiento de siempre.
            var conSecreto = session.ViewerSecret.Length > 0;

            // SI YA HAY UN VISOR ABIERTO, la PC nueva va a SU ventana.
            //
            // Es la misma tuberia por la que le llego su primer ticket, que
            // ahora se queda abierta justo para esto. Sin ella, controlar cuatro
            // PCs son cuatro ventanas sueltas, y ninguna dice de cual es cual sin
            // leerle la barra.
            if (conSecreto && await AlVisorAbiertoAsync(session))
            {
                _remoteSessionId = session.SessionId;
                _abiertas.Add(SelectedMachine.MachineId);
                CommandFeedback = $"Sesion remota abierta sobre {SelectedMachine.MachineCode}";

                return true;
            }

            var proceso = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(ejecutable, session.LaunchArguments)
                {
                    UseShellExecute = !conSecreto,
                    RedirectStandardInput = conSecreto
                })
                ?? throw new InvalidOperationException("No se pudo lanzar el cliente remoto.");

            if (conSecreto)
            {
                // Por stdin y no por argumentos: los argumentos de un proceso los
                // lee cualquier usuario de esta PC.
                await proceso.StandardInput.WriteLineAsync(session.ViewerSecret);
                await proceso.StandardInput.FlushAsync();

                // Y NO SE CIERRA. Antes se cerraba aqui mismo, porque no habia
                // nada mas que decir; ahora es por donde entran las sesiones
                // siguientes.
                _visor = proceso;
            }

            _remoteSessionId = session.SessionId;
            _abiertas.Add(SelectedMachine.MachineId);

            // host_notified viene en falso cuando el motor necesitaba arrancar el
            // otro extremo y el agente no estaba conectado. La sesion es valida,
            // pero no va a contestar nadie: mas vale decirlo que dejar al tecnico
            // mirando una ventana negra.
            CommandFeedback = session.HostNotified || session.ViewerSecret.Length == 0
                ? $"Sesion remota abierta sobre {SelectedMachine.MachineCode}"
                : $"Sesion abierta sobre {SelectedMachine.MachineCode}, pero su agente no esta " +
                  "conectado: nadie va a atender del otro lado.";

            return true;
        }
        catch (Exception ex)
        {
            // Si el lanzamiento fallo, la sesion ya quedo abierta en el servidor.
            // Sin cerrarla aqui, la auditoria mostraria a alguien "dentro" de una
            // maquina donde nunca entro, hasta el timeout de 8 h.
            if (session is not null)
            {
                try { await _client.EndRemoteSessionAsync(session.SessionId, CancellationToken.None); }
                catch (Exception) { /* el servidor la cerrara por timeout */ }
            }

            CommandFeedback = Describe(ex);

            // EN UNA TANDA NO SE ABRE UN DIALOGO POR CADA FALLO.
            //
            // Cada uno PARA todo hasta que alguien lo acepte, y con 29 equipos
            // eso son 29 interrupciones en fila. Quien lanza una tanda quiere
            // volver y encontrarla hecha, con la lista de lo que no pudo -- no
            // ir aceptando cuadros de uno en uno.
            if (!_sinDialogos)
                MessageBox.Show(Describe(ex), "Control remoto", MessageBoxButton.OK, MessageBoxImage.Warning);

            return false;
        }
    }

    /// <summary>Callado mientras dura una tanda: los fallos se juntan y se
    /// cuentan al final en vez de interrumpir uno por uno.</summary>
    private bool _sinDialogos;

    /// <summary>
    /// Maquinas con sesion ya abierta DESDE ESTE dashboard.
    ///
    /// ABRIR DOS VECES LA MISMA PC MATA LA PRIMERA. El agente solo sostiene un
    /// RemoteHost -- StartAsync empieza con Stop("llega una sesion nueva") -- asi
    /// que la segunda sesion se lleva por delante a la primera, y al tecnico le
    /// queda una pestana viva con la pantalla congelada y un
    /// "la pantalla dejo de emitir" que no explica por que.
    ///
    /// Se vio abriendo la tanda dos veces seguidas sobre los mismos 29 equipos.
    /// </summary>
    private readonly HashSet<string> _abiertas = [];

    private bool YaAbierta(string machineId) => _abiertas.Contains(machineId);

    /// <summary>
    /// Localiza el cliente de control remoto EN ESTA PC.
    ///
    /// Lanzarlo por nombre depende del PATH, y RustDesk no se añade al PATH: el
    /// intento fallaba practicamente siempre con un error de Windows en bruto.
    /// Se busca donde de verdad se instala, igual que hace el agente.
    /// </summary>
    private static string? ResolverCliente(string target)
    {
        if (Path.IsPathFullyQualified(target) && File.Exists(target))
            return target;

        var nombre = Path.GetFileName(target);

        // Primero JUNTO AL DASHBOARD. Los clientes propios se publican en esta
        // misma carpeta y no se registran en ningun sitio, asi que buscarlos en
        // las claves de desinstalacion no los encontraria nunca.
        var alLado = Path.Combine(AppContext.BaseDirectory, nombre);

        if (File.Exists(alLado))
            return alLado;

        // Y despues en las claves de desinstalacion. Esto SI nombra un producto,
        // y es el unico sitio del dashboard donde pasa: es una busqueda de
        // instalacion de terceros, no logica de motor. Cuando haya un segundo
        // motor externo, esta lista se movera a configuracion.
        foreach (var clave in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk"
                 })
        {
            try
            {
                using var registro = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(clave);
                var ruta = registro?.GetValue("InstallLocation")?.ToString();

                if (!string.IsNullOrWhiteSpace(ruta) && File.Exists(Path.Combine(ruta, nombre)))
                    return Path.Combine(ruta, nombre);
            }
            catch (Exception)
            {
                // sin permisos para leer esa clave
            }
        }

        foreach (var carpeta in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            var candidato = Path.Combine(carpeta, "RustDesk", nombre);

            if (File.Exists(candidato))
                return candidato;
        }

        return null;
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

    /// <summary>
    /// El visor abierto, si lo hay. Se guarda por su TUBERIA de entrada, que es
    /// lo unico que hace falta: por ahi entra cada PC nueva como una pestaña mas.
    /// </summary>
    private System.Diagnostics.Process? _visor;

    /// <summary>
    /// Le pasa la sesion al visor que ya esta abierto. Devuelve false si no hay
    /// ninguno, si murio, o si es de una version que no sabe recibir mas de una
    /// -- y entonces se lanza otro, que es lo que se hacia siempre.
    /// </summary>
    private async Task<bool> AlVisorAbiertoAsync(RemoteSessionReply session)
    {
        if (_visor is not { } visor || visor.HasExited)
            return false;

        try
        {
            // Dos lineas: la sesion y su ticket. El ticket NUNCA en la misma
            // linea que los argumentos, por lo mismo de siempre -- que un dia
            // alguien registre esa linea entera.
            // La marca literal, igual que los "--server" y "--session" que
            // IRemoteProvider escribe a mano: el visor no puede referenciar los
            // contratos del agente ni el dashboard los del visor, asi que el
            // acuerdo se sostiene en las dos puntas. Al otro lado, App.Marca.
            await visor.StandardInput.WriteLineAsync("+sesion " + session.LaunchArguments);

            await visor.StandardInput.WriteLineAsync(session.ViewerSecret);
            await visor.StandardInput.FlushAsync();

            return true;
        }
        catch (Exception)
        {
            // La tuberia se rompio: el visor se cerro entre la comprobacion y la
            // escritura. Se lanza uno nuevo.
            _visor = null;
            return false;
        }
    }

    /// <summary>
    /// Le dice a esta PC que mire el recurso de actualizaciones AHORA.
    ///
    /// El agente lo comprueba cada seis horas por su cuenta; esto es para cuando
    /// se acaba de publicar algo y esperarlas no tiene sentido. Si encuentra
    /// version nueva, el servicio se reinicia en segundos -- asi que la respuesta
    /// puede no llegar, y eso NO es un fallo.
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        var entry = await RunAsync(CommandType.CheckUpdate);

        CommandFeedback = entry is null
            ? "No se pudo pedir la comprobacion."
            : entry.Status == CommandStatus.Completed
                ? entry.Result
                : "Pedida la comprobacion. Si habia version nueva, el agente se esta reiniciando.";
    }

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

        OnPropertyChanged(nameof(ProcessesFiltered));
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

        OnPropertyChanged(nameof(ServicesFiltered));
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
