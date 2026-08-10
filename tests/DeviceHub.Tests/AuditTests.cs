using DeviceHub.Contracts;
using DeviceHub.Server.Data;
using Xunit;

namespace DeviceHub.Tests;

public class AuditActionTests
{
    /// <summary>
    /// Cada comando peligroso tiene su propia accion. Registrar
    /// "COMMAND_REQUESTED" para un apagado seria cierto e inutil: buscar despues
    /// "quien apago maquinas" no puede exigir abrir el detalle de cada fila.
    /// </summary>
    [Theory]
    [InlineData(CommandType.ShutdownMachine, "MACHINE_SHUTDOWN")]
    [InlineData(CommandType.RestartMachine, "MACHINE_REBOOT")]
    [InlineData(CommandType.KillProcess, "PROCESS_KILL")]
    [InlineData(CommandType.RestartService, "SERVICE_RESTART")]
    [InlineData(CommandType.StopService, "SERVICE_STOP")]
    [InlineData(CommandType.StartService, "SERVICE_START")]
    public void Dangerous_commands_get_their_own_audit_action(CommandType type, string expected)
        => Assert.Equal(expected, AuditActions.ForCommand(type));

    [Theory]
    [InlineData(CommandType.Ping)]
    [InlineData(CommandType.GetProcesses)]
    [InlineData(CommandType.GetServices)]
    public void Informational_commands_share_the_generic_action(CommandType type)
        => Assert.Equal(AuditActions.CommandRequested, AuditActions.ForCommand(type));

    /// <summary>
    /// Todo comando destructivo debe ser distinguible en la auditoria sin leer
    /// los detalles.
    /// </summary>
    [Fact]
    public void Every_destructive_command_is_distinguishable()
    {
        var destructive = CommandPolicy.All.Where(d => d.IsDestructive).ToList();

        Assert.NotEmpty(destructive);
        Assert.All(destructive, d => Assert.NotEqual(AuditActions.CommandRequested, AuditActions.ForCommand(d.Type)));
    }
}

public class AuditEntryTests
{
    /// <summary>
    /// Un intento rechazado se audita igual que uno permitido: es la fila que
    /// dice que alguien sin permisos intento apagar una PC.
    /// </summary>
    [Fact]
    public void Denied_is_a_first_class_outcome()
    {
        var entry = new AuditEntry("yahir", Roles.Viewer, AuditActions.MachineShutdown,
            "id", "M1-FCT-01", "ILSAN-MTY", "req-1", "192.168.1.25", AuditEntry.Denied, "sin permisos");

        Assert.Equal("denied", entry.Outcome);
        Assert.Equal("M1-FCT-01", entry.MachineCode);
    }

    /// <summary>
    /// machine_code y site_code se guardan como COPIA porque la fila no tiene
    /// foreign key: debe sobrevivir a que la maquina se borre.
    /// </summary>
    [Fact]
    public void The_machine_is_snapshotted_by_code_not_only_by_id()
    {
        var entry = new AuditEntry("yahir", Roles.Administrator, AuditActions.RemoteStart,
            "id", "M1-FCT-01", "ILSAN-MTY", null, null, AuditEntry.Allowed, null);

        Assert.NotNull(entry.MachineCode);
        Assert.NotNull(entry.SiteCode);
    }
}
