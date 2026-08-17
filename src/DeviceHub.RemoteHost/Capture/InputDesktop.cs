using System.Runtime.InteropServices;
using System.Text;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Sigue el escritorio ACTIVO de la sesion. Fase 19.
///
/// Windows no tiene un escritorio, tiene varios dentro de la misma estacion de
/// ventanas, y va cambiando cual recibe la entrada:
///
///     Default     el escritorio normal del usuario
///     Winlogon    la pantalla de bloqueo, el login y los dialogos de UAC
///     Screen-saver
///
/// Un hilo solo ve el escritorio al que esta atado. Por eso, cuando la PC se
/// bloquea, un host atado a Default deja de capturar -- no falla, simplemente
/// entrega la ultima imagen para siempre -- y su SendInput no llega a ningun
/// sitio. Esa era exactamente la queja: "quiero ver y desbloquear".
///
/// Cambiar de escritorio invalida la duplicacion DXGI, asi que quien use esto
/// tiene que recrear la captura cuando <see cref="SeguirActivo"/> diga que hubo
/// salto.
///
/// EXIGE SYSTEM. Como usuario normal, OpenInputDesktop sobre Winlogon devuelve
/// acceso denegado, y es correcto que asi sea.
/// </summary>
public sealed class InputDesktop : IDisposable
{
    private IntPtr _actual;
    private string _nombre = string.Empty;

    public string Name => _nombre;
    public long Switches { get; private set; }

    /// <summary>
    /// Ata el hilo que llama al escritorio que recibe la entrada ahora mismo.
    /// Devuelve true si cambio respecto de la llamada anterior.
    ///
    /// Se llama desde el hilo de CAPTURA y solo desde el: SetThreadDesktop ata
    /// un hilo, no el proceso, y DXGI ya vive ahi por la disciplina de un solo
    /// hilo que se arrastra desde la Fase 2.
    /// </summary>
    public bool SeguirActivo()
    {
        var entrada = OpenInputDesktop(0, false, DesktopGenericAll);

        if (entrada == IntPtr.Zero)
            return false;   // otra sesion tiene la entrada; se sigue con lo que hay

        var nombre = NombreDe(entrada);

        if (nombre == _nombre)
        {
            CloseDesktop(entrada);
            return false;
        }

        if (!SetThreadDesktop(entrada))
        {
            // Pasa si el hilo tiene ventanas o ganchos creados. Este no tiene
            // ninguna -- solo captura y codifica -- pero si algun dia falla, es
            // mejor seguir en el escritorio viejo que quedarse sin ninguno.
            CloseDesktop(entrada);
            return false;
        }

        // El anterior se cierra DESPUES de atarse al nuevo: cerrar primero
        // dejaria el hilo un instante sin escritorio.
        if (_actual != IntPtr.Zero)
            CloseDesktop(_actual);

        _actual = entrada;
        _nombre = nombre;
        Switches++;
        return true;
    }

    /// <summary>
    /// La estacion de ventanas interactiva. Se hace UNA vez por proceso y antes
    /// de tocar escritorios: los escritorios cuelgan de una estacion, y un
    /// proceso lanzado desde un servicio puede arrancar en una que no es esta.
    /// </summary>
    public static void UsarEstacionInteractiva()
    {
        var estacion = OpenWindowStation("winsta0", false, WinstaAllAccess);

        if (estacion != IntPtr.Zero)
            SetProcessWindowStation(estacion);
    }

    private static string NombreDe(IntPtr escritorio)
    {
        var texto = new StringBuilder(256);

        return GetUserObjectInformation(escritorio, UoiName, texto, texto.Capacity, out _)
            ? texto.ToString()
            : string.Empty;
    }

    public void Dispose()
    {
        if (_actual == IntPtr.Zero)
            return;

        CloseDesktop(_actual);
        _actual = IntPtr.Zero;
    }

    // ------------------------------------------------------------------ interop

    private const uint DesktopGenericAll = 0x10000000;
    private const uint WinstaAllAccess = 0x0000037F;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(
        string name, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(IntPtr station);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle, int index, StringBuilder info, int length, out int needed);
}
