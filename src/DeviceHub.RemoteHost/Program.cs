// Host de control remoto: corre en la PC CONTROLADA, dentro de la sesion
// interactiva del usuario (nunca en la sesion 0, donde no hay escritorio que
// capturar). Lo lanza DeviceHub.Agent; no se instala como servicio.
//
// Los modos de diagnostico salen antes de montar nada, igual que --inventory y
// --metrics en DeviceHub.Agent\Program.cs.

if (args.Contains("--capture-test"))
{
    Console.Error.WriteLine("--capture-test llega en la Fase 1 (captura DXGI).");
    return 1;
}

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
      --capture-test    mide la captura de pantalla        (Fase 1)
      --encode-test     mide el encoder H.264              (Fase 2)
    """);

return 1;
