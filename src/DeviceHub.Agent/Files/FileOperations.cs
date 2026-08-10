using System.Text.Json;

namespace DeviceHub.Agent.Files;

public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long SizeBytes, DateTime ModifiedUtc);

/// <summary>
/// Operaciones de archivo permitidas en remoto (Fase 14).
///
/// Toda ruta pasa por <see cref="PathGuard"/> ANTES de tocar el disco. Ninguna
/// de estas funciones acepta una ruta sin normalizar.
/// </summary>
public static class FileOperations
{
    public static string ListDirectory(string path)
    {
        // Listar SI se permite en rutas protegidas: ver que hay en C:\Windows es
        // legitimo para diagnosticar. Lo que no se permite es modificarlo.
        if (!PathGuard.TryValidate(path, forWriting: false, out var full, out var error))
            throw new ArgumentException(error);

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"No existe el directorio: {full}");

        var entries = new List<FileEntry>();

        foreach (var directory in Directory.EnumerateDirectories(full))
            entries.Add(Describe(directory, isDirectory: true));

        foreach (var file in Directory.EnumerateFiles(full))
            entries.Add(Describe(file, isDirectory: false));

        return JsonSerializer.Serialize(entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static FileEntry Describe(string path, bool isDirectory)
    {
        try
        {
            var info = new FileInfo(path);

            return new FileEntry(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                path,
                isDirectory,
                isDirectory ? 0 : info.Length,
                info.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            // Archivo bloqueado o sin permisos: se lista igual, sin metadatos.
            return new FileEntry(Path.GetFileName(path), path, isDirectory, 0, DateTime.UnixEpoch);
        }
    }

    public static string CreateDirectory(string path)
    {
        if (!PathGuard.TryValidate(path, forWriting: true, out var full, out var error))
            throw new ArgumentException(error);

        Directory.CreateDirectory(full);
        return $"Creado: {full}";
    }

    /// <summary>
    /// Borra un archivo o un directorio VACIO.
    ///
    /// El borrado recursivo no se ofrece a proposito: un `path` mal escrito
    /// borraria un arbol entero de una PC de produccion sin papelera ni deshacer.
    /// Vaciar un directorio exige borrar su contenido, que es visible y auditable
    /// paso a paso.
    /// </summary>
    public static string Delete(string path)
    {
        if (!PathGuard.TryValidate(path, forWriting: true, out var full, out var error))
            throw new ArgumentException(error);

        if (Directory.Exists(full))
        {
            if (Directory.EnumerateFileSystemEntries(full).Any())
                throw new IOException("El directorio no esta vacio; no se permite borrado recursivo en remoto");

            Directory.Delete(full);
            return $"Directorio borrado: {full}";
        }

        if (!File.Exists(full))
            throw new FileNotFoundException($"No existe: {full}");

        File.Delete(full);
        return $"Archivo borrado: {full}";
    }

    /// <summary>Renombra dentro del mismo directorio. Mover entre carpetas exigiria
    /// validar dos rutas y no aporta nada que no cubra copiar y borrar.</summary>
    public static string Rename(string path, string newName)
    {
        if (!PathGuard.TryValidate(path, forWriting: true, out var full, out var error))
            throw new ArgumentException(error);

        if (string.IsNullOrWhiteSpace(newName) || newName.Contains(Path.DirectorySeparatorChar)
            || newName.Contains(Path.AltDirectorySeparatorChar) || newName.Contains(':'))
        {
            throw new ArgumentException("El nombre nuevo no puede incluir ruta");
        }

        var target = Path.Combine(Path.GetDirectoryName(full)!, newName);

        if (!PathGuard.TryValidate(target, forWriting: true, out var fullTarget, out var targetError))
            throw new ArgumentException(targetError);

        if (Directory.Exists(full))
            Directory.Move(full, fullTarget);
        else
            File.Move(full, fullTarget, overwrite: false);

        return $"Renombrado a: {fullTarget}";
    }

    public static string ReadFile(string path)
    {
        if (!PathGuard.TryValidate(path, forWriting: false, out var full, out var error))
            throw new ArgumentException(error);

        if (!File.Exists(full))
            throw new FileNotFoundException($"No existe: {full}");

        var info = new FileInfo(full);

        if (info.Length > PathGuard.MaxTransferBytes)
        {
            throw new IOException(
                $"El archivo pesa {info.Length / 1024 / 1024} MB y el maximo es " +
                $"{PathGuard.MaxTransferBytes / 1024 / 1024} MB");
        }

        return JsonSerializer.Serialize(new
        {
            Path = full,
            info.Length,
            ContentBase64 = Convert.ToBase64String(File.ReadAllBytes(full))
        });
    }

    public static string WriteFile(string path, string contentBase64)
    {
        if (!PathGuard.TryValidate(path, forWriting: true, out var full, out var error))
            throw new ArgumentException(error);

        byte[] content;

        try
        {
            content = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            throw new ArgumentException("El contenido no es base64 valido");
        }

        if (content.Length > PathGuard.MaxTransferBytes)
            throw new IOException($"Maximo {PathGuard.MaxTransferBytes / 1024 / 1024} MB por transferencia");

        File.WriteAllBytes(full, content);
        return $"Escrito {content.Length} bytes en {full}";
    }
}
