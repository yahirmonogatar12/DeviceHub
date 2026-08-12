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
    ulong FrameId, bool KeyFrame, uint ConfigVersion, IReadOnlyList<VideoChunk> Chunks);

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
/// Reensambla frames a partir de chunks.
///
/// Los limites se comprueban ANTES de reservar nada. Un `chunk_count` inventado
/// es, si no, una peticion de memoria arbitraria: el emisor elegiria cuanta RAM
/// reserva el receptor.
///
/// Un frame al que le falta un trozo NO se entrega a medias. Se abandona entero
/// y el llamante pide un keyframe: media imagen descodificada no es una imagen
/// con defectos, es ruido que ademas contamina las siguientes.
/// </summary>
public sealed class VideoFrameAssembler
{
    private byte[]?[] _piezas = [];
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

    /// <summary>Ultimo frame que se reensamblo entero. Es lo que se manda en
    /// KeyframeRequest para decir cuanto se perdio.</summary>
    public ulong LastGoodFrameId { get; private set; }

    /// <summary>
    /// Devuelve true cuando este chunk completa un frame. Los chunks pueden
    /// llegar en cualquier orden dentro de su frame.
    /// </summary>
    public bool TryAdd(VideoChunk chunk, out AssembledFrame? frame)
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
            _piezas = new byte[]?[chunk.ChunkCount];
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

        _piezas[chunk.ChunkIndex] = chunk.Data.ToByteArray();
        _recibidos++;

        if (_recibidos != _piezas.Length)
            return false;

        var total = 0;

        foreach (var pieza in _piezas)
            total += pieza!.Length;

        if (total > RemoteSessionProtocol.MaxFrameBytes)
        {
            Rechazar($"frame {chunk.FrameId} reensamblado a {total} bytes");
            Reiniciar();
            return false;
        }

        var completo = new byte[total];
        var offset = 0;

        foreach (var pieza in _piezas)
        {
            pieza!.CopyTo(completo, offset);
            offset += pieza.Length;
        }

        LastGoodFrameId = chunk.FrameId;
        _algunoCompleto = true;
        Reiniciar();

        frame = new AssembledFrame(
            chunk.FrameId, chunk.KeyFrame, chunk.ConfigVersion, chunk.CaptureTimestampUs, completo);

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
