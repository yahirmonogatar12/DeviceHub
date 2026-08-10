using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeviceHub.Agent.Terminal;

public sealed record ShellResult(string Output, int ExitCode, string WorkingDir, bool Truncated);

/// <summary>
/// Ejecuta un comando de shell dentro de una sesion de terminal (Fase 15).
///
/// NO se mantiene un powershell.exe vivo con stdin/stdout en tuberia. Un shell
/// persistente obliga a detectar donde acaba la salida de cada comando, y no hay
/// delimitador fiable: se acaba inyectando marcadores centinela y parseandolos,
/// que es exactamente el tipo de codigo que falla de madrugada con una salida
/// rara. Cada comando es un proceso nuevo, y su salida es inequivoca.
///
/// Lo que se pierde son las variables entre comandos. Lo que se conserva -- que
/// es lo que la gente usa de verdad -- es el directorio actual.
/// </summary>
public static class ShellRunner
{
    /// <summary>
    /// Tope de salida. Un `dir C:\ -Recurse` devuelve cientos de megas, y el
    /// resultado viaja por gRPC y acaba en una columna de MySQL.
    /// </summary>
    public const int MaxOutputBytes = 64 * 1024;

    public static string Execute(string command, string workingDir, TimeSpan timeout)
    {
        var result = Run(command, workingDir, timeout);

        return JsonSerializer.Serialize(new
        {
            result.Output,
            result.ExitCode,
            result.WorkingDir,
            result.Truncated
        });
    }

    public static ShellResult Run(string command, string workingDir, TimeSpan timeout)
    {
        var directory = Directory.Exists(workingDir) ? workingDir : Environment.SystemDirectory;

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // -NoProfile: el perfil del usuario podria redefinir cmdlets y cambiar
            //             lo que hace un comando aparentemente inocente.
            // -NonInteractive: sin esto, cualquier comando que pida confirmacion
            //             se queda esperando para siempre a alguien que no existe.
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo lanzar powershell.exe");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            return new ShellResult($"Cancelado: excedio {timeout.TotalSeconds:0} s", -1, directory, false);
        }

        var output = string.Concat(stdout.Result, stderr.Result);
        var truncated = false;

        if (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
        {
            output = string.Concat(output.AsSpan(0, Math.Min(output.Length, MaxOutputBytes)),
                "\n\n[...salida truncada...]");
            truncated = true;
        }

        return new ShellResult(output, process.ExitCode, ResolveWorkingDir(command, directory), truncated);
    }

    /// <summary>
    /// Mantiene el directorio entre comandos.
    ///
    /// Se resuelve leyendo el comando en vez de preguntandoselo al shell porque
    /// el proceso ya termino: consultar la ubicacion final exigiria una segunda
    /// invocacion, o mezclar la respuesta con la salida del usuario.
    /// </summary>
    private static string ResolveWorkingDir(string command, string current)
    {
        var trimmed = command.Trim();

        foreach (var prefix in new[] { "cd ", "Set-Location ", "sl ", "chdir " })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = trimmed[prefix.Length..].Trim().Trim('"', '\'');

            try
            {
                var candidate = Path.GetFullPath(Path.IsPathFullyQualified(target)
                    ? target
                    : Path.Combine(current, target));

                if (Directory.Exists(candidate))
                    return candidate;
            }
            catch (Exception)
            {
                // Ruta invalida: se queda donde estaba, igual que haria un shell.
            }
        }

        return current;
    }

    private static void TryKill(Process process)
    {
        try
        {
            // El arbol entero: un comando colgado suele haber lanzado hijos que
            // seguirian corriendo en la PC sin nadie mirando.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // ya termino
        }
    }
}
