using Xunit;
using DeviceHub.Archivos;

namespace DeviceHub.Tests;

/// <summary>
/// Donde cae cada archivo al copiar una carpeta.
///
/// Es la unica parte de esto que se puede probar sin disco, y es justo donde
/// equivocarse no da error: una ruta relativa mal calculada no falla, deja los
/// archivos desparramados en el sitio equivocado -- y eso se descubre mucho
/// despues, cuando alguien busca un plano que no esta donde deberia.
/// </summary>
public class ExpandirTests
{
    [Fact]
    public void La_carpeta_copiada_conserva_su_nombre()
    {
        // Copiar "Planos" pega "Planos", no su contenido suelto. Por eso lo
        // relativo se mide desde el PADRE de la raiz.
        Assert.Equal(
            Path.Combine("Planos", "a.dwg"),
            Expandir.Relativa(@"C:\trabajo\Planos", @"C:\trabajo\Planos\a.dwg"));
    }

    [Fact]
    public void Las_subcarpetas_se_conservan_enteras()
    {
        Assert.Equal(
            Path.Combine("Planos", "2026", "enero", "a.dwg"),
            Expandir.Relativa(@"C:\trabajo\Planos", @"C:\trabajo\Planos\2026\enero\a.dwg"));
    }

    [Fact]
    public void Un_archivo_suelto_es_solo_su_nombre()
    {
        Assert.Equal("informe.docx", Expandir.Relativa(@"C:\trabajo\informe.docx", @"C:\trabajo\informe.docx"));
    }

    [Fact]
    public void La_barra_final_no_cambia_el_resultado()
    {
        // El Explorador y CF_HDROP no se ponen de acuerdo en si una carpeta
        // acaba en barra. Si eso cambiara el arbol, la misma copia daria dos
        // resultados distintos segun de donde viniera.
        Assert.Equal(
            Expandir.Relativa(@"C:\trabajo\Planos", @"C:\trabajo\Planos\a.dwg"),
            Expandir.Relativa(@"C:\trabajo\Planos\", @"C:\trabajo\Planos\a.dwg"));
    }

    [Fact]
    public void Lo_que_cae_fuera_del_arbol_se_deja_plano()
    {
        // Pasa mezclando unidades o rutas de red en una seleccion. Inventarle
        // un sitio con ".." escribiria FUERA del deposito, que es peor que
        // perder la estructura.
        var suelto = Expandir.Relativa(@"C:\trabajo\Planos", @"D:\otra\cosa.txt");

        Assert.Equal("cosa.txt", suelto);
        Assert.DoesNotContain("..", suelto);
    }
}
