using Xunit;
using DeviceHub.RemoteViewer.Transferencia;

namespace DeviceHub.Tests;

/// <summary>
/// Las reglas de arrastrar y soltar sobre la ventana del visor.
/// </summary>
public class SoltadoTests
{
    private const string Carpeta = @"D:\Datos";

    [Fact]
    public void Va_a_la_carpeta_abierta_y_solo_con_el_NOMBRE()
    {
        var plan = Soltado.Preparar([@"C:\Users\yahir\informe.pdf"], false, Carpeta);

        Assert.Null(plan.Queja);
        var (local, remoto) = Assert.Single(plan.Subidas);

        Assert.Equal(@"C:\Users\yahir\informe.pdf", local);
        Assert.Equal(@"D:\Datos\informe.pdf", remoto);
    }

    [Fact]
    public void Varios_a_la_vez()
    {
        var plan = Soltado.Preparar([@"C:\a.txt", @"C:\b\c.log"], false, Carpeta);

        Assert.Null(plan.Queja);
        Assert.Equal([@"D:\Datos\a.txt", @"D:\Datos\c.log"], plan.Subidas.Select(s => s.Remoto));
    }

    [Fact]
    public void Sin_carpeta_elegida_no_se_inventa_un_destino()
    {
        // El host corre como SYSTEM: cualquier destino "por defecto" acabaria en
        // el perfil de SYSTEM, donde nadie va a buscar nada.
        var plan = Soltado.Preparar([@"C:\a.txt"], false, "");

        Assert.Empty(plan.Subidas);
        Assert.Contains("carpeta", plan.Queja!);
    }

    [Fact]
    public void Una_carpeta_soltada_lo_dice_en_vez_de_callarse()
    {
        var plan = Soltado.Preparar([], habiaCarpetas: true, Carpeta);

        Assert.Empty(plan.Subidas);
        Assert.Contains("Carpetas", plan.Queja!);
    }

    [Fact]
    public void Nada_utilizable_tambien_lo_dice()
    {
        var plan = Soltado.Preparar([], habiaCarpetas: false, Carpeta);

        Assert.Empty(plan.Subidas);
        Assert.NotNull(plan.Queja);
    }

    [Fact]
    public void No_se_puede_escapar_de_la_carpeta_elegida()
    {
        // GetFileName deja ".." en nada, asi que estas no producen subida. Lo que
        // NO puede pasar es que alguna acabe fuera de D:\Datos.
        var plan = Soltado.Preparar([@"C:\x\..", @"C:\y\"], false, Carpeta);

        foreach (var (_, remoto) in plan.Subidas)
            Assert.StartsWith(Carpeta + @"\", remoto);
    }
}
