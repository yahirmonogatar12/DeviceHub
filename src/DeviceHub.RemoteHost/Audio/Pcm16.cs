namespace DeviceHub.RemoteHost.Audio;

/// <summary>
/// De lo que da Windows a lo que traga el codificador.
///
/// WASAPI entrega flotante de 32 bits -- es lo que mezcla el motor de audio --
/// y el codificador AAC de Media Foundation solo acepta PCM entero de 16 bits.
/// Entre los dos hay esta conversion y nada mas.
///
/// Aparte y pura para poder probarla: los errores aqui NO dan error. Un factor
/// de escala equivocado da sonido distorsionado, olvidar el recorte da
/// chasquidos justo en los picos -- que es donde esta la alarma que se quiere
/// oir -- y confundir el orden de los bytes da ruido blanco. Nada de eso lanza
/// una excepcion.
/// </summary>
public static class Pcm16
{
    /// <summary>
    /// Flotante de 32 bits a entero de 16, con recorte.
    ///
    /// EL RECORTE NO ES OPCIONAL. El flotante de WASAPI puede pasar de 1.0 --
    /// el motor de audio mezcla varias fuentes y no normaliza -- y multiplicar
    /// 1.2 por 32767 desborda un short: en complemento a dos eso no satura, da
    /// la vuelta, y un pico positivo sale como un pico NEGATIVO. Se oye como un
    /// chasquido fuerte exactamente en el momento mas alto del sonido.
    ///
    /// Se multiplica por 32767 y no por 32768: 1.0 tiene que caer en el maximo
    /// representable, no uno mas alla.
    /// </summary>
    public static int Convertir(ReadOnlySpan<byte> flotante, Span<byte> destino)
    {
        var muestras = flotante.Length / 4;

        if (destino.Length < muestras * 2)
            return 0;

        for (var i = 0; i < muestras; i++)
        {
            var valor = BitConverter.ToSingle(flotante.Slice(i * 4, 4));

            // NaN o infinito: un dispositivo roto o un driver que se equivoca.
            // Silencio es la respuesta segura; convertirlo da un valor
            // arbitrario que suena a golpe.
            if (float.IsNaN(valor) || float.IsInfinity(valor))
                valor = 0f;

            var entero = (short)Math.Clamp(valor * 32767f, short.MinValue, short.MaxValue);

            BitConverter.TryWriteBytes(destino.Slice(i * 2, 2), entero);
        }

        return muestras * 2;
    }

    /// <summary>
    /// Estereo a mono, promediando los dos canales.
    ///
    /// Existe porque el codificador cuesta la mitad con un canal y para oir una
    /// alarma de planta el estereo no aporta nada. Se PROMEDIA en vez de tomar
    /// el canal izquierdo: un sonido que solo suene a la derecha desapareceria
    /// entero, y es justo el caso de un aviso conectado a un altavoz.
    ///
    /// La suma se hace en int para que dos picos del mismo signo no desborden
    /// antes de dividirse.
    /// </summary>
    public static int AMono(ReadOnlySpan<byte> estereo, Span<byte> destino)
    {
        var fotogramas = estereo.Length / 4;   // 2 canales x 2 bytes

        if (destino.Length < fotogramas * 2)
            return 0;

        for (var i = 0; i < fotogramas; i++)
        {
            int izquierdo = BitConverter.ToInt16(estereo.Slice(i * 4, 2));
            int derecho = BitConverter.ToInt16(estereo.Slice(i * 4 + 2, 2));

            BitConverter.TryWriteBytes(destino.Slice(i * 2, 2), (short)((izquierdo + derecho) / 2));
        }

        return fotogramas * 2;
    }
}
