using System.Text.Json;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// El peldano en el que esta PC acabo la ultima vez que la escalera tuvo que bajar.
///
/// RemoteHost se lanza NUEVO en cada sesion, asi que el estado de EscaleraCodec
/// -- que es estatico -- muere con el proceso. Sin esto, una PC cuyo H.265 y
/// cuyo H.264 por hardware no entregan vuelve a descubrirlo desde cero cada vez
/// que se abre: dos rehechos por peldano, dos peldanos, y el tecnico esperando.
/// Eso es lo que se sintio como "ya cargo pero se tardo".
///
/// Solo se anota cuando la escalera BAJA. Si el codec de fabrica funciona no hay
/// nada que recordar: el de fabrica ya es lo que se habria elegido, y el archivo
/// solo existe en las PCs que dieron problema.
/// </summary>
public static class MemoriaCodec
{
    public sealed record Nota(VideoCodec Codec, bool SoloSoftware, DateTimeOffset Cuando);

    /// <summary>
    /// Cuanto vale una nota antes de volver a probar desde arriba.
    ///
    /// No es para siempre a proposito. El fallo que la escrita puede ser del
    /// ENTORNO y no de la maquina -- una pantalla apagada deja a Quick Sync sin
    /// salida que codificar, y con el monitor encendido vuelve a ir. Recordarlo
    /// eternamente condenaria esa PC a codificar por CPU para siempre, que en
    /// una PC de planta corriendo el MES se paga en produccion.
    /// </summary>
    public static readonly TimeSpan Caducidad = TimeSpan.FromDays(7);

    /// <summary>Si la nota sigue valiendo. Aritmetica pura, se prueba sin disco.</summary>
    public static bool Vale(Nota nota, DateTimeOffset ahora)
        => ahora - nota.Cuando < Caducidad && ahora >= nota.Cuando;

    private static string Ruta =>
        Path.Combine(@"C:\ProgramData\ILSANSYSTEM\DeviceHub", "codec.json");

    /// <summary>El peldano recordado, o null si no hay o ya caduco.</summary>
    public static Nota? Leer()
    {
        try
        {
            if (!File.Exists(Ruta))
                return null;

            var nota = JsonSerializer.Deserialize<Nota>(File.ReadAllText(Ruta));

            return nota is not null && Vale(nota, DateTimeOffset.UtcNow) ? nota : null;
        }
        catch
        {
            // ponytail: una nota ilegible es una nota que no hay. Perder la
            // memoria cuesta unos segundos de escalera; tirar la sesion por un
            // json corrupto cuesta la sesion.
            return null;
        }
    }

    public static void Anotar(VideoCodec codec, bool soloSoftware)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);

            File.WriteAllText(Ruta, JsonSerializer.Serialize(
                new Nota(codec, soloSoftware, DateTimeOffset.UtcNow)));
        }
        catch
        {
            // Igual que arriba: esto es un atajo para la proxima vez, no un
            // requisito de esta sesion.
        }
    }
}
