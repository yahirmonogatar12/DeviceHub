using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteViewer;

/// <summary>
/// El gestor de archivos de dos paneles, como el de AnyDesk.
///
/// ESTO ES LA VISTA Y NADA MAS. Quien mueve bytes sigue siendo SesionRemota: es
/// la que tiene el stream, la cola en serie de la Fase 24 y el estado de la
/// descarga a medias. Partir eso en dos habria significado dos sitios que
/// pueden desincronizarse -- justo lo que el `.parcial` evita al ser el propio
/// archivo el registro de por donde iba.
///
/// El panel de la IZQUIERDA es local y no habla con nadie: son DirectoryInfo y
/// DriveInfo. El de la DERECHA pide listas por el relay y las pinta cuando
/// llegan.
/// </summary>
public partial class GestorArchivos : Window
{
    private readonly SesionRemota _sesion;

    /// <summary>Vacio = las unidades. Es el mismo convenio que usa FileListRequest
    /// para el otro lado, y asi las dos mitades se leen igual.</summary>
    private string _local = string.Empty;

    public GestorArchivos(SesionRemota sesion, string titulo)
    {
        InitializeComponent();

        _sesion = sesion;
        Title = $"DeviceHub - Archivos con {titulo}";
        TituloRemoto.Text = titulo;

        Loaded += (_, _) =>
        {
            // Se arranca en Descargas y no en las unidades: es donde acaba lo
            // que se baja, asi que es la carpeta que se va a mirar.
            IrLocal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } perfil
                ? Path.Combine(perfil, "Downloads")
                : string.Empty);

            _sesion.PedirLista(_sesion.RutaRemotaActual);
        };

        // La ventana se esconde, no se destruye: cerrarla y reabrirla perderia
        // las dos rutas, y en medio de una tanda de copias eso molesta.
        Closing += (_, e) =>
        {
            if (!_sesion.Terminando)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>
    /// Una fila, valga para el panel que valga.
    ///
    /// PUBLICO Y CON PROPIEDADES PUBLICAS. El enlace de WPF busca por reflexion
    /// y no lee las de un tipo interno: no lanza, pinta las filas VACIAS -- que
    /// es como se descubrio la primera vez.
    /// </summary>
    public sealed record Fila(string Nombre, bool Carpeta, ulong Tamano, DateTime? Fecha)
    {
        /// <summary>La subida. Nombre vacio para que no se confunda con un
        /// archivo que de verdad se llame "..".</summary>
        public static Fila Arriba => new("..", true, 0, null);

        public bool EsArriba => Carpeta && Nombre == "..";

        public string Etiqueta => Carpeta ? $"[{Nombre}]" : Nombre;

        public string Peso => Carpeta ? string.Empty : Legible(Tamano);

        public string Cuando => Fecha is { } f ? f.ToString("dd/MM/yyyy HH:mm") : string.Empty;

        private static string Legible(ulong bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
            < 1024UL * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
        };
    }

    // ------------------------------------------------------------ este equipo

    private void IrLocal(string ruta)
    {
        var filas = new List<Fila>();

        try
        {
            if (string.IsNullOrWhiteSpace(ruta) || !Directory.Exists(ruta))
            {
                // Sin ruta valida, las unidades. Es el unico sitio desde el que
                // se puede empezar sin saber nada.
                _local = string.Empty;

                foreach (var unidad in DriveInfo.GetDrives())
                {
                    if (unidad.IsReady)
                        filas.Add(new Fila(unidad.Name, true, 0, null));
                }
            }
            else
            {
                _local = Path.GetFullPath(ruta);
                filas.Add(Fila.Arriba);

                var carpeta = new DirectoryInfo(_local);

                foreach (var sub in carpeta.EnumerateDirectories())
                    filas.Add(new Fila(sub.Name, true, 0, sub.LastWriteTime));

                foreach (var archivo in carpeta.EnumerateFiles())
                    filas.Add(new Fila(archivo.Name, false, (ulong)archivo.Length, archivo.LastWriteTime));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Una carpeta que no se deja abrir no es un fallo de la sesion: se
            // dice y se deja donde estaba.
            Estado.Text = $"No se pudo abrir: {ex.Message}";
            return;
        }

        RutaLocal.Text = _local;
        Local.ItemsSource = filas;
    }

    private void SubirLocal(object sender, RoutedEventArgs e) => IrLocal(PadreDe(_local));

    private void CasaLocal(object sender, RoutedEventArgs e) => IrLocal(string.Empty);

    private void RefrescarLocal(object sender, RoutedEventArgs e) => IrLocal(_local);

    private void RutaLocalEscrita(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        // Se expanden AQUI porque son de ESTA PC. Las de la otra las expande el
        // host, que es el unico que sabe donde tiene su temporal.
        IrLocal(Environment.ExpandEnvironmentVariables(RutaLocal.Text.Trim()));
    }

    private void AbrirLocal(object sender, MouseButtonEventArgs e)
    {
        if (Local.SelectedItem is not Fila fila || !fila.Carpeta)
            return;

        IrLocal(fila.EsArriba ? PadreDe(_local) : Path.Combine(_local, fila.Nombre));
    }

    /// <summary>La carpeta de arriba, o vacio para volver a las unidades.</summary>
    private static string PadreDe(string ruta)
        => string.IsNullOrEmpty(ruta)
            ? string.Empty
            : Path.GetDirectoryName(ruta.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;

    // --------------------------------------------------------- equipo remoto

    private void SubirRemoto(object sender, RoutedEventArgs e)
        => _sesion.PedirLista(PadreDe(_sesion.RutaRemotaActual));

    private void CasaRemota(object sender, RoutedEventArgs e) => _sesion.PedirLista(string.Empty);

    private void RefrescarRemoto(object sender, RoutedEventArgs e)
        => _sesion.PedirLista(_sesion.RutaRemotaActual);

    private void RutaRemotaEscrita(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        _sesion.PedirLista(RutaRemota.Text.Trim());
    }

    private void AbrirRemoto(object sender, MouseButtonEventArgs e)
    {
        if (Remoto.SelectedItem is not Fila fila || !fila.Carpeta)
            return;

        _sesion.PedirLista(fila.EsArriba
            ? PadreDe(_sesion.RutaRemotaActual)
            : CombinarRemoto(fila.Nombre));
    }

    /// <summary>Las unidades llegan como "C:\", que ya es una ruta completa.</summary>
    private string CombinarRemoto(string nombre)
        => string.IsNullOrEmpty(_sesion.RutaRemotaActual)
            ? nombre
            : Path.Combine(_sesion.RutaRemotaActual, nombre);

    /// <summary>Lo llama la sesion cuando llega una lista del otro lado.</summary>
    public void MostrarRemoto(FileList lista)
    {
        if (lista.Error.Length > 0)
        {
            Estado.Text = lista.Error;
            return;
        }

        var filas = new List<Fila>();

        if (lista.Path.Length > 0)
            filas.Add(Fila.Arriba);

        filas.AddRange(lista.Entries
            .OrderByDescending(x => x.Directory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Fila(
                x.Name, x.Directory, x.Size,
                x.ModifiedUs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(x.ModifiedUs / 1000).LocalDateTime
                    : null)));

        RutaRemota.Text = lista.Path;
        Remoto.ItemsSource = filas;

        Estado.Text = $"{lista.Entries.Count} elementos";
    }

    // -------------------------------------------------------- transferencias

    private void Cargar(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_local))
        {
            Estado.Text = "Entra en una carpeta de este equipo.";
            return;
        }

        if (string.IsNullOrEmpty(_sesion.RutaRemotaActual))
        {
            Estado.Text = "Elige primero la carpeta de destino en la PC remota.";
            return;
        }

        var elegidos = Local.SelectedItems.OfType<Fila>()
            .Where(f => !f.Carpeta)
            .Select(f => (
                Local: Path.Combine(_local, f.Nombre),
                Remoto: Path.Combine(_sesion.RutaRemotaActual, f.Nombre)))
            .ToList();

        if (elegidos.Count == 0)
        {
            // Carpetas todavia no, igual que en toda la Fase 24. Se dice, que es
            // mejor que no hacer nada.
            Estado.Text = Local.SelectedItems.Count > 0
                ? "Carpetas todavia no: elige archivos sueltos."
                : "Elige que subir.";

            return;
        }

        _sesion.SubirVarios(elegidos);
    }

    private void Descargar(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_local))
        {
            Estado.Text = "Entra en una carpeta de este equipo para dejarlos ahi.";
            return;
        }

        var elegidos = Remoto.SelectedItems.OfType<Fila>()
            .Where(f => !f.Carpeta)
            .Select(f => (Remoto: CombinarRemoto(f.Nombre), Local: Path.Combine(_local, f.Nombre)))
            .ToList();

        if (elegidos.Count == 0)
        {
            Estado.Text = Remoto.SelectedItems.Count > 0
                ? "Carpetas todavia no: elige archivos sueltos."
                : "Elige que bajar.";

            return;
        }

        _sesion.BajarVarios(elegidos);
    }

    private void Traer(object sender, RoutedEventArgs e) => _sesion.TraerCopiado();

    private void Llevar(object sender, RoutedEventArgs e) => _sesion.LlevarCopiado();

    // ------------------------------------------------------ arrastrar y soltar

    private void ArrastrandoEncima(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    /// <summary>
    /// Soltar aqui SUBE a la carpeta remota abierta.
    ///
    /// Al reves que soltar sobre el escritorio remoto, que pega donde apunte el
    /// raton: aqui no hay ninguna pantalla debajo, hay una carpeta elegida.
    /// </summary>
    private void Soltados(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] rutas)
            return;

        if (string.IsNullOrEmpty(_sesion.RutaRemotaActual))
        {
            Estado.Text = "Elige primero la carpeta de destino en la PC remota.";
            return;
        }

        var archivos = rutas.Where(File.Exists)
            .Select(local => (
                Local: local,
                Remoto: Path.Combine(_sesion.RutaRemotaActual, Path.GetFileName(local))))
            .ToList();

        if (archivos.Count == 0)
        {
            Estado.Text = "Carpetas todavia no: arrastra archivos sueltos.";
            return;
        }

        _sesion.SubirVarios(archivos);
    }

    // ------------------------------------------------ lo que escribe la sesion

    public void Decir(string texto) => Estado.Text = texto;

    public void Avanzar(double porcentaje) => Progreso.Value = porcentaje;

    public void HayCopiadoAlla(bool si) => BotonTraer.IsEnabled = si;

    /// <summary>Se llama al terminar una bajada: la carpeta local acaba de
    /// cambiar y quedarse con la lista vieja es enseñar que no llego nada.</summary>
    public void RefrescarLocal() => IrLocal(_local);
}
