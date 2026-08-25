using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Todos los monitores a la vez, compuestos en UNA imagen del tamano del
/// escritorio virtual. Fase 22.
///
/// Es lo que hace posible arrastrar una ventana de una pantalla a otra desde el
/// visor. La entrada no necesita ni una linea: InputInjector trabaja en
/// coordenadas del escritorio virtual desde la Fase 9, y aqui lo capturado ES el
/// escritorio virtual, asi que la traslacion sale exacta.
///
/// LA DIFERENCIA CON DxgiDesktopCapture, que importa: alli la textura entregada
/// es la del duplicador y no se copia nada. Aqui hay una copia GPU->GPU por
/// monitor y por frame, porque no existe un duplicador del escritorio virtual --
/// Windows solo duplica salidas sueltas. Sigue sin tocar la RAM.
///
/// No es thread-safe, igual que su hermana: un solo hilo de captura.
/// </summary>
public sealed class VirtualDesktopCapture : IScreenCapture
{
    /// <summary>
    /// Cuanto se duerme cuando NINGUN monitor cambio.
    ///
    /// Aqui no se puede bloquear en AcquireNextFrame como con una sola pantalla:
    /// esperar 100 ms en el monitor 1 dejaria el monitor 2 a 10 FPS aunque fuera
    /// el unico que se mueve. Se sondean todos sin esperar y se duerme al final.
    ///
    /// ponytail: 8 ms pone el techo en ~125 FPS, muy por encima de lo que se
    /// codifica. Si algun dia estorba, la salida es un hilo por monitor, no bajar
    /// este numero.
    /// </summary>
    private const int SinCambiosMs = 8;

    private readonly int _adapterIndex;

    private ID3D11Device _device = null!;
    private IDXGIAdapter1 _adapter = null!;
    private ID3D11Texture2D _lienzo = null!;

    /// <summary>Un duplicador por monitor, con su esquina dentro del lienzo.</summary>
    private (IDXGIOutput1 Salida, IDXGIOutputDuplication Duplicador, int X, int Y)[] _monitores = [];

    private readonly CursorTracker _cursor = new();

    public CursorState? TomarCursor() => _cursor.Tomar();

    private bool _frameVivo;
    private ulong _frameId;
    private bool _avisadoDelDesajuste;

    /// <summary>Ultimo desajuste entre lo que entrega un monitor y el lienzo. Se
    /// cuenta una vez, no en cada frame.</summary>
    public string? Desajuste { get; private set; }

    /// <summary>
    /// Solo se componen las salidas de UN adaptador. Desktop Duplication exige
    /// que el dispositivo D3D11 este en la misma GPU que gobierna la salida, y en
    /// un equipo hibrido las pantallas pueden estar repartidas: componer dos GPU
    /// obligaria a copiar por RAM entre ellas, que es lo que este proyecto evita
    /// desde la Fase 1.
    /// </summary>
    public VirtualDesktopCapture(int adapterIndex = 0)
    {
        _adapterIndex = adapterIndex;
        Abrir();
    }

    public string Adapter { get; private set; } = string.Empty;
    public string Output { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Device Device => _device;
    public Vortice.Luid AdapterLuid { get; private set; }
    public uint AdapterVendorId { get; private set; }
    public int DesktopLeft { get; private set; }
    public int DesktopTop { get; private set; }

    public long Timeouts { get; private set; }

    /// <summary>
    /// Se reparte a las duplicaciones de dentro. Aqui no se puede bloquear en
    /// una sola pantalla -- esperar en el monitor 1 dejaria el 2 a 10 FPS
    /// aunque fuera el unico moviendose -- asi que se sondean todas sin espera
    /// y el descanso va al final.
    /// </summary>
    public int EsperaMs { get; set; }
    public long AccessLostRecoveries { get; private set; }
    public long ResolutionChanges { get; private set; }
    public long Dropped { get; private set; }

    public Task<VideoFrame?> CaptureAsync(CancellationToken cancellationToken)
        => Task.FromResult(Capturar(cancellationToken));

    private VideoFrame? Capturar(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_frameVivo)
            throw new InvalidOperationException(
                "El frame anterior sigue vivo. Hay que llamar a VideoFrame.Dispose() antes de pedir otro.");

        var cambio = false;

        foreach (var monitor in _monitores)
        {
            // Sin espera: el que no tenga novedades no puede retrasar al que si.
            var resultado = monitor.Duplicador.AcquireNextFrame(0, out var info, out var recurso);

            if (resultado == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                Timeouts++;
                continue;
            }

            if (resultado == Vortice.DXGI.ResultCode.AccessLost)
            {
                AccessLostRecoveries++;
                Reabrir();
                return null;
            }

            if (resultado.Failure)
                throw new ScreenCaptureUnavailableException(
                    $"AcquireNextFrame fallo en {monitor.Salida.Description.DeviceName}: {resultado}");

            try
            {
                // El puntero se anota aunque este monitor no haya cambiado de
                // imagen: la esquina lo coloca dentro del lienzo compuesto.
                _cursor.Anotar(info, monitor.Duplicador, Width, Height, monitor.X, monitor.Y);

                if (info.LastPresentTime == 0)
                    continue;   // solo se movio el puntero; la imagen es la de antes

                if (info.AccumulatedFrames > 1)
                    Dropped += info.AccumulatedFrames - 1;

                using var textura = recurso.QueryInterface<ID3D11Texture2D>();
                var medida = textura.Description;

                // QUE QUEPA, antes de copiar.
                //
                // Aqui estaba el E_INVALIDARG que tumbaba la sesion: el lienzo se
                // dimensiona al abrir, con los tamanos que los monitores tenian
                // ENTONCES. Si uno cambia de resolucion despues -- o si la
                // duplicacion entrega una textura de otro tamano durante una
                // transicion -- la copia se sale del lienzo y D3D lo rechaza por
                // parametro invalido.
                //
                // La excepcion mataba el hilo de captura, el bucle de fuera lo
                // rehacia, y vuelta a empezar: 47 versiones de configuracion en
                // un minuto, cada una tirando el decodificador del visor.
                //
                // Saltarse ESE monitor deja el resto de la imagen viva. Rehacer
                // la captura entera por un monitor que no cuadra es cambiar un
                // problema pequeno por uno grande.
                if (monitor.X + medida.Width > Width || monitor.Y + medida.Height > Height)
                {
                    if (!_avisadoDelDesajuste)
                    {
                        _avisadoDelDesajuste = true;
                        Desajuste =
                            $"{monitor.Salida.Description.DeviceName} entrega {medida.Width}x{medida.Height} " +
                            $"en @{monitor.X},{monitor.Y} y el lienzo mide {Width}x{Height}: " +
                            "se rehace la composicion";
                    }

                    // El lienzo ya no vale: se rehace con los tamanos de ahora.
                    Reabrir();
                    return null;
                }

                // GPU -> GPU, a su esquina del lienzo. La region de origen es
                // null: se copia el monitor entero.
                _device.ImmediateContext.CopySubresourceRegion(
                    _lienzo, 0, (uint)monitor.X, (uint)monitor.Y, 0, textura, 0);

                cambio = true;
            }
            finally
            {
                recurso.Dispose();

                // Se suelta EN EL ACTO, no al final del frame: ya esta copiado, y
                // retenerlo bloquearia las presentaciones de ese monitor.
                monitor.Duplicador.ReleaseFrame();
            }
        }

        if (!cambio)
        {
            Thread.Sleep(SinCambiosMs);
            return null;
        }

        _frameVivo = true;

        // El lienzo es NUESTRO y se reutiliza, asi que soltarlo no libera nada en
        // DXGI: los duplicadores ya se soltaron arriba.
        return new VideoFrame(
            _lienzo, Width, Height, ++_frameId, Reloj.Ahora(), desktopChanged: true,
            release: () => _frameVivo = false);
    }

    private void Abrir()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        if (factory.EnumAdapters1((uint)_adapterIndex, out var adaptador).Failure || adaptador is null)
            throw new ScreenCaptureUnavailableException(
                $"No hay adaptador grafico {_adapterIndex}.");

        _adapter = adaptador;
        Adapter = adaptador.Description.Description.Trim();
        AdapterVendorId = adaptador.Description.VendorId;
        AdapterLuid = adaptador.Description1.Luid;

        var salidas = new List<(IDXGIOutput1 Salida, int X, int Y, int Ancho, int Alto)>();

        for (uint o = 0; adaptador.EnumOutputs(o, out var salida).Success && salida is not null; o++)
        {
            using (salida)
            {
                var d = salida.Description;

                if (!d.AttachedToDesktop)
                    continue;

                var caja = d.DesktopCoordinates;

                salidas.Add((
                    salida.QueryInterface<IDXGIOutput1>(),
                    caja.Left, caja.Top,
                    caja.Right - caja.Left, caja.Bottom - caja.Top));
            }
        }

        if (salidas.Count == 0)
            throw new ScreenCaptureUnavailableException(
                $"El adaptador '{Adapter}' no gobierna ninguna pantalla conectada al escritorio.");

        (DesktopLeft, DesktopTop, Width, Height) = Pantallas.Envolvente(
            [.. salidas.Select(s => (s.X, s.Y, s.Ancho, s.Alto))]);

        Output = string.Join(" + ", salidas.Select(s => s.Salida.Description.DeviceName));

        var resultado = D3D11.D3D11CreateDevice(
            _adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out _device!);

        if (resultado.Failure || _device is null)
            throw new ScreenCaptureUnavailableException(
                $"No se pudo crear el dispositivo D3D11 sobre '{Adapter}': {resultado}");

        // Las mismas banderas que trae una textura de Desktop Duplication: el
        // convertidor a NV12 crea una vista de entrada de video sobre ella.
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

        _monitores = [.. salidas.Select(s =>
        {
            IDXGIOutputDuplication duplicador;

            try
            {
                duplicador = s.Salida.DuplicateOutput(_device);
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                throw new ScreenCaptureUnavailableException(
                    $"No se pudo duplicar {s.Salida.Description.DeviceName}: {ex.ResultCode}. " +
                    "Otra aplicacion puede estar duplicando ya esa salida, o la sesion no es interactiva.",
                    ex);
            }

            // Coordenadas RELATIVAS al lienzo: el monitor de la izquierda tiene X
            // negativa en el escritorio virtual y aqui tiene que caer en 0.
            return (s.Salida, duplicador, s.X - DesktopLeft, s.Y - DesktopTop);
        })];
    }

    /// <summary>
    /// Rehace todo. Al contrario que con una sola pantalla, aqui no se conserva
    /// nada: un ACCESS_LOST en modo compuesto suele venir de un cambio de
    /// resolucion o de un monitor que se enchufa, y las dos cosas cambian el
    /// tamano del lienzo.
    /// </summary>
    private void Reabrir()
    {
        ResolutionChanges++;
        _avisadoDelDesajuste = false;
        Cerrar();
        Abrir();
    }

    private void Cerrar()
    {
        foreach (var monitor in _monitores)
        {
            monitor.Duplicador.Dispose();
            monitor.Salida.Dispose();
        }

        _monitores = [];

        _lienzo?.Dispose();
        _device?.Dispose();
        _adapter?.Dispose();

        _lienzo = null!;
        _device = null!;
        _adapter = null!;
    }

    public void Dispose() => Cerrar();
}
