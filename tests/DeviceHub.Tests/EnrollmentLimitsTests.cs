using Xunit;
using DeviceHub.Server.Data;

namespace DeviceHub.Tests;

/// <summary>
/// El tope de caducidad de un codigo de enrolamiento.
///
/// Existe este test por lo que paso: el 480 estaba escrito a mano en los DOS
/// caminos que emiten codigos -- el dashboard y la linea de comandos -- y pedir
/// un dia se aceptaba sin protestar y vencia a las ocho horas.
/// </summary>
public class EnrollmentLimitsTests
{
    [Fact]
    public void Un_dia_entero_cabe()
        => Assert.True(EnrollmentLimits.MaxMinutes >= 24 * 60);

    [Fact]
    public void Pero_sigue_habiendo_tope()
    {
        // El codigo viaja DENTRO del instalador: su caducidad es lo unico que
        // limita para que sirve ese .exe si se queda olvidado en una USB.
        Assert.True(EnrollmentLimits.MaxMinutes <= 7 * 24 * 60);
    }
}
