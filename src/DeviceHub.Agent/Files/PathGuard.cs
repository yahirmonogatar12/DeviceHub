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
    /// Normaliza y resuelve enlaces.
    ///
    /// GetFullPath por si solo colapsa `..\` pero NO sigue junctions ni symlinks,
    /// y en Windows un directorio de usuario puede apuntar a C:\Windows. Sin
    /// resolver el destino real, la comprobacion se saltaria con un enlace.
    /// </summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        try
        {
            var target = Directory.Exists(full)
                ? new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                : new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName;

            if (!string.IsNullOrEmpty(target))
                full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            // Enlace roto o sin permisos para resolverlo: se sigue con la ruta
            // normalizada, que ya cerro el paso a `..\`.
        }

        return full;
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
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
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
