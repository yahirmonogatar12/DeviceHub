using System.Collections.Generic;
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
/// driver y ya sabe de rangos de color.
///
/// PINTA VARIAS PANTALLAS EN UN LIENZO. Desde que el host manda un flujo por
/// monitor, aqui llegan N imagenes independientes que hay que colocar. Cada una
/// se pinta en su rectangulo de una textura propia que PERSISTE, y esa textura
/// se copia al buffer trasero antes de presentar.
///
/// El lienzo intermedio no es un lujo: con SwapEffect.FlipDiscard el contenido
/// del buffer trasero queda indefinido despues de cada Present, asi que pintar
/// el monitor 1 y presentar borraria lo que el monitor 2 pinto en la vuelta
/// anterior. Con una sola pantalla cuesta una copia de mas y ahorra una rama.
///
/// Todo ocurre en el hilo de reproduccion. El unico contacto con el hilo de la
/// interfaz es el HWND que recibe al construirse.
/// </summary>
public sealed class VideoPresenter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    /// <summary>Donde se acumula lo que pinta cada pantalla.</summary>
    private ID3D11Texture2D _lienzo;

    /// <summary>Un procesador de video POR PANTALLA: cada uno se crea con el
    /// tamano de SU flujo, y ese tamano forma parte de su configuracion.</summary>
    private readonly Dictionary<uint, Pantalla> _pantallas = [];

    private sealed record Pantalla(
        ID3D11VideoProcessorEnumerator Enumerador, ID3D11VideoProcessor Procesador,
        int Ancho, int Alto, RawRect Destino) : IDisposable
    {
        public void Dispose()
        {
            Procesador.Dispose();
            Enumerador.Dispose();
        }
    }

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

    public VideoPresenter(ID3D11Device device, IntPtr hwnd, int ancho, int alto)
    {
        Width = ancho;
        Height = alto;
        _device = device;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        // El swapchain se crea del tamano del LIENZO y no se recrea nunca:
        // Scaling.Stretch deja que DXGI escale al tamano de la ventana, y
        // ResizeBuffers se encarga de los cambios de resolucion. Un swapchain
        // NUEVO sobre el mismo HWND no lo compone el DWM hasta que llega un
        // WM_SIZE, y esa fue la imagen congelada al cambiar de monitor.
        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, new SwapChainDescription1
        {
            Width = (uint)ancho,
            Height = (uint)alto,
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
        _lienzo = CrearLienzo(ancho, alto);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    private ID3D11Texture2D CrearLienzo(int ancho, int alto)
        => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)ancho,
            Height = (uint)alto,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });

    /// <summary>Cambia el tamano del lienzo SIN tirar el swapchain.</summary>
    public void Redimensionar(int ancho, int alto)
    {
        if (ancho == Width && alto == Height)
            return;

        foreach (var pantalla in _pantallas.Values)
            pantalla.Dispose();

        _pantallas.Clear();

        Width = ancho;
        Height = alto;

        _lienzo.Dispose();
        _lienzo = CrearLienzo(ancho, alto);

        _swapChain.ResizeBuffers(2, (uint)ancho, (uint)alto, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
    }

    /// <summary>
    /// Declara donde va una pantalla. `origen` es el trozo visible de su textura
    /// codificada -- H.264 codifica en macrobloques de 16, asi que 1080 de alto
    /// viaja como 1088 y sin recortar se pintan 8 filas de relleno -- y `destino`
    /// su hueco dentro del lienzo.
    /// </summary>
    public void Colocar(
        uint display, int anchoCodificado, int altoCodificado,
        int visibleX, int visibleY, int visibleAncho, int visibleAlto,
        int destinoX, int destinoY)
    {
        if (_pantallas.TryGetValue(display, out var previa))
        {
            if (previa.Ancho == anchoCodificado && previa.Alto == altoCodificado
                && previa.Destino.Left == destinoX && previa.Destino.Top == destinoY)
            {
                return;
            }

            previa.Dispose();
            _pantallas.Remove(display);
        }

        var enumerador = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)anchoCodificado,
            InputHeight = (uint)altoCodificado,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.PlaybackNormal
        });

        var procesador = _videoDevice.CreateVideoProcessor(enumerador, 0);

        if (visibleX != 0 || visibleY != 0
            || visibleAncho != anchoCodificado || visibleAlto != altoCodificado)
        {
            _videoContext.VideoProcessorSetStreamSourceRect(
                procesador, 0, true,
                new RawRect(visibleX, visibleY, visibleX + visibleAncho, visibleY + visibleAlto));
        }

        var destino = new RawRect(
            destinoX, destinoY, destinoX + visibleAncho, destinoY + visibleAlto);

        // El hueco de ESTA pantalla dentro del lienzo. Sin esto, la segunda
        // pantalla se pintaria encima de la primera en la esquina.
        _videoContext.VideoProcessorSetStreamDestRect(procesador, 0, true, destino);

        // Justo al reves que en el host: entra NV12 de rango limitado y sale RGB
        // de rango completo. Sin declararlo, los negros salen grises.
        _videoContext.VideoProcessorSetStreamFrameFormat(procesador, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamColorSpace(procesador, 0, new VideoProcessorColorSpace { Nominal_Range = 1 });
        _videoContext.VideoProcessorSetOutputColorSpace(procesador, new VideoProcessorColorSpace { RGB_Range = 0 });

        _pantallas[display] = new Pantalla(
            enumerador, procesador, anchoCodificado, altoCodificado, destino);
    }

    /// <summary>
    /// Pinta un frame NV12 de una pantalla en su hueco del lienzo y presenta.
    ///
    /// `guardarEn` pide una captura. Se atiende entre la copia y el Present:
    /// con FlipDiscard el buffer trasero queda indefinido en cuanto se presenta.
    /// </summary>
    public void Present(uint display, ID3D11Texture2D nv12, uint subresource, string? guardarEn = null)
    {
        if (!_pantallas.TryGetValue(display, out var pantalla))
            return;   // todavia no se sabe donde va

        using (var entrada = _videoDevice.CreateVideoProcessorInputView(
            nv12, pantalla.Enumerador, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { ArraySlice = subresource }
            }))
        using (var salidaLienzo = _videoDevice.CreateVideoProcessorOutputView(
            _lienzo, pantalla.Enumerador, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            }))
        {
            _videoContext.VideoProcessorBlt(pantalla.Procesador, salidaLienzo, 0, [new VideoProcessorStream
            {
                Enable = true,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = entrada
            }]);
        }

        // En el modelo flip hay que volver a pedir el buffer despues de cada
        // Present: los buffers rotan y guardarse el primero pinta sobre uno que
        // ya no se ve.
        using var trasera = _swapChain.GetBuffer<ID3D11Texture2D>(0);

        _device.ImmediateContext.CopyResource(trasera, _lienzo);

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
        foreach (var pantalla in _pantallas.Values)
            pantalla.Dispose();

        _pantallas.Clear();

        _lectura?.Dispose();
        _lienzo.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _swapChain.Dispose();

        // El dispositivo NO se dispone aqui: lo comparte con el decodificador y
        // lo cierra quien lo creo.
    }
}
