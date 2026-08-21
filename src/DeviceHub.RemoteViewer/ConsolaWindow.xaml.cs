using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Varias PCs en una sola ventana, como AnyDesk y RustDesk.
///
/// Todas las sesiones estan CONSTRUIDAS a la vez y solo una se ve. No es un
/// TabControl a proposito: el de WPF solo realiza el arbol visual de la pestaña
/// seleccionada, y el video vive en un HwndHost -- una ventana Win32 con su
/// cadena de intercambio D3D11 y su descodificador colgando. Cambiar de pestaña
/// lo destruiria y volver costaria una reconexion entera, con su ticket, su
/// negociacion y su primer IDR. Con Visibility se esconde y la ventana hija
/// sigue viva.
///
/// Lo que SI se apaga al cambiar de pestaña es la entrada: el gancho de teclado
/// y las teclas hundidas de la PC que se deja atras. Eso no es opcional --
/// cambiar de pestaña con Ctrl pulsado dejaria ese Ctrl hundido en una PC de
/// planta que el tecnico ya no esta mirando.
/// </summary>
public partial class ConsolaWindow : Window
{
    private readonly List<Pestana> _pestanas = [];
    private Pestana? _delante;

    private sealed record Pestana(SesionRemota Sesion, Border Ficha, TextBlock Etiqueta);

    public ConsolaWindow()
    {
        InitializeComponent();

        // El foco de la VENTANA, que no es lo mismo que la pestaña elegida. Las
        // dos cosas tienen que darse para que el teclado vaya a la PC remota.
        Activated += (_, _) => _delante?.Sesion.Activar();
        Deactivated += (_, _) => _delante?.Sesion.Desactivar();

        // UN TECLADO, VARIAS PCs: lo reparte la ventana y va a la pestaña de
        // delante. En la sesion no puede estar -- WPF tunela hasta el elemento
        // con foco, y con el video en una ventana Win32 que no toma foco, el
        // que se queda con las teclas es este.
        PreviewKeyDown += (_, e) => _delante?.Sesion.Teclear(e, pulsada: true);
        PreviewKeyUp += (_, e) => _delante?.Sesion.Teclear(e, pulsada: false);

        Closed += (_, _) =>
        {
            foreach (var pestana in _pestanas)
                pestana.Sesion.Cerrar();
        };
    }

    /// <summary>Abre una sesion en una pestaña nueva y la pone delante.</summary>
    public void Abrir(SesionRemota sesion)
    {
        var etiqueta = new TextBlock
        {
            Text = sesion.Titulo,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var cerrar = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("LetraTenue"),
            Cursor = Cursors.Hand,
            ToolTip = "Cerrar esta sesion"
        };

        var ficha = new Border
        {
            BorderBrush = (Brush)FindResource("Linea"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 6, 8, 6),
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { etiqueta, cerrar }
            }
        };

        var pestana = new Pestana(sesion, ficha, etiqueta);

        ficha.MouseLeftButtonDown += (_, _) => Seleccionar(pestana);
        cerrar.Click += (_, e) =>
        {
            // O el clic llegaria tambien a la ficha y seleccionaria la pestaña
            // que se acaba de cerrar.
            e.Handled = true;
            Cerrar(pestana);
        };

        sesion.Visibility = Visibility.Collapsed;

        _pestanas.Add(pestana);
        Pestanas.Children.Add(ficha);
        Contenido.Children.Add(sesion);

        Seleccionar(pestana);
        Repintar();

        // Al frente: el tecnico acaba de pulsar CONTROLAR PC en el dashboard, y
        // si la consola se queda detras parece que no paso nada.
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }

    private void Seleccionar(Pestana pestana)
    {
        if (ReferenceEquals(_delante, pestana))
            return;

        if (_delante is { } anterior)
        {
            // PRIMERO se suelta la de atras y despues se enciende la de delante.
            // Al reves, el gancho de teclado de la nueva se instalaria y el
            // desenganche de la vieja lo quitaria acto seguido.
            anterior.Sesion.Desactivar();
            anterior.Sesion.Visibility = Visibility.Collapsed;
        }

        _delante = pestana;

        pestana.Sesion.Visibility = Visibility.Visible;
        Title = $"DeviceHub - {pestana.Sesion.Titulo}";

        // Solo si la ventana tiene el foco: si el tecnico esta en otra
        // aplicacion, elegir pestaña no puede engancharle el teclado.
        if (IsActive)
            pestana.Sesion.Activar();

        Repintar();
    }

    private void Cerrar(Pestana pestana)
    {
        pestana.Sesion.Cerrar();

        _pestanas.Remove(pestana);
        Pestanas.Children.Remove(pestana.Ficha);
        Contenido.Children.Remove(pestana.Sesion);

        if (ReferenceEquals(_delante, pestana))
        {
            _delante = null;

            if (_pestanas.Count > 0)
                Seleccionar(_pestanas[^1]);
        }

        Repintar();
    }

    /// <summary>La franja solo cuando hay algo que elegir, y la ficha de delante
    /// marcada. Con una sola PC abierta la franja no aporta nada y le quita alto
    /// al escritorio remoto.</summary>
    private void Repintar()
    {
        FranjaPestanas.Visibility = _pestanas.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SinSesiones.Visibility = _pestanas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var pestana in _pestanas)
        {
            var delante = ReferenceEquals(_delante, pestana);

            pestana.Ficha.Background = delante ? (Brush)FindResource("Barra") : Brushes.Transparent;
            pestana.Etiqueta.Foreground = (Brush)FindResource(delante ? "Letra" : "LetraTenue");
        }
    }
}
