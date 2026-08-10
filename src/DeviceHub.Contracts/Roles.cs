namespace DeviceHub.Contracts;

/// <summary>
/// Roles ordenados. "Technician o superior" tiene que ser una comparacion, no
/// una lista de strings repetida en cada handler.
/// </summary>
public static class Roles
{
    public const string Viewer = "viewer";
    public const string Technician = "technician";
    public const string Engineer = "engineer";
    public const string Administrator = "administrator";

    private static readonly string[] Ordered = [Viewer, Technician, Engineer, Administrator];

    /// <summary>-1 si el rol no existe: un rol desconocido no satisface nada.</summary>
    public static int Rank(string? role)
        => role is null ? -1 : Array.IndexOf(Ordered, role.Trim().ToLowerInvariant());

    public static bool Satisfies(string? userRole, string requiredRole)
    {
        var userRank = Rank(userRole);
        return userRank >= 0 && userRank >= Rank(requiredRole);
    }
}
