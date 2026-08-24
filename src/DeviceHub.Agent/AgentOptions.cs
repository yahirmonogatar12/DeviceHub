using DeviceHub.Agent.Identity;

namespace DeviceHub.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "DeviceHub";

    public string ServerHost { get; set; } = "localhost";
    public int ServerPort { get; set; } = 5443;

    /// <summary>Codigo de un solo uso emitido por un administrador. Lo escribe el
    /// instalador. Se consume en el primer Register y deja de servir.</summary>
    public string? EnrollmentCode { get; set; }

    /// <summary>
    /// Pines SPKI que trae el instalador. Solo se usan como semilla: en cuanto la
    /// maquina tiene identidad propia, manda lo guardado en machine.json, que es
    /// lo que el servidor actualiza durante una rotacion.
    ///
    /// Vacio aqui y sin identidad previa = TOFU en la primera conexion.
    /// </summary>
    public List<string> PinnedKeys { get; set; } = [];

    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>
    /// Fase 7. El host de control remoto no valida el certificado del relay.
    ///
    /// Escotilla de laboratorio, apagada por defecto: normalmente el host recibe
    /// los pines SPKI del agente por el named pipe y valida con ellos, que es
    /// mas fuerte que la cadena de CA para un certificado autofirmado. Solo hace
    /// falta encenderla en una PC donde el agente todavia no tenga pines.
    /// </summary>
    public bool RemoteAllowUntrusted { get; set; }

    /// <summary>
    /// Recurso compartido con update.json y los paquetes (Fase 16).
    /// Vacio = no se auto-actualiza.
    /// </summary>
    public string UpdateShare { get; set; } = string.Empty;

    /// <summary>
    /// Anillo de despliegue que se pide al SERVIDOR. Vacio = no se pregunta.
    ///
    /// El agente ya no necesita saber donde vive el recurso: se lo pide a la
    /// misma maquina con la que ya habla, por el mismo puerto y con el mismo
    /// certificado pinado. Eso importa porque la configuracion de cada PC solo
    /// se puede cambiar yendo a esa PC, y las que hay que arreglar son
    /// justamente las que no se pueden actualizar.
    ///
    /// UpdateShare sigue existiendo como respaldo: donde SMB funciona -- el
    /// propio servidor, sin ir mas lejos -- sirve igual, y asi el servidor puede
    /// actualizarse antes de que exista el endpoint.
    /// </summary>
    public string UpdateRing { get; set; } = "production";

    /// <summary>
    /// Thumbprint del certificado con el que se firman los paquetes.
    ///
    /// Vacio significa que la unica proteccion de la flota es la ACL del recurso
    /// compartido: quien pueda escribir ahi ejecuta codigo como SYSTEM en cada
    /// PC. El agente lo avisa en el log en cada comprobacion.
    /// </summary>
    public string UpdatePublisherThumbprint { get; set; } = string.Empty;

    public int UpdateCheckHours { get; set; } = 6;

    public string DataDirectory { get; set; } = MachineIdentity.DefaultDirectory;

    public string ServerAddress => $"https://{ServerHost}:{ServerPort}";
}
