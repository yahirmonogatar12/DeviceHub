using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DeviceHub.Archivos;
using DeviceHub.Remote.Contracts;
using Google.Protobuf;
using Microsoft.Win32.SafeHandles;

namespace DeviceHub.RemoteHost.Input;

/// <summary>
/// El lado de RemoteHost del pegado al estilo RustDesk: arranca el ayudante y
/// le pasa los mensajes. El trabajo de verdad esta en <see cref="PortapapelesVirtual"/>.
///
/// POR QUE OTRO PROCESO. RemoteHost corre como SYSTEM (Fase 19), y lo que se
/// pone en el portapapeles no son bytes sino un objeto COM al que el Explorador
/// del usuario llama mientras pega. Un Explorador de integridad media llamando a
/// un proceso SYSTEM choca con la seguridad de COM y con la de integridad, y
/// abrirlas para eso seria abrirle a cualquiera de la sesion todo el proceso.
/// El ayudante corre como el usuario, igual que el Explorador, y no hay nada que
/// abrir. Es la misma razon por la que RustDesk lo hace en su proceso "cm".
/// </summary>
public static class AyudanteDePortapapeles
{
    /// <summary>Por donde salen las peticiones hacia el visor. Lo pone el hilo
    /// de red cada vez que (re)conecta: un trozo pedido tiene que salir por el
    /// stream vivo, no por el de antes del microcorte.</summary>
    public static Action<RemotePacket>? Enviar;

    private static readonly object Cerrojo = new();
    private static Stream? _haciaAyudante;

    /// <summary>Rutas con una peticion en vuelo. Solo esas se le entregan al
    /// ayudante; cualquier otro FileChunk es una subida normal.</summary>
    private static readonly ConcurrentDictionary<string, byte> Pedidos = new(StringComparer.OrdinalIgnoreCase);

    private static TaskCompletionSource<string?>? _confirmacion;

    /// <summary>
    /// Pone en el portapapeles de esta PC lo que el tecnico copio en la suya.
    ///
    /// BLOQUEA hasta que esta puesto, y a proposito: lo llama el hilo de red, y
    /// el Ctrl+V que el tecnico pulsa justo despues viene detras en el mismo
    /// stream. Si no se esperase, el primer pegado de la sesion -- el que paga
    /// el arranque del ayudante -- pegaria lo de antes.
    /// </summary>
    public static string Ofrecer(ClipboardFiles anuncio)
    {
        var confirmacion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmacion = confirmacion;

        try
        {
            Mandar(new RemotePacket { ClipboardFiles = anuncio });
        }
        catch (Exception ex)
        {
            return $"No se pudo preparar el pegado de archivos: {ex.Message}";
        }

        if (!confirmacion.Task.Wait(TimeSpan.FromSeconds(5)))
            return "El portapapeles de esta PC no contesto a tiempo.";

        var archivos = anuncio.Entries.Count;

        return confirmacion.Task.Result is { } error
            ? $"No se pudo preparar el pegado de archivos: {error}"
            : $"{archivos} archivo(s) del tecnico listos para pegar con Ctrl+V.";
    }

    /// <summary>Un trozo que llega del visor. True si era para el ayudante.</summary>
    public static bool Entregar(FileChunk trozo)
    {
        if (!Pedidos.TryRemove(trozo.Path, out _))
            return false;

        try
        {
            Mandar(new RemotePacket { FileChunk = trozo });
        }
        catch (Exception)
        {
            // El ayudante murio, y Mandar puede haber intentado arrancar otro y
            // fallado. Lo que estaba pegando ya fallo por su lado; esto lo llama
            // el hilo de red de la sesion, y nada de aqui puede tumbarlo.
        }

        return true;
    }

    private static void Mandar(RemotePacket paquete)
    {
        lock (Cerrojo)
        {
            _haciaAyudante ??= Arrancar();

            try
            {
                paquete.WriteDelimitedTo(_haciaAyudante);
                _haciaAyudante.Flush();
            }
            catch (IOException)
            {
                // Se murio: el siguiente intento lo vuelve a arrancar.
                _haciaAyudante = null;
                throw;
            }
        }
    }

    private static void Leer(Stream desdeAyudante)
    {
        try
        {
            while (true)
            {
                var paquete = RemotePacket.Parser.ParseDelimitedFrom(desdeAyudante);

                switch (paquete.PayloadCase)
                {
                    case RemotePacket.PayloadOneofCase.FileAck:
                        // "Mandame desde este byte": la peticion del Explorador
                        // de aqui, camino del visor.
                        Pedidos[paquete.FileAck.Path] = 0;
                        Enviar?.Invoke(paquete);
                        break;

                    case RemotePacket.PayloadOneofCase.ClipboardFiles:
                        _confirmacion?.TrySetResult(null);
                        break;

                    case RemotePacket.PayloadOneofCase.Error:
                        _confirmacion?.TrySetResult(paquete.Error.Detail);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Fin del ayudante. Lo que queda colgado es el pegado que estuviera
            // en curso, y ese ya lo da por fallido el Explorador.
        }
    }

    // ------------------------------------------------- el proceso ayudante

    /// <summary>
    /// El proceso ayudante: <c>DeviceHub.RemoteHost.exe --portapapeles</c>.
    ///
    /// Habla con RemoteHost por stdin/stdout con los mismos RemotePacket de la
    /// sesion, delimitados: le llega el anuncio (ClipboardFiles) y los trozos
    /// (FileChunk), y pide cada trozo con un FileAck -- "mandame desde este
    /// byte", que es justo lo que un acuse significa en la subida.
    ///
    /// Muere cuando se cierra su entrada, que es cuando muere RemoteHost, y al
    /// morir se lleva la promesa del portapapeles.
    /// </summary>
    public static int Correr()
    {
        var entrada = Console.OpenStandardInput();
        var salida = Console.OpenStandardOutput();
        var cerrojo = new object();
        var pendientes = new ConcurrentDictionary<string, TaskCompletionSource<FileChunk>>(
            StringComparer.OrdinalIgnoreCase);

        void Mandar(RemotePacket paquete)
        {
            lock (cerrojo)
            {
                paquete.WriteDelimitedTo(salida);
                salida.Flush();
            }
        }

        FileChunk Pedir(string ruta, ulong desde)
        {
            // Una peticion por archivo a la vez: el Explorador lee cada flujo en
            // orden y no vuelve a llamar hasta tener el trozo anterior.
            //
            // ponytail: un trozo de 60 KB por ida y vuelta. En la red de planta
            // son decenas de MB/s; si hace falta mas, pedir varios por delante.
            var espera = new TaskCompletionSource<FileChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendientes[ruta] = espera;

            Mandar(new RemotePacket { FileAck = new FileAck { Path = ruta, Received = desde } });

            return espera.Task.Wait(TimeSpan.FromSeconds(30))
                ? espera.Task.Result
                : new FileChunk { Path = ruta, Offset = desde, Error = "La PC del tecnico no contesto." };
        }

        var portapapeles = new PortapapelesVirtual(Pedir);

        while (true)
        {
            RemotePacket paquete;

            try
            {
                paquete = RemotePacket.Parser.ParseDelimitedFrom(entrada);
            }
            catch (Exception)
            {
                // Fin de la entrada: RemoteHost cerro o murio.
                break;
            }

            switch (paquete.PayloadCase)
            {
                case RemotePacket.PayloadOneofCase.ClipboardFiles:
                    // FUERA de este hilo. Si el Explorador esta a mitad de un
                    // pegado, el hilo del portapapeles esta esperando un trozo
                    // que solo este bucle puede entregar: esperar aqui a que lo
                    // atienda seria un abrazo mortal.
                    var anuncio = paquete.ClipboardFiles;

                    _ = Task.Run(() =>
                    {
                        var error = portapapeles.Ofrecer(anuncio);

                        Mandar(error is null
                            ? new RemotePacket { ClipboardFiles = new ClipboardFiles { Apply = true } }
                            : new RemotePacket { Error = new RemoteError { Detail = error } });
                    });

                    break;

                case RemotePacket.PayloadOneofCase.FileChunk:
                    if (pendientes.TryRemove(paquete.FileChunk.Path, out var espera))
                        espera.TrySetResult(paquete.FileChunk);

                    break;
            }
        }

        // Lo que estuviera esperando un trozo se suelta con error, no se cuelga.
        foreach (var espera in pendientes.Values)
            espera.TrySetResult(new FileChunk { Error = "La sesion remota termino." });

        portapapeles.Cerrar();
        return 0;
    }

    // --------------------------------------------------------------- arranque

    private static Stream Arrancar()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("No se sabe donde esta DeviceHub.RemoteHost.exe");

        if (!WindowsIdentity.GetCurrent().IsSystem)
        {
            // Ya somos el usuario (SecureDesktop apagado, o desarrollo): basta
            // un Process.Start normal.
            var proceso = Process.Start(new ProcessStartInfo(exe, "--portapapeles")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("No arranco el ayudante del portapapeles");

            Hilo(proceso.StandardOutput.BaseStream);
            return proceso.StandardInput.BaseStream;
        }

        var hacia = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var desde = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        try
        {
            LanzarComoUsuario(exe, hacia.ClientSafePipeHandle, desde.ClientSafePipeHandle);
        }
        catch
        {
            hacia.Dispose();
            desde.Dispose();
            throw;
        }

        // Las puntas del ayudante ya son suyas. Sin soltarlas aqui, su salida
        // nunca daria fin de archivo: seguiria habiendo un escritor abierto.
        hacia.DisposeLocalCopyOfClientHandle();
        desde.DisposeLocalCopyOfClientHandle();

        Hilo(desde);
        return hacia;
    }

    private static void Hilo(Stream desdeAyudante)
        => new Thread(() => Leer(desdeAyudante)) { IsBackground = true, Name = "ayudante portapapeles" }.Start();

    /// <summary>
    /// El ayudante con el token del usuario de ESTA sesion, y sus stdin/stdout
    /// enganchados a las dos tuberias.
    ///
    /// WTSQueryUserToken exige SeTcbPrivilege, que este proceso tiene por ser
    /// SYSTEM. Es lo mismo que hacia el agente para lanzar RemoteHost antes de
    /// la Fase 19.
    ///
    /// ponytail: bInheritHandles hereda TODOS los handles heredables, no solo
    /// las tuberias. .NET los crea no heredables, asi que hoy son solo estas;
    /// si eso cambia, PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
    /// </summary>
    private static void LanzarComoUsuario(string exe, SafePipeHandle entrada, SafePipeHandle salida)
    {
        var sesion = (uint)Process.GetCurrentProcess().SessionId;

        if (!WTSQueryUserToken(sesion, out var suplantacion))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Nadie logueado en la sesion {sesion}");

        using (suplantacion)
        {
            if (!DuplicateTokenEx(suplantacion, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary,
                    out var primario))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            }

            using (primario)
            {
                var inicio = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfo>(),
                    lpDesktop = @"winsta0\default",
                    dwFlags = StartfUseStdHandles,
                    hStdInput = entrada.DangerousGetHandle(),
                    hStdOutput = salida.DangerousGetHandle()
                };

                // CreateProcessAsUser puede ESCRIBIR en la linea de comandos: no
                // se le puede pasar una cadena, igual que en el agente.
                var linea = new System.Text.StringBuilder($"\"{exe}\" --portapapeles");

                if (!CreateProcessAsUser(primario, null, linea, IntPtr.Zero, IntPtr.Zero,
                        bInheritHandles: true, CreateNoWindow, IntPtr.Zero, null, ref inicio, out var proceso))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
                }

                CloseHandle(proceso.hThread);
                CloseHandle(proceso.hProcess);
            }
        }
    }

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sesion, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existente, uint acceso, IntPtr atributos, int nivel, int tipo,
        out SafeAccessTokenHandle nuevo);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token, string? aplicacion, System.Text.StringBuilder lineaDeComandos,
        IntPtr atributosProceso, IntPtr atributosHilo, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint banderas, IntPtr entorno, string? directorio,
        ref StartupInfo inicio, out ProcessInformation proceso);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr mango);
}
