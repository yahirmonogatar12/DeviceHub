using System.Runtime.InteropServices;
using System.Text;

namespace DeviceHub.RemoteHost.Input;

/// <summary>
/// La segunda mitad de arrastrar y soltar: el Ctrl+V donde el tecnico solto.
///
/// POR QUE NO SE PREGUNTA LA RUTA. La carpeta que tiene abierta una ventana del
/// Explorador solo la sabe el Explorador, y se pide por COM (IShellWindows).
/// Este proceso corre como SYSTEM, asi que esa llamada devolveria las ventanas
/// de SYSTEM -- ninguna. Se puede leer la barra de direcciones por
/// automatizacion, pero el texto lleva el idioma de Windows delante y eso es un
/// analisis que se rompe en la primera PC en ingles.
///
/// Asi que no se averigua la ruta: se hace lo mismo que haria una persona. Los
/// bytes ya estan en el portapapeles de esta maquina, y esto pone la ventana
/// delante y manda Ctrl+V. Explorer pega donde tenga abierto.
///
/// LA COMPROBACION DE CLASE NO ES COSMETICA. Sin ella, un Ctrl+V a ciegas cae
/// sobre la ventana que hubiera debajo -- y en estas PCs debajo hay software de
/// produccion. Solo se teclea si es una carpeta del Explorador o el escritorio.
/// </summary>
public static class PegarEnPunto
{
    /// <summary>Lo que paso, para que el visor lo diga en vez de callarse.</summary>
    public enum Resultado { Pegado, NoEsUnaCarpeta, NoHayVentana }

    /// <summary>
    /// Clases de ventana que aceptan pegar archivos.
    ///
    /// CabinetWClass es una carpeta con su marco; ExploreWClass es la vista con
    /// el arbol a la izquierda. Progman y WorkerW son el escritorio -- cual de
    /// las dos queda delante depende de si hay fondo de pantalla animado, asi que
    /// se aceptan las dos.
    /// </summary>
    private static readonly string[] Carpetas =
        ["CabinetWClass", "ExploreWClass", "Progman", "WorkerW"];

    public static Resultado Pegar(int x, int y)
    {
        var ventana = WindowFromPoint(new POINT { X = x, Y = y });

        if (ventana == IntPtr.Zero)
            return Resultado.NoHayVentana;

        // Del control concreto que haya bajo el cursor hasta su ventana de
        // arriba: el punto puede caer en la lista de archivos, en el arbol o en
        // la barra de estado, y la clase que interesa es la del marco.
        var raiz = GetAncestor(ventana, GaRoot);

        if (raiz == IntPtr.Zero)
            raiz = ventana;

        if (!EsCarpeta(raiz))
            return Resultado.NoEsUnaCarpeta;

        SetForegroundWindow(raiz);

        // Un respiro antes de teclear. SetForegroundWindow vuelve en cuanto
        // pide el cambio, no cuando el foco ya esta puesto, y un Ctrl+V que
        // llega antes se lo come la ventana anterior.
        Thread.Sleep(120);

        CtrlV();
        return Resultado.Pegado;
    }

    private static bool EsCarpeta(IntPtr ventana)
    {
        var nombre = new StringBuilder(64);

        if (GetClassName(ventana, nombre, nombre.Capacity) == 0)
            return false;

        return Array.Exists(Carpetas, c => c.Equals(nombre.ToString(), StringComparison.Ordinal));
    }

    private static void CtrlV()
    {
        // Las cuatro en UNA sola llamada. Repartidas en varias, algo puede
        // colarse en medio y dejar el Control hundido en la PC de planta.
        INPUT[] teclas =
        [
            Tecla(VkControl, false),
            Tecla(VkV, false),
            Tecla(VkV, true),
            Tecla(VkControl, true)
        ];

        SendInput((uint)teclas.Length, teclas, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Tecla(ushort vk, bool soltar) => new()
    {
        type = InputKeyboard,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = soltar ? KeyeventfKeyup : 0
            }
        }
    };

    private const int GaRoot = 2;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT punto);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr ventana, uint bandera);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr ventana, StringBuilder nombre, int largo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr ventana);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint cuantas, INPUT[] entradas, int tamano);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint codigo, uint tipo);
}
