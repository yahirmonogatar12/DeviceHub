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

    /// <summary>
    /// El nombre del escritorio que recibe la entrada AHORA, sin atarse a el.
    ///
    /// Hace falta separado de SeguirActivo porque la decision de que capturador
    /// usar hay que tomarla ANTES de crear nada, y ademas hay que poder tomarla
    /// aunque atarse falle -- que en Winlogon es lo normal para un hilo que ya
    /// tiene ventanas.
    /// </summary>
    public static string NombreDeEntrada()
    {
        var entrada = OpenInputDesktop(0, false, ParaMirar);

        if (entrada == IntPtr.Zero)
        {
            // Se guarda para que alguien pueda verlo: sin esto, "no hay
            // escritorio" y "no me dejan mirar" son el mismo string vacio, y
            // fue exactamente lo que escondio el fallo durante cinco versiones.
            ErrorAlMirar = Marshal.GetLastWin32Error();
            return string.Empty;
        }

        ErrorAlMirar = 0;

        try
        {
            return NombreDe(entrada);
        }
        finally
        {
            CloseDesktop(entrada);
        }
    }

    /// <summary>
    /// El escritorio al que esta atado EL HILO QUE LLAMA.
    ///
    /// No es lo mismo que NombreDeEntrada: uno dice quien recibe la entrada y
    /// este dice donde estamos nosotros. Cuando no coinciden, se captura y se
    /// inyecta en un escritorio que ya no es el que se ve -- y todo lo demas
    /// parece sano, que es lo que lo hacia indetectable.
    /// </summary>
    public static string NombreDelHilo()
    {
        var propio = GetThreadDesktop(GetCurrentThreadId());

        return propio == IntPtr.Zero ? string.Empty : NombreDe(propio);
    }

    /// <summary>
    /// El escritorio normal del usuario. Cualquier otro -- Winlogon,
    /// Screen-saver, o uno creado por una aplicacion -- es un escritorio que
    /// DXGI no va a poder duplicar.
    /// </summary>
    public const string Normal = "Default";

    /// <summary>Ultimo error de OpenInputDesktop al leer el nombre. 0 = ninguno.</summary>
    public static int ErrorAlMirar { get; private set; }

    /// <summary>
    /// false = el escritorio se abrio SOLO para leer, asi que SendInput no va a
    /// entrar. Sin esto, atarse con permiso de lectura parece un exito y el
    /// tecnico se queda mirando una pantalla que no responde sin saber por que.
    /// </summary>
    public bool EscrituraConcedida { get; private set; } = true;
    public long Switches { get; private set; }

    /// <summary>
    /// Ata el hilo que llama al escritorio que recibe la entrada ahora mismo.
    /// Devuelve true si cambio respecto de la llamada anterior.
    ///
    /// Se llama desde el hilo de CAPTURA y solo desde el: SetThreadDesktop ata
    /// un hilo, no el proceso, y DXGI ya vive ahi por la disciplina de un solo
    /// hilo que se arrastra desde la Fase 2.
    /// </summary>
    public Salto SeguirActivo(bool exigirEscritura = false)
    {
        var entrada = AbrirParaEscribir(out var mascara);

        // Ultimo recurso: solo mirar. Vale para la CAPTURA -- podra seguir al
        // escritorio aunque la entrada no llegue, y media sesion es mejor que
        // ninguna -- y NO vale para el hilo de entrada.
        //
        // Atarse a Winlogon con permiso de lectura y seguir llamando a SendInput
        // es la peor de las salidas: la llamada devuelve exito, el contador de
        // aplicados sube, y la PC no responde. El tecnico ve "entrada 2748/1" y
        // una pantalla de bloqueo quieta, que es un exito falso.
        if (entrada == IntPtr.Zero && !exigirEscritura)
        {
            entrada = OpenInputDesktop(0, false, ParaMirar);
            mascara = ParaMirar;
        }

        if (entrada == IntPtr.Zero)
        {
            UltimoError = Marshal.GetLastWin32Error();
            EscrituraConcedida = false;
            NombrePedido = NombreDeEntrada();
            return Salto.NoSePudoAbrir;
        }

        var escribible = mascara != ParaMirar;
        var nombre = NombreDe(entrada);

        // "SIN CAMBIO" TIENE QUE MIRAR TAMBIEN EL PERMISO, no solo el nombre.
        //
        // Antes esto ponia EscrituraConcedida = true al entrar y luego salia por
        // aqui al ver el mismo nombre, conservando un _actual que se habia
        // abierto SOLO PARA LEER. Resultado: atado en lectura y anunciando
        // escritura. Ademas de mentir, cerraba la puerta a mejorar -- si Winlogon
        // empezaba a conceder escritura, este atajo no dejaba volver a atarse.
        if (nombre == _nombre && escribible == EscrituraConcedida && _actual != IntPtr.Zero)
        {
            CloseDesktop(entrada);
            return Salto.SinCambio;
        }

        if (!SetThreadDesktop(entrada))
        {
            // FALLA SI EL HILO TIENE VENTANAS O GANCHOS, y eso incluye las
            // ventanas ocultas que crean D3D11 y Media Foundation. El hilo de
            // captura las tiene en cuanto codifica un frame; el de entrada no
            // las tiene nunca, y por eso es el que puede seguir a Winlogon.
            UltimoError = Marshal.GetLastWin32Error();
            NombrePedido = nombre;
            EscrituraConcedida = false;
            CloseDesktop(entrada);
            return Salto.NoSePudoAtar;
        }

        // El anterior se cierra DESPUES de atarse al nuevo: cerrar primero
        // dejaria el hilo un instante sin escritorio.
        if (_actual != IntPtr.Zero)
            CloseDesktop(_actual);

        _actual = entrada;
        _nombre = nombre;

        // Y AQUI, no al entrar: solo se concede lo que de verdad se abrio.
        EscrituraConcedida = escribible;
        Mascara = mascara;
        UltimoError = 0;
        NombrePedido = string.Empty;

        Switches++;
        return Salto.Cambiado;
    }

    /// <summary>
    /// Abrir para escribir, de la mascara mas estrecha a la mas ancha.
    ///
    /// Es una ESCALERA y no una mascara sola porque las dos veces que se intento
    /// afinar esto a secas se rompio el control del escritorio NORMAL, que es lo
    /// que la gente usa todos los dias. Sobre un escritorio el permiso se
    /// concede o se deniega EN BLOQUE, asi que cada derecho que sobra es una
    /// forma nueva de que te digan que no -- y cada derecho que falta, una forma
    /// de quedarte sin poder inyectar.
    ///
    /// Con la escalera no hay que acertar: se prueba la estrecha, que es la que
    /// Winlogon puede conceder, y si no sale se cae a la ancha, que es la unica
    /// con la que se ha visto controlar de verdad una PC. Ninguna PC pierde lo
    /// que ya tenia y las bloqueadas ganan una oportunidad.
    /// </summary>
    private static IntPtr AbrirParaEscribir(out uint mascara)
    {
        foreach (var candidata in Mascaras)
        {
            var abierto = OpenInputDesktop(0, false, candidata);

            if (abierto != IntPtr.Zero)
            {
                mascara = candidata;
                return abierto;
            }
        }

        mascara = 0;
        return IntPtr.Zero;
    }

    /// <summary>Con que mascara se abrio el escritorio actual. Para el overlay.</summary>
    public uint Mascara { get; private set; }

    /// <summary>Como se llama la mascara, para que el tecnico no lea hexadecimal.</summary>
    public string ComoSeAbrio => Mascara switch
    {
        LeerEscribirEjecutar => "lectura+escritura+ejecucion",
        TodoElAcceso => "todo el acceso",
        ParaMirar => "SOLO LECTURA",
        _ => "sin abrir"
    };

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

    /// <summary>
    /// Para MIRAR: leer el nombre del escritorio de entrada, y nada mas.
    ///
    /// GENERIC_READ y no GENERIC_ALL. La lista de control de acceso del
    /// escritorio de Winlogon no concede todos los derechos ni siquiera a
    /// SYSTEM, asi que pedir GENERIC_ALL devuelve NULL -- y el codigo lo leia
    /// como "no hay escritorio de entrada" en vez de "no me dejan mirar". La
    /// captura no se rehacia jamas: congelada al bloquear y al desbloquear.
    ///
    /// Es lo que pide Chrome Remote Desktop en OpenInputDesktop.
    /// </summary>
    private const uint ParaMirar = 0x80000000;

    /// <summary>
    /// Para ESCRIBIR: atar el hilo y meterle entrada con SendInput.
    ///
    /// VUELVE A SER GENERIC_ALL, que es lo que habia antes de que yo lo tocara y
    /// lo unico con lo que se ha visto controlar de verdad una PC.
    ///
    /// Lo intente afinar dos veces para que Winlogon lo concediera, y las dos
    /// rompi el control del escritorio NORMAL, que es lo que la gente usa todos
    /// los dias. La leccion no es cual es la mascara buena -- sigo sin saberlo --
    /// sino que esto no se toca sin poder medirlo en la maquina de destino.
    /// </summary>
    /// <summary>
    /// GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE. Lo que pide Chromium.
    ///
    /// Es la primera de la escalera porque es la que Winlogon puede conceder.
    /// OJO con el mapeo generico de un escritorio: GENERIC_EXECUTE incluye
    /// DESKTOP_SWITCHDESKTOP, que es justo el derecho que una vez tumbo la
    /// peticion en el escritorio NORMAL. Por eso esta mascara no sustituye a la
    /// de abajo, va delante de ella.
    /// </summary>
    private const uint LeerEscribirEjecutar = 0xE0000000;

    /// <summary>
    /// GENERIC_ALL: nueve derechos de golpe.
    ///
    /// Es lo unico con lo que se ha visto controlar de verdad una PC, y por eso
    /// sigue en la escalera. Winlogon no la concede.
    /// </summary>
    private const uint TodoElAcceso = 0x10000000;

    private static readonly uint[] Mascaras = [LeerEscribirEjecutar, TodoElAcceso];

    private const uint WinstaAllAccess = 0x0000037F;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint hilo);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
