using System.Runtime.InteropServices;
using System.Text;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Que paso al intentar seguir al escritorio activo.
///
/// Son CUATRO casos y no dos. La version anterior devolvia un bool, y con el
/// "no cambio nada" y "no me pude atar" valian lo mismo -- que es como la
/// pantalla de bloqueo se veia sin poder tocarla durante toda una fase.
/// </summary>
public enum Salto
{
    SinCambio,
    Cambiado,

    /// <summary>Otra sesion tiene la entrada. Se sigue con lo que hay.</summary>
    NoSePudoAbrir,

    /// <summary>El escritorio existe y no se pudo atar el hilo. Este es GRAVE:
    /// la entrada se esta inyectando en el escritorio equivocado.</summary>
    NoSePudoAtar
}

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
    public Salto SeguirActivo()
    {
        var entrada = OpenInputDesktop(0, false, DesktopGenericAll);

        if (entrada == IntPtr.Zero)
        {
            UltimoError = Marshal.GetLastWin32Error();
            return Salto.NoSePudoAbrir;
        }

        var nombre = NombreDe(entrada);

        if (nombre == _nombre)
        {
            CloseDesktop(entrada);
            return Salto.SinCambio;
        }

        if (!SetThreadDesktop(entrada))
        {
            // FALLA SI EL HILO TIENE VENTANAS O GANCHOS, y eso incluye las
            // ventanas ocultas que crean D3D11 y Media Foundation. El hilo de
            // captura las tiene en cuanto codifica un frame.
            //
            // Y aqui estaba el fallo de la pantalla de bloqueo: esto devolvia
            // `false`, el MISMO valor que "no cambio nada", asi que el que
            // llamaba no podia distinguirlos. El video seguia viendose -- la
            // duplicacion DXGI entrega lo que haya en la salida, incluido
            // Winlogon -- pero SendInput se quedaba disparando contra el
            // escritorio viejo, donde no habia nadie escuchando. Se veia la
            // pantalla de bloqueo y no se podia pulsar nada.
            UltimoError = Marshal.GetLastWin32Error();
            NombrePedido = nombre;
            CloseDesktop(entrada);
            return Salto.NoSePudoAtar;
        }

        // El anterior se cierra DESPUES de atarse al nuevo: cerrar primero
        // dejaria el hilo un instante sin escritorio.
        if (_actual != IntPtr.Zero)
            CloseDesktop(_actual);

        _actual = entrada;
        _nombre = nombre;
        Switches++;
        return Salto.Cambiado;
    }

    /// <summary>Ultimo error de Win32 al no poder saltar. 0 = ninguno.</summary>
    public int UltimoError { get; private set; }

    /// <summary>El escritorio al que NO se pudo saltar. Sirve para el log: sin el
    /// nombre, "no se pudo atar" no dice si era Winlogon o cualquier otro.</summary>
    public string NombrePedido { get; private set; } = string.Empty;

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
