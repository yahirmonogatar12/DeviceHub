using DeviceHub.Contracts;

namespace DeviceHub.Server.Domain;

/// <summary>
/// Deteccion de clonacion. Un GUID en disco sobrevive a sysprep y a la clonacion
/// de imagen, asi que dos PCs pueden acabar con el mismo machineId.
///
/// Regla clave: el fingerprint solo decide cuando AMBOS lados son HIGH. Si el
/// guardado era LOW, la diferencia puede venir de un WMI que fallo, no de un
/// clon -- y marcar conflicto ahi seria ruido puro.
///
/// El detector que de verdad sostiene esto no vive aqui: son dos streams Connect
/// activos con el mismo machineId, que no depende del hardware en absoluto.
/// </summary>
public static class IdentityGuard
{
    /// <summary>Umbral de la degradacion aprendida.</summary>
    public const int SharedFingerprintLimit = 3;

    public static bool IsHardwareConflict(
        string? storedHash,
        FingerprintConfidence storedConfidence,
        string? incomingHash,
        FingerprintConfidence incomingConfidence)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || string.IsNullOrWhiteSpace(incomingHash))
            return false;

        if (storedHash == incomingHash)
            return false;

        return storedConfidence == FingerprintConfidence.High
            && incomingConfidence == FingerprintConfidence.High;
    }

    /// <summary>
    /// Degradacion aprendida: si el mismo valor aparece en >=3 maquinas distintas,
    /// no discrimina nada. Cubre el lote de placas que salio de fabrica con el
    /// mismo serial sin tener que adivinar la lista completa por adelantado.
    /// </summary>
    public static FingerprintConfidence Degrade(FingerprintConfidence reported, int machinesSharingFingerprint)
        => machinesSharingFingerprint >= SharedFingerprintLimit
            ? FingerprintConfidence.Low
            : reported;
}
