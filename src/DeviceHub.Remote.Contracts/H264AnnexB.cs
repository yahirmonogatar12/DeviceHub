namespace DeviceHub.Remote.Contracts;

/// <summary>Una imagen completa dentro del flujo, con sus NAL de cabecera.</summary>
public readonly record struct AccessUnit(int Offset, int Length, bool KeyFrame);

/// <summary>
/// Trocea un flujo H.264 Annex-B en unidades de acceso.
///
/// Vive aqui, en los contratos, y no en el visor: la Fase 5 necesita exactamente
/// esta frontera para trocear por frame completo y no por chunk suelto. Es
/// aritmetica de bytes, sin GPU ni Media Foundation, asi que se prueba en CI.
///
/// Un decodificador acepta varios NAL en una misma muestra, pero no acepta media
/// imagen ni dos imagenes juntas: la marca de tiempo se pega a la muestra, y si
/// dentro van dos frames uno de los dos la pierde.
/// </summary>
public static class H264AnnexB
{
    /// <summary>Posicion del siguiente prefijo 00 00 01, o -1.</summary>
    private static int Next(ReadOnlySpan<byte> flujo, int desde)
    {
        for (var i = Math.Max(desde, 0); i + 2 < flujo.Length; i++)
        {
            if (flujo[i] == 0 && flujo[i + 1] == 0 && flujo[i + 2] == 1)
                return i;
        }

        return -1;
    }

    public static List<AccessUnit> Split(ReadOnlySpan<byte> flujo)
    {
        var unidades = new List<AccessUnit>();

        var inicio = -1;        // donde empieza la unidad en curso
        var conImagen = false;  // ya lleva al menos una rebanada
        var clave = false;

        var p = Next(flujo, 0);

        while (p >= 0)
        {
            var siguiente = Next(flujo, p + 3);
            var nal = p + 3;

            // El prefijo puede ser de 3 o 4 bytes y la busqueda encuentra los tres
            // ultimos. Sin recuperar el cero de delante, la unidad empezaria en
            // mitad de su propio prefijo: el flujo sigue siendo valido -- 00 00 01
            // basta -- pero las unidades dejarian de cubrir el archivo entero y el
            // primer byte no llegaria al decodificador.
            var codigo = p > 0 && flujo[p - 1] == 0 ? p - 1 : p;

            if (nal >= flujo.Length)
                break;

            var tipo = flujo[nal] & 0x1F;

            // Que NAL pueden ABRIR una unidad de acceso nueva.
            //
            // Para una rebanada (1 = normal, 5 = IDR) la condicion es que sea la
            // PRIMERA de su imagen. Eso se lee sin descodificar nada: el primer
            // campo de la cabecera es first_mb_in_slice como ue(v), y un ue(v)
            // que vale cero es un unico bit a 1. O sea, basta mirar el bit alto
            // del byte siguiente a la cabecera del NAL.
            //
            // Sin esa comprobacion, una imagen partida en varias rebanadas -- que
            // es lo normal en los codificadores de hardware -- se trocearia en
            // tantas unidades como rebanadas, y el decodificador recibiria
            // imagenes incompletas.
            var abre = tipo switch
            {
                9 or 7 or 8 or 6 => true,                                    // AUD, SPS, PPS, SEI
                1 or 5 => nal + 1 < flujo.Length && (flujo[nal + 1] & 0x80) != 0,
                _ => false
            };

            if (abre && conImagen && inicio >= 0)
            {
                unidades.Add(new AccessUnit(inicio, codigo - inicio, clave));
                inicio = -1;
                conImagen = false;
                clave = false;
            }

            if (inicio < 0)
                inicio = codigo;

            if (tipo is 1 or 5)
                conImagen = true;

            // IDR, o los parametros que solo se reenvian delante de un IDR.
            if (tipo is 5 or 7)
                clave = true;

            p = siguiente;
        }

        // La ultima unidad llega hasta el final. Solo se emite si contiene una
        // imagen: un SPS+PPS colgando al final sin rebanada no es reproducible y
        // pasarlo al decodificador solo genera ruido.
        if (inicio >= 0 && conImagen)
            unidades.Add(new AccessUnit(inicio, flujo.Length - inicio, clave));

        return unidades;
    }
}
