using DeviceHub.RemoteHost.Input;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Fases 9 y 10: conversion de coordenadas de la entrada remota.
///
/// Lo demas de InputInjector es SendInput, que exige un escritorio de verdad
/// delante y no se puede probar en CI. Esto si: es la aritmetica que decide
/// donde acaba el raton, y cuando se equivoca no falla -- hace clic en el sitio
/// equivocado, que es peor.
/// </summary>
public class RemoteInputTests
{
    /// <summary>Un solo monitor 1920x1080 en 0,0: el caso comun.</summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 65535)]
    [InlineData(0.5, 32768)]
    public void One_monitor_maps_edge_to_edge(double normalizado, int esperado)
        => Assert.Equal(esperado, InputInjector.Absoluto(normalizado, 0, 1920, 0, 1920));

    /// <summary>
    /// Segundo monitor a la DERECHA. La pantalla capturada empieza en x=1920 del
    /// escritorio virtual, que mide 3840: su borde izquierdo cae a la mitad.
    ///
    /// Sin la traslacion, un clic en la esquina de este monitor aterrizaria en la
    /// esquina del otro -- y con un solo monitor el fallo es invisible.
    /// </summary>
    [Fact]
    public void A_second_monitor_on_the_right_is_translated()
    {
        Assert.Equal(32768, InputInjector.Absoluto(0.0, 1920, 1920, 0, 3840));
        Assert.Equal(65535, InputInjector.Absoluto(1.0, 1920, 1920, 0, 3840));
    }

    /// <summary>
    /// Segundo monitor a la IZQUIERDA: el escritorio virtual arranca en negativo,
    /// que es la parte que rompe una conversion escrita a ojo.
    /// </summary>
    [Fact]
    public void A_monitor_on_the_left_has_negative_origin()
    {
        Assert.Equal(0, InputInjector.Absoluto(0.0, -1920, 1920, -1920, 3840));
        Assert.Equal(32768, InputInjector.Absoluto(1.0, -1920, 1920, -1920, 3840));
    }

    /// <summary>Resoluciones distintas entre visor y remoto: lo normalizado no se
    /// entera, que es justo por lo que se manda normalizado.</summary>
    [Fact]
    public void The_remote_resolution_does_not_change_the_result()
    {
        Assert.Equal(
            InputInjector.Absoluto(0.25, 0, 1920, 0, 1920),
            InputInjector.Absoluto(0.25, 0, 3840, 0, 3840));
    }

    /// <summary>Un escritorio virtual de tamano cero no divide entre cero. Suena
    /// imposible; pasa mientras Windows reconfigura las pantallas.</summary>
    [Fact]
    public void A_zero_sized_desktop_does_not_divide_by_zero()
        => Assert.Equal(0, InputInjector.Absoluto(0.5, 0, 0, 0, 0));
}
