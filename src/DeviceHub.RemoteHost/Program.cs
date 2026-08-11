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
{
    Console.Error.WriteLine("--encode-test llega en la Fase 2 (encoder H.264).");
    return 1;
}

Console.Error.WriteLine("""
    DeviceHub.RemoteHost

    No se ejecuta a mano: lo lanza el agente en la sesion del usuario con el
    nombre de un named pipe por el que recibe la sesion y su ticket.

    Modos de diagnostico:
      --displays                    lista GPUs y pantallas
      --capture-test                mide la captura de pantalla   (Fase 1)
      --encode-test                 mide el encoder H.264         (Fase 2)

    Opciones de --capture-test:
      --adapter N   que GPU     (por defecto 0)
      --output N    que pantalla (por defecto 0)
      --seconds N   duracion    (por defecto 30)

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
