using System.Windows;

namespace DeviceHub.Dashboard;

public partial class MainWindow : Window
{
    private readonly DeviceHubClient _client;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _client = new DeviceHubClient(DashboardSettings.Load());
        _viewModel = new MainViewModel(_client);
        DataContext = _viewModel;

        // Si quedo sesion de la vez anterior, se entra sin preguntar. Va aqui y
        // no en el constructor del ViewModel porque arranca el stream de
        // novedades, y eso quiere la ventana ya montada.
        Loaded += (_, _) => _viewModel.ReanudarSesion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Shutdown();
        _client.Dispose();
        base.OnClosed(e);
    }
}
