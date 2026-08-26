namespace DeviceHub.Server.Data;

/// <summary>
/// Cuanto puede durar un codigo de enrolamiento.
///
/// EN UN SOLO SITIO PORQUE HAY DOS CAMINOS. Se emite por el dashboard
/// (AdminGrpcService) y por linea de comandos (--enrollment-code), y los dos
/// tenian el 480 escrito a mano. Con dos copias, subir el tope en una deja la
/// otra recortando en silencio: se pide un dia, se acepta sin protestar, y el
/// codigo vence a las ocho horas.
/// </summary>
public static class EnrollmentLimits
{
    /// <summary>Un dia. Una ronda de instalaciones por la planta no siempre cabe
    /// en un turno.
    ///
    /// Que haya tope no es burocracia: el codigo viaja DENTRO del instalador, y
    /// su caducidad es lo unico que limita para que sirve ese .exe el dia que se
    /// queda olvidado en una USB.</summary>
    public const int MaxMinutes = 1440;
}
