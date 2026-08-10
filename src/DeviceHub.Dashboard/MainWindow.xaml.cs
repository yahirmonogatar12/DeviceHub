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
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Shutdown();
        _client.Dispose();
        base.OnClosed(e);
    }
}
