using DeviceHub.RemoteViewer.Input;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Que teclas le arrebata el visor a Windows. Es una tabla, y equivocarse en una
/// fila no da error: da una tecla que actua en la PC del tecnico en vez de en la
/// remota, o una letra que llega dos veces.
/// </summary>
public class RemoteKeyboardTests
{
    private const uint Tab = 0x09, Escape = 0x1B, ImprPant = 0x2C, WinIzq = 0x5B, WinDer = 0x5C;
    private const uint Letra_A = 0x41, F4 = 0x73;

    [Fact]
    public void The_windows_key_is_always_taken()
    {
        // Sin modificadores tambien: sola es lo que abre el menu Inicio.
        Assert.True(TeclasDeWindows.LaAtiendeElShell(WinIzq, alt: false, ctrl: false));
        Assert.True(TeclasDeWindows.LaAtiendeElShell(WinDer, alt: false, ctrl: false));
    }

    [Fact]
    public void Tab_is_taken_only_with_alt()
    {
        Assert.True(TeclasDeWindows.LaAtiendeElShell(Tab, alt: true, ctrl: false));

        // El Tab suelto es una tecla normal. Arrebatarlo aqui lo mandaria dos
        // veces, porque ese ya viaja por PreviewKeyDown.
        Assert.False(TeclasDeWindows.LaAtiendeElShell(Tab, alt: false, ctrl: false));
        Assert.False(TeclasDeWindows.LaAtiendeElShell(Tab, alt: false, ctrl: true));
    }

    [Fact]
    public void Escape_is_taken_only_with_alt_or_ctrl()
    {
        Assert.True(TeclasDeWindows.LaAtiendeElShell(Escape, alt: true, ctrl: false));
        Assert.True(TeclasDeWindows.LaAtiendeElShell(Escape, alt: false, ctrl: true));

        // Escape solo cierra dialogos en la PC remota todo el rato.
        Assert.False(TeclasDeWindows.LaAtiendeElShell(Escape, alt: false, ctrl: false));
    }

    [Fact]
    public void Ordinary_keys_are_left_alone()
    {
        Assert.True(TeclasDeWindows.LaAtiendeElShell(ImprPant, alt: false, ctrl: false));

        // Alt+F4 NO: lo recibe la aplicacion con el foco, asi que ya llega a WPF
        // y lo reenvia el camino normal.
        Assert.False(TeclasDeWindows.LaAtiendeElShell(F4, alt: true, ctrl: false));
        Assert.False(TeclasDeWindows.LaAtiendeElShell(Letra_A, alt: false, ctrl: true));
        Assert.False(TeclasDeWindows.LaAtiendeElShell(Letra_A, alt: true, ctrl: true));
    }
}
