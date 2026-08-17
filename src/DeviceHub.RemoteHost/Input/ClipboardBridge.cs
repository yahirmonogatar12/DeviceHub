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

    /// <summary>Devuelve el texto solo si CAMBIO desde la ultima llamada.</summary>
    public static string? LeerSiCambio()
    {
        var secuencia = GetClipboardSequenceNumber();

        if (secuencia == _secuencia)
            return null;

        _secuencia = secuencia;

        if (!IsClipboardFormatAvailable(CfUnicodeText))
            return null;

        var texto = Leer();

        if (texto is null || texto == _ultimo)
            return null;

        // Demasiado grande no se recorta: se ignora. Mandar medio texto es peor
        // que no mandarlo, porque quien pega no ve que le falta la otra mitad.
        if (texto.Length > RemoteSessionProtocol.MaxClipboardChars)
            return null;

        _ultimo = texto;
        return texto;
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
    private const uint GmemMoveable = 0x0002;

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
