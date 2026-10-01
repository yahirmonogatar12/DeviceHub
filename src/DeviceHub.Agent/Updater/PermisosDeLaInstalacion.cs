using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeviceHub.Agent.Updater;

/// <summary>
/// La carpeta del agente tiene que poder LEERLA cualquier usuario.
///
/// El ayudante del portapapeles de RemoteHost corre como el usuario y arranca
/// desde aqui. Sin lectura, el lanzador de .NET ni siquiera resuelve su propia
/// ruta y muere con 0x80008085 ("Failed to resolve full path of the current
/// executable"): pegar con Ctrl+V en la PC remota no hacia nada.
///
/// LA ROMPIAN LAS ACTUALIZACIONES. El paquete se extrae en el temporal del
/// servicio -- el de SYSTEM, que en Windows reciente es C:\Windows\SystemTemp y
/// solo lo abre SYSTEM -- y el actualizador lo MUEVE a Program Files. Mover
/// conserva la ACL de origen, asi que tras la primera actualizacion la carpeta
/// quedaba solo para SYSTEM y Administradores. Nadie lo noto mientras todo lo
/// que se ejecutaba desde aqui corria como SYSTEM.
///
/// Se repara al ARRANCAR, y no solo en el actualizador, porque esta version
/// llega instalada por el actualizador de la anterior, que la deja igual de mal.
/// </summary>
public static class PermisosDeLaInstalacion
{
    public static void Reparar(ILogger logger)
    {
        var carpeta = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var muestra = Path.Combine(carpeta, "DeviceHub.RemoteHost.exe");

        try
        {
            if (!File.Exists(muestra) || UsuariosPuedenLeer(muestra))
                return;

            // /reset: la ACL heredada de Program Files, en la carpeta y en todo
            // lo de dentro. Es lo mismo que tendria recien instalada.
            using var icacls = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "icacls.exe"), $"\"{carpeta}\" /reset /T /C /Q")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });

            icacls?.WaitForExit(TimeSpan.FromMinutes(1));

            if (UsuariosPuedenLeer(muestra))
                logger.LogWarning("La carpeta {Carpeta} solo la podia leer SYSTEM (la dejo asi una actualizacion); permisos restablecidos", carpeta);
            else
                logger.LogError("La carpeta {Carpeta} sigue sin poder leerla un usuario (icacls {Codigo}): el pegado de archivos en sesiones remotas no funcionara",
                    carpeta, icacls?.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron revisar los permisos de {Carpeta}", carpeta);
        }
    }

    /// <summary>Si un usuario cualquiera de la PC puede leer el archivo.</summary>
    public static bool UsuariosPuedenLeer(string archivo)
    {
        SecurityIdentifier[] cualquiera =
        [
            new(WellKnownSidType.BuiltinUsersSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.WorldSid, null)
        ];

        return new FileInfo(archivo).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                      && cualquiera.Contains(r.IdentityReference)
                      && (r.FileSystemRights & FileSystemRights.ReadData) != 0);
    }
}
