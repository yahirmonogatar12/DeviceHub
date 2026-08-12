using DeviceHub.RemoteHost.Capture;
using DeviceHub.RemoteHost.Encode;
using DeviceHub.RemoteHost.Relay;

// Host de control remoto: corre en la PC CONTROLADA, dentro de la sesion
// interactiva del usuario (nunca en la sesion 0, donde no hay escritorio que
// capturar). Lo lanza DeviceHub.Agent; no se instala como servicio.
//
// Los modos de diagnostico salen antes de montar nada, igual que --inventory y
// --metrics en DeviceHub.Agent\Program.cs.

if (args.Contains("--displays"))
{
    foreach (var linea in DxgiDesktopCapture.Enumerate())
        Console.WriteLine(linea);

    return 0;
}

if (args.Contains("--encoders"))
    return EncoderProbe.Run();

if (args.Contains("--capture-test"))
    return CaptureTest.Run(Indice(args, "--adapter"), Indice(args, "--output"), Indice(args, "--seconds", 30));

// El modo de produccion (Fase 7). Lo unico que llega por argumento es el nombre
// del pipe, que no abre nada por si solo: la sesion y el ticket vienen por
// dentro, y ese pipe tiene la ACL restringida al SID de este usuario.
if (Texto(args, "--pipe") is { } tuberia)
{
    return await HostSession.RunAsync(
        tuberia, Indice(args, "--adapter"), Indice(args, "--output"),
        Indice(args, "--fps", 60), Indice(args, "--bitrate", 6_000_000));
}

if (args.Contains("--relay-test"))
    return await RelaySession.RunAsync(new RelayOptions
    {
        Servidor = Texto(args, "--server") ?? "https://192.168.1.10:5443",
        SesionId = Texto(args, "--session") ?? Guid.NewGuid().ToString("n"),
        MachineId = Texto(args, "--machine-id") ?? Environment.MachineName,
        Adapter = Indice(args, "--adapter"),
        Output = Indice(args, "--output"),
        Seconds = Indice(args, "--seconds", 60),
        Fps = Indice(args, "--fps", 60),
        Bitrate = Indice(args, "--bitrate", 6_000_000),
        AllowUntrusted = args.Contains("--allow-untrusted")
    }, CancellationToken.None);

if (args.Contains("--encode-test"))
    return EncodeTest.Run(
        Indice(args, "--adapter"), Indice(args, "--output"), Indice(args, "--seconds", 30),
        Indice(args, "--fps", 60), Indice(args, "--bitrate", 6_000_000),
        Texto(args, "--scenario") ?? "sin etiquetar", Texto(args, "--save"));

Console.Error.WriteLine("""
    DeviceHub.RemoteHost

    No se ejecuta a mano: lo lanza el agente en la sesion del usuario con el
    nombre de un named pipe por el que recibe la sesion y su ticket.

    Modo de produccion:
      --pipe NOMBRE                 lo pone el agente; todo lo demas va dentro

    Modos de diagnostico:
      --displays                    lista GPUs y pantallas
      --encoders                    lista codificadores H.264 y que aceptan
      --capture-test                mide la captura de pantalla   (Fase 1)
      --encode-test                 mide la cadena completa       (Fase 2)
      --relay-test                  manda video al relay          (Fase 5)

    Comunes:
      --adapter N   que GPU      (por defecto 0)
      --output N    que pantalla (por defecto 0)
      --seconds N   duracion     (por defecto 30)

    Solo --encode-test:
      --fps N          objetivo         (por defecto 60)
      --bitrate N      bits por segundo (por defecto 6000000)
      --scenario TXT   etiqueta del escenario que se esta generando
      --save RUTA      guarda el H.264 para abrirlo en un reproductor

    Solo --relay-test:
      --server URL       por defecto https://192.168.1.10:5443
      --session ID       identificador de la sesion; se comparte con el viewer
      --machine-id ID    el machine_id de DeviceHub al que se ato el ticket
      --allow-untrusted  no valida el certificado (solo para probar)

    El ticket NO se pasa por linea de comandos ni aqui ni nunca: los argumentos
    de un proceso los lee cualquier usuario de la maquina.

    El adaptador hace falta en equipos con dos GPUs: Desktop Duplication exige
    que el dispositivo D3D11 este en la misma que gobierna la pantalla.
    """);

return 1;

static int Indice(string[] args, string nombre, int porDefecto = 0)
{
    var posicion = Array.IndexOf(args, nombre);

    return posicion >= 0 && posicion + 1 < args.Length && int.TryParse(args[posicion + 1], out var indice)
        ? indice
        : porDefecto;
}

static string? Texto(string[] args, string nombre)
{
    var posicion = Array.IndexOf(args, nombre);

    return posicion >= 0 && posicion + 1 < args.Length ? args[posicion + 1] : null;
}
