namespace DeviceHub.Contracts;

/// <summary>
/// Todo lo que distingue a un comando de otro, en un solo sitio.
///
/// Sin esto, las diferencias entre "hacer ping" y "apagar la maquina" acabarian
/// repartidas en `if (type == ...)` por el servidor, el agente y la UI. La tabla
/// de abajo es la unica autoridad sobre quien puede pedir que, que caduca
/// cuando, y que se puede reintentar.
/// </summary>
/// <param name="RequiredRole">Rol minimo. Ver <see cref="Roles"/>.</param>
/// <param name="IsDestructive">Interrumpe el trabajo de alguien. Exige confirmacion en la UI.</param>
/// <param name="AllowRetry">Si un reintento automatico es seguro.</param>
/// <param name="Ttl">Cuanto vale la pena seguir intentando entregarlo.</param>
/// <param name="Timeout">Cuanto puede tardar en ejecutarse antes de darlo por fallido.</param>
public sealed record CommandDefinition(
    CommandType Type,
    string RequiredRole,
    bool IsDestructive,
    bool AllowRetry,
    TimeSpan Ttl,
    TimeSpan Timeout);

public static class CommandPolicy
{
    private static readonly Dictionary<CommandType, CommandDefinition> Definitions = new()
    {
        // --- Informativos: baratos, repetibles, sin efecto ---
        [CommandType.Ping] = new(CommandType.Ping, Roles.Viewer,
            IsDestructive: false, AllowRetry: true, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(10)),

        [CommandType.GetProcesses] = new(CommandType.GetProcesses, Roles.Technician,
            IsDestructive: false, AllowRetry: true, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(30)),

        [CommandType.GetServices] = new(CommandType.GetServices, Roles.Technician,
            IsDestructive: false, AllowRetry: true, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(30)),

        // --- Administrativos: cambian el estado de la PC, sin reintento automatico ---
        [CommandType.KillProcess] = new(CommandType.KillProcess, Roles.Technician,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(20)),

        [CommandType.StartService] = new(CommandType.StartService, Roles.Engineer,
            IsDestructive: false, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(60)),

        [CommandType.StopService] = new(CommandType.StopService, Roles.Engineer,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(60)),

        [CommandType.RestartService] = new(CommandType.RestartService, Roles.Engineer,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(90)),

        // --- Destructivos: TTL corto a proposito ---
        //
        // Una PC apagada dos horas NO debe reiniciarse al reconectar porque
        // alguien lo pidio hace dos horas. Para entonces el motivo ya no existe
        // y el operador esta trabajando en ella.
        [CommandType.RestartMachine] = new(CommandType.RestartMachine, Roles.Engineer,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromSeconds(30), Timeout: TimeSpan.FromSeconds(15)),

        [CommandType.ShutdownMachine] = new(CommandType.ShutdownMachine, Roles.Administrator,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromSeconds(30), Timeout: TimeSpan.FromSeconds(15))
    };

    /// <summary>Un tipo fuera de la tabla no existe. No hay default permisivo.</summary>
    public static bool TryGet(CommandType type, out CommandDefinition definition)
        => Definitions.TryGetValue(type, out definition!);

    public static CommandDefinition Get(CommandType type)
        => TryGet(type, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de comando desconocido");

    public static IReadOnlyCollection<CommandDefinition> All => Definitions.Values;

    /// <summary>Nombre de parametro obligatorio, o null si el comando no lleva.</summary>
    public static string? RequiredParameter(CommandType type) => type switch
    {
        CommandType.KillProcess => "pid",
        CommandType.StartService or CommandType.StopService or CommandType.RestartService => "service",
        _ => null
    };
}
