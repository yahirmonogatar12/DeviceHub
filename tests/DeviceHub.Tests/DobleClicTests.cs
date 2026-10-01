using DeviceHub.Remote.Contracts;
using DeviceHub.RemoteViewer;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// La ventana del video lleva CS_DBLCLKS, asi que el segundo clic rapido llega
/// como WM_xBUTTONDBLCLK. Si eso no se traduce a una pulsacion, la PC remota
/// recibe pulsar-soltar-soltar y el doble clic no abre nada.
/// </summary>
public class DobleClicTests
{
    [Theory]
    [InlineData(0x0203, MouseButtonId.MouseButtonLeft)]
    [InlineData(0x0206, MouseButtonId.MouseButtonRight)]
    [InlineData(0x0209, MouseButtonId.MouseButtonMiddle)]
    public void El_segundo_clic_de_un_doble_clic_es_una_pulsacion(int mensaje, MouseButtonId boton)
        => Assert.Equal((boton, true), SesionRemota.Boton(mensaje));
}
