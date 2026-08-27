using System.Runtime.InteropServices;

namespace DeviceHub.Agent.Updater;

/// <summary>
/// Comprobar de verdad la firma Authenticode de un ejecutable.
///
/// Extraer el certificado de un PE firmado y comparar su huella NO es
/// verificar la firma: dice quien lo firmo alguna vez, no que el archivo siga
/// siendo el que se firmo. Un PE alterado despues de firmarlo conserva su
/// bloque de certificado y sigue devolviendo el mismo firmante.
///
/// Quien comprueba las dos cosas -- publicador Y que el codigo no se haya
/// modificado desde que se firmo -- es WinVerifyTrust con
/// WINTRUST_ACTION_GENERIC_VERIFY_V2. Es la unica API de Windows que hace esa
/// validacion, y por eso hay interop aqui.
///
/// El orden importa: primero esto, y solo despues comparar la huella. Al reves
/// se estaria confiando en un certificado sacado de un archivo que todavia no
/// se sabe si es integro.
/// </summary>
public static class Authenticode
{
    /// <summary>0 = firma valida y archivo integro. Otro valor es el motivo.</summary>
    public static int Verificar(string ejecutable)
    {
        var accion = ActionGenericVerifyV2;

        var archivo = new WinTrustFileInfo
        {
            Size = Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = ejecutable,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };

        var memoria = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());

        try
        {
            Marshal.StructureToPtr(archivo, memoria, false);

            var datos = new WinTrustData
            {
                Size = Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,

                // NONE: sin interfaz. Esto corre en un servicio, sin escritorio
                // donde enseñar un dialogo -- y un dialogo aqui seria un cuelgue.
                UiChoice = UiNone,

                // Sin comprobar revocacion: el agente puede estar en una PC de
                // planta sin salida a internet, y ahi la comprobacion no falla,
                // se cuelga hasta agotar su tiempo. La cadena de confianza de la
                // maquina sigue validandose.
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfoPtr = memoria,
                StateAction = StateActionVerify,
                StateData = IntPtr.Zero,
                UrlReference = null,
                ProvFlags = 0,
                UiContext = 0
            };

            var resultado = WinVerifyTrust(IntPtr.Zero, ref accion, ref datos);

            // Cerrar el estado SIEMPRE, o se filtra por cada comprobacion.
            datos.StateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref accion, ref datos);

            return resultado;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(memoria);
            Marshal.FreeHGlobal(memoria);
        }
    }

    /// <summary>El motivo en cristiano. Los tres que se ven de verdad.</summary>
    public static string Motivo(int codigo) => unchecked((uint)codigo) switch
    {
        0x800B0100 => "el archivo no esta firmado",
        0x800B0101 => "el certificado del firmante caduco",
        0x800B0004 => "el archivo NO coincide con su firma: se modifico despues de firmarlo",
        0x800B010A => "no se pudo construir la cadena hasta una raiz de confianza",
        _ => $"0x{codigo:X8}"
    };

    private static Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public int Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPtr;
        public uint StateAction;
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public uint ProvFlags;
        public uint UiContext;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr ventana, ref Guid accion, ref WinTrustData datos);
}
