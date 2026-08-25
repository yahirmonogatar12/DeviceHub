using System.Diagnostics;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Audio;

/// <summary>
/// Captura, codifica y entrega el sonido de la PC controlada.
///
/// SU PROPIO HILO, como cada pantalla. El sonido no puede ir en el bucle de
/// captura de video: ahi una vuelta cuesta lo que cuesta capturar Y codificar
/// un frame -- entre 4 y 28 ms medidos -- y WASAPI entrega paquetes cada 10.
/// Colgarlo de ese bucle seria perder fotogramas de audio en cada frame lento,
/// y eso no se ve como una imagen mas fea: se oye como chasquidos.
///
/// APAGADA HASTA QUE ALGUIEN LA ENCIENDE, y el dispositivo ni se abre. En un
/// servidor sin tarjeta de sonido abrirlo falla, y fallar al abrir una sesion
/// por algo que nadie pidio seria cambiar una funcion nueva por una averia.
/// </summary>
public sealed class BombaDeSonido : IDisposable
{
    private readonly Action<RemotePacket> _enviar;
    private readonly Action<string> _avisar;
    private readonly string _sesionId;

    private CapturaDeSonido? _captura;
    private AacEncoder? _codificador;
    private byte[] _crudo = [], _pcm16 = [], _mono = [];

    private volatile bool _encendida;
    private volatile bool _configPendiente;
    private uint _version;

    public long Paquetes { get; private set; }
    public long Bytes { get; private set; }

    /// <summary>Lo ultimo que impidio sonar, o null si todo va bien. Sale en la
    /// linea de medidas: un sonido que no suena y no dice por que es peor que
    /// no tener sonido.</summary>
    public string? Queja { get; private set; }

    public bool Encendida => _encendida;

    public BombaDeSonido(string sesionId, Action<RemotePacket> enviar, Action<string> avisar)
    {
        _sesionId = sesionId;
        _enviar = enviar;
        _avisar = avisar;
    }

    /// <summary>
    /// Lo pide el visor. Encender ABRE el dispositivo; apagar lo cierra, porque
    /// tenerlo abierto sin mandar nada gasta CPU en una maquina de produccion.
    /// </summary>
    public void Encender(bool si)
    {
        if (si == _encendida)
            return;

        _encendida = si;

        if (si)
            _configPendiente = true;
        else
            _avisar("Sonido apagado.");
    }

    /// <summary>El visor reconecto o pidio configuracion: hay que reenviarla.</summary>
    public void ReenviarConfig() => _configPendiente = true;

    public void Bombear(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_encendida)
                {
                    Cerrar();
                    cancellationToken.WaitHandle.WaitOne(200);
                    continue;
                }

                if (!Abrir())
                {
                    // Ya se avisó al encender; no se insiste cada 200 ms.
                    _encendida = false;
                    continue;
                }

                if (_configPendiente)
                {
                    _configPendiente = false;
                    Mandar(Config());
                }

                var leidos = _captura!.Recoger(_crudo);

                if (leidos == 0)
                {
                    // WASAPI en loopback no entrega nada cuando no suena nada.
                    // Diez milisegundos es el periodo tipico del motor de audio:
                    // esperar menos es girar en vacio y esperar mas es perder el
                    // principio de un pitido.
                    cancellationToken.WaitHandle.WaitOne(10);
                    continue;
                }

                Codificar(leidos);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Queja = $"{ex.GetType().Name}: {ex.Message}";
            _avisar($"El sonido dejo de funcionar: {Queja}");
        }
        finally
        {
            Cerrar();
        }
    }

    private void Codificar(int leidos)
    {
        var formato = _captura!.Formato;

        var enteros = Pcm16.Convertir(_crudo.AsSpan(0, leidos), _pcm16);

        if (enteros == 0)
            return;

        var fuente = formato.Canales == 2
            ? _mono.AsSpan(0, Pcm16.AMono(_pcm16.AsSpan(0, enteros), _mono))
            : _pcm16.AsSpan(0, enteros);

        // LA MARCA DE TIEMPO ES DEL RELOJ DE LA SESION, el mismo que el video.
        // Restarlas en el visor da el desfase real; con dos relojes distintos
        // solo daria un numero sin significado.
        var ahora = Reloj.Ahora();

        foreach (var bloque in _codificador!.Codificar(fuente, ahora))
        {
            Paquetes++;
            Bytes += bloque.Length;

            Mandar(new RemotePacket
            {
                ProtocolVersion = RemoteSessionProtocol.Version,
                SessionId = _sesionId,
                AudioChunk = new AudioChunk
                {
                    ConfigVersion = _version,
                    CaptureTimestampUs = ahora,
                    Data = Google.Protobuf.ByteString.CopyFrom(bloque)
                }
            });
        }
    }

    private RemotePacket Config() => new()
    {
        ProtocolVersion = RemoteSessionProtocol.Version,
        SessionId = _sesionId,
        AudioConfig = new AudioConfig
        {
            Codec = "aac-lc",
            SampleRate = (uint)_codificador!.Hz,
            Channels = (uint)_codificador.Canales,
            BitrateBitsPerSecond = (uint)_codificador.BitsPorSegundo,
            ParameterSets = Google.Protobuf.ByteString.CopyFrom(_codificador.Configuracion),
            ConfigVersion = _version
        }
    };

    private void Mandar(RemotePacket paquete)
    {
        try
        {
            _enviar(paquete);
        }
        catch (Exception ex)
        {
            Queja = $"no se pudo enviar: {ex.Message}";
        }
    }

    private bool Abrir()
    {
        if (_captura is not null && _codificador is not null)
            return true;

        try
        {
            _captura = new CapturaDeSonido();

            // MONO a proposito. Cuesta la mitad de codificar y para oir una
            // alarma de planta el estereo no aporta nada; el promedio de los
            // dos canales conserva un aviso que solo suene por un lado.
            _codificador = new AacEncoder(_captura.Formato.Hz, 1);

            // Un segundo de margen. El bucle recoge cada 10 ms, asi que nunca
            // se llena -- existe para que un atasco momentaneo no descarte.
            var porSegundo = _captura.Formato.Hz * _captura.Formato.BytesPorFotograma;

            _crudo = new byte[porSegundo];
            _pcm16 = new byte[porSegundo / 2];
            _mono = new byte[porSegundo / 4];

            _version++;
            _configPendiente = true;
            Queja = null;

            _avisar(
                $"Sonido encendido: {_captura.Dispositivo}, {_captura.Formato}, " +
                $"AAC {_codificador.BitsPorSegundo / 1000} kbps mono.");

            return true;
        }
        catch (Exception ex) when (ex is SonidoNoDisponibleException or AacNoDisponibleException)
        {
            Queja = ex.Message;
            _avisar($"No hay sonido en esta PC: {ex.Message}");
            Cerrar();

            return false;
        }
    }

    private void Cerrar()
    {
        _codificador?.Dispose();
        _codificador = null;

        _captura?.Dispose();
        _captura = null;
    }

    public void Dispose() => Cerrar();
}
