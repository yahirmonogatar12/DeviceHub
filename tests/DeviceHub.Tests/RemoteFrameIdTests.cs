using DeviceHub.Remote.Contracts;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// La regla de "frame atrasado" del reensamblador, y lo que cuesta cuando el
/// emisor reinicia su numeracion.
///
/// Es la causa de la congelacion al cambiar de pantalla: cada captura nueva
/// estrenaba contador y volvia a empezar en 1, asi que todos los frames del
/// flujo nuevo entraban por esta puerta y se iban a la basura. Sin un solo
/// error, y con el contador de tardios subiendo donde nadie lo miraba.
/// </summary>
public class RemoteFrameIdTests
{
    private static VideoFrameChunks Frame(ulong id) =>
        VideoFraming.Split(id, keyFrame: true, configVersion: 1, captureTimestampUs: 0, payload: [1, 2, 3]);

    private static bool Entregar(VideoFrameAssembler montador, ulong id)
    {
        var entregado = false;

        foreach (var trozo in Frame(id).Chunks)
        {
            if (montador.TryAdd(trozo, out _))
                entregado = true;
        }

        return entregado;
    }

    [Fact]
    public void Los_frames_en_orden_se_entregan()
    {
        var montador = new VideoFrameAssembler();

        Assert.True(Entregar(montador, 1));
        Assert.True(Entregar(montador, 2));
        Assert.True(Entregar(montador, 3));
    }

    /// <summary>
    /// El comportamiento que causaba la congelacion, fijado a proposito: un
    /// emisor que reinicia la numeracion queda MUDO para el reensamblador.
    ///
    /// La regla es correcta -- un frame viejo de verdad corromperia el actual --
    /// asi que lo que no puede pasar es que el emisor retroceda. Por eso el host
    /// numera por SESION y no por captura.
    /// </summary>
    [Fact]
    public void Reiniciar_la_numeracion_deja_al_reensamblador_mudo()
    {
        var montador = new VideoFrameAssembler();

        Entregar(montador, 100);
        Entregar(montador, 101);

        Assert.False(Entregar(montador, 1));
        Assert.False(Entregar(montador, 2));
        Assert.True(montador.Stale > 0);
    }

    /// <summary>Y la salida: un reensamblador nuevo por flujo nuevo. Es lo que
    /// hace el visor al llegar una config distinta.</summary>
    [Fact]
    public void Un_reensamblador_nuevo_acepta_la_numeracion_nueva()
    {
        var montador = new VideoFrameAssembler();

        Entregar(montador, 100);

        montador = new VideoFrameAssembler();

        Assert.True(Entregar(montador, 1));
    }
}
