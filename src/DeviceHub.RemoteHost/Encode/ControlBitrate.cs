namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Decide el bitrate objetivo a partir de lo que se acumula en la cola de
/// salida. Fase 13.
///
/// FUNCION PURA, y por eso vive aparte: la unica forma de probar un controlador
/// de este tipo sin montar una red es darle series de medidas y mirar a donde
/// converge. Con la logica dentro del bucle de red no se puede probar nada.
///
/// SE MIDE LA COLA Y NO EL RTT. El RTT lo calcula el visor, no el host, y
/// traerlo hasta aqui seria protocolo nuevo para saber algo que la cola ya dice:
/// si los frames se amontonan esperando cable, la red no da para el bitrate
/// actual. Es la senal directa, no un sintoma.
/// </summary>
public static class ControlBitrate
{
    /// <summary>Por debajo de esto la imagen ya no sirve para trabajar, y seguir
    /// bajando solo cambia una sesion mala por una inutil.</summary>
    public const int Minimo = 1_000_000;

    public const int Maximo = 15_000_000;

    /// <summary>
    /// Baja DEPRISA y sube DESPACIO, que es la asimetria de todo control de
    /// congestion sensato: pasarse por abajo cuesta un poco de nitidez durante
    /// unos segundos, y pasarse por arriba cuesta que se congele la imagen.
    ///
    /// `ocupacion` es cuantos frames esperan en la cola y `capacidad` cuantos
    /// caben. La cola es de 8 y con espera, asi que llena significa que la
    /// captura ya se esta frenando sola.
    /// </summary>
    public static int Siguiente(int actual, int ocupacion, int capacidad)
    {
        var lleno = ocupacion / (double)Math.Max(capacidad, 1);

        var objetivo = lleno switch
        {
            // Medio llena ya es tarde: a partir de ahi los frames viejos se
            // quedan esperando y el tecnico ve la pantalla a destiempo.
            >= 0.5 => actual * 0.7,

            // Con algo de cola no se toca. Subir aqui es lo que produce el
            // vaiven de bajar y subir cada dos segundos.
            > 0.0 => actual,

            // Vacia de verdad: hay sitio para mas calidad.
            _ => actual * 1.1
        };

        return (int)Math.Clamp(objetivo, Minimo, Maximo);
    }
}
