using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeviceHub.Agent.Terminal;

public sealed record ShellResult(
    string Output, int ExitCode, string WorkingDir, bool Truncated, string Identity);

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

    /// <summary>
    /// Se le PIDE AL HIJO QUE HABLE UTF-8, en vez de adivinar como habla.
    ///
    /// ipconfig, qwinsta y los demas programas nativos no escriben en UTF-8:
    /// usan la pagina de codigos OEM de la consola -- 850 en un Windows en
    /// espanol -- y nosotros leiamos su salida como UTF-8. Resultado:
    /// "Direcci?n", "M?scara", "?rea". Con acentos en casi todas las lineas.
    ///
    /// Decodificar como OEM en vez de UTF-8 tampoco vale: son varias paginas
    /// segun el idioma de la PC, y .NET no las trae de serie -- haria falta un
    /// paquete mas solo para leer una salida.
    ///
    /// chcp funciona aunque no se vea ninguna ventana: CREATE_NO_WINDOW da
    /// consola al hijo, solo que oculta. Con la pagina puesta a 65001 los
    /// programas nativos escriben UTF-8 y nuestra lectura ya era UTF-8.
    /// </summary>
    private const int Utf8 = 65001;

    /// <summary>
    /// Bajo QUE identidad se ejecuta todo esto. Fase 23.
    ///
    /// El agente es un servicio LocalSystem, asi que powershell.exe hereda su
    /// token y cada comando corre como NT AUTHORITY\SYSTEM -- por encima de
    /// administrador. Eso no es un anadido de esta fase: es asi desde que existe
    /// la terminal, y lo que faltaba era que alguien lo dijera.
    ///
    /// Importa saberlo porque cambia lo que uno ve: %USERPROFILE% apunta al
    /// perfil de SYSTEM, las unidades de red del usuario no existen y HKCU es
    /// otra rama. Un tecnico que investigue un problema del usuario y no sepa
    /// esto llega a la conclusion contraria.
    ///
    /// Se calcula una vez: la identidad de un servicio no cambia mientras corre.
    /// </summary>
    public static readonly string Identity = Identificar();

    private static string Identificar()
    {
        try
        {
            using var actual = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(actual);

            var papel = actual.IsSystem
                ? "SYSTEM"
                : principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                    ? "administrador"
                    : "usuario sin elevar";

            return $"{actual.Name} ({papel})";
        }
        catch (Exception ex)
        {
            return $"identidad desconocida: {ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Los dos shells, y solo estos dos.
    ///
    /// Lista cerrada a proposito. El nombre viene del dashboard, o sea de la
    /// red: dejarlo pasar a ProcessStartInfo convertiria el selector de shell en
    /// "ejecuta el .exe que yo diga", que es exactamente lo que la Fase 15
    /// evitaba al no aceptar RUN_SHELL suelto.
    /// </summary>
    private static string Elegir(string? pedido) => pedido?.Trim().ToLowerInvariant() switch
    {
        "cmd" => "cmd",

        // Vacio incluido: es lo que hacia esto desde la Fase 15, y una peticion
        // vieja no puede cambiar de shell por haber actualizado.
        _ => "powershell"
    };

    public static string Execute(string command, string workingDir, TimeSpan timeout, string? shell = null)
    {
        var result = Run(command, workingDir, timeout, shell);

        return JsonSerializer.Serialize(new
        {
            result.Output,
            result.ExitCode,
            result.WorkingDir,
            result.Truncated,
            result.Identity
        });
    }

    public static ShellResult Run(
        string command, string workingDir, TimeSpan timeout, string? shell = null)
    {
        var directory = Directory.Exists(workingDir) ? workingDir : Environment.SystemDirectory;
        var elegido = Elegir(shell);

        var startInfo = new ProcessStartInfo(elegido == "cmd" ? "cmd.exe" : "powershell.exe")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (elegido == "cmd")
        {
            // /d: NO ejecutar lo que haya en AutoRun del registro. Ahi puede
            //     haber cualquier cosa puesta por otro programa, y saldria
            //     mezclada con la salida del tecnico -- o peor, correria como
            //     SYSTEM sin que nadie lo pidiera.
            // /c: un comando y se acaba, que es el modelo de esta terminal.
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"chcp {Utf8} > nul & {command}");
        }
        else
        {
            // -NoProfile: el perfil del usuario podria redefinir cmdlets y cambiar
            //             lo que hace un comando aparentemente inocente.
            // -NonInteractive: sin esto, cualquier comando que pida confirmacion
            //             se queda esperando para siempre a alguien que no existe.
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"chcp {Utf8} > $null; {command}");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"No se pudo lanzar {startInfo.FileName}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            return new ShellResult($"Cancelado: excedio {timeout.TotalSeconds:0} s", -1, directory, false, Identity);
        }

        var output = string.Concat(stdout.Result, stderr.Result);
        var truncated = false;

        if (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
        {
            output = string.Concat(output.AsSpan(0, Math.Min(output.Length, MaxOutputBytes)),
                "\n\n[...salida truncada...]");
            truncated = true;
        }

        return new ShellResult(
            output, process.ExitCode, ResolveWorkingDir(command, directory), truncated, Identity);
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
