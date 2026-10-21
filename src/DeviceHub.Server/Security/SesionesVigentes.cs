using System.Collections.Concurrent;
using System.Security.Claims;
using DeviceHub.Server.Data;

namespace DeviceHub.Server.Security;

/// <summary>
/// Si un JWT sigue valiendo para el usuario TAL COMO ESTA AHORA.
///
/// El token lleva el rol dentro y dura doce horas, y `is_active` y `role` solo
/// se miraban en el Login. Degradar a un administrador lo dejaba de
/// administrador hasta que caducara su token; desactivar a alguien no lo
/// sacaba; y si a una cuenta comprometida se le cambiaba la contrasena, el
/// token robado seguia sirviendo. La guarda del "ultimo administrador activo"
/// protegia la tabla, no las sesiones vivas.
///
/// Ahora cada token lleva un SELLO -- un resumen del rol, de si esta activo y
/// del hash de la contrasena -- y cada llamada lo compara con la fila actual.
/// Cambiar cualquiera de las tres cosas invalida los tokens anteriores. Sin
/// columna nueva ni migracion: el sello se deriva de lo que ya hay.
///
/// ponytail: la fila se recuerda unos segundos para no ir a MySQL en cada
/// llamada del dashboard. Lo que se cambia en ESTE servidor se olvida al
/// momento (ver Olvidar); el resto tarda como mucho <see cref="Memoria"/>.
/// Los streams largos -- WatchMachines, el visor -- se autorizan al abrirse y
/// no se cortan solos: el cambio les llega en la siguiente llamada que hagan.
/// </summary>
public sealed class SesionesVigentes(UserRepository users, ILogger<SesionesVigentes> logger)
{
    public const string ClaimSello = "dh_sello";

    public static readonly TimeSpan Memoria = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, (DateTime Hasta, UserRow? Fila)> _filas =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 16 hex del SHA-256 de rol, activo y hash de contrasena. No sirve para
    /// nada fuera de esta comparacion: el hash de la contrasena ya es un PBKDF2
    /// con sal, y de aqui solo sale un trozo de su resumen.
    /// </summary>
    public static string Sello(UserRow usuario)
        => Secrets.Sha256Hex($"{usuario.Role}|{usuario.IsActive}|{usuario.PasswordHash}")[..16];

    public async Task<bool> SigueValidaAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var nombre = principal.FindFirst(ClaimTypes.Name)?.Value;

        if (string.IsNullOrEmpty(nombre))
            return false;

        UserRow? fila;

        try
        {
            fila = await FilaAsync(nombre, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MySQL caido: se usa lo ultimo que se supo, aunque este pasado. Si
            // no hay nada, se deja pasar -- el servidor sin base de datos no
            // sirve de mucho de todos modos, y echar a todos los tecnicos por un
            // corte de la base es peor que unos segundos de un token viejo.
            logger.LogWarning("No se pudo comprobar la sesion de {Usuario}: {Error}", nombre, ex.Message);
            return !_filas.TryGetValue(nombre, out var vieja) || Coincide(principal, vieja.Fila);
        }

        return Coincide(principal, fila);
    }

    /// <summary>Lo cambiado en ESTE servidor surte efecto ya, sin esperar a que
    /// caduque lo recordado.</summary>
    public void Olvidar(string username) => _filas.TryRemove(username, out _);

    private static bool Coincide(ClaimsPrincipal principal, UserRow? fila)
    {
        if (fila is null || !fila.IsActive)
            return false;

        var sello = principal.FindFirst(ClaimSello)?.Value;

        // Tokens emitidos antes de que existiera el sello: se comprueba lo que
        // llevan, que es el rol. Asi actualizar el servidor no echa a nadie, y
        // degradar o desactivar ya funciona tambien con ellos.
        if (sello is null)
            return string.Equals(principal.FindFirst(ClaimTypes.Role)?.Value, fila.Role, StringComparison.Ordinal);

        return string.Equals(sello, Sello(fila), StringComparison.Ordinal);
    }

    private async Task<UserRow?> FilaAsync(string nombre, CancellationToken ct)
    {
        var ahora = DateTime.UtcNow;

        if (_filas.TryGetValue(nombre, out var recordada) && recordada.Hasta > ahora)
            return recordada.Fila;

        var fila = await users.FindAsync(nombre, ct);
        _filas[nombre] = (ahora + Memoria, fila);
        return fila;
    }
}
