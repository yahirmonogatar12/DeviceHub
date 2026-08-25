using DeviceHub.RemoteHost.Audio;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// De flotante de 32 bits a PCM de 16, y de estereo a mono.
///
/// Se prueba porque aqui NADA falla ruidosamente. Un factor de escala
/// equivocado distorsiona; olvidar el recorte da un chasquido justo en el pico
/// -- que es donde esta la alarma que se quiere oir -- y promediar mal hace
/// desaparecer un sonido que solo suena por un lado. Todo eso se descubre
/// escuchando, mucho despues.
/// </summary>
public class Pcm16Tests
{
    private static byte[] Flotante(params float[] muestras)
    {
        var bytes = new byte[muestras.Length * 4];

        for (var i = 0; i < muestras.Length; i++)
            BitConverter.GetBytes(muestras[i]).CopyTo(bytes, i * 4);

        return bytes;
    }

    private static short[] Enteros(byte[] pcm, int bytes)
    {
        var salida = new short[bytes / 2];

        for (var i = 0; i < salida.Length; i++)
            salida[i] = BitConverter.ToInt16(pcm, i * 2);

        return salida;
    }

    [Fact]
    public void El_silencio_sigue_siendo_silencio()
    {
        var destino = new byte[8];
        var bytes = Pcm16.Convertir(Flotante(0f, 0f, 0f, 0f), destino);

        Assert.Equal(8, bytes);
        Assert.All(Enteros(destino, bytes), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Uno_coma_cero_cae_en_el_maximo_representable()
    {
        var destino = new byte[4];
        var bytes = Pcm16.Convertir(Flotante(1f, -1f), destino);

        var valores = Enteros(destino, bytes);

        Assert.Equal(32767, valores[0]);
        Assert.Equal(-32767, valores[1]);
    }

    /// <summary>
    /// EL RECORTE. El flotante de WASAPI puede pasar de 1.0 -- el motor mezcla
    /// varias fuentes y no normaliza -- y sin recortar, 1.2 x 32767 desborda un
    /// short: en complemento a dos no satura, DA LA VUELTA, y un pico positivo
    /// sale como un pico negativo. Se oye como un golpe seco en el momento mas
    /// alto del sonido.
    /// </summary>
    [Theory]
    [InlineData(1.5f, 32767)]
    [InlineData(3f, 32767)]
    [InlineData(100f, 32767)]
    [InlineData(-1.5f, -32768)]
    [InlineData(-50f, -32768)]
    public void Lo_que_pasa_de_uno_se_recorta_y_no_da_la_vuelta(float entrada, short esperado)
    {
        var destino = new byte[2];
        var bytes = Pcm16.Convertir(Flotante(entrada), destino);

        Assert.Equal(esperado, Enteros(destino, bytes)[0]);
    }

    /// <summary>NaN e infinito: un driver equivocado. Silencio es la respuesta
    /// segura; convertirlos da un valor arbitrario que suena a golpe.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Los_valores_imposibles_salen_como_silencio(float entrada)
    {
        var destino = new byte[2];
        var bytes = Pcm16.Convertir(Flotante(entrada), destino);

        Assert.Equal(0, Enteros(destino, bytes)[0]);
    }

    [Fact]
    public void No_escribe_si_el_destino_no_cabe()
    {
        var destino = new byte[2];   // hacen falta 4

        Assert.Equal(0, Pcm16.Convertir(Flotante(1f, 1f), destino));
    }

    /// <summary>
    /// El promedio de los dos canales, no el izquierdo. Un aviso conectado a un
    /// solo altavoz desapareceria entero tomando un canal, y ese es justo el
    /// caso de una alarma de planta.
    /// </summary>
    [Fact]
    public void Mono_promedia_los_dos_canales()
    {
        var estereo = new byte[8];
        BitConverter.GetBytes((short)1000).CopyTo(estereo, 0);   // izq
        BitConverter.GetBytes((short)2000).CopyTo(estereo, 2);   // der
        BitConverter.GetBytes((short)0).CopyTo(estereo, 4);
        BitConverter.GetBytes((short)500).CopyTo(estereo, 6);

        var destino = new byte[4];
        var bytes = Pcm16.AMono(estereo, destino);

        var valores = Enteros(destino, bytes);

        Assert.Equal(4, bytes);
        Assert.Equal(1500, valores[0]);
        Assert.Equal(250, valores[1]);
    }

    /// <summary>Un sonido que solo suena a la derecha NO desaparece.</summary>
    [Fact]
    public void Un_canal_mudo_no_borra_el_otro()
    {
        var estereo = new byte[4];
        BitConverter.GetBytes((short)0).CopyTo(estereo, 0);
        BitConverter.GetBytes((short)20000).CopyTo(estereo, 2);

        var destino = new byte[2];
        Pcm16.AMono(estereo, destino);

        Assert.Equal(10000, BitConverter.ToInt16(destino));
    }

    /// <summary>
    /// Dos picos del mismo signo no desbordan antes de dividirse. La suma se
    /// hace en int a proposito: en short, 32000 + 32000 da la vuelta y el pico
    /// mas alto sale como el mas bajo.
    /// </summary>
    [Fact]
    public void Dos_picos_altos_no_desbordan_al_sumarse()
    {
        var estereo = new byte[4];
        BitConverter.GetBytes((short)32000).CopyTo(estereo, 0);
        BitConverter.GetBytes((short)32000).CopyTo(estereo, 2);

        var destino = new byte[2];
        Pcm16.AMono(estereo, destino);

        Assert.Equal(32000, BitConverter.ToInt16(destino));
    }
}
