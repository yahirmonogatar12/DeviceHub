namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Quien se da cuenta de que el video se atasco con la conexion viva: llegan
/// trozos de una pantalla y no se pinta ninguno -- el descodificador perdio la
/// referencia y espera un IDR que nadie pide. El remedio es pedirlo.
///
/// Una pantalla QUIETA no es un atasco: el host no manda video si nada cambia.
/// Por eso hace falta que los trozos SIGAN llegando, no solo que no se pinte.
///
/// La conexion muerta NO es cosa de esta clase. Cortar por silencio aqui
/// cerraria la sesion justo cuando el relay sostiene al tecnico esperando a que
/// vuelva la PC controlada -- ahi no llega nada y es correcto. De eso se
/// encarga el keepalive de HTTP/2, que lo contesta el servidor y no el host.
///
/// Clase suelta y sin WPF por lo mismo que <see cref="Ritmo"/>: son reglas con
/// casos de borde y probarlas dentro de una Window no se puede.
/// </summary>
/// <remarks>Los tiempos son segundos de un reloj que empieza con la conexion.
/// Solo se usa desde el hilo de reproduccion.</remarks>
public sealed class Vigia(double atascoMaximo = 3.0)
{
    private readonly Dictionary<uint, Estado> _pantallas = [];

    private sealed class Estado
    {
        public double? SinPintarDesde;
        public double UltimoTrozo;
    }

    /// <summary>Llego un trozo de video de esta pantalla.</summary>
    public void Trozo(uint pantalla, double ahora)
    {
        if (!_pantallas.TryGetValue(pantalla, out var estado))
        {
            estado = new Estado();
            _pantallas[pantalla] = estado;
        }

        // Un frame suelto que el descodificador se quedo dentro -- algunos
        // devuelven la imagen al recibir la SIGUIENTE -- no puede contar como
        // atasco cuando, minutos despues, la pantalla remota vuelve a moverse.
        // Si los trozos dejaron de llegar, la espera empieza de nuevo.
        if (estado.SinPintarDesde is not null && ahora - estado.UltimoTrozo > atascoMaximo)
            estado.SinPintarDesde = null;

        estado.SinPintarDesde ??= ahora;
        estado.UltimoTrozo = ahora;
    }

    /// <summary>Se pinto un frame de esta pantalla: esta viva.</summary>
    public void Pintado(uint pantalla)
    {
        if (_pantallas.TryGetValue(pantalla, out var estado))
            estado.SinPintarDesde = null;
    }

    /// <summary>
    /// Las pantallas que reciben video y no pintan nada desde hace
    /// <c>atascoMaximo</c> segundos. Cada una se devuelve una vez por periodo:
    /// la cuenta vuelve a empezar al avisar, para no pedir un IDR por paquete.
    /// </summary>
    public List<uint> Atascadas(double ahora)
    {
        var atascadas = new List<uint>();

        foreach (var (pantalla, estado) in _pantallas)
        {
            if (estado.SinPintarDesde is not { } desde
                || ahora - desde < atascoMaximo
                || ahora - estado.UltimoTrozo > atascoMaximo)
            {
                continue;
            }

            atascadas.Add(pantalla);
            estado.SinPintarDesde = ahora;
        }

        return atascadas;
    }
}
