using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteViewer.Audio;

/// <summary>Lo que no se pudo abrir para reproducir, y por que.</summary>
public sealed class SalidaNoDisponibleException(string mensaje, Exception? interna = null)
    : Exception(mensaje, interna);

/// <summary>
/// Reproduce en los altavoces del TECNICO lo que suena en la PC controlada.
///
/// Lo que Vortice no trae -- IAudioClient e IAudioRenderClient -- se declara
/// aqui, igual que en el host. Se declara solo lo que se usa.
///
/// EN MODO COMPARTIDO: no se le quita el sonido a las demas aplicaciones del
/// tecnico. Estar oyendo una PC de planta no puede dejar muda la propia.
/// </summary>
public sealed class WasapiSalida : IDisposable
{
    private const int ClsCtxAll = 0x17;
    private const int ShareModeShared = 0;
    private const long UnidadesPorMs = 10_000;
    private const uint RenderFlagSilent = 0x2;

    private static readonly Guid IdAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IdAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    private readonly IAudioClient _cliente;
    private readonly IAudioRenderClient _render;
    private readonly int _bytesPorFotograma;
    private bool _corriendo;

    /// <summary>Fotogramas que caben en el bufer del dispositivo.</summary>
    public uint Capacidad { get; }

    public int Hz { get; }
    public int Canales { get; }

    /// <summary>
    /// Veces que no cupo lo que se queria escribir. Si sube, llega mas sonido
    /// del que se reproduce -- normalmente porque los relojes de las dos PCs no
    /// corren exactamente igual.
    /// </summary>
    public long NoCupo { get; private set; }

    public WasapiSalida(int hz, int canales)
    {
        IMMDeviceEnumerator? enumerador = null;
        IMMDevice? dispositivo = null;

        try
        {
            enumerador = new IMMDeviceEnumerator();
            dispositivo = enumerador.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);

            var id = IdAudioClient;
            var hr = dispositivo.Activate(id, ClsCtxAll, null, out var bruto);

            if (hr.Failure || bruto == IntPtr.Zero)
                throw new SalidaNoDisponibleException($"No se pudo activar IAudioClient ({hr}).");

            if (Marshal.GetObjectForIUnknown(bruto) is not IAudioClient cliente)
                throw new SalidaNoDisponibleException("Lo que devolvio Activate no es un IAudioClient.");

            Marshal.Release(bruto);
            _cliente = cliente;

            Hz = hz;
            Canales = canales;
            _bytesPorFotograma = canales * 2;

            var wfx = Marshal.AllocHGlobal(18);

            try
            {
                // WAVEFORMATEX de PCM entero de 16 bits, que es lo que sale del
                // decodificador. Se construye a mano porque aqui SI se sabe el
                // formato exacto -- no se negocia con nadie.
                Marshal.WriteInt16(wfx, 0, 1);                              // WAVE_FORMAT_PCM
                Marshal.WriteInt16(wfx, 2, (short)canales);
                Marshal.WriteInt32(wfx, 4, hz);
                Marshal.WriteInt32(wfx, 8, hz * _bytesPorFotograma);
                Marshal.WriteInt16(wfx, 12, (short)_bytesPorFotograma);
                Marshal.WriteInt16(wfx, 14, 16);
                Marshal.WriteInt16(wfx, 16, 0);

                // 200 ms de bufer. No es la latencia -- en compartido la marca
                // Windows -- es cuanto sonido cabe por delante. Corto corta al
                // primer tropiezo de red; largo mete retraso que se nota cuando
                // el sonido tiene que cuadrar con la imagen.
                var inicio = _cliente.Initialize(
                    ShareModeShared, 0, 200 * UnidadesPorMs, 0, wfx, IntPtr.Zero);

                if (inicio < 0)
                {
                    throw new SalidaNoDisponibleException(
                        $"El dispositivo no acepta PCM de {hz} Hz con {canales} canal(es) " +
                        $"(0x{inicio:X8}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(wfx);
            }

            _cliente.GetBufferSize(out var capacidad);
            Capacidad = capacidad;

            var servicio = IdAudioRenderClient;

            if (_cliente.GetService(ref servicio, out var obj) < 0 || obj is not IAudioRenderClient render)
                throw new SalidaNoDisponibleException("No se pudo obtener IAudioRenderClient.");

            _render = render;
            _cliente.Start();
            _corriendo = true;
        }
        catch (SalidaNoDisponibleException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SalidaNoDisponibleException($"No se pudo abrir el sonido: {ex.Message}", ex);
        }
        finally
        {
            dispositivo?.Dispose();
            enumerador?.Dispose();
        }
    }

    /// <summary>
    /// Cuantos fotogramas hay esperando a sonar. Es la latencia que queda por
    /// delante, y con ella se calcula cuando se oira lo que se escriba ahora.
    /// </summary>
    public uint EnCola
    {
        get
        {
            if (!_corriendo || _cliente.GetCurrentPadding(out var pendientes) < 0)
                return 0;

            return pendientes;
        }
    }

    /// <summary>
    /// Escribe PCM de 16 bits. Devuelve cuantos BYTES entraron, que pueden ser
    /// menos de los ofrecidos si el bufer del dispositivo esta lleno.
    /// </summary>
    public int Escribir(ReadOnlySpan<byte> pcm)
    {
        if (!_corriendo || pcm.Length < _bytesPorFotograma)
            return 0;

        if (_cliente.GetCurrentPadding(out var pendientes) < 0)
            return 0;

        var libres = Capacidad - pendientes;
        var quieren = (uint)(pcm.Length / _bytesPorFotograma);
        var caben = Math.Min(libres, quieren);

        if (caben == 0)
        {
            NoCupo++;
            return 0;
        }

        if (_render.GetBuffer(caben, out var destino) < 0)
            return 0;

        var bytes = (int)caben * _bytesPorFotograma;

        try
        {
            Marshal.Copy(Copia(pcm[..bytes]), 0, destino, bytes);
        }
        finally
        {
            _render.ReleaseBuffer(caben, 0);
        }

        return bytes;
    }

    /// <summary>
    /// Mete silencio. Se usa cuando hay un hueco en el sonido que llega: el
    /// dispositivo REPITE lo ultimo si se queda sin datos, y eso se oye como un
    /// zumbido -- peor que el silencio que sustituye.
    /// </summary>
    public void Silencio(uint fotogramas)
    {
        if (!_corriendo || fotogramas == 0)
            return;

        if (_render.GetBuffer(fotogramas, out _) >= 0)
            _render.ReleaseBuffer(fotogramas, RenderFlagSilent);
    }

    private byte[] _copia = [];

    private byte[] Copia(ReadOnlySpan<byte> origen)
    {
        if (_copia.Length < origen.Length)
            _copia = new byte[origen.Length];

        origen.CopyTo(_copia);
        return _copia;
    }

    public void Dispose()
    {
        if (_corriendo)
        {
            try { _cliente.Stop(); } catch (Exception) { }
            _corriendo = false;
        }

        if (_render is not null)
            Marshal.ReleaseComObject(_render);

        if (_cliente is not null)
            Marshal.ReleaseComObject(_cliente);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint flags, long duracion, long periodo, IntPtr formato, IntPtr sesion);

        [PreserveSig] int GetBufferSize(out uint fotogramas);
        [PreserveSig] int GetStreamLatency(out long latencia);
        [PreserveSig] int GetCurrentPadding(out uint fotogramas);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr formato, out IntPtr masCercano);
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
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint fotogramas, out IntPtr datos);
        [PreserveSig] int ReleaseBuffer(uint fotogramas, uint banderas);
    }
}
