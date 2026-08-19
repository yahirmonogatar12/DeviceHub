namespace DeviceHub.RemoteViewer.Input;

/// <summary>
/// Que pulsaciones se queda Windows para si, y por tanto hay que arrebatarle
/// para que lleguen a la PC remota.
///
/// Clase suelta y sin WPF por lo mismo que <see cref="Render.Escalado"/>: es una
/// tabla de decision, y probarla dentro de una Window obligaria al proyecto de
/// pruebas a activar UseWPF entero.
/// </summary>
public static class TeclasDeWindows
{
    private const uint Tab = 0x09;
    private const uint Escape = 0x1B;
    private const uint ImprPant = 0x2C;
    private const uint WinIzquierda = 0x5B;
    private const uint WinDerecha = 0x5C;

    /// <summary>
    /// SOLO las que el shell atiende antes que la aplicacion con el foco. Todo
    /// lo demas llega a WPF por su cuenta y lo reenvia el camino normal:
    /// arrebatarlo aqui tambien lo mandaria dos veces.
    ///
    /// Ctrl+Alt+Supr y Win+L no estan porque no se pueden atrapar desde un
    /// gancho: el primero lo genera el kernel y el segundo lo atiende winlogon.
    /// </summary>
    public static bool LaAtiendeElShell(uint virtualKey, bool alt, bool ctrl) => virtualKey switch
    {
        // La tecla Windows entera, pulsada Y soltada. Quedarse solo con la
        // pulsacion dejaria al Windows local creyendo que sigue hundida.
        WinIzquierda or WinDerecha => true,

        // Alt+Tab. El Tab suelto es una tecla normal y viaja por el camino de
        // siempre.
        Tab => alt,

        // Alt+Esc y Ctrl+Esc, que abre el menu Inicio. Escape solo, no.
        Escape => alt || ctrl,

        ImprPant => true,

        _ => false
    };
}
