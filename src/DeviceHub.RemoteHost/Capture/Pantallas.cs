using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>Una pantalla conectada al escritorio. Fase 22.</summary>
/// <param name="Id">Indice plano sobre todas las salidas de todos los adaptadores.
/// Es lo que el visor manda de vuelta para pedir una; <see cref="Pantallas.Todas"/>
/// pide el escritorio virtual entero.</param>
public sealed record Pantalla(
    int Id, int AdapterIndex, int OutputIndex, string Nombre, string Adaptador,
    int X, int Y, int Ancho, int Alto, bool Primaria);

public static class Pantallas
{
    /// <summary>El escritorio virtual entero, con todos los monitores compuestos
    /// en una sola imagen. No es una pantalla real, por eso el id negativo.</summary>
    public const int Todas = -1;

    /// <summary>
    /// Las pantallas conectadas, en el orden en que DXGI las enumera.
    ///
    /// Se relee cada vez y no se cachea: enchufar o desenchufar un monitor no
    /// avisa, y una lista vieja manda a duplicar una salida que ya no existe.
    /// </summary>
    public static IReadOnlyList<Pantalla> Listar()
    {
        var pantallas = new List<Pantalla>();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint a = 0; factory.EnumAdapters1(a, out var adaptador).Success && adaptador is not null; a++)
        {
            using (adaptador)
            {
                var nombreAdaptador = adaptador.Description.Description.Trim();

                for (uint o = 0; adaptador.EnumOutputs(o, out var salida).Success && salida is not null; o++)
                {
                    using (salida)
                    {
                        var d = salida.Description;

                        // Una salida sin escritorio es un conector vacio. Ofrecerla
                        // solo sirve para que alguien la elija y la sesion falle.
                        if (!d.AttachedToDesktop)
                            continue;

                        var caja = d.DesktopCoordinates;

                        pantallas.Add(new Pantalla(
                            pantallas.Count, (int)a, (int)o,
                            d.DeviceName, nombreAdaptador,
                            caja.Left, caja.Top,
                            caja.Right - caja.Left, caja.Bottom - caja.Top,

                            // La principal es la que tiene la esquina en 0,0. No hay
                            // bandera para esto en DXGI, y es la definicion que usa
                            // Windows para colocar el escritorio virtual.
                            caja.Left == 0 && caja.Top == 0));
                    }
                }
            }
        }

        return pantallas;
    }

    /// <summary>
    /// La caja que envuelve a todas: el escritorio virtual.
    ///
    /// Separada y pura porque es donde se equivoca uno. El monitor de la
    /// IZQUIERDA tiene X negativa en Windows, asi que el ancho no es la suma de
    /// los anchos ni el borde derecho es el maximo de los anchos, y con un solo
    /// monitor todas las formulas equivocadas dan el resultado correcto -- el
    /// fallo no aparece hasta que alguien enchufa el segundo.
    /// </summary>
    public static (int X, int Y, int Ancho, int Alto) Envolvente(
        IReadOnlyCollection<(int X, int Y, int Ancho, int Alto)> cajas)
    {
        if (cajas.Count == 0)
            throw new ArgumentException("No hay ninguna pantalla que envolver.", nameof(cajas));

        var x = cajas.Min(c => c.X);
        var y = cajas.Min(c => c.Y);

        return (x, y, cajas.Max(c => c.X + c.Ancho) - x, cajas.Max(c => c.Y + c.Alto) - y);
    }
}
