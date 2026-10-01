using System.Buffers.Binary;
using System.Text;
using DeviceHub.Archivos;
using DeviceHub.Remote.Contracts;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Pegar con Ctrl+V lo copiado en la PC del tecnico.
///
/// Lo que se prueba es la lista que lee el Explorador: un desplazamiento mal
/// puesto no da error, da nombres cortados o tamanos absurdos en el dialogo de
/// copia, y eso solo se ve pegando en una PC de planta.
/// </summary>
public class PortapapelesVirtualTests
{
    [Fact]
    public void El_descriptor_tiene_el_formato_que_lee_el_Explorador()
    {
        var bytes = DescriptorDeArchivos.Construir(
        [
            new Entrada(string.Empty, "Planos", 0, Carpeta: true),
            new Entrada(@"C:\x\grande.bin", @"Planos\grande.bin", (5UL << 32) + 7, Carpeta: false)
        ]);

        Assert.Equal(4 + 2 * DescriptorDeArchivos.Tamano, bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        var carpeta = bytes.AsSpan(4, DescriptorDeArchivos.Tamano);
        Assert.Equal(DescriptorDeArchivos.AtributoCarpeta, BinaryPrimitives.ReadUInt32LittleEndian(carpeta[36..]));
        Assert.Equal("Planos", Nombre(carpeta));

        // Mas de 4 GB: la parte alta tiene que ir en nFileSizeHigh.
        var archivo = bytes.AsSpan(4 + DescriptorDeArchivos.Tamano, DescriptorDeArchivos.Tamano);
        Assert.Equal(DescriptorDeArchivos.AtributoNormal, BinaryPrimitives.ReadUInt32LittleEndian(archivo[36..]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(archivo[64..]));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(archivo[68..]));
        Assert.Equal(@"Planos\grande.bin", Nombre(archivo));
    }

    /// <summary>El Explorador crea las entradas en orden: una carpeta tiene que
    /// salir antes que lo que cuelga de ella, y una vacia tambien tiene que
    /// salir.</summary>
    [Fact]
    public void Las_carpetas_salen_antes_que_sus_hijas_y_las_vacias_tambien()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "devicehub-carpetas-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(raiz, "a", "b"));
        Directory.CreateDirectory(Path.Combine(raiz, "vacia"));

        try
        {
            var carpetas = Expandir.Carpetas([raiz]);
            var nombre = Path.GetFileName(raiz);

            Assert.Equal(nombre, carpetas[0]);
            Assert.Contains(Path.Combine(nombre, "vacia"), carpetas);
            Assert.True(
                carpetas.IndexOf(Path.Combine(nombre, "a")) < carpetas.IndexOf(Path.Combine(nombre, "a", "b")));
        }
        finally
        {
            Directory.Delete(raiz, recursive: true);
        }
    }

    /// <summary>Lo que sirve cada lado cuando el Explorador de enfrente pide un
    /// trozo: desde donde pide, marcando el ultimo, y un fallo DENTRO del trozo
    /// -- alla hay un Explorador esperando respuesta, no una excepcion.</summary>
    [Fact]
    public void Un_trozo_sale_desde_donde_se_pide_y_un_fallo_viaja_dentro()
    {
        var ruta = Path.Combine(Path.GetTempPath(), "devicehub-trozo-" + Guid.NewGuid().ToString("N")[..8]);
        var datos = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(ruta, datos);

        try
        {
            var primero = TrozoDeArchivo.Leer(ruta, 0, (ulong)datos.Length);
            Assert.Equal(TrozoDeArchivo.Tamano, primero.Data.Length);
            Assert.False(primero.Last);

            var resto = TrozoDeArchivo.Leer(ruta, (ulong)TrozoDeArchivo.Tamano, (ulong)datos.Length);
            Assert.True(resto.Last);
            Assert.Equal(datos.AsSpan(TrozoDeArchivo.Tamano).ToArray(), resto.Data.ToByteArray());
        }
        finally
        {
            File.Delete(ruta);
        }

        Assert.NotEmpty(TrozoDeArchivo.Leer(ruta, 0, 10).Error);
    }

    private static string Nombre(ReadOnlySpan<byte> descriptor)
    {
        var texto = Encoding.Unicode.GetString(descriptor.Slice(72, 520));
        return texto[..texto.IndexOf('\0')];
    }
}
