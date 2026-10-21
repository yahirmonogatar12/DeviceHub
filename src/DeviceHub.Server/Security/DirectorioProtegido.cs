using System.Security.AccessControl;
using System.Security.Principal;

namespace DeviceHub.Server.Security;

/// <summary>
/// La ACL del directorio de datos del servidor: solo SYSTEM y Administradores.
///
/// Ahi viven server.pfx -- sin contrasena a proposito -- y jwt.key, 64 bytes en
/// claro. Los dos comentarios que lo justifican decian que "lo que protege la
/// clave es la ACL del directorio", y nadie la ponia: el directorio heredaba la
/// de C:\ProgramData, donde Usuarios tiene lectura. Cualquier usuario de la
/// maquina del servidor podia leer jwt.key, firmarse un token con
/// role=administrator y manejar la planta entera, o sacar la clave privada del
/// certificado al que los agentes tienen fijado el pin.
///
/// Es lo mismo que el agente ya hacia con su machine.json
/// (MachineIdentity.EnsureDirectory), y con la misma regla.
/// </summary>
public static class DirectorioProtegido
{
    public static void Endurecer(string directorio, ILogger logger)
    {
        var info = Directory.CreateDirectory(directorio);

        if (!OperatingSystem.IsWindows())
            return;

        // Sin ser SYSTEM ni estar elevado, aplicar la ACL dejaria FUERA a este
        // mismo proceso y el servidor no podria ni leer su certificado. Como
        // servicio corre como LocalSystem y entra.
        using (var identidad = WindowsIdentity.GetCurrent())
        {
            if (!identidad.IsSystem && !new WindowsPrincipal(identidad).IsInRole(WindowsBuiltInRole.Administrator))
            {
                logger.LogWarning(
                    "Proceso sin privilegios: {Directorio} queda con la ACL heredada y jwt.key es legible " +
                    "por los usuarios de esta maquina. Como servicio (LocalSystem) se endurece solo.",
                    directorio);
                return;
            }
        }

        try
        {
            var seguridad = new DirectorySecurity();
            seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                seguridad.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            info.SetAccessControl(seguridad);

            // Los archivos que ya existian conservan su ACL EXPLICITA, si la
            // tienen: proteger el directorio solo cambia lo que heredan. Se les
            // devuelve la herencia para que les llegue la nueva.
            foreach (var archivo in info.EnumerateFiles())
            {
                var suya = archivo.GetAccessControl();
                suya.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

                foreach (FileSystemAccessRule regla in suya.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                    suya.RemoveAccessRuleSpecific(regla);

                archivo.SetAccessControl(suya);
            }
        }
        catch (Exception ex)
        {
            // Avisar fuerte y seguir: un servidor que no arranca deja ciega a la
            // planta, y la ACL se puede poner a mano.
            logger.LogError(ex,
                "No se pudo endurecer la ACL de {Directorio}: jwt.key y server.pfx pueden ser legibles por los usuarios de esta maquina.",
                directorio);
        }
    }
}
