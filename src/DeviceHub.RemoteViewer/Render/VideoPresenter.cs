using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Dueno del dispositivo D3D11, del swapchain y de la conversion NV12 -> RGB.
///
/// La conversion la hace ID3D11VideoProcessor, no un shader propio. Es la misma
/// pieza que el host usa en sentido contrario para BGRA -> NV12, viene con el
/// driver y ya sabe de rangos de color: escribir un shader HLSL para esto seria
/// anadir compilacion en tiempo de ejecucion y una tabla de coeficientes que
/// alguien tendria que mantener.
///
/// Todo lo de esta clase ocurre en el hilo de reproduccion. El unico contacto
/// con el hilo de la interfaz es el HWND que recibe al construirse.
/// </summary>
public sealed class VideoPresenter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private ID3D11VideoProcessorEnumerator _enumerator;
    private ID3D11VideoProcessor _processor;

    /// <summary>
    /// Crea el dispositivo que comparten decodificador y presentador.
    ///
    /// VideoSupport es obligatorio: sin esa bandera, QueryInterface a
    /// ID3D11VideoDevice devuelve null y el fallo aparece mucho mas tarde y en
    /// otro sitio.
    /// </summary>
    public static ID3D11Device CreateDevice()
    {
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device).CheckError();

        // El decodificador usa el dispositivo por dentro desde sus propios hilos
        // mientras el presentador dibuja desde el de reproduccion.
        using (var multihilo = device!.QueryInterfaceOrNull<ID3D11Multithread>())
            multihilo?.SetMultithreadProtected(true);

        return device;
    }

    /// <summary>
    /// El tamano llega del primer frame descodificado, no del que creiamos: lo
    /// dicta el SPS del flujo. `codificado` es la textura completa y `visible` el
    /// trozo que hay que mostrar, que no coinciden cuando el alto no es multiplo
    /// de 16.
    /// </summary>
    public VideoPresenter(
        ID3D11Device device, IntPtr hwnd, int anchoCodificado, int altoCodificado,
        int visibleX, int visibleY, int width, int height)
    {
        Width = width;
        Height = height;
        _device = device;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        // El swapchain se crea del tamano del VIDEO y nunca se redimensiona:
        // Scaling.Stretch deja que DXGI escale al tamano de la ventana. Asi no
        // hay que rehacer nada al cambiar el tamano, que es la parte que suele
        // dejar el visor en negro.
        //
        // ponytail: estira sin respetar la relacion de aspecto. Si molesta, se
        // arregla con VideoProcessorSetStreamDestRect, no redimensionando.
        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
            SampleDescription = new SampleDescription(1, 0)
        });

        // Que Alt+Enter no se lleve la ventana a pantalla completa por su cuenta.
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);

        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)anchoCodificado,
            InputHeight = (uint)altoCodificado,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal
        });

        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        Recortar(visibleX, visibleY, width, height, anchoCodificado, altoCodificado);

    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// Cambia de resolucion SIN tirar el swapchain.
    ///
    /// Es la diferencia entre que se vea y que no. Un swapchain NUEVO sobre el
    /// mismo HWND no lo compone el DWM hasta que la ventana recibe un WM_SIZE, y
    /// al cambiar de monitor el tamano de la ventana no tiene por que cambiar:
    /// resultado, imagen congelada hasta que alguien la redimensiona a mano.
    ///
    /// ResizeBuffers conserva el swapchain y con el su asociacion con la
    /// ventana, asi que lo unico que se rehace es el procesador de video -- que
    /// SI depende del tamano de entrada.
    /// </summary>
    public void Reconfigurar(
        int anchoCodificado, int altoCodificado, int visibleX, int visibleY, int width, int height)
    {
        _processor.Dispose();
        _enumerator.Dispose();

        Width = width;
        Height = height;

        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)anchoCodificado,
            InputHeight = (uint)altoCodificado,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal
        });

        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        Recortar(visibleX, visibleY, width, height, anchoCodificado, altoCodificado);
    }

    /// <summary>
    /// Recorte al trozo visible. H.264 codifica en macrobloques de 16, asi que
    /// 1080 de alto viaja como 1088 y las 8 filas de mas son relleno: sin este
    /// recorte se pintan, y se ven.
    /// </summary>
    private void Recortar(int visibleX, int visibleY, int width, int height, int ancho, int alto)
    {
        if (visibleX != 0 || visibleY != 0 || width != ancho || height != alto)
        {
            _videoContext.VideoProcessorSetStreamSourceRect(
                _processor, 0, true, new RawRect(visibleX, visibleY, visibleX + width, visibleY + height));
        }

        // Justo al reves que en el host: entra NV12 de rango limitado y sale RGB
        // de rango completo. Sin declararlo, los negros salen grises.
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { Nominal_Range = 1 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { RGB_Range = 0 });
    }

    /// <summary>
    /// Pinta un frame NV12 y lo presenta. `subresource` es la rebanada del array
    /// de texturas del decodificador; sin el se pinta siempre la misma imagen.
    ///
    /// `guardarEn` pide una captura de ESTE frame. Se atiende aqui y no desde
    /// fuera porque el unico momento en que la imagen existe ya convertida a RGB
    /// es entre el Blt y el Present: con SwapEffect.FlipDiscard el contenido del
    /// buffer trasero queda indefinido en cuanto se presenta.
    /// </summary>
    public void Present(ID3D11Texture2D nv12, uint subresource, string? guardarEn = null)
    {
        // En el modelo flip hay que volver a pedir el buffer despues de cada
        // Present: los buffers rotan y guardarse el primero pinta sobre uno que
        // ya no se ve.
        using var trasera = _swapChain.GetBuffer<ID3D11Texture2D>(0);

        using var salida = _videoDevice.CreateVideoProcessorOutputView(
            trasera, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });

        using var entrada = _videoDevice.CreateVideoProcessorInputView(
            nv12, _enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { ArraySlice = subresource }
            });

        _videoContext.VideoProcessorBlt(_processor, salida, 0, [new VideoProcessorStream
        {
            Enable = true,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = entrada
        }]);

        if (guardarEn is not null)
            Guardar(trasera, guardarEn);

        // Intervalo 0: el ritmo lo marca el reproductor, no el monitor. Esperar
        // al vsync aqui sumaria hasta 16 ms de retraso a cada frame.
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>Textura de lectura para las capturas. Se crea la primera vez y se
    /// reutiliza: una captura es un gesto manual, pero reservar video por cada
    /// pulsacion no tiene ninguna ventaja.</summary>
    private ID3D11Texture2D? _lectura;

    private void Guardar(ID3D11Texture2D trasera, string ruta)
    {
        _lectura ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });

        var contexto = _device.ImmediateContext;

        // GPU -> RAM. Es la unica copia a memoria de todo el visor, y ocurre solo
        // cuando alguien pulsa el boton.
        contexto.CopyResource(_lectura, trasera);

        var mapa = contexto.Map(_lectura, 0, MapMode.Read);

        try
        {
            // El paso (RowPitch) casi nunca es ancho*4: la GPU alinea las filas.
            // Pasarlo como stride es lo que evita la imagen inclinada.
            var imagen = BitmapSource.Create(
                Width, Height, 96, 96, PixelFormats.Bgra32, null,
                mapa.DataPointer, (int)(mapa.RowPitch * (uint)Height), (int)mapa.RowPitch);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(imagen));

            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);

            using var archivo = File.Create(ruta);
            png.Save(archivo);
        }
        finally
        {
            contexto.Unmap(_lectura, 0);
        }
    }

    public void Dispose()
    {
        _lectura?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _swapChain.Dispose();

        // El dispositivo NO se dispone aqui: lo comparte con el decodificador y
        // lo cierra quien lo creo.
    }
}
