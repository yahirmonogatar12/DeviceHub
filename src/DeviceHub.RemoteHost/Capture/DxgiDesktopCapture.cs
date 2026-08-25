using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Captura del escritorio con DXGI Desktop Duplication.
///
/// Es la unica implementacion real. Graphics.CopyFromScreen queda descartado:
/// copia por CPU, no sabe que ha cambiado y no llega a 1080p30 sin comerse un
/// nucleo.
///
/// No es thread-safe: el bucle de captura vive en un solo hilo, que es como se
/// usa. Meterle un candado aqui solo escondería un uso equivocado.
/// </summary>
public sealed class DxgiDesktopCapture : IScreenCapture
{
    /// <summary>
    /// Espera de AcquireNextFrame. No es un spin: el driver despierta al llegar
    /// una presentacion, asi que un valor generoso no cuesta CPU y evita
    /// convertir una pantalla quieta en un bucle ocupado.
    /// </summary>
    private const int AcquireTimeoutMs = 100;

    /// <summary>
    /// Cuanto espera AcquireNextFrame. 0 = no espera.
    ///
    /// Con UNA pantalla conviene esperar: el driver despierta al llegar una
    /// presentacion y bloquear ahi no cuesta CPU. Con VARIAS es al reves --
    /// se sondean en el mismo hilo, asi que una pantalla quieta se lleva 100 ms
    /// de cada vuelta y deja a las demas a 5 FPS aunque sean las unicas que se
    /// mueven. El freno del ritmo ya evita el bucle ocupado.
    /// </summary>
    public int EsperaMs { get; set; } = AcquireTimeoutMs;

    private readonly int _adapterIndex;
    private readonly int _outputIndex;

    private ID3D11Device _device = null!;
    private IDXGIAdapter1 _adapter = null!;
    private IDXGIOutput1 _output = null!;
    private IDXGIOutputDuplication _duplication = null!;

    private readonly CursorTracker _cursor = new();

    public CursorState? TomarCursor() => _cursor.Tomar();

    private bool _frameOutstanding;
    private ulong _frameId;

    /// <summary>
    /// El adaptador importa: Desktop Duplication exige que el dispositivo D3D11
    /// este en la MISMA GPU que gobierna la salida. En un portatil hibrido (iGPU
    /// + discreta) tomar el adaptador 0 a ciegas puede no encontrar la pantalla,
    /// asi que se puede elegir.
    /// </summary>
    public DxgiDesktopCapture(int adapterIndex = 0, int outputIndex = 0)
    {
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
        Open();
    }

    /// <summary>Las GPU y las pantallas que gobierna cada una, para diagnostico.</summary>
    public static IEnumerable<string> Enumerate()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success && adapter is not null; a++)
        {
            using (adapter)
            {
                yield return $"adapter {a}: {adapter.Description.Description.Trim()}";

                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success && output is not null; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var ancho = d.DesktopCoordinates.Right - d.DesktopCoordinates.Left;
                        var alto = d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top;
                        var estado = d.AttachedToDesktop ? "conectada" : "SIN escritorio";

                        yield return $"  output {o}: {d.DeviceName}  {ancho}x{alto}  {estado}";
                    }
                }
            }
        }
    }

    /// <summary>
    /// El dispositivo sobre el que llegan las texturas. El encoder tiene que usar
    /// ESTE y no crear el suyo: dos dispositivos distintos obligarian a copiar
    /// cada frame entre ellos, que es justo lo que se evita trabajando en GPU.
    /// </summary>
    public ID3D11Device Device => _device;

    /// <summary>ID de fabricante de la GPU (PCI). Lo usa el encoder para elegir
    /// un MFT del mismo fabricante y no cruzar frames entre tarjetas.</summary>
    public uint AdapterVendorId { get; private set; }

    /// <summary>Identificador exacto del adaptador DXGI. Es lo que usa el encoder
    /// para pedir a Windows los MFT de ESTA GPU y no los de la de al lado.</summary>
    public Vortice.Luid AdapterLuid { get; private set; }

    public string Adapter { get; private set; } = string.Empty;
    public string Output { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// Esquina de esta pantalla en el escritorio VIRTUAL, que no empieza en 0,0
    /// cuando hay varios monitores -- el de la izquierda tiene Left negativo.
    ///
    /// La entrada remota lo necesita: SendInput absoluto se expresa sobre el
    /// escritorio virtual entero, y sin esta traslacion el raton aparece en el
    /// monitor equivocado en cuanto hay mas de uno.
    /// </summary>
    public int DesktopLeft { get; private set; }
    public int DesktopTop { get; private set; }

    public long Timeouts { get; private set; }
    public long AccessLostRecoveries { get; private set; }
    public long ResolutionChanges { get; private set; }
    public long Dropped { get; private set; }

    /// <summary>
    /// Antiguedad del ultimo frame entregado: microsegundos entre el momento en
    /// que el escritorio lo presento y el momento en que lo recogimos.
    ///
    /// Es la latencia que importa. Medir cuanto tarda la llamada no dice nada,
    /// porque la mayor parte del tiempo esta bloqueada esperando a que haya algo
    /// que capturar.
    /// </summary>
    public long LastFrameAgeUs { get; private set; }

    public Task<VideoFrame?> CaptureAsync(CancellationToken cancellationToken)
        => Task.FromResult(Capture(cancellationToken));

    private VideoFrame? Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_frameOutstanding)
            throw new InvalidOperationException(
                "El frame anterior sigue vivo. Desktop Duplication solo cede una superficie a la vez: " +
                "hay que llamar a VideoFrame.Dispose() antes de pedir el siguiente.");

        var duplication = _duplication;
        var result = duplication.AcquireNextFrame((uint)EsperaMs, out var info, out var resource);

        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            // La pantalla no cambio. No hay frame y NO hay que liberar nada.
            Timeouts++;
            return null;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            // Pasa al cambiar de resolucion, al aparecer UAC y al cambiar de
            // usuario. Se recrea el duplicador y se sigue.
            AccessLostRecoveries++;
            Reopen();
            return null;
        }

        if (result.Failure)
            throw Fail(result, "AcquireNextFrame fallo");

        // El PUNTERO viaja en la misma llamada, y hasta la Fase 11 se tiraba.
        // Desktop Duplication NO compone el cursor en la imagen: lo entrega
        // aparte, asi que sin leer esto el escritorio remoto llega literalmente
        // sin raton.
        _cursor.Anotar(info, duplication, Width, Height, 0, 0);

        // A partir de aqui el frame esta adquirido: cualquier salida tiene que
        // liberarlo o quedaria retenido y el siguiente Acquire fallaria.
        try
        {
            var texture = resource.QueryInterface<ID3D11Texture2D>();
            resource.Dispose();

            var description = texture.Description;

            if (description.Width != Width || description.Height != Height)
            {
                // Normalmente el cambio de resolucion llega como ACCESS_LOST,
                // pero no siempre: si la textura viene con otro tamano, manda
                // ella y hay que avisar al encoder.
                ResolutionChanges++;
                Width = (int)description.Width;
                Height = (int)description.Height;
            }

            // LastPresentTime a 0 significa que la imagen NO se actualizo y solo
            // se movio el puntero: la textura trae el contenido anterior.
            var desktopChanged = info.LastPresentTime != 0;

            if (desktopChanged)
            {
                LastFrameAgeUs = Reloj.DesdeQpc(info.LastPresentTime);

                // AccumulatedFrames > 1 = DXGI junto varias presentaciones
                // porque no ibamos a su ritmo. Las que no vimos, se perdieron.
                if (info.AccumulatedFrames > 1)
                    Dropped += info.AccumulatedFrames - 1;
            }

            _frameOutstanding = true;

            return new VideoFrame(
                texture, Width, Height, ++_frameId, Reloj.Ahora(), desktopChanged,
                release: () =>
                {
                    _frameOutstanding = false;

                    // Contra ESTE duplicador, no contra el que haya en el campo:
                    // si entretanto se recreo, liberar en el nuevo seria mentira.
                    duplication.ReleaseFrame();
                });
        }
        catch
        {
            duplication.ReleaseFrame();
            throw;
        }
    }

    /// <summary>
    /// Recrea el duplicador conservando el dispositivo D3D11: el adaptador es el
    /// mismo, asi que el dispositivo sigue valido y volver a crearlo costaria
    /// cientos de milisegundos por cada UAC que aparezca.
    ///
    /// Se releen adaptador y salida porque el ACCESS_LOST puede venir justamente
    /// de un cambio de resolucion.
    /// </summary>
    private void Reopen()
    {
        _duplication.Dispose();
        _output.Dispose();
        _adapter.Dispose();

        _duplication = null!;
        _output = null!;
        _adapter = null!;

        OpenOutput();
        Duplicate();
    }

    private void Open()
    {
        OpenOutput();

        // El dispositivo se crea SOBRE EL ADAPTADOR QUE GOBIERNA LA PANTALLA, no
        // sobre el que Windows considere principal. Desktop Duplication exige que
        // coincidan: en un portatil hibrido, crear el dispositivo por defecto y
        // duplicar una salida de la otra GPU falla sin decir por que.
        //
        // Con un adaptador explicito, DriverType tiene que ser Unknown.
        var result = D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device!);

        if (result.Failure || _device is null)
            throw Fail(result, $"No se pudo crear el dispositivo D3D11 sobre '{Adapter}'");

        Duplicate();
    }

    /// <summary>Localiza adaptador y salida, sin crear el dispositivo.</summary>
    private void OpenOutput()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        if (factory.EnumAdapters1((uint)_adapterIndex, out var adapter).Failure || adapter is null)
            throw new ScreenCaptureUnavailableException(
                $"No hay adaptador grafico {_adapterIndex}. Una maquina virtual sin adaptador WDDM " +
                "no soporta Desktop Duplication, y no hay forma de sortearlo.");

        _adapter = adapter;
        Adapter = adapter.Description.Description.Trim();
        AdapterVendorId = adapter.Description.VendorId;
        AdapterLuid = adapter.Description1.Luid;

        if (adapter.EnumOutputs((uint)_outputIndex, out var output).Failure || output is null)
            throw new ScreenCaptureUnavailableException(
                $"El adaptador '{Adapter}' no tiene la salida {_outputIndex}. " +
                "Puede que el monitor este desconectado, o que la pantalla la gobierne otra GPU " +
                "(en un equipo hibrido, prueba otro adaptador).");

        using (output)
        {
            var description = output.Description;

            if (!description.AttachedToDesktop)
                throw new ScreenCaptureUnavailableException(
                    $"La salida {description.DeviceName} no esta conectada al escritorio.");

            Output = description.DeviceName;
            Width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
            Height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;
            DesktopLeft = description.DesktopCoordinates.Left;
            DesktopTop = description.DesktopCoordinates.Top;

            _output = output.QueryInterface<IDXGIOutput1>();
        }
    }

    private void Duplicate()
    {
        try
        {
            _duplication = _output.DuplicateOutput(_device);
        }
        catch (SharpGenException ex)
        {
            throw Fail(ex.ResultCode, "DuplicateOutput fallo", ex);
        }
    }

    /// <summary>
    /// Traduce los fallos que tienen una causa concreta y accionable. El resto
    /// se deja como estan: inventarles un mensaje solo despistaria.
    /// </summary>
    private static Exception Fail(Result result, string what, Exception? inner = null)
    {
        // E_ACCESSDENIED. Es el caso de la sesion 0 y del escritorio seguro:
        // hay escritorio, pero este proceso no puede verlo.
        if (result.Code == unchecked((int)0x80070005))
            return new ScreenCaptureUnavailableException(
                $"{what}: acceso denegado al escritorio. El proceso no corre en una sesion " +
                "interactiva, o hay un escritorio seguro delante (UAC, pantalla de bloqueo).", inner);

        if (result == Vortice.DXGI.ResultCode.SessionDisconnected)
            return new ScreenCaptureUnavailableException(
                $"{what}: la sesion de Windows esta desconectada.", inner);

        if (result == Vortice.DXGI.ResultCode.NotCurrentlyAvailable)
            return new ScreenCaptureUnavailableException(
                $"{what}: ya hay el maximo de aplicaciones duplicando este escritorio.", inner);

        if (result == Vortice.DXGI.ResultCode.Unsupported)
            return new ScreenCaptureUnavailableException(
                $"{what}: el adaptador no soporta Desktop Duplication.", inner);

        return inner ?? new InvalidOperationException($"{what}: {result.Description} (0x{result.Code:X8})");
    }



    public void Dispose()
    {
        _duplication?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
        _device?.Dispose();
    }
}
