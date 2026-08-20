using System.Runtime.InteropServices;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Input;

/// <summary>
/// El portapapeles de texto de la PC controlada. Fase 21.
///
/// Interop crudo y no System.Windows.Clipboard porque el host es una aplicacion
/// de consola: arrastrar WPF entero para leer una cadena seria cambiar el tamano
/// del instalador del agente por comodidad.
///
/// TODO desde el hilo de captura, igual que SendInput. El portapapeles pertenece
/// a la ESTACION de ventanas, y este es el hilo que ya esta atado a la
/// interactiva.
/// </summary>
public static class ClipboardBridge
{
    /// <summary>
    /// Numero de secuencia del portapapeles la ultima vez que se miro. Windows
    /// lo incrementa en cada cambio, asi que detectar que hay algo nuevo cuesta
    /// UNA llamada y no hay que abrir el portapapeles ni comparar cadenas.
    ///
    /// Abrirlo cada 500 ms para comprobar seria pelearse con cada aplicacion de
    /// la PC por un recurso exclusivo, para nada el 99 % de las veces.
    /// </summary>
    private static uint _secuencia;

    /// <summary>Lo ultimo que pasó por aqui, en cualquiera de los dos sentidos.
    /// Es lo que evita el eco: sin esto, lo que llega del tecnico se detecta como
    /// cambio local y se le devuelve.</summary>
    private static string? _ultimo;

    /// <summary>
    /// Lo que haya de nuevo en el portapapeles, o nada si no cambio.
    ///
    /// Los archivos se miran ANTES que el texto: al copiar en el Explorador,
    /// Windows pone CF_HDROP y ademas texto con los nombres. Mirar el texto
    /// primero convertiria una copia de archivos en una copia de nombres.
    /// </summary>
    public static (string? Texto, IReadOnlyList<string> Archivos) LeerSiCambio()
    {
        var secuencia = GetClipboardSequenceNumber();

        if (secuencia == _secuencia)
            return (null, []);

        _secuencia = secuencia;

        var archivos = LeerArchivos();

        if (archivos.Count > 0)
        {
            // La firma hace de "lo ultimo" para los archivos igual que el texto
            // para el texto: sin ella, lo que llega del tecnico se detectaria
            // como copia local y se le devolveria.
            var firma = string.Join("|", archivos);

            if (firma == _ultimo)
                return (null, []);

            _ultimo = firma;
            return (null, archivos);
        }

        if (!IsClipboardFormatAvailable(CfUnicodeText))
            return (null, []);

        var texto = Leer();

        if (texto is null || texto == _ultimo)
            return (null, []);

        // Demasiado grande no se recorta: se ignora. Mandar medio texto es peor
        // que no mandarlo, porque quien pega no ve que le falta la otra mitad.
        if (texto.Length > RemoteSessionProtocol.MaxClipboardChars)
            return (null, []);

        _ultimo = texto;
        return (texto, []);
    }

    /// <summary>
    /// Los ARCHIVOS del portapapeles, si los hay. Fase 25.
    ///
    /// Se pregunta antes que por el texto: al copiar en el Explorador, Windows
    /// pone CF_HDROP y ademas texto con los nombres. Mirar el texto primero
    /// convertiria una copia de archivos en una copia de nombres, que es
    /// exactamente lo que no se quiere.
    /// </summary>
    public static IReadOnlyList<string> LeerArchivos()
    {
        if (!IsClipboardFormatAvailable(CfHdrop) || !Abrir())
            return [];

        try
        {
            var mango = GetClipboardData(CfHdrop);

            if (mango == IntPtr.Zero)
                return [];

            // 0xFFFFFFFF pide la CUENTA en vez de un nombre concreto. Es la
            // convencion de DragQueryFile, no un centinela inventado.
            var cuantos = DragQueryFile(mango, 0xFFFFFFFF, null, 0);
            var rutas = new List<string>((int)cuantos);

            for (uint i = 0; i < cuantos; i++)
            {
                var largo = DragQueryFile(mango, i, null, 0);

                if (largo == 0)
                    continue;

                var bufer = new System.Text.StringBuilder((int)largo + 1);

                if (DragQueryFile(mango, i, bufer, (uint)bufer.Capacity) > 0)
                    rutas.Add(bufer.ToString());
            }

            return rutas;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Pone archivos en el portapapeles. Las rutas tienen que existir YA en esta
    /// maquina: CF_HDROP son referencias, no contenido, y pegar rutas que no
    /// existen da un error del Explorador y ninguna pista de por que.
    /// </summary>
    public static bool EscribirArchivos(IReadOnlyList<string> rutas)
    {
        if (rutas.Count == 0 || !Abrir())
            return false;

        var mango = IntPtr.Zero;

        try
        {
            if (!EmptyClipboard())
                return false;

            var contenido = Dropfiles(rutas);

            mango = GlobalAlloc(GmemMoveable, (UIntPtr)contenido.Length);

            if (mango == IntPtr.Zero)
                return false;

            var bloque = GlobalLock(mango);

            if (bloque == IntPtr.Zero)
                return false;

            try
            {
                Marshal.Copy(contenido, 0, bloque, contenido.Length);
            }
            finally
            {
                GlobalUnlock(mango);
            }

            if (SetClipboardData(CfHdrop, mango) == IntPtr.Zero)
                return false;

            mango = IntPtr.Zero;   // ya es del portapapeles
            return true;
        }
        finally
        {
            CloseClipboard();

            if (mango != IntPtr.Zero)
                GlobalFree(mango);

            _secuencia = GetClipboardSequenceNumber();
        }
    }

    /// <summary>
    /// El bloque CF_HDROP entero: cabecera DROPFILES de 20 bytes y detras la
    /// lista de rutas en UTF-16, cada una terminada en nulo y el conjunto en un
    /// nulo mas.
    ///
    ///   0  pFiles  (uint32)  desplazamiento donde empieza la lista = 20
    ///   4  pt.x    (int32)
    ///   8  pt.y    (int32)
    ///  12  fNC     (int32)
    ///  16  fWide   (int32)   1 = UTF-16. En 0, Windows lee la lista como ANSI
    ///                        y las rutas salen troceadas por los nulos altos.
    ///
    /// Publico y separado de la llamada a Win32 para poder probarlo: un
    /// desplazamiento mal puesto o el nulo final que falta no dan error, dan
    /// rutas basura al pegar.
    /// </summary>
    public static byte[] Dropfiles(IReadOnlyList<string> rutas)
    {
        const int cabecera = 20;

        // Un nulo por ruta y otro para cerrar la lista.
        var caracteres = rutas.Sum(r => r.Length + 1) + 1;
        var bloque = new byte[cabecera + caracteres * 2];

        BitConverter.GetBytes(cabecera).CopyTo(bloque, 0);
        BitConverter.GetBytes(1).CopyTo(bloque, 16);

        var desplazamiento = cabecera;

        foreach (var ruta in rutas)
        {
            var texto = System.Text.Encoding.Unicode.GetBytes(ruta);

            texto.CopyTo(bloque, desplazamiento);

            // +2 del terminador, que ya viene a cero del array recien creado.
            desplazamiento += texto.Length + 2;
        }

        return bloque;
    }

    private static string? Leer()
    {
        if (!Abrir())
            return null;

        try
        {
            var mango = GetClipboardData(CfUnicodeText);

            if (mango == IntPtr.Zero)
                return null;

            var bloque = GlobalLock(mango);

            if (bloque == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(bloque);
            }
            finally
            {
                GlobalUnlock(mango);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Pone lo que mando el tecnico. No devuelve error: si el
    /// portapapeles esta ocupado se reintenta en la siguiente vuelta.</summary>
    public static bool Escribir(string texto)
    {
        if (texto.Length > RemoteSessionProtocol.MaxClipboardChars || !Abrir())
            return false;

        var bytes = (texto.Length + 1) * 2;
        var mango = IntPtr.Zero;

        try
        {
            if (!EmptyClipboard())
                return false;

            // GMEM_MOVEABLE y no un bloque fijo: SetClipboardData toma posesion
            // del handle y lo libera cuando el portapapeles cambia, y solo sabe
            // hacerlo con memoria movible.
            mango = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);

            if (mango == IntPtr.Zero)
                return false;

            var bloque = GlobalLock(mango);

            if (bloque == IntPtr.Zero)
                return false;

            try
            {
                Marshal.Copy(texto.ToCharArray(), 0, bloque, texto.Length);
                Marshal.WriteInt16(bloque, texto.Length * 2, 0);   // terminador
            }
            finally
            {
                GlobalUnlock(mango);
            }

            if (SetClipboardData(CfUnicodeText, mango) == IntPtr.Zero)
                return false;

            // A partir de aqui el bloque es del portapapeles: liberarlo seria
            // dejarle un puntero muerto.
            mango = IntPtr.Zero;
            _ultimo = texto;

            return true;
        }
        finally
        {
            CloseClipboard();

            if (mango != IntPtr.Zero)
                GlobalFree(mango);

            // Despues de cerrar: nuestro propio cambio tambien incrementa la
            // secuencia, y sin anotarlo se detectaria como cambio local y se
            // devolveria al tecnico de vuelta.
            _secuencia = GetClipboardSequenceNumber();
        }
    }

    /// <summary>
    /// Tres intentos sin esperar entre ellos. El portapapeles es exclusivo y
    /// cualquier aplicacion puede tenerlo abierto un instante; dormir aqui seria
    /// dormir el hilo de captura, que es el que dibuja.
    /// </summary>
    private static bool Abrir()
    {
        for (var intento = 0; intento < 3; intento++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
        }

        return false;
    }

    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(
        IntPtr drop, uint indice, System.Text.StringBuilder? nombre, uint tamano);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
