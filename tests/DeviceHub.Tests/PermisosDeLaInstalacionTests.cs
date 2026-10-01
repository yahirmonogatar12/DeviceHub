using System.Security.AccessControl;
using System.Security.Principal;
using DeviceHub.Agent.Updater;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Una actualizacion dejaba la carpeta del agente solo para SYSTEM, y el
/// ayudante del portapapeles -- que corre como el usuario -- no arrancaba. El
/// agente lo detecta al arrancar con esta comprobacion.
/// </summary>
public class PermisosDeLaInstalacionTests
{
    [Fact]
    public void Distingue_un_archivo_que_solo_lee_su_dueno_de_uno_que_lee_cualquiera()
    {
        var archivo = Path.Combine(Path.GetTempPath(), "devicehub-acl-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(archivo, "x");

        try
        {
            var info = new FileInfo(archivo);
            var yo = WindowsIdentity.GetCurrent().User!;

            // Como lo dejaba el actualizador: sin herencia, solo una cuenta.
            var solo = new FileSecurity();
            solo.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            solo.AddAccessRule(new FileSystemAccessRule(yo, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(solo);

            Assert.False(PermisosDeLaInstalacion.UsuariosPuedenLeer(archivo));

            // Como en Program Files: los usuarios leen y ejecutan.
            solo.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            info.SetAccessControl(solo);

            Assert.True(PermisosDeLaInstalacion.UsuariosPuedenLeer(archivo));
        }
        finally
        {
            File.Delete(archivo);
        }
    }
}
