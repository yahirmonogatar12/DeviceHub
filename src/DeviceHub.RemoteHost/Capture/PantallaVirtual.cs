using System.Diagnostics;

namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Un monitor de mas en la PC controlada, para trabajar sin taparle la pantalla
/// al operador. Fase 27.
///
/// NO ES UN ESCRITORIO VIRTUAL DE WINDOWS. Esos son la misma pantalla con otras
/// ventanas: cambiar de uno a otro cambia lo que el operador esta viendo, y
/// ademas Desktop Duplication captura el activo, asi que no aisla nada. Esto es
/// un monitor de VERDAD para Windows -- lo sirve un driver de pantalla indirecta
/// -- que existe sin haber nada enchufado.
///
/// EL DRIVER NO VIENE DENTRO. Es de Amyuni (usbmmidd_v2), gratuito, firmado por
/// WHQL y redistribuible; se copia a mano una vez por PC. Sin el, esto contesta
/// que no esta y la sesion sigue como si nada: una funcion que falta no puede
/// convertirse en una sesion que no abre.
///
/// Y VIVE EN ProgramData, NO JUNTO AL AGENTE. La actualizacion mueve la carpeta
/// de instalacion entera y solo rescata appsettings.json, asi que un driver
/// puesto ahi durara exactamente hasta la siguiente version.
///
/// LO QUE ESTO NO ARREGLA: sigue siendo la misma sesion de Windows. Un cursor,
/// un foco y un portapapeles para los dos. Mover el raton al monitor virtual se
/// lo quita de la pantalla al operador. Aislamiento de verdad pide una segunda
/// sesion, y Windows 11 Pro no da mas de una.
/// </summary>
public static class PantallaVirtual
{
    private const string Carpeta = @"C:\ProgramData\ILSANSYSTEM\DeviceHub\usbmmidd_v2";

    private static string Instalador => Path.Combine(Carpeta, "deviceinstaller64.exe");

    /// <summary>Si el driver esta copiado en esta PC.</summary>
    public static bool Disponible => File.Exists(Instalador);

    public static string DondeVa => Carpeta;

    /// <summary>Cuantos monitores hay ahora mismo. Se usa para esperar al que
    /// aparece: enableidd vuelve antes de que Windows lo haya enganchado.</summary>
    private static int Cuantas() => Pantallas.Listar().Count;

    /// <summary>
    /// Enciende el monitor virtual y devuelve su id, o -1 si no se pudo.
    ///
    /// Se mira la lista ANTES y DESPUES en vez de buscar el driver por nombre:
    /// el nombre del adaptador depende de la version del driver y del idioma, y
    /// el que aparezca de mas es el que es, se llame como se llame.
    /// </summary>
    public static int Encender(out string queja)
    {
        if (!Disponible)
        {
            queja = $"No hay driver de pantalla virtual en esta PC. Copia usbmmidd_v2 en {Carpeta}.";
            return -1;
        }

        var antes = Pantallas.Listar().Select(p => p.Nombre).ToHashSet(StringComparer.Ordinal);

        // La primera vez hay que instalar el driver; despues basta con activarlo.
        // Se intenta activar primero para no reinstalar en cada sesion.
        if (!Correr("enableidd 1", out queja) &&
            (!Correr("install usbmmidd.inf usbmmidd", out queja) || !Correr("enableidd 1", out queja)))
        {
            return -1;
        }

        // Windows tarda en engancharlo. Cinco segundos es de sobra y no deja la
        // sesion colgada si el driver se instalo mal.
        var limite = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < limite)
        {
            var nueva = Pantallas.Listar().FirstOrDefault(p => !antes.Contains(p.Nombre));

            if (nueva is not null)
            {
                queja = $"Pantalla virtual anadida: {nueva.Ancho}x{nueva.Alto}.";
                return nueva.Id;
            }

            Thread.Sleep(200);
        }

        queja = "El driver acepto la orden pero Windows no engancho ninguna pantalla nueva.";
        return -1;
    }

    /// <summary>
    /// Quita el monitor virtual. Silencioso si no habia ninguno o no hay driver:
    /// se llama tambien al cerrar la sesion, donde no hay nadie a quien avisar.
    /// </summary>
    public static bool Apagar(out string queja)
    {
        if (!Disponible)
        {
            queja = string.Empty;
            return true;
        }

        var bien = Correr("enableidd 0", out queja);

        if (bien)
            queja = "Pantalla virtual quitada.";

        return bien;
    }

    private static bool Correr(string argumentos, out string queja)
    {
        try
        {
            using var proceso = Process.Start(new ProcessStartInfo
            {
                FileName = Instalador,
                Arguments = argumentos,
                WorkingDirectory = Carpeta,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (proceso is null)
            {
                queja = "No se pudo lanzar el instalador del driver.";
                return false;
            }

            var salida = proceso.StandardOutput.ReadToEnd();
            var error = proceso.StandardError.ReadToEnd();

            // Treinta segundos: instalar un driver despierta al Administrador de
            // dispositivos y eso no siempre es instantaneo. Sin tope, una
            // instalacion atascada se lleva por delante el hilo de red.
            if (!proceso.WaitForExit(30_000))
            {
                try { proceso.Kill(entireProcessTree: true); } catch (Exception) { }

                queja = "El instalador del driver se quedo colgado.";
                return false;
            }

            queja = proceso.ExitCode == 0
                ? string.Empty
                : $"El driver contesto {proceso.ExitCode}: {(error + salida).Trim()}";

            return proceso.ExitCode == 0;
        }
        catch (Exception ex)
        {
            queja = $"No se pudo usar el driver de pantalla virtual: {ex.Message}";
            return false;
        }
    }
}
