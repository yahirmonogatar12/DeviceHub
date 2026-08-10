namespace DeviceHub.Server.Remote;

/// <summary>Que tiene que ejecutar la PC del tecnico para abrir la sesion.</summary>
public sealed record RemoteLaunch(string Provider, string DeviceId, string Target, string Arguments);

/// <summary>
/// Motor de control remoto. Es la unica pieza del sistema que sabe como se
/// llama el producto que hay detras.
///
/// Una sola operacion, no las dos del plan original: obtener la informacion de
/// conexion resulto ser una lectura de la BD que no necesita saber nada del
/// motor, y el lanzamiento ocurre en la PC del tecnico, no en el servidor. Lo
/// unico especifico del motor es COMO se invoca.
/// </summary>
public interface IRemoteProvider
{
    string Provider { get; }

    RemoteLaunch BuildLaunch(string deviceId);
}

/// <summary>
/// RustDesk. Todo lo especifico del producto esta aqui y en el detector del
/// agente; ni el contrato, ni la BD, ni el dashboard lo nombran.
///
/// NO se maneja la contrasena de RustDesk a proposito. Guardarla obligaria a
/// cifrado en reposo, rotacion y auditoria propia -- y una tabla con las claves
/// de acceso remoto de toda la planta es un objetivo demasiado goloso para
/// resolverlo de paso. El tecnico la introduce, o se configura acceso
/// desatendido en el propio RustDesk. Ver deuda deliberada en el roadmap.
/// </summary>
public sealed class RustDeskProvider : IRemoteProvider
{
    public string Provider => "rustdesk";

    public RemoteLaunch BuildLaunch(string deviceId)
        => new(Provider, deviceId, "rustdesk.exe", $"--connect {deviceId}");
}
