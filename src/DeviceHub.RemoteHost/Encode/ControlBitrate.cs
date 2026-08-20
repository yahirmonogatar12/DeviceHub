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
    /// bajando solo cambia una sesion mala por una inutil.
    ///
    /// Bajado de 1 Mbps: con el objetivo de 1080p en 1.4 Mbps, un suelo de 1
    /// dejaba al controlador tres pasos de margen antes de tocar fondo.</summary>
    public const int Minimo = 400_000;

    /// <summary>
    /// Lo minimo que se le da a UNA pantalla al repartir.
    ///
    /// Minimo es el suelo de la SESION. Usarlo tambien por pantalla convertia el
    /// suelo en un multiplo del numero de monitores: el controlador creia haber
    /// bajado a 400 kbps y con dos salian 800 reales, con cuatro 1.6 Mbps, y
    /// justo con la red mala, que es cuando el suelo existe para algo.
    ///
    /// Esto es solo para no darle cero a un codificador, que no lo acepta.
    /// </summary>
    public const int MinimoPorPantalla = 100_000;

    public const int Maximo = 15_000_000;

    /// <summary>
    /// Cuantos bits se le dan a la imagen, sobre la base que pide su tamano.
    ///
    /// Son los mismos tres de RustDesk (BR_BEST, BR_BALANCED, BR_SPEED). Estaba
    /// fijo en Equilibrado y no habia forma de pedir mas, y eso se veia: en
    /// 1080p a 42 FPS son 33 kbit por frame, contra los 160 que gasta RustDesk
    /// en esta misma PC. Con la pantalla quieta da igual -- el codificador no
    /// necesita nada -- pero al mover una ventana el control de tasa tiene que
    /// caber en ese presupuesto y lo paga en nitidez.
    /// </summary>
    public const double CalidadFiel = 1.5;

    public const double CalidadEquilibrada = 0.67;

    public const double CalidadRapida = 0.5;

    /// <summary>
    /// Baja DEPRISA y sube DESPACIO, que es la asimetria de todo control de
    /// congestion sensato: pasarse por abajo cuesta un poco de nitidez durante
    /// unos segundos, y pasarse por arriba cuesta que se congele la imagen.
    ///
    /// `ocupacion` es cuantos frames esperan en la cola y `capacidad` cuantos
    /// caben. La cola es de 8 y con espera, asi que llena significa que la
    /// captura ya se esta frenando sola.
    /// </summary>
    /// <param name="pantallaViva">
    /// Si el escritorio remoto esta cambiando de verdad.
    ///
    /// SIN ESTO SE SUBIA SIEMPRE. Con la pantalla quieta no se codifica casi
    /// nada, la cola vive vacia y el controlador leia eso como "hay sitio para
    /// mas calidad" -- asi que trepaba hasta el techo sin una sola prueba de que
    /// cupiera. Cuando por fin alguien movia una ventana, salia de golpe a 15
    /// Mbps contra una red que nunca se habia medido.
    ///
    /// RustDesk lo mira igual en video_qos.rs: solo sube el ratio cuando su
    /// send_counter pasa de DYNAMIC_SCREEN_THRESHOLD.
    /// </param>
    public static int Siguiente(int actual, int ocupacion, int capacidad, bool pantallaViva = true)
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

            // Vacia de verdad: hay sitio para mas calidad, PERO solo si la
            // pantalla se esta moviendo. Vacia con el escritorio quieto no
            // demuestra nada.
            // Se sube un 15 % y no un 10, que es lo que hace RustDesk con la red
            // holgada. Al 10 %, ir de 1.4 a 3.8 Mbps pedia veintiun segundos de
            // movimiento seguido, y un arrastre de ventana dura dos: la subida
            // llegaba siempre tarde y la nitidez se recuperaba cuando ya nadie
            // estaba mirando.
            _ => pantallaViva ? actual * 1.15 : actual
        };

        return (int)Math.Clamp(objetivo, Minimo, Maximo);
    }

    /// <summary>
    /// Con cuanto ARRANCAR segun el tamano de la pantalla.
    ///
    /// Antes se arrancaba en 6 Mbps fijos para todo, y era mucho: RustDesk
    /// apunta a 2073 kbps en 1080p y lo multiplica por 0.67 en calidad
    /// equilibrada, o sea ~1.4 Mbps -- la cuarta parte.
    ///
    /// Y el bitrate no es solo ancho de banda, que en una LAN sobra: es TAMANO
    /// DE FRAME. Un frame cuatro veces mas gordo tarda cuatro veces mas en
    /// cruzar y en descodificarse, y eso se paga en cada vuelta.
    ///
    /// De aqui sale el punto de partida; a partir de ahi lo mueve Siguiente,
    /// que es quien puede comprobar si cabe.
    /// </summary>
    public static int PorResolucion(int ancho, int alto, double calidad = CalidadEquilibrada)
    {
        // Los mismos tres puntos que su RESOLUTION_PRESETS. Entre ellos se
        // interpola por pixeles y fuera se queda en el extremo: la curva no es
        // proporcional -- 4K no necesita cuatro veces lo de 1080p -- y por eso
        // es una tabla y no una multiplicacion.
        (long Pixeles, int Kbps)[] tabla =
        [
            (640L * 480, 400),
            (1920L * 1080, 2073),
            (3840L * 2160, 5000)
        ];

        var pixeles = (long)Math.Max(ancho, 1) * Math.Max(alto, 1);
        double kbps;

        if (pixeles <= tabla[0].Pixeles)
        {
            kbps = tabla[0].Kbps;
        }
        else if (pixeles >= tabla[^1].Pixeles)
        {
            kbps = tabla[^1].Kbps;
        }
        else
        {
            var i = 1;

            while (pixeles > tabla[i].Pixeles)
                i++;

            var (desdePx, desdeKbps) = tabla[i - 1];
            var (hastaPx, hastaKbps) = tabla[i];

            var t = (pixeles - desdePx) / (double)(hastaPx - desdePx);
            kbps = desdeKbps + t * (hastaKbps - desdeKbps);
        }

        return (int)Math.Clamp(kbps * 1000 * calidad, Minimo, Maximo);
    }
}
