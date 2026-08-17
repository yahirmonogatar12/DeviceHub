using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Files;
using Google.Protobuf;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 24: trocear, reensamblar y REANUDAR.
///
/// Lo que se prueba es la reanudacion, que es lo unico que distingue esto de
/// partir bytes. Y se prueba con archivos de verdad en el temporal: el estado de
/// una transferencia interrumpida ES el archivo a medias, asi que simularlo con
/// un doble no probaria nada.
/// </summary>
public class RemoteFileTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(), "devicehub-fase24-" + Guid.NewGuid().ToString("N")[..8]);

    public RemoteFileTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Ruta(string nombre) => Path.Combine(_carpeta, nombre);

    private static FileChunk Trozo(string ruta, ulong offset, byte[] datos, bool ultimo, ulong total)
        => new()
        {
            Path = ruta,
            Offset = offset,
            Total = total,
            Data = ByteString.CopyFrom(datos),
            Last = ultimo
        };

    /// <summary>
    /// El sondeo: un trozo sin datos no escribe, solo contesta cuanto hay. Es la
    /// pieza que permite reanudar sin que el emisor tenga que adivinar.
    /// </summary>
    [Fact]
    public void El_sondeo_de_un_destino_nuevo_dice_cero()
    {
        using var servicio = new FileService();

        var acuse = servicio.Escribir(new FileChunk { Path = Ruta("nuevo.bin"), Total = 100 });

        Assert.Equal(string.Empty, acuse.Error);
        Assert.Equal(0UL, acuse.Received);
    }

    /// <summary>Una subida cortada por la mitad y retomada tiene que dar el mismo
    /// archivo que una de una sola pieza.</summary>
    [Fact]
    public void Una_subida_interrumpida_se_reanuda_donde_iba()
    {
        var ruta = Ruta("grande.bin");
        var contenido = new byte[5000];
        Random.Shared.NextBytes(contenido);

        // Primera mitad, y se pierde la conexion: el servicio se cierra.
        using (var servicio = new FileService())
        {
            var acuse = servicio.Escribir(
                Trozo(ruta, 0, contenido[..2000], ultimo: false, (ulong)contenido.Length));

            Assert.Equal(2000UL, acuse.Received);
        }

        // Sesion nueva. El sondeo tiene que encontrar los 2000 de antes.
        using (var servicio = new FileService())
        {
            var sondeo = servicio.Escribir(new FileChunk { Path = ruta, Total = (ulong)contenido.Length });
            Assert.Equal(2000UL, sondeo.Received);

            var acuse = servicio.Escribir(
                Trozo(ruta, 2000, contenido[2000..], ultimo: true, (ulong)contenido.Length));

            Assert.Equal(string.Empty, acuse.Error);
            Assert.Equal(5000UL, acuse.Received);
        }

        Assert.Equal(contenido, File.ReadAllBytes(ruta));
    }

    /// <summary>
    /// Reescribir un archivo con otro mas CORTO no puede dejar la cola del
    /// anterior detras. Es el fallo clasico de abrir con OpenOrCreate y no
    /// truncar, y produce un archivo que parece intacto y esta corrupto al final.
    /// </summary>
    [Fact]
    public void Un_archivo_mas_corto_no_arrastra_la_cola_del_anterior()
    {
        var ruta = Ruta("encoge.bin");
        File.WriteAllBytes(ruta, new byte[4000]);

        using var servicio = new FileService();

        var corto = new byte[10];
        Random.Shared.NextBytes(corto);

        // Sin sondeo: se manda desde cero a proposito, que es lo que hace un
        // "sobrescribir" de verdad.
        servicio.Escribir(Trozo(ruta, 0, corto, ultimo: true, 10));

        Assert.Equal(corto, File.ReadAllBytes(ruta));
    }

    /// <summary>La descarga en el otro sentido: leer desde un offset devuelve
    /// exactamente la cola que falta, ni un byte antes.</summary>
    [Fact]
    public void Leer_desde_un_offset_devuelve_solo_lo_que_falta()
    {
        var ruta = Ruta("lectura.bin");
        var contenido = new byte[200_000];
        Random.Shared.NextBytes(contenido);
        File.WriteAllBytes(ruta, contenido);

        var recibido = new List<byte>();
        var ultimos = 0;

        FileService.Leer(ruta, 150_000, trozo =>
        {
            Assert.Equal(string.Empty, trozo.Error);
            Assert.Equal((ulong)contenido.Length, trozo.Total);

            recibido.AddRange(trozo.Data);

            if (trozo.Last)
                ultimos++;
        }, CancellationToken.None);

        Assert.Equal(contenido[150_000..], recibido);
        Assert.Equal(1, ultimos);
    }

    /// <summary>
    /// Un offset mayor que el archivo significa que la copia del otro lado es de
    /// otra cosa. Se manda entero en vez de devolver cero bytes y dejar una
    /// descarga que nunca termina.
    /// </summary>
    [Fact]
    public void Un_offset_imposible_reempieza_desde_cero()
    {
        var ruta = Ruta("corto.bin");
        File.WriteAllBytes(ruta, new byte[100]);

        var recibidos = 0;
        FileService.Leer(ruta, 9999, trozo => recibidos += trozo.Data.Length, CancellationToken.None);

        Assert.Equal(100, recibidos);
    }

    /// <summary>Un archivo vacio tiene que cerrar la transferencia igual. Sin
    /// esto, quien descarga se queda esperando un ultimo trozo que no sale.</summary>
    [Fact]
    public void Un_archivo_vacio_manda_su_ultimo_trozo()
    {
        var ruta = Ruta("vacio.bin");
        File.WriteAllBytes(ruta, []);

        var ultimos = 0;
        FileService.Leer(ruta, 0, trozo => { if (trozo.Last) ultimos++; }, CancellationToken.None);

        Assert.Equal(1, ultimos);
    }

    /// <summary>Ningun trozo puede pasar del tope del protocolo: el relay los
    /// rechazaria y la transferencia moriria a la primera.</summary>
    [Fact]
    public void Ningun_trozo_pasa_del_tope_del_relay()
    {
        var ruta = Ruta("tope.bin");
        File.WriteAllBytes(ruta, new byte[500_000]);

        FileService.Leer(ruta, 0,
            trozo => Assert.True(trozo.Data.Length <= RemoteSessionProtocol.MaxChunkBytes),
            CancellationToken.None);
    }

    /// <summary>Listar una carpeta que no existe no puede tumbar la sesion: es un
    /// error de ESA carpeta, no del canal.</summary>
    [Fact]
    public void Listar_una_carpeta_que_no_existe_devuelve_error_y_no_lanza()
    {
        var lista = FileService.Listar(Ruta("no-existe"));

        Assert.NotEqual(string.Empty, lista.Error);
        Assert.Empty(lista.Entries);
    }
}
