namespace DeviceHub.RemoteViewer;

/// <summary>
/// Donde cae una pestaña que se esta arrastrando.
///
/// Aparte de la ventana porque es aritmetica y tiene una trampa: si se compara
/// contra el BORDE de cada ficha en vez de contra su centro, arrastrar una
/// ancha sobre una estrecha la hace saltar de ida y vuelta sin parar -- el
/// intercambio mueve la ficha bajo el cursor, el cursor vuelve a caer en la
/// otra, y se intercambian otra vez. Con el centro no puede pasar: despues de
/// intercambiar, el cursor esta del lado bueno.
/// </summary>
public static class Reordenar
{
    /// <summary>
    /// En que posicion cae `x`, medida desde el inicio de la franja. Devuelve
    /// -1 si no hay pestañas.
    /// </summary>
    public static int IndiceEn(IReadOnlyList<double> anchos, double x)
    {
        if (anchos.Count == 0)
            return -1;

        double izquierda = 0;

        for (var i = 0; i < anchos.Count; i++)
        {
            if (x < izquierda + anchos[i] / 2)
                return i;

            izquierda += anchos[i];
        }

        return anchos.Count - 1;
    }
}
