namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Cuanto se encoge la pantalla antes de codificarla en una PC sin GPU.
///
/// Aritmetica pura y separada del resto para poder probarla: un lado impar o un
/// redondeo al alza no dan error, dan un codificador que rechaza el tipo de
/// entrada mucho despues y por un motivo que no se parece a este.
/// </summary>
public static class Reducir
{
    /// <summary>
    /// Lado largo a partir del cual se reduce. Por debajo NO SE TOCA NADA.
    ///
    /// RustDesk en Windows no escala: le pasa al codificador la resolucion de
    /// captura tal cual. Su unico escalado vive en Android, se llama
    /// ENABLE_ANDROID_SOFTWARE_ENCODING_HALF_SCALE y es a la MITAD exacta.
    ///
    /// Aqui se reducia 1280x1024 a 960x768, que es una razon de 0.75 por vecino
    /// mas proximo: tira una de cada cuatro filas y columnas. Sobre el texto de
    /// una consola, con trazos de un pixel de ancho, eso no lo empeora un poco
    /// -- rompe las letras.
    ///
    /// Y se pago para nada. Medido en la maquina que motivo el escalado, la
    /// conversion se reparte en 9.3 ms de bajar la imagen y 4.0 de procesarla, y
    /// reducir SOLO abarata la segunda: la bajada es a resolucion completa
    /// porque Desktop Duplication entrega la pantalla entera. Unos ocho
    /// milisegundos de un presupuesto de cuarenta, a cambio de no poder leer.
    /// </summary>
    public const int LadoMaximo = 2000;

    /// <summary>
    /// El tamaño al que conviene codificar. Devuelve el mismo si ya cabe, que es
    /// el caso de cualquier pantalla normal.
    ///
    /// Cuando no cabe se parte por la MITAD, y se repite si hace falta: 2560 ->
    /// 1280, 3840 -> 1920. A la mitad exacta el vecino mas proximo toma un pixel
    /// de cada dos en cada eje, que es regular y se ve; a 0.75 el patron de lo
    /// que se tira cambia cada cuatro pixeles y el texto tiembla.
    ///
    /// Las dos medidas salen PARES siempre: NV12 guarda un par de croma por cada
    /// bloque de 2x2, asi que un lado impar no tiene representacion.
    /// </summary>
    public static (int Ancho, int Alto) Cabe(int ancho, int alto)
    {
        if (ancho <= 0 || alto <= 0)
            return (2, 2);

        while (Math.Max(ancho, alto) > LadoMaximo)
        {
            ancho /= 2;
            alto /= 2;
        }

        return (Par(ancho), Par(alto));
    }

    /// <summary>Par y nunca cero: 1 pixel de alto no es una imagen, pero 0 no es nada.</summary>
    private static int Par(int valor) => Math.Max(valor - (valor & 1), 2);
}
