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

    public string DataDirectory { get; set; } = MachineIdentity.DefaultDirectory;

    public string ServerAddress => $"https://{ServerHost}:{ServerPort}";
}
