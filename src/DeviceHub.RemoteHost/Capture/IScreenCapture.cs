namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// El escritorio no se puede capturar en esta maquina, y no por un fallo
/// transitorio: no hay monitor conectado, o el proceso corre donde no hay
/// escritorio que duplicar (sesion 0, escritorio seguro, sesion bloqueada).
///
/// Se distingue de una excepcion cualquiera porque el llamante tiene que
/// reportarlo como motivo de sesion fallida, no reintentar en bucle.
/// </summary>
public sealed class ScreenCaptureUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IScreenCapture : IDisposable
{
    /// <summary>Nombre de la GPU. Para el diagnostico, no para decidir nada.</summary>
    string Adapter { get; }

    /// <summary>Salida duplicada, p.ej. \\.\DISPLAY1.</summary>
    string Output { get; }

    int Width { get; }
    int Height { get; }

    /// <summary>Veces que AcquireNextFrame expiro sin novedades en pantalla.</summary>
    long Timeouts { get; }

    /// <summary>Veces que hubo que recrear el duplicador por DXGI_ERROR_ACCESS_LOST.</summary>
    long AccessLostRecoveries { get; }

    /// <summary>Cambios de resolucion detectados.</summary>
    long ResolutionChanges { get; }

    /// <summary>
    /// Presentaciones que DXGI agrupo porque no ibamos a su ritmo. Es el numero
    /// honesto de frames perdidos en la captura: si sale alto, el consumidor
    /// tarda mas de lo que dura un frame.
    /// </summary>
    long Dropped { get; }

    /// <summary>
    /// Devuelve null si no hay frame nuevo (timeout) o si hubo que recrear el
    /// duplicador. Null NO es un error, y por eso no se cuenta como frame:
    /// con la pantalla quieta Desktop Duplication no entrega 30 imagenes por
    /// segundo, y tratar los timeouts como frames inflaria los FPS.
    ///
    /// La llamada nativa es sincrona; la forma async existe para que el
    /// encoder de la Fase 2 pueda encadenarse sin cambiar la interfaz.
    ///
    /// No es reentrante: hay que disponer el frame anterior antes de pedir otro.
    /// </summary>
    Task<VideoFrame?> CaptureAsync(CancellationToken cancellationToken);
}
