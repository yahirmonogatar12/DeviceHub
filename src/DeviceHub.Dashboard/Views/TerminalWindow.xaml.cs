using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceHub.Dashboard.Views;

/// <summary>
/// Terminal remota. Fase 23.
///
/// Los RPC del servidor existian desde la Fase 15 y no habia forma de usarlos:
/// no habia terminal en ninguna interfaz. Esto es esa interfaz, y nada mas -- la
/// politica, el timeout, el tope de salida y la auditoria ya estaban resueltos
/// al otro lado.
///
/// UN COMANDO, UN PROCESO. No hay powershell.exe vivo esperando: el agente lanza
/// uno por comando y devuelve su salida entera. Se pierden las variables entre
/// comandos y se conserva el directorio, que es lo que la gente usa.
/// </summary>
public partial class TerminalWindow : Window
{
    private readonly DeviceHubClient _cliente;
    private readonly string _machineId;
    private readonly string _titulo;
    private readonly CancellationTokenSource _cancelacion = new();

    /// <summary>Historial para las flechas. Es lo primero que alguien echa de
    /// menos en una caja de texto que pretende ser una terminal.</summary>
    private readonly List<string> _historial = [];
    private int _posicion;

    private string? _sesion;

    /// <summary>
    /// "cmd" o "powershell".
    ///
    /// Arranca en PowerShell porque es lo que esta terminal ha hecho SIEMPRE, y
    /// cambiarlo por defecto haria que un comando que alguien tenia apuntado
    /// dejara de funcionar sin avisar. Lo que si cambia es que ahora el prompt
    /// dice cual de los dos es: antes ponia C:\> y ejecutaba PowerShell.
    /// </summary>
    private string _shell = "powershell";

    private string _directorio = @"C:\";

    public TerminalWindow(DeviceHubClient cliente, string machineId, string titulo)
    {
        InitializeComponent();

        _cliente = cliente;
        _machineId = machineId;
        _titulo = titulo;

        Title = $"DeviceHub - Terminal en {titulo}";

        Loaded += async (_, _) => await AbrirAsync();
        Closed += (_, _) => Cerrar();
    }

    private async Task AbrirAsync()
    {
        try
        {
            var sesion = await _cliente.StartTerminalSessionAsync(_machineId, _cancelacion.Token);

            _sesion = sesion.SessionId;
            _directorio = sesion.WorkingDir;

            Cabecera.Text = $"{_titulo}   sesion {sesion.SessionId}";
            Identidad.Text =
                "La identidad se conoce al ejecutar el primer comando. Prueba con: whoami";

            Pintar();

            // Y tambien cuando la ventana vuelve al frente. El foco se pierde al
            // cambiar de ventana, y volver a una consola donde no se puede
            // escribir se parece demasiado a una consola rota.
            Activated += (_, _) => Entrada.Focus();

            // PEGAR VARIAS LINEAS EJECUTA VARIAS LINEAS.
            //
            // La entrada es de una sola linea, asi que al pegar un bloque WPF se
            // queda con la primera y tira el resto EN SILENCIO. Quien pega cuatro
            // comandos ve ejecutarse uno y no entiende que paso con los otros
            // tres -- y en una terminal remota eso da miedo, porque no sabes si
            // corrieron a medias.
            //
            // Se hace lo que hace cualquier terminal: se ejecutan en orden, una
            // detras de otra, esperando a que cada una termine.
            DataObject.AddPastingHandler(Entrada, Pegando);

            Entrada.Focus();
        }
        catch (Exception ex)
        {
            Cabecera.Text = $"No se pudo abrir la sesion: {ex.Message}";
        }
    }

    private void ElegirCmd(object sender, RoutedEventArgs e) => Cambiar("cmd");

    private void ElegirPowerShell(object sender, RoutedEventArgs e) => Cambiar("powershell");

    /// <summary>
    /// Cambia de shell SIN cerrar la sesion ni perder el directorio.
    ///
    /// La sesion es del servidor y no sabe de shells: cada comando dice con cual
    /// se ejecuta. Asi que cambiar aqui no cuesta nada, y se puede ir y volver a
    /// mitad de una tanda -- que es justo lo que hace falta cuando un comando
    /// sale mejor en uno y el siguiente en el otro.
    /// </summary>
    private void Cambiar(string shell)
    {
        if (_shell == shell)
            return;

        _shell = shell;

        Escribir(shell == "cmd"
            ? "-- cmd: dir, %PATH%, for /f --"
            : "-- PowerShell: Get-ChildItem, $env:PATH, objetos --");

        Pintar();
        Entrada.Focus();
    }

    /// <summary>El prompt y la pestaña marcada, que son lo mismo dicho dos
    /// veces: cual de los dos shells va a ejecutar lo que se escriba.</summary>
    private void Pintar()
    {
        var esCmd = _shell == "cmd";

        // El prompt REAL de cada uno. Ponerle C:\> a PowerShell fue el origen de
        // toda la confusion: se escribia un comando de cmd y el error que volvia
        // no se parecia a nada.
        Prompt.Text = esCmd ? $"{_directorio}>" : $"PS {_directorio}>";

        BotonCmd.Background = esCmd ? new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)) : Brushes.Transparent;
        BotonCmd.Foreground = esCmd ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));

        BotonPowerShell.Background = esCmd ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x01, 0x24, 0x56));
        BotonPowerShell.Foreground = esCmd ? new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)) : Brushes.White;
    }

    private async void Pegando(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string texto)
            return;

        if (!texto.Contains('\n') && !texto.Contains('\r'))
            return;

        // Se cancela el pegado normal: si no, la primera linea quedaria ademas
        // escrita en la caja y se ejecutaria dos veces.
        e.CancelCommand();

        // EL BLOQUE ENTERO COMO UN SOLO COMANDO, no linea por linea.
        //
        // Aqui cada comando es un proceso nuevo -- es el modelo de esta terminal
        // desde la Fase 15 -- asi que las variables no sobreviven de uno al
        // siguiente. Ejecutar las lineas sueltas rompia cualquier bloque que
        // empezara definiendo algo, y lo rompia de la peor manera: la primera
        // linea parecia funcionar y las demas fallaban por separado.
        //
        //     $e = "C:\...\DeviceHub.RemoteHost.exe"
        //     & $e --displays          <- ahi $e ya no existe
        //
        // Pegado entero, las lineas comparten proceso y el bloque hace lo que
        // dice, que es ademas lo que espera quien pega un script.
        await EjecutarAsync(texto);
    }

    private async void TeclaEnLaEntrada(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await EjecutarAsync();
                break;

            // El historial va del mas reciente hacia atras, como en cualquier
            // shell. La posicion al final de la lista es "linea nueva y vacia".
            case Key.Up:
                e.Handled = true;
                Recordar(-1);
                break;

            case Key.Down:
                e.Handled = true;
                Recordar(+1);
                break;
        }
    }

    private void Recordar(int paso)
    {
        if (_historial.Count == 0)
            return;

        _posicion = Math.Clamp(_posicion + paso, 0, _historial.Count);

        Entrada.Text = _posicion == _historial.Count ? string.Empty : _historial[_posicion];
        Entrada.CaretIndex = Entrada.Text.Length;
    }

    private async Task EjecutarAsync(string? pegado = null)
    {
        var comando = (pegado ?? Entrada.Text).Trim();

        if (_sesion is null || comando.Length == 0)
            return;

        _historial.Add(comando);
        _posicion = _historial.Count;

        // Un bloque pegado se escribe con el prompt en la primera linea y las
        // demas alineadas debajo: asi se ve que fue UNA ejecucion y no varias
        // sueltas, que es justo la diferencia que importa aqui.
        var lineas = comando.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Escribir($"{Prompt.Text} {lineas[0]}");

        foreach (var extra in lineas.Skip(1))
            Escribir(new string(' ', Prompt.Text.Length + 1) + extra);

        Entrada.Text = string.Empty;
        Entrada.IsEnabled = false;

        try
        {
            var respuesta = await _cliente.RunTerminalCommandAsync(
                _sesion, comando, _shell, _cancelacion.Token);

            if (respuesta.Output.Length > 0)
                Escribir(respuesta.Output.TrimEnd());

            // El codigo de salida solo se anuncia cuando NO es 0. Repetir "exit 0"
            // detras de cada comando convierte la salida en ruido.
            if (respuesta.ExitCode != 0)
                Escribir($"[codigo de salida {respuesta.ExitCode}]");

            if (respuesta.Truncated)
                Escribir($"[salida truncada; el tope son {64} KiB]");

            _directorio = respuesta.WorkingDir;
            Pintar();

            if (respuesta.Identity.Length > 0)
                Identidad.Text = $"Los comandos se ejecutan como {respuesta.Identity}";
        }
        catch (Exception ex)
        {
            Escribir($"[fallo: {ex.Message}]");
        }
        finally
        {
            Entrada.IsEnabled = true;
            Entrada.Focus();
        }
    }

    private void Escribir(string texto)
    {
        Salida.AppendText(texto + Environment.NewLine);
        Salida.ScrollToEnd();
    }

    /// <summary>
    /// Cierra la sesion en el servidor. Sin esto queda abierta hasta que el
    /// barrido de huerfanas la recoge a las 8 h, y mientras tanto figura en la
    /// auditoria como una terminal que alguien dejo puesta.
    /// </summary>
    private void Cerrar()
    {
        var sesion = _sesion;
        _sesion = null;

        if (sesion is not null)
        {
            // Sin await: la ventana ya se esta cerrando. El token no se cancela
            // hasta despues para que a esta llamada le de tiempo a salir.
            _ = _cliente.EndTerminalSessionAsync(sesion, CancellationToken.None)
                .ContinueWith(_ => _cancelacion.Dispose(), TaskScheduler.Default);

            return;
        }

        _cancelacion.Dispose();
    }
}
