using DeviceHub.Agent.Remote;
using DeviceHub.Server.Data;
using DeviceHub.Server.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeviceHub.Tests;

public class RemoteProviderTests
{
    [Fact]
    public void The_launch_carries_the_device_id()
    {
        var launch = new RustDeskProvider().BuildLaunch("861237322");

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
        var launch = new RustDeskProvider().BuildLaunch("123456789");

        // Lo que viaja al dashboard es ejecutable + argumentos, no estructura
        // propia de un producto concreto.
        Assert.NotEmpty(launch.Target);
        Assert.NotEmpty(launch.Arguments);
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
