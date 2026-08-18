namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Decide a cuantos frames por segundo hay que capturar. Fase 13, segunda mitad.
///
/// POR QUE ES UNA PALANCA APARTE DEL BITRATE. Son dos problemas distintos y
/// mezclarlos es lo que hace que un control se pelee consigo mismo: el bitrate
/// arregla "la imagen ocupa demasiado" y los FPS arreglan "estoy generando
/// imagenes mas deprisa de lo que caben". RustDesk las lleva separadas por lo
/// mismo, y ajusta cada una por su cuenta.
///
/// SE MIDE EL RTT y no la cola. La cola dice que algo va lento pero no QUE: con
/// el codificador saturado tambien se llena, y bajar los FPS por culpa del
/// codificador es tirar calidad para arreglar algo que no estaba roto. El RTT
/// habla solo de la red.
///
/// Funcion pura para poder probarla sin red, igual que el control de bitrate.
/// </summary>
public static class ControlFps
{
    /// <summary>Por debajo de esto ya no es control remoto, es un pase de
    /// diapositivas: mas vale una imagen peor que llegue seguida.</summary>
    public const int Minimo = 5;

    public const int Maximo = 60;

    /// <summary>
    /// Objetivo de arranque. No se empieza por el maximo a proposito: subir
    /// desde abajo cuesta unos segundos de imagen mas pobre, y empezar arriba
    /// cuesta que la primera impresion de la sesion sea una pantalla atascada.
    /// </summary>
    public const int Inicial = 20;

    /// <summary>
    /// `rttMs` negativo = todavia no hay medida. No se toca nada: mover los FPS
    /// sin saber como esta la red es adivinar.
    /// </summary>
    public static int Siguiente(int actual, double rttMs)
    {
        if (rttMs < 0)
            return Math.Clamp(actual, Minimo, Maximo);

        var objetivo = rttMs switch
        {
            // Red holgada. Se sube de dos en dos y no de golpe: un salto grande
            // llena la cola antes de que la siguiente medida lo note.
            < 50 => actual + 2,

            // Zona de trabajo normal. Aqui NO se toca, y es deliberado: un
            // control que se mueve siempre produce vaiven, y el vaiven en los
            // FPS se ve como tirones.
            < 150 => actual,

            // Ya se nota al mover el raton.
            < 300 => actual - 2,

            // La red no da. Se baja fuerte en vez de ir descontando: llegar
            // tarde al fondo significa segundos de sesion inutilizable.
            _ => actual / 2
        };

        return Math.Clamp(objetivo, Minimo, Maximo);
    }
}
