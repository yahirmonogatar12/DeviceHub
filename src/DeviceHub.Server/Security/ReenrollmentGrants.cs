using System.Collections.Concurrent;

namespace DeviceHub.Server.Security;

/// <summary>
/// Permisos de reasociacion vivos: "espero a ESTA maquina, AHORA".
///
/// Existen porque un administrador puede dejar una PC permanentemente offline
/// sin pretenderlo. Al emitir identidad nueva sobre una maquina desconectada, su
/// token deja de valer y la unica salida era ir fisicamente hasta ella a poner
/// un recovery code en el appsettings. Con esto, un clic en el dashboard basta:
/// el agente lo intenta cada minuto por su cuenta y entra en cuanto hay permiso.
///
/// LA DECISION SIGUE SIENDO DE UN HUMANO. Lo que se quita es el viaje a la
/// planta, no la autorizacion. Sin un permiso vivo, un agente sin token no
/// consigue nada.
///
/// Solo en memoria y a proposito, igual que los tickets de control remoto:
/// persistirlo dejaria puertas abiertas que nadie recuerda haber abierto. Si el
/// servidor se reinicia, el administrador vuelve a pulsar.
///
/// El permiso es POR MAQUINA, no un secreto. Es mas debil que un recovery code
/// -- basta conocer el machineId de la PC para aprovecharlo -- y por eso dura
/// poco y lo enciende una persona a mano. Es el mismo compromiso que un codigo
/// de enrolamiento de un solo uso con ventana de 30 minutos, con menos ventana.
/// </summary>
public sealed class ReenrollmentGrants(TimeProvider? clock = null)
{
    /// <summary>Diez minutos: lo que tarda alguien en pulsar el boton y esperar
    /// a que el agente lo intente. No es una ventana para dejar abierta.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _permisos = new(StringComparer.Ordinal);
    private readonly TimeProvider _reloj = clock ?? TimeProvider.System;

    public int Count => _permisos.Count;

    /// <summary>Devuelve cuando caduca, para poder decirselo a quien lo pidio.</summary>
    public DateTimeOffset Authorize(string machineId)
    {
        Limpiar();

        var vence = _reloj.GetUtcNow().Add(Vigencia);
        _permisos[machineId] = vence;
        return vence;
    }

    /// <summary>
    /// Un solo uso: se quita al consumirlo. Si el registro falla despues por
    /// hardware en conflicto, el administrador vuelve a pulsar -- que es
    /// preferible a dejar el permiso puesto tras un intento sospechoso.
    /// </summary>
    public bool TryConsume(string machineId)
    {
        if (!_permisos.TryRemove(machineId, out var vence))
            return false;

        return vence > _reloj.GetUtcNow();
    }

    public bool IsAuthorized(string machineId)
        => _permisos.TryGetValue(machineId, out var vence) && vence > _reloj.GetUtcNow();

    public void Revoke(string machineId) => _permisos.TryRemove(machineId, out _);

    /// <summary>Sin esto, cada permiso no usado se queda en el diccionario hasta
    /// que reinicien el servidor.</summary>
    private void Limpiar()
    {
        var ahora = _reloj.GetUtcNow();

        foreach (var (maquina, vence) in _permisos)
        {
            if (vence <= ahora)
                _permisos.TryRemove(new KeyValuePair<string, DateTimeOffset>(maquina, vence));
        }
    }
}
