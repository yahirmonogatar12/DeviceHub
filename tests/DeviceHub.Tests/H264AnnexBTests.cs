using DeviceHub.Remote.Contracts;
using Xunit;

namespace DeviceHub.Tests;

public class H264AnnexBTests
{
    /// <summary>
    /// Un NAL con prefijo de 4 bytes. `primera` controla first_mb_in_slice: el
    /// bit alto a 1 significa cero, o sea que la rebanada abre imagen.
    /// </summary>
    private static byte[] Nal(int tipo, bool primera = true, int relleno = 3)
    {
        var bytes = new List<byte> { 0, 0, 0, 1, (byte)(0x60 | tipo) };

        // Cabecera de rebanada: 0x80 => first_mb_in_slice = 0.
        bytes.Add(primera ? (byte)0x80 : (byte)0x0C);

        for (var i = 0; i < relleno; i++)
            bytes.Add(0x42);

        return [.. bytes];
    }

    private static byte[] Unir(params byte[][] partes) => [.. partes.SelectMany(p => p)];

    [Fact]
    public void Groups_parameter_sets_with_the_frame_that_follows_them()
    {
        var flujo = Unir(Nal(7), Nal(8), Nal(5), Nal(1));

        var unidades = H264AnnexB.Split(flujo);

        Assert.Equal(2, unidades.Count);
        Assert.Equal(0, unidades[0].Offset);
        Assert.True(unidades[0].KeyFrame);
        Assert.False(unidades[1].KeyFrame);
    }

    [Fact]
    public void A_picture_split_into_several_slices_stays_one_access_unit()
    {
        // Es el caso que rompe la version ingenua: tres rebanadas de la MISMA
        // imagen se convertirian en tres unidades, y el decodificador recibiria
        // imagenes a medias.
        var flujo = Unir(Nal(5), Nal(1, primera: false), Nal(1, primera: false));

        var unidades = H264AnnexB.Split(flujo);

        Assert.Single(unidades);
        Assert.Equal(flujo.Length, unidades[0].Length);
    }

    [Fact]
    public void Covers_the_whole_stream_without_gaps_or_overlap()
    {
        var flujo = Unir(Nal(7), Nal(8), Nal(5), Nal(1), Nal(1));

        var unidades = H264AnnexB.Split(flujo);

        Assert.Equal(0, unidades[0].Offset);
        Assert.Equal(flujo.Length, unidades[^1].Offset + unidades[^1].Length);

        for (var i = 1; i < unidades.Count; i++)
            Assert.Equal(unidades[i - 1].Offset + unidades[i - 1].Length, unidades[i].Offset);
    }

    [Fact]
    public void Accepts_three_byte_start_codes()
    {
        // Prefijo corto: los codificadores mezclan 00 00 01 y 00 00 00 01 en el
        // mismo archivo.
        var flujo = Unir([0, 0, 1, 0x65, 0x80, 0x42], [0, 0, 1, 0x61, 0x80, 0x42]);

        var unidades = H264AnnexB.Split(flujo);

        Assert.Equal(2, unidades.Count);
        Assert.True(unidades[0].KeyFrame);
    }

    [Fact]
    public void Ignores_a_trailing_header_with_no_picture()
    {
        // SPS+PPS sueltos al final: no son reproducibles y no forman unidad.
        var flujo = Unir(Nal(5), Nal(7), Nal(8));

        var unidades = H264AnnexB.Split(flujo);

        Assert.Single(unidades);
    }

    [Fact]
    public void Empty_and_garbage_input_produce_nothing()
    {
        Assert.Empty(H264AnnexB.Split([]));
        Assert.Empty(H264AnnexB.Split([0x42, 0x42, 0x42, 0x42]));
    }
}
