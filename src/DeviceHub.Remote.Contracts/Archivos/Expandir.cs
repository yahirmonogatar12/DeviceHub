using System.IO;
using System.Collections.Generic;

// AQUI Y NO EN CADA LADO. La aritmetica de rutas y el recorrido son identicos
// en los dos sentidos -- traer una carpeta de alla y llevarla de aqui -- y dos
// copias serian dos formas distintas de equivocarse.
//
// Este proyecto es "solo el contrato", y esto no lo rompe: no hay captura ni
// interfaz. Es el unico ensamblado que el host y el visor comparten, y
// enlazarlo como archivo suelto lo compilaba en los dos, con lo que el mismo
// tipo existia por duplicado y no habia forma de probarlo.
namespace DeviceHub.Archivos;

/// <summary>
/// Convertir lo que el tecnico selecciono en la lista de archivos que hay que
/// mover, cada uno con su sitio dentro del arbol.
///
/// Las carpetas NO necesitan protocolo propio. La transferencia de la Fase 24
/// mueve archivos con reanudacion, y una carpeta es un conjunto de archivos mas
/// la forma en que estan colocados: si cada archivo viaja con su ruta RELATIVA,
/// el otro lado reconstruye el arbol creando directorios sobre la marcha -- que
/// es algo que IniciarBajada ya hacia para poder escribir en subcarpetas.
///
/// Inventar mensajes de "crear carpeta" habria sido un protocolo nuevo con sus
/// propios estados y sus propios fallos a mitad de camino, a cambio de nada.
/// </summary>
public static class Expandir
{
    /// <summary>Un archivo a mover: donde esta, donde va, y cuanto ocupa.</summary>
    public readonly record struct Pieza(string Ruta, string Relativa, ulong Tamano);

    /// <summary>
    /// Donde cae <paramref name="archivo"/> dentro del arbol que cuelga de
    /// <paramref name="raiz"/>.
    ///
    /// La raiz cuenta CON su nombre: al copiar la carpeta "Planos", lo que se
    /// pega es "Planos", no su contenido suelto. Por eso lo relativo se calcula
    /// desde el PADRE de la raiz.
    ///
    ///     raiz    C:\trabajo\Planos
    ///     archivo C:\trabajo\Planos\2026\a.dwg
    ///     ->      Planos\2026\a.dwg
    ///
    /// Y un archivo suelto es su propio nombre, sin carpeta ninguna.
    /// </summary>
    public static string Relativa(string raiz, string archivo)
    {
        var padre = Path.GetDirectoryName(raiz.TrimEnd(Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(padre))
            return Path.GetFileName(archivo);

        var relativa = Path.GetRelativePath(padre, archivo);

        // Fuera del arbol: no se inventa un sitio, se deja plano. Pasa con
        // rutas de red o unidades distintas mezcladas en una seleccion.
        return relativa.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativa)
            ? Path.GetFileName(archivo)
            : relativa;
    }

    /// <summary>
    /// Todo lo que hay que mover para llevarse <paramref name="raices"/>.
    ///
    /// Una carpeta vacia no produce piezas y por tanto no viaja. Es una perdida
    /// consciente: mandar una carpeta vacia obligaria a un mensaje de "crea esto
    /// aunque no lleve nada", que es justo el protocolo que se evita. Se anota
    /// aqui para que quien lo note sepa que no es un olvido.
    /// </summary>
    public static List<Pieza> Todo(IEnumerable<string> raices)
    {
        var piezas = new List<Pieza>();

        foreach (var raiz in raices)
        {
            try
            {
                if (File.Exists(raiz))
                {
                    piezas.Add(new Pieza(
                        raiz, Path.GetFileName(raiz), (ulong)new FileInfo(raiz).Length));

                    continue;
                }

                if (!Directory.Exists(raiz))
                    continue;

                foreach (var archivo in Directory.EnumerateFiles(raiz, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        piezas.Add(new Pieza(
                            archivo, Relativa(raiz, archivo), (ulong)new FileInfo(archivo).Length));
                    }
                    catch (Exception)
                    {
                        // Un archivo que no se deja leer no tumba la carpeta
                        // entera: se queda fuera y el resto viaja.
                    }
                }
            }
            catch (Exception)
            {
                // Ni una raiz ilegible. Lo mismo: lo que se pueda.
            }
        }

        return piezas;
    }
}
