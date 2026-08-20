using System.Runtime.InteropServices;
using DeviceHub.Remote.Contracts;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteViewer.Decode;

/// <summary>
/// Un frame descodificado, todavia en la GPU.
///
/// La textura NO es nuestra: pertenece al banco interno del decodificador y solo
/// es valida mientras viva la muestra que la trajo. Por eso el frame sostiene la
/// muestra y el buffer, y disponerlo es lo que devuelve la textura al banco. Un
/// decodificador tiene pocas -- del orden de una docena -- y quedarse con ellas
/// lo para en seco.
/// </summary>
public sealed class DecodedFrame : IDisposable
{
    private readonly IMFSample _muestra;
    private readonly IMFMediaBuffer _buffer;
    private readonly IMFDXGIBuffer _dxgi;

    internal DecodedFrame(
        IMFSample muestra, IMFMediaBuffer buffer, IMFDXGIBuffer dxgi,
        ID3D11Texture2D textura, uint subrecurso, long timestampUs)
    {
        _muestra = muestra;
        _buffer = buffer;
        _dxgi = dxgi;

        Texture = textura;
        Subresource = subrecurso;
        TimestampUs = timestampUs;
    }

    /// <summary>Textura NV12. Un decodificador entrega un ARRAY de texturas y
    /// cada frame es una rebanada: sin el subrecurso se pinta siempre la misma.</summary>
    public ID3D11Texture2D Texture { get; }
    public uint Subresource { get; }
    public long TimestampUs { get; }

    public void Dispose()
    {
        Texture.Dispose();
        _dxgi.Dispose();
        _buffer.Dispose();
        _muestra.Dispose();
    }
}

public sealed class VideoDecoderUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record VideoDecoderCapabilities(string Name, bool Hardware, int Width, int Height);

/// <summary>
/// El rectangulo del frame que de verdad se ve.
///
/// H.264 codifica en macrobloques de 16 pixeles, asi que 1080 de alto se guarda
/// como 1088 y las 8 filas sobrantes contienen relleno. Pintarlas sin recortar
/// deja una banda de basura abajo, que es exactamente lo que salia en la primera
/// reproduccion.
/// </summary>
public readonly record struct VideoAperture(int X, int Y, int Width, int Height);

/// <summary>
/// Decodificador H.264 sobre Media Foundation, con salida en textura D3D11.
///
/// Simetrico al codificador de la Fase 2 salvo en un punto, y el punto importa:
/// ante MF_E_TRANSFORM_STREAM_CHANGE el codificador REIMPONE su tipo de salida
/// -- si no, la resolucion del stream dependeria del driver de cada PC -- y el
/// decodificador ACEPTA el que le proponen. Aqui el flujo manda: la resolucion
/// viene escrita en el SPS y discutirla no tiene sentido.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const uint StreamChange = 0xC00D6D61;   // MF_E_TRANSFORM_STREAM_CHANGE
    private const uint NeedMoreInput = 0xC00D6D72;  // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int MessageNeedInput = 601;
    private const int MessageHaveOutput = 602;
    private const int EventNoWait = 0x00000001;

    // Vortice no expone estas claves.
    private static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid HardwareUrl = new("2fb866ac-b078-4942-ab6c-003d05cda674");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFMediaEventGenerator? _events;

    private readonly bool _asincrono;
    private int _needInput;

    /// <summary>MFVideoFormat_HEVC. Escrito a mano porque Vortice no lo expone:
    /// es el fourcc 'HEVC' dentro del GUID base de Media Foundation.</summary>
    private static readonly Guid Hevc = new("43564548-0000-0010-8000-00aa00389b71");

    /// <summary>Que codec descodifica. Lo dice VideoConfig, y hay que hacerle
    /// caso: alimentar HEVC a un descodificador H.264 no da error al crearlo --
    /// falla despues, en el primer frame, y lejos de aqui.</summary>
    public VideoCodec Codec { get; }

    private Guid Subtipo => Codec == VideoCodec.H265 ? Hevc : VideoFormatGuids.H264;

    public H264Decoder(ID3D11Device device, int width, int height, VideoCodec codec = VideoCodec.H264)
    {
        Codec = codec;
        Width = width;
        Height = height;

        // Sin gestor DXGI el decodificador trabaja en RAM y entrega buffers de
        // sistema: 3 MB por frame subiendo a la GPU otra vez para pintarlos.
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();

        var (transform, nombre, hardware) = Select(Subtipo);
        _transform = transform;

        _asincrono = Flag(_transform, TransformAsync);
        _needInput = _asincrono ? 0 : int.MaxValue;

        if (_asincrono)
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();

        Capabilities = new VideoDecoderCapabilities(nombre, hardware, width, height);

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public VideoDecoderCapabilities Capabilities { get; }

    /// <summary>Resolucion real del flujo. Puede cambiar tras el primer SPS: el
    /// archivo manda, no lo que le pedimos al abrir.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Que trozo de la textura se ve. Se rellena al fijar el tipo de
    /// salida; por defecto, todo.</summary>
    public VideoAperture Aperture { get; private set; }

    public long Submitted { get; private set; }
    public long Produced { get; private set; }
    public long StreamChanges { get; private set; }
    public string? LastIssue { get; private set; }

    /// <summary>
    /// Entrega una unidad de acceso completa. Devuelve cero, uno o varios frames:
    /// un decodificador reordena, y las primeras unidades no producen nada hasta
    /// que tiene contexto.
    /// </summary>
    public IReadOnlyList<DecodedFrame> Decode(byte[] flujo, int offset, int length, long timestampUs)
    {
        var salidas = new List<DecodedFrame>(1);

        DrainEvents(salidas);

        if (_needInput > 0)
        {
            Submit(flujo, offset, length, timestampUs);

            if (_needInput != int.MaxValue)
                _needInput--;
        }

        DrainEvents(salidas);

        if (_events is null)
            DrainOutputs(salidas);

        return salidas;
    }

    /// <summary>Vacia lo que el decodificador tenga retenido. Se llama al final
    /// del archivo: las ultimas imagenes salen solo si se le pide.</summary>
    public IReadOnlyList<DecodedFrame> Drain()
    {
        var salidas = new List<DecodedFrame>();

        _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);

        if (_events is null)
        {
            DrainOutputs(salidas);
            return salidas;
        }

        // El MFT asincrono avisa con METransformDrainComplete (603); hasta
        // entonces sigue mandando HaveOutput.
        var limite = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < limite)
        {
            var antes = salidas.Count;
            DrainEvents(salidas);

            if (salidas.Count == antes)
                break;
        }

        return salidas;
    }

    private void Submit(byte[] flujo, int offset, int length, long timestampUs)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);

        buffer.Lock(out var puntero, out _, out _);

        try
        {
            // Marshal.Copy y no un Span sobre el puntero: el repositorio compila
            // sin AllowUnsafeBlocks y no se hace una excepcion por una copia.
            Marshal.Copy(flujo, offset, puntero, length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = length;

        using var muestra = MediaFactory.MFCreateSample();
        muestra.AddBuffer(buffer);
        muestra.SampleTime = timestampUs * 10;   // microsegundos -> unidades de 100 ns

        _transform.ProcessInput(0, muestra, 0);
        Submitted++;
    }

    private void DrainEvents(List<DecodedFrame> salidas)
    {
        if (_events is null)
            return;

        while (true)
        {
            IMFMediaEvent? evento;

            try
            {
                evento = _events.GetEvent(EventNoWait);
            }
            catch (SharpGenException)
            {
                return;   // MF_E_NO_EVENTS_AVAILABLE
            }

            if (evento is null)
                return;

            using (evento)
            {
                switch ((int)evento.EventType)
                {
                    case MessageNeedInput:
                        _needInput++;
                        break;

                    case MessageHaveOutput:
                        DrainOutputs(salidas);
                        break;
                }
            }
        }
    }

    private void DrainOutputs(List<DecodedFrame> salidas)
    {
        while (true)
        {
            var info = _transform.GetOutputStreamInfo(0);

            // 0x100 = MFT_OUTPUT_STREAM_PROVIDES_SAMPLES. Con gestor DXGI atado,
            // un decodificador lo declara siempre: las texturas salen de su banco
            // interno, no las reservamos nosotros.
            if (((uint)info.Flags & 0x100) == 0)
                throw new VideoDecoderUnavailableException(
                    $"{Capabilities.Name} no entrega sus propias muestras, asi que decodifica en RAM " +
                    "y no en la GPU. La ruta por memoria de sistema no esta implementada a proposito: " +
                    "el visor es de GPU a GPU.");

            var buffers = new OutputDataBuffer[1];
            buffers[0].StreamID = 0;

            var resultado = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out _);

            if ((uint)resultado.Code == StreamChange)
            {
                Renegotiate();
                continue;
            }

            if (resultado.Failure)
            {
                if ((uint)resultado.Code != NeedMoreInput)
                    LastIssue = $"0x{resultado.Code:X8} tras {Produced} frames";

                return;
            }

            var muestra = buffers[0].Sample;

            if (muestra is null)
                return;

            salidas.Add(Wrap(muestra));
            Produced++;
        }
    }

    /// <summary>
    /// Saca la textura de la muestra sin copiarla.
    ///
    /// El buffer de un decodificador con DXVA es un IMFDXGIBuffer: envuelve una
    /// rebanada de un array de texturas. Hay que pedir las dos cosas -- recurso y
    /// subrecurso -- porque todas las rebanadas comparten el mismo ID3D11Texture2D.
    /// </summary>
    private DecodedFrame Wrap(IMFSample muestra)
    {
        var buffer = muestra.GetBufferByIndex(0);
        var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>()
            ?? throw new VideoDecoderUnavailableException(
                "El decodificador devolvio un buffer de memoria de sistema en vez de una superficie DXGI.");

        var puntero = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);

        return new DecodedFrame(
            muestra, buffer, dxgi, new ID3D11Texture2D(puntero), dxgi.SubresourceIndex,
            muestra.SampleTime / 10);
    }

    /// <summary>
    /// Acepta el tipo de salida que propone el decodificador.
    ///
    /// Al contrario que en el codificador, aqui NO se reimpone el nuestro: el
    /// tamano real del video lo dicta el SPS del flujo, y este evento es
    /// justamente el decodificador diciendo que ya lo ha leido. Se exige NV12 --
    /// es lo unico que el presentador sabe pintar -- pero la resolucion se toma
    /// tal cual venga.
    /// </summary>
    private void Renegotiate()
    {
        StreamChanges++;

        for (var i = 0; i < 16; i++)
        {
            IMFMediaType? tipo;

            try { tipo = _transform.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }

            if (tipo is null)
                break;

            using (tipo)
            {
                if (tipo.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;

                _transform.SetOutputType(0, tipo, 0);
                Adopt(tipo);
                return;
            }
        }

        throw new VideoDecoderUnavailableException(
            $"{Capabilities.Name} no ofrece NV12 tras renegociar.");
    }

    /// <summary>
    /// Elige decodificador. SORTANDFILTER pone los de hardware delante, que es lo
    /// que queremos: el software decodifica 1080p a costa de la CPU de la PC del
    /// tecnico, que ademas esta corriendo el dashboard.
    /// </summary>
    private (IMFTransform, string, bool) Select(Guid subtipo)
    {
        var fallos = new List<string>();

        var entrada = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = subtipo
        };

        var salida = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12
        };

        // 0x1 sincronos | 0x2 asincronos | 0x4 hardware | 0x40 ordenar y filtrar.
        using var coleccion = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder, 0x1 | 0x2 | 0x4 | 0x40, entrada, salida);

        foreach (var activate in coleccion)
        {
            var nombre = Attribute(activate, FriendlyName) ?? "(sin nombre)";
            var hardware = Attribute(activate, HardwareUrl) is not null;

            IMFTransform? transform = null;

            try
            {
                transform = activate.ActivateObject<IMFTransform>();

                if (Flag(transform, TransformAsync))
                    transform.Attributes?.Set(TransformAsyncUnlock, 1u);

                // Baja latencia: sin esto el decodificador retiene varias imagenes
                // antes de soltar la primera, y en control remoto eso se siente
                // como retraso constante aunque los FPS salgan bien.
                try { transform.Attributes?.Set(LowLatency, 1u); }
                catch (SharpGenException) { /* no todos lo admiten */ }

                transform.ProcessMessage(
                    TMessageType.MessageSetD3DManager,
                    (UIntPtr)(ulong)(long)_deviceManager.NativePointer);

                Configure(transform, Width, Height);

                return (transform, nombre, hardware);
            }
            catch (Exception ex)
            {
                fallos.Add($"{nombre}: {ex.Message.Split('\n')[0]}");
                transform?.Dispose();
            }
        }

        throw new VideoDecoderUnavailableException(
            "Ningun decodificador H.264 acepto la configuracion.\n" +
            string.Join("\n", fallos.Select(f => "  " + f)));
    }

    /// <summary>
    /// Toma nota del tamano codificado y del trozo visible del tipo de salida
    /// que se acaba de fijar.
    ///
    /// MF_MT_MINIMUM_DISPLAY_APERTURE llega como blob de 16 bytes -- MFVideoArea:
    /// dos MFOffset de 4 y un SIZE de 8 -- y no todos los decodificadores lo
    /// ponen. Sin el, se muestra la textura entera, que es el comportamiento de
    /// antes y sigue siendo mejor que no mostrar nada.
    /// </summary>
    private void Adopt(IMFMediaType tipo)
    {
        var tamano = tipo.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        Width = (int)(tamano >> 32);
        Height = (int)(tamano & 0xFFFFFFFF);

        Aperture = new VideoAperture(0, 0, Width, Height);

        try
        {
            var area = tipo.GetBlob(MinimumDisplayAperture);

            if (area.Length >= 16)
            {
                Aperture = new VideoAperture(
                    BitConverter.ToInt16(area, 2),      // OffsetX.value
                    BitConverter.ToInt16(area, 6),      // OffsetY.value
                    BitConverter.ToInt32(area, 8),      // Area.cx
                    BitConverter.ToInt32(area, 12));    // Area.cy
            }
        }
        catch (SharpGenException)
        {
            // El atributo es opcional.
        }
    }

    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");

    /// <summary>ENTRADA primero: un decodificador no sabe que puede producir
    /// hasta saber que va a recibir. Es el orden inverso al del codificador.</summary>
    private void Configure(IMFTransform transform, int width, int height)
    {
        using var entrada = MediaFactory.MFCreateMediaType();
        entrada.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        entrada.Set(MediaTypeAttributeKeys.Subtype, Subtipo);
        entrada.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        entrada.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        transform.SetInputType(0, entrada, 0);

        // La salida se toma de la lista del propio MFT y no se fabrica a mano: un
        // decodificador rellena ahi campos que nosotros no conocemos -- stride,
        // apertura, rango de color -- y un tipo incompleto lo rechaza.
        for (var i = 0; i < 16; i++)
        {
            IMFMediaType? tipo;

            try { tipo = transform.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }

            if (tipo is null)
                break;

            using (tipo)
            {
                if (tipo.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;

                transform.SetOutputType(0, tipo, 0);
                Adopt(tipo);
                return;
            }
        }

        throw new VideoDecoderUnavailableException("No ofrece NV12 como salida.");
    }

    private static bool Flag(IMFTransform transform, Guid key)
    {
        try
        {
            var atributos = transform.Attributes;
            return atributos is not null && atributos.GetUInt32(key) != 0;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    private static string? Attribute(IMFActivate activate, Guid key)
    {
        try
        {
            return activate.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private static ulong Pack(uint alto, uint bajo) => ((ulong)alto << 32) | bajo;

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
            // Cerrando.
        }

        _events?.Dispose();
        _transform.Dispose();
        _deviceManager.Dispose();
    }
}
