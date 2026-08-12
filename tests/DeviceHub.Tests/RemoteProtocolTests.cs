using DeviceHub.Remote.Contracts;
using Google.Protobuf;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// El contrato de la Fase 4: sobre, troceado y reensamblado.
///
/// Nada de esto toca red ni GPU, asi que corre entero en CI. Es a proposito: la
/// aritmetica de fragmentacion es donde se cuelan los fallos que luego solo se
/// ven como imagen corrupta en la PC del tecnico.
/// </summary>
public class RemoteProtocolTests
{
    private static byte[] Datos(int cuantos)
    {
        var bytes = new byte[cuantos];

        // Patron dependiente de la posicion: un reensamblado que ordene mal los
        // trozos da el tamano correcto y el contenido equivocado, y con relleno
        // constante eso pasaria desapercibido.
        for (var i = 0; i < cuantos; i++)
            bytes[i] = (byte)(i * 31 + (i >> 8));

        return bytes;
    }

    private static VideoFrameChunks Trocear(ulong id, byte[] datos, bool clave = false, uint config = 1)
        => VideoFraming.Split(id, clave, config, 1_000, datos);

    // -- Sobre --------------------------------------------------------------

    [Fact]
    public void The_envelope_survives_a_round_trip()
    {
        var original = new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = "s-123",
            Sequence = 42,
            TimestampUs = 1_700_000_000_000_000,
            Hello = new Hello
            {
                Role = RemoteRole.Host,
                MachineId = "INPUTM4",
                Capabilities = new RemoteCapabilities
                {
                    MaxProtocolVersion = 1,
                    SupportsInput = true,
                    SupportsCursor = true,
                    Codecs = { VideoCodec.H264 }
                }
            }
        };

        var copia = RemotePacket.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(original, copia);
        Assert.Equal(RemotePacket.PayloadOneofCase.Hello, copia.PayloadCase);
        Assert.Equal(RemoteRole.Host, copia.Hello.Role);
        Assert.Equal(VideoCodec.H264, copia.Hello.Capabilities.Codecs[0]);
    }

    [Fact]
    public void Every_payload_kind_round_trips_and_keeps_its_oneof_case()
    {
        var casos = new (RemotePacket Paquete, RemotePacket.PayloadOneofCase Caso)[]
        {
            (new RemotePacket { VideoConfig = new VideoConfig { Width = 1920, Height = 1080 } },
                RemotePacket.PayloadOneofCase.VideoConfig),
            (new RemotePacket { VideoChunk = new VideoChunk { FrameId = 7, ChunkCount = 1 } },
                RemotePacket.PayloadOneofCase.VideoChunk),
            (new RemotePacket { Cursor = new CursorUpdate { X = 0.5, Y = 0.25, Visible = true } },
                RemotePacket.PayloadOneofCase.Cursor),
            (new RemotePacket { Input = new InputEvent { MouseMove = new MouseMove { X = 0.1, Y = 0.9 } } },
                RemotePacket.PayloadOneofCase.Input),
            (new RemotePacket { Ping = new Ping { SentAtUs = 99 } },
                RemotePacket.PayloadOneofCase.Ping),
            (new RemotePacket { Pong = new Pong { SentAtUs = 99 } },
                RemotePacket.PayloadOneofCase.Pong),
            (new RemotePacket { KeyframeRequest = new KeyframeRequest { Reason = KeyframeReason.LostChunk } },
                RemotePacket.PayloadOneofCase.KeyframeRequest),
            (new RemotePacket { Close = new SessionClose { Reason = SessionCloseReason.Normal } },
                RemotePacket.PayloadOneofCase.Close),
            (new RemotePacket { Error = new RemoteError { Code = RemoteErrorCode.InvalidTicket } },
                RemotePacket.PayloadOneofCase.Error)
        };

        foreach (var (paquete, caso) in casos)
        {
            var copia = RemotePacket.Parser.ParseFrom(paquete.ToByteArray());

            Assert.Equal(caso, copia.PayloadCase);
            Assert.Equal(paquete, copia);
        }
    }

    [Fact]
    public void Input_events_are_defined_but_carry_no_behaviour()
    {
        // Definidos en la Fase 4 para no versionar el protocolo en las Fases 9-11.
        // Lo que se comprueba aqui es que el contrato existe y viaja entero.
        var tecla = new RemotePacket
        {
            Input = new InputEvent
            {
                Key = new KeyEvent { VirtualKey = 0x11, ScanCode = 0x1D, Pressed = true, Extended = false }
            }
        };

        var copia = RemotePacket.Parser.ParseFrom(tecla.ToByteArray());

        Assert.Equal(InputEvent.EventOneofCase.Key, copia.Input.EventCase);
        Assert.Equal(0x1Du, copia.Input.Key.ScanCode);
        Assert.True(copia.Input.Key.Pressed);
    }

    // -- Troceado -----------------------------------------------------------

    [Fact]
    public void One_byte_becomes_a_single_chunk()
    {
        var trozos = Trocear(1, Datos(1));

        Assert.Single(trozos.Chunks);
        Assert.Equal(1u, trozos.Chunks[0].ChunkCount);
        Assert.Equal(1, trozos.Chunks[0].Data.Length);
    }

    [Fact]
    public void Exactly_64_KiB_still_fits_in_one_chunk()
    {
        var trozos = Trocear(1, Datos(RemoteSessionProtocol.MaxChunkBytes));

        Assert.Single(trozos.Chunks);
        Assert.Equal(RemoteSessionProtocol.MaxChunkBytes, trozos.Chunks[0].Data.Length);
    }

    [Fact]
    public void One_byte_over_64_KiB_needs_two()
    {
        var trozos = Trocear(1, Datos(RemoteSessionProtocol.MaxChunkBytes + 1));

        Assert.Equal(2, trozos.Chunks.Count);
        Assert.Equal(RemoteSessionProtocol.MaxChunkBytes, trozos.Chunks[0].Data.Length);
        Assert.Equal(1, trozos.Chunks[1].Data.Length);
    }

    [Fact]
    public void No_chunk_ever_exceeds_64_KiB()
    {
        // Un tamano que no es multiplo del trozo, para que el ultimo sea parcial.
        foreach (var tamano in new[] { 1, 1000, 65_535, 65_536, 65_537, 300_000, 2_000_000 })
        {
            foreach (var trozo in Trocear(1, Datos(tamano)).Chunks)
                Assert.True(trozo.Data.Length <= RemoteSessionProtocol.MaxChunkBytes);
        }
    }

    [Fact]
    public void Chunks_of_one_frame_share_frame_id_and_count()
    {
        // Es lo que permite al relay tirarlos JUNTOS. Sin esto la unidad de
        // descarte seria el chunk, y descartar uno suelto fabrica una perdida
        // que la red no tuvo.
        var trozos = Trocear(77, Datos(200_000), clave: true, config: 3);

        Assert.Equal(77ul, trozos.FrameId);
        Assert.True(trozos.KeyFrame);

        for (var i = 0; i < trozos.Chunks.Count; i++)
        {
            Assert.Equal(77ul, trozos.Chunks[i].FrameId);
            Assert.Equal((uint)trozos.Chunks.Count, trozos.Chunks[i].ChunkCount);
            Assert.Equal((uint)i, trozos.Chunks[i].ChunkIndex);
            Assert.True(trozos.Chunks[i].KeyFrame);
            Assert.Equal(3u, trozos.Chunks[i].ConfigVersion);
        }
    }

    [Fact]
    public void An_empty_or_oversized_frame_is_refused_at_the_source()
    {
        Assert.Throws<ArgumentException>(() => VideoFraming.Split(1, false, 1, 0, []));
        Assert.Throws<ArgumentException>(() =>
            VideoFraming.Split(1, false, 1, 0, new byte[RemoteSessionProtocol.MaxFrameBytes + 1]));
    }

    // -- Reensamblado -------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(1_500_000)]
    public void Reassembly_returns_exactly_the_original_payload(int tamano)
    {
        var original = Datos(tamano);
        var montador = new VideoFrameAssembler();

        AssembledFrame? frame = null;

        foreach (var trozo in Trocear(5, original, clave: true, config: 2).Chunks)
        {
            if (montador.TryAdd(trozo, out var salida))
                frame = salida;
        }

        Assert.NotNull(frame);
        Assert.Equal(original, frame.Payload);
        Assert.Equal(5ul, frame.FrameId);
        Assert.True(frame.KeyFrame);
        Assert.Equal(2u, frame.ConfigVersion);
        Assert.Equal(0L, montador.Dropped);
        Assert.Equal(0L, montador.Rejected);
    }

    [Fact]
    public void Several_frames_in_a_row_come_out_whole_and_in_order()
    {
        var montador = new VideoFrameAssembler();
        var esperados = new List<byte[]>();
        var obtenidos = new List<byte[]>();

        for (var i = 0; i < 12; i++)
        {
            var datos = Datos(1 + i * 40_000);
            esperados.Add(datos);

            foreach (var trozo in Trocear((ulong)(i + 1), datos).Chunks)
            {
                if (montador.TryAdd(trozo, out var frame))
                    obtenidos.Add(frame!.Payload);
            }
        }

        Assert.Equal(12, obtenidos.Count);
        Assert.Equal(esperados, obtenidos);
        Assert.Equal(0L, montador.Dropped);
        Assert.Equal(12ul, montador.LastGoodFrameId);
    }

    [Fact]
    public void Chunks_may_arrive_out_of_order_within_their_frame()
    {
        var original = Datos(200_000);
        var montador = new VideoFrameAssembler();

        AssembledFrame? frame = null;

        foreach (var trozo in Trocear(1, original).Chunks.Reverse())
        {
            if (montador.TryAdd(trozo, out var salida))
                frame = salida;
        }

        Assert.NotNull(frame);
        Assert.Equal(original, frame.Payload);
    }

    [Fact]
    public void A_frame_missing_a_chunk_is_dropped_whole_and_never_delivered_partial()
    {
        var montador = new VideoFrameAssembler();
        var incompleto = Trocear(1, Datos(200_000)).Chunks;

        // Todos menos uno del medio.
        for (var i = 0; i < incompleto.Count; i++)
        {
            if (i == 1)
                continue;

            Assert.False(montador.TryAdd(incompleto[i], out _));
        }

        Assert.Equal(0L, montador.Dropped);   // todavia esperando

        // Empieza el siguiente frame: al anterior ya no le van a llegar trozos.
        AssembledFrame? frame = null;

        foreach (var trozo in Trocear(2, Datos(1000)).Chunks)
        {
            if (montador.TryAdd(trozo, out var salida))
                frame = salida;
        }

        Assert.Equal(1L, montador.Dropped);
        Assert.NotNull(frame);
        Assert.Equal(2ul, frame.FrameId);
        Assert.Equal(2ul, montador.LastGoodFrameId);   // el 1 nunca fue bueno
    }

    [Fact]
    public void A_late_chunk_of_a_closed_frame_is_ignored()
    {
        var montador = new VideoFrameAssembler();
        var primero = Trocear(1, Datos(1000)).Chunks;

        Assert.True(montador.TryAdd(primero[0], out _));

        // Llega otra vez, tarde, cuando ese frame ya se entrego.
        Assert.False(montador.TryAdd(primero[0], out var nada));

        Assert.Null(nada);
        Assert.Equal(1L, montador.Stale);
        Assert.Equal(0L, montador.Dropped);
    }

    [Fact]
    public void A_repeated_chunk_of_the_current_frame_is_harmless()
    {
        var montador = new VideoFrameAssembler();
        var trozos = Trocear(1, Datos(200_000)).Chunks;

        Assert.False(montador.TryAdd(trozos[0], out _));
        Assert.False(montador.TryAdd(trozos[0], out _));   // repetido

        AssembledFrame? frame = null;

        for (var i = 1; i < trozos.Count; i++)
        {
            if (montador.TryAdd(trozos[i], out var salida))
                frame = salida;
        }

        Assert.NotNull(frame);
        Assert.Equal(0L, montador.Rejected);
    }

    // -- Limites ------------------------------------------------------------

    [Fact]
    public void Limits_are_checked_before_anything_is_allocated()
    {
        var montador = new VideoFrameAssembler();

        // chunk_count desorbitado: seria una reserva de memoria a eleccion del
        // emisor.
        Assert.False(montador.TryAdd(new VideoChunk
        {
            FrameId = 1,
            ChunkIndex = 0,
            ChunkCount = uint.MaxValue,
            Data = ByteString.CopyFrom([1])
        }, out _));

        // chunk_count cero.
        Assert.False(montador.TryAdd(new VideoChunk { FrameId = 1, ChunkCount = 0 }, out _));

        // indice fuera de rango.
        Assert.False(montador.TryAdd(new VideoChunk
        {
            FrameId = 1,
            ChunkIndex = 5,
            ChunkCount = 2,
            Data = ByteString.CopyFrom([1])
        }, out _));

        // payload por encima de 64 KiB.
        Assert.False(montador.TryAdd(new VideoChunk
        {
            FrameId = 1,
            ChunkIndex = 0,
            ChunkCount = 1,
            Data = ByteString.CopyFrom(new byte[RemoteSessionProtocol.MaxChunkBytes + 1])
        }, out _));

        Assert.Equal(4L, montador.Rejected);
        Assert.NotNull(montador.LastRejection);
    }

    [Fact]
    public void A_chunk_that_changes_the_count_mid_frame_is_rejected()
    {
        var montador = new VideoFrameAssembler();
        var trozos = Trocear(1, Datos(200_000)).Chunks;

        Assert.False(montador.TryAdd(trozos[0], out _));

        var mentiroso = trozos[1].Clone();
        mentiroso.ChunkCount = 2;

        Assert.False(montador.TryAdd(mentiroso, out _));
        Assert.Equal(1L, montador.Rejected);
    }

    // -- Cambio de configuracion en mitad de la sesion ----------------------

    [Fact]
    public void The_configuration_can_change_mid_session()
    {
        // VideoConfig -> IDR -> VideoConfig nuevo -> IDR.
        //
        // Demuestra desde el contrato que una sesion puede renegociar: cambio de
        // resolucion del escritorio remoto, o un STREAM_CHANGE del codificador.
        // Cada frame lleva la version con la que se codifico, asi que uno que
        // llegue tarde tras el cambio se reconoce como viejo en vez de
        // descodificarse con los parametros nuevos.
        var cable = new List<RemotePacket>();

        void Configurar(uint version, uint ancho, uint alto) => cable.Add(new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = "s-1",
            VideoConfig = new VideoConfig
            {
                ConfigVersion = version,
                Codec = VideoCodec.H264,
                Width = ancho,
                Height = alto,
                FramesPerSecond = 60,
                BitrateBitsPerSecond = 6_000_000,
                ParameterSets = ByteString.CopyFrom([0, 0, 0, 1, 0x67, (byte)version]),
                VisibleWidth = ancho,
                VisibleHeight = alto
            }
        });

        void Idr(ulong id, uint version, byte[] datos)
        {
            foreach (var trozo in VideoFraming.Split(id, true, version, 0, datos).Chunks)
                cable.Add(new RemotePacket { SessionId = "s-1", VideoChunk = trozo });
        }

        var primero = Datos(120_000);
        var segundo = Datos(90_000);

        Configurar(1, 1920, 1080);
        Idr(1, 1, primero);
        Configurar(2, 2560, 1080);
        Idr(2, 2, segundo);

        // El receptor: aplica la configuracion vigente y monta los frames.
        var montador = new VideoFrameAssembler();
        var vigente = 0u;
        var recibidos = new List<(uint Config, uint Ancho, byte[] Payload)>();
        var anchoVigente = 0u;

        foreach (var paquete in cable.Select(p => RemotePacket.Parser.ParseFrom(p.ToByteArray())))
        {
            switch (paquete.PayloadCase)
            {
                case RemotePacket.PayloadOneofCase.VideoConfig:
                    vigente = paquete.VideoConfig.ConfigVersion;
                    anchoVigente = paquete.VideoConfig.Width;
                    break;

                case RemotePacket.PayloadOneofCase.VideoChunk:
                    if (montador.TryAdd(paquete.VideoChunk, out var frame))
                    {
                        Assert.Equal(vigente, frame!.ConfigVersion);
                        recibidos.Add((frame.ConfigVersion, anchoVigente, frame.Payload));
                    }

                    break;
            }
        }

        Assert.Equal(2, recibidos.Count);
        Assert.Equal((1u, 1920u), (recibidos[0].Config, recibidos[0].Ancho));
        Assert.Equal((2u, 2560u), (recibidos[1].Config, recibidos[1].Ancho));
        Assert.Equal(primero, recibidos[0].Payload);
        Assert.Equal(segundo, recibidos[1].Payload);
        Assert.Equal(0L, montador.Dropped);
        Assert.Equal(0L, montador.Rejected);
    }
}
