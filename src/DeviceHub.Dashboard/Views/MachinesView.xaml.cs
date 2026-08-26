using System.Windows.Controls;

namespace DeviceHub.Dashboard.Views;

public partial class MachinesView : UserControl
{
    public MachinesView() => InitializeComponent();

    /// <summary>
    /// La seleccion ENTERA hasta el modelo de vista.
    ///
    /// Con seleccion multiple, SelectedItem solo conoce una fila: el resto vive
    /// en SelectedItems, que no es una propiedad enlazable. Cuatro lineas aqui
    /// cuestan menos que un comportamiento adjunto para lo mismo.
    /// </summary>
    private void EquiposSeleccionados(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid rejilla && DataContext is MainViewModel vm)
            vm.FijarSeleccion(rejilla.SelectedItems.OfType<MachineViewModel>());
    }
}
