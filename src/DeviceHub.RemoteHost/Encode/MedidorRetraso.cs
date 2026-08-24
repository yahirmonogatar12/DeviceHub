namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Separa el retraso de ENCOLADO del de la red.
///
/// POR QUE NO VALE EL RTT A SECAS. El controlador de FPS reaccionaba al RTT
/// crudo, y el RTT crudo lleva dos cosas sumadas: lo que tarda la red -- que no
/// podemos cambiar -- y lo que los frames pasan esperando turno -- que es
/// exactamente lo que hay que corregir. Un enlace con 30 ms de base parecia
/// permanentemente peor que uno de 5 sin estar peor en nada, y peor todavia:
/// nuestras propias colas inflaban el numero al que reaccionabamos, asi que el
/// control se estrangulaba solo.
///
/// El suelo de la ventana es la red pura: en 60 muestras alguna pilla el cable
/// vacio. Lo que sobra de ese suelo es cola, y es la unica parte sobre la que
/// bajar los FPS sirve de algo.
///
/// Es lo que hace RustDesk en video_qos.rs, donde la senal es
/// `avg_delay() - RTT` y ese RTT es una media ponderada de minimos sobre una
/// ventana de 60.
/// </summary>
public sealed class MedidorRetraso
{
    /// <summary>Igual que la suya. Con 60 muestras cada 2 s son dos minutos de
    /// historia: suficiente para que el suelo sea el de ESTA red y no el de una
    /// racha buena de hace media hora.</summary>
    public const int Ventana = 60;

    private readonly double[] _muestras = new double[Ventana];
    private readonly Lock _puerta = new();

    private int _siguiente;
    private int _cuantas;

    /// <summary>Ultima medida. Negativo = todavia no hay ninguna.</summary>
    public double Ultimo { get; private set; } = -1;

    public void Anotar(double rttMs)
    {
        if (rttMs < 0)
            return;

        lock (_puerta)
        {
            _muestras[_siguiente] = rttMs;
            _siguiente = (_siguiente + 1) % Ventana;

            if (_cuantas < Ventana)
                _cuantas++;

            Ultimo = rttMs;
        }
    }

    /// <summary>
    /// El percentil que se pida sobre la ventana. Negativo si no hay medidas.
    ///
    /// Copia y ordena sesenta numeros. Se llama una vez cada dos segundos para
    /// pintar una linea de texto, asi que no hay nada que optimizar aqui.
    /// </summary>
    public double Percentil(double fraccion)
    {
        lock (_puerta)
        {
            if (_cuantas == 0)
                return -1;

            var copia = _muestras[.._cuantas];
            Array.Sort(copia);

            return copia[Math.Clamp((int)(fraccion * (copia.Length - 1)), 0, copia.Length - 1)];
        }
    }

    /// <summary>El suelo de la ventana: lo que cuesta la red cuando no hay cola.
    /// Negativo si todavia no hay medidas.</summary>
    public double Base
    {
        get
        {
            lock (_puerta)
            {
                if (_cuantas == 0)
                    return -1;

                var minimo = double.MaxValue;

                for (var i = 0; i < _cuantas; i++)
                    minimo = Math.Min(minimo, _muestras[i]);

                return minimo;
            }
        }
    }

    /// <summary>
    /// Lo que la ultima medida excede al suelo: la cola, y nada mas.
    ///
    /// Negativo mientras no haya medidas, que es lo que los controladores
    /// entienden como "no toques nada": mover el ritmo sin saber como esta la
    /// red es adivinar.
    /// </summary>
    public double Encolado
    {
        get
        {
            lock (_puerta)
            {
                if (_cuantas == 0)
                    return -1;

                var minimo = double.MaxValue;

                for (var i = 0; i < _cuantas; i++)
                    minimo = Math.Min(minimo, _muestras[i]);

                // Nunca negativo: la propia medida que fijo el suelo da 0, que
                // es justo lo que significa -- no hay cola.
                return Math.Max(Ultimo - minimo, 0);
            }
        }
    }
}
