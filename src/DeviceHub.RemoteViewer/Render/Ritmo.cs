namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Frames por segundo de los ULTIMOS SEGUNDOS, no de la sesion entera.
///
/// La media acumulada parecia lo mismo y no lo es: con el escritorio remoto
/// quieto -- que es su estado normal, porque nadie esta tocando esa PC -- el
/// numero se hunde y deja de decir nada de lo que esta pasando ahora. En una
/// sesion de 38 minutos marcaba 1.74 FPS mientras la imagen iba fina, y hubo
/// que preguntar para entenderlo. Una medida que hay que explicar esta rota.
///
/// Clase suelta y sin WPF por lo mismo que <see cref="Escalado"/>: es
/// aritmetica con un caso de borde -- el arranque, cuando todavia no hay
/// ventana entera -- y probarla dentro de una Window obligaria al proyecto de
/// pruebas a activar UseWPF.
/// </summary>
/// <remarks>El reloj empieza en cero CON LA SESION: es lo que permite saber, sin
/// guardar nada mas, cuanto tiempo se lleva observando.</remarks>
public sealed class Ritmo(double ventanaSegundos = 2.0)
{
    private readonly Queue<double> _instantes = new();

    public void Marcar(double segundos) => _instantes.Enqueue(segundos);

    public double Fps(double ahora)
    {
        while (_instantes.Count > 0 && ahora - _instantes.Peek() > ventanaSegundos)
            _instantes.Dequeue();

        if (_instantes.Count == 0)
            return 0;   // no se ha pintado nada ultimamente, y eso ES cero

        // El periodo observado, que al arrancar es la sesion entera y despues la
        // ventana. Dividir siempre por la ventana daria la mitad de lo que va
        // durante los primeros segundos, que es justo cuando alguien mira la
        // barra para ver si la sesion arranco bien.
        //
        // Y NO se mide desde el primer frame: el hueco entre que la sesion abre
        // y llega la primera imagen es tiempo observado en el que no se pinto
        // nada, y descontarlo seria inventarse ritmo.
        var lapso = Math.Min(ventanaSegundos, ahora);

        return lapso > 0 ? _instantes.Count / lapso : 0;
    }
}
