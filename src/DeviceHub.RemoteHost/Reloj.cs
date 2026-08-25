using System.Diagnostics;

namespace DeviceHub.RemoteHost;

/// <summary>
/// EL RELOJ DE LA SESION. Uno solo, y todo lo marca contra el.
///
/// Es la pieza que no se puede añadir despues. Si el video marca sus tiempos
/// contra un reloj y el sonido contra otro, ninguna sincronia posterior los
/// puede cuadrar: el desfase nace en el origen y solo se puede medir, no
/// corregir.
///
/// Y ya habia un problema sin audio de por medio. Cada captura contaba desde
/// que se creo ESE objeto, asi que al relevar a GDI o al rehacer el
/// codificador el reloj volvia a cero y el visor recibia marcas que
/// RETROCEDIAN. Para el video eso pasaba desapercibido porque nadie las usaba
/// para decidir cuando pintar; en cuanto entra el sonido, deja de pasar
/// desapercibido.
///
/// Monotono y desde el arranque del proceso. RemoteHost vive lo que dura una
/// sesion, asi que los valores empiezan pequeños y no hay riesgo de desbordar
/// nada: a microsegundos, un long aguanta doscientos noventa mil años.
/// </summary>
public static class Reloj
{
    private static readonly long Inicio = Stopwatch.GetTimestamp();

    /// <summary>Microsegundos desde que arranco la sesion.</summary>
    public static long Ahora()
        => (Stopwatch.GetTimestamp() - Inicio) * 1_000_000L / Stopwatch.Frequency;

    /// <summary>
    /// Microsegundos entre una marca de QueryPerformanceCounter y ahora.
    ///
    /// DXGI entrega LastPresentTime en unidades de QPC, que es el mismo reloj
    /// que Stopwatch.GetTimestamp, asi que se restan directamente. Existe aqui
    /// para que la conversion viva en un solo sitio.
    /// </summary>
    public static long DesdeQpc(long marcaQpc)
        => (Stopwatch.GetTimestamp() - marcaQpc) * 1_000_000L / Stopwatch.Frequency;
}
