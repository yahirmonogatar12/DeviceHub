using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// BGRA -> NV12 en la GPU, con ID3D11VideoProcessor.
///
/// Es obligatorio y no un adorno: el codificador POR SOFTWARE solo acepta NV12,
/// y ese es justamente el que usara una PC de planta sin encoder de hardware.
/// Los MFT de AMD y NVIDIA tragan ARGB32 y tentaba saltarse este paso, pero esa
/// ruta pasaria en el portatil de desarrollo y fallaria donde importa.
///
/// El video processor convierte en la GPU: la textura nunca baja a RAM.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorOutputView _outputView;

    public Nv12Converter(ID3D11Device device, int width, int height)
    {
        Width = width;
        Height = height;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contenido = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contenido);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // La textura NV12 se crea UNA vez y se reutiliza. Crear una por frame a
        // 60 fps son 60 asignaciones de video por segundo: el coste se come la
        // ventaja de trabajar en GPU.
        Output = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });

        _outputView = _videoDevice.CreateVideoProcessorOutputView(
            Output, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });

        // El escritorio es RGB full range; NV12 para H.264 es limitado. Sin
        // declararlo, los negros salen grises y los blancos lavados.
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { Nominal_Range = 1 });
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>La textura NV12 reutilizada. Valida hasta la siguiente conversion.</summary>
    public ID3D11Texture2D Output { get; }

    public void Convert(ID3D11Texture2D bgra)
    {
        using var inputView = _videoDevice.CreateVideoProcessorInputView(
            bgra, _enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D
            });

        var stream = new VideoProcessorStream
        {
            Enable = true,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView
        };

        _videoContext.VideoProcessorBlt(_processor, _outputView, 0, [stream]);
    }

    public void Dispose()
    {
        _outputView.Dispose();
        Output.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
