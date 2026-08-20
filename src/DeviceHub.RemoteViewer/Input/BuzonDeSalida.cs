using System.Threading.Channels;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteViewer.Input;

/// <summary>
/// Todo lo que el visor manda al host, con sus prioridades.
///
/// VIVE FUERA DE LA VENTANA a proposito. Aqui esta la logica que ya ha fallado
/// tres veces -- coalescencia del raton, recursion al saturarse, entrada vieja
/// reproducida tras reconectar -- y dentro de una Window no se podia probar
/// ninguna: el proyecto de pruebas tendria que activar UseWPF entero. Son colas
/// y banderas, no interfaz.
///
/// Tres carriles, y no es lujo:
///
///   RESCATE      soltar lo hundido. Va delante de todo y NO pasa por la cola,
///                porque se pide justo cuando la cola es el problema.
///
///   MOVIMIENTO   gana el ultimo. Las coordenadas son absolutas, asi que de una
///                rafaga solo el ultimo dice algo; los demas ya no valen nada y
///                encolarlos era lo que llenaba la cola.
///
///   LO DEMAS     teclas, botones, rueda, portapapeles, acuses. NO se descarta
///                nada: perder un KeyUp deja esa tecla hundida en la PC de
///                planta, donde nadie puede despegarla.
/// </summary>
public sealed class BuzonDeSalida
{
    private readonly Channel<RemotePacket> _cola;

    /// <summary>Golpecito para despertar al hilo de envio cuando hay movimiento
    /// pendiente. No se manda: el movimiento vive en su propio hueco.</summary>
    private static readonly RemotePacket Golpecito = new();

    private RemotePacket? _movimiento;
    private int _soltarPendiente;

    public BuzonDeSalida(int capacidad = 512)
        => _cola = Channel.CreateBounded<RemotePacket>(new BoundedChannelOptions(capacidad)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public long Enviados { get; private set; }

    /// <summary>Criticos que no cupieron. Tiene que quedarse en cero: si sube,
    /// algo hundido pudo quedarse sin su KeyUp.</summary>
    public long Perdidos { get; private set; }

    /// <summary>Movimientos que se comieron a otro anterior.</summary>
    public long Fundidos { get; private set; }

    /// <summary>Se levanta cuando hay que pedirle al host que suelte lo hundido.
    /// NO se encola: el motivo mas comun para necesitarlo es que la cola acaba
    /// de fallar, y mandarlo por ahi seria pedirselo a quien no puede.</summary>
    public void PedirSoltar()
    {
        Interlocked.Exchange(ref _soltarPendiente, 1);

        // Y SE DESPIERTA AL HILO DE ENVIO. EsperarAsync solo mira la cola, asi
        // que una bandera a secas no lo saca de la espera: el rescate se
        // quedaba ahi hasta el siguiente latido, o sea hasta un segundo.
        //
        // Se nota justo donde mas duele, en Reiniciar: vacia la cola, tira el
        // movimiento y pide soltar -- sobre una cola que acaba de quedarse
        // vacia, asi que no hay nada que despierte a nadie.
        _cola.Writer.TryWrite(Golpecito);
    }

    public bool Encolar(RemotePacket paquete)
    {
        if (EsMovimiento(paquete))
        {
            if (Interlocked.Exchange(ref _movimiento, paquete) is not null)
                Fundidos++;

            // El golpecito puede caerse sin consecuencia: si la cola esta llena
            // el hilo de envio ya tiene trabajo y va a pasar por aqui igual.
            _cola.Writer.TryWrite(Golpecito);
            return true;
        }

        if (_cola.Writer.TryWrite(paquete))
        {
            Enviados++;
            return true;
        }

        // No se reintenta por la misma cola: eso era la recursion. Se levanta la
        // bandera y ya la vera el hilo de envio, que no depende de esto.
        Perdidos++;
        PedirSoltar();

        return false;
    }

    /// <summary>
    /// Saca lo siguiente que hay que mandar, en orden de prioridad. Devuelve
    /// false cuando no queda nada.
    /// </summary>
    public bool TryTomar(string sesion, out RemotePacket paquete)
    {
        if (Interlocked.Exchange(ref _soltarPendiente, 0) == 1)
        {
            paquete = new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = sesion,
                HostAction = new HostAction { Kind = HostAction.Types.Kind.HostActionReleaseInput }
            };

            return true;
        }

        if (Interlocked.Exchange(ref _movimiento, null) is { } movimiento)
        {
            Enviados++;
            paquete = movimiento;

            return true;
        }

        while (_cola.Reader.TryRead(out var siguiente))
        {
            if (ReferenceEquals(siguiente, Golpecito))
                continue;   // solo servia para despertar

            paquete = siguiente;
            return true;
        }

        paquete = null!;
        return false;
    }

    /// <summary>Espera a que haya algo. Solo mira la cola: el rescate y el
    /// movimiento siempre dejan un golpecito o llegan con uno.</summary>
    public ValueTask<bool> EsperarAsync(CancellationToken cancellationToken)
        => _cola.Reader.WaitToReadAsync(cancellationToken);

    /// <summary>
    /// Conexion nueva: lo pendiente se tira y se pide soltar.
    ///
    /// NADA DE LO QUE QUEDO SIGUE VALIENDO. El relay nunca lo recibio, los
    /// acuses son de un stream muerto y los frames que confirmaban ya no
    /// existen. Reproducirlo seria aplicar en la PC remota clics y teclas de
    /// hace medio minuto, contra una pantalla que ya no es la que el tecnico
    /// estaba mirando.
    /// </summary>
    public void Reiniciar()
    {
        while (_cola.Reader.TryRead(out _))
        {
        }

        Interlocked.Exchange(ref _movimiento, null);
        PedirSoltar();
    }

    private static bool EsMovimiento(RemotePacket paquete)
        => paquete.PayloadCase == RemotePacket.PayloadOneofCase.Input
           && paquete.Input.EventCase == InputEvent.EventOneofCase.MouseMove;
}
