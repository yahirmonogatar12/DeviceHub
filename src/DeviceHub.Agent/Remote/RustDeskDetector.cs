using System.Diagnostics;
using Microsoft.Win32;

namespace DeviceHub.Agent.Remote;

/// <summary>
/// Averigua el identificador de la maquina en el motor remoto instalado.
///
/// Existe como interfaz porque la Fase 18 puede sustituir RustDesk por otro
/// motor, y entonces solo se cambia la implementacion.
/// </summary>
public interface IRemoteAgentDetector
{
    string Provider { get; }

    /// <summary>null si el motor no esta instalado. NO es un error: la maquina
    /// simplemente aparece sin control remoto disponible.</summary>
    string? DetectDeviceId();
}

/// <summary>
/// Deteccion de RustDesk.
///
/// Preferencia por preguntarle al programa (`rustdesk.exe --get-id`) en vez de
/// parsear su archivo de configuracion: la ruta y el formato del TOML cambian
/// entre versiones -- unas guardan el id en RustDesk.toml, otras en
/// RustDesk2.toml, y las nuevas lo cifran. El propio ejecutable sabe responder
/// sin importar la version.
///
/// Es el mismo principio que en la seleccion de NIC: preguntarle al sistema en
/// vez de mantener una lista de casos.
///
/// TODO ESTO es privado de esta clase. DeviceHub solo recibe un string opaco.
/// </summary>
public sealed class RustDeskDetector(ILogger<RustDeskDetector> logger) : IRemoteAgentDetector
{
    public string Provider => "rustdesk";

    public string? DetectDeviceId()
    {
        var executable = FindExecutable();

        if (executable is null)
        {
            logger.LogDebug("RustDesk no esta instalado en esta maquina");
            return null;
        }

        return AskExecutable(executable) ?? ReadFromConfig();
    }

    /// <summary>Ruta por descarte, no una constante: el instalador la cambia.</summary>
    private string? FindExecutable()
    {
        foreach (var key in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk"
                 })
        {
            try
            {
                using var registry = Registry.LocalMachine.OpenSubKey(key);
                var location = registry?.GetValue("InstallLocation")?.ToString();

                if (!string.IsNullOrWhiteSpace(location))
                {
                    var candidate = Path.Combine(location, "rustdesk.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No se pudo leer {Key}", key);
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\RustDesk\rustdesk.exe",
                     @"C:\Program Files (x86)\RustDesk\rustdesk.exe"
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private string? AskExecutable(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--get-id")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });

            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(10_000))
                return null;

            var id = Sanitize(output);

            if (id is not null)
                logger.LogDebug("ID remoto obtenido de {Executable}", executable);

            return id;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallo --get-id; se intentara leer la configuracion");
            return null;
        }
    }

    /// <summary>
    /// Respaldo para versiones donde --get-id no existe. Se recorren las rutas
    /// conocidas en orden: instalacion como servicio primero, porque es la que
    /// usa una PC de planta.
    /// </summary>
    private string? ReadFromConfig()
    {
        var roots = new[]
        {
            @"C:\Windows\ServiceProfile\AppData\Roaming\RustDesk\config",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config"),
            @"C:\ProgramData\RustDesk\config"
        };

        foreach (var root in roots)
        {
            foreach (var file in new[] { "RustDesk.toml", "RustDesk2.toml" })
            {
                var path = Path.Combine(root, file);

                if (!File.Exists(path))
                    continue;

                try
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var trimmed = line.Trim();

                        // Solo `id`, nunca `enc_id` ni `password`: DeviceHub no
                        // toca credenciales del motor remoto.
                        if (!trimmed.StartsWith("id", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var separator = trimmed.IndexOf('=');
                        if (separator < 0 || trimmed[..separator].Trim() is not "id")
                            continue;

                        if (Sanitize(trimmed[(separator + 1)..]) is { } id)
                        {
                            logger.LogDebug("ID remoto leido de {Path}", path);
                            return id;
                        }
                    }
                }
                catch (IOException ex)
                {
                    logger.LogDebug(ex, "No se pudo leer {Path}", path);
                }
            }
        }

        return null;
    }

    /// <summary>Solo digitos: un ID de RustDesk es numerico, y asi no se cuela
    /// ruido de consola ni comillas del TOML.</summary>
    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length is >= 6 and <= 16 ? digits : null;
    }
}
