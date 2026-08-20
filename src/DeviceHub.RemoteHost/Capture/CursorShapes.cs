namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Convierte la forma del puntero que entrega DXGI a BGRA plano. Fase 11.
///
/// Se convierte AQUI y no en el visor a proposito: el contrato dice `bytes bgra`
/// desde la Fase 4, asi que los tres formatos raros de Windows se quedan del
/// lado que ya sabe de Windows y el visor solo pinta.
///
/// Aritmetica pura y sin GPU para poder probarla: el desplazamiento de las
/// mascaras es donde se falla, y cuando se falla no hay error -- sale un cursor
/// invertido, desplazado o invisible.
/// </summary>
public static class CursorShapes
{
    /// <summary>1bpp con DOS mascaras apiladas: arriba AND, abajo XOR. El alto
    /// del bufer es el doble del cursor real.</summary>
    public const uint Monocromo = 1;

    /// <summary>32bpp BGRA con alfa de verdad. El caso comodo.</summary>
    public const uint Color = 2;

    /// <summary>32bpp donde el alfa NO es alfa: es una mascara. 0 = copiar el
    /// color, 0xFF = XOR contra la pantalla.</summary>
    public const uint ColorEnmascarado = 4;

    /// <summary>
    /// Devuelve BGRA de arriba a abajo y el alto REAL del cursor, que en
    /// monocromo es la mitad del alto del bufer.
    /// </summary>
    public static byte[] ABgra(
        uint tipo, int ancho, int altoBufer, int pitch, ReadOnlySpan<byte> datos, out int alto)
    {
        if (tipo == Monocromo)
        {
            alto = altoBufer / 2;
            return Mono(ancho, alto, pitch, datos);
        }

        alto = altoBufer;
        return DesdeColor(tipo, ancho, alto, pitch, datos);
    }

    private static byte[] Mono(int ancho, int alto, int pitch, ReadOnlySpan<byte> datos)
    {
        var salida = new byte[ancho * alto * 4];

        for (var y = 0; y < alto; y++)
        {
            for (var x = 0; x < ancho; x++)
            {
                var desplazamiento = x >> 3;
                var bit = (byte)(0x80 >> (x & 7));

                var and = (datos[y * pitch + desplazamiento] & bit) != 0;
                var xor = (datos[(alto + y) * pitch + desplazamiento] & bit) != 0;

                // AND=1 y XOR=0 es transparente, y el array ya viene a cero.
                if (and && !xor)
                    continue;

                // AND=0 XOR=0 -> negro.  AND=0 XOR=1 -> blanco.
                //
                // AND=1 XOR=1 es "invierte la pantalla", que no se puede
                // representar en un cursor plano. Se aproxima a NEGRO porque es
                // el cuerpo de la barra de texto y el fondo mas comun debajo es
                // claro. Sobre fondo oscuro se pierde: es el techo conocido de
                // esta aproximacion.
                var claro = !and && xor;
                var valor = (byte)(claro ? 255 : 0);
                var o = (y * ancho + x) * 4;

                salida[o] = valor;
                salida[o + 1] = valor;
                salida[o + 2] = valor;
                salida[o + 3] = 255;
            }
        }

        return salida;
    }

    private static byte[] DesdeColor(uint tipo, int ancho, int alto, int pitch, ReadOnlySpan<byte> datos)
    {
        var salida = new byte[ancho * alto * 4];

        for (var y = 0; y < alto; y++)
        {
            for (var x = 0; x < ancho; x++)
            {
                var i = y * pitch + x * 4;
                var o = (y * ancho + x) * 4;

                var b = datos[i];
                var g = datos[i + 1];
                var r = datos[i + 2];
                var a = datos[i + 3];

                if (tipo == ColorEnmascarado)
                {
                    // Mascara puesta y color negro = XOR con cero = la pantalla
                    // no cambia. Eso es la parte TRANSPARENTE del cursor, y es la
                    // regla que se olvida: sin ella el cursor sale dentro de un
                    // rectangulo negro.
                    a = a != 0 && b == 0 && g == 0 && r == 0 ? (byte)0 : (byte)255;
                }

                salida[o] = b;
                salida[o + 1] = g;
                salida[o + 2] = r;
                salida[o + 3] = a;
            }
        }

        return salida;
    }
}

/// <summary>
/// Lo que se sabe del puntero en un instante. `Bgra` viene relleno SOLO cuando
/// la forma cambio; el resto de veces es null y el paquete son dos numeros.
/// </summary>
public sealed record CursorState(
    double X, double Y, bool Visible,
    byte[]? Bgra, int Ancho, int Alto, int HotspotX, int HotspotY, ulong FormaId);
