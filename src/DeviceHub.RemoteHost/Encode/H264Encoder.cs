using System.Diagnostics;
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

    private readonly int Ancho, Alto, Fps, Bits;

    public H264Encoder(
        ID3D11Device device, int width, int height, int framesPerSecond, int bitrate,
        Vortice.Luid adapterLuid, uint vendorId)
    {
        Ancho = width;
        Alto = height;
        Fps = framesPerSecond;
        Bits = bitrate;

        _converter = new Nv12Converter(device, width, height);
        _frameDurationHns = 10_000_000L / framesPerSecond;

        // El gestor DXGI es lo que permite entregar texturas al MFT sin copiarlas
        // a RAM. Sin el, cada frame seria una bajada y subida de 8 MB.
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();

        var (transform, nombre, hardware) = Select(adapterLuid, vendorId);
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

    /// <summary>Eventos recibidos del MFT por tipo. 601 = pide entrada,
    /// 602 = tiene salida, 603 = drenaje terminado.</summary>
    public Dictionary<int, long> Events { get; } = [];

    /// <summary>Muestras entregadas al MFT y salidas obtenidas. Si la primera
    /// crece y la segunda no, el codificador se esta tragando los frames.</summary>
    public long Submitted { get; private set; }
    public long Produced { get; private set; }

    /// <summary>Banderas del stream de salida. El bit 0x100 dice si el
    /// codificador reserva sus propias muestras o hay que darselas.</summary>
    public uint OutputFlags { get; private set; }

    /// <summary>Motivo por el que ProcessOutput no devolvio nada, si aplica.</summary>
    public string? LastOutputIssue { get; private set; }

    /// <summary>Veces que el MFT pidio renegociar el tipo de salida.</summary>
    public long StreamChanges { get; private set; }

    public uint LastBufferStatus { get; private set; }
    public uint LastProcessStatus { get; private set; }
    public string OutputTypeBefore { get; private set; } = "-";
    public string OutputTypeAfter { get; private set; } = "-";

    private const uint StreamChange = 0xC00D6D61;   // MF_E_TRANSFORM_STREAM_CHANGE
    private const int MaxRenegotiations = 3;
    private int _renegotiationsSinFruto;

    public IReadOnlyList<EncodedFrame> Encode(VideoFrame frame, CancellationToken cancellationToken)
    {
        var salidas = new List<EncodedFrame>(1);

        DrainEvents(salidas);

        // NO se espera al evento NeedInput. Se intento -- media duracion de frame
        // sondeando -- para recuperar los frames que en la PC Intel se perdian
        // por unos milisegundos, y el resultado en NVIDIA con movimiento continuo
        // fue: 95.64 -> 46.31 FPS, p95 de 0.74 -> 26.91 ms, de 0 a 229 drops.
        //
        // El motivo es que las dos situaciones se parecen y no son la misma. Con
        // el escritorio casi quieto el codificador esta libre y el evento solo va
        // tarde; a pantalla completa esta saturado y esperarlo es tiempo tirado
        // que ademas atrasa la captura. Sin poder distinguirlas desde aqui, la
        // espera perjudica el caso que importa.
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
        Submitted++;
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
                var tipo = (int)evento.EventType;

                // Cuenta de TODO lo que llega, no solo lo que se entiende. Un
                // encoder que produce cero frames se diagnostica mirando que
                // eventos manda y cuales no: sin esto solo se puede adivinar.
                Events[tipo] = 1 + Events.GetValueOrDefault(tipo);

                switch (tipo)
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

        // 0x100 = MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.
        //
        // NO todos los codificadores las proveen, y darlo por hecho costo la
        // primera prueba en planta: NVIDIA las reserva, Intel Quick Sync NO, y
        // el codigo salia sin llamar nunca a ProcessOutput. Resultado: 121
        // frames capturados, 0 codificados, y ni un error.
        OutputFlags = (uint)info.Flags;
        var proveeMuestras = (OutputFlags & 0x100) != 0;

        while (true)
        {
            var buffers = new OutputDataBuffer[1];
            buffers[0].StreamID = 0;

            IMFSample? propia = null;

            if (!proveeMuestras)
            {
                // La reservamos nosotros, del tamano que pide el MFT.
                propia = MediaFactory.MFCreateSample();
                var buffer = MediaFactory.MFCreateMemoryBuffer((int)Math.Max(info.Size, 1));
                propia.AddBuffer(buffer);
                buffer.Dispose();

                buffers[0].Sample = propia;
            }

            var resultado = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out var estado);

            LastBufferStatus = (uint)buffers[0].Status;
            LastProcessStatus = (uint)estado;

            // MF_E_TRANSFORM_STREAM_CHANGE: no hay muestra y hay que volver a
            // fijar el tipo de salida antes de seguir. Sin tratarlo, el MFT deja
            // de pedir entrada y todo se para en silencio -- que es justo lo que
            // paso en la PC de planta: 416 capturados, 0 codificados.
            if ((uint)resultado.Code == StreamChange)
            {
                propia?.Dispose();
                Renegotiate();
                continue;
            }

            if (resultado.Failure)
            {
                // Se anota SIEMPRE el ultimo codigo, sin decidir cual es benigno:
                // 0xC00D6D72 (NEED_MORE_INPUT) y 0x8000FFFF son lo normal al
                // vaciar la cola en NVIDIA, pero no se en que devuelve Intel, y
                // filtrar por lo que conozco de una GPU ocultaria justo el dato
                // que hace falta en la otra.
                LastOutputIssue = $"0x{resultado.Code:X8} tras {Produced} salidas";

                propia?.Dispose();
                return;
            }

            var muestra = buffers[0].Sample;

            if (muestra is null)
            {
                propia?.Dispose();
                return;
            }

            using (muestra)
                salidas.Add(Read(muestra));

            Produced++;

            // Hubo fruto: el contador de renegociaciones seguidas se reinicia.
            _renegotiationsSinFruto = 0;
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
    private (IMFTransform Transform, string Name, bool Hardware) Select(Vortice.Luid luid, uint vendorId)
    {
        var fallos = new List<string>();

        // 1) Los MFT que Windows asocia a ESTA GPU. Es el criterio exacto:
        //    MFT_ENUM_ADAPTER_LUID existe justo para esto, y distingue dos
        //    tarjetas del mismo fabricante, cosa que el vendor ID no puede.
        var propios = EncoderProbe.EnumerateForAdapter(luid);

        try
        {
            foreach (var candidato in propios)
            {
                if (Intentar(candidato, out var elegido, fallos))
                    return elegido;
            }
        }
        finally
        {
            foreach (var (_, _, activate) in propios)
                activate.Dispose();
        }

        // 2) Respaldo: cualquier codificador de la maquina, con el vendor ID
        //    solo para ordenar. Se llega aqui si el filtro por LUID no devolvio
        //    nada util -- una GPU sin encoder propio, o un Windows que no
        //    soporte MFTEnum2 -- y entonces vale mas un software MFT que nada.
        using var lista = EncoderProbe.Enumerate();

        foreach (var candidato in Ordenar(lista.Items, vendorId))
        {
            if (Intentar(candidato, out var elegido, fallos))
                return elegido;
        }

        throw new VideoEncoderUnavailableException(
            "Ningun codificador H.264 acepto la configuracion.\n" +
            string.Join("\n", fallos.Select(f => "  " + f)) +
            "\n\nEn Windows Server, Media Foundation es una caracteristica opcional que no viene instalada.");
    }

    /// <summary>
    /// Activa y configura un candidato. Devuelve false y anota el motivo si no
    /// sirve: estar en la lista no garantiza que acepte esta configuracion.
    /// </summary>
    private bool Intentar(
        (string Name, bool Hardware, IMFActivate Activate) candidato,
        out (IMFTransform, string, bool) elegido, List<string> fallos)
    {
        IMFTransform? transform = null;

        try
        {
            transform = candidato.Activate.ActivateObject<IMFTransform>();

            if (Flag(transform, TransformAsync))
                transform.Attributes?.Set(TransformAsyncUnlock, 1u);

            // El gestor DXGI ANTES de los tipos: algunos MFT rechazan la
            // configuracion si todavia no saben sobre que dispositivo van.
            transform.ProcessMessage(
                TMessageType.MessageSetD3DManager,
                (UIntPtr)(ulong)(long)_deviceManager.NativePointer);

            Configure(transform, Ancho, Alto, Fps, Bits);

            elegido = (transform, candidato.Name, candidato.Hardware);
            return true;
        }
        catch (Exception ex)
        {
            fallos.Add($"{candidato.Name}: {ex.Message.Split('\n')[0]}");
            transform?.Dispose();
            elegido = default;
            return false;
        }
    }

    /// <summary>
    /// Respaldo cuando el filtro por LUID no da nada: pone delante los del mismo
    /// fabricante que la GPU donde vive la textura.
    ///
    /// Es peor criterio que el LUID -- no distingue dos tarjetas iguales, y el ID
    /// de fabricante del MFT esta documentado como opcional -- pero sirve para
    /// ordenar cuando no hay nada mejor.
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

    /// <summary>
    /// Reaplica EXACTAMENTE el mismo tipo de salida tras un STREAM_CHANGE.
    ///
    /// Cuando ProcessOutput devuelve MF_E_TRANSFORM_STREAM_CHANGE no produce
    /// muestra y hay que volver a fijar un tipo de salida antes de seguir. No
    /// significa que la resolucion haya cambiado: el MFT puede tener una
    /// preferencia nueva, y es valido responder que seguimos queriendo lo mismo.
    ///
    /// Se insiste en el mismo formato a proposito. Aceptar lo que el codificador
    /// proponga haria que la resolucion del stream dependiera del driver de cada
    /// PC, y el protocolo dejaria de ser determinista.
    /// </summary>
    private void Renegotiate()
    {
        StreamChanges++;

        if (++_renegotiationsSinFruto > MaxRenegotiations)
            throw new VideoEncoderUnavailableException(
                $"{Capabilities.Name} pidio renegociar {MaxRenegotiations} veces seguidas sin producir " +
                $"ni pedir entrada. Tipo antes: {OutputTypeBefore}. Despues: {OutputTypeAfter}.");

        OutputTypeBefore = DescribeOutputType();

        try
        {
            ApplyOutputType(_transform, Ancho, Alto, Fps, Bits);
        }
        catch (SharpGenException ex)
        {
            throw new VideoEncoderUnavailableException(
                $"{Capabilities.Name} rechaza H.264 {Ancho}x{Alto}@{Fps} tras STREAM_CHANGE ({ex.Message.Split('\n')[0]}).\n" +
                "Lo que ofrece:\n" + string.Join("\n", OfferedOutputTypes().Select(t => "  " + t)) + "\n\n" +
                "No se acepta otra resolucion en silencio: el stream dejaria de ser el mismo en cada PC.");
        }

        OutputTypeAfter = DescribeOutputType();
    }

    private IEnumerable<string> OfferedOutputTypes()
    {
        for (var i = 0; i < 16; i++)
        {
            IMFMediaType? tipo;

            try { tipo = _transform.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { yield break; }

            if (tipo is null)
                yield break;

            using (tipo)
                yield return Describe(tipo);
        }
    }

    private string DescribeOutputType()
    {
        try
        {
            using var tipo = _transform.GetOutputCurrentType(0);
            return tipo is null ? "(ninguno)" : Describe(tipo);
        }
        catch (SharpGenException)
        {
            return "(no legible)";
        }
    }

    private static string Describe(IMFMediaType tipo)
    {
        try
        {
            var tamano = tipo.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            var subtipo = tipo.GetGUID(MediaTypeAttributeKeys.Subtype);
            var nombre = subtipo == VideoFormatGuids.H264 ? "H264" : subtipo.ToString();

            return $"{nombre} {tamano >> 32}x{tamano & 0xFFFFFFFF}";
        }
        catch (SharpGenException)
        {
            return "(incompleto)";
        }
    }

    /// <summary>SALIDA primero y entrada despues: un codificador H.264 no sabe
    /// que entradas admite hasta saber que tiene que producir.</summary>
    private static void Configure(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        ApplyOutputType(transform, width, height, fps, bitrate);

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

    /// <summary>
    /// Lee un atributo booleano del MFT. Sin atributos, FALSE.
    ///
    /// Estaba escrito `transform.Attributes?.GetUInt32(key) != 0`, que con
    /// Attributes en null da `null != 0`, es decir TRUE: un MFT sin tienda de
    /// atributos se declaraba asincrono y entraba en el camino equivocado.
    /// </summary>
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

    private static void ApplyOutputType(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        using var salida = MediaFactory.MFCreateMediaType();
        salida.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        salida.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        salida.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        salida.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        salida.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
        salida.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        transform.SetOutputType(0, salida, 0);
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
