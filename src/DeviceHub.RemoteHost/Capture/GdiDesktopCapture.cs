using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Captura por GDI. El respaldo para el ESCRITORIO SEGURO. Fase 19.
///
/// POR QUE EXISTE, que es lo que costo tres intentos averiguar:
///
///   DXGI Desktop Duplication ata el dispositivo D3D al escritorio en el momento
///   de crearlo. Ni SetThreadDesktop, ni un hilo recien nacido, ni relanzar el
///   proceso con lpDesktop lo mueven despues. No hay truco: en el escritorio de
///   Winlogon -- la pantalla de bloqueo, el login, los dialogos de UAC -- la
///   duplicacion no esta disponible y punto.
///
///   GDI no ata nada. Se re-atacha el hilo al escritorio de entrada, se sueltan
///   los DC, se vuelven a crear, y BitBlt captura lo que haya. Es exactamente lo
///   que hace Chrome Remote Desktop -- y QuickDesk, que usa su motor: DXGI como
///   ruta rapida y GDI como respaldo, y el escritorio seguro cae siempre en el
///   respaldo.
///
/// EL PRECIO, que es real y por eso esto no es la ruta principal: BitBlt copia
/// por CPU y la imagen tiene que subir despues a la GPU para que el codificador
/// la vea. Son unos 8 MB por frame a 1080p. En la pantalla de bloqueo da igual
/// -- la imagen esta quieta y la sesion dura lo que se tarda en escribir una
/// contrasena -- pero seria inaceptable para un escritorio de trabajo.
///
/// La Fase 1 descarto Graphics.CopyFromScreen por esa misma razon y tenia razon.
/// Lo que faltaba era darse cuenta de que el escritorio seguro es justo el caso
/// que pedia el respaldo.
/// </summary>
public sealed class GdiDesktopCapture : IScreenCapture
{
    private readonly long _inicioTicks = Stopwatch.GetTimestamp();

    private ID3D11Device _device = null!;
    private IDXGIAdapter1 _adapter = null!;

    /// <summary>Textura que se rellena desde la CPU y textura que ve el
    /// codificador. Son dos porque una dinamica no vale como entrada de un
    /// procesador de video: se escribe en la primera y se copia a la segunda.</summary>
    private ID3D11Texture2D _subida = null!;
    private ID3D11Texture2D _lienzo = null!;

    private IntPtr _dcEscritorio, _dcMemoria, _mapa, _bits, _anterior;
    private IntPtr _escritorioAtado;
    private string _nombreEscritorio = string.Empty;

    private bool _frameVivo;
    private ulong _frameId;
    private long _ultimaCaptura;

    /// <summary>
    /// Techo de 10 frames por segundo.
    ///
    /// DXGI se frena solo: AcquireNextFrame BLOQUEA hasta que la pantalla cambia.
    /// GDI no -- BitBlt devuelve al instante -- asi que sin freno este bucle
    /// capturaria y subiria 8 MB a la GPU tan rapido como el procesador diera,
    /// quemando un nucleo entero de una PC de planta para mostrar una pantalla
    /// de bloqueo que no se mueve.
    ///
    /// ponytail: 10 FPS fijos. Lo que se ve aqui es alguien escribiendo una
    /// contrasena; si algun dia hay que ver algo con movimiento en el escritorio
    /// seguro, esto se hace adaptativo comparando frames.
    /// </summary>
    private static readonly long IntervaloTicks = Stopwatch.Frequency / 10;

    public GdiDesktopCapture()
    {
        // La pantalla PRINCIPAL. La de bloqueo sale ahi, y capturar el escritorio
        // virtual entero solo anadiria monitores en negro a un caso que ya es el
        // respaldo lento.
        Width = ParOSuperior(GetSystemMetrics(SmCxScreen));
        Height = ParOSuperior(GetSystemMetrics(SmCyScreen));

        Abrir();
    }

    /// <summary>H.264 codifica en macrobloques y no traga dimensiones impares.
    /// DXGI siempre da pares; GetSystemMetrics no lo garantiza.</summary>
    private static int ParOSuperior(int valor) => valor % 2 == 0 ? valor : valor + 1;

    public string Adapter { get; private set; } = string.Empty;
    public string Output => $"GDI {_nombreEscritorio}";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Device Device => _device;
    public Vortice.Luid AdapterLuid { get; private set; }
    public uint AdapterVendorId { get; private set; }
    public int DesktopLeft => 0;
    public int DesktopTop => 0;

    public long Timeouts { get; private set; }
    public long AccessLostRecoveries { get; private set; }
    public long ResolutionChanges => 0;
    public long Dropped => 0;

    /// <summary>
    /// GDI no entrega el puntero, igual que DXGI. Aqui NO se recupera: en la
    /// pantalla de bloqueo el tecnico ve su propio cursor y le basta para pulsar
    /// "Iniciar sesion". Se anade el dia que estorbe.
    /// </summary>
    public CursorState? TomarCursor() => null;

    public Task<VideoFrame?> CaptureAsync(CancellationToken cancellationToken)
        => Task.FromResult(Capturar(cancellationToken));

    private VideoFrame? Capturar(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_frameVivo)
            throw new InvalidOperationException("Hay que disponer el frame anterior antes de pedir otro.");

        // El freno va ANTES de tocar nada: dormir despues de haber copiado los
        // 8 MB no ahorra el trabajo, solo lo reparte peor.
        var desde = Stopwatch.GetTimestamp() - _ultimaCaptura;

        if (_ultimaCaptura != 0 && desde < IntervaloTicks)
        {
            Thread.Sleep((int)((IntervaloTicks - desde) * 1000 / Stopwatch.Frequency));

            if (cancellationToken.IsCancellationRequested)
                return null;
        }

        _ultimaCaptura = Stopwatch.GetTimestamp();

        SeguirAlEscritorioDeEntrada();

        if (_dcEscritorio == IntPtr.Zero || _dcMemoria == IntPtr.Zero)
        {
            // Sin DC no hay nada que capturar. Se duerme para no convertir el
            // fallo en un bucle ocupado.
            Timeouts++;
            Thread.Sleep(100);
            return null;
        }

        // CAPTUREBLT ademas de SRCCOPY: sin el no salen las ventanas por capas, y
        // la interfaz de inicio de sesion de Windows es una de ellas.
        if (!BitBlt(_dcMemoria, 0, 0, Width, Height, _dcEscritorio, 0, 0, SrcCopy | CaptureBlt))
        {
            Timeouts++;
            Thread.Sleep(100);
            return null;
        }

        Subir();
        _frameVivo = true;

        // Siempre "cambio": GDI no sabe si la pantalla se movio. El codificador
        // se come el coste, y en una pantalla quieta eso son frames casi vacios.
        return new VideoFrame(
            _lienzo, Width, Height, ++_frameId, TranscurridoUs(), desktopChanged: true,
            release: () => _frameVivo = false);
    }

    /// <summary>
    /// CPU -> GPU. Fila a fila porque el paso del DIB y el de la textura casi
    /// nunca coinciden: la GPU alinea sus filas, y copiar de golpe da una imagen
    /// inclinada.
    /// </summary>
    private void Subir()
    {
        var contexto = _device.ImmediateContext;
        var mapa = contexto.Map(_subida, 0, MapMode.WriteDiscard);

        try
        {
            var anchoBytes = Width * 4;
            var fila = new byte[anchoBytes];

            for (var y = 0; y < Height; y++)
            {
                Marshal.Copy(_bits + (y * anchoBytes), fila, 0, anchoBytes);
                Marshal.Copy(fila, 0, mapa.DataPointer + (int)(y * mapa.RowPitch), anchoBytes);
            }
        }
        finally
        {
            contexto.Unmap(_subida, 0);
        }

        contexto.CopyResource(_lienzo, _subida);
    }

    /// <summary>
    /// El corazon del respaldo, y lo unico que DXGI no puede hacer.
    ///
    /// Se comprueba en CADA captura, no una vez: la pantalla se bloquea cuando le
    /// parece. Al cambiar hay que soltar los DC viejos -- pertenecen al
    /// escritorio anterior y no dibujan nada del nuevo -- y crearlos de cero.
    /// </summary>
    private void SeguirAlEscritorioDeEntrada()
    {
        var entrada = OpenInputDesktop(0, false, DesktopGenericAll);

        if (entrada == IntPtr.Zero)
            return;

        var nombre = NombreDe(entrada);

        if (nombre == _nombreEscritorio && _dcEscritorio != IntPtr.Zero)
        {
            CloseDesktop(entrada);
            return;
        }

        if (!SetThreadDesktop(entrada))
        {
            CloseDesktop(entrada);
            return;
        }

        SoltarGdi();

        if (_escritorioAtado != IntPtr.Zero)
            CloseDesktop(_escritorioAtado);

        _escritorioAtado = entrada;
        _nombreEscritorio = nombre;
        AccessLostRecoveries++;

        CrearGdi();
    }

    private void CrearGdi()
    {
        // CreateDC y no GetDC(null): GetDC entrega el DC de la ventana de
        // escritorio que hubiera antes del salto.
        _dcEscritorio = CreateDC("DISPLAY", null, null, IntPtr.Zero);

        if (_dcEscritorio == IntPtr.Zero)
            return;

        _dcMemoria = CreateCompatibleDC(_dcEscritorio);

        if (_dcMemoria == IntPtr.Zero)
            return;

        // Alto NEGATIVO: DIB de arriba a abajo, que es como lo espera D3D. Con
        // alto positivo la imagen llega del reves y nadie avisa.
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };

        _mapa = CreateDIBSection(_dcMemoria, ref info, 0, out _bits, IntPtr.Zero, 0);

        if (_mapa != IntPtr.Zero)
            _anterior = SelectObject(_dcMemoria, _mapa);
    }

    private void SoltarGdi()
    {
        if (_dcMemoria != IntPtr.Zero && _anterior != IntPtr.Zero)
            SelectObject(_dcMemoria, _anterior);

        if (_mapa != IntPtr.Zero) DeleteObject(_mapa);
        if (_dcMemoria != IntPtr.Zero) DeleteDC(_dcMemoria);
        if (_dcEscritorio != IntPtr.Zero) DeleteDC(_dcEscritorio);

        _anterior = _mapa = _dcMemoria = _dcEscritorio = _bits = IntPtr.Zero;
    }

    private void Abrir()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        if (factory.EnumAdapters1(0, out var adaptador).Failure || adaptador is null)
            throw new ScreenCaptureUnavailableException("No hay adaptador grafico para el respaldo GDI.");

        _adapter = adaptador;
        Adapter = adaptador.Description.Description.Trim();
        AdapterVendorId = adaptador.Description.VendorId;
        AdapterLuid = adaptador.Description1.Luid;

        var resultado = D3D11.D3D11CreateDevice(
            _adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out _device!);

        if (resultado.Failure || _device is null)
            throw new ScreenCaptureUnavailableException(
                $"No se pudo crear el dispositivo D3D11 para el respaldo GDI: {resultado}");

        _subida = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        // Las mismas banderas que una textura de Desktop Duplication: el
        // convertidor a NV12 crea una vista de entrada de video sobre esta.
        _lienzo = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });

        SeguirAlEscritorioDeEntrada();
    }

    private long TranscurridoUs()
        => (Stopwatch.GetTimestamp() - _inicioTicks) * 1_000_000L / Stopwatch.Frequency;

    private static string NombreDe(IntPtr escritorio)
    {
        var bufer = new byte[256];

        return GetUserObjectInformation(escritorio, UoiName, bufer, bufer.Length, out var usados)
            ? System.Text.Encoding.Unicode.GetString(bufer, 0, Math.Max(usados - 2, 0))
            : string.Empty;
    }

    public void Dispose()
    {
        SoltarGdi();

        if (_escritorioAtado != IntPtr.Zero)
        {
            CloseDesktop(_escritorioAtado);
            _escritorioAtado = IntPtr.Zero;
        }

        _subida?.Dispose();
        _lienzo?.Dispose();
        _device?.Dispose();
        _adapter?.Dispose();
    }

    // ------------------------------------------------------------------ interop

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint DesktopGenericAll = 0x10000000;
    private const int UoiName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int indice);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint banderas, [MarshalAs(UnmanagedType.Bool)] bool hereda, uint acceso);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr escritorio);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr escritorio);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr objeto, int indice, byte[] info, int tamano, out int usados);

    [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string? dispositivo, string? salida, IntPtr modo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFOHEADER info, uint uso, out IntPtr bits, IntPtr seccion, uint desplazamiento);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr objeto);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objeto);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destino, int x, int y, int ancho, int alto,
        IntPtr origen, int origenX, int origenY, uint operacion);
}
