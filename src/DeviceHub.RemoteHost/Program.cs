using DeviceHub.RemoteHost.Capture;
using DeviceHub.RemoteHost.Encode;

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

if (args.Contains("--encode-test"))
    return EncodeTest.Run(
        Indice(args, "--adapter"), Indice(args, "--output"), Indice(args, "--seconds", 30),
        Indice(args, "--fps", 60), Indice(args, "--bitrate", 6_000_000),
        Texto(args, "--scenario") ?? "sin etiquetar", Texto(args, "--save"));

Console.Error.WriteLine("""
    DeviceHub.RemoteHost

    No se ejecuta a mano: lo lanza el agente en la sesion del usuario con el
    nombre de un named pipe por el que recibe la sesion y su ticket.

    Modos de diagnostico:
      --displays                    lista GPUs y pantallas
      --encoders                    lista codificadores H.264 y que aceptan
      --capture-test                mide la captura de pantalla   (Fase 1)
      --encode-test                 mide la cadena completa       (Fase 2)

    Comunes:
      --adapter N   que GPU      (por defecto 0)
      --output N    que pantalla (por defecto 0)
      --seconds N   duracion     (por defecto 30)

    Solo --encode-test:
      --fps N          objetivo         (por defecto 60)
      --bitrate N      bits por segundo (por defecto 6000000)
      --scenario TXT   etiqueta del escenario que se esta generando
      --save RUTA      guarda el H.264 para abrirlo en un reproductor

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
