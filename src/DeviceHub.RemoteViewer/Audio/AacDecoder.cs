using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteViewer.Audio;

/// <summary>Lo que no se pudo montar y por que.</summary>
public sealed class AacDecoderNoDisponibleException(string mensaje, Exception? interna = null)
    : Exception(mensaje, interna);

/// <summary>
/// De AAC a PCM de 16 bits, por Media Foundation.
///
/// El espejo del codificador del host, y con la misma leccion aprendida a
/// golpes ahi: los tipos NO se construyen a mano. Se enumeran los que el
/// decodificador ofrece y se usa el que cuadra. Construirlos daba
/// MF_E_INVALIDMEDIATYPE aunque los valores coincidieran exactamente, porque un
/// tipo de Media Foundation lleva mas atributos de los que se ven.
///
/// La entrada SI se construye, porque lleva el AudioSpecificConfig que viene
/// del otro lado y eso no lo puede enumerar nadie.
/// </summary>
public sealed class AacDecoder : IDisposable
{
    private const uint TransformNeedMoreInput = 0xC00D6D72;

    private readonly IMFTransform _transform;
    private byte[] _copia = [];

    public int Hz { get; }
    public int Canales { get; }

    public long Entradas { get; private set; }
    public long Salidas { get; private set; }

    public AacDecoder(int hz, int canales, byte[] configuracion)
    {
        Hz = hz;
        Canales = canales;

        _transform = Activar()
            ?? throw new AacDecoderNoDisponibleException(
                "Windows no expone ningun descodificador AAC en esta PC.");

        try
        {
            // LA ENTRADA, con el AudioSpecificConfig del host dentro.
            //
            // MF lo espera precedido de los doce bytes de HEAACWAVEINFO que el
            // codificador quito al mandarlo. Se reponen: sin ellos el
            // descodificador lee la configuracion desplazada y saca ruido, no
            // un error.
            using var entrada = MediaFactory.MFCreateMediaType();
            entrada.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            entrada.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            entrada.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, hz);
            entrada.Set(MediaTypeAttributeKeys.AudioNumChannels, canales);
            entrada.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
            entrada.Set(MediaTypeAttributeKeys.AacPayloadType, 0);
            entrada.SetBlob(MediaTypeAttributeKeys.UserData, ConCabecera(configuracion));

            _transform.SetInputType(0, entrada, 0);

            using var salida = SalidaQueCuadre(_transform, hz, canales)
                ?? throw new AacDecoderNoDisponibleException(
                    $"El descodificador no ofrece PCM de {hz} Hz con {canales} canal(es).");

            _transform.SetOutputType(0, salida, 0);
            _transform.ProcessMessage((TMessageType)(0x10000000 | 1), UIntPtr.Zero);
        }
        catch (AacDecoderNoDisponibleException)
        {
            _transform.Dispose();
            throw;
        }
        catch (SharpGenException ex)
        {
            _transform.Dispose();

            throw new AacDecoderNoDisponibleException(
                $"El descodificador AAC no acepto {hz} Hz, {canales} canales: {ex.ResultCode}", ex);
        }
    }

    /// <summary>Mete un paquete AAC y devuelve el PCM que salga.</summary>
    public IReadOnlyList<byte[]> Descodificar(byte[] aac, long timestampUs)
    {
        var salidas = new List<byte[]>(1);

        if (aac.Length == 0)
            return salidas;

        using (var muestra = MediaFactory.MFCreateSample())
        {
            using var bufer = MediaFactory.MFCreateMemoryBuffer(aac.Length);

            bufer.Lock(out var destino, out _, out _);

            try
            {
                Marshal.Copy(aac, 0, destino, aac.Length);
            }
            finally
            {
                bufer.Unlock();
            }

            bufer.CurrentLength = aac.Length;

            muestra.AddBuffer(bufer);
            muestra.SampleTime = timestampUs * 10;

            try
            {
                _transform.ProcessInput(0, muestra, 0);
                Entradas++;
            }
            catch (SharpGenException)
            {
                // Un paquete que no entra se pierde y se oye como un chasquido.
                // No es motivo para tirar el descodificador: el siguiente
                // probablemente si entre.
                return salidas;
            }
        }

        Vaciar(salidas);
        return salidas;
    }

    private void Vaciar(List<byte[]> salidas)
    {
        var info = _transform.GetOutputStreamInfo(0);
        var proveeMuestras = ((uint)info.Flags & 0x100) != 0;

        while (true)
        {
            var buffers = new OutputDataBuffer[1];
            buffers[0].StreamID = 0;

            IMFSample? propia = null;

            if (!proveeMuestras)
            {
                propia = MediaFactory.MFCreateSample();

                var bufer = MediaFactory.MFCreateMemoryBuffer((int)Math.Max(info.Size, 8192));
                propia.AddBuffer(bufer);
                bufer.Dispose();

                buffers[0].Sample = propia;
            }

            var resultado = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out _);

            if (resultado.Failure)
            {
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
    /// El AudioSpecificConfig con los doce bytes de HEAACWAVEINFO delante, que
    /// es como MF lo espera. El codificador los quita al mandarlo porque no
    /// aportan nada al otro lado; aqui hay que reponerlos.
    ///
    /// wPayloadType(2) wAudioProfileLevelIndication(2) wStructType(2)
    /// wReserved1(2) dwReserved2(4) -- ceros salvo el perfil, y ni ese lo mira
    /// el descodificador cuando el AudioSpecificConfig esta presente.
    /// </summary>
    private static byte[] ConCabecera(byte[] configuracion)
    {
        var datos = new byte[12 + configuracion.Length];
        configuracion.CopyTo(datos, 12);

        return datos;
    }

    private static IMFMediaType? SalidaQueCuadre(IMFTransform transform, int hz, int canales)
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
                return null;
            }

            try
            {
                if (tipo.GetGUID(MediaTypeAttributeKeys.Subtype) == AudioFormatGuids.Pcm
                    && tipo.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond) == hz
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

    private static IMFTransform? Activar()
    {
        var entrada = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Audio,
            GuidSubtype = AudioFormatGuids.Aac
        };

        using var coleccion = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.AudioDecoder, 0, entrada, null);

        foreach (var activate in coleccion)
        {
            try
            {
                return activate.ActivateObject<IMFTransform>();
            }
            catch (Exception)
            {
            }
            finally
            {
                activate.Dispose();
            }
        }

        return null;
    }

    public void Dispose() => _transform.Dispose();
}
