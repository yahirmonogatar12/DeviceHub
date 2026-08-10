using DeviceHub.Agent.Terminal;
using DeviceHub.Contracts;
using DeviceHub.Server.Data;
using Xunit;

namespace DeviceHub.Tests;

public class ShellRunnerTests
{
    private static readonly string Temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

    [Fact]
    public void It_runs_a_command_and_returns_its_output()
    {
        var result = ShellRunner.Run("Write-Output 'hola-devicehub'", Temp, TimeSpan.FromSeconds(30));

        Assert.Contains("hola-devicehub", result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// Sin -NonInteractive, un comando que pide confirmacion se queda esperando
    /// para siempre a alguien que no existe. El timeout es la ultima red.
    /// </summary>
    [Fact]
    public void A_hung_command_is_killed_by_the_timeout()
    {
        var result = ShellRunner.Run("Start-Sleep -Seconds 30", Temp, TimeSpan.FromSeconds(3));

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("excedio", result.Output);
    }

    /// <summary>
    /// Un `dir C:\ -Recurse` devuelve cientos de megas, y esa salida viaja por
    /// gRPC y acaba en una columna de MySQL.
    /// </summary>
    [Fact]
    public void Huge_output_is_truncated()
    {
        var result = ShellRunner.Run("1..200000 | ForEach-Object { 'linea de relleno ' + $_ }",
            Temp, TimeSpan.FromSeconds(45));

        Assert.True(result.Truncated);
        Assert.Contains("truncada", result.Output);
    }

    /// <summary>Lo que la gente usa de verdad entre comandos es el directorio.</summary>
    [Fact]
    public void The_working_directory_survives_a_cd()
    {
        var result = ShellRunner.Run($"cd \"{Temp}\"", Environment.SystemDirectory, TimeSpan.FromSeconds(30));

        Assert.Equal(Temp, result.WorkingDir.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
    }

    [Fact]
    public void An_invalid_cd_leaves_the_directory_alone()
    {
        var result = ShellRunner.Run(@"cd C:\directorio-que-no-existe-devicehub", Temp, TimeSpan.FromSeconds(30));

        Assert.Equal(Temp, result.WorkingDir.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
    }

    [Fact]
    public void A_failing_command_reports_its_error()
    {
        var result = ShellRunner.Run(@"Get-Item C:\no-existe-devicehub-xyz", Temp, TimeSpan.FromSeconds(30));

        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public void A_nonexistent_working_directory_falls_back_instead_of_failing()
    {
        var result = ShellRunner.Run("Write-Output ok", @"C:\no-existe-devicehub", TimeSpan.FromSeconds(30));

        Assert.Contains("ok", result.Output);
    }
}

public class TerminalPolicyTests
{
    /// <summary>
    /// RunShell no puede pedirse suelto por SendCommand: de otro modo existiria
    /// el "POST /execute con lo que sea" que todo el diseño evita, y bastaria con
    /// saltarse la UI para ejecutar sin sesion ni registro.
    /// </summary>
    [Fact]
    public void RunShell_requires_an_open_session()
        => Assert.True(CommandPolicy.RequiresSession(CommandType.RunShell));

    [Theory]
    [InlineData(CommandType.Ping)]
    [InlineData(CommandType.RestartService)]
    [InlineData(CommandType.DeletePath)]
    public void Other_commands_do_not(CommandType type)
        => Assert.False(CommandPolicy.RequiresSession(type));

    [Theory]
    [InlineData(Roles.Technician, false)]
    [InlineData(Roles.Engineer, true)]
    [InlineData(Roles.Administrator, true)]
    public void The_terminal_is_for_engineers_and_up(string role, bool allowed)
        => Assert.Equal(allowed, Roles.Satisfies(role, CommandPolicy.Get(CommandType.RunShell).RequiredRole));

    /// <summary>
    /// El terminal caduca mucho antes que una sesion de control remoto: en la
    /// remota hay alguien mirando la pantalla, un terminal olvidado abierto es
    /// una consola con permisos de SYSTEM esperando a que alguien pase.
    /// </summary>
    [Fact]
    public void Terminal_inactivity_is_shorter_than_remote_orphan_timeout()
    {
        Assert.True(TerminalRepository.InactivityTimeout < SessionRepository.OrphanTimeout);
        Assert.InRange(TerminalRepository.InactivityTimeout, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void Terminal_commands_expire_fast()
        => Assert.True(CommandPolicy.Get(CommandType.RunShell).Ttl <= TimeSpan.FromMinutes(2));
}
