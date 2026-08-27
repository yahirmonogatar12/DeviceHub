using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Que probar cuando el codificador que hay puesto NO entrega imagen.
///
/// La escalera de la APERTURA baja cuando un codificador falla al construirse.
/// Esta es la otra, la que hace falta cuando se construye bien y luego no
/// entrega nada -- los MFT mienten: aceptan toda la configuracion, devuelven
/// exito, y no sueltan un frame.
///
/// Clase suelta y sin estado a proposito, como Escalado o Cuadricula: son tres
/// peldanos y una decision, y asi se prueban sin GPU, sin MFT y sin sesion. Que
/// es todo lo que de esto se puede comprobar en CI -- el resto solo lo dice el
/// hardware de una PC de planta.
/// </summary>
public static class EscaleraCodec
{
    /// <summary>A que se pasa, y que se le dice al tecnico.</summary>
    public readonly record struct Paso(VideoCodec Codec, bool SoloSoftware, string Aviso);

    /// <summary>
    /// El siguiente peldano, o null si ya no queda nada que probar.
    ///
    /// El orden no es capricho:
    ///
    ///   1. H.265 por hardware   menos ancho de banda, y va bien donde va
    ///   2. H.264 por hardware   sigue siendo la GPU, sigue sin costar CPU
    ///   3. H.264 por software   lento, pero funciona en cualquier maquina
    ///
    /// Se baja de uno en uno y solo cuando el de arriba ya demostro que no
    /// entrega. Saltar directo al software por si acaso condenaria a todas las
    /// PCs a codificar por CPU por lo que le pasa a una.
    /// </summary>
    public static Paso? Siguiente(VideoCodec actual, bool yaEsSoftware)
    {
        // No queda peldano: el software es el ultimo, y funciona en cualquier
        // maquina. Si tampoco entrega, el problema no es el codificador.
        if (yaEsSoftware)
            return null;

        if (actual == VideoCodec.H265)
        {
            return new Paso(
                VideoCodec.H264, false,
                "El H.265 de esta PC acepta la configuracion y no entrega imagen; se pasa a H.264.");
        }

        return new Paso(
            VideoCodec.H264, true,
            "Tampoco el H.264 por hardware entrega imagen en esta PC; " +
            "se pasa al codificador por SOFTWARE.");
    }

    /// <summary>
    /// Rehechos seguidos sin una sola salida antes de bajar un peldano.
    ///
    /// Dos, no uno: el primero puede ser un codificador frio que no llego a
    /// tiempo. Dos seguidos ya no es mala suerte.
    /// </summary>
    public const int RehechosAntesDeBajar = 2;
}
