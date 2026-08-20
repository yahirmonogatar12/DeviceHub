using Vortice;
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
    private readonly int _entradaAncho;
    private readonly int _entradaAlto;

    /// <summary>
    /// `width`/`height` es lo que ENTRA; `salidaAncho`/`salidaAlto` lo que sale.
    ///
    /// Cuando difieren, el procesador de video ESCALA de paso: es un blit, y
    /// escalar no le cuesta mas que copiar. Sirve para el modo compuesto, donde
    /// dos monitores dan una imagen que el codificador de esta GPU no traga.
    /// </summary>
    public Nv12Converter(ID3D11Device device, int width, int height, int salidaAncho = 0, int salidaAlto = 0)
    {
        Width = salidaAncho > 0 ? salidaAncho : width;
        Height = salidaAlto > 0 ? salidaAlto : height;

        // Acotar el blt solo vale cuando entra y sale el MISMO tamano. Si el
        // procesador esta escalando -- el modo compuesto lo hace -- la caja de
        // la entrada no cae donde toca en la salida, y actualizar la esquina
        // equivocada es peor que convertirlo todo.
        _entradaAncho = width;
        _entradaAlto = height;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contenido = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.PlaybackNormal
        };

        try
        {
            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contenido);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            // El procesador de video tiene su propio tope de tamano, distinto del
            // del codificador y del de la duplicacion. Con una sola pantalla no se
            // roza nunca; componiendo dos, si.
            throw new VideoEncoderUnavailableException(
                $"El procesador de video no admite {width}x{height} para BGRA -> NV12: {ex.ResultCode}", ex);
        }
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // La textura NV12 se crea UNA vez y se reutiliza. Crear una por frame a
        // 60 fps son 60 asignaciones de video por segundo: el coste se come la
        // ventaja de trabajar en GPU.
        Output = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
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

    /// <summary>Cuantos pixeles se han convertido y cuantos habria costado
    /// convertirlo todo. La diferencia es lo que ahorran los rectangulos
    /// sucios, y sin medirla no hay forma de saber si sirven.</summary>
    public long PixelesConvertidos { get; private set; }

    public long PixelesTotales { get; private set; }

    /// <param name="zona">
    /// Solo esta caja. Null = la pantalla entera.
    ///
    /// LO QUE QUEDA FUERA NO SE TOCA, y es correcto: la textura NV12 se
    /// reutiliza entre frames, asi que ahi sigue exactamente el contenido
    /// anterior -- que para un pixel que no cambio ES el contenido actual.
    ///
    /// Solo vale si quien pasa la zona GARANTIZA que cubre todo lo que cambio.
    /// DXGI lo garantiza; adivinarlo no.
    /// </param>
    public void Convert(ID3D11Texture2D bgra, RawRect? zona = null)
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

        if (zona is { } caja && Width == _entradaAncho && Height == _entradaAlto)
        {
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, caja);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, caja);

            // Y QUE NO TOQUE NADA MAS. VideoProcessorBlt escribe la superficie
            // de salida ENTERA y rellena con el color de fondo lo que queda
            // fuera del stream: sin acotar el objetivo, actualizar una esquina
            // borraria el resto de la pantalla. Es la misma leccion que costo
            // la segunda pantalla en el visor.
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, caja);

            PixelesConvertidos += (long)(caja.Right - caja.Left) * (caja.Bottom - caja.Top);
        }
        else
        {
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, false, default);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, false, default);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, false, default);

            PixelesConvertidos += (long)Width * Height;
        }

        PixelesTotales += (long)Width * Height;

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
