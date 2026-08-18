using System.Runtime.InteropServices;

namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Convierte el BGRA que manda el host en un cursor de Windows. Fase 11.
///
/// Se hace con CreateIconIndirect y no construyendo un .cur en memoria porque el
/// destino es una ventana WIN32 -- la del video -- y esa responde a WM_SETCURSOR
/// con un HCURSOR, no con un Cursor de WPF. Fabricar el .cur solo para que WPF
/// lo volviera a convertir seria dar un rodeo.
/// </summary>
public static class CursorRemoto
{
    /// <summary>
    /// Devuelve un HCURSOR nuevo, o IntPtr.Zero si no se pudo. Quien lo recibe es
    /// su dueno y tiene que destruirlo con DestroyIcon.
    /// </summary>
    public static IntPtr Crear(byte[] bgra, int ancho, int alto, int hotspotX, int hotspotY)
    {
        if (ancho <= 0 || alto <= 0 || bgra.Length < ancho * alto * 4)
            return IntPtr.Zero;

        // La mascara va entera a cero: con 32 bits por pixel Windows usa el ALFA
        // del mapa de color y la mascara AND deja de mandar. Sigue siendo
        // obligatoria en la estructura aunque no decida nada.
        var mascara = new byte[(ancho + 7) / 8 * alto];

        var color = CreateBitmap(ancho, alto, 1, 32, bgra);
        var and = CreateBitmap(ancho, alto, 1, 1, mascara);

        try
        {
            if (color == IntPtr.Zero || and == IntPtr.Zero)
                return IntPtr.Zero;

            var icono = new ICONINFO
            {
                fIcon = false,                    // false = cursor, true = icono
                xHotspot = hotspotX,
                yHotspot = hotspotY,
                hbmMask = and,
                hbmColor = color
            };

            return CreateIconIndirect(ref icono);
        }
        finally
        {
            // CreateIconIndirect COPIA los mapas de bits, asi que estos se
            // liberan aqui. No hacerlo es una fuga de GDI silenciosa que solo se
            // nota cuando la sesion lleva horas y el cursor deja de cambiar.
            if (color != IntPtr.Zero) DeleteObject(color);
            if (and != IntPtr.Zero) DeleteObject(and);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int ancho, int alto, uint planos, uint bits, byte[] datos);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objeto);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO icono);
}
