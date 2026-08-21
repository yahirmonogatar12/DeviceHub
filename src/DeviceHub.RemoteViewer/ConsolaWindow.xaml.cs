using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// Varias PCs en una sola ventana, como AnyDesk y RustDesk.
///
/// Todas las sesiones estan CONSTRUIDAS a la vez y en modo normal solo una se
/// ve. No es un TabControl a proposito: el de WPF solo realiza el arbol visual
/// de la pestaña seleccionada, y el video vive en un HwndHost -- una ventana
/// Win32 con su cadena de intercambio D3D11 y su descodificador colgando.
/// Cambiar de pestaña lo destruiria y volver costaria una reconexion entera, con
/// su ticket, su negociacion y su primer IDR. Con Visibility se esconde y la
/// ventana hija sigue viva.
///
/// Lo que SI se apaga al cambiar de pestaña es la entrada: el gancho de teclado
/// y las teclas hundidas de la PC que se deja atras. Eso no es opcional --
/// cambiar de pestaña con Ctrl pulsado dejaria ese Ctrl hundido en una PC de
/// planta que el tecnico ya no esta mirando.
///
/// EN MOSAICO se enseñan todas a la vez, como una pared de camaras, y ninguna
/// acepta entrada: ahi un clic significa "quiero esta grande".
/// </summary>
public partial class ConsolaWindow : Window
{
    private readonly List<Pestana> _pestanas = [];
    private Pestana? _delante;
    private bool _mosaico;

    private sealed record Pestana(SesionRemota Sesion, Border Ficha, TextBlock Etiqueta);

    public ConsolaWindow()
    {
        InitializeComponent();

        // El foco de la VENTANA, que no es lo mismo que la pestaña elegida. Las
        // dos cosas tienen que darse para que el teclado vaya a la PC remota.
        Activated += (_, _) => { if (!_mosaico) _delante?.Sesion.Activar(); };
        Deactivated += (_, _) => _delante?.Sesion.Desactivar();

        // UN TECLADO, VARIAS PCs: lo reparte la ventana y va a la pestaña de
        // delante. En la sesion no puede estar -- WPF tunela hasta el elemento
        // con foco, y con el video en una ventana Win32 que no toma foco, el
        // que se queda con las teclas es este.
        PreviewKeyDown += (_, e) => { if (!_mosaico) _delante?.Sesion.Teclear(e, pulsada: true); };
        PreviewKeyUp += (_, e) => { if (!_mosaico) _delante?.Sesion.Teclear(e, pulsada: false); };

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

        ficha.MouseLeftButtonDown += (_, e) => EmpezarArrastre(pestana, e);
        ficha.MouseMove += (_, e) => Arrastrar(e);
        ficha.MouseLeftButtonUp += (_, _) => SoltarArrastre();
        ficha.LostMouseCapture += (_, _) => SoltarArrastre();

        cerrar.Click += (_, e) =>
        {
            // O el clic llegaria tambien a la ficha y seleccionaria la pestaña
            // que se acaba de cerrar.
            e.Handled = true;
            Cerrar(pestana);
        };

        // En mosaico un clic sobre una pantalla no va a la PC remota: la elige.
        sesion.Pulsada += (_, _) =>
        {
            if (_mosaico)
                Dispatcher.Invoke(() => { _mosaico = false; Seleccionar(pestana); });
        };

        sesion.Visibility = Visibility.Collapsed;

        _pestanas.Add(pestana);
        Pestanas.Children.Add(ficha);
        Contenido.Children.Add(sesion);

        _mosaico = false;
        Seleccionar(pestana);

        // Al frente: el tecnico acaba de pulsar CONTROLAR PC en el dashboard, y
        // si la consola se queda detras parece que no paso nada.
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }

    private void Seleccionar(Pestana pestana)
    {
        if (_delante is { } anterior && !ReferenceEquals(anterior, pestana))
        {
            // PRIMERO se suelta la de atras y despues se enciende la de delante.
            // Al reves, el gancho de teclado de la nueva se instalaria y el
            // desenganche de la vieja lo quitaria acto seguido.
            anterior.Sesion.Desactivar();
        }

        _delante = pestana;
        Title = $"DeviceHub - {pestana.Sesion.Titulo}";

        Repintar();
    }

    private void Cerrar(Pestana pestana)
    {
        pestana.Sesion.Cerrar();
        pestana.Sesion.Visibility = Visibility.Collapsed;

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

    // ------------------------------------------------------------- mosaico

    private void AlternarMosaico(object sender, RoutedEventArgs e)
    {
        _mosaico = !_mosaico;

        // Al entrar se suelta el teclado: en mosaico no hay una PC "de delante"
        // a la que mandar teclas, y dejarlo enganchado seguiria tragandose la
        // tecla Windows del tecnico sin mandarla a ningun sitio.
        if (_mosaico)
            _delante?.Sesion.Desactivar();

        Repintar();
    }

    private void AlternarPantallaCompleta(object sender, RoutedEventArgs e)
    {
        var completa = WindowStyle == WindowStyle.None;

        // El orden importa: en WPF hay que salir de Maximized para cambiar el
        // estilo, o la ventana maximizada se queda tapando la barra de tareas
        // con el borde puesto.
        WindowState = WindowState.Normal;
        WindowStyle = completa ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        ResizeMode = completa ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowState = completa ? WindowState.Normal : WindowState.Maximized;

        BotonCompleta.ToolTip = completa ? "Pantalla completa" : "Salir de pantalla completa";
    }

    /// <summary>
    /// Quien se ve, quien acepta entrada y como esta la franja.
    ///
    /// Todo en un sitio: son cuatro estados cruzados -- mosaico o no, con foco o
    /// sin el, la de delante y las demas -- y repartirlos por los manejadores es
    /// como se acaba con una sesion visible que no responde al raton.
    /// </summary>
    private void Repintar()
    {
        FranjaPestanas.Visibility = _pestanas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SinSesiones.Visibility = _pestanas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        BotonMosaico.IsEnabled = _pestanas.Count > 1;
        BotonMosaico.Foreground = (Brush)FindResource(_mosaico ? "Acento" : "LetraTenue");

        foreach (var pestana in _pestanas)
        {
            var delante = ReferenceEquals(_delante, pestana);

            pestana.Sesion.Visibility = _mosaico || delante ? Visibility.Visible : Visibility.Collapsed;
            pestana.Sesion.Interactiva = !_mosaico && delante;

            pestana.Ficha.Background = delante && !_mosaico
                ? (Brush)FindResource("Barra")
                : Brushes.Transparent;

            pestana.Etiqueta.Foreground = (Brush)FindResource(
                delante && !_mosaico ? "Letra" : "LetraTenue");
        }

        if (!_mosaico && IsActive)
            _delante?.Sesion.Activar();
    }

    // ---------------------------------------------------------- reordenar

    private Pestana? _arrastrando;
    private Point _agarre;

    private void EmpezarArrastre(Pestana pestana, MouseButtonEventArgs e)
    {
        if (!_mosaico)
            Seleccionar(pestana);

        _arrastrando = pestana;
        _agarre = e.GetPosition(Pestanas);

        pestana.Ficha.CaptureMouse();
    }

    /// <summary>
    /// Reordena mientras se arrastra, sin DragDrop de WPF: aqui no hay nada que
    /// transferir entre aplicaciones, solo un hijo que cambia de sitio en un
    /// StackPanel. El umbral evita que un clic con la mano poco firme reordene
    /// las pestañas sin querer.
    /// </summary>
    private void Arrastrar(MouseEventArgs e)
    {
        if (_arrastrando is not { } pestana || e.LeftButton != MouseButtonState.Pressed)
            return;

        var donde = e.GetPosition(Pestanas);

        if (Math.Abs(donde.X - _agarre.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        var destino = Reordenar.IndiceEn([.. _pestanas.Select(p => p.Ficha.ActualWidth)], donde.X);
        var actual = _pestanas.IndexOf(pestana);

        if (destino < 0 || destino == actual)
            return;

        _pestanas.RemoveAt(actual);
        _pestanas.Insert(destino, pestana);

        Pestanas.Children.Remove(pestana.Ficha);
        Pestanas.Children.Insert(destino, pestana.Ficha);

        _agarre = donde;
    }

    private void SoltarArrastre()
    {
        _arrastrando?.Ficha.ReleaseMouseCapture();
        _arrastrando = null;
    }

}
