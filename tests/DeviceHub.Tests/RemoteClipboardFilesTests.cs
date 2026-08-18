using System.Text;
using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteHost.Input;
using DeviceHub.Server.Services;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fase 25: el bloque CF_HDROP.
///
/// Se prueba el formato y no las llamadas al portapapeles, que exigen una
/// estacion de ventanas. Es donde de verdad se falla: un desplazamiento mal
/// puesto o el nulo final que falta no dan error, dan rutas basura AL PEGAR --
/// mucho despues y en otro sitio.
/// </summary>
public class RemoteClipboardFilesTests
{
    /// <summary>Lee el bloque como lo leeria Windows y devuelve las rutas.</summary>
    private static List<string> Interpretar(byte[] bloque)
    {
        var inicio = BitConverter.ToInt32(bloque, 0);

        Assert.Equal(20, inicio);
        Assert.Equal(1, BitConverter.ToInt32(bloque, 16));   // fWide

        var texto = Encoding.Unicode.GetString(bloque, inicio, bloque.Length - inicio);

        // Doble nulo = fin de la lista. Lo que venga detras no cuenta.
        var fin = texto.IndexOf("\0\0", StringComparison.Ordinal);

        return [.. texto[..(fin < 0 ? texto.Length : fin)].Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    [Fact]
    public void Una_ruta_va_y_vuelve_igual()
        => Assert.Equal(
            [@"C:\temp\informe.pdf"],
            Interpretar(ClipboardBridge.Dropfiles([@"C:\temp\informe.pdf"])));

    /// <summary>Varias rutas separadas por nulo. Sin el separador salen pegadas
    /// en una sola que no existe.</summary>
    [Fact]
    public void Varias_rutas_conservan_el_orden()
    {
        string[] rutas = [@"C:\a.txt", @"C:\carpeta\b.log", @"D:\c.zip"];

        Assert.Equal(rutas, Interpretar(ClipboardBridge.Dropfiles(rutas)));
    }

    /// <summary>
    /// La lista termina en DOS nulos: uno de la ultima ruta y otro que cierra el
    /// conjunto. Sin el segundo, Windows sigue leyendo mas alla del bloque.
    /// </summary>
    [Fact]
    public void La_lista_cierra_con_dos_nulos()
    {
        var bloque = ClipboardBridge.Dropfiles([@"C:\a.txt"]);

        Assert.Equal(0, bloque[^1]);
        Assert.Equal(0, bloque[^2]);
        Assert.Equal(0, bloque[^3]);
        Assert.Equal(0, bloque[^4]);
    }

    /// <summary>Acentos y caracteres fuera de ASCII: es el caso que delata un
    /// bloque escrito en ANSI en vez de UTF-16.</summary>
    [Fact]
    public void Las_rutas_con_acentos_sobreviven()
        => Assert.Equal(
            [@"C:\Documentación\año 2026\informe ñ.txt"],
            Interpretar(ClipboardBridge.Dropfiles([@"C:\Documentación\año 2026\informe ñ.txt"])));

    /// <summary>El tamano no se calcula a ojo: cabecera + (caracteres + un nulo
    /// por ruta + uno final) * 2.</summary>
    [Fact]
    public void El_tamano_del_bloque_es_exacto()
    {
        // "C:\ab" son 5 caracteres y "D:\cde" son 6, mas un nulo cada una y otro
        // que cierra la lista.
        var bloque = ClipboardBridge.Dropfiles([@"C:\ab", @"D:\cde"]);

        Assert.Equal(20 + (5 + 1 + 6 + 1 + 1) * 2, bloque.Length);
    }

    /// <summary>El anuncio y la orden van en los dos sentidos: cada extremo puede
    /// copiar archivos y llevarselos al otro.</summary>
    [Theory]
    [InlineData(RemoteRole.Host)]
    [InlineData(RemoteRole.Viewer)]
    public void El_portapapeles_de_archivos_viaja_en_los_dos_sentidos(RemoteRole papel)
    {
        var paquete = new RemotePacket
        {
            ProtocolVersion = RemoteSessionProtocol.Version,
            SessionId = "s1",
            ClipboardFiles = new ClipboardFiles { Paths = { @"C:\a.txt" } }
        };

        Assert.Null(RemoteRelayGrpcService.Revisar(paquete, papel, "s1"));
    }
}
