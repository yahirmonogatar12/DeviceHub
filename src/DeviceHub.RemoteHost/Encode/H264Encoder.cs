using System.Runtime.InteropServices;
using DeviceHub.RemoteHost.Capture;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Codificador H.264 sobre Media Foundation, alimentado con texturas D3D11.
///
/// Ruta unica: BGRA -> NV12 en GPU -> superficie DXGI -> MFT. Sin atajo por
/// ARGB32 aunque AMD y NVIDIA lo acepten, porque el codificador por software
/// solo admite NV12 y es el que acaba usando una PC de planta sin hardware.
///
/// Los MFT de hardware son ASINCRONOS: no se les llama ProcessInput cuando uno
/// quiere, sino cuando ellos lo piden por evento. Tratarlos como sincronos
/// compila, arranca y luego no produce nada.
/// </summary>
public sealed class H264Encoder : IVideoEncoder
{
    private const int MessageNeedInput = 601;   // METransformNeedInput
    private const int MessageHaveOutput = 602;  // METransformHaveOutput
    private const int EventNoWait = 0x00000001;

    // Vortice no expone estas claves.
    private static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid HardwareUrl = new("2fb866ac-b078-4942-ab6c-003d05cda674");

    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFMediaEventGenerator? _events;
    private readonly Nv12Converter _converter;
    private readonly long _frameDurationHns;

    private int _needInput;
    private ulong _sequence;

    public H264Encoder(ID3D11Device device, int width, int height, int framesPerSecond, int bitrate, uint vendorId)
    {
        _converter = new Nv12Converter(device, width, height);
        _frameDurationHns = 10_000_000L / framesPerSecond;

        // El gestor DXGI es lo que permite entregar texturas al MFT sin copiarlas
        // a RAM. Sin el, cada frame seria una bajada y subida de 8 MB.
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();

        var (transform, nombre, hardware) = Select(width, height, framesPerSecond, bitrate, _deviceManager, vendorId);
        _transform = transform;

        var asincrono = Flag(_transform, TransformAsync);

        Capabilities = new VideoEncoderCapabilities(
            nombre, hardware, asincrono, "NV12", width, height, framesPerSecond, bitrate);

        if (asincrono)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _needInput = 0;
        }
        else
        {
            // Un MFT sincrono acepta entrada siempre que se le pida.
            _needInput = int.MaxValue;
        }

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public VideoEncoderCapabilities Capabilities { get; }
    public long Dropped { get; private set; }

    public IReadOnlyList<EncodedFrame> Encode(VideoFrame frame, CancellationToken cancellationToken)
    {
        var salidas = new List<EncodedFrame>(1);

        DrainEvents(salidas);

        if (_needInput > 0)
        {
            _converter.Convert(frame.Texture);
            Submit(frame.TimestampUs);

            if (_needInput != int.MaxValue)
                _needInput--;
        }
        else
        {
            // El codificador no pidio entrada: va por detras de la captura.
            Dropped++;
        }

        DrainEvents(salidas);

        // Un MFT sincrono no avisa por evento: hay que preguntarle.
        if (_events is null)
            DrainOutputs(salidas);

        return salidas;
    }

    private void Submit(long timestampUs)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID, _converter.Output, 0, false);

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampUs * 10;      // microsegundos -> unidades de 100 ns
        sample.SampleDuration = _frameDurationHns;

        _transform.ProcessInput(0, sample, 0);
    }

    /// <summary>Consume los eventos que haya SIN bloquear.</summary>
    private void DrainEvents(List<EncodedFrame> salidas)
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
                // MF_E_NO_EVENTS_AVAILABLE: no hay nada pendiente.
                return;
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

    private void DrainOutputs(List<EncodedFrame> salidas)
    {
        var info = _transform.GetOutputStreamInfo(0);

        while (true)
        {
            var buffers = new OutputDataBuffer[1];
            buffers[0].StreamID = 0;

            // Si el MFT no reserva sus propias muestras, hay que darselas hechas.
            // Bandera 0x100 = MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.
            var proveeMuestras = ((uint)info.Flags & 0x100) != 0;

            if (!proveeMuestras)
                return; // los encoders H.264 siempre las proveen; si no, no hay ruta simple

            var resultado = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out _);

            if (resultado.Failure || buffers[0].Sample is null)
                return;

            using var muestra = buffers[0].Sample;
            salidas.Add(Read(muestra));
        }
    }

    private EncodedFrame Read(IMFSample muestra)
    {
        using var buffer = muestra.ConvertToContiguousBuffer();

        buffer.Lock(out var puntero, out _, out var longitud);

        try
        {
            var datos = new byte[longitud];
            Marshal.Copy(puntero, datos, 0, longitud);

            // MFSampleExtension_CleanPoint: 1 = keyframe.
            var esClave = false;

            try
            {
                esClave = muestra.GetUInt32(CleanPoint) != 0;
            }
            catch (SharpGenException)
            {
                // El atributo puede no estar; se asume que no lo es.
            }

            return new EncodedFrame(
                ++_sequence, muestra.SampleTime / 10, esClave,
                Capabilities.Width, Capabilities.Height, datos);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    /// <summary>
    /// Elige codificador. El orden lo pone MFTEnumEx con SORTANDFILTER: hardware
    /// primero, software despues. Se prueba uno a uno porque estar en la lista no
    /// garantiza que acepte esta configuracion -- el AVC DX12 de Microsoft, por
    /// ejemplo, aparece y luego rechaza todo sin un dispositivo D3D12.
    /// </summary>
    private static (IMFTransform Transform, string Name, bool Hardware) Select(
        int width, int height, int fps, int bitrate, IMFDXGIDeviceManager deviceManager, uint vendorId)
    {
        var fallos = new List<string>();

        using var lista = EncoderProbe.Enumerate();

        foreach (var (nombre, hardware, activate) in Ordenar(lista.Items, vendorId))
        {
            {
                IMFTransform? transform = null;

                try
                {
                    transform = activate.ActivateObject<IMFTransform>();

                    if (Flag(transform, TransformAsync))
                        transform.Attributes?.Set(TransformAsyncUnlock, 1u);

                    // El gestor DXGI ANTES de los tipos: algunos MFT rechazan la
                    // configuracion si todavia no saben sobre que dispositivo van.
                    transform.ProcessMessage(
                        TMessageType.MessageSetD3DManager,
                        (UIntPtr)(ulong)(long)deviceManager.NativePointer);

                    Configure(transform, width, height, fps, bitrate);

                    return (transform, nombre, hardware);
                }
                catch (Exception ex)
                {
                    var donde = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                    fallos.Add($"{nombre}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}  {donde}");
                    transform?.Dispose();
                }
            }
        }

        throw new VideoEncoderUnavailableException(
            "Ningun codificador H.264 acepto la configuracion.\n" +
            string.Join("\n", fallos.Select(f => "  " + f)) +
            "\n\nEn Windows Server, Media Foundation es una caracteristica opcional que no viene instalada.");
    }

    /// <summary>
    /// Pone delante los codificadores del MISMO fabricante que la GPU donde vive
    /// la textura.
    ///
    /// Sin esto, en un equipo con dos GPUs se elige el primero de la lista y sale
    /// un disparate silencioso: capturar en la NVIDIA y codificar en el MFT de
    /// AMD funciona -- da H.264 valido -- pero cada frame cruza de una tarjeta a
    /// otra por memoria del sistema. En la primera medida eso costo 11 ms de
    /// encode con el motor de video al 0%: el trabajo no lo estaba haciendo la
    /// GPU que creiamos.
    ///
    /// Se compara el ID de fabricante que declara el propio MFT, no su nombre:
    /// una lista de nombres seria la misma trampa que la lista negra de
    /// adaptadores de red que este proyecto ya evito una vez.
    /// </summary>
    private static IEnumerable<(string Name, bool Hardware, IMFActivate Activate)> Ordenar(
        IEnumerable<(string Name, bool Hardware, IMFActivate Activate)> candidatos, uint vendorId)
    {
        var esperado = $"VEN_{vendorId:X4}";

        return candidatos
            .Select(c => (Candidato: c, MismoFabricante: Vendor(c.Activate)?.Contains(esperado, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(x => x.MismoFabricante)
            .ThenByDescending(x => x.Candidato.Hardware)
            .Select(x => x.Candidato)
            .ToList();
    }

    private static readonly Guid HardwareVendorId = new("3aecb0cc-035b-4bcc-8185-2b8d551ef3af");

    private static string? Vendor(IMFActivate activate)
    {
        try
        {
            return activate.GetString(HardwareVendorId);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    /// <summary>SALIDA primero y entrada despues: un codificador H.264 no sabe
    /// que entradas admite hasta saber que tiene que producir.</summary>
    private static void Configure(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        using var salida = MediaFactory.MFCreateMediaType();
        salida.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        salida.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        salida.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        salida.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        salida.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
        salida.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        transform.SetOutputType(0, salida, 0);

        using var entrada = MediaFactory.MFCreateMediaType();
        entrada.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        entrada.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        entrada.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        entrada.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
        entrada.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        transform.SetInputType(0, entrada, 0);

        // Baja latencia por atributo, sin ICodecAPI. Si un codificador lo ignora,
        // se vera en la latencia captura->codificado y se documentara antes de
        // meter mas interop.
        try
        {
            transform.Attributes?.Set(SinkWriterAttributeKeys.LowLatency, true);
        }
        catch (SharpGenException)
        {
            // No todos lo admiten; no es motivo para rechazar el codificador.
        }
    }

    private static bool Flag(IMFTransform transform, Guid key)
    {
        try
        {
            return transform.Attributes?.GetUInt32(key) != 0;
        }
        catch (SharpGenException)
        {
            return false;
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
            // Cerrando: que falle el aviso no cambia nada.
        }

        _events?.Dispose();
        _transform.Dispose();
        _deviceManager.Dispose();
        _converter.Dispose();
    }
}
