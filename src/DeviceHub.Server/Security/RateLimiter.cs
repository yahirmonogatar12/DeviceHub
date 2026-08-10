using System.Collections.Concurrent;

namespace DeviceHub.Server.Security;

/// <summary>
/// Ventana deslizante por clave.
///
/// La auditoria ya registra los intentos fallidos, pero registrar no es
/// detener: sin esto, alguien puede probar contrasenas contra la consola de
/// administracion tan rapido como aguante la red, y lo unico que consigue
/// DeviceHub es dejar constancia detallada de como lo consiguieron.
///
/// ponytail: en memoria, no en base. Un reinicio del servidor limpia los
/// contadores -- aceptable, porque reiniciar el servicio no es algo que un
/// atacante remoto pueda provocar a voluntad. Si algun dia hay varias instancias
/// detras de un balanceador, esto tiene que pasar a almacenamiento compartido.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    private sealed class Window
    {
        public readonly Lock Gate = new();
        public readonly Queue<DateTime> Hits = new();
    }

    /// <summary>
    /// True si la accion se permite. Consume un intento.
    /// </summary>
    public bool TryAcquire(string key, int limit, TimeSpan period, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var window = _windows.GetOrAdd(key, _ => new Window());

        lock (window.Gate)
        {
            while (window.Hits.Count > 0 && now - window.Hits.Peek() >= period)
                window.Hits.Dequeue();

            if (window.Hits.Count >= limit)
                return false;

            window.Hits.Enqueue(now);
            return true;
        }
    }

    /// <summary>Tras un login correcto se borra el contador del usuario.</summary>
    public void Reset(string key) => _windows.TryRemove(key, out _);

    public int Tracked => _windows.Count;
}

public static class RateLimits
{
    /// <summary>Intentos de login por usuario y por origen.</summary>
    public const int LoginAttempts = 5;
    public static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Comandos por maquina. Alto para no estorbar el uso normal, pero acotado:
    /// un bucle en el dashboard no puede encolar mil reinicios.
    /// </summary>
    public const int CommandsPerMachine = 30;
    public static readonly TimeSpan CommandWindow = TimeSpan.FromMinutes(1);
}
