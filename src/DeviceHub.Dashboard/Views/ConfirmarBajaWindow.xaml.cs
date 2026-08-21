using System.Windows;
using System.Windows.Input;

namespace DeviceHub.Dashboard.Views;

/// <summary>
/// Pide la contrasena antes de dar de baja una PC.
///
/// LA COMPRUEBA CONTRA EL SERVIDOR, no contra nada de aqui: se intenta un login
/// con el usuario de la sesion y se tira la respuesta. Asi la comprobacion es la
/// de verdad -- el mismo hash, el mismo limitador de intentos -- y no una copia
/// que se pueda quedar atras.
///
/// Y se tira la respuesta a proposito: aceptar el token nuevo alargaria la
/// sesion doce horas mas por el hecho de confirmar una baja.
/// </summary>
public partial class ConfirmarBajaWindow : Window
{
    private readonly DeviceHubClient _cliente;
    private bool _comprobando;

    public ConfirmarBajaWindow(DeviceHubClient cliente, string maquina)
    {
        InitializeComponent();

        _cliente = cliente;

        Titulo.Text = $"Dar de baja {maquina}";
        Quien.Text = $"Confirma con la contrasena de {cliente.Username}.";

        Loaded += (_, _) => Clave.Focus();
    }

    private void Cancelar(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TeclaEnLaClave(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = ComprobarAsync();
    }

    private void Confirmar(object sender, RoutedEventArgs e) => _ = ComprobarAsync();

    private async Task ComprobarAsync()
    {
        // Sin esto, teclear Enter varias veces manda varios intentos y el
        // limitador del servidor los cuenta todos.
        if (_comprobando)
            return;

        _comprobando = true;
        BotonConfirmar.IsEnabled = false;
        Error.Visibility = Visibility.Collapsed;

        try
        {
            if (await _cliente.ContrasenaCorrectaAsync(Clave.Password, CancellationToken.None))
            {
                DialogResult = true;
                return;
            }

            Mostrar("Contrasena incorrecta.");
            Clave.Clear();
            Clave.Focus();
        }
        catch (Exception ex)
        {
            // Un limitador que salta o un servidor que no contesta NO es una
            // contrasena incorrecta, y decirlo asi mandaria a probar otra.
            Mostrar($"No se pudo comprobar: {ex.Message}");
        }
        finally
        {
            _comprobando = false;
            BotonConfirmar.IsEnabled = true;
        }
    }

    private void Mostrar(string texto)
    {
        Error.Text = texto;
        Error.Visibility = Visibility.Visible;
    }
}
