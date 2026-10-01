using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeviceHub.Dashboard;

/// <summary>
/// El dashboard se actualiza solo.
///
/// Antes cada version nueva era ir PC por PC a ejecutar update-dashboard.ps1
/// como administrador, y la mitad de las veces el tecnico seguia con la vieja
/// sin saberlo -- con arreglos que ya estaban publicados.
///
/// LO MISMO QUE EL AGENTE: un manifiesto (version, archivo, sha256) en una
/// carpeta del servidor de actualizaciones, servido por el MISMO puerto y con
/// el MISMO pin que todo lo demas (/updates/dashboard/...). Lo publica
/// deploy\publish-dashboard.ps1.
///
/// Y QUIEN INSTALA ES EL MISMO SCRIPT de siempre, update-dashboard.ps1, que
/// viaja con el paquete: una sola forma de instalar, la que ya estaba probada a
/// mano. El dashboard vive en Program Files, asi que se lanza elevado -- Windows
/// pide permiso una vez -- y el dashboard se cierra para que el script pueda
/// sobrescribirlo.
/// </summary>
public sealed class Actualizacion(DeviceHubClient cliente)
{
    private const string Carpeta = "/updates/dashboard/";

    /// <summary>update.json tal cual lo escribe publish-dashboard.ps1.</summary>
    public sealed record Manifiesto(string Version, string File, string Sha256, string ScriptSha256);

    /// <summary>La version de ESTE dashboard, la que publish-dashboard.ps1 le puso al compilar.</summary>
    public static Version Actual => typeof(Actualizacion).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>El manifiesto si hay una version MAS NUEVA que esta; null si no.</summary>
    public async Task<Manifiesto?> BuscarAsync(CancellationToken ct)
    {
        var json = await cliente.BajarTextoAsync(Carpeta + "update.json", ct);

        var manifiesto = JsonSerializer.Deserialize<Manifiesto>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return manifiesto is not null
               && Version.TryParse(manifiesto.Version, out var nueva)
               && nueva > Actual
            ? manifiesto
            : null;
    }

    /// <summary>
    /// Baja el paquete y el script, los comprueba y lanza la instalacion.
    /// Null si quedo lanzada -- y entonces el dashboard TIENE que cerrarse --, o
    /// el motivo por el que no.
    /// </summary>
    public async Task<string?> InstalarAsync(Manifiesto manifiesto, CancellationToken ct)
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "devicehub-dashboard-actualizacion");
        Directory.CreateDirectory(carpeta);

        var zip = Path.Combine(carpeta, Path.GetFileName(manifiesto.File));
        var script = Path.Combine(carpeta, "update-dashboard.ps1");

        await cliente.BajarAArchivoAsync(Carpeta + manifiesto.File, zip, ct);
        await cliente.BajarAArchivoAsync(Carpeta + "update-dashboard.ps1", script, ct);

        // LOS DOS se comprueban: el script va a correr como administrador.
        if (!Coincide(zip, manifiesto.Sha256))
            return "El paquete bajado no coincide con su sha256: no se instala.";

        if (!Coincide(script, manifiesto.ScriptSha256))
            return "El script de instalacion bajado no coincide con su sha256: no se instala.";

        try
        {
            // -Esperar: el script no toca nada hasta que este proceso se cierre,
            // que es lo que libera los archivos. Y vuelve a abrir el dashboard
            // al terminar, sin elevar.
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                $"-Zip \"{zip}\" -Sha256 {manifiesto.Sha256} " +
                $"-InstallPath \"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\" " +
                $"-Esperar {Environment.ProcessId} -Desatendido")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: dijeron que no al permiso de Windows.
            return "Actualizacion cancelada: hace falta aceptar el permiso de Windows.";
        }

        return null;
    }

    private static bool Coincide(string archivo, string sha256)
    {
        using var flujo = File.OpenRead(archivo);
        return Convert.ToHexString(SHA256.HashData(flujo)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
