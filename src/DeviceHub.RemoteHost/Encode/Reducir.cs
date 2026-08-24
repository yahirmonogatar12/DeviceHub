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
    /// Lado largo maximo cuando codifica la CPU.
    ///
    /// 960 y no 1280 porque el coste va con el AREA: 1280x1024 -> 960x768 es un
    /// 44% menos de pixeles que convertir y codificar. Y no menos de 960 porque
    /// por debajo de eso el texto de una consola deja de leerse, que es
    /// justamente para lo que se entra a estas maquinas.
    /// </summary>
    public const int LadoMaximo = 960;

    /// <summary>
    /// El tamaño al que conviene codificar. Devuelve el mismo si ya cabe.
    ///
    /// Las dos medidas salen PARES siempre: NV12 guarda un par de croma por cada
    /// bloque de 2x2, asi que un lado impar no tiene representacion.
    /// </summary>
    public static (int Ancho, int Alto) Cabe(int ancho, int alto)
    {
        var largo = Math.Max(ancho, alto);

        if (largo <= LadoMaximo || ancho <= 0 || alto <= 0)
            return (Par(ancho), Par(alto));

        // Se escala por el lado LARGO para que la relacion de aspecto aguante
        // igual en una pantalla apaisada que en una vertical.
        return (Par((int)((long)ancho * LadoMaximo / largo)),
                Par((int)((long)alto * LadoMaximo / largo)));
    }

    /// <summary>Par y nunca cero: 1 pixel de alto no es una imagen, pero 0 no es nada.</summary>
    private static int Par(int valor) => Math.Max(valor - (valor & 1), 2);
}
