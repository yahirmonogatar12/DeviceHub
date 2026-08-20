using Vortice;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// La caja que envuelve lo que cambio en un frame.
///
/// DXGI sabe exactamente que rectangulos se ensuciaron y hasta ahora no se lo
/// preguntabamos: cada frame se convertia entero de BGRA a NV12, dos millones
/// de pixeles, aunque lo unico que hubiera cambiado fuera el reloj de la barra
/// de tareas. En el propio codigo de RustDesk esta el comentario diciendo que
/// esto se puede hacer, y sin hacer.
///
/// SE DEVUELVE UNA CAJA Y NO LA LISTA. Convertir veinte rectangulos sueltos son
/// veinte VideoProcessorBlt con su cambio de estado cada uno, y a esa escala el
/// coste de mandar el trabajo se come lo que se ahorra en pixeles. Una sola
/// caja que los envuelva a todos es un blt, y en el caso normal -- escribir en
/// una ventana, un cursor parpadeando, un reloj -- esa caja es minuscula.
///
/// Clase suelta y pura porque el borde es facil de equivocar: NV12 submuestrea
/// el color 2x2, asi que un origen impar desplaza medio pixel de croma y tine
/// el borde de la zona actualizada. Eso no da error, da una franja de color
/// raro que aparece dias despues en una captura de pantalla.
/// </summary>
public static class ZonaSucia
{
    /// <summary>
    /// Envuelve todos los rectangulos, alinea a par y recorta a la pantalla.
    ///
    /// `null` significa CONVIERTE TODO, y es lo que se devuelve cuando no hay
    /// rectangulos: sin informacion no se adivina. Es la respuesta segura --
    /// convertir de mas cuesta tiempo, convertir de menos deja pixeles viejos.
    /// </summary>
    public static RawRect? Caja(ReadOnlySpan<RawRect> rectangulos, int ancho, int alto)
    {
        if (rectangulos.Length == 0 || ancho <= 0 || alto <= 0)
            return null;

        var izquierda = int.MaxValue;
        var arriba = int.MaxValue;
        var derecha = int.MinValue;
        var abajo = int.MinValue;

        foreach (var r in rectangulos)
        {
            // Normalizado: un rectangulo con los lados al reves envolveria media
            // pantalla si se suma tal cual.
            izquierda = Math.Min(izquierda, Math.Min(r.Left, r.Right));
            arriba = Math.Min(arriba, Math.Min(r.Top, r.Bottom));
            derecha = Math.Max(derecha, Math.Max(r.Left, r.Right));
            abajo = Math.Max(abajo, Math.Max(r.Top, r.Bottom));
        }

        // Al par HACIA FUERA: hacia dentro recortaria una fila de pixeles que si
        // cambio, y esa fila se quedaria con la imagen anterior.
        izquierda = Math.Max(izquierda & ~1, 0);
        arriba = Math.Max(arriba & ~1, 0);
        derecha = Math.Min((derecha + 1) & ~1, ancho);
        abajo = Math.Min((abajo + 1) & ~1, alto);

        if (derecha <= izquierda || abajo <= arriba)
            return null;

        // Si casi toda la pantalla cambio, se dice que todo: acotar el blt tiene
        // su propio coste en cambios de estado y a partir de aqui no compensa.
        if ((long)(derecha - izquierda) * (abajo - arriba) >= (long)ancho * alto * 9 / 10)
            return null;

        return new RawRect(izquierda, arriba, derecha, abajo);
    }
}
