namespace DeviceHub.Server.Remote;

/// <summary>
/// Lo que hace falta para montar el lanzamiento, sea cual sea el motor.
///
/// La direccion del servidor y el pin los trae el DASHBOARD, no la
/// configuracion del servidor: el servidor no puede saber por que direccion se
/// le ve desde la red del tecnico -- puede haber NAT, varias interfaces, o un
/// nombre distinto segun la planta. Quien lo sabe es el que acaba de conectarse.
/// </summary>
public sealed record RemoteLaunchContext(
    string MachineId,
    string DeviceId,
    string SessionId,
    string ViewerTicket,
    string ViewerMachineId,
    string ServerAddress,
    string ServerPin);

/// <summary>
/// Que tiene que ejecutar la PC del tecnico para abrir la sesion.
///
/// `Secret` va por STDIN, nunca en `Arguments`. Los argumentos de un proceso los
/// lee cualquier usuario de la maquina con abrir el administrador de tareas, y
/// un ticket de sesion da acceso a ver y controlar una pantalla.
/// </summary>
public sealed record RemoteLaunch(
    string Provider, string DeviceId, string Target, string Arguments, string Secret = "");

/// <summary>
/// Motor de control remoto. Es la unica pieza del sistema que sabe como se
/// llama el producto que hay detras.
///
/// Fase 8: la firma se ensancho. La version anterior recibia solo el device_id,
/// que basta para un producto de terceros al que se le dice "conecta con este
/// id", pero no para un motor propio, que necesita ademas la sesion, el ticket
/// y a donde conectarse.
/// </summary>
public interface IRemoteProvider
{
    string Provider { get; }

    /// <summary>
    /// true = este motor necesita que ALGUIEN arranque el otro extremo en la PC
    /// controlada. El servidor emite tickets y le manda la orden al agente.
    ///
    /// RustDesk dice false: su servicio ya esta corriendo alli y no hay nada que
    /// lanzar. Es la unica diferencia de flujo entre los dos, y por eso es una
    /// bandera y no dos caminos separados en el servidor.
    /// </summary>
    bool RequiresHostLaunch { get; }

    RemoteLaunch BuildLaunch(RemoteLaunchContext context);
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

    /// <summary>Su servicio ya corre en la PC controlada.</summary>
    public bool RequiresHostLaunch => false;

    public RemoteLaunch BuildLaunch(RemoteLaunchContext context)
        => new(Provider, context.DeviceId, "rustdesk.exe", $"--connect {context.DeviceId}");
}

/// <summary>
/// El motor propio. Lanza DeviceHub.RemoteViewer contra el relay del servidor.
///
/// El ticket va por `Secret`, o sea por stdin. Es la misma regla que en el lado
/// del agente, donde viaja por un named pipe con ACL: en ningun punto del
/// sistema una credencial de sesion aparece en una linea de comandos.
/// </summary>
public sealed class DeviceHubRemoteProvider : IRemoteProvider
{
    public string Provider => "devicehub";

    /// <summary>El host no existe hasta que el agente lo arranca.</summary>
    public bool RequiresHostLaunch => true;

    public RemoteLaunch BuildLaunch(RemoteLaunchContext context)
    {
        // Sin pin no hay con que validar el certificado del relay. Se pasa a la
        // escotilla y el visor lo AVISA en su barra de estado, en vez de fingir
        // que la sesion es segura.
        var confianza = string.IsNullOrWhiteSpace(context.ServerPin)
            ? "--allow-untrusted"
            : $"--pin {context.ServerPin}";

        return new RemoteLaunch(
            Provider,
            context.DeviceId,
            "DeviceHub.RemoteViewer.exe",
            $"--relay-test --server {context.ServerAddress} " +
            $"--session {context.SessionId} --machine-id {context.ViewerMachineId} {confianza}",
            context.ViewerTicket);
    }
}
