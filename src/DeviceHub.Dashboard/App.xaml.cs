using System.Windows;
using System.Windows.Threading;

namespace DeviceHub.Dashboard;

public partial class App : Application
{
    /// <summary>
    /// Sin esto, una excepcion al arrancar hace que la ventana no aparezca y el
    /// proceso muera EN SILENCIO: el usuario hace doble clic y no pasa nada, sin
    /// mensaje, sin log, sin nada que investigar. Un appsettings.json mal formado
    /// bastaba para dejar el dashboard "sin abrir" sin explicacion.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Mostrar(args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Mostrar(ex);
        };

        base.OnStartup(e);
    }

    private static void Mostrar(Exception ex)
        => MessageBox.Show(
            $"{ex.Message}\n\n" +
            "Revisa appsettings.json junto al ejecutable:\n" +
            "  ServerHost, ServerPort y ServerPin\n\n" +
            $"Detalle tecnico:\n{ex.GetType().Name}\n{ex.StackTrace}",
            "DeviceHub Dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
}
