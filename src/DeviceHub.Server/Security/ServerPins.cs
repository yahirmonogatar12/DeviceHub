namespace DeviceHub.Server.Security;

/// <summary>
/// Pines SPKI que el servidor publica a los agentes.
///
/// Es una LISTA porque la rotacion de clave es multi-pin: durante el cambio se
/// anuncian {A, B}, se espera a que el 100% de las maquinas ONLINE reporten B
/// por heartbeat, y solo entonces Kestrel cambia de certificado.
/// </summary>
public sealed class ServerPins(IEnumerable<string> pins)
{
    public IReadOnlyList<string> Current { get; } = [.. pins];
}
