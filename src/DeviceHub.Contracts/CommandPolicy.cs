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

        // Administrador y no tecnico: empuja una version a esa PC, y la version
        // la decide quien publica en el recurso. TTL corto -- si la PC estaba
        // apagada, mañana ya no interesa este comando sino el temporizador.
        [CommandType.CheckUpdate] = new(CommandType.CheckUpdate, Roles.Administrator,
            IsDestructive: false, AllowRetry: false, Ttl: TimeSpan.FromMinutes(10), Timeout: TimeSpan.FromMinutes(2)),

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
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromSeconds(30), Timeout: TimeSpan.FromSeconds(15)),

        // --- Archivos (Fase 14) ---
        //
        // Listar y leer exigen Engineer, no Technician: el contenido de los
        // archivos de una PC de planta puede incluir cadenas de conexion,
        // recetas y configuracion de proceso.
        [CommandType.ListDirectory] = new(CommandType.ListDirectory, Roles.Engineer,
            IsDestructive: false, AllowRetry: true, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(30)),

        [CommandType.ReadFile] = new(CommandType.ReadFile, Roles.Engineer,
            IsDestructive: false, AllowRetry: true, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(60)),

        [CommandType.CreateDirectory] = new(CommandType.CreateDirectory, Roles.Engineer,
            IsDestructive: false, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(30)),

        [CommandType.RenamePath] = new(CommandType.RenamePath, Roles.Engineer,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(30)),

        [CommandType.WriteFile] = new(CommandType.WriteFile, Roles.Engineer,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(60)),

        // Borrar es irreversible y no hay papelera en remoto.
        [CommandType.DeletePath] = new(CommandType.DeletePath, Roles.Administrator,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(2), Timeout: TimeSpan.FromSeconds(60)),

        // --- Terminal (Fase 15) ---
        //
        // TTL corto: un comando de shell que no se pudo entregar en un minuto ya
        // no interesa, porque quien lo escribio esta mirando la pantalla.
        [CommandType.RunShell] = new(CommandType.RunShell, Roles.Engineer,
            IsDestructive: true, AllowRetry: false, Ttl: TimeSpan.FromMinutes(1), Timeout: TimeSpan.FromSeconds(60))
    };

    /// <summary>
    /// Comandos que NO pueden pedirse sueltos por SendCommand.
    ///
    /// RunShell solo se emite desde una sesion de terminal abierta: de otro modo
    /// existiria el "POST /execute con lo que sea" que todo el diseño evita, y
    /// bastaria con saltarse la UI para ejecutar sin sesion ni registro.
    /// </summary>
    public static bool RequiresSession(CommandType type) => type == CommandType.RunShell;

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
        CommandType.ListDirectory or CommandType.CreateDirectory or CommandType.DeletePath
            or CommandType.RenamePath or CommandType.ReadFile or CommandType.WriteFile => "path",
        CommandType.RunShell => "command",
        _ => null
    };
}
