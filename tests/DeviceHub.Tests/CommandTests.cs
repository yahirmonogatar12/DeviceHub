using DeviceHub.Agent.Commands;
using DeviceHub.Contracts;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeviceHub.Tests;

public class RolesTests
{
    [Theory]
    [InlineData(Roles.Administrator, Roles.Viewer, true)]
    [InlineData(Roles.Engineer, Roles.Technician, true)]
    [InlineData(Roles.Technician, Roles.Technician, true)]
    [InlineData(Roles.Viewer, Roles.Technician, false)]
    [InlineData(Roles.Technician, Roles.Engineer, false)]
    [InlineData(Roles.Engineer, Roles.Administrator, false)]
    public void Higher_roles_satisfy_lower_requirements(string user, string required, bool expected)
        => Assert.Equal(expected, Roles.Satisfies(user, required));

    /// <summary>Un rol que no existe no satisface nada, ni el mas bajo.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("superadmin")]
    [InlineData("root")]
    public void Unknown_roles_satisfy_nothing(string? role)
        => Assert.False(Roles.Satisfies(role, Roles.Viewer));

    [Fact]
    public void Role_comparison_ignores_case_and_spaces()
        => Assert.True(Roles.Satisfies("  Administrator ", Roles.Engineer));
}

public class CommandPolicyTests
{
    /// <summary>
    /// Si alguien agrega un CommandType al .proto y olvida la politica, el
    /// servidor lo rechazaria en runtime. Mejor que falle aqui.
    /// </summary>
    [Fact]
    public void Every_command_type_has_a_policy()
    {
        var missing = System.Enum.GetValues<CommandType>()
            .Where(t => t != CommandType.Unspecified)
            .Where(t => !CommandPolicy.TryGet(t, out _))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Unspecified_is_never_valid()
        => Assert.False(CommandPolicy.TryGet(CommandType.Unspecified, out _));

    /// <summary>
    /// La matriz. Es el test que impide que un dia un Viewer pueda apagar una PC.
    /// </summary>
    [Theory]
    // Ver es para todos.
    [InlineData(CommandType.Ping, Roles.Viewer, true)]
    // Listar procesos y servicios ya expone informacion sensible.
    [InlineData(CommandType.GetProcesses, Roles.Viewer, false)]
    [InlineData(CommandType.GetProcesses, Roles.Technician, true)]
    [InlineData(CommandType.GetServices, Roles.Viewer, false)]
    [InlineData(CommandType.GetServices, Roles.Technician, true)]
    // Matar procesos: tecnico.
    [InlineData(CommandType.KillProcess, Roles.Viewer, false)]
    [InlineData(CommandType.KillProcess, Roles.Technician, true)]
    // Tocar servicios: ingeniero. Un tecnico NO reinicia MySQL80.
    [InlineData(CommandType.RestartService, Roles.Technician, false)]
    [InlineData(CommandType.RestartService, Roles.Engineer, true)]
    [InlineData(CommandType.StopService, Roles.Technician, false)]
    [InlineData(CommandType.StartService, Roles.Engineer, true)]
    // Reiniciar la maquina: ingeniero. Apagarla: solo administrador, porque
    // nadie puede volver a encenderla en remoto.
    [InlineData(CommandType.RestartMachine, Roles.Technician, false)]
    [InlineData(CommandType.RestartMachine, Roles.Engineer, true)]
    [InlineData(CommandType.ShutdownMachine, Roles.Engineer, false)]
    [InlineData(CommandType.ShutdownMachine, Roles.Administrator, true)]
    public void Authorization_matrix(CommandType type, string role, bool allowed)
        => Assert.Equal(allowed, Roles.Satisfies(role, CommandPolicy.Get(type).RequiredRole));

    /// <summary>
    /// Un comando destructivo no se reintenta solo. "Se cayo la red, lo mando de
    /// nuevo" no puede aplicarse a un apagado.
    /// </summary>
    [Fact]
    public void Destructive_commands_never_auto_retry()
        => Assert.All(CommandPolicy.All.Where(d => d.IsDestructive), d => Assert.False(d.AllowRetry));

    /// <summary>
    /// El TTL corto en reinicio y apagado es el punto entero del vencimiento: una
    /// PC apagada dos horas no debe reiniciarse al reconectar.
    /// </summary>
    [Theory]
    [InlineData(CommandType.RestartMachine)]
    [InlineData(CommandType.ShutdownMachine)]
    public void Machine_level_commands_expire_fast(CommandType type)
        => Assert.True(CommandPolicy.Get(type).Ttl <= TimeSpan.FromSeconds(60));

    [Fact]
    public void Informational_commands_can_be_retried()
    {
        Assert.True(CommandPolicy.Get(CommandType.Ping).AllowRetry);
        Assert.True(CommandPolicy.Get(CommandType.GetProcesses).AllowRetry);
    }

    [Theory]
    [InlineData(CommandType.KillProcess, "pid")]
    [InlineData(CommandType.RestartService, "service")]
    [InlineData(CommandType.Ping, null)]
    public void Required_parameters(CommandType type, string? expected)
        => Assert.Equal(expected, CommandPolicy.RequiredParameter(type));
}

public class CommandRunnerTests
{
    private static readonly CommandRunner Runner = new(NullLogger<CommandRunner>.Instance);

    private static CommandRequest Request(CommandType type, DateTime expiresAtUtc)
        => new()
        {
            CommandId = Guid.NewGuid().ToString(),
            Type = type,
            ExpiresAt = Timestamp.FromDateTime(expiresAtUtc)
        };

    /// <summary>
    /// El test central de la Fase 7. Se usa RestartService sobre un servicio
    /// inexistente a proposito: si el vencimiento NO se respetara, el resultado
    /// seria Failed (servicio no encontrado) en vez de Expired. Asi se distingue
    /// "no ejecuto" de "ejecuto y fallo" sin arriesgar la maquina de pruebas.
    /// </summary>
    [Fact]
    public async Task An_expired_command_is_not_executed()
    {
        var request = Request(CommandType.RestartService, DateTime.UtcNow.AddSeconds(-1));
        request.Parameters["service"] = "ServicioQueNoExiste_DeviceHubTest";

        var result = await Runner.ExecuteAsync(request, "test", CancellationToken.None);

        Assert.Equal(CommandStatus.Expired, result.Status);
        Assert.Equal("EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task An_unknown_command_is_rejected_by_the_agent_too()
    {
        var result = await Runner.ExecuteAsync(
            Request(CommandType.Unspecified, DateTime.UtcNow.AddMinutes(5)), "test", CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Equal("UNKNOWN_COMMAND", result.ErrorCode);
    }

    [Fact]
    public async Task Ping_answers()
    {
        var result = await Runner.ExecuteAsync(
            Request(CommandType.Ping, DateTime.UtcNow.AddMinutes(5)), "1.0.0", CancellationToken.None);

        Assert.Equal(CommandStatus.Completed, result.Status);
        Assert.Contains("pong", result.Result);
        Assert.Equal("1.0.0", result.AgentVersion);
    }

    [Fact]
    public async Task A_missing_parameter_fails_instead_of_guessing()
    {
        var result = await Runner.ExecuteAsync(
            Request(CommandType.RestartService, DateTime.UtcNow.AddMinutes(5)), "test", CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Contains("service", result.Result);
    }

    /// <summary>Matar al propio agente dejaria la PC invisible y sin rescate remoto.</summary>
    [Fact]
    public async Task The_agent_refuses_to_kill_itself()
    {
        var request = Request(CommandType.KillProcess, DateTime.UtcNow.AddMinutes(5));
        request.Parameters["pid"] = Environment.ProcessId.ToString();

        var result = await Runner.ExecuteAsync(request, "test", CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    public async Task System_processes_are_protected(string pid)
    {
        var request = Request(CommandType.KillProcess, DateTime.UtcNow.AddMinutes(5));
        request.Parameters["pid"] = pid;

        var result = await Runner.ExecuteAsync(request, "test", CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, result.Status);
    }
}

public class CommandJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhcmd-" + Guid.NewGuid().ToString("N"));

    private static CommandResult Result(string id) => new()
    {
        CommandId = id,
        Status = CommandStatus.Completed,
        Result = "hecho"
    };

    [Fact]
    public void An_unseen_command_is_not_found()
    {
        using var journal = new CommandJournal(_directory);
        Assert.Null(journal.Find("nunca-visto"));
    }

    /// <summary>
    /// El caso que evita el doble reinicio: se ejecuto, se guardo, y al reenviarlo
    /// se devuelve lo guardado en vez de ejecutar otra vez.
    /// </summary>
    [Fact]
    public void An_executed_command_is_remembered()
    {
        using var journal = new CommandJournal(_directory);
        journal.Record(Result("cmd-1"));

        var found = journal.Find("cmd-1");

        Assert.NotNull(found);
        Assert.Equal(CommandStatus.Completed, found.Status);
        Assert.Equal("hecho", found.Result);
    }

    /// <summary>
    /// Va a disco justamente porque el escenario peligroso incluye que el
    /// servicio se haya reiniciado entre ejecutar y reportar.
    /// </summary>
    [Fact]
    public void It_survives_a_service_restart()
    {
        using (var journal = new CommandJournal(_directory))
            journal.Record(Result("cmd-2"));

        using var reopened = new CommandJournal(_directory);
        Assert.NotNull(reopened.Find("cmd-2"));
    }

    [Fact]
    public void The_journal_is_capped()
    {
        using var journal = new CommandJournal(_directory);

        for (var i = 0; i < CommandJournal.MaxEntries + 50; i++)
            journal.Record(Result($"cmd-{i:D5}"));

        Assert.NotNull(journal.Find($"cmd-{CommandJournal.MaxEntries + 49:D5}"));
        Assert.Null(journal.Find("cmd-00000"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // temporal; SQLite puede tardar en liberar el archivo
        }
    }
}
