using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Una ventana hija de Win32 dentro del arbol WPF, y nada mas.
///
/// El video se presenta con un swapchain D3D11 sobre este HWND, no con
/// D3DImage: D3DImage obliga a compartir superficies con D3D9Ex, que es la capa
/// mas fragil de todo el camino. El precio es el problema de AIRSPACE -- nada de
/// XAML puede dibujarse encima de este rectangulo -- y por eso las estadisticas
/// van AL LADO, en una barra propia, no superpuestas.
///
/// Esta clase no toca Direct3D. Solo entrega el handle y lo destruye; quien
/// dibuja es VideoPresenter, desde el hilo de reproduccion.
/// </summary>
public sealed class VideoSurface : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    private readonly ManualResetEventSlim _listo = new(false);
    private IntPtr _hwnd;

    /// <summary>Espera a que WPF construya la ventana hija. La reproduccion no
    /// puede crear el swapchain antes de que exista el HWND.</summary>
    public IntPtr WaitForWindow(TimeSpan espera)
        => _listo.Wait(espera) ? _hwnd : IntPtr.Zero;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Clase "static": una de las predefinidas de Windows, asi que no hay que
        // registrar ninguna. No dibuja nada por su cuenta, que es exactamente lo
        // que se quiere de una superficie que va a pintar DXGI.
        _hwnd = CreateWindowEx(
            0, "static", null, WsChild | WsVisible,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"No se pudo crear la ventana del video (error {Marshal.GetLastWin32Error()}).");

        // ARRASTRAR UN ARCHIVO ENCIMA DEL VIDEO.
        //
        // Por Win32 y no por WPF, por lo mismo que el raton: esto es una ventana
        // de verdad tapando el arbol visual, asi que un archivo soltado aqui
        // nunca llega a AllowDrop ni a Drop. Se acepta en el HWND y se avisa
        // hacia arriba.
        DragAcceptFiles(_hwnd, true);

        // Y ADEMAS SE ABRE EL FILTRO DE MENSAJES.
        //
        // Si el visor corre elevado y el Explorador no, UIPI descarta estos tres
        // mensajes SIN ERROR: el cursor enseña que se puede soltar, se suelta, y
        // no pasa absolutamente nada. Es un fallo que no deja rastro y que solo
        // aparece en la PC de quien abre el dashboard como administrador.
        foreach (var mensaje in (uint[])[WmDropFiles, WmCopyGlobalData, WmCopyData])
            ChangeWindowMessageFilterEx(_hwnd, mensaje, MsgfltAllow, IntPtr.Zero);

        _listo.Set();
        return new HandleRef(this, _hwnd);
    }

    /// <summary>
    /// Archivos soltados sobre el video: sus rutas de ESTA PC y DONDE se
    /// soltaron, normalizado 0..1 sobre la pantalla remota.
    ///
    /// El punto importa: es lo que permite pegarlos en la carpeta que el tecnico
    /// tiene abierta alla, en vez de en una que haya que elegir a mano.
    /// </summary>
    public event Action<string[], double, double>? Soltados;

    /// <summary>
    /// Raton sobre el video: (x, y) normalizados 0..1, el mensaje de Win32 y su
    /// wParam.
    ///
    /// POR QUE NO SE USAN LOS EVENTOS DE WPF. Esta es una ventana Win32 de
    /// verdad, encima del arbol visual. Los mensajes del raton van a ELLA, no a
    /// WPF, asi que MouseMove y MouseDown del elemento no se disparan nunca --
    /// el video se veia perfecto y no se podia controlar nada. Se cogen aqui,
    /// donde llegan.
    /// </summary>
    public event Action<double, double, int, IntPtr>? Raton;

    /// <summary>
    /// La clase "static" contesta HTTRANSPARENT al WM_NCHITTEST, y con eso
    /// Windows enruta los mensajes del raton a la ventana de debajo en vez de a
    /// esta. Se responde HTCLIENT para quedarselos.
    /// </summary>
    /// <summary>
    /// El cursor que se ensena sobre el video: el de la PC REMOTA. Fase 11.
    ///
    /// Se responde a WM_SETCURSOR porque la clase "static" pone la flecha por su
    /// cuenta en cada movimiento; poner el cursor una vez y marcharse no dura ni
    /// un pixel.
    /// </summary>
    private IntPtr _cursor;
    private bool _cursorVisible = true;

    public void UsarCursor(IntPtr nuevo, bool visible)
    {
        _cursorVisible = visible;

        if (nuevo == IntPtr.Zero || nuevo == _cursor)
            return;

        var anterior = _cursor;
        _cursor = nuevo;

        // El anterior se destruye DESPUES de cambiar el campo: destruirlo antes
        // deja un instante en el que WM_SETCURSOR usaria un handle muerto.
        if (anterior != IntPtr.Zero)
            DestroyIcon(anterior);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSetCursor && _cursor != IntPtr.Zero)
        {
            // Zero oculta el puntero, que es lo correcto cuando la aplicacion de
            // alla lo escondio -- un juego, un visor a pantalla completa.
            SetCursor(_cursorVisible ? _cursor : IntPtr.Zero);
            handled = true;
            return 1;
        }

        if (msg == WmNcHitTest)
        {
            handled = true;
            return HtClient;
        }

        if (msg is >= WmMouseFirst and <= WmMouseLast && Raton is not null && GetClientRect(hwnd, out var caja))
        {
            var ancho = Math.Max(caja.Right - caja.Left, 1);
            var alto = Math.Max(caja.Bottom - caja.Top, 1);

            // La rueda trae coordenadas de PANTALLA, no de cliente. Es la unica
            // del grupo que lo hace, y mezclarlas manda el puntero a otro sitio.
            var bruto = (int)(lParam.ToInt64() & 0xFFFFFFFF);
            var x = (short)(bruto & 0xFFFF);
            var y = (short)((bruto >> 16) & 0xFFFF);

            if (msg == WmMouseWheel)
            {
                var punto = new POINT { X = x, Y = y };
                ScreenToClient(hwnd, ref punto);
                x = (short)punto.X;
                y = (short)punto.Y;
            }

            Raton((double)x / ancho, (double)y / alto, msg, wParam);
        }

        if (msg == WmDropFiles)
        {
            // DragFinish SIEMPRE, tambien si no habia nada que leer: es quien
            // libera la memoria que el Explorador reservo para la operacion.
            try
            {
                var rutas = LeerSoltados(wParam);

                if (rutas.Length > 0 && GetClientRect(hwnd, out var lienzo))
                {
                    // DragQueryPoint da el punto en coordenadas de ESTA ventana,
                    // que es justo lo que hace falta: el video ocupa el control
                    // entero, asi que dividir por el cliente da el 0..1 de la
                    // pantalla remota.
                    DragQueryPoint(wParam, out var punto);

                    var w = Math.Max(lienzo.Right - lienzo.Left, 1);
                    var h = Math.Max(lienzo.Bottom - lienzo.Top, 1);

                    Soltados?.Invoke(
                        rutas,
                        Math.Clamp((double)punto.X / w, 0, 1),
                        Math.Clamp((double)punto.Y / h, 0, 1));
                }
            }
            finally
            {
                DragFinish(wParam);
            }

            handled = true;
            return IntPtr.Zero;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>
    /// Las rutas de un HDROP.
    ///
    /// Se pregunta DOS veces por cada una: primero cuanto mide y despues el
    /// texto. Reservar un MAX_PATH y confiar es lo que corta los nombres largos,
    /// que en Windows pasan de 260 caracteres desde hace tiempo.
    /// </summary>
    private static string[] LeerSoltados(IntPtr hdrop)
    {
        var cuantas = DragQueryFile(hdrop, 0xFFFFFFFF, null, 0);

        if (cuantas == 0)
            return [];

        var rutas = new string[cuantas];

        for (uint i = 0; i < cuantas; i++)
        {
            var largo = DragQueryFile(hdrop, i, null, 0) + 1;
            var nombre = new System.Text.StringBuilder((int)largo);

            DragQueryFile(hdrop, i, nombre, largo);
            rutas[i] = nombre.ToString();
        }

        return rutas;
    }

    private const int WmNcHitTest = 0x0084;
    private const int WmSetCursor = 0x0020;
    private const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MsgfltAllow = 1;

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool aceptar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(
        IntPtr hdrop, uint indice, System.Text.StringBuilder? nombre, uint largo);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hdrop);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DragQueryPoint(IntPtr hdrop, out POINT punto);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, uint mensaje, uint accion, IntPtr cambio);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icono);
    private const int HtClient = 1;
    private const int WmMouseFirst = 0x0200;
    private const int WmMouseLast = 0x020E;
    public const int WmMouseWheel = 0x020A;

    /// <summary>Lo mira la sesion cuando NO es interactiva: en mosaico un clic
    /// no se reenvia, sirve para elegir esa pantalla.</summary>
    public const int WmLButtonUp = 0x0202;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_cursor != IntPtr.Zero)
        {
            DestroyIcon(_cursor);
            _cursor = IntPtr.Zero;
        }

        _listo.Reset();
        _hwnd = IntPtr.Zero;
        DestroyWindow(hwnd.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
