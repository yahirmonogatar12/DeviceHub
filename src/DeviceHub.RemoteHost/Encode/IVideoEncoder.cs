using DeviceHub.RemoteHost.Capture;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>Un frame ya codificado, listo para trocear y mandar por el cable.</summary>
public sealed record EncodedFrame(
    ulong Sequence, long TimestampUs, bool IsKeyFrame, int Width, int Height, byte[] Payload);

/// <summary>
/// De donde salio el codificador. `Hardware` es lo que decide si una PC de
/// planta puede transmitir sin robarle CPU al software de test.
/// </summary>
public sealed record VideoEncoderCapabilities(
    string Name, bool Hardware, bool Asynchronous, string InputFormat,
    int Width, int Height, int FramesPerSecond, int BitrateBitsPerSecond);

public sealed class VideoEncoderUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IVideoEncoder : IDisposable
{
    VideoEncoderCapabilities Capabilities { get; }

    /// <summary>
    /// Frames que no se pudieron meter porque el codificador no los pedia. Es la
    /// medida honesta de "no da abasto": si crece, el encoder va por detras de la
    /// captura.
    /// </summary>
    long Dropped { get; }

    /// <summary>
    /// Un frame de entrada puede producir cero, uno o varios de salida: el
    /// codificador tiene su propia cola. Devolver una lista y no un frame evita
    /// mentir sobre esa relacion.
    ///
    /// La textura del VideoFrame solo se usa DURANTE la llamada; al volver, el
    /// llamante puede disponerlo.
    /// </summary>
    IReadOnlyList<EncodedFrame> Encode(VideoFrame frame, CancellationToken cancellationToken);
}
