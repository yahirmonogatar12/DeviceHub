using System.Diagnostics;
using System.ServiceProcess;
using DeviceHub.Agent.Files;
using DeviceHub.Agent.Terminal;
using System.Text.Json;
using DeviceHub.Contracts;

namespace DeviceHub.Agent.Commands;

/// <summary>
/// Ejecuta los comandos permitidos. Un solo `switch` en un solo archivo: todo lo
/// que esta maquina puede hacer por orden remota se audita leyendo esta clase.
/// Repartirlo en nueve ejecutores enchufables solo esconderia la superficie.
/// </summary>
public sealed class CommandRunner(ILogger<CommandRunner> logger)
{
    /// <summary>
    /// Lo pone el Worker al arrancar. Una funcion y no el servicio entero: aqui
    /// no hace falta saber nada del actualizador, solo poder decirle que mire ya.
    /// </summary>
    public Func<bool>? ComprobarActualizacion { get; set; }

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, string agentVersion, CancellationToken ct)
    {
        var result = new CommandResult { CommandId = request.CommandId, AgentVersion = agentVersion };

        // El agente tambien valida el tipo, no solo el servidor: un servidor
        // comprometido o una version futura no deben poder inventar comandos.
        if (!CommandPolicy.TryGet(request.Type, out var definition))
        {
            logger.LogWarning("Comando desconocido rechazado: {Type}", request.Type);
            result.Status = CommandStatus.Failed;
            result.ErrorCode = "UNKNOWN_COMMAND";
            result.Result = $"Tipo no permitido: {request.Type}";
            return result;
        }

        // LA proteccion contra ejecutar ordenes viejas. Una PC apagada dos horas
        // no debe reiniciarse al volver porque alguien lo pidio hace dos horas.
        if (request.ExpiresAt is not null && request.ExpiresAt.ToDateTime() <= DateTime.UtcNow)
        {
            logger.LogWarning("Comando {CommandId} ({Type}) vencido; no se ejecuta", request.CommandId, request.Type);
            result.Status = CommandStatus.Expired;
            result.ErrorCode = "EXPIRED";
            result.Result = "El comando vencio antes de poder ejecutarse";
            return result;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(definition.Timeout);

        try
        {
            result.Result = await RunAsync(request, timeout.Token);
            result.Status = CommandStatus.Completed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Status = CommandStatus.Failed;
            result.ErrorCode = "TIMEOUT";
            result.Result = $"Excedio {definition.Timeout.TotalSeconds:0} s";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo el comando {Type}", request.Type);
            result.Status = CommandStatus.Failed;
            result.ErrorCode = ex.GetType().Name;
            result.Result = ex.Message;
        }

        return result;
    }

    private Task<string> RunAsync(CommandRequest request, CancellationToken ct) => request.Type switch
    {
        // Si encuentra algo, el actualizador para este servicio en segundos: la
        // respuesta puede no llegar nunca, y eso es justo lo que se pidio. Por
        // eso el texto dice lo que VA a pasar, no lo que paso.
        CommandType.CheckUpdate => Task.FromResult(
            ComprobarActualizacion is null
                ? "El actualizador no esta disponible en este agente."
                : ComprobarActualizacion()
                    ? "Hay version nueva: se esta instalando y el servicio se reiniciara."
                    : "Sin novedades: ya esta en la ultima version publicada."),

        CommandType.Ping => Task.FromResult(
            $"pong desde {Environment.MachineName}, uptime {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\d\\ hh\\:mm}"),

        CommandType.GetProcesses => GetProcessesAsync(ct),
        CommandType.GetServices => Task.FromResult(GetServices()),
        CommandType.KillProcess => Task.FromResult(KillProcess(Parameter(request, "pid"))),
        CommandType.StartService => Task.FromResult(ControlService(Parameter(request, "service"), ServiceAction.Start)),
        CommandType.StopService => Task.FromResult(ControlService(Parameter(request, "service"), ServiceAction.Stop)),
        CommandType.RestartService => Task.FromResult(ControlService(Parameter(request, "service"), ServiceAction.Restart)),
        CommandType.RestartMachine => Task.FromResult(Shutdown(restart: true)),
        CommandType.ShutdownMachine => Task.FromResult(Shutdown(restart: false)),

        // Fase 14. La validacion de rutas vive en PathGuard, dentro del agente:
        // solo esta maquina sabe donde estan sus directorios criticos.
        CommandType.ListDirectory => Task.FromResult(FileOperations.ListDirectory(Parameter(request, "path"))),
        CommandType.CreateDirectory => Task.FromResult(FileOperations.CreateDirectory(Parameter(request, "path"))),
        CommandType.DeletePath => Task.FromResult(Audited(FileOperations.Delete(Parameter(request, "path")))),
        CommandType.RenamePath => Task.FromResult(Audited(
            FileOperations.Rename(Parameter(request, "path"), Parameter(request, "newName")))),
        CommandType.ReadFile => Task.FromResult(FileOperations.ReadFile(Parameter(request, "path"))),

        // Fase 15. El agente no sabe de sesiones: eso lo controla el servidor.
        // Aqui solo se ejecuta, con timeout y tope de salida.
        CommandType.RunShell => Task.FromResult(Audited(ShellRunner.Execute(
            Parameter(request, "command"),
            request.Parameters.TryGetValue("cwd", out var cwd) ? cwd : Environment.SystemDirectory,
            CommandPolicy.Get(CommandType.RunShell).Timeout - TimeSpan.FromSeconds(5)))),
        CommandType.WriteFile => Task.FromResult(Audited(
            FileOperations.WriteFile(Parameter(request, "path"), Parameter(request, "content")))),

        _ => throw new InvalidOperationException($"Sin implementacion para {request.Type}")
    };

    // ------------------------------------------------------------ Fase 8: procesos

    private sealed record ProcessInfo(string Name, int Pid, long MemoryBytes, double CpuPercent);

    /// <summary>
    /// Dos muestras separadas medio segundo: el % de CPU de un proceso es un
    /// delta, no un valor que se pueda leer de golpe. Sin esto solo se puede
    /// reportar tiempo acumulado, que no sirve para "que me esta comiendo la CPU
    /// ahora".
    /// </summary>
    private static async Task<string> GetProcessesAsync(CancellationToken ct)
    {
        var first = SampleCpuTimes();
        var start = DateTime.UtcNow;

        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        var elapsed = (DateTime.UtcNow - start).TotalSeconds * Environment.ProcessorCount;
        var processes = new List<ProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var cpu = 0d;

                    if (first.TryGetValue(process.Id, out var before) && elapsed > 0)
                        cpu = Math.Clamp((process.TotalProcessorTime.TotalSeconds - before) / elapsed * 100, 0, 100);

                    processes.Add(new ProcessInfo(process.ProcessName, process.Id, process.WorkingSet64, Math.Round(cpu, 1)));
                }
                catch (Exception)
                {
                    // Proceso protegido o que murio entre la enumeracion y la
                    // lectura. Se omite: no debe tumbar el listado entero.
                }
            }
        }

        return JsonSerializer.Serialize(processes.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryBytes));
    }

    private static Dictionary<int, double> SampleCpuTimes()
    {
        var samples = new Dictionary<int, double>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    samples[process.Id] = process.TotalProcessorTime.TotalSeconds;
                }
                catch (Exception)
                {
                    // idem
                }
            }
        }

        return samples;
    }

    private string KillProcess(string rawPid)
    {
        if (!int.TryParse(rawPid, out var pid))
            throw new ArgumentException($"pid invalido: {rawPid}");

        // System (4), Idle (0) y el propio agente. Matar al agente dejaria la PC
        // invisible y sin forma remota de recuperarla.
        if (pid <= 4)
            throw new InvalidOperationException($"Rechazado: {pid} es un proceso del sistema");

        if (pid == Environment.ProcessId)
            throw new InvalidOperationException("Rechazado: es el propio agente de DeviceHub");

        using var process = Process.GetProcessById(pid);
        var name = process.ProcessName;

        logger.LogWarning("Matando proceso {Name} ({Pid}) por orden remota", name, pid);
        process.Kill(entireProcessTree: false);
        process.WaitForExit(10_000);

        return $"Proceso {name} ({pid}) terminado";
    }

    // ----------------------------------------------------------- Fase 9: servicios

    private sealed record ServiceInfo(string Name, string DisplayName, string Status, string StartType);

    private static string GetServices()
    {
        var services = new List<ServiceInfo>();

        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                try
                {
                    services.Add(new ServiceInfo(
                        service.ServiceName, service.DisplayName, service.Status.ToString(), service.StartType.ToString()));
                }
                catch (Exception)
                {
                    // Servicio sin permisos de consulta.
                }
            }
        }

        return JsonSerializer.Serialize(services.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private enum ServiceAction { Start, Stop, Restart }

    private string ControlService(string name, ServiceAction action)
    {
        using var service = new ServiceController(name);
        var wait = TimeSpan.FromSeconds(30);

        logger.LogWarning("{Action} sobre el servicio {Service} por orden remota", action, name);

        if (action is ServiceAction.Stop or ServiceAction.Restart)
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, wait);
            }
        }

        if (action is ServiceAction.Start or ServiceAction.Restart)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, wait);
        }

        service.Refresh();
        return $"{service.DisplayName}: {service.Status}";
    }

    // -------------------------------------------------------------- apagado

    /// <summary>
    /// Con retraso de 5 s a proposito: da tiempo a responder COMPLETED antes de
    /// que la maquina se vaya. Si no, el comando quedaria eternamente en RUNNING
    /// y el operador no sabria si funciono.
    /// </summary>
    private string Shutdown(bool restart)
    {
        var flag = restart ? "/r" : "/s";
        logger.LogWarning("{Action} de la maquina por orden remota", restart ? "Reinicio" : "Apagado");

        using var process = Process.Start(new ProcessStartInfo("shutdown", $"{flag} /t 5 /c \"ILSAN DeviceHub\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("No se pudo lanzar shutdown.exe");

        process.WaitForExit(10_000);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"shutdown.exe devolvio {process.ExitCode}");

        return restart ? "Reinicio programado en 5 s" : "Apagado programado en 5 s";
    }

    /// <summary>Deja constancia local de las operaciones destructivas de archivo,
    /// ademas de la auditoria del servidor: si alguien discute que paso en la PC,
    /// el registro de eventos de Windows lo respalda.</summary>
    private string Audited(string result)
    {
        logger.LogWarning("Operacion de archivo por orden remota: {Result}", result);
        return result;
    }

    private static string Parameter(CommandRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Falta el parametro '{name}'");
}
