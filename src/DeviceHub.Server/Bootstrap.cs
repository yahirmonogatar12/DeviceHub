using DeviceHub.Server.Data;
using DeviceHub.Server.Security;

namespace DeviceHub.Server;

public static class Bootstrap
{
    /// <summary>
    /// Crea el primer administrador si la tabla esta vacia, con una password
    /// aleatoria que se escribe UNA vez en el log.
    ///
    /// No hay usuario sembrado en la migracion a proposito: un default
    /// hardcodeado es exactamente lo que nadie cambia despues.
    /// </summary>
    public static async Task EnsureAdminUserAsync(UserRepository users, ILogger logger, CancellationToken ct = default)
    {
        if (await users.CountAsync(ct) > 0)
            return;

        var password = Secrets.NewReadablePassword();
        await users.CreateAsync("admin", Secrets.HashPassword(password), "administrator", ct);

        logger.LogWarning(
            "Usuario inicial creado -> usuario: admin | password: {Password} | cambiala tras el primer ingreso (no se vuelve a mostrar)",
            password);
    }
}
