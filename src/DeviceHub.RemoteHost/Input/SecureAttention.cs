using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeviceHub.RemoteHost.Input;

/// <summary>
/// Ctrl + Alt + Supr. Fase 19.
///
/// No se puede generar con SendInput NUNCA, ni con ganchos, ni con scan codes.
/// Es la secuencia de atencion segura y Windows la reserva a proposito: si
/// cualquier programa pudiera fabricarla, una ventana falsa de login seria
/// indistinguible de la de verdad. Esa garantia es justamente lo que hace util
/// pulsarla.
///
/// La unica puerta oficial es SendSAS de sas.dll, y cobra dos peajes:
///
///   1. el proceso tiene que ser SYSTEM        -- lo es desde la Fase 19
///   2. la directiva SoftwareSASGeneration     -- se activa aqui
/// </summary>
public static class SecureAttention
{
    private const string Directiva =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>Servicios y aplicaciones con UIAccess. Es el valor que necesita
    /// SendSAS llamado desde aqui.</summary>
    private const int ServiciosYAccesibilidad = 3;

    public static bool Enviar(out string detalle)
    {
        if (!Habilitar(out detalle))
            return false;

        try
        {
            // false = de un servicio, no de una aplicacion con UIAccess.
            SendSAS(asUser: false);
            detalle = "Ctrl+Alt+Supr enviado";
            return true;
        }
        catch (DllNotFoundException)
        {
            detalle = "sas.dll no esta en esta edicion de Windows";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            detalle = "sas.dll no expone SendSAS";
            return false;
        }
    }

    /// <summary>
    /// Enciende la directiva si hace falta, y solo si hace falta.
    ///
    /// Se hace aqui y no en el instalador a proposito: es el unico componente
    /// que la necesita, sabe cuando la necesita, y como corre con SYSTEM puede
    /// ponerla. Escribirla en cada arranque seria ruido en el registro; por eso
    /// se lee primero.
    /// </summary>
    private static bool Habilitar(out string detalle)
    {
        detalle = string.Empty;

        try
        {
            using var clave = Registry.LocalMachine.CreateSubKey(Directiva, writable: true);

            if (clave is null)
            {
                detalle = "No se pudo abrir la directiva SoftwareSASGeneration";
                return false;
            }

            if (clave.GetValue("SoftwareSASGeneration") is int actual
                && (actual == ServiciosYAccesibilidad || actual == 1))
            {
                return true;
            }

            clave.SetValue("SoftwareSASGeneration", ServiciosYAccesibilidad, RegistryValueKind.DWord);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Sin SYSTEM no hay forma, y tampoco la habria de capturar Winlogon:
            // el mensaje dice la causa real en vez de "acceso denegado".
            detalle = "Hace falta SYSTEM para activar SoftwareSASGeneration";
            return false;
        }
    }

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
