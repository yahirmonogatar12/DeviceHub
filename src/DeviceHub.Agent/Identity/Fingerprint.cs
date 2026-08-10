using System.Management;
using System.Security.Cryptography;
using System.Text;
using DeviceHub.Contracts;

namespace DeviceHub.Agent.Identity;

/// <summary>
/// Fingerprint de hardware: SHA256 del UUID SMBIOS + serial de baseboard.
///
/// Es una SENAL FUERTE, NO UNA VERDAD ABSOLUTA. Las placas industriales y los
/// BIOS mal poblados reportan valores genericos, vacios o repetidos en todo un
/// lote. Por eso el resultado viaja con un nivel de confianza y solo HIGH
/// habilita la deteccion de clonacion por hardware.
///
/// El detector que no depende de nada de esto -- dos streams Connect activos con
/// el mismo machineId -- sigue funcionando incluso con confianza LOW.
///
/// ponytail: devuelve directamente el mensaje del contrato en vez de un tipo
/// propio + funcion de mapeo. Un tipo menos y cero conversion.
/// </summary>
public static class Fingerprint
{
    // Valores que los fabricantes dejan sin poblar. La lista cubre lo conocido;
    // lo que se escape lo atrapa la degradacion aprendida del servidor (mismo
    // fingerprint en >=3 maquinas distintas => LOW).
    private static readonly HashSet<string> BogusValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "to be filled by o.e.m.",
        "tobefilledbyoem",
        "default string",
        "defaultstring",
        "system serial number",
        "systemserialnumber",
        "base board serial number",
        "not specified",
        "notspecified",
        "not applicable",
        "notapplicable",
        "none",
        "null",
        "unknown",
        "invalid",
        "o.e.m.",
        "oem",
        "0123456789",
        "123456789"
    };

    /// <summary>True si el valor no sirve para identificar la maquina.</summary>
    public static bool IsBogus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        if (BogusValues.Contains(trimmed))
            return true;

        // Solo lo alfanumerico: descarta guiones del UUID y espacios de relleno.
        var alnum = new string(trimmed.Where(char.IsLetterOrDigit).ToArray());

        if (alnum.Length < 4)
            return true;

        // Todo el mismo caracter cubre 00000000-0000-0000-0000-000000000000,
        // FFFFFFFF-FFFF-..., "0000000000" y similares.
        if (alnum.All(c => c == alnum[0]))
            return true;

        return BogusValues.Contains(alnum);
    }

    /// <summary>
    /// Calcula hash y confianza a partir de los dos componentes crudos.
    /// Pura y determinista: es la parte que se testea.
    /// </summary>
    public static HardwareFingerprint Evaluate(string? smbiosUuid, string? baseboardSerial)
    {
        var uuidOk = !IsBogus(smbiosUuid);
        var serialOk = !IsBogus(baseboardSerial);

        var confidence = (uuidOk, serialOk) switch
        {
            (true, true) => FingerprintConfidence.High,
            (true, false) or (false, true) => FingerprintConfidence.Medium,
            _ => FingerprintConfidence.Low
        };

        var material = $"{Normalize(smbiosUuid)}|{Normalize(baseboardSerial)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

        return new HardwareFingerprint { Hash = hash, Confidence = confidence };
    }

    /// <summary>Lee los valores reales de este equipo via WMI.</summary>
    public static HardwareFingerprint Collect()
        => Evaluate(
            QuerySingle("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"),
            QuerySingle("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"));

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? QuerySingle(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var item in searcher.Get())
            {
                using (item)
                    return item[property]?.ToString();
            }
        }
        catch
        {
            // WMI roto o deshabilitado no debe tumbar el agente: se degrada a LOW
            // y la deteccion por streams concurrentes sigue cubriendo la clonacion.
        }

        return null;
    }
}
