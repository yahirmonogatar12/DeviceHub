using System.Windows;
using System.Windows.Input;
using DeviceHub.Contracts;
using Grpc.Core;

namespace DeviceHub.Dashboard.Views;

/// <summary>
/// Cambia la contrasena del usuario de la sesion.
///
/// El servidor ya lo permitia (ChangeOwnPassword) y el admin inicial nace con
/// una contrasena que el log pide cambiar, pero no habia donde hacerlo: la unica
/// salida era tocar la base de datos a mano.
///
/// La politica se comprueba aqui ANTES de mandar nada, con la misma clase que
/// usa el servidor, para no gastar un intento con la actual en una nueva que
/// iba a ser rechazada de todos modos.
/// </summary>
public partial class CambiarContrasenaWindow : Window
{
    private readonly DeviceHubClient _cliente;
    private bool _enviando;

    public CambiarContrasenaWindow(DeviceHubClient cliente)
    {
        InitializeComponent();

        _cliente = cliente;
        Quien.Text = $"Usuario: {cliente.Username}";

        Loaded += (_, _) => Actual.Focus();
    }

    private void Cancelar(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TeclaEnRepetida(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = CambiarAsync();
    }

    private void Cambiar(object sender, RoutedEventArgs e) => _ = CambiarAsync();

    private async Task CambiarAsync()
    {
        if (_enviando)
            return;

        if (Nueva.Password != Repetida.Password)
        {
            Mostrar("Las dos contrasenas nuevas no coinciden.");
            return;
        }

        if (!PasswordPolicy.IsValid(Nueva.Password, out var error))
        {
            Mostrar(error);
            return;
        }

        _enviando = true;
        BotonCambiar.IsEnabled = false;
        Error.Visibility = Visibility.Collapsed;

        try
        {
            await _cliente.ChangeOwnPasswordAsync(Actual.Password, Nueva.Password, CancellationToken.None);
            DialogResult = true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Mostrar("La contrasena actual no es correcta.");
            Actual.Clear();
            Actual.Focus();
        }
        catch (Exception ex)
        {
            Mostrar($"No se pudo cambiar: {(ex is RpcException rpc ? rpc.Status.Detail : ex.Message)}");
        }
        finally
        {
            _enviando = false;
            BotonCambiar.IsEnabled = true;
        }
    }

    private void Mostrar(string texto)
    {
        Error.Text = texto;
        Error.Visibility = Visibility.Visible;
    }
}
