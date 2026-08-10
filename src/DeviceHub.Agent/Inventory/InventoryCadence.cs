namespace DeviceHub.Agent.Inventory;

/// <summary>
/// Cuando vale la pena mandar el inventario.
///
/// Tres disparadores, en este orden: nunca se envio, el hardware cambio, o
/// pasaron 12 horas. Nada mas. El inventario de una PC no se mueve en meses;
/// tratarlo como el heartbeat seria repetir los mismos bytes 2.880 veces al dia.
/// </summary>
public static class InventoryCadence
{
    /// <summary>Cada cuanto se reenvia aunque no haya cambiado nada.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromHours(12);

    /// <summary>
    /// Cada cuanto se molesta a WMI para ver si cambio algo. Consultar WMI cuesta
    /// cientos de milisegundos, asi que no se hace en cada latido.
    /// </summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(30);

    public static bool ShouldSend(string currentHash, string? lastSentHash, DateTime? lastSentUtc, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(lastSentHash) || lastSentUtc is null)
            return true;

        if (currentHash != lastSentHash)
            return true;

        return nowUtc - DateTime.SpecifyKind(lastSentUtc.Value, DateTimeKind.Utc) >= ResendInterval;
    }
}
