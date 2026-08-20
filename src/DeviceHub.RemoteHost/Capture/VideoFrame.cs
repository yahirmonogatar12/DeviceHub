using Vortice.Direct3D11;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Un frame del escritorio, todavia en la GPU.
///
/// POSESION -- lo mas importante de esta clase:
///
/// AcquireNextFrame cede temporalmente la superficie al capturador, y esta clase
/// REPRESENTA esa cesion. Despues de ReleaseFrame la superficie deja de ser
/// valida para operaciones DirectX, asi que:
///
///   - <see cref="Dispose"/> es el UNICO sitio que llama a ReleaseFrame.
///   - El consumidor termina TODO uso GPU de la textura antes de disponer.
///   - Antes del siguiente AcquireNextFrame, este frame tiene que estar dispuesto.
///
/// Lo que NO se puede hacer, que es justo lo que sugiere el instinto de liberar
/// siempre en un finally:
///
///     try { AcquireNextFrame(...); return new VideoFrame(texture); }
///     finally { duplication.ReleaseFrame(); }   // devuelve una textura muerta
///
/// Ese codigo compila, parece prudente y entrega basura.
///
/// Si en la Fase 2 hace falta desacoplar captura y encoder con colas, la salida
/// NO es alargar la vida de este frame: es copiar en GPU a una textura propia y
/// liberar el frame de Desktop Duplication en el acto.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private readonly Action _release;
    private bool _disposed;

    internal VideoFrame(
        ID3D11Texture2D texture, int width, int height,
        ulong frameId, long timestampUs, bool desktopChanged, Action release,
        Vortice.RawRect? dirty = null)
    {
        Dirty = dirty;
        Texture = texture;
        Width = width;
        Height = height;
        FrameId = frameId;
        TimestampUs = timestampUs;
        DesktopChanged = desktopChanged;
        _release = release;
    }

    /// <summary>
    /// La caja que envuelve lo que cambio, o null si no se sabe.
    ///
    /// Null NO significa "no cambio nada" -- para eso esta DesktopChanged --
    /// sino "convierte todo". Es la respuesta segura cuando DXGI no da
    /// metadatos: convertir de mas cuesta tiempo, convertir de menos deja
    /// pixeles viejos en pantalla.
    /// </summary>
    public Vortice.RawRect? Dirty { get; }

    /// <summary>Valida solo hasta <see cref="Dispose"/>.</summary>
    public ID3D11Texture2D Texture { get; }

    public int Width { get; }
    public int Height { get; }
    public ulong FrameId { get; }

    /// <summary>Microsegundos desde el arranque del capturador, reloj monotono.</summary>
    public long TimestampUs { get; }

    /// <summary>
    /// false = la imagen del escritorio NO cambio; solo se movio el puntero, y
    /// la textura trae el contenido anterior.
    ///
    /// Es la diferencia entre medir frames de verdad y contar movimientos de
    /// raton como si fueran actividad de pantalla.
    /// </summary>
    public bool DesktopChanged { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Texture.Dispose();
        _release();
    }
}
