using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DeviceHub.Agent.Identity;

public sealed class MachineIdentityFile
{
    public string MachineId { get; set; } = string.Empty;
    public string? MachineCode { get; set; }
    /// <summary>Token permanente, cifrado con DPAPI scope LocalMachine.</summary>
    public string? ProtectedToken { get; set; }
    public string? HardwareFingerprint { get; set; }
    /// <summary>Pines SPKI aceptados. Conjunto, no valor unico: es lo que permite
    /// la rotacion de certificado sin ventana de caida.</summary>
    public List<string> PinnedKeys { get; set; } = [];

    /// <summary>
    /// Ultimo inventario enviado (Fase 5). Sobrevive al reinicio del servicio para
    /// no reenviar lo mismo en cada arranque.
    ///
    /// ponytail: son dos campos, no una base de datos. SQLite entra en Fase 6, que
    /// es cuando hay series de metricas que agregar y realmente lo justifica.
    /// </summary>
    public string? LastInventoryHash { get; set; }

    public DateTime? LastInventoryUtc { get; set; }
}

/// <summary>
/// Identidad permanente de la maquina en disco.
///
/// El GUID se genera UNA vez y no se recalcula nunca: no depende de la IP, ni
/// del hostname, ni del hardware. Un cambio de motherboard no debe huerfanar la
/// maquina ni perder su historial -- por eso la deteccion de clonacion la
/// resuelve un humano en el servidor, no una heuristica local.
/// </summary>
public sealed class MachineIdentity(string directory, ILogger<MachineIdentity> logger)
{
    public const string DefaultDirectory = @"C:\ProgramData\ILSANSYSTEM\DeviceHub";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory, "machine.json");
    private readonly Lock _gate = new();
    private bool _hardened;

    public MachineIdentityFile Load()
    {
        lock (_gate)
        {
            EnsureDirectory();

            if (File.Exists(_path))
            {
                var existing = JsonSerializer.Deserialize<MachineIdentityFile>(File.ReadAllText(_path));
                if (existing is not null && !string.IsNullOrWhiteSpace(existing.MachineId))
                    return existing;

                logger.LogWarning("machine.json ilegible o sin machineId; se genera identidad nueva");
            }

            var created = new MachineIdentityFile { MachineId = Guid.NewGuid().ToString() };
            SaveLocked(created);
            logger.LogInformation("Identidad nueva generada: {MachineId}", created.MachineId);
            return created;
        }
    }

    public void Save(MachineIdentityFile identity)
    {
        lock (_gate)
            SaveLocked(identity);
    }

    /// <summary>Borra la identidad. Solo lo dispara ISSUE_NEW_IDENTITY tras un
    /// conflicto resuelto por un administrador.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
    }

    public static string? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
            return null;

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedToken), null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Perfil DPAPI de la maquina cambiado (p.ej. restauracion de imagen):
            // el token es irrecuperable y hace falta un recovery code.
            return null;
        }
        catch (FormatException)
        {
            // Ni siquiera es base64: machine.json editado a mano o truncado por
            // un corte de luz. Se trata igual que el ilegible -- antes escapaba
            // sin capturar y tumbaba el bucle de sesion con un error que no
            // mencionaba el token por ningun lado.
            return null;
        }
    }

    public static string Protect(string token)
        => Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), null, DataProtectionScope.LocalMachine));

    private void SaveLocked(MachineIdentityFile identity)
    {
        EnsureDirectory();
        File.WriteAllText(_path, JsonSerializer.Serialize(identity, JsonOptions));
    }

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(_path)!;
        var info = Directory.CreateDirectory(dir); // no-op si ya existe

        // Se intenta una vez por instancia, no solo al crear el directorio: si una
        // ejecucion previa lo dejo con la ACL por defecto, el servicio la corrige
        // en su primer arranque.
        if (_hardened)
            return;

        _hardened = true;

        // Aplicar la ACL sin ser SYSTEM ni estar elevado dejaria FUERA a este mismo
        // proceso -- y como se aplica antes de escribir, el directorio quedaria
        // inservible. En produccion el servicio corre como LocalSystem y si entra.
        if (!CanHarden())
        {
            logger.LogWarning(
                "Proceso sin privilegios: {Directory} queda con la ACL heredada. Como servicio (LocalSystem) se endurece solo.",
                dir);
            return;
        }

        try
        {
            // Solo SYSTEM y Administradores: el token vive aqui, y DPAPI con scope
            // LocalMachine lo puede descifrar cualquier proceso local. La ACL es la
            // proteccion real, no el cifrado.
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo endurecer la ACL de {Directory}", dir);
        }
    }

    /// <summary>
    /// Un administrador NO elevado tiene el SID de Administradores en modo
    /// deny-only, asi que IsInRole devuelve false: justo lo que hace falta saber.
    /// </summary>
    private static bool CanHarden()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
