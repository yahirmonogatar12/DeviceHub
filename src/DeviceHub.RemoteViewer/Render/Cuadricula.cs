namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Cuantas columnas y cuantas filas para el mosaico.
///
/// EL UniformGrid DE WPF NO SIRVE SOLO. Sin Rows ni Columns aplica una regla
/// fija -- ceil(raiz(n)) columnas -- que no mira ni la forma de la ventana ni la
/// de las pantallas remotas. Con seis sesiones da siempre 3x2, aunque la ventana
/// sea mas alta que ancha y ahi lo que cabe sea 2x3; y con TRES da 2x2, o sea
/// una celda entera desperdiciada teniendo 3x1 a mano.
///
/// Aqui se prueban todos los repartos y gana el que deje la imagen mas grande.
/// Como el aspecto es fijo, el ancho de la imagen encajada ordena igual que el
/// area: maximizar el lado es maximizar lo que se ve.
///
/// Sin nada de WPF a proposito, igual que Escalado: son diez lineas de
/// aritmetica y asi se prueban sin arrastrar la interfaz detras.
/// </summary>
public static class Cuadricula
{
    /// <summary>
    /// <paramref name="aspecto"/> es ancho/alto de lo que se pinta en cada
    /// celda. 16:9 por defecto, que es lo que tienen las PCs de planta, pero se
    /// pasa el real: un servidor con un monitor 5:4 cambia que reparto conviene.
    /// </summary>
    public static (int Columnas, int Filas) Repartir(
        int pantallas, double ancho, double alto, double aspecto = 16.0 / 9.0)
    {
        if (pantallas <= 1)
            return (1, 1);

        // Antes del primer Measure el hueco todavia no existe. Devolver la regla
        // de WPF -- y no 1x1 -- deja el mosaico bien de entrada aunque el
        // SizeChanged que lo afina llegue una vuelta despues.
        if (ancho <= 0 || alto <= 0 || aspecto <= 0)
        {
            var columnas = (int)Math.Ceiling(Math.Sqrt(pantallas));
            return (columnas, (int)Math.Ceiling((double)pantallas / columnas));
        }

        var mejores = 1;
        var mayor = 0.0;

        for (var columnas = 1; columnas <= pantallas; columnas++)
        {
            var filas = (int)Math.Ceiling((double)pantallas / columnas);

            // Lo que mide la imagen dentro de la celda conservando el aspecto:
            // o la limita el ancho de la celda, o su alto.
            var lado = Math.Min(ancho / columnas, alto / filas * aspecto);

            // Estricto: en un empate gana el reparto de MENOS columnas, que es
            // el mas cuadrado. Con cuatro pantallas en una ventana ancha, 2x2 y
            // 3x2 dan la misma imagen y 2x2 no deja un hueco vacio.
            if (lado > mayor)
            {
                mayor = lado;
                mejores = columnas;
            }
        }

        return (mejores, (int)Math.Ceiling((double)pantallas / mejores));
    }
}
