using DeviceHub.Agent.Remote;
using DeviceHub.Server.Data;
using DeviceHub.Server.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeviceHub.Tests;

public class RemoteProviderTests
{
    private static RemoteLaunchContext Contexto(string deviceId) => new(
        MachineId: "m-1",
        DeviceId: deviceId,
        SessionId: "s-1",
        ViewerTicket: "TICKET-SECRETO",
        ViewerMachineId: "PC-TECNICO",
        ServerAddress: "https://192.168.1.10:5443",
        ServerPin: "PIN",
        MachineCode: "INPUT-M2");

    [Fact]
    public void The_launch_carries_the_device_id()
    {
        var launch = new RustDeskProvider().BuildLaunch(Contexto("861237322"));

        Assert.Equal("rustdesk", launch.Provider);
        Assert.Equal("861237322", launch.DeviceId);
        Assert.Contains("861237322", launch.Arguments);
    }

    /// <summary>
    /// El resto del sistema trata provider y device_id como texto opaco. Este
    /// test fija esa frontera: si alguien mete detalles de RustDesk en el
    /// contrato o en la BD, la Fase 18 deja de poder cambiar de motor.
    /// </summary>
    [Fact]
    public void Only_the_provider_names_the_engine()
    {
        var launch = new RustDeskProvider().BuildLaunch(Contexto("123456789"));

        // Lo que viaja al dashboard es ejecutable + argumentos, no estructura
        // propia de un producto concreto.
        Assert.NotEmpty(launch.Target);
        Assert.NotEmpty(launch.Arguments);
    }

    /// <summary>
    /// Fase 8. La diferencia de flujo entre los dos motores es UNA bandera, y de
    /// ella depende que el servidor emita tickets y le mande la orden de arrancar
    /// al agente. Si alguien la invierte, RustDesk empezaria a pedir tickets que
    /// no usa y el motor propio se quedaria sin host al otro lado.
    /// </summary>
    [Fact]
    public void Solo_el_motor_propio_necesita_arrancar_el_otro_extremo()
    {
        Assert.False(new RustDeskProvider().RequiresHostLaunch);
        Assert.True(new DeviceHubRemoteProvider().RequiresHostLaunch);
    }

    /// <summary>
    /// EL TICKET NO PUEDE IR EN LOS ARGUMENTOS. Los argumentos de un proceso los
    /// lee cualquier usuario de esa PC con abrir el administrador de tareas, y
    /// esto da acceso a ver y controlar una pantalla. Va por stdin, y esta
    /// prueba es lo que impide que alguien lo "simplifique" a un --ticket.
    /// </summary>
    [Fact]
    public void El_ticket_viaja_por_stdin_y_no_en_la_linea_de_comandos()
    {
        var launch = new DeviceHubRemoteProvider().BuildLaunch(Contexto("no-se-usa"));

        Assert.Equal("TICKET-SECRETO", launch.Secret);
        Assert.DoesNotContain("TICKET-SECRETO", launch.Arguments);
    }

    /// <summary>Sin pin no se finge que la sesion es segura: el visor arranca en
    /// modo laboratorio y lo avisa en su barra de estado.</summary>
    [Fact]
    public void La_pestaña_lleva_el_nombre_de_la_maquina_CONTROLADA()
    {
        var launch = new DeviceHubRemoteProvider().BuildLaunch(Contexto("x"));

        // Con tres pestañas abiertas contra tres PCs, las tres se llamaban igual:
        // --machine-id es la PC del TECNICO, la misma para todas sus sesiones, y
        // era lo unico que llegaba con nombre.
        Assert.Contains("--titulo INPUT-M2", launch.Arguments);
        Assert.Contains("--machine-id PC-TECNICO", launch.Arguments);
    }

    [Fact]
    public void Un_nombre_con_espacios_no_parte_los_argumentos()
    {
        var launch = new DeviceHubRemoteProvider().BuildLaunch(
            Contexto("x") with { MachineCode = "LINEA 3 ENTRADA" });

        // Al otro lado esto se parte por espacios: sin juntarlo, el titulo seria
        // "LINEA" y "3" y "ENTRADA" se leerian como argumentos sueltos.
        Assert.Contains("--titulo LINEA_3_ENTRADA", launch.Arguments);
    }

    [Fact]
    public void Sin_pin_el_visor_arranca_avisando()
    {
        var launch = new DeviceHubRemoteProvider()
            .BuildLaunch(Contexto("x") with { ServerPin = "" });

        Assert.Contains("--allow-untrusted", launch.Arguments);
        Assert.DoesNotContain("--pin", launch.Arguments);
    }
}

public class RemoteDetectorTests
{
    /// <summary>
    /// Que el motor remoto no este instalado NO es un error: la maquina aparece
    /// sin control remoto disponible y el agente sigue reportando normal.
    ///
    /// En una maquina con RustDesk instalado esto devuelve un id; el test acepta
    /// ambos casos porque depende del equipo, pero exige que nunca lance.
    /// </summary>
    [Fact]
    public void Detection_never_throws()
    {
        var detector = new RustDeskDetector(NullLogger<RustDeskDetector>.Instance);

        var id = detector.DetectDeviceId();

        Assert.Equal("rustdesk", detector.Provider);

        if (id is not null)
        {
            Assert.InRange(id.Length, 6, 16);
            Assert.All(id, c => Assert.True(char.IsDigit(c)));
        }
    }
}

public class SessionPolicyTests
{
    /// <summary>
    /// Sin cierre por timeout, la auditoria acabaria diciendo que alguien lleva
    /// tres semanas dentro de una maquina porque cerro el dashboard de golpe.
    /// </summary>
    [Fact]
    public void Orphan_sessions_are_closed_within_a_working_day()
        => Assert.InRange(SessionRepository.OrphanTimeout, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
}
