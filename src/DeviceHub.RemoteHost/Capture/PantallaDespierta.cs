using System.Runtime.InteropServices;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// La pantalla de la PC controlada no se apaga mientras dura la sesion, y si
/// estaba apagada se enciende.
///
/// Con la pantalla en ahorro de energia Windows no presenta nada, y DXGI sin
/// presentaciones no entrega su primer frame: a los 3 s la sesion se relevaba
/// por GDI y entraba CONGELADA. Lo mismo a media sesion, si nadie tocaba nada el
/// tiempo de apagado: la imagen dejaba de llegar sin que nada lo explicara.
///
/// Es lo que hace cualquier control remoto: mientras alguien mira, la maquina
/// esta despierta. Al terminar la sesion se suelta y vuelve a su ahorro normal.
/// </summary>
public static class PantallaDespierta
{
    public static void Mientras(CancellationToken sesion)
        => new Thread(() =>
        {
            // POR HILO: Windows ata la peticion al hilo que la hace y la suelta
            // cuando ese hilo termina. Por eso un hilo propio que vive lo que la
            // sesion, y no el que arranca y sigue con otra cosa.
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);

            sesion.WaitHandle.WaitOne();

            SetThreadExecutionState(EsContinuous);
        })
        {
            IsBackground = true,
            Name = "pantalla despierta"
        }.Start();

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint estado);
}
