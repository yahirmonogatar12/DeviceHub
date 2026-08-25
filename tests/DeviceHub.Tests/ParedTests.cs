using Xunit;
using DeviceHub.RemoteViewer.Render;

namespace DeviceHub.Tests;

/// <summary>
/// Donde cae cada mosaico. Lo que se arregla aqui son los cuadros negros: el
/// sobrante deja de repartirse en bandas entre filas y en una celda vacia en una
/// esquina, y pasa a ser un margen alrededor de la pared.
/// </summary>
public class ParedTests
{
    private const double Ancho = 1920, Alto = 975;
    private const double Aspecto = 16.0 / 9.0;

    [Fact]
    public void Todos_los_mosaicos_miden_lo_mismo_y_con_la_forma_de_la_pantalla()
    {
        foreach (var n in new[] { 2, 3, 4, 5, 6, 7, 9, 12 })
        {
            var huecos = Pared.Repartir(n, Ancho, Alto, Aspecto);

            Assert.Equal(n, huecos.Length);

            foreach (var hueco in huecos)
            {
                Assert.Equal(huecos[0].Ancho, hueco.Ancho, 6);
                Assert.Equal(huecos[0].Alto, hueco.Alto, 6);

                // La forma es la de la pantalla remota: por eso no queda banda
                // negra DENTRO del mosaico.
                Assert.Equal(Aspecto, hueco.Ancho / hueco.Alto, 6);
            }
        }
    }

    [Fact]
    public void Ninguno_se_sale_ni_se_solapa()
    {
        foreach (var n in Enumerable.Range(2, 15))
        {
            var huecos = Pared.Repartir(n, Ancho, Alto, Aspecto);

            foreach (var hueco in huecos)
            {
                Assert.True(hueco.X >= -0.001 && hueco.Y >= -0.001, $"{n}: {hueco} se sale por arriba");
                Assert.True(hueco.X + hueco.Ancho <= Ancho + 0.001, $"{n}: {hueco} se sale a la derecha");
                Assert.True(hueco.Y + hueco.Alto <= Alto + 0.001, $"{n}: {hueco} se sale por abajo");
            }

            for (var i = 0; i < huecos.Length; i++)
            for (var j = i + 1; j < huecos.Length; j++)
            {
                var a = huecos[i];
                var b = huecos[j];

                var pisan = a.X < b.X + b.Ancho - 0.001 && b.X < a.X + a.Ancho - 0.001
                         && a.Y < b.Y + b.Alto - 0.001 && b.Y < a.Y + a.Alto - 0.001;

                Assert.False(pisan, $"{n}: el {i} y el {j} se pisan");
            }
        }
    }

    [Fact]
    public void La_ultima_fila_incompleta_va_CENTRADA()
    {
        // EL CUADRO NEGRO DE LA ESQUINA. Cinco pantallas van en 3x2, asi que la
        // fila de abajo lleva dos y sobra un hueco: pegadas a la izquierda, ese
        // hueco queda de rectangulo negro a la derecha; centradas, se parte en
        // dos margenes iguales.
        var huecos = Pared.Repartir(5, Ancho, Alto, Aspecto);

        var izquierda = huecos[3].X;
        var derecha = Ancho - (huecos[4].X + huecos[4].Ancho);

        Assert.Equal(izquierda, derecha, 6);

        // Y de verdad esta descolocada respecto a la fila de arriba, o no
        // estaria centrada.
        Assert.True(huecos[3].X > huecos[0].X + 1);
    }

    [Fact]
    public void La_pared_entera_va_centrada_en_el_hueco()
    {
        var huecos = Pared.Repartir(6, Ancho, Alto, Aspecto);

        var arriba = huecos[0].Y;
        var abajo = Alto - (huecos[5].Y + huecos[5].Alto);

        Assert.Equal(arriba, abajo, 6);
    }

    [Fact]
    public void Una_sola_ocupa_todo_sin_encajar()
    {
        // En vista normal manda el menu de Vista de la sesion -- original,
        // 150 %, 200 % -- y encajarla aqui le quitaria el sitio a su scroll.
        var huecos = Pared.Repartir(1, Ancho, Alto, Aspecto);

        Assert.Single(huecos);
        Assert.Equal(new Pared.Hueco(0, 0, Ancho, Alto), huecos[0]);
    }

    [Fact]
    public void Los_mosaicos_crecieron_respecto_al_3x3_de_antes()
    {
        // Seis pantallas: WPF hacia la cuadricula cuadrada -- 3x3 -- y salian
        // mosaicos de 578x325 con una fila entera negra debajo. Con 3x2 el
        // mosaico crece, y eso es lo que se gana aunque el margen se note mas.
        var ahora = Pared.Repartir(6, Ancho, Alto, Aspecto)[0];
        var antes = Math.Min(Ancho / 3, Alto / 3 * Aspecto);

        Assert.True(ahora.Ancho > antes, $"{ahora.Ancho:0} no es mayor que {antes:0}");
    }

    [Fact]
    public void Sin_hueco_medido_no_revienta()
    {
        var huecos = Pared.Repartir(6, 0, 0, Aspecto);

        Assert.Equal(6, huecos.Length);
    }

    [Fact]
    public void Los_mosaicos_de_una_fila_van_PEGADOS()
    {
        // Se probo con 4 px de separacion y el usuario la rechazo: quiere la
        // pared continua, como estaba antes.
        var huecos = Pared.Repartir(6, Ancho, Alto, Aspecto);

        Assert.Equal(huecos[0].X + huecos[0].Ancho, huecos[1].X, 6);
        Assert.Equal(huecos[0].Y + huecos[0].Alto, huecos[3].Y, 6);
    }
}
