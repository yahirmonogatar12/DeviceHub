namespace DeviceHub.Agent.Files;

/// <summary>
/// Decide que rutas se pueden tocar en remoto.
///
/// Vive en el AGENTE y no en el servidor a proposito: `Environment.GetFolderPath`
/// en el servidor devolveria las carpetas DEL SERVIDOR. Solo la maquina sabe
/// donde estan sus propios directorios criticos, y en una instalacion no estandar
/// (Windows en D:, sistema en otro idioma) las rutas fijas no coinciden.
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Tope de transferencia en un solo mensaje. Cubre configuraciones, .ini,
    /// recortes de log -- que es el caso real de soporte remoto -- y evita montar
    /// una maquinaria de troceado, reensamblado y reanudacion para el dia que
    /// alguien quiera bajarse un log de 500 MB.
    /// </summary>
    public const int MaxTransferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Directorios criticos del sistema, resueltos EN ESTA maquina.
    ///
    /// Es una lista de denegacion, que en otros sitios del proyecto se evito
    /// (adaptadores de red, motores remotos). Aqui es inevitable: un gestor de
    /// archivos util tiene que poder recorrer discos enteros, asi que no cabe una
    /// lista de permitidos. Lo que si se hace es preguntarle a Windows donde
    /// estan esas carpetas en vez de escribirlas a mano.
    /// </summary>
    public static IReadOnlyList<string> ProtectedRoots { get; } = BuildProtectedRoots();

    private static List<string> BuildProtectedRoots()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonApplicationData
        };

        var roots = folders
            .Select(Environment.GetFolderPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
            .ToList();

        // El propio directorio de DeviceHub. Contiene el token de la maquina, y
        // borrarlo dejaria al equipo huerfano y sin forma remota de recuperarlo:
        // habria que ir fisicamente con un recovery code.
        roots.Add(Identity.MachineIdentity.DefaultDirectory.TrimEnd(Path.DirectorySeparatorChar));

        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Normaliza y resuelve enlaces EN CADA SEGMENTO de la ruta.
    ///
    /// GetFullPath por si solo colapsa `..\` pero NO sigue junctions ni symlinks,
    /// y en Windows un directorio de usuario puede apuntar a C:\Windows. Sin
    /// resolver el destino real, la comprobacion se saltaria con un enlace.
    ///
    /// Antes solo se miraba el ULTIMO componente. Con `C:\Datos\junc\config.ini`
    /// y `junc -> C:\Windows`, el archivo no es un enlace, asi que no se resolvia
    /// nada y la ruta se comparaba como `C:\Datos\...`: se podia escribir dentro
    /// de un arbol de solo lectura justo por el camino que este metodo decia
    /// cerrar.
    /// </summary>
    /// <exception cref="IOException">Demasiados enlaces encadenados -- un ciclo --,
    /// o un enlace que no se pudo leer.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso para leer un
    /// enlace del camino.</exception>
    /// <remarks>
    /// LANZA en vez de seguir con la ruta sin resolver, que es lo que hacia antes
    /// al fallar: un enlace que no se sabe a donde va es justo el caso en el que
    /// no se puede decir que la ruta este fuera de C:\Windows. TryValidate lo
    /// convierte en un rechazo.
    /// </remarks>
    public static string Normalize(string path)
    {
        var saltos = 0;
        return Recortar(ResolverEnlaces(Path.GetFullPath(path), ref saltos));
    }

    /// <summary>Lo bastante para cualquier instalacion real, y un tope para que
    /// un ciclo de enlaces no cuelgue al agente.</summary>
    private const int MaxSaltos = 32;

    private static string ResolverEnlaces(string full, ref int saltos)
    {
        var raiz = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(raiz))
            return full;

        var segmentos = full[raiz.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var actual = raiz;

        for (var i = 0; i < segmentos.Length; i++)
        {
            var siguiente = Path.Combine(actual, segmentos[i]);

            // Los ATRIBUTOS y no Directory.Exists: un enlace cuyo destino no
            // existe da false en Exists, y seguiria siendo un enlace.
            // GetAttributes mira el enlace en si, sin seguirlo.
            FileAttributes atributos;

            try
            {
                atributos = File.GetAttributes(siguiente);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Lo que no existe no puede ser un enlace, ni nada de lo que
                // cuelgue de ello: el resto se pega tal cual.
                return Path.Combine(actual, string.Join(Path.DirectorySeparatorChar, segmentos[i..]));
            }

            // Otros puntos de reanalisis -- archivos de OneDrive, deduplicacion --
            // no tienen LinkTarget y no llevan a ningun otro sitio.
            FileSystemInfo info = atributos.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(siguiente)
                : new FileInfo(siguiente);

            if (atributos.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null)
            {
                if (++saltos > MaxSaltos)
                    throw new IOException($"Demasiados enlaces encadenados al resolver {full}");

                var destino = info.ResolveLinkTarget(returnFinalTarget: true);

                // El destino puede a su vez pasar por otro enlace en un segmento
                // intermedio: se resuelve entero otra vez.
                if (destino is not null)
                    siguiente = ResolverEnlaces(Path.GetFullPath(destino.FullName), ref saltos);
            }

            actual = siguiente;
        }

        return actual;
    }

    /// <summary>
    /// Quita el separador final, SALVO en la raiz de una unidad.
    ///
    /// "C:\" recortado es "C:", y en Windows "C:" no es la raiz: es el directorio
    /// actual del proceso en esa unidad. El servicio corre en C:\Windows\System32,
    /// asi que listar "C:\" devolvia System32.
    /// </summary>
    private static string Recortar(string full)
    {
        var raiz = Path.GetPathRoot(full);

        return raiz is not null && full.Length <= raiz.Length
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// True si la ruta cae dentro de un directorio protegido.
    ///
    /// La comparacion exige limite de segmento: `C:\Windows` no debe bloquear
    /// `C:\WindowsApps`, que es un directorio distinto.
    /// </summary>
    public static bool IsProtected(string normalizedPath)
        => ProtectedRoots.Any(root =>
            normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Valida una ruta para lectura o para escritura.
    ///
    /// Los directorios protegidos son de SOLO LECTURA en remoto, para nadie.
    /// No es una cuestion de rol: borrar o sobrescribir dentro de C:\Windows a
    /// distancia es como se deja una PC de planta inservible, y ningun flujo de
    /// soporte legitimo lo necesita.
    /// </summary>
    public static bool TryValidate(string path, bool forWriting, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Ruta vacia";
            return false;
        }

        // Se comprueba sobre la ENTRADA, no sobre el resultado de Normalize:
        // GetFullPath resuelve las relativas contra el directorio de trabajo del
        // proceso -- que en un servicio de Windows es C:\Windows\System32 --, asi
        // que despues de normalizar toda ruta parece absoluta y la comprobacion
        // no serviria de nada.
        if (!Path.IsPathFullyQualified(path))
        {
            error = "Se requiere una ruta absoluta";
            return false;
        }

        try
        {
            normalized = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // IOException cubre PathTooLong y tambien el enlace que no se pudo
            // resolver: si no se sabe a donde apunta, no se toca.
            error = $"Ruta invalida: {ex.Message}";
            return false;
        }

        if (forWriting && IsProtected(normalized))
        {
            error = $"Ruta protegida, solo lectura: {normalized}";
            return false;
        }

        return true;
    }
}
