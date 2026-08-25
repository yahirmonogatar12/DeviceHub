using DeviceHub.RemoteHost.Audio;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Lectura de WAVEFORMATEX, que es aritmetica de bytes sobre memoria que da
/// Windows.
///
/// Se prueba porque los errores aqui NO dan error: un desplazamiento mal puesto
/// da 24000 Hz en vez de 48000, y eso no lanza nada -- se oye como sonido a la
/// mitad de velocidad, mucho despues y en otro sitio. Lo mismo con confundir
/// flotante y entero: los mismos bytes interpretados al reves suenan a ruido
/// blanco.
/// </summary>
public class WasapiFormatoTests
{
    /// <summary>Un WAVEFORMATEX en memoria, como lo devuelve GetMixFormat.</summary>
    private static IntPtr Escribir(
        ushort etiqueta, ushort canales, int hz, ushort bloque, ushort bits, int subformato = 0)
    {
        var bytes = new byte[40];   // cabecera 18 + espacio del EXTENSIBLE

        BitConverter.GetBytes(etiqueta).CopyTo(bytes, 0);
        BitConverter.GetBytes(canales).CopyTo(bytes, 2);
        BitConverter.GetBytes(hz).CopyTo(bytes, 4);
        BitConverter.GetBytes(hz * bloque).CopyTo(bytes, 8);
        BitConverter.GetBytes(bloque).CopyTo(bytes, 12);
        BitConverter.GetBytes(bits).CopyTo(bytes, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(bytes, 16);
        BitConverter.GetBytes(subformato).CopyTo(bytes, 24);

        var memoria = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, memoria, bytes.Length);

        return memoria;
    }

    private static void Con(IntPtr memoria, Action<Wasapi.Formato> comprobar)
    {
        try
        {
            comprobar(Wasapi.LeerFormato(memoria));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(memoria);
        }
    }

    /// <summary>Lo que devuelve casi cualquier Windows moderno: 48 kHz estereo
    /// flotante, declarado como EXTENSIBLE.</summary>
    [Fact]
    public void Extensible_flotante_de_48k()
        => Con(Escribir(0xFFFE, 2, 48000, 8, 32, subformato: 3), f =>
        {
            Assert.Equal(48000, f.Hz);
            Assert.Equal(2, f.Canales);
            Assert.Equal(32, f.BitsPorMuestra);
            Assert.Equal(8, f.BytesPorFotograma);
            Assert.True(f.EsFlotante);
        });

    /// <summary>El mismo EXTENSIBLE pero con subformato PCM. Los mismos bytes,
    /// y confundirlo suena a ruido blanco.</summary>
    [Fact]
    public void Extensible_entero_no_es_flotante()
        => Con(Escribir(0xFFFE, 2, 48000, 4, 16, subformato: 1), f =>
        {
            Assert.False(f.EsFlotante);
            Assert.Equal(16, f.BitsPorMuestra);
            Assert.Equal(4, f.BytesPorFotograma);
        });

    [Fact]
    public void Flotante_declarado_directamente()
        => Con(Escribir(3, 2, 44100, 8, 32), f =>
        {
            Assert.True(f.EsFlotante);
            Assert.Equal(44100, f.Hz);
        });

    [Fact]
    public void Pcm_clasico_de_16_bits()
        => Con(Escribir(1, 1, 16000, 2, 16), f =>
        {
            Assert.False(f.EsFlotante);
            Assert.Equal(1, f.Canales);
            Assert.Equal(16000, f.Hz);
            Assert.Equal(2, f.BytesPorFotograma);
        });

    /// <summary>
    /// Mono y 8 canales. El tamaño de fotograma es lo que multiplica todo lo
    /// demas: si sale mal, la cuenta de "cuantos bytes son estos fotogramas"
    /// sale mal en cada paquete.
    /// </summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(6, 24)]
    [InlineData(8, 32)]
    public void El_tamano_de_fotograma_se_lee_tal_cual(ushort canales, ushort bloque)
        => Con(Escribir(0xFFFE, canales, 48000, bloque, 32, subformato: 3), f =>
        {
            Assert.Equal(canales, f.Canales);
            Assert.Equal(bloque, f.BytesPorFotograma);
        });
}
