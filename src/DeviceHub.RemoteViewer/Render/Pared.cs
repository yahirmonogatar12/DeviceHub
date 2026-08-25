namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Donde va exactamente cada mosaico.
///
/// LA REJILLA UNIFORME REPARTE CELDAS, NO IMAGENES, y ahi nacen los cuadros
/// negros. Una celda de 640x487 con una pantalla 16:9 dentro pinta 640x360 y
/// deja 127 px negros repartidos arriba y abajo; y cuando las pantallas no
/// llenan la ultima fila -- cinco en 3x2 -- la celda que sobra queda como un
/// rectangulo negro suelto en una esquina.
///
/// Aqui se reparte la IMAGEN: cada hueco mide justo lo que mide lo que se pinta
/// dentro, las filas van centradas y la ultima, si lleva menos, va centrada
/// tambien. El negro deja de ser cuadros sueltos y pasa a ser un margen
/// alrededor.
///
/// LO QUE ESTO NO HACE ES INVENTAR ESPACIO. Cinco pantallas 16:9 en una ventana
/// 1920x975 ocupan como mucho el 61 % del hueco, y seis el 74 %: el resto es la
/// diferencia entre la forma de la ventana y la de lo que se mira. Solo se
/// recupera recortando los bordes de cada escritorio o deformandolo, y las dos
/// cosas se descartaron en la Fase 20 por la misma razon -- en una PC de planta
/// se nota enseguida en el texto.
/// </summary>
public static class Pared
{
    public readonly record struct Hueco(double X, double Y, double Ancho, double Alto);

    public static Hueco[] Repartir(
        int pantallas, double ancho, double alto, double aspecto = 16.0 / 9.0)
    {
        if (pantallas <= 0)
            return [];

        // Una sola ocupa TODO y sin conservar aspecto: en vista normal manda el
        // menu de Vista de la sesion -- original, 150 %, 200 % -- y encajarla
        // aqui le quitaria el sitio a su propio scroll.
        if (pantallas == 1)
            return [new Hueco(0, 0, ancho, alto)];

        if (ancho <= 0 || alto <= 0 || aspecto <= 0)
            return new Hueco[pantallas];

        var (columnas, filas) = Cuadricula.Repartir(pantallas, ancho, alto, aspecto);

        // PEGADOS, sin separacion. Se probo con 4 px entre mosaicos para que
        // seis escritorios oscuros no se leyeran como una mancha, y el usuario
        // lo rechazo: prefiere la pared continua.
        var anchoMosaico = Math.Max(Math.Min(ancho / columnas, alto / filas * aspecto), 1);
        var altoMosaico = Math.Max(anchoMosaico / aspecto, 1);

        var arriba = (alto - altoMosaico * filas) / 2;
        var huecos = new Hueco[pantallas];

        for (var i = 0; i < pantallas; i++)
        {
            var fila = i / columnas;
            var columna = i % columnas;

            // LA ULTIMA FILA PUEDE LLEVAR MENOS, y va centrada. Sin esto, cinco
            // pantallas dejan el hueco de la sexta como un cuadro negro pegado a
            // la derecha; centrando, ese hueco se parte en dos margenes iguales
            // y la pared se lee como algo hecho a proposito.
            var enLaFila = Math.Min(columnas, pantallas - fila * columnas);
            var izquierda = (ancho - anchoMosaico * enLaFila) / 2;

            huecos[i] = new Hueco(
                izquierda + columna * anchoMosaico,
                arriba + fila * altoMosaico,
                anchoMosaico,
                altoMosaico);
        }

        return huecos;
    }
}
