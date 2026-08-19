using System.Collections.Concurrent;
using DeviceHub.Remote.Contracts;
using DeviceHub.Server.Remote;
using Google.Protobuf;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// El relay de la Fase 5, probado sin levantar gRPC.
///
/// Las piezas que deciden -- emparejamiento, politica de descarte y bomba de
/// envio -- estan separadas del servicio a proposito: un fallo de recuperacion
/// H.264 se ve aqui en milisegundos, y en una prueba de extremo a extremo se ve
/// como "a veces la imagen sale rara".
/// </summary>
public class RemoteRelayTests
{
    private static VideoConfig Config(uint version = 1, uint ancho = 1920, uint alto = 1080)
        => new()
        {
            ConfigVersion = version,
            Codec = VideoCodec.H264,
            Width = ancho,
            Height = alto,
            FramesPerSecond = 60,
            BitrateBitsPerSecond = 6_000_000,
            ParameterSets = ByteString.CopyFrom([0, 0, 0, 1, 0x67, (byte)version])
        };

    private static VideoFrameChunks Frame(ulong id, bool clave, uint config = 1, int tamano = 1000)
        => VideoFraming.Split(id, clave, config, (long)id * 16_000, new byte[tamano]);

    private static RelayConnection Viewer(string sesion = "s") => new(sesion, RemoteRole.Viewer);

    // -- Emparejamiento -----------------------------------------------------

    [Fact]
    public void The_host_may_connect_first()
    {
        var registro = new RemoteSessionRegistry();
        var sesion = registro.GetOrCreate("s");

        using var host = new RelayConnection("s", RemoteRole.Host);
        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(host));
        Assert.Equal(RemoteSessionState.WaitingForViewer, sesion.State);

        using var viewer = Viewer();
        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(viewer));
        Assert.Equal(RemoteSessionState.Connected, sesion.State);
    }

    [Fact]
    public void The_viewer_may_connect_first()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("s");

        using var viewer = Viewer();
        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(viewer));
        Assert.Equal(RemoteSessionState.WaitingForHost, sesion.State);

        using var host = new RelayConnection("s", RemoteRole.Host);
        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(host));
        Assert.Equal(RemoteSessionState.Connected, sesion.State);
    }

    [Fact]
    public void A_second_host_is_refused_and_does_not_replace_the_first()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("s");

        using var primero = new RelayConnection("s", RemoteRole.Host);
        using var segundo = new RelayConnection("s", RemoteRole.Host);

        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(primero));
        Assert.Equal(JoinOutcome.RoleTaken, sesion.TryJoin(segundo));

        // Sustituir en silencio seria un secuestro: cualquiera con el session_id
        // echaria al que estaba.
        Assert.Same(primero, sesion.Host);
    }

    [Fact]
    public void A_second_viewer_is_refused_and_does_not_replace_the_first()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("s");

        using var primero = Viewer();
        using var segundo = Viewer();

        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(primero));
        Assert.Equal(JoinOutcome.RoleTaken, sesion.TryJoin(segundo));
        Assert.Same(primero, sesion.Viewer);
    }

    [Fact]
    public void Leaving_reports_the_peer_and_clears_the_session()
    {
        var registro = new RemoteSessionRegistry();
        var sesion = registro.GetOrCreate("s");

        using var host = new RelayConnection("s", RemoteRole.Host);
        using var viewer = Viewer();

        sesion.TryJoin(host);
        sesion.TryJoin(viewer);

        // Se va el HOST: al viewer no le va a llegar un frame mas, hay que
        // cerrarle la sesion.
        Assert.Same(viewer, sesion.Leave(host, SessionCloseReason.HostGone));
        Assert.Equal(RemoteSessionState.Closing, sesion.State);

        registro.DropIfEmpty(sesion);
        Assert.Equal(1, registro.Count);   // el viewer sigue ahi

        Assert.Null(sesion.Leave(viewer, SessionCloseReason.ViewerGone));
        Assert.Equal(RemoteSessionState.Closed, sesion.State);

        registro.DropIfEmpty(sesion);
        Assert.Equal(0, registro.Count);   // sin sesiones huerfanas
    }

    [Fact]
    public void When_only_the_viewer_leaves_the_host_is_left_alone()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("s");

        using var host = new RelayConnection("s", RemoteRole.Host);
        using var viewer = Viewer();

        sesion.TryJoin(host);
        sesion.TryJoin(viewer);

        // Al host NO se le avisa: sigue capturando y puede conectarse otro
        // viewer. Cerrarle la sesion porque el tecnico cerro su ventana es lo
        // que impedia reconectar.
        Assert.Null(sesion.Leave(viewer, SessionCloseReason.ViewerGone));
        Assert.Equal(RemoteSessionState.WaitingForViewer, sesion.State);
        Assert.Same(host, sesion.Host);
    }

    [Fact]
    public void A_reconnecting_viewer_gets_the_configuration_and_waits_for_an_IDR()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("s");

        using var host = new RelayConnection("s", RemoteRole.Host);
        sesion.TryJoin(host);
        sesion.FromHostAsync(new RemotePacket { VideoConfig = Config() }, default).AsTask().Wait();

        using var primero = Viewer();
        sesion.TryJoin(primero);
        sesion.Leave(primero, SessionCloseReason.ViewerGone);

        using var segundo = Viewer();
        Assert.Equal(JoinOutcome.Joined, sesion.TryJoin(segundo));
        Assert.Equal(RemoteSessionState.Connected, sesion.State);

        // El que vuelve no tiene contexto: config por delante y a esperar IDR.
        Assert.True(segundo.Video.AwaitingKeyframe);
        Assert.True(segundo.Video.ConfigPending);
        Assert.False(segundo.Video.TryEnqueue(Frame(500, clave: false)));
        Assert.True(segundo.Video.TryEnqueue(Frame(501, clave: true)));
        Assert.True(segundo.Video.TryDequeue(out var config, out _));
        Assert.NotNull(config);
    }

    // -- Configuracion antes del IDR ----------------------------------------

    [Fact]
    public void Nothing_flows_before_there_is_a_configuration()
    {
        var cola = new VideoRelayQueue();

        Assert.False(cola.TryEnqueue(Frame(1, clave: true)));
        Assert.Equal(1, cola.DiscardedNoConfig);
    }

    [Fact]
    public void The_configuration_goes_out_ahead_of_the_first_keyframe()
    {
        var cola = new VideoRelayQueue();
        cola.SetConfig(Config());

        // Un P-frame antes del IDR no sirve de nada: no hay contra que
        // descodificarlo.
        Assert.False(cola.TryEnqueue(Frame(1, clave: false)));
        Assert.Equal(1, cola.DiscardedWaitingIdr);

        Assert.True(cola.TryEnqueue(Frame(2, clave: true)));
        Assert.True(cola.TryDequeue(out var config, out var frame));

        Assert.NotNull(config);
        Assert.Equal(2ul, frame!.FrameId);

        // Ya no hace falta repetirla con cada frame.
        Assert.True(cola.TryEnqueue(Frame(3, clave: false)));
        Assert.True(cola.TryDequeue(out var otra, out _));
        Assert.Null(otra);
    }

    [Fact]
    public void A_new_viewer_gets_the_configuration_and_a_keyframe()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("s");
        using var host = new RelayConnection("s", RemoteRole.Host);
        sesion.TryJoin(host);

        // El host ya venia emitiendo antes de que llegara nadie.
        sesion.FromHostAsync(new RemotePacket { VideoConfig = Config() }, default).AsTask().Wait();

        using var viewer = Viewer();
        sesion.TryJoin(viewer);

        Assert.True(viewer.Video.AwaitingKeyframe);
        Assert.True(viewer.Video.ConfigPending);

        Assert.False(viewer.Video.TryEnqueue(Frame(10, clave: false)));
        Assert.True(viewer.Video.TryEnqueue(Frame(11, clave: true)));
        Assert.True(viewer.Video.TryDequeue(out var config, out _));
        Assert.NotNull(config);
    }

    // -- Recuperacion H.264 -------------------------------------------------

    [Fact]
    public void Congestion_drops_whole_frames_and_never_leaves_a_lone_chunk()
    {
        var cola = new VideoRelayQueue(capacidad: 2);
        cola.SetConfig(Config());

        Assert.True(cola.TryEnqueue(Frame(1, clave: true, tamano: 200_000)));
        Assert.True(cola.TryEnqueue(Frame(2, clave: false, tamano: 200_000)));

        // La cola esta llena. El tercero provoca el descarte.
        Assert.False(cola.TryEnqueue(Frame(3, clave: false, tamano: 200_000)));

        Assert.Equal(2, cola.FramesDropped);
        Assert.Equal(0, cola.Depth);

        // Lo que salga tiene que ser un frame ENTERO o nada. Nunca medio.
        Assert.False(cola.TryDequeue(out _, out _));
    }

    [Fact]
    public void Dropping_a_frame_enters_awaiting_keyframe()
    {
        var cola = new VideoRelayQueue(capacidad: 1);
        cola.SetConfig(Config());

        cola.TryEnqueue(Frame(1, clave: true));
        Assert.False(cola.AwaitingKeyframe);

        cola.TryEnqueue(Frame(2, clave: false));   // desborda

        Assert.True(cola.AwaitingKeyframe);
        Assert.True(cola.ConfigPending);
    }

    [Fact]
    public void After_a_drop_the_following_P_frames_are_discarded()
    {
        // Es el punto entero de la politica: un P-frame se codifica contra los
        // anteriores, asi que reenviarlo tras una perdida no da una imagen con
        // defectos, da corrupcion que se acumula.
        var cola = new VideoRelayQueue(capacidad: 1);
        cola.SetConfig(Config());

        cola.TryEnqueue(Frame(1, clave: true));
        cola.TryEnqueue(Frame(2, clave: false));   // desborda y rompe la cadena

        var antes = cola.DiscardedWaitingIdr;

        for (ulong i = 3; i < 20; i++)
            Assert.False(cola.TryEnqueue(Frame(i, clave: false)));

        Assert.Equal(antes + 17, cola.DiscardedWaitingIdr);
        Assert.Equal(0, cola.Depth);
    }

    [Fact]
    public void The_next_IDR_resumes_forwarding_with_the_configuration_in_front()
    {
        var cola = new VideoRelayQueue(capacidad: 1);
        cola.SetConfig(Config());

        cola.TryEnqueue(Frame(1, clave: true));
        cola.TryDequeue(out _, out _);
        cola.TryEnqueue(Frame(2, clave: false));
        cola.TryEnqueue(Frame(3, clave: false));   // desborda

        Assert.True(cola.AwaitingKeyframe);

        Assert.False(cola.TryEnqueue(Frame(4, clave: false)));
        Assert.True(cola.TryEnqueue(Frame(5, clave: true)));

        Assert.False(cola.AwaitingKeyframe);
        Assert.True(cola.TryDequeue(out var config, out var frame));

        Assert.NotNull(config);            // VideoConfig por delante
        Assert.Equal(5ul, frame!.FrameId); // y despues el IDR

        // A partir de aqui los P-frames vuelven a pasar.
        Assert.True(cola.TryEnqueue(Frame(6, clave: false)));
    }

    // -- Configuracion vieja ------------------------------------------------

    [Fact]
    public void A_frame_with_an_old_config_version_is_refused()
    {
        var cola = new VideoRelayQueue();
        cola.SetConfig(Config(version: 1));
        cola.TryEnqueue(Frame(1, clave: true, config: 1));

        cola.SetConfig(Config(version: 2, ancho: 2560));

        // Rezagado de la configuracion anterior. Descodificarlo con los
        // parametros nuevos no da error, da imagen corrupta.
        Assert.False(cola.TryEnqueue(Frame(2, clave: true, config: 1)));
        Assert.True(cola.StaleConfig >= 1);

        Assert.True(cola.TryEnqueue(Frame(3, clave: true, config: 2)));
    }

    [Fact]
    public void Changing_the_configuration_clears_what_was_queued()
    {
        var cola = new VideoRelayQueue(capacidad: 4);
        cola.SetConfig(Config(version: 1));

        cola.TryEnqueue(Frame(1, clave: true, config: 1));
        cola.TryEnqueue(Frame(2, clave: false, config: 1));
        Assert.Equal(2, cola.Depth);

        cola.SetConfig(Config(version: 2));

        Assert.Equal(0, cola.Depth);
        Assert.True(cola.AwaitingKeyframe);
        Assert.True(cola.ConfigPending);
    }

    // -- Reconciliacion -----------------------------------------------------

    [Fact]
    public async Task The_snapshot_reconciles_the_chain_in_one_read()
    {
        // Los tres numeros de la cadena, leidos A LA VEZ. Compararlos de
        // lecturas distintas fue lo que hizo parecer que el viewer recibia mas
        // frames de los que mandaba el host.
        var registro = new RemoteSessionRegistry();
        var sesion = registro.GetOrCreate("planta-01");

        using var host = new RelayConnection("planta-01", RemoteRole.Host);
        using var viewer = Viewer("planta-01");

        sesion.TryJoin(host);
        sesion.TryJoin(viewer);

        await sesion.FromHostAsync(new RemotePacket { VideoConfig = Config() }, default);

        // Un IDR y dos P-frames, chunk a chunk como llegarian del host.
        foreach (var grupo in new[] { Frame(1, true), Frame(2, false), Frame(3, false) })
        {
            foreach (var trozo in grupo.Chunks)
                await sesion.FromHostAsync(new RemotePacket { VideoChunk = trozo }, default);
        }

        var foto = sesion.Snapshot();

        Assert.Equal("planta-01", foto.SessionId);
        Assert.Equal(RemoteSessionState.Connected, foto.State);
        Assert.True(foto.HostConnected);
        Assert.True(foto.ViewerConnected);
        Assert.Equal(1u, foto.ConfigVersion);

        Assert.Equal(3, foto.FramesReceived);
        Assert.Equal(3, foto.FramesForwarded);

        // La cadena baja de forma monotona y cada escalon tiene su contador.
        Assert.True(foto.FramesReceived >= foto.FramesForwarded);
        Assert.Equal(
            foto.FramesReceived - foto.FramesForwarded,
            foto.FramesDropped + foto.DiscardedWaitingIdr + foto.StaleConfig + foto.DiscardedNoConfig);

        // Y la linea lleva el session_id delante, que es lo que permite cruzarla
        // con lo que imprimen el host y el viewer.
        Assert.Contains("planta-01", foto.ToString());
        Assert.Contains("recibidos=3", foto.ToString());
    }

    [Fact]
    public async Task The_snapshot_explains_every_frame_that_did_not_get_through()
    {
        var sesion = new RemoteSessionRegistry().GetOrCreate("planta-02");

        using var host = new RelayConnection("planta-02", RemoteRole.Host);
        using var viewer = Viewer("planta-02");

        sesion.TryJoin(host);
        sesion.TryJoin(viewer);

        // Sin VideoConfig: nada es descodificable, y tiene que quedar dicho POR
        // QUE no paso, no solo que no paso.
        foreach (var trozo in Frame(1, true).Chunks)
            await sesion.FromHostAsync(new RemotePacket { VideoChunk = trozo }, default);

        var foto = sesion.Snapshot();

        Assert.Equal(1, foto.FramesReceived);
        Assert.Equal(0, foto.FramesForwarded);
        Assert.Equal(1, foto.DiscardedNoConfig);
    }

    // -- Bomba de envio -----------------------------------------------------

    private sealed class Escritor : IRemotePacketWriter
    {
        private int _dentro;

        public ConcurrentQueue<RemotePacket> Escritos { get; } = new();
        public int Solapadas;
        public TimeSpan Retardo { get; set; }

        public async Task WriteAsync(RemotePacket packet, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _dentro, 1) == 1)
                Interlocked.Increment(ref Solapadas);

            try
            {
                if (Retardo > TimeSpan.Zero)
                    await Task.Delay(Retardo, cancellationToken);

                Escritos.Enqueue(packet.Clone());
            }
            finally
            {
                Interlocked.Exchange(ref _dentro, 0);
            }
        }
    }

    private static async Task<Escritor> BombearAsync(RelayConnection conexion, Action trabajo)
    {
        var escritor = new Escritor();
        using var alto = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var bomba = conexion.PumpAsync(escritor, alto.Token);

        trabajo();

        conexion.Complete();
        await bomba;

        return escritor;
    }

    [Fact]
    public async Task Only_one_writer_ever_touches_the_stream()
    {
        using var conexion = Viewer();
        conexion.SetVideoConfig(Config());

        var escritor = await BombearAsync(conexion, () =>
        {
            // Video y control a la vez, desde varios hilos: es exactamente lo
            // que rompe un IServerStreamWriter si hay mas de un escritor.
            Parallel.For(0, 8, i =>
            {
                for (var j = 0; j < 20; j++)
                {
                    conexion.SendVideo(Frame((ulong)(i * 100 + j + 1), clave: true));

                    conexion.SendControlAsync(new RemotePacket
                    {
                        Ping = new Ping { SentAtUs = j }
                    }, CancellationToken.None).AsTask().Wait();
                }
            });
        });

        Assert.Equal(0, escritor.Solapadas);
        Assert.Equal(0, conexion.ConcurrentWrites);
        Assert.NotEmpty(escritor.Escritos);
    }

    [Fact]
    public async Task Control_does_not_queue_up_behind_video()
    {
        using var conexion = Viewer();
        conexion.SetVideoConfig(Config());

        var escritor = await BombearAsync(conexion, () =>
        {
            // Un frame gordo por delante y el cierre justo detras.
            conexion.SendVideo(Frame(1, clave: true, tamano: 500_000));

            conexion.SendControlAsync(new RemotePacket
            {
                Close = new SessionClose { Reason = SessionCloseReason.Normal }
            }, CancellationToken.None).AsTask().Wait();
        });

        var salidos = escritor.Escritos.ToList();
        var cierre = salidos.FindIndex(p => p.PayloadCase == RemotePacket.PayloadOneofCase.Close);
        var ultimoChunk = salidos.FindLastIndex(p => p.PayloadCase == RemotePacket.PayloadOneofCase.VideoChunk);

        Assert.True(cierre >= 0, "el SessionClose tiene que salir");
        Assert.True(cierre < ultimoChunk, "el control no puede esperar a que termine el video");
    }

    [Fact]
    public async Task A_session_close_is_never_lost_even_while_closing()
    {
        using var conexion = Viewer();

        var escritor = await BombearAsync(conexion, () =>
            conexion.SendControlAsync(new RemotePacket
            {
                Close = new SessionClose { Reason = SessionCloseReason.HostGone }
            }, CancellationToken.None).AsTask().Wait());

        Assert.Contains(escritor.Escritos, p => p.PayloadCase == RemotePacket.PayloadOneofCase.Close);
    }

    [Fact]
    public async Task A_multipart_frame_is_forwarded_whole_and_in_order()
    {
        using var conexion = Viewer();
        conexion.SetVideoConfig(Config());

        var original = new byte[200_000];

        for (var i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 17);

        var escritor = await BombearAsync(conexion, () =>
            conexion.SendVideo(VideoFraming.Split(1, true, 1, 0, original)));

        var salidos = escritor.Escritos.ToList();

        Assert.Equal(RemotePacket.PayloadOneofCase.VideoConfig, salidos[0].PayloadCase);

        // Reensamblar lo que salio por el cable tiene que dar el original.
        var montador = new VideoFrameAssembler();
        AssembledFrame? montado = null;

        foreach (var paquete in salidos.Where(p => p.PayloadCase == RemotePacket.PayloadOneofCase.VideoChunk))
        {
            if (montador.TryAdd(paquete.VideoChunk, out var frame))
                montado = frame;
        }

        Assert.NotNull(montado);
        Assert.Equal(original, montado.Payload);
    }

    [Fact]
    public async Task Cancelling_the_pump_finishes_without_a_deadlock()
    {
        using var conexion = Viewer();
        conexion.SetVideoConfig(Config());

        using var alto = new CancellationTokenSource();
        var escritor = new Escritor { Retardo = TimeSpan.FromMilliseconds(2) };

        var bomba = conexion.PumpAsync(escritor, alto.Token);

        for (ulong i = 1; i <= 50; i++)
            conexion.SendVideo(Frame(i, clave: true));

        await alto.CancelAsync();

        // Sin deadlock: si la bomba se quedara esperando un timbre que ya no va
        // a sonar, esto se colgaria hasta el timeout de xUnit.
        await bomba.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_control_queue_is_bounded_and_applies_backpressure()
    {
        using var conexion = Viewer();

        // Nadie bombea: la cola se llena y la escritura numero 257 tiene que
        // esperar, no crecer. Al agotarse el plazo, se cierra con motivo en vez
        // de seguir tragando.
        for (var i = 0; i < 256; i++)
        {
            await conexion.SendControlAsync(new RemotePacket
            {
                Ping = new Ping { SentAtUs = i }
            }, CancellationToken.None);
        }

        await Assert.ThrowsAsync<RelayBackpressureException>(async () =>
            await conexion.SendControlAsync(new RemotePacket { Ping = new Ping() }, CancellationToken.None));
    }

    // -- Varias pantallas ----------------------------------------------------

    private static VideoFrameChunks EnPantalla(VideoFrameChunks grupo, uint pantalla)
    {
        foreach (var trozo in grupo.Chunks)
            trozo.DisplayId = pantalla;

        return grupo;
    }

    private static async Task Mandar(RemoteSession sesion, VideoFrameChunks grupo, uint pantalla)
    {
        foreach (var trozo in EnPantalla(grupo, pantalla).Chunks)
            await sesion.FromHostAsync(new RemotePacket { VideoChunk = trozo }, default);
    }

    [Fact]
    public async Task Two_displays_do_not_cancel_each_other_out()
    {
        // LA PRUEBA DEL SEGUNDO MONITOR CONGELADO.
        //
        // Con una sola cola y un solo agrupador para toda la sesion, esto
        // reenviaba 2 frames de 4: la configuracion de la pantalla 1 sustituia a
        // la de la 0, y a partir de ahi todo frame de la 0 se tiraba como
        // StaleConfig sin salir nunca del servidor. Encima el agrupador
        // compartido marcaba como atrasado el frame 3 por haber completado ya el
        // 4, que es de la otra pantalla.
        //
        // Se veia como una imagen que carga una vez y se queda quieta.
        var registro = new RemoteSessionRegistry();
        var sesion = registro.GetOrCreate("dos-pantallas");

        using var host = new RelayConnection("dos-pantallas", RemoteRole.Host);
        using var viewer = Viewer("dos-pantallas");

        sesion.TryJoin(host);
        sesion.TryJoin(viewer);

        var cero = Config(version: 1);
        var uno = Config(version: 2);
        uno.DisplayId = 1;

        await sesion.FromHostAsync(new RemotePacket { VideoConfig = cero }, default);
        await sesion.FromHostAsync(new RemotePacket { VideoConfig = uno }, default);

        // Entrelazadas y con la numeracion compartida que usa el host: los ids
        // suben, pero entre pantallas no salen en orden.
        await Mandar(sesion, Frame(1, clave: true, config: 1), 0);
        await Mandar(sesion, Frame(2, clave: true, config: 2), 1);
        await Mandar(sesion, Frame(4, clave: false, config: 2), 1);
        await Mandar(sesion, Frame(3, clave: false, config: 1), 0);

        var foto = sesion.Snapshot();

        Assert.Equal(4, foto.FramesReceived);
        Assert.Equal(4, foto.FramesForwarded);
        Assert.Equal(0, foto.StaleConfig);
        Assert.Equal(0, foto.DiscardedWaitingIdr);
    }
}
