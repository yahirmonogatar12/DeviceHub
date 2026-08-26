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

        // La advertencia y la contrasena en el MISMO dialogo. Un "seguro?" al
        // que se dice que si por inercia, y despues una contrasena, no protege
        // mas que pedirla una vez con lo que va a pasar escrito al lado.
        var dialogo = new ConfirmarBajaWindow(_client, maquina.MachineCode)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialogo.ShowDialog() != true)
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

            Entrar();
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
    /// Lo que pasa despues de entrar, venga de la contrasena o de la sesion
    /// guardada. Estaba escrito dentro del login y por eso reanudar no podia
    /// reutilizarlo.
    /// </summary>
    private void Entrar()
    {
        IsLoggedIn = true;
        SessionLabel = $"{_client.Username} ({_client.Role})";
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(IsAdministrator));

        _watch = new CancellationTokenSource();
        _ = WatchAsync(_watch.Token);
    }

    /// <summary>
    /// Reanuda la sesion guardada, si la hay y no ha caducado.
    ///
    /// Se entra SIN comprobar nada contra el servidor: si el token ya no vale,
    /// la primera llamada del stream fallara con Unauthenticated y de ahi se
    /// vuelve a la pantalla de entrada. Preguntar antes seria una ida y vuelta
    /// mas para llegar a la misma conclusion medio segundo despues.
    /// </summary>
    public void ReanudarSesion()
    {
        if (_client.Reanudar())
            Entrar();
    }

    /// <summary>Cierra la sesion y olvida la guardada.</summary>
    [RelayCommand]
    private void Salir()
    {
        _watch?.Cancel();
        _client.Olvidar();

        Machines.Clear();
        SelectedMachine = null;
        Detail = null;
        IsLoggedIn = false;
        SessionLabel = string.Empty;

        RefreshCounts();
        RefreshPages();
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
            catch (Grpc.Core.RpcException rpc)
                when (rpc.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
            {
                // La sesion guardada ya no vale: caduco el token, o el servidor
                // se reinicio con otra clave de firma. NO se reintenta --
                // insistir con una credencial rechazada es esperar a que cambie
                // de opinion. Se olvida y se pide la contrasena.
                Salir();
                StatusMessage = "La sesion guardada ya no vale. Entra otra vez.";

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

    /// <summary>
    /// Lo que hay marcado en la lista. Lo mantiene la propia rejilla: con
    /// seleccion multiple, SelectedItem solo conoce una y hace falta el resto.
    /// </summary>
    public IReadOnlyList<MachineViewModel> Seleccionados { get; private set; } = [];

    public void FijarSeleccion(IEnumerable<MachineViewModel> cuales)
        => Seleccionados = [.. cuales];

    /// <summary>
    /// Abre sesion remota contra TODO lo que este marcado.
    ///
    /// Van a la MISMA ventana del visor, cada una en su pestana: la tuberia por
    /// la que llego el primer ticket se queda abierta justo para esto. Es el
    /// mismo camino que abrir una detras de otra, sin los clics.
    ///
    /// EN SERIE Y NO A LA VEZ. Cada apertura pide un ticket, arranca el host en
    /// la PC de planta y espera su primer keyframe; lanzarlas en paralelo pone a
    /// competir por la red a veinte codificadores que todavia no han dicho su
    /// primera palabra, y lo que se gana en clics se pierde en espera.
    /// </summary>
    [RelayCommand]
    private async Task ControlarSeleccionadosAsync()
    {
        // Las que ya tienen pestana NO se vuelven a abrir.
        //
        // El agente solo sostiene un RemoteHost por PC: la segunda sesion mata a
        // la primera y deja la pestana anterior congelada. Lo mas util es dejar
        // en paz las que ya estan.
        var todas = Seleccionados.Where(m => !m.Retired).ToList();
        var cuales = todas.Where(m => !YaAbierta(m.MachineId)).ToList();
        var repetidas = todas.Count - cuales.Count;

        if (cuales.Count == 0)
        {
            MessageBox.Show(
                repetidas > 0
                    ? $"Los {repetidas} equipos marcados ya tienen su pestana abierta en el visor."
                    : "Marca uno o varios equipos en la lista. Con Ctrl se anaden sueltos y con Mayus un rango.",
                "Controlar", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        // CADA SESION ES UN VIDEO. Con cuatro o cinco no se nota; con veinte se
        // nota en la red de planta y en el servidor, que las reenvia todas. El
        // numero va en la pregunta para que la decision se tome viendolo.
        if (cuales.Count > 3)
        {
            var salto = Environment.NewLine;

            var pregunta = MessageBox.Show(
                $"Se van a abrir {cuales.Count} sesiones remotas, cada una con su video." +
                salto + salto +
                "Todas van a la misma ventana, en pestanas." + salto +
                "Con muchas a la vez, la red de planta y el servidor lo notan." +
                salto + salto + "Continuar?",
                "Controlar seleccionados", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (pregunta != MessageBoxResult.Yes)
                return;
        }

        var anterior = SelectedMachine;
        var abiertas = 0;
        var fallidas = new List<string>();

        // EN SILENCIO MIENTRAS DURA LA TANDA.
        //
        // Cada fallo abria su propio cuadro de dialogo y PARABA todo hasta que
        // alguien lo aceptara. Con 29 equipos eso son 29 interrupciones en fila
        // -- se vio abriendo 28: un modal de RustDesk a mitad, y la tanda
        // esperando a que lo cerraran.
        _sinDialogos = true;

        try
        {
            foreach (var maquina in cuales)
            {
                StatusMessage = $"Abriendo {maquina.MachineCode}... ({abiertas + fallidas.Count + 1} de {cuales.Count})";

                // RemoteControlAsync trabaja sobre SelectedMachine, que es como
                // lo usa el boton de siempre. Se le presta la maquina de turno en
                // vez de duplicar aqui todo el lanzamiento -- ticket, stdin y
                // reuso del visor abierto -- que es donde estan las decisiones
                // dificiles.
                SelectedMachine = maquina;

                // "devicehub" EXPLICITO, no el motor por defecto.
                //
                // El de por defecto lo decide DeviceHub:RemoteProvider en el
                // servidor, y hoy es "rustdesk" -- la Fase 18 todavia no lo ha
                // cambiado. Asi que este boton abria por RustDesk, y en las PCs
                // que no lo tienen instalado saltaba "esta maquina no tiene motor
                // de control remoto". El motor propio no necesita nada alla:
                // direcciona por machine_id, que siempre existe.
                if (await RemoteControlAsync("devicehub"))
                    abiertas++;
                else
                    fallidas.Add(maquina.MachineCode);

                // UN RESPIRO ENTRE UNA Y LA SIGUIENTE.
                //
                // "En serie" solo serializaba la PETICION: cuando ya hay visor
                // abierto, abrir una sesion es escribirle un ticket por la
                // tuberia y volver -- no se espera a que la sesion se establezca.
                // Por eso las 29 salieron en el MISMO segundo, y con ellas 29
                // hosts arrancando y 29 codificadores calentando a la vez contra
                // el mismo relay.
                //
                // Medio segundo no arregla el ancho de banda, pero reparte el
                // arranque, que es el pico: cada codificador necesita sus
                // primeros frames sin competir con otros veintiocho.
                if (maquina != cuales[^1])
                    await Task.Delay(500);
            }
        }
        finally
        {
            _sinDialogos = false;
        }

        SelectedMachine = anterior;

        var yaEstaban = repetidas > 0 ? $"; {repetidas} ya estaban abiertas" : string.Empty;

        StatusMessage = fallidas.Count == 0
            ? $"{abiertas} sesion(es) abiertas en la ventana del visor{yaEstaban}."
            : $"{abiertas} abiertas{yaEstaban}; {fallidas.Count} no se pudieron: " +
              string.Join(", ", fallidas.Take(6)) + (fallidas.Count > 6 ? "..." : string.Empty);
    }

    /// <summary>
    /// Pide un numero con un dialogo simple. Devuelve null si se cancela.
    ///
    /// Con InputBox de VisualBasic y no con una ventana propia: son dos
    /// preguntas de un solo campo, y montarles un XAML con su ViewModel seria
    /// mas codigo que el que las usa. El ensamblado ya viene con el runtime.
    /// </summary>
    private static int? PedirNumero(string pregunta, string titulo, int porDefecto, int minimo, int maximo)
    {
        var texto = Microsoft.VisualBasic.Interaction.InputBox(
            $"{pregunta}  ({minimo}-{maximo})", titulo, porDefecto.ToString());

        // Cancelar devuelve cadena vacia, igual que dejarlo en blanco. Las dos
        // significan lo mismo aqui: no emitir el codigo.
        if (string.IsNullOrWhiteSpace(texto))
            return null;

        if (!int.TryParse(texto.Trim(), out var valor))
        {
            MessageBox.Show($"'{texto}' no es un numero.", titulo,
                MessageBoxButton.OK, MessageBoxImage.Warning);

            return null;
        }

        return Math.Clamp(valor, minimo, maximo);
    }

    /// <summary>
    /// Le dice a TODAS las PCs conectadas que miren el recurso AHORA.
    ///
    /// El agente lo comprueba solo cada seis horas. Publicar algo urgente y
    /// esperar seis horas es casi lo mismo que no haberlo publicado, y la
    /// alternativa era pulsar "Actualizar ahora" equipo por equipo -- con
    /// veinte, eso es una tarde.
    ///
    /// SOLO A LAS QUE ESTAN EN LINEA. A una apagada el comando se le queda
    /// encolado y caduca; enviarselo solo sirve para llenar la auditoria de
    /// ordenes que nadie ejecuto. Cuando encienda, mirara sola.
    ///
    /// Y NO SE ESPERA RESPUESTA, a proposito. Si hay version nueva el servicio
    /// se REINICIA para aplicarla, asi que la respuesta no llega casi nunca:
    /// esperarla daria una fila de "sin respuesta" que parecen fallos y son
    /// exactamente lo contrario -- la prueba de que funciono.
    /// </summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var enLinea = Machines
            .Where(m => !m.Retired && m.Status == MachineStatus.Online)
            .ToList();

        if (enLinea.Count == 0)
        {
            MessageBox.Show("No hay ningun equipo en linea ahora mismo.",
                "Actualizar todos", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var salto = Environment.NewLine;

        var pregunta = MessageBox.Show(
            $"Se le va a pedir a {enLinea.Count} equipo(s) que busquen actualizacion ahora." +
            salto + salto +
            "Los que tengan version nueva REINICIARAN su servicio para aplicarla." + salto +
            "La sesion remota que este abierta contra ellos se cortara." +
            salto + salto +
            "Continuar?",
            "Actualizar todos", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (pregunta != MessageBoxResult.Yes)
            return;

        var pedidos = 0;
        var fallidos = 0;

        foreach (var maquina in enLinea)
        {
            try
            {
                await _client.SendCommandAsync(
                    new SendCommandRequest { MachineId = maquina.MachineId, Type = CommandType.CheckUpdate },
                    CancellationToken.None);

                pedidos++;
            }
            catch (Exception)
            {
                // Una que falle no puede parar a las demas: es lo que convierte
                // "actualizar todos" en "actualizar hasta la primera que dio
                // problemas", que es peor que no tener el boton.
                fallidos++;
            }

            StatusMessage = $"Pidiendo actualizacion... {pedidos + fallidos} de {enLinea.Count}";
        }

        StatusMessage = fallidos == 0
            ? $"Pedida la actualizacion a {pedidos} equipo(s). Los que tengan version nueva se reinician solos."
            : $"Pedida a {pedidos} equipo(s); {fallidos} no aceptaron la orden.";
    }

    [RelayCommand]
    private async Task CreateEnrollmentCodeAsync()
    {
        try
        {
            // CUANTAS PCs Y CUANTO DURA, en vez de 1 uso y 30 minutos fijos.
            //
            // Un codigo de un solo uso obliga a volver al dashboard entre PC y
            // PC, y media hora no alcanza para una ronda por la planta: quien
            // iba a instalar cinco equipos acababa generando cinco codigos desde
            // el telefono, o dejando uno de mas emitido por si acaso.
            //
            // Sigue habiendo tope en los dos: un codigo es la llave para dar de
            // alta una maquina, y uno sin limites que se queda en una USB es una
            // puerta abierta que nadie recuerda cerrar.
            var cuantas = PedirNumero(
                "Para cuantas PCs va a valer este codigo?", "PCs", 5, 1, 50);

            if (cuantas is not { } usos)
                return;

            var minutos = PedirNumero(
                "Cuantos minutos quieres que dure?", "Minutos", 240, 5, 1440);

            if (minutos is not { } vigencia)
                return;

            var reply = await _client.CreateEnrollmentCodeAsync(
                new CreateEnrollmentCodeRequest { MaxUses = usos, ValidMinutes = vigencia },
                CancellationToken.None);

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
