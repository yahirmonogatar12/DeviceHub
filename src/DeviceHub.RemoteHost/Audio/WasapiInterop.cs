using System.Runtime.InteropServices;

namespace DeviceHub.RemoteHost.Audio;

/// <summary>
/// Lo que Vortice NO trae de WASAPI.
///
/// Vortice.MediaFoundation expone IMMDeviceEnumerator e IMMDevice -- enumerar
/// dispositivos -- pero no IAudioClient ni IAudioCaptureClient, que son los que
/// de verdad capturan. Se declaran aqui, con marshalling explicito y sin
/// /unsafe, siguiendo el mismo precedente que ICodecAPI en H264Encoder.
///
/// Se declara SOLO lo que se usa. Un binding completo de WASAPI son cientos de
/// lineas que despues hay que mantener, y aqui hacen falta seis metodos.
/// </summary>
public static class Wasapi
{
    /// <summary>El sonido que SALE por los altavoces, no el que entra por el
    /// microfono. Es la diferencia entre oir la PC remota y oir su sala.</summary>
    internal const uint StreamFlagsLoopback = 0x00020000;

    internal const uint RenderDataFlagSilent = 0x2;

    /// <summary>Compartido: no se le quita el sonido a nadie. Exclusivo daria
    /// menos latencia y dejaria muda la PC que se esta mirando.</summary>
    internal const int ShareModeShared = 0;

    internal const int RenderFlow = 0;      // eRender
    internal const int ConsoleRole = 0;     // eConsole

    /// <summary>Unidades de 100 ns, que es como cuenta WASAPI.</summary>
    internal const long UnidadesPorMs = 10_000;

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            int shareMode, uint streamFlags, long duracionBufer, long periodo,
            IntPtr formato, IntPtr sesion);

        [PreserveSig] int GetBufferSize(out uint fotogramas);
        [PreserveSig] int GetStreamLatency(out long latencia);
        [PreserveSig] int GetCurrentPadding(out uint fotogramas);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr formato, out IntPtr masCercano);

        [PreserveSig] int GetMixFormat(out IntPtr formato);
        [PreserveSig] int GetDevicePeriod(out long porDefecto, out long minimo);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr evento);

        [PreserveSig]
        int GetService(ref Guid interfaz, [MarshalAs(UnmanagedType.IUnknown)] out object servicio);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr datos, out uint fotogramas, out uint banderas,
            out ulong posicion, out ulong marcaQpc);

        [PreserveSig] int ReleaseBuffer(uint fotogramas);
        [PreserveSig] int GetNextPacketSize(out uint fotogramas);
    }

    internal static readonly Guid IdAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    internal static readonly Guid IdAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>
    /// WAVEFORMATEX tal cual lo devuelve GetMixFormat, leido campo a campo.
    ///
    /// No se usa una estructura con [StructLayout] porque lo que devuelve puede
    /// ser un WAVEFORMATEXTENSIBLE -- mas largo -- y copiar solo la cabecera con
    /// Marshal.PtrToStructure funcionaria por casualidad hasta que dejara de
    /// hacerlo. Se leen los campos que hacen falta y el puntero se pasa entero
    /// al Initialize, que es quien sabe interpretarlo.
    /// </summary>
    public readonly record struct Formato(
        int Canales, int Hz, int BitsPorMuestra, int BytesPorFotograma, bool EsFlotante)
    {
        public override string ToString()
            => $"{Hz} Hz, {Canales} canales, {BitsPorMuestra} bits {(EsFlotante ? "flotante" : "entero")}";
    }

    public static Formato LeerFormato(IntPtr wfx)
    {
        // WAVEFORMATEX: wFormatTag(2) nChannels(2) nSamplesPerSec(4)
        //               nAvgBytesPerSec(4) nBlockAlign(2) wBitsPerSample(2) cbSize(2)
        var etiqueta = (ushort)Marshal.ReadInt16(wfx, 0);
        var canales = (ushort)Marshal.ReadInt16(wfx, 2);
        var hz = Marshal.ReadInt32(wfx, 4);
        var bloque = (ushort)Marshal.ReadInt16(wfx, 12);
        var bits = (ushort)Marshal.ReadInt16(wfx, 14);

        // 0xFFFE = EXTENSIBLE. El formato de verdad esta en el SubFormat, a
        // partir del byte 24, y para lo que hace falta aqui basta el primer
        // DWORD del GUID: 3 = IEEE float, 1 = PCM entero.
        var esFlotante = etiqueta == 3
                         || (etiqueta == 0xFFFE && Marshal.ReadInt32(wfx, 24) == 3);

        return new Formato(canales, hz, bits, bloque, esFlotante);
    }

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr memoria);
}
