using System.Runtime.InteropServices;

namespace DeviceHub.RemoteViewer.Input;

/// <summary>Una pulsacion tal y como la ve el sistema, antes de que nadie la
/// interprete.</summary>
public readonly record struct TeclaCruda(uint VirtualKey, uint ScanCode, bool Pulsada, bool Extendida);

/// <summary>
/// Gancho de teclado de bajo nivel. Es la unica forma de que la tecla Windows,
/// Alt+Tab y compania lleguen a la PC remota.
///
/// POR QUE NO BASTA CON PreviewKeyDown. El shell de Windows atiende esas
/// combinaciones ANTES que la aplicacion con el foco: para cuando WPF podria
/// verlas, el menu Inicio ya se abrio y el conmutador de ventanas ya salio. En
/// la PC del tecnico, no en la remota. Un WH_KEYBOARD_LL se pone por delante de
/// todo eso y puede tragarse la pulsacion devolviendo 1 en vez de encadenarla.
///
/// Es lo mismo que hacen RustDesk, AnyDesk y el propio Escritorio remoto de
/// Windows, y tiene los mismos dos limites, que no se pueden sortear desde aqui:
/// Ctrl+Alt+Supr lo genera el kernel y Win+L lo atiende winlogon. Los dos tienen
/// su boton en la barra.
///
/// EL GANCHO CORRE EN EL HILO DE LA INTERFAZ y bloquea la entrada de TODA la
/// sesion de Windows mientras la funcion no vuelva. Aqui dentro no se hace nada
/// que pueda esperar: se mira la tecla, se encola y se vuelve.
/// </summary>
public sealed class GanchoTeclado : IDisposable
{
    private const int WhKeyboardLl = 13;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr Procedimiento(int codigo, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, Procedimiento lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int codigo, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>El delegado se guarda en un campo A PROPOSITO: Windows conserva un
    /// puntero a el, y si el recolector se lleva el thunk el proceso se cae con
    /// una violacion de acceso dentro de user32, lejos de aqui y sin pista.</summary>
    private readonly Procedimiento _procedimiento;

    private readonly Func<TeclaCruda, bool> _decidir;

    private IntPtr _gancho;

    /// <param name="decidir">Devuelve true si la pulsacion se queda aqui -- se
    /// manda a la PC remota y NO llega al Windows local.</param>
    public GanchoTeclado(Func<TeclaCruda, bool> decidir)
    {
        _decidir = decidir;
        _procedimiento = Atender;

        _gancho = SetWindowsHookExW(WhKeyboardLl, _procedimiento, GetModuleHandleW(null), 0);

        if (_gancho == IntPtr.Zero)
            throw new InvalidOperationException($"No se pudo instalar el gancho de teclado: {Marshal.GetLastWin32Error()}.");
    }

    private IntPtr Atender(int codigo, IntPtr wParam, IntPtr lParam)
    {
        if (codigo < 0)
            return CallNextHookEx(_gancho, codigo, wParam, lParam);

        var mensaje = (int)wParam;
        var datos = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

        // Lo que inyecta otro programa se deja pasar. Si algun dia el visor
        // recibe entrada sintetica, tragarsela seria discutir consigo mismo.
        if ((datos.Flags & LlkhfInjected) != 0)
            return CallNextHookEx(_gancho, codigo, wParam, lParam);

        var pulsada = mensaje is WmKeyDown or WmSysKeyDown;
        var soltada = mensaje is WmKeyUp or WmSysKeyUp;

        if (!pulsada && !soltada)
            return CallNextHookEx(_gancho, codigo, wParam, lParam);

        var tecla = new TeclaCruda(
            datos.VkCode, datos.ScanCode, pulsada, (datos.Flags & LlkhfExtended) != 0);

        // Una excepcion que se escape de aqui deja el teclado de TODA la sesion
        // sin atender hasta que Windows retire el gancho por lento. Se traga.
        try
        {
            if (_decidir(tecla))
                return 1;
        }
        catch
        {
            // Nada que hacer y menos aqui dentro: la tecla sigue su camino.
        }

        return CallNextHookEx(_gancho, codigo, wParam, lParam);
    }

    public void Dispose()
    {
        if (_gancho == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_gancho);
        _gancho = IntPtr.Zero;
    }
}
