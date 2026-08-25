using System.Windows;
using System.Windows.Controls;

namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// El panel del mosaico. Sustituye al UniformGrid.
///
/// El UniformGrid reparte CELDAS y deja que cada sesion encaje su imagen dentro,
/// asi que el sobrante sale como bandas negras entre filas. Este reparte los
/// huecos ya con la forma de lo que se va a pintar -- ver Pared -- y junta todo
/// el sobrante en un margen alrededor de la pared.
///
/// UNA SOLA VISIBLE OCUPA TODO. Es la vista normal, y ahi manda el menu de Vista
/// de la sesion: encajarla aqui le quitaria el sitio a su propio scroll cuando
/// alguien pide 150 % o tamano original.
/// </summary>
public sealed class ParedDeMosaicos : Panel
{
    /// <summary>Ancho/alto de las pantallas remotas. Lo pone la consola con la
    /// media de las sesiones abiertas.</summary>
    public double Aspecto { get; set; } = 16.0 / 9.0;

    private Pared.Hueco[] Repartir(Size hueco)
    {
        var visibles = 0;

        foreach (UIElement hijo in InternalChildren)
        {
            if (hijo.Visibility != Visibility.Collapsed)
                visibles++;
        }

        return Pared.Repartir(visibles, hueco.Width, hueco.Height, Aspecto);
    }

    protected override Size MeasureOverride(Size disponible)
    {
        // Sin limite no se puede repartir nada: pasa dentro de un ScrollViewer,
        // que ofrece infinito. Aqui no hay ninguno, pero medir con infinito y
        // devolverlo cuelga el layout, asi que se acota.
        var ancho = double.IsInfinity(disponible.Width) ? 0 : disponible.Width;
        var alto = double.IsInfinity(disponible.Height) ? 0 : disponible.Height;

        var huecos = Repartir(new Size(ancho, alto));
        var siguiente = 0;

        foreach (UIElement hijo in InternalChildren)
        {
            // Las escondidas se miden con el hueco entero, igual que hacia el
            // UniformGrid: son ventanas Win32 con su cadena de intercambio
            // dentro, y dejarlas en cero las obligaria a rehacerla al volver.
            var suyo = new Size(ancho, alto);

            if (hijo.Visibility != Visibility.Collapsed && siguiente < huecos.Length)
            {
                var hueco = huecos[siguiente++];
                suyo = new Size(hueco.Ancho, hueco.Alto);
            }

            hijo.Measure(suyo);
        }

        return new Size(ancho, alto);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var huecos = Repartir(final);
        var siguiente = 0;

        foreach (UIElement hijo in InternalChildren)
        {
            if (hijo.Visibility != Visibility.Collapsed && siguiente < huecos.Length)
            {
                var hueco = huecos[siguiente++];
                hijo.Arrange(new Rect(hueco.X, hueco.Y, hueco.Ancho, hueco.Alto));
            }
            else
            {
                hijo.Arrange(new Rect(0, 0, final.Width, final.Height));
            }
        }

        return final;
    }
}
