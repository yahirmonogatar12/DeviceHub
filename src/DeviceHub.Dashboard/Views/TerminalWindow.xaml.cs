using System.Windows;
using System.Windows.Input;

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
            Cabecera.Text = $"{_titulo}   sesion {sesion.SessionId}";
            Prompt.Text = $"{sesion.WorkingDir}>";
            Identidad.Text =
                "La identidad se conoce al ejecutar el primer comando. Prueba con: whoami";

            Entrada.Focus();
        }
        catch (Exception ex)
        {
            Cabecera.Text = $"No se pudo abrir la sesion: {ex.Message}";
        }
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

    private async Task EjecutarAsync()
    {
        var comando = Entrada.Text.Trim();

        if (_sesion is null || comando.Length == 0)
            return;

        _historial.Add(comando);
        _posicion = _historial.Count;

        Escribir($"{Prompt.Text} {comando}");

        Entrada.Text = string.Empty;
        Entrada.IsEnabled = false;

        try
        {
            var respuesta = await _cliente.RunTerminalCommandAsync(_sesion, comando, _cancelacion.Token);

            if (respuesta.Output.Length > 0)
                Escribir(respuesta.Output.TrimEnd());

            // El codigo de salida solo se anuncia cuando NO es 0. Repetir "exit 0"
            // detras de cada comando convierte la salida en ruido.
            if (respuesta.ExitCode != 0)
                Escribir($"[codigo de salida {respuesta.ExitCode}]");

            if (respuesta.Truncated)
                Escribir($"[salida truncada; el tope son {64} KiB]");

            Prompt.Text = $"{respuesta.WorkingDir}>";

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
