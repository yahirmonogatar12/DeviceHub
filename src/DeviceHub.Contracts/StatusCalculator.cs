namespace DeviceHub.Contracts;

/// <summary>
/// El estado NO se almacena: se deriva de last_seen.
///
/// Guardar una columna `status` obligaria a un servicio de fondo recorriendo la
/// tabla cada segundo para mantenerla al dia. Derivarla no cuesta nada y no
/// puede quedar desincronizada.
///
/// Vive en Contracts porque el dashboard tiene que recalcularlo por su cuenta:
/// una maquina que deja de latir no genera ningun mensaje nuevo, asi que si la
/// UI se quedara con el valor que le empujaron, seguiria mostrando ONLINE para
/// siempre.
/// </summary>
public static class StatusCalculator
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan UnreachableWindow = TimeSpan.FromMinutes(5);

    public static MachineStatus Compute(DateTime? lastSeenUtc, DateTime nowUtc)
    {
        if (lastSeenUtc is null)
            return MachineStatus.Offline;

        var age = nowUtc - DateTime.SpecifyKind(lastSeenUtc.Value, DateTimeKind.Utc);

        if (age < TimeSpan.Zero)
            return MachineStatus.Online; // reloj del server ligeramente atras: no castigar

        if (age < OnlineWindow)
            return MachineStatus.Online;

        return age < UnreachableWindow ? MachineStatus.Unreachable : MachineStatus.Offline;
    }
}
