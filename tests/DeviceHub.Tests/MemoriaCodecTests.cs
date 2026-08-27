using Xunit;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Encode;

namespace DeviceHub.Tests;

/// <summary>
/// La nota vale un rato, no para siempre. Es lo unico de MemoriaCodec que se
/// puede comprobar sin disco -- y es justo la parte donde equivocarse cuesta
/// caro: una nota eterna condena a una PC de planta a codificar por CPU aunque
/// lo que fallara fuera el monitor apagado, no la GPU.
/// </summary>
public class MemoriaCodecTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Una_nota_recien_escrita_vale()
    {
        var nota = new MemoriaCodec.Nota(VideoCodec.H264, true, Ahora);

        Assert.True(MemoriaCodec.Vale(nota, Ahora));
        Assert.True(MemoriaCodec.Vale(nota, Ahora + TimeSpan.FromHours(6)));
    }

    [Fact]
    public void Pasada_la_caducidad_se_vuelve_a_probar_desde_arriba()
    {
        var nota = new MemoriaCodec.Nota(VideoCodec.H264, true, Ahora);

        Assert.False(MemoriaCodec.Vale(nota, Ahora + MemoriaCodec.Caducidad));
        Assert.False(MemoriaCodec.Vale(nota, Ahora + TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Una_nota_del_futuro_no_vale()
    {
        // El reloj de una PC de planta se va. Una nota fechada por delante
        // duraria mas que la caducidad sin que nadie lo pidiera.
        var nota = new MemoriaCodec.Nota(VideoCodec.H264, true, Ahora + TimeSpan.FromDays(1));

        Assert.False(MemoriaCodec.Vale(nota, Ahora));
    }

    [Fact]
    public void La_caducidad_no_es_eterna()
    {
        Assert.True(MemoriaCodec.Caducidad > TimeSpan.Zero);
        Assert.True(MemoriaCodec.Caducidad <= TimeSpan.FromDays(30));
    }
}
