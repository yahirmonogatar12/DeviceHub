using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Audio;

/// <summary>Lo que no se pudo montar y por que.</summary>
public sealed class AacNoDisponibleException(string mensaje, Exception? interna = null)
    : Exception(mensaje, interna);

/// <summary>
/// AAC por Media Foundation, para el sonido de la PC controlada.
///
/// POR QUE AAC Y NO PCM CRUDO. El sonido que da WASAPI son 384 KB/s -- tres
/// megabits, mas que el video entero. Incluso reducido a mono de 16 bits siguen
/// siendo 768 kbps sobre una red de planta que ya lleva el escritorio. AAC deja
/// eso en 96 y viene DENTRO de Media Foundation, el mismo stack que ya usamos
/// para video: cero dependencias nuevas.
///
/// Y por que no Opus, que seria mejor: es una biblioteca nativa, y este
/// repositorio no tiene ninguna. Ese precio se paga cuando un numero lo pida --
/// hoy no lo pide.
///
/// EL DECODIFICADOR NECESITA LA CONFIGURACION. AAC no es como H.264, donde los
/// parametros pueden viajar dentro del flujo: aqui viven en el
/// AudioSpecificConfig, que Media Foundation entrega en MF_MT_USER_DATA del
/// tipo de salida y hay que mandar aparte. Sin el, el otro lado no puede
/// descodificar ni un byte.
/// </summary>
public sealed class AacEncoder : IDisposable
{
    /// <summary>CLSID_AACMFTEncoder.</summary>
    private static readonly Guid Codificador = new("93AF0C51-2275-45d2-A35B-F2BA21CAED00");

    private const int MessageBeginStreaming = 0x10000000 | 1;   // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
    private const uint TransformNeedMoreInput = 0xC00D6D72;     // MF_E_TRANSFORM_NEED_MORE_INPUT

    private readonly IMFTransform _transform;
    private readonly int _bytesPorFotograma;
    private byte[] _copia = [];

    /// <summary>
    /// El AudioSpecificConfig que el decodificador necesita para arrancar. Se
    /// manda una vez, igual que el SPS/PPS del video.
    /// </summary>
    public byte[] Configuracion { get; } = [];

    public int Hz { get; }
    public int Canales { get; }
    public int BitsPorSegundo { get; }

    public long Entradas { get; private set; }
    public long Salidas { get; private set; }

    /// <summary>El ultimo codigo raro de ProcessOutput, si lo hubo. NEED_MORE_INPUT
    /// no cuenta: es lo normal mientras el codificador junta un bloque.</summary>
    public string? UltimoProblema { get; private set; }

    /// <param name="hz">44100 o 48000. El codificador de Windows no acepta otros.</param>
    /// <param name="canales">1 o 2.</param>
    /// <param name="bytesPorSegundo">12000, 16000, 20000 o 24000, o sea 96, 128,
    /// 160 y 192 kbps. Son los unicos que admite.</param>
    public AacEncoder(int hz, int canales, int bytesPorSegundo = 12000)
    {
        if (hz is not (44100 or 48000))
            throw new AacNoDisponibleException($"El codificador AAC de Windows no acepta {hz} Hz.");

        if (canales is not (1 or 2))
            throw new AacNoDisponibleException($"El codificador AAC de Windows no acepta {canales} canales.");

        Hz = hz;
        Canales = canales;
        BitsPorSegundo = bytesPorSegundo * 8;
        _bytesPorFotograma = canales * 2;

        // SE ENUMERA, no se crea por CLSID.
        //
        // Activator.CreateInstance sobre el CLSID devuelve un envoltorio COM
        // generico que NO se puede convertir al IMFTransform de Vortice -- que
        // es una clase, no una interfaz suelta. Fallaba siempre, y el mensaje
        // culpaba a Media Foundation de no estar instalado en una PC donde el
        // video codifica sin problema.
        //
        // Media Foundation tiene su propia forma de hacer esto y es la que ya
        // usa el codificador de video: enumerar por categoria y activar.
        _transform = Activar()
            ?? throw new AacNoDisponibleException(
                "Windows no expone ningun codificador AAC. En Windows Server, Media " +
                "Foundation es una caracteristica opcional que no viene instalada.");

        try
        {
            // LA SALIDA PRIMERO, Y LA SUYA, NO UNA CONSTRUIDA A MANO.
            //
            // Construir el tipo de salida campo a campo fallaba con
            // MF_E_INVALIDMEDIATYPE aunque los valores coincidieran con los que
            // el propio codificador enumera. Un tipo de Media Foundation lleva
            // mas atributos de los que se ven, y adivinar cuales faltan es lo
            // que convierte esto en una tarde perdida.
            //
            // GetOutputAvailableType los da hechos: se elige el que cuadra y se
            // usa tal cual. Y si un dia una maquina ofrece otra combinacion, el
            // mensaje dice exactamente cuales habia.
            using var salida = SalidaQueCuadre(_transform, hz, canales, bytesPorSegundo)
                ?? throw new AacNoDisponibleException(
                    $"El codificador AAC no ofrece {hz} Hz, {canales} canal(es), " +
                    $"{bytesPorSegundo * 8 / 1000} kbps.");

            _transform.SetOutputType(0, salida, 0);

            // Y LA ENTRADA TAMBIEN LA SUYA. Era el ultimo tipo construido a
            // mano y era el que fallaba: con la salida ya fijada, el codificador
            // enumera exactamente que PCM acepta, y usarlo tal cual quita la
            // ultima suposicion de aqui.
            using var entrada = EntradaQueCuadre(_transform, hz, canales)
                ?? throw new AacNoDisponibleException(
                    $"El codificador AAC no acepta PCM de {hz} Hz con {canales} canal(es).");

            _transform.SetInputType(0, entrada, 0);

            Configuracion = LeerConfiguracion(salida);
            _transform.ProcessMessage((TMessageType)MessageBeginStreaming, UIntPtr.Zero);
        }
        catch (AacNoDisponibleException)
        {
            _transform.Dispose();
            throw;
        }
        catch (SharpGenException ex)
        {
            _transform.Dispose();

            throw new AacNoDisponibleException(
                $"El codificador AAC no acepto {hz} Hz, {canales} canales, " +
                $"{bytesPorSegundo * 8 / 1000} kbps: {ex.ResultCode}", ex);
        }
    }

    /// <summary>El tipo de salida que ofrece el propio codificador y cuadra con
    /// lo pedido, o null si no ofrece ninguno.</summary>
    private static IMFMediaType? SalidaQueCuadre(
        IMFTransform transform, int hz, int canales, int bytesPorSegundo)
    {
        for (var i = 0; i < 64; i++)
        {
            IMFMediaType tipo;

            try
            {
                tipo = transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                return null;   // no hay mas
            }

            try
            {
                if (tipo.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond) == hz
                    && tipo.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels) == canales
                    && tipo.GetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond) == bytesPorSegundo)
                {
                    return tipo;
                }
            }
            catch (SharpGenException)
            {
                // Un tipo sin esos atributos no es el que buscamos.
            }

            tipo.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Mete PCM de 16 bits y devuelve los paquetes AAC que salgan, que pueden
    /// ser CERO: AAC trabaja en bloques de 1024 muestras y el codificador
    /// acumula hasta tener uno entero.
    /// </summary>
    public IReadOnlyList<byte[]> Codificar(ReadOnlySpan<byte> pcm, long timestampUs)
    {
        var salidas = new List<byte[]>(1);

        if (pcm.Length < _bytesPorFotograma)
            return salidas;

        using (var muestra = MediaFactory.MFCreateSample())
        {
            using var bufer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);

            bufer.Lock(out var destino, out _, out _);

            try
            {
                Marshal.Copy(Copia(pcm), 0, destino, pcm.Length);
            }
            finally
            {
                bufer.Unlock();
            }

            // CurrentLength A MANO: MFCreateMemoryBuffer reserva sitio y deja la
            // longitud en cero. Un MFT que reciba un bufer de longitud cero no
            // se queja -- se traga la entrada y no saca nada. Es el mismo
            // tropiezo que costo la Fase 2 en video.
            bufer.CurrentLength = pcm.Length;

            muestra.AddBuffer(bufer);
            muestra.SampleTime = timestampUs * 10;
            muestra.SampleDuration = pcm.Length / _bytesPorFotograma * 10_000_000L / Hz;

            _transform.ProcessInput(0, muestra, 0);
            Entradas++;
        }

        Vaciar(salidas);
        return salidas;
    }

    private void Vaciar(List<byte[]> salidas)
    {
        var info = _transform.GetOutputStreamInfo(0);

        // 0x100 = MFT_OUTPUT_STREAM_PROVIDES_SAMPLES. Si no las provee, las
        // reservamos nosotros. Darlo por hecho costo una prueba entera de planta
        // en video -- NVIDIA las reserva, Intel Quick Sync no -- y aqui no se va
        // a repetir.
        var proveeMuestras = ((uint)info.Flags & 0x100) != 0;

        while (true)
        {
            var buffers = new OutputDataBuffer[1];
            buffers[0].StreamID = 0;

            IMFSample? propia = null;

            if (!proveeMuestras)
            {
                propia = MediaFactory.MFCreateSample();

                var bufer = MediaFactory.MFCreateMemoryBuffer((int)Math.Max(info.Size, 4096));
                propia.AddBuffer(bufer);
                bufer.Dispose();

                buffers[0].Sample = propia;
            }

            var resultado = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out _);

            if (resultado.Failure)
            {
                // NEED_MORE_INPUT es lo normal: todavia no hay un bloque entero.
                // Cualquier otra cosa se apunta, porque un codificador que falla
                // en silencio es como se pierde el sonido sin que nadie lo sepa.
                if ((uint)resultado.Code != TransformNeedMoreInput)
                    UltimoProblema = $"0x{resultado.Code:X8} tras {Salidas} salidas";

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
                salidas.Add(Leer(muestra));

            Salidas++;
        }
    }

    /// <summary>El tipo de ENTRADA que ofrece el codificador con la salida ya
    /// fijada, o null si ninguno cuadra.</summary>
    private static IMFMediaType? EntradaQueCuadre(IMFTransform transform, int hz, int canales)
    {
        for (var i = 0; i < 64; i++)
        {
            IMFMediaType tipo;

            try
            {
                tipo = transform.GetInputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                return null;
            }

            try
            {
                if (tipo.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond) == hz
                    && tipo.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels) == canales
                    && tipo.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample) == 16)
                {
                    return tipo;
                }
            }
            catch (SharpGenException)
            {
            }

            tipo.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Lo que el codificador AAC de esta maquina dice que acepta.
    ///
    /// Se pregunta en vez de adivinar. MF_E_INVALIDMEDIATYPE no dice CUAL de los
    /// dos tipos rechaza ni por que, y probar combinaciones a ciegas es como se
    /// pierden tardes -- GetOutputAvailableType las enumera de una vez.
    /// </summary>
    public static IReadOnlyList<string> Acepta()
    {
        var lineas = new List<string>();
        var transform = Activar();

        if (transform is null)
            return ["No hay ningun codificador AAC en esta maquina."];

        using (transform)
        {
            for (var i = 0; ; i++)
            {
                IMFMediaType tipo;

                try
                {
                    tipo = transform.GetOutputAvailableType(0, i);
                }
                catch (SharpGenException)
                {
                    break;   // no hay mas
                }

                using (tipo)
                    lineas.Add($"  salida {i}: {Describir(tipo)}");

                if (i > 40)
                    break;
            }
        }

        return lineas.Count > 0 ? lineas : ["El codificador no enumero ningun tipo de salida."];
    }

    private static string Describir(IMFMediaType tipo)
    {
        string Leer(Guid clave, string nombre)
        {
            try
            {
                return $"{nombre}={tipo.GetUInt32(clave)}";
            }
            catch (Exception)
            {
                return $"{nombre}=?";
            }
        }

        return string.Join("  ",
            Leer(MediaTypeAttributeKeys.AudioSamplesPerSecond, "Hz"),
            Leer(MediaTypeAttributeKeys.AudioNumChannels, "canales"),
            Leer(MediaTypeAttributeKeys.AudioBitsPerSample, "bits"),
            Leer(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, "bytes/s"),
            Leer(MediaTypeAttributeKeys.AacPayloadType, "payload"));
    }

    /// <summary>
    /// El primer codificador de audio que sepa producir AAC, activado.
    /// Null si Windows no ofrece ninguno.
    /// </summary>
    private static IMFTransform? Activar()
    {
        var salida = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Audio,
            GuidSubtype = AudioFormatGuids.Aac
        };

        using var coleccion = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.AudioEncoder, 0, null, salida);

        foreach (var activate in coleccion)
        {
            try
            {
                return activate.ActivateObject<IMFTransform>();
            }
            catch (Exception)
            {
                // Uno que no arranca no descarta a los demas.
            }
            finally
            {
                activate.Dispose();
            }
        }

        return null;
    }

    private static byte[] Leer(IMFSample muestra)
    {
        using var plano = muestra.ConvertToContiguousBuffer();

        plano.Lock(out var puntero, out _, out var largo);

        try
        {
            var bytes = new byte[largo];
            Marshal.Copy(puntero, bytes, 0, largo);

            return bytes;
        }
        finally
        {
            plano.Unlock();
        }
    }

    /// <summary>
    /// El AudioSpecificConfig, que en Media Foundation viaja dentro de
    /// MF_MT_USER_DATA precedido por los doce bytes de HEAACWAVEINFO que sobran.
    /// Sin quitarlos, el decodificador del otro lado recibe basura delante de su
    /// configuracion y no arranca.
    /// </summary>
    private static byte[] LeerConfiguracion(IMFMediaType tipo)
    {
        try
        {
            var datos = tipo.GetBlob(MediaTypeAttributeKeys.UserData);

            return datos is { Length: > 12 } ? datos[12..] : datos ?? [];
        }
        catch (SharpGenException)
        {
            return [];
        }
    }

    /// <summary>
    /// Marshal.Copy necesita un byte[] gestionado y este repositorio no compila
    /// con /unsafe. Se reutiliza: esto ocurre decenas de veces por segundo.
    /// </summary>
    private byte[] Copia(ReadOnlySpan<byte> origen)
    {
        if (_copia.Length < origen.Length)
            _copia = new byte[origen.Length];

        origen.CopyTo(_copia);
        return _copia;
    }

    public void Dispose() => _transform.Dispose();
}
