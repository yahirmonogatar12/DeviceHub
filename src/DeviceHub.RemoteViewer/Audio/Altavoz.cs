using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteViewer.Audio;

/// <summary>
/// Recibe el sonido de la PC controlada, lo descodifica y lo reproduce.
///
/// Y MIDE EL DESFASE CONTRA EL VIDEO, que es la razon de haber unificado el
/// reloj del host. Las dos marcas de tiempo -- la del ultimo frame pintado y la
/// del ultimo bloque de sonido reproducido -- salen del MISMO reloj alla, asi
/// que restarlas da el desfase REAL en milisegundos.
///
/// SIN CORREGIR TODAVIA, y a proposito. Corregir significa retrasar el video
/// hasta que el sonido lo alcance, y eso deshace parte de la latencia que costo
/// dos dias bajar. Primero se mide en una sesion real; si el desfase es pequeño
/// y estable no hay nada que corregir, y si no lo es, el numero dice cuanto.
/// Decidirlo antes de medir seria pagar latencia por un problema que a lo mejor
/// no existe.
/// </summary>
public sealed class Altavoz : IDisposable
{
    private readonly Action<string> _nota;

    private AacDecoder? _decodificador;
    private WasapiSalida? _salida;
    private uint _version;

    /// <summary>Marca de captura del ultimo bloque que se entrego al
    /// dispositivo. En el reloj del HOST.</summary>
    private long _ultimoSonidoUs;

    public long Paquetes { get; private set; }
    public long Bytes { get; private set; }

    /// <summary>Paquetes que no se pudieron descodificar o no cupieron. Cada uno
    /// se oye como un chasquido.</summary>
    public long Perdidos { get; private set; }

    /// <summary>Lo ultimo que impidio sonar, o null.</summary>
    public string? Queja { get; private set; }

    public bool Sonando => _salida is not null;

    /// <summary>
    /// Milisegundos que el SONIDO va por detras del VIDEO. Positivo = el sonido
    /// llega tarde. NaN mientras no haya de los dos.
    ///
    /// Las dos marcas vienen del mismo reloj del host, asi que esta resta
    /// significa algo. Con dos relojes distintos daria un numero sin sentido, y
    /// por eso el reloj se unifico ANTES de escribir esto.
    /// </summary>
    public double DesfaseMs(long ultimoVideoUs)
        => _ultimoSonidoUs == 0 || ultimoVideoUs == 0
            ? double.NaN
            : (ultimoVideoUs - _ultimoSonidoUs) / 1000.0;

    public Altavoz(Action<string> nota) => _nota = nota;

    /// <summary>Llega la configuracion del host. Rehace todo si cambio la
    /// version: un PCM descodificado con la configuracion anterior es ruido.</summary>
    public void Configurar(AudioConfig config)
    {
        if (_decodificador is not null && config.ConfigVersion == _version)
            return;

        Cerrar();

        try
        {
            _decodificador = new AacDecoder(
                (int)config.SampleRate, (int)config.Channels, config.ParameterSets.ToByteArray());

            _salida = new WasapiSalida((int)config.SampleRate, (int)config.Channels);
            _version = config.ConfigVersion;
            Queja = null;

            _nota($"Sonido: {config.SampleRate} Hz, {config.Channels} canal(es), " +
                  $"{config.BitrateBitsPerSecond / 1000} kbps.");
        }
        catch (Exception ex) when (ex is AacDecoderNoDisponibleException or SalidaNoDisponibleException)
        {
            Queja = ex.Message;
            _nota($"No se puede reproducir el sonido: {ex.Message}");
            Cerrar();
        }
    }

    /// <summary>Llega un bloque. Se descodifica y se entrega al dispositivo.</summary>
    public void Recibir(AudioChunk bloque)
    {
        if (_decodificador is null || _salida is null)
            return;

        // Un bloque de una version anterior no da error: descodifica ruido. Se
        // tira y se espera a la configuracion nueva, que ya viene de camino.
        if (bloque.ConfigVersion != _version)
        {
            Perdidos++;
            return;
        }

        Paquetes++;

        foreach (var pcm in _decodificador.Descodificar(
                     bloque.Data.ToByteArray(), bloque.CaptureTimestampUs))
        {
            var escritos = _salida.Escribir(pcm);

            if (escritos < pcm.Length)
            {
                // No cupo entero. Pasa cuando llega mas sonido del que se
                // reproduce, normalmente porque los relojes de las dos PCs no
                // corren exactamente igual. Se pierde el resto de este bloque:
                // guardarlo solo retrasaria todo lo que venga detras.
                Perdidos++;
            }

            Bytes += escritos;
        }

        // La marca del ULTIMO bloque entregado, para el desfase.
        _ultimoSonidoUs = bloque.CaptureTimestampUs;
    }

    public void Apagar()
    {
        Cerrar();
        _ultimoSonidoUs = 0;
    }

    private void Cerrar()
    {
        _decodificador?.Dispose();
        _decodificador = null;

        _salida?.Dispose();
        _salida = null;
    }

    public void Dispose() => Cerrar();
}
