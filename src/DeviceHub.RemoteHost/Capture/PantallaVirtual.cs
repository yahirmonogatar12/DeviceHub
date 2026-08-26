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
/// EL DRIVER ES DE UN TERCERO: Amyuni usbmmidd_v2, gratuito, firmado por WHQL y
/// redistribuible. Viaja DENTRO del paquete del agente, asi que llega a la flota
/// con una actualizacion normal y no hay que tocar ninguna PC. Sin el, esto
/// contesta que no esta y la sesion sigue como si nada: una funcion que falta no
/// puede convertirse en una sesion que no abre.
///
/// SE BUSCA EN DOS SITIOS, y el orden importa. Primero junto al agente, que es
/// donde lo deja la actualizacion. Despues en ProgramData, que es donde se copia
/// a mano en una PC suelta -- ese sitio sobrevive a las actualizaciones porque el
/// actualizador mueve la carpeta de instalacion entera y solo rescata
/// appsettings.json.
///
/// QUE ESTE NO SIGNIFICA QUE HAYA UN MONITOR DE MAS. El driver se registra la
/// primera vez que alguien lo pide, y el monitor solo existe mientras esta
/// encendido. Registrarlo y desregistrarlo en cada sesion seria despertar al
/// Administrador de dispositivos varias veces al dia en una PC de produccion,
/// para nada.
///
/// LO QUE ESTO NO ARREGLA: sigue siendo la misma sesion de Windows. Un cursor,
/// un foco y un portapapeles para los dos. Mover el raton al monitor virtual se
/// lo quita de la pantalla al operador. Aislamiento de verdad pide una segunda
/// sesion, y Windows 11 Pro no da mas de una.
/// </summary>
public static class PantallaVirtual
{
    /// <summary>Donde se copia a mano en una PC suelta. Fuera de la carpeta de
    /// instalacion a proposito: ahi sobrevive a las actualizaciones.</summary>
    public const string Refugio = @"C:\ProgramData\ILSANSYSTEM\DeviceHub\usbmmidd_v2";

    /// <summary>Junto al agente, que es donde lo deja la actualizacion.</summary>
    private static string ConElAgente =>
        Path.Combine(AppContext.BaseDirectory, "usbmmidd_v2");

    /// <summary>La carpeta que de verdad tiene el driver, o null.</summary>
    private static string? Donde
    {
        get
        {
            foreach (var sitio in (string[])[ConElAgente, Refugio])
            {
                if (File.Exists(Path.Combine(sitio, "deviceinstaller64.exe")))
                    return sitio;
            }

            return null;
        }
    }

    /// <summary>Si el driver esta en esta PC.</summary>
    public static bool Disponible => Donde is not null;

    public static string DondeVa => Refugio;

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
        if (Donde is null)
        {
            queja = "Esta version del agente no trae el driver de pantalla virtual. " +
                    $"Actualiza el agente, o copia usbmmidd_v2 en {Refugio}.";

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
    /// Quita TODOS los monitores virtuales. Silencioso si no habia ninguno o no
    /// hay driver: se llama tambien al cerrar la sesion, donde no hay nadie a
    /// quien avisar.
    ///
    /// EN BUCLE, Y NO POR PRUDENCIA. Lo dicen las instrucciones del driver:
    /// "enableidd 1" se puede repetir para anadir hasta CUATRO monitores, y
    /// "enableidd 0" quita uno por llamada. Con una sola llamada, un tecnico que
    /// pulsara "Anadir" dos veces dejaria un monitor fantasma en esa PC de
    /// planta -- y en un monitor que no existe fisicamente se pueden perder
    /// ventanas para siempre.
    ///
    /// Se para cuando la cuenta de pantallas deja de bajar, que es la senal de
    /// que ya no queda ninguna virtual. El tope de cuatro es el del driver.
    /// </summary>
    public static bool Apagar(out string queja)
    {
        if (!Disponible)
        {
            queja = string.Empty;
            return true;
        }

        var quitadas = 0;

        for (var intento = 0; intento < MaximoDelDriver; intento++)
        {
            var antes = Pantallas.Listar().Count;

            if (!Correr("enableidd 0", out queja))
                break;

            // El driver contesta bien aunque no hubiera ninguna que quitar, asi
            // que quien dice si paso algo es la lista de pantallas.
            if (Pantallas.Listar().Count >= antes)
                break;

            quitadas++;
        }

        queja = quitadas == 0
            ? string.Empty
            : $"Pantalla virtual quitada{(quitadas > 1 ? $" (x{quitadas})" : string.Empty)}.";

        return true;
    }

    /// <summary>Cuantos monitores admite el driver a la vez. Sale de sus propias
    /// instrucciones, no de una suposicion.</summary>
    private const int MaximoDelDriver = 4;

    private static bool Correr(string argumentos, out string queja)
    {
        if (Donde is not { } carpeta)
        {
            queja = "No hay driver de pantalla virtual en esta PC.";
            return false;
        }

        try
        {
            using var proceso = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(carpeta, "deviceinstaller64.exe"),
                Arguments = argumentos,

                // El .inf se nombra relativo, asi que el directorio de trabajo
                // TIENE que ser el del driver o la instalacion no encuentra nada.
                WorkingDirectory = carpeta,
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
