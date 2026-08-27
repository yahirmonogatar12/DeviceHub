using Xunit;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Encode;

namespace DeviceHub.Tests;

/// <summary>
/// El ciclo completo cuando el codificador se construye bien y no entrega nada.
///
/// Se escribe entero como recorrido -- no peldano a peldano -- porque el fallo
/// que costo la tarde no estaba en ningun paso suelto: estaba en que el tercero
/// no se alcanzaba. La barra decia "se pasa al codificador por SOFTWARE" y
/// seguia poniendo HW Intel Quick Sync.
/// </summary>
public class EscaleraCodecTests
{
    [Fact]
    public void El_ciclo_entero_es_H265_luego_H264_luego_software()
    {
        // 1. Se empieza en H.265 por hardware, que es lo de fabrica.
        var codec = VideoCodec.H265;
        var software = false;

        // 2. No entrega -> H.264, todavia por hardware.
        var paso = EscaleraCodec.Siguiente(codec, software);

        Assert.NotNull(paso);
        Assert.Equal(VideoCodec.H264, paso!.Value.Codec);
        Assert.False(paso.Value.SoloSoftware);

        (codec, software) = (paso.Value.Codec, paso.Value.SoloSoftware);

        // 3. Tampoco entrega -> por software. ESTE es el peldano que faltaba.
        paso = EscaleraCodec.Siguiente(codec, software);

        Assert.NotNull(paso);
        Assert.Equal(VideoCodec.H264, paso!.Value.Codec);
        Assert.True(paso.Value.SoloSoftware);

        (codec, software) = (paso.Value.Codec, paso.Value.SoloSoftware);

        // 4. Y ahi se acaba: el software funciona en cualquier maquina, asi que
        //    si tampoco entrega, el problema no es el codificador. Seguir
        //    bajando seria un bucle sin fondo.
        Assert.Null(EscaleraCodec.Siguiente(codec, software));
    }

    [Fact]
    public void Empezando_en_H264_se_salta_directo_al_software()
    {
        // Una PC con DeviceHub:Codec = "h264" en su appsettings no tiene un
        // peldano de H.265 que bajar.
        var paso = EscaleraCodec.Siguiente(VideoCodec.H264, yaEsSoftware: false);

        Assert.NotNull(paso);
        Assert.True(paso!.Value.SoloSoftware);
    }

    [Fact]
    public void Del_software_no_se_baja_nunca()
    {
        Assert.Null(EscaleraCodec.Siguiente(VideoCodec.H264, yaEsSoftware: true));
        Assert.Null(EscaleraCodec.Siguiente(VideoCodec.H265, yaEsSoftware: true));
    }

    [Fact]
    public void Cada_paso_dice_QUE_paso_y_A_QUE_se_pasa()
    {
        // El tecnico ve esto en la barra y es lo unico que le explica por que la
        // sesion tardo en abrir. Un paso mudo es un paso que parece un cuelgue.
        foreach (var (codec, software) in new[]
                 {
                     (VideoCodec.H265, false),
                     (VideoCodec.H264, false)
                 })
        {
            var paso = EscaleraCodec.Siguiente(codec, software);

            Assert.NotNull(paso);
            Assert.NotEmpty(paso!.Value.Aviso);
        }
    }

    [Fact]
    public void No_se_baja_al_primer_tropiezo()
    {
        // Uno puede ser un codificador frio que no llego a tiempo; dos seguidos
        // ya no es mala suerte. Bajar al primero condenaria a codificar por CPU
        // a una PC cuya GPU solo iba lenta arrancando.
        Assert.True(EscaleraCodec.RehechosAntesDeBajar >= 2);
    }
}
