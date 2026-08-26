using Xunit;
using DeviceHub.Agent.Terminal;

namespace DeviceHub.Tests;

/// <summary>
/// Que shell se lanza. El nombre viene del dashboard, o sea de la red, asi que
/// lo que NO puede pasar es que llegue a ProcessStartInfo sin filtrar: eso
/// convertiria el selector en "ejecuta el .exe que yo diga".
/// </summary>
public class ShellElegidoTests
{
    /// <summary>Se ejecuta un comando inofensivo y se mira QUE shell contesto.
    /// Cada uno responde distinto a lo mismo, y esa es la prueba.</summary>
    private static string Salida(string? shell)
        => ShellRunner.Run("echo %COMSPEC%", @"C:\", TimeSpan.FromSeconds(20), shell).Output;

    [Fact]
    public void Cmd_expande_la_variable_al_estilo_de_cmd()
    {
        // En cmd, %COMSPEC% se sustituye. En PowerShell es texto literal.
        Assert.Contains("cmd.exe", Salida("cmd"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShell_la_deja_literal()
        => Assert.Contains("%COMSPEC%", Salida("powershell"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_shell_se_queda_en_PowerShell(string? nada)
    {
        // Es lo que hacia desde la Fase 15. Una peticion vieja -- un agente
        // nuevo hablando con un servidor que todavia no manda el campo -- no
        // puede cambiar de shell por haber actualizado.
        Assert.Contains("%COMSPEC%", Salida(nada));
    }

    [Theory]
    [InlineData("calc")]
    [InlineData("cmd.exe /c whoami")]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData("bash")]
    public void Cualquier_otra_cosa_cae_en_PowerShell_y_no_se_ejecuta(string intento)
    {
        // La lista es cerrada: nada que no sea "cmd" acaba lanzando otro
        // programa, se queda en el shell por defecto.
        Assert.Contains("%COMSPEC%", Salida(intento));
    }

    [Fact]
    public void CMD_en_mayusculas_tambien_vale()
        => Assert.Contains("cmd.exe", Salida(" CMD "), StringComparison.OrdinalIgnoreCase);
}
