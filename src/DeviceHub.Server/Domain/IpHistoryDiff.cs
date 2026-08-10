namespace DeviceHub.Server.Domain;

public sealed record IpEntry(string Ip, string? Mac);

public sealed record IpHistoryChange(IReadOnlyList<IpEntry> ToClose, IReadOnlyList<IpEntry> ToOpen)
{
    public bool HasChanges => ToClose.Count > 0 || ToOpen.Count > 0;

    public static readonly IpHistoryChange None = new([], []);
}

/// <summary>
/// Diferencia entre las IPs vigentes en historial y las que reporta el heartbeat.
///
/// CRITICO: con heartbeat cada 30 s, escribir siempre serian 2.880 filas por dia
/// y por PC. Solo se toca la tabla cuando el conjunto CAMBIA.
/// </summary>
public static class IpHistoryDiff
{
    public static IpHistoryChange Compute(IEnumerable<IpEntry> currentlyOpen, IEnumerable<IpEntry> incoming)
    {
        var open = currentlyOpen.ToHashSet();
        var now = incoming.ToHashSet();

        if (open.SetEquals(now))
            return IpHistoryChange.None;

        return new IpHistoryChange(
            ToClose: [.. open.Except(now)],
            ToOpen: [.. now.Except(open)]);
    }
}
