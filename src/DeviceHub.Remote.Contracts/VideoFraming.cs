using Google.Protobuf;

namespace DeviceHub.Remote.Contracts;

/// <summary>
/// Los chunks de UN frame, atados juntos.
///
/// Existe este tipo -- y no una lista suelta de <see cref="VideoChunk"/> --
/// porque la unidad de descarte es el frame. Lo que entra en la cola del relay
/// es esto, de forma que al presionar se van todos los trozos del frame mas
/// viejo a la vez.
///
/// Con chunks sueltos en la cola, un DropOldest tira uno del medio, el viewer
/// tiene que tirar el frame entero igualmente y encima pide un keyframe: se
/// fabrica una perdida que la red no tuvo, y con backlog alto se realimenta
/// hasta que no pasa nada util.
/// </summary>
public sealed record VideoFrameChunks(
    ulong FrameId, bool KeyFrame, uint ConfigVersion, IReadOnlyList<VideoChunk> Chunks)
{
    /// <summary>Bytes de video, sin contar las cabeceras del sobre.</summary>
    public int PayloadBytes
    {
        get
        {
            var total = 0;

            foreach (var trozo in Chunks)
                total += trozo.Data.Length;

            return total;
        }
    }
}

/// <summary>Un frame reensamblado, listo para el decodificador.</summary>
public sealed record AssembledFrame(
    ulong FrameId, bool KeyFrame, uint ConfigVersion, long CaptureTimestampUs, byte[] Payload);

/// <summary>Trocea un frame codificado en mensajes que caben en el cable.</summary>
public static class VideoFraming
{
    public static VideoFrameChunks Split(
        ulong frameId, bool keyFrame, uint configVersion, long captureTimestampUs,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            throw new ArgumentException(
                "Un frame codificado vacio es un fallo del codificador. Reenviarlo dejaria al " +
                "viewer esperando una imagen que no lleva nada dentro.", nameof(payload));

        if (payload.Length > RemoteSessionProtocol.MaxFrameBytes)
            throw new ArgumentException(
                $"Frame de {payload.Length} bytes; el techo son {RemoteSessionProtocol.MaxFrameBytes}.",
                nameof(payload));

        var cuantos = (payload.Length + RemoteSessionProtocol.MaxChunkBytes - 1)
                      / RemoteSessionProtocol.MaxChunkBytes;

        var trozos = new List<VideoChunk>(cuantos);

        for (var i = 0; i < cuantos; i++)
        {
            var desde = i * RemoteSessionProtocol.MaxChunkBytes;
            var cuanto = Math.Min(RemoteSessionProtocol.MaxChunkBytes, payload.Length - desde);

            trozos.Add(new VideoChunk
            {
                FrameId = frameId,
                ChunkIndex = (uint)i,
                ChunkCount = (uint)cuantos,
                KeyFrame = keyFrame,
                ConfigVersion = configVersion,
                CaptureTimestampUs = captureTimestampUs,
                Data = ByteString.CopyFrom(payload.Slice(desde, cuanto))
            });
        }

        return new VideoFrameChunks(frameId, keyFrame, configVersion, trozos);
    }
}

/// <summary>
/// Agrupa chunks sueltos en frames completos, SIN concatenar los bytes.
///
/// Es lo que necesita el relay: para reenviar no hace falta juntar el payload,
/// y juntarlo seria copiar cada frame una vez de mas en el servidor. Lo que si
/// hace falta es saber cuando el frame esta entero, porque a partir de ahi el
/// frame es la unidad que se encola y la unidad que se descarta.
///
/// Los limites se comprueban ANTES de reservar nada. Un `chunk_count` inventado
/// es, si no, una peticion de memoria a eleccion del emisor.
/// </summary>
public sealed class VideoFrameCollector
{
    private VideoChunk?[] _piezas = [];
    private ulong _frameEnCurso;
    private bool _hayFrame;
    private bool _algunoCompleto;
    private int _recibidos;

    /// <summary>Frames abandonados por incompletos.</summary>
    public long Dropped { get; private set; }

    /// <summary>Chunks rechazados por saltarse un limite.</summary>
    public long Rejected { get; private set; }

    /// <summary>Chunks de un frame ya cerrado o mas viejo que el actual.</summary>
    public long Stale { get; private set; }

    /// <summary>Motivo del ultimo rechazo. Para diagnostico, no para el cable.</summary>
    public string? LastRejection { get; private set; }

    /// <summary>Ultimo frame que se completo. Es lo que se manda en
    /// KeyframeRequest para decir cuanto se perdio.</summary>
    public ulong LastGoodFrameId { get; private set; }

    /// <summary>
    /// Devuelve true cuando este chunk completa un frame. Los chunks pueden
    /// llegar en cualquier orden dentro de su frame.
    /// </summary>
    public bool TryAdd(VideoChunk chunk, out VideoFrameChunks? frame)
    {
        frame = null;

        if (!Valido(chunk))
            return false;

        if (!_hayFrame || chunk.FrameId != _frameEnCurso)
        {
            // Un frame_id anterior al que se esta montando llega tarde: su frame
            // ya se entrego o ya se abandono, y meterlo ahora corromperia el
            // actual.
            if (_hayFrame && chunk.FrameId < _frameEnCurso)
            {
                Stale++;
                return false;
            }

            // Con un bool y no con `LastGoodFrameId != 0`: un emisor que numere
            // desde cero dejaria esa comprobacion desactivada justo despues de
            // entregar su primer frame.
            if (_algunoCompleto && chunk.FrameId <= LastGoodFrameId)
            {
                Stale++;
                return false;
            }

            if (_hayFrame)
                Dropped++;   // al anterior le faltaban trozos y ya no van a llegar

            _frameEnCurso = chunk.FrameId;
            _piezas = new VideoChunk?[chunk.ChunkCount];
            _recibidos = 0;
            _hayFrame = true;
        }

        if (chunk.ChunkCount != _piezas.Length)
        {
            Rechazar($"chunk_count {chunk.ChunkCount} no coincide con {_piezas.Length} del frame {chunk.FrameId}");
            return false;
        }

        if (_piezas[chunk.ChunkIndex] is not null)
            return false;   // repetido: ni se cuenta dos veces ni es un error

        _piezas[chunk.ChunkIndex] = chunk;
        _recibidos++;

        if (_recibidos != _piezas.Length)
            return false;

        var trozos = new List<VideoChunk>(_piezas.Length);

        foreach (var pieza in _piezas)
            trozos.Add(pieza!);

        LastGoodFrameId = chunk.FrameId;
        _algunoCompleto = true;
        Reiniciar();

        frame = new VideoFrameChunks(chunk.FrameId, chunk.KeyFrame, chunk.ConfigVersion, trozos);
        return true;
    }

    private bool Valido(VideoChunk chunk)
    {
        if (chunk.ChunkCount == 0)
            return Rechazar($"chunk_count 0 en el frame {chunk.FrameId}");

        if (chunk.ChunkCount > RemoteSessionProtocol.MaxChunksPerFrame)
            return Rechazar(
                $"chunk_count {chunk.ChunkCount} supera el maximo de {RemoteSessionProtocol.MaxChunksPerFrame}");

        if (chunk.ChunkIndex >= chunk.ChunkCount)
            return Rechazar($"chunk_index {chunk.ChunkIndex} fuera de {chunk.ChunkCount}");

        if (chunk.Data.Length > RemoteSessionProtocol.MaxChunkBytes)
            return Rechazar(
                $"chunk de {chunk.Data.Length} bytes; el maximo son {RemoteSessionProtocol.MaxChunkBytes}");

        return true;
    }

    private bool Rechazar(string motivo)
    {
        Rejected++;
        LastRejection = motivo;
        return false;
    }

    private void Reiniciar()
    {
        _piezas = [];
        _hayFrame = false;
        _recibidos = 0;
    }
}

/// <summary>
/// Reensambla frames a partir de chunks. Lo usa el VIEWER, que si necesita los
/// bytes seguidos para dárselos al decodificador.
///
/// Un frame al que le falta un trozo NO se entrega a medias. Se abandona entero
/// y el llamante pide un keyframe: media imagen descodificada no es una imagen
/// con defectos, es ruido que ademas contamina las siguientes.
/// </summary>
public sealed class VideoFrameAssembler
{
    private readonly VideoFrameCollector _agrupador = new();

    public long Dropped => _agrupador.Dropped;
    public long Rejected => _agrupador.Rejected + _rechazosPropios;
    public long Stale => _agrupador.Stale;
    public string? LastRejection => _ultimoPropio ?? _agrupador.LastRejection;
    public ulong LastGoodFrameId => _agrupador.LastGoodFrameId;

    private long _rechazosPropios;
    private string? _ultimoPropio;

    public bool TryAdd(VideoChunk chunk, out AssembledFrame? frame)
    {
        frame = null;

        if (!_agrupador.TryAdd(chunk, out var grupo))
            return false;

        var total = grupo!.PayloadBytes;

        if (total > RemoteSessionProtocol.MaxFrameBytes)
        {
            _rechazosPropios++;
            _ultimoPropio = $"frame {grupo.FrameId} reensamblado a {total} bytes";
            return false;
        }

        var completo = new byte[total];
        var offset = 0;

        foreach (var trozo in grupo.Chunks)
        {
            trozo.Data.CopyTo(completo, offset);
            offset += trozo.Data.Length;
        }

        frame = new AssembledFrame(
            grupo.FrameId, grupo.KeyFrame, grupo.ConfigVersion,
            grupo.Chunks[0].CaptureTimestampUs, completo);

        return true;
    }
}
