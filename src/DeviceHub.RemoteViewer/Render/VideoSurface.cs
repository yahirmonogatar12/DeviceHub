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

        _listo.Set();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
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
