using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DeviceHub.Remote.Contracts;

namespace DeviceHub.Agent.Remote;

/// <summary>
/// Lanza DeviceHub.RemoteHost DENTRO de la sesion interactiva del usuario.
///
/// El agente corre como servicio en la Session 0, que no tiene escritorio: si
/// arrancara el host ahi, Desktop Duplication capturaria una pantalla que no
/// existe. Windows obliga a pedir el token del usuario de la consola y crear el
/// proceso con el:
///
///     WTSGetActiveConsoleSessionId -> WTSQueryUserToken -> DuplicateTokenEx
///         -> CreateEnvironmentBlock -> CreateProcessAsUser
///
/// Sin sesion interactiva NO HAY CAPTURA. La PC en la pantalla de bloqueo o sin
/// nadie logueado falla con un motivo explicito; el escritorio seguro esta
/// fuera de alcance por decision propia (Fase 16).
///
/// Todo con DllImport y marshalling explicito, sin AllowUnsafeBlocks, igual que
/// Monitoring\SystemSampler.cs.
/// </summary>
public sealed class InteractiveSessionLauncher(ILogger<InteractiveSessionLauncher> logger) : IDisposable
{
    private readonly Lock _puerta = new();
    private Sesion? _actual;

    /// <summary>
    /// Sin BOM. Encoding.UTF8 escribe el preambulo en la primera linea, y el
    /// saludo dejaria de ser JSON valido para cualquier lector que no lo detecte.
    /// Depender de esa deteccion es depender de un detalle del StreamReader.
    /// </summary>
    private static readonly System.Text.UTF8Encoding Texto = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Un host a la vez. Una segunda orden de arranque sustituye a la
    /// anterior: dos capturas compitiendo por el mismo escritorio no dan el
    /// doble de nada.</summary>
    private sealed record Sesion(
        string SessionId, Process Proceso, NamedPipeServerStream Pipe, StreamWriter Ordenes);

    public string? SessionIdActiva
    {
        get { lock (_puerta) return _actual?.SessionId; }
    }

    /// <summary>
    /// Devuelve false si no se pudo arrancar. No lanza: que una PC no tenga
    /// usuario logueado es un estado normal, no una excepcion.
    /// </summary>
    public async Task<bool> StartAsync(RemoteHostHandshake saludo, CancellationToken ct)
    {
        Stop("llega una sesion nueva");

        var exe = Path.Combine(AppContext.BaseDirectory, "DeviceHub.RemoteHost.exe");

        if (!File.Exists(exe))
        {
            logger.LogError("No esta DeviceHub.RemoteHost.exe junto al agente ({Ruta})", exe);
            return false;
        }

        // Nombre nuevo por sesion. No es un secreto -- la ACL es la que protege
        // el pipe -- pero reutilizarlo dejaria a un host viejo enganchado al
        // canal de control del siguiente.
        var tuberia = $"devicehub-remote-{Guid.NewGuid():n}";

        NamedPipeServerStream? pipe = null;
        Process? proceso = null;

        try
        {
            var (token, usuario) = TokenDeLaConsola();

            try
            {
                pipe = CrearPipe(tuberia, usuario);

                // El pipe existe ANTES de lanzar: si se creara despues, el host
                // podria llegar a conectarse antes de que hubiera nada que
                // encontrar y morir por una carrera que no es culpa suya.
                proceso = Lanzar(exe, $"--pipe {tuberia}", token);

                logger.LogInformation(
                    "RemoteHost lanzado (pid {Pid}) para la {Saludo}", proceso.Id, saludo);
            }
            finally
            {
                token?.Dispose();
            }

            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
            espera.CancelAfter(TimeSpan.FromSeconds(15));

            await pipe.WaitForConnectionAsync(espera.Token);

            var ordenes = new StreamWriter(pipe, Texto, leaveOpen: true) { AutoFlush = true };
            await ordenes.WriteLineAsync(saludo.ToLine());

            var sesion = new Sesion(saludo.SessionId, proceso, pipe, ordenes);

            lock (_puerta)
                _actual = sesion;

            _ = Task.Run(() => VigilarAsync(sesion), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo arrancar RemoteHost para la sesion {Sesion}", saludo.SessionId);

            Matar(proceso);
            pipe?.Dispose();
            return false;
        }
    }

    public void Stop(string motivo)
    {
        Sesion? sesion;

        lock (_puerta)
        {
            sesion = _actual;
            _actual = null;
        }

        if (sesion is null)
            return;

        logger.LogInformation("Deteniendo RemoteHost de la sesion {Sesion}: {Motivo}", sesion.SessionId, motivo);

        // Primero por las buenas: STOP por el canal de control deja que el host
        // cierre la sesion en el relay y suelte la GPU en orden.
        try
        {
            sesion.Ordenes.WriteLine(RemoteHostPipe.Stop);

            if (sesion.Proceso.WaitForExit(3000))
                return;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "El host no acepto el STOP; se mata");
        }

        Matar(sesion.Proceso);
        sesion.Pipe.Dispose();
    }

    /// <summary>Lee lo que el host reporta hasta que se muera. Es lo unico que
    /// el agente ve de una sesion remota: la consola del host no va a ningun
    /// sitio, se lanza con CREATE_NO_WINDOW.</summary>
    private async Task VigilarAsync(Sesion sesion)
    {
        try
        {
            using var lector = new StreamReader(sesion.Pipe, Texto, leaveOpen: true);

            while (await lector.ReadLineAsync() is { } linea)
            {
                if (linea.StartsWith(RemoteHostPipe.Ended, StringComparison.Ordinal))
                    logger.LogInformation("RemoteHost {Sesion} termino: {Linea}", sesion.SessionId, linea);
                else
                    logger.LogInformation("RemoteHost {Sesion}: {Linea}", sesion.SessionId, linea);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Canal de control de {Sesion} cerrado", sesion.SessionId);
        }
        finally
        {
            // El host murio por su cuenta -- fin de sesion, relay caido, ticket
            // vencido. Se limpia sin volver a mandar STOP a un proceso que ya no
            // esta.
            lock (_puerta)
            {
                if (ReferenceEquals(_actual, sesion))
                    _actual = null;
            }

            Matar(sesion.Proceso);
            sesion.Pipe.Dispose();
        }
    }

    private void Matar(Process? proceso)
    {
        try
        {
            if (proceso is { HasExited: false })
                proceso.Kill(entireProcessTree: true);

            proceso?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo cerrar el proceso del host");
        }
    }

    public void Dispose() => Stop("el agente se detiene");

    // ------------------------------------------------------------- named pipe

    /// <summary>
    /// El pipe solo lo abre el usuario al que se lanzo el host, y SYSTEM. Sin
    /// esta ACL el pipe seria legible por cualquiera de la maquina y habriamos
    /// cambiado la fuga de los argumentos por otra identica.
    /// </summary>
    private static NamedPipeServerStream CrearPipe(string nombre, SecurityIdentifier? usuario)
    {
        // Sin SetOwner: el dueno es quien lo crea, que es lo que se quiere, y
        // ponerlo a mano exige SeRestorePrivilege -- el agente lo tiene como
        // SYSTEM pero no cuando se ejecuta a mano para depurar, y ahi reventaria.
        var permisos = new PipeSecurity();

        permisos.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        permisos.AddAccessRule(new PipeAccessRule(
            usuario ?? WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Los buffers NO pueden ser 0. Para Win32 eso significa "sin buffer", y
        // entonces cada escritura se queda bloqueada hasta que el otro extremo
        // lea: el WriteLine sincrono del STOP colgaria el hilo del agente para
        // siempre si el host esta atascado. Medido -- con 0 se cuelga, con 4096
        // fluye.
        return NamedPipeServerStreamAcl.Create(
            nombre, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 4096, permisos);
    }

    // --------------------------------------------------------- sesion de Windows

    /// <summary>
    /// Token primario del usuario de la consola, o null si el agente ya corre
    /// dentro de una sesion interactiva.
    ///
    /// El null no es un fallo: en desarrollo el agente se ejecuta a mano desde
    /// una consola normal, ya esta en la sesion del usuario, y ahi
    /// WTSQueryUserToken falla por privilegio -- CreateProcessAsUser sobra
    /// porque un Process.Start normal cae justo donde tiene que caer.
    /// </summary>
    private (SafeTokenHandle? Token, SecurityIdentifier? Usuario) TokenDeLaConsola()
    {
        if (Process.GetCurrentProcess().SessionId != 0)
        {
            logger.LogInformation("El agente ya esta en una sesion interactiva; se lanza sin suplantar");
            return (null, WindowsIdentity.GetCurrent().User);
        }

        var sesion = WTSGetActiveConsoleSessionId();

        if (sesion == 0xFFFFFFFF)
            throw new InvalidOperationException("No hay sesion de consola activa en esta PC");

        if (!WTSQueryUserToken(sesion, out var bruto))
        {
            throw new InvalidOperationException(
                $"Nadie logueado en la sesion {sesion} (WTSQueryUserToken: {Marshal.GetLastWin32Error()})");
        }

        using (bruto)
        {
            // El token que devuelve WTS es de suplantacion. CreateProcessAsUser
            // exige uno PRIMARIO, y duplicarlo es la unica forma de conseguirlo.
            if (!DuplicateTokenEx(bruto, MaximumAllowed, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out var primario))
            {
                throw new InvalidOperationException(
                    $"DuplicateTokenEx fallo: {Marshal.GetLastWin32Error()}");
            }

            using var identidad = new WindowsIdentity(primario.DangerousGetHandle());
            return (primario, identidad.User);
        }
    }

    private static Process Lanzar(string exe, string argumentos, SafeTokenHandle? token)
    {
        if (token is null)
        {
            var arranque = new ProcessStartInfo(exe, argumentos)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            return Process.Start(arranque) ?? throw new InvalidOperationException("Process.Start devolvio null");
        }

        // El bloque de entorno del USUARIO, no el del servicio: sin el, el host
        // hereda las variables de SYSTEM y cosas como TEMP apuntan a un sitio al
        // que ese usuario puede no tener acceso.
        if (!CreateEnvironmentBlock(out var entorno, token, bInherit: false))
            throw new InvalidOperationException($"CreateEnvironmentBlock fallo: {Marshal.GetLastWin32Error()}");

        try
        {
            var inicio = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),

                // El escritorio interactivo por defecto. Omitirlo deja el proceso
                // en el escritorio del servicio, donde no hay nada que capturar.
                lpDesktop = @"winsta0\default"
            };

            // CreateProcessAsUser puede ESCRIBIR en la linea de comandos, asi que
            // no se le puede pasar una cadena literal.
            var linea = new System.Text.StringBuilder($"\"{exe}\" {argumentos}");

            if (!CreateProcessAsUser(
                    token, null, linea, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false,
                    CreateUnicodeEnvironment | CreateNoWindow, entorno,
                    AppContext.BaseDirectory, ref inicio, out var info))
            {
                throw new InvalidOperationException($"CreateProcessAsUser fallo: {Marshal.GetLastWin32Error()}");
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);

            return Process.GetProcessById(info.dwProcessId);
        }
        finally
        {
            DestroyEnvironmentBlock(entorno);
        }
    }

    // ------------------------------------------------------------------ interop

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    /// <summary>
    /// El constructor sin parametros es PUBLICO y explicito porque lo llama el
    /// marshaller al rellenar los `out SafeTokenHandle`. Sin el, el P/Invoke
    /// falla en tiempo de ejecucion, no al compilar.
    /// </summary>
    internal sealed class SafeTokenHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeTokenHandle existingToken, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeTokenHandle newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment, SafeTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeTokenHandle token, string? applicationName, System.Text.StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);
}
