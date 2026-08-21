using System.Diagnostics;
using System.Runtime.InteropServices;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Capture;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Codificador H.264 o H.265 sobre Media Foundation, alimentado con texturas
/// D3D11. El nombre se queda por lo que era: los dos comparten TODO menos el
/// subtipo de salida, y partirlo en dos clases seria duplicar 700 lineas para
/// cambiar un GUID.
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

    /// <summary>
    /// `width`/`height` es lo que se CODIFICA. `anchoEntrada`/`altoEntrada` es lo
    /// que entrega la captura, y si son mayores el convertidor escala de paso --
    /// es un blit, y escalar no le cuesta mas que copiar.
    ///
    /// Existe por el modo compuesto: dos monitores dan una imagen que el
    /// codificador de una iGPU no siempre traga.
    /// </summary>
    /// <summary>
    /// MFVideoFormat_HEVC. Vortice no lo expone, asi que se escribe a mano igual
    /// que CleanPoint: es el fourcc 'HEVC' dentro del GUID base de Media
    /// Foundation, no un identificador inventado.
    /// </summary>
    private static readonly Guid Hevc = new("43564548-0000-0010-8000-00aa00389b71");

    /// <summary>Que codec produce este codificador. La entrada es NV12 en los
    /// dos casos; lo unico que cambia es el subtipo de salida.</summary>
    public VideoCodec Codec { get; }

    private Guid Subtipo => Codec == VideoCodec.H265 ? Hevc : VideoFormatGuids.H264;

    private string Nombre => Codec == VideoCodec.H265 ? "H.265" : "H.264";

    public H264Encoder(
        ID3D11Device device, int width, int height, int framesPerSecond, int bitrate,
        Vortice.Luid adapterLuid, uint vendorId, int anchoEntrada = 0, int altoEntrada = 0,
        VideoCodec codec = VideoCodec.H264)
    {
        Codec = codec;

        if (anchoEntrada <= 0) anchoEntrada = width;
        if (altoEntrada <= 0) altoEntrada = height;

        Ancho = width;
        Alto = height;
        Fps = framesPerSecond;
        Bits = bitrate;

        _converter = new Nv12Converter(device, anchoEntrada, altoEntrada, width, height);
        _frameDurationHns = 10_000_000L / framesPerSecond;

        // El gestor DXGI es lo que permite entregar texturas al MFT sin copiarlas
        // a RAM. Sin el, cada frame seria una bajada y subida de 8 MB.
        // ESTA ES LA LLAMADA QUE SE CAE EN UNA MAQUINA SIN GPU DE VERDAD.
        //
        // ResetDevice pregunta al dispositivo D3D11 por su ID3D11VideoDevice, y
        // un dispositivo que no hace video no lo tiene: contesta E_NOINTERFACE,
        // que traducido es "aqui no hay tuberia de video por hardware". Pasa en
        // los servidores con adaptador de gestion, y pasa con WARP, que es el
        // D3D11 por software al que se cae la captura en esas mismas maquinas.
        //
        // El HRESULT suelto no lo explicaba y mandaba a buscar el problema donde
        // no esta -- se veia "al abrir las capturas" y la captura ya funcionaba.
        try
        {
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(device).CheckError();
        }
        catch (Exception ex)
        {
            throw new VideoEncoderUnavailableException(
                "Esta maquina no tiene tuberia de video por hardware: su dispositivo " +
                "grafico no expone ID3D11VideoDevice. Es lo normal en un servidor sin " +
                "tarjeta grafica. Dos cosas que probar, en orden: " +
                "1) Install-WindowsFeature Server-Media-Foundation y reiniciar. " +
                "2) Si aun asi falla, esa PC se controla con RustDesk, que trae su propio " +
                "codificador por software y no depende de Media Foundation.", ex);
        }

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

    /// <summary>MFSampleExtension_ForceKeyFrame.</summary>
    private static readonly Guid ForceKeyFrame =
        new("089e57c7-47d3-4a26-bf9c-4b64fafb5d1e");

    /// <summary>CODECAPI_AVEncCommonMeanBitRate.</summary>
    private static readonly Guid MeanBitRate =
        new("f7222374-2144-4815-b550-a37f8e12ee52");

    /// <summary>CODECAPI_AVEncCommonLowLatency.</summary>
    private static readonly Guid BajaLatencia =
        new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");

    /// <summary>CODECAPI_AVEncMPVDefaultBPictureCount.</summary>
    private static readonly Guid CuantasB =
        new("8d390aac-dc5c-4200-b57f-814d04babab2");

    /// <summary>
    /// Cuantos B-frames dice el codificador que va a emitir. -1 si no contesta.
    ///
    /// TIENE QUE SER 0. Un B-frame se codifica mirando un frame FUTURO, asi que
    /// obliga a emitir en un orden distinto del de reproduccion -- y el
    /// decodificador del visor va en MF_LOW_LATENCY, que es justo el modo en el
    /// que NO reordena: saca los frames segun le llegan. Con B-frames en el
    /// flujo, eso se ve como una ventana que al arrastrarla avanza, retrocede y
    /// vuelve a avanzar. No es la red ni el desgarro: el desgarro parte un frame
    /// en dos mitades, pero no puede enseñar el pasado.
    /// </summary>
    public int BFrames { get; private set; } = -1;

    private bool _forzarKeyframe;

    public long KeyframesForzados { get; private set; }
    public long BitratesAplicados { get; private set; }

    /// <summary>
    /// El proximo frame sale como IDR. Lo pide el visor cuando pierde la
    /// sincronia, y hasta la Fase 13 su peticion se registraba y se tiraba.
    ///
    /// SOLO desde el hilo de captura, como todo lo que toca el MFT.
    /// </summary>
    public void ForzarKeyframe() => _forzarKeyframe = true;

    /// <summary>
    /// Cambia el bitrate objetivo SIN rehacer el codificador.
    ///
    /// Recrearlo seria lo facil y esta descartado: estrena SPS, o sea
    /// config_version nueva, o sea que el visor tira decodificador y presentador.
    /// Hacer eso cada vez que la red respira convertiria la adaptacion en el
    /// problema que viene a resolver.
    ///
    /// Devuelve false si el codificador no expone ICodecAPI. No es un fallo: se
    /// sigue con el bitrate fijo, que es lo que habia.
    /// </summary>
    public bool CambiarBitrate(int bitsPorSegundo)
        => ConCodec(_transform, codec =>
        {
            var api = MeanBitRate;
            object valor = bitsPorSegundo;

            if (codec.SetValue(ref api, ref valor) < 0)
                return false;

            BitratesAplicados++;
            return true;
        });

    /// <summary>
    /// Vortice no cubre ICodecAPI, asi que se declara aqui y solo esa. Es el
    /// precedente de la Fase 2: interop puntual para la API que falta, nunca
    /// migrar toda la capa a otra biblioteca por una funcion.
    ///
    /// Devuelve false si el codificador no la expone. No es un fallo: se sigue
    /// con lo que haya, que es lo que habia antes de pedirlo.
    /// </summary>
    private static bool ConCodec(IMFTransform transform, Func<ICodecAPI, bool> hacer)
    {
        object? crudo = null;

        try
        {
            crudo = System.Runtime.InteropServices.Marshal
                .GetObjectForIUnknown(transform.NativePointer);

            return crudo is ICodecAPI codec && hacer(codec);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (crudo is not null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(crudo);
        }
    }

    /// <summary>
    /// ICodecAPI, declarada a mano porque Vortice no la trae.
    ///
    /// El ORDEN de los metodos es el de la vtable y no se puede tocar: aunque
    /// solo se use SetValue, los seis de delante tienen que estar declarados o
    /// la llamada acabaria en el metodo equivocado. Por eso estan y por eso no
    /// se borran "porque no se usan".
    /// </summary>
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [System.Runtime.InteropServices.PreserveSig] int IsSupported(ref Guid api);
        [System.Runtime.InteropServices.PreserveSig] int IsModifiable(ref Guid api);

        [System.Runtime.InteropServices.PreserveSig]
        int GetParameterRange(ref Guid api, out IntPtr minimo, out IntPtr maximo, out IntPtr paso);

        [System.Runtime.InteropServices.PreserveSig]
        int GetParameterValues(ref Guid api, out IntPtr valores, out int cuantos);

        [System.Runtime.InteropServices.PreserveSig]
        int GetDefaultValue(ref Guid api, out IntPtr valor);

        [System.Runtime.InteropServices.PreserveSig]
        int GetValue(ref Guid api,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Struct)] out object valor);

        [System.Runtime.InteropServices.PreserveSig]
        int SetValue(ref Guid api,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Struct)] ref object valor);
    }

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

        // Fase 13. Un IDR bajo demanda se pide POR MUESTRA, con un atributo, y no
        // por ICodecAPI: es una linea en vez de una interfaz COM entera, y el
        // codificador que lo ignore simplemente tardara un GOP mas -- que es
        // exactamente lo que pasaba antes, cuando el KeyframeRequest llegaba y
        // nadie hacia nada con el.
        if (_forzarKeyframe)
        {
            _forzarKeyframe = false;

            try
            {
                sample.Set(ForceKeyFrame, 1u);
                KeyframesForzados++;
            }
            catch (SharpGenException)
            {
                // Codificador que no admite el atributo. No es motivo para tirar
                // el frame: sale como frame normal.
            }
        }

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
        var propios = EncoderProbe.EnumerateForAdapter(luid, Subtipo);

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
        using var lista = EncoderProbe.Enumerate(Subtipo);

        foreach (var candidato in Ordenar(lista.Items, vendorId))
        {
            if (Intentar(candidato, out var elegido, fallos))
                return elegido;
        }

        throw new VideoEncoderUnavailableException(
            $"Ningun codificador {Nombre} acepto la configuracion.\n" +
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
            ApplyOutputType(_transform, Ancho, Alto, Fps, Bits, Subtipo, Nombre);
        }
        catch (SharpGenException ex)
        {
            throw new VideoEncoderUnavailableException(
                $"{Capabilities.Name} rechaza {Nombre} {Ancho}x{Alto}@{Fps} tras STREAM_CHANGE ({ex.Message.Split('\n')[0]}).\n" +
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

    /// <summary>SALIDA primero y entrada despues: un codificador no sabe que
    /// entradas admite hasta saber que tiene que producir.</summary>
    private void Configure(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        ApplyOutputType(transform, width, height, fps, bitrate, Subtipo, Nombre);

        using var entrada = MediaFactory.MFCreateMediaType();
        entrada.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        entrada.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        entrada.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        entrada.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
        entrada.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);

        try
        {
            transform.SetInputType(0, entrada, 0);
        }
        catch (SharpGenException ex)
        {
            // El tipo de ENTRADA es el que rechaza las resoluciones que la GPU no
            // sabe tragar. Distinguirlo del de salida importa: uno se arregla
            // bajando la resolucion y el otro tocando perfil o bitrate.
            throw new VideoEncoderUnavailableException(
                $"El codificador no acepta NV12 de {width}x{height} como entrada: {ex.ResultCode}", ex);
        }

        try
        {
            transform.Attributes?.Set(SinkWriterAttributeKeys.LowLatency, true);
        }
        catch (SharpGenException)
        {
            // No todos lo admiten; no es motivo para rechazar el codificador.
        }

        SinReordenar(transform);
    }

    /// <summary>
    /// Se lo pide TAMBIEN por ICodecAPI, y se lee lo que contesta.
    ///
    /// MF_LOW_LATENCY es un atributo del MFT y un codificador puede aceptarlo y
    /// no hacerle caso -- el comentario que habia aqui decia que si lo ignoraba
    /// "se veria en la latencia", y no: un B-frame no cuesta latencia medible en
    /// el host, cuesta ORDEN. El frame sale antes que el que va delante suyo, y
    /// eso no aparece en ningun percentil de encode.
    ///
    /// Donde aparece es en la pantalla del tecnico: el decodificador del visor
    /// tambien va en baja latencia, o sea que NO reordena, asi que enseña los
    /// frames en el orden en que llegan. Una ventana arrastrada avanza,
    /// retrocede y vuelve a avanzar.
    ///
    /// Y se LEE de vuelta en vez de darlo por hecho. Pedirlo y suponer que se
    /// obedecio es como llegamos a "el codificador no es el cuello de botella"
    /// mirando un contador que nunca se rellenaba.
    /// </summary>
    private void SinReordenar(IMFTransform transform)
        => ConCodec(transform, codec =>
        {
            var api = BajaLatencia;
            object si = true;

            codec.SetValue(ref api, ref si);

            api = CuantasB;
            object cero = 0u;

            codec.SetValue(ref api, ref cero);

            api = CuantasB;

            BFrames = codec.GetValue(ref api, out var leido) >= 0 && leido is not null
                ? Convert.ToInt32(leido)
                : -1;

            return true;
        });

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

    private static void ApplyOutputType(
        IMFTransform transform, int width, int height, int fps, int bitrate,
        Guid subtipo, string nombre)
    {
        using var salida = MediaFactory.MFCreateMediaType();
        salida.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        salida.Set(MediaTypeAttributeKeys.Subtype, subtipo);
        salida.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        salida.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        salida.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
        salida.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        try
        {
            transform.SetOutputType(0, salida, 0);
        }
        catch (SharpGenException ex)
        {
            throw new VideoEncoderUnavailableException(
                $"El codificador no acepta {nombre} de {width}x{height} como salida " +
                $"(perfil o bitrate fuera de rango): {ex.ResultCode}", ex);
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
