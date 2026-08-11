using System.Windows;

namespace DeviceHub.Dashboard.Views;

/// <summary>
/// Dialogo tonto: solo recoge los campos. Quien llama a MoveMachine es el
/// view model, para que el guardado no dependa de que la ventana siga viva.
/// </summary>
public partial class EditMachineWindow : Window
{
    public EditMachineWindow() => InitializeComponent();

    private void Guardar(object sender, RoutedEventArgs e) => DialogResult = true;
}
