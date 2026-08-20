using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Sigue el puntero que DXGI entrega junto a cada frame. Fase 11.
///
/// Desktop Duplication NO compone el cursor en la imagen del escritorio: lo da
/// aparte, y hasta esta fase se tiraba. Por eso el escritorio remoto llegaba
/// literalmente sin raton, y lo que el tecnico veia encima del video era su
/// PROPIO cursor -- coincidia porque el lo estaba moviendo.
///
/// Compartido por las dos capturas: una pantalla y el escritorio virtual
/// compuesto. La unica diferencia es el desplazamiento de la esquina.
/// </summary>
public sealed class CursorTracker
{
    private CursorState? _pendiente;
    private byte[]? _bgra;
    private int _ancho, _alto, _hotX, _hotY;
    private ulong _formaId;

    /// <summary>Lo ultimo que se sabe, o null si no cambio desde la ultima
    /// llamada. Lo consume el hilo de captura y lo vacia.</summary>
    public CursorState? Tomar()
    {
        var estado = _pendiente;
        _pendiente = null;
        return estado;
    }

    public void Anotar(
        OutduplFrameInfo info, IDXGIOutputDuplication duplicacion,
        int ancho, int alto, int desplazamientoX, int desplazamientoY)
    {
        // 0 = el puntero no cambio en esta vuelta, ni de sitio ni de forma.
        if (info.LastMouseUpdateTime == 0)
            return;

        var nueva = info.PointerShapeBufferSize > 0
                    && LeerForma(duplicacion, (int)info.PointerShapeBufferSize);

        // Normalizadas y nunca en pixeles: la resolucion remota puede cambiar a
        // media sesion, y unos pixeles de antes del cambio apuntarian a otro
        // sitio. El desplazamiento coloca el puntero dentro del lienzo cuando se
        // capturan varias pantallas a la vez.
        var x = (info.PointerPosition.Position.X + desplazamientoX) / (double)Math.Max(ancho, 1);
        var y = (info.PointerPosition.Position.Y + desplazamientoY) / (double)Math.Max(alto, 1);

        _pendiente = new CursorState(
            Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), info.PointerPosition.Visible,
            nueva ? _bgra : null, _ancho, _alto, _hotX, _hotY, _formaId);
    }

    private bool LeerForma(IDXGIOutputDuplication duplicacion, int tamano)
    {
        var bufer = Marshal.AllocHGlobal(tamano);

        try
        {
            duplicacion.GetFramePointerShape((uint)tamano, bufer, out _, out var forma);

            var crudo = new byte[tamano];
            Marshal.Copy(bufer, crudo, 0, tamano);

            _bgra = CursorShapes.ABgra(
                (uint)forma.Type, (int)forma.Width, (int)forma.Height, (int)forma.Pitch,
                crudo, out var alto);

            _ancho = (int)forma.Width;
            _alto = alto;
            _hotX = forma.HotSpot.X;
            _hotY = forma.HotSpot.Y;

            // Un id creciente y nada mas: el visor lo usa para no reconstruir el
            // cursor de Windows cuando llega la misma forma otra vez.
            _formaId++;

            return true;
        }
        catch (SharpGenException)
        {
            // Que falle una forma no puede tumbar la captura: se sigue con la
            // anterior, que es la que el tecnico ya esta viendo.
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(bufer);
        }
    }
}
