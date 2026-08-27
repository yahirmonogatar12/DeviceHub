namespace DeviceHub.RemoteHost.Relay;

/// <summary>
/// Cuanto esperar antes de volver a montar la cadena de video de una pantalla.
///
/// Rehacerla es la respuesta a casi todo -- una bomba que murio, una que se
/// quedo colgada, un HRESULT inesperado en mitad de un cambio de escritorio --
/// pero rehacerla SIN ESPERAR es como se convierte un fallo permanente en un
/// bucle que quema CPU y llena el registro de eventos:
///
///     crear MFT -> E_INVALIDARG -> crear MFT -> E_INVALIDARG -> ...
///
/// Asi que se espera un poco mas cada vez, con techo. Y se olvida: una sesion
/// que lleva un rato sana vuelve a empezar por la espera corta, porque el
/// siguiente fallo probablemente no tenga nada que ver con el anterior.
///
/// Clase suelta y sin estado, como EscaleraCodec o VentanaDeReconexion: es
/// aritmetica, y asi se prueba sin GPU, sin MFT y sin sesion.
/// </summary>
public static class RepetirVideo
{
    /// <summary>Primera espera. Corta a proposito: la mayoria de los rehechos
    /// son por un cambio de escritorio y ahi retrasar es empeorar.</summary>
    public static readonly TimeSpan Minima = TimeSpan.FromMilliseconds(100);

    /// <summary>Techo. Mas de un segundo entre intentos ya se siente como una
    /// sesion muerta, y no arregla nada que no arregle un segundo.</summary>
    public static readonly TimeSpan Maxima = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Sin un fallo en este tiempo, la cuenta se olvida.
    ///
    /// Es lo que separa "esta PC no puede codificar" de "hoy se bloqueo dos
    /// veces": sin olvido, una sesion de ocho horas acabaria esperando el maximo
    /// por dos tropiezos que no tenian nada que ver.
    /// </summary>
    public static readonly TimeSpan Olvido = TimeSpan.FromSeconds(30);

    /// <summary>La espera tras <paramref name="seguidos"/> rehechos seguidos.
    /// El primero es 1.</summary>
    public static TimeSpan Espera(int seguidos)
    {
        if (seguidos <= 1)
            return Minima;

        // Doblando, y con el techo puesto ANTES de desplazar: con muchos fallos
        // seguidos, 100 << 60 se sale de un long y vuelve en negativo.
        var pasos = Math.Min(seguidos - 1, 10);
        var ms = Minima.TotalMilliseconds * Math.Pow(2, pasos);

        return ms >= Maxima.TotalMilliseconds ? Maxima : TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Cuantos rehechos seguidos van, contando este.</summary>
    public static int Seguidos(int previos, DateTimeOffset? ultimo, DateTimeOffset ahora)
        => ultimo is { } cuando && ahora - cuando < Olvido ? previos + 1 : 1;
}
