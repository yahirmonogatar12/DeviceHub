using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeviceHub.RemoteHost.Input;

/// <summary>
/// Ordenes que no son entrada. Fase 21.
///
/// Ninguna pasa por SendInput: bloquear la estacion, congelar el raton y
/// reiniciar no son teclas, y no hay combinacion que las produzca.
///
/// Todas devuelven un texto para el registro en vez de lanzar. Son gestos
/// manuales del tecnico, y que uno falle no es motivo para tirar una sesion que
/// por lo demas funciona.
/// </summary>
public static class HostActions
{
    /// <summary>
    /// Bloquea la estacion. No hace falta privilegio: bloquea la sesion del
    /// usuario que llama, que es la que estamos controlando.
    /// </summary>
    public static string Bloquear()
        => LockWorkStation()
            ? "Estacion bloqueada."
            : $"No se pudo bloquear la estacion (error {Marshal.GetLastWin32Error()}).";

    /// <summary>
    /// Congela el raton y el teclado FISICOS de la PC remota.
    ///
    /// Tres limites que hay que saber antes de confiar en esto, y que Windows
    /// impone a proposito:
    ///
    ///   - Exige que el proceso corra elevado. Como usuario normal falla, y el
    ///     mensaje lo dice en vez de fingir que quedo bloqueado.
    ///   - Ctrl+Alt+Supr lo desactiva SIEMPRE. Es la salida de emergencia de
    ///     quien esta delante de la PC, y no se puede quitar.
    ///   - Windows lo levanta solo si el proceso que lo pidio muere. Eso es lo
    ///     que evita dejar una PC de planta inutilizable si el host se cae.
    ///
    /// Y la que decide donde vive esta llamada: la exencion es del HILO, no del
    /// proceso. Solo el hilo que bloqueo puede desbloquear, y solo su SendInput
    /// sigue entrando mientras dure. Por eso lo llama devicehub-entrada, que es
    /// el mismo que inyecta: pedirlo desde el hilo de red dejaba una PC de
    /// planta congelada para todos, el tecnico incluido.
    /// </summary>
    public static bool Congelar(bool congelar, out string mensaje)
    {
        if (BlockInput(congelar))
        {
            mensaje = congelar
                ? "Entrada local congelada. Ctrl+Alt+Supr la reactiva desde alla."
                : "Entrada local reactivada.";

            return true;
        }

        mensaje = $"No se pudo {(congelar ? "congelar" : "reactivar")} la entrada local " +
                  $"(error {Marshal.GetLastWin32Error()}; suele ser falta de elevacion).";

        return false;
    }

    /// <summary>
    /// Reinicia.
    ///
    /// Con shutdown.exe y no con ExitWindowsEx: reiniciar exige SeShutdownPrivilege
    /// habilitado en el token, y shutdown.exe ya lo hace por su cuenta. Evita
    /// cuarenta lineas de OpenProcessToken + LookupPrivilegeValue +
    /// AdjustTokenPrivileges para llegar al mismo sitio.
    /// </summary>
    public static string Reiniciar()
    {
        try
        {
            // /t 0 y no /f: /f mata las aplicaciones sin dejarlas guardar, y esto
            // se dispara sobre una PC donde alguien puede estar trabajando.
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })?.Dispose();

            return "Reinicio solicitado.";
        }
        catch (Exception ex)
        {
            return $"No se pudo reiniciar: {ex.Message}";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool block);
}
