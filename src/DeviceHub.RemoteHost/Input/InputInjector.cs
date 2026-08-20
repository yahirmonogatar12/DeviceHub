using System.Runtime.InteropServices;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Input;

/// <summary>
/// Aplica en esta PC lo que el tecnico hace en la suya. Fases 9 y 10.
///
/// Las coordenadas llegan NORMALIZADAS 0..1 sobre la pantalla capturada, nunca
/// en pixeles: la resolucion del escritorio remoto puede cambiar a media sesion
/// y unos pixeles enviados antes del cambio apuntarian a otro sitio.
///
/// SendInput y no SetCursorPos + mouse_event: SendInput inyecta en la misma cola
/// que el hardware, respeta el orden entre teclas y clics, y es lo unico que
/// aceptan las aplicaciones que leen la entrada a bajo nivel.
/// </summary>
public sealed class InputInjector(int ancho, int alto, int izquierda, int arriba)
{
    /// <summary>
    /// El escritorio VIRTUAL, que con varios monitores no empieza en 0,0. Se lee
    /// una vez por evento y no se cachea: enchufar un monitor lo cambia sin
    /// avisar, y un cache viejo manda el raton a un sitio que ya no existe.
    /// </summary>
    private static (int X, int Y, int Ancho, int Alto) EscritorioVirtual() => (
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 1),
        Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 1));

    public long Applied { get; private set; }

    /// <summary>
    /// Lo que esta hundido AHORA MISMO en la maquina remota.
    ///
    /// Sin esta lista no hay forma de deshacerlo: SendInput no tiene "suelta
    /// todo", y el host no ve el teclado fisico del tecnico -- solo los eventos
    /// que le llegaron. Si el KeyUp no llego, esa tecla se queda hundida y nadie
    /// en la PC de planta puede despegarla.
    /// </summary>
    private readonly HashSet<uint> _teclasHundidas = [];

    private readonly HashSet<MouseButtonId> _botonesHundidos = [];

    /// <summary>
    /// Suelta todo lo que quedo hundido. Se llama al conectar, al reconectar y
    /// al cerrar la captura.
    ///
    /// Es idempotente a proposito: soltar una tecla que no estaba pulsada no
    /// hace nada, asi que llamarlo de mas es gratis y llamarlo de menos deja un
    /// Ctrl invisible pegado.
    /// </summary>
    public int SoltarTodo()
    {
        var cuantos = 0;

        foreach (var vk in _teclasHundidas.ToArray())
        {
            Soltar(vk);
            cuantos++;
        }

        _teclasHundidas.Clear();

        foreach (var boton in _botonesHundidos.ToArray())
        {
            Pulsar(boton, false);
            cuantos++;
        }

        _botonesHundidos.Clear();

        return cuantos;
    }
    public long Rejected { get; private set; }

    public void Apply(InputEvent evento)
    {
        switch (evento.EventCase)
        {
            case InputEvent.EventOneofCase.MouseMove:
                Mover(evento.MouseMove.X, evento.MouseMove.Y);
                break;

            case InputEvent.EventOneofCase.MouseButton:
                var boton = evento.MouseButton;

                // El movimiento va PEGADO al clic, en el mismo SendInput no,
                // pero si antes y sin hueco: soltar el clic donde el cursor
                // estaba hace un instante es como se pierden los arrastres.
                Mover(boton.X, boton.Y);
                Pulsar(boton.Button, boton.Pressed);
                break;

            case InputEvent.EventOneofCase.MouseWheel:
                Rueda(evento.MouseWheel.Delta, evento.MouseWheel.Horizontal);
                break;

            case InputEvent.EventOneofCase.Key:
                Tecla(evento.Key);
                break;

            default:
                Rejected++;
                return;
        }
    }

    private void Mover(double x, double y)
    {
        // Fuera de la pantalla no se recorta a los bordes: se descarta. Recortar
        // convertiria un evento corrupto en un clic real en una esquina.
        if (x is < 0 or > 1 || y is < 0 or > 1)
        {
            Rejected++;
            return;
        }

        var virtualDesk = EscritorioVirtual();

        Enviar(new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = Absoluto(x, izquierda, ancho, virtualDesk.X, virtualDesk.Ancho),
                    dy = Absoluto(y, arriba, alto, virtualDesk.Y, virtualDesk.Alto),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                }
            }
        });
    }

    /// <summary>
    /// De 0..1 sobre NUESTRA pantalla al rango 0..65535 del escritorio VIRTUAL,
    /// que es lo unico que entiende MOUSEEVENTF_ABSOLUTE.
    ///
    /// Los dos pasos son necesarios y es facil olvidar el segundo: con un solo
    /// monitor el escritorio virtual coincide con la pantalla y la traslacion es
    /// invisible, asi que el fallo no aparece hasta que alguien enchufa el
    /// segundo -- y entonces el raton se va al monitor de al lado.
    ///
    /// Publico y estatico para poder probarlo: es aritmetica pura, y el resto de
    /// esta clase depende de que haya un escritorio de verdad delante.
    /// </summary>
    public static int Absoluto(
        double normalizado, int origenPantalla, int tamPantalla, int origenVirtual, int tamVirtual)
    {
        var pixel = origenPantalla + normalizado * tamPantalla;

        return (int)Math.Round((pixel - origenVirtual) * 65535.0 / Math.Max(tamVirtual, 1));
    }

    private void Pulsar(MouseButtonId boton, bool pulsado)
    {
        var bandera = boton switch
        {
            MouseButtonId.MouseButtonLeft => pulsado ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButtonId.MouseButtonRight => pulsado ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButtonId.MouseButtonMiddle => pulsado ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u
        };

        if (bandera == 0)
        {
            Rejected++;
            return;
        }

        if (pulsado)
            _botonesHundidos.Add(boton);
        else
            _botonesHundidos.Remove(boton);

        Enviar(new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = bandera } }
        });
    }

    /// <summary>Suelta una tecla por codigo virtual, sin evento de por medio.
    /// Es lo que hace falta para deshacer lo que quedo hundido: del KeyDown
    /// original ya no se conserva ni el scan code ni la marca de extendida.</summary>
    private void Soltar(uint virtualKey)
    {
        var scan = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
        var banderas = KEYEVENTF_KEYUP | (scan != 0 ? KEYEVENTF_SCANCODE : 0);

        // Las de la derecha y las de navegacion son extendidas. Soltarlas sin la
        // marca deja hundida la otra mitad del par: Windows trata Ctrl izquierdo
        // y derecho como teclas distintas aunque compartan codigo virtual.
        if (virtualKey is 0xA3 or 0xA5 or 0x2D or 0x2E or 0x24 or 0x23
            or 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28 or 0x5B or 0x5C)
        {
            banderas |= KEYEVENTF_EXTENDEDKEY;
        }

        Enviar(new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = scan != 0 ? (ushort)0 : (ushort)virtualKey,
                    wScan = scan,
                    dwFlags = banderas
                }
            }
        });
    }

    private void Rueda(int delta, bool horizontal)
        => Enviar(new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    mouseData = delta,
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL
                }
            }
        });

    private void Tecla(KeyEvent tecla)
    {
        if (tecla.VirtualKey > 0xFF)
        {
            Rejected++;
            return;
        }

        var banderas = KEYEVENTF_SCANCODE;

        if (!tecla.Pressed)
            banderas |= KEYEVENTF_KEYUP;

        // La bandera de extendida no es decorativa: sin ella el teclado numerico
        // y las flechas comparten scan code y Windows resuelve la ambiguedad al
        // reves de lo que el tecnico pulso.
        if (tecla.Extended)
            banderas |= KEYEVENTF_EXTENDEDKEY;

        var scan = tecla.ScanCode != 0
            ? (ushort)tecla.ScanCode
            : (ushort)MapVirtualKey(tecla.VirtualKey, MAPVK_VK_TO_VSC);

        // Sin scan code utilizable se cae al codigo virtual. Es peor -- las
        // aplicaciones que leen el hardware no lo ven -- pero es mejor que
        // tragarse la tecla.
        if (scan == 0)
            banderas &= ~KEYEVENTF_SCANCODE;

        // Se apunta ANTES de mandarla. Si SendInput falla la tecla no quedo
        // hundida, pero apuntarla de mas solo cuesta un KeyUp de mas al soltar
        // -- que no hace nada -- y apuntarla de menos deja un Ctrl invisible.
        if (tecla.Pressed)
            _teclasHundidas.Add(tecla.VirtualKey);
        else
            _teclasHundidas.Remove(tecla.VirtualKey);

        Enviar(new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)((banderas & KEYEVENTF_SCANCODE) != 0 ? 0 : tecla.VirtualKey),
                    wScan = scan,
                    dwFlags = banderas
                }
            }
        });
    }

    /// <summary>
    /// SOLO se llama desde el hilo de captura.
    ///
    /// SendInput no inyecta "en Windows": inyecta en el escritorio al que esta
    /// atado el hilo que llama. El hilo de captura ya esta atado al escritorio
    /// activo -- InputDesktop lo mantiene asi -- y ademas es el unico dueño de
    /// DXGI y del codificador.
    ///
    /// El intento anterior fue atar TAMBIEN el hilo de red, para no coordinarse
    /// con el de captura. Fue peor: dos hilos abriendo y CERRANDO handles del
    /// mismo escritorio por su cuenta, y el CloseDesktop del hilo de red tiraba
    /// el escritorio bajo los pies del de captura. El sintoma no apuntaba a nada
    /// -- el visor recibia una configuracion que ningun decodificador aceptaba.
    ///
    /// Evitar la coordinacion era el problema, no la solucion.
    /// </summary>
    private void Enviar(INPUT entrada)
    {
        if (SendInput(1, [entrada], Marshal.SizeOf<INPUT>()) == 1)
            Applied++;
        else
            Rejected++;   // el escritorio activo no es el nuestro, o falta privilegio
    }

    // ------------------------------------------------------------------ interop

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MAPVK_VK_TO_VSC = 0;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public int mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
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
        public int type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
