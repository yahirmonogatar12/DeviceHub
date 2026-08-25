using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Audio;

/// <summary>Lo que no se puede capturar y por que.</summary>
public sealed class SonidoNoDisponibleException(string mensaje, Exception? interna = null)
    : Exception(mensaje, interna);

/// <summary>
/// El sonido que SALE por los altavoces de la PC controlada, en modo loopback.
///
/// Loopback y no microfono: lo que interesa es oir lo que suena en esa maquina
/// -- una alarma, un video, el pitido de un error -- no la sala donde esta.
///
/// EN MODO COMPARTIDO. El exclusivo daria menos latencia y dejaria muda a la
/// PC que se esta mirando, que es exactamente lo que no se puede hacer en una
/// maquina de produccion.
///
/// SIN CONVERTIR NADA. Se entrega el formato que da Windows -- normalmente 48
/// kHz flotante -- y quien lo consuma decide. Convertir aqui seria adivinar que
/// quiere el codificador antes de que exista.
/// </summary>
public sealed class CapturaDeSonido : IDisposable
{
    /// <summary>CLSCTX_ALL. Vortice no expone la constante.</summary>
    private const int ClsCtxAll = 0x17;


    private readonly Wasapi.IAudioClient _cliente;
    private readonly Wasapi.IAudioCaptureClient _captura;
    private bool _corriendo;

    /// <summary>El formato que entrega Windows. No se negocia.</summary>
    public Wasapi.Formato Formato { get; }

    /// <summary>Nombre del dispositivo, para que el diagnostico diga de donde
    /// sale el sonido en vez de solo cuanto.</summary>
    public string Dispositivo { get; }

    /// <summary>Fotogramas que Windows dio por perdidos porque no los recogimos
    /// a tiempo. Si esto sube, el consumidor va lento.</summary>
    public long Discontinuidades { get; private set; }

    /// <summary>Paquetes marcados como silencio. Windows no siempre los llena de
    /// ceros -- dice "esto es silencio" y el bufer puede traer basura -- asi que
    /// hay que hacerles caso en vez de codificar lo que venga.</summary>
    public long Silencios { get; private set; }

    public CapturaDeSonido()
    {
        IMMDeviceEnumerator? enumerador = null;
        IMMDevice? dispositivo = null;

        try
        {
            enumerador = new IMMDeviceEnumerator();
            dispositivo = enumerador.GetDefaultAudioEndpoint(
                (DataFlow)Wasapi.RenderFlow, (Role)Wasapi.ConsoleRole);

            Dispositivo = NombreDe(dispositivo);

            // Activate devuelve un puntero crudo, no un objeto: la envoltura de
            // Vortice no conoce IAudioClient -- por eso lo declaramos nosotros --
            // asi que el marshalling a la interfaz lo hacemos aqui.
            var id = Wasapi.IdAudioClient;
            var hr = dispositivo.Activate(id, ClsCtxAll, null, out var bruto);

            if (hr.Failure || bruto == IntPtr.Zero)
                throw new SonidoNoDisponibleException($"No se pudo activar IAudioClient ({hr}).");

            if (Marshal.GetObjectForIUnknown(bruto) is not Wasapi.IAudioClient cliente)
                throw new SonidoNoDisponibleException("Lo que devolvio Activate no es un IAudioClient.");

            // GetObjectForIUnknown se queda con su propia referencia; la que nos
            // dio Activate es nuestra y hay que soltarla o el dispositivo no se
            // libera al cerrar la sesion.
            Marshal.Release(bruto);

            _cliente = cliente;

            if (_cliente.GetMixFormat(out var wfx) < 0 || wfx == IntPtr.Zero)
                throw new SonidoNoDisponibleException("El dispositivo no dijo en que formato mezcla.");

            try
            {
                Formato = Wasapi.LeerFormato(wfx);

                // Un bufer de 200 ms. No es la latencia -- en compartido la marca
                // Windows -- es cuanto aguanta sin que se pierda nada si el
                // consumidor se atasca un momento. Corto gasta menos memoria y
                // pierde antes; largo esconde atascos que conviene ver.
                var inicio = _cliente.Initialize(
                    Wasapi.ShareModeShared,
                    Wasapi.StreamFlagsLoopback,
                    200 * Wasapi.UnidadesPorMs, 0, wfx, IntPtr.Zero);

                if (inicio < 0)
                    throw new SonidoNoDisponibleException($"IAudioClient.Initialize fallo (0x{inicio:X8}).");
            }
            finally
            {
                // Lo reservo WASAPI con CoTaskMemAlloc y es nuestro liberarlo,
                // pase lo que pase con el Initialize.
                Wasapi.CoTaskMemFree(wfx);
            }

            var servicio = Wasapi.IdAudioCaptureClient;

            if (_cliente.GetService(ref servicio, out var obj) < 0
                || obj is not Wasapi.IAudioCaptureClient captura)
            {
                throw new SonidoNoDisponibleException("No se pudo obtener IAudioCaptureClient.");
            }

            _captura = captura;
            _cliente.Start();
            _corriendo = true;
        }
        catch (SonidoNoDisponibleException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SonidoNoDisponibleException(
                $"No se pudo abrir el sonido de esta PC: {ex.Message}", ex);
        }
        finally
        {
            dispositivo?.Dispose();
            enumerador?.Dispose();
        }
    }

    /// <summary>
    /// Recoge lo que haya, sin esperar. Devuelve cuantos BYTES se escribieron en
    /// el destino, o 0 si no habia nada.
    ///
    /// Sin bloquear a proposito: con loopback y silencio absoluto Windows no
    /// genera paquetes, y un consumidor que espere se queda parado para siempre
    /// en vez de darse cuenta de que no hay sonido.
    /// </summary>
    public int Recoger(Span<byte> destino)
    {
        if (!_corriendo)
            return 0;

        var escritos = 0;

        while (_captura.GetNextPacketSize(out var cuantos) >= 0 && cuantos > 0)
        {
            if (_captura.GetBuffer(out var datos, out var fotogramas, out var banderas, out _, out _) < 0)
                break;

            try
            {
                // La discontinuidad llega en las banderas del paquete SIGUIENTE
                // al hueco, no en el que falta. Se anota aqui o no se anota
                // nunca -- y un contador que siempre vale cero es peor que no
                // tenerlo, porque parece una respuesta.
                if ((banderas & DatosDiscontinuos) != 0)
                    Discontinuidades++;

                if (fotogramas == 0)
                    continue;

                var bytes = (int)fotogramas * Formato.BytesPorFotograma;

                if (escritos + bytes > destino.Length)
                    break;   // no cabe; lo que quede se recoge en la siguiente

                if ((banderas & Wasapi.RenderDataFlagSilent) != 0)
                {
                    // SILENCIO DECLARADO. El bufer puede traer cualquier cosa:
                    // Windows solo promete que no hay sonido, no que este a
                    // cero. Copiarlo tal cual mete ruido de la nada.
                    Silencios++;
                    destino.Slice(escritos, bytes).Clear();
                }
                else
                {
                    Marshal.Copy(datos, ArrayTemporal(bytes), 0, bytes);
                    _temporal.AsSpan(0, bytes).CopyTo(destino[escritos..]);
                }

                escritos += bytes;
            }
            finally
            {
                _captura.ReleaseBuffer(fotogramas);
            }
        }

        return escritos;
    }

    /// <summary>AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.</summary>
    private const uint DatosDiscontinuos = 0x1;

    private byte[] _temporal = [];

    /// <summary>
    /// Marshal.Copy necesita un byte[] gestionado; no hay forma de copiar de un
    /// IntPtr a un Span sin /unsafe, que este repositorio no activa. Se
    /// reutiliza en vez de reservar por paquete: a 48 kHz esto ocurre cientos de
    /// veces por segundo.
    /// </summary>
    private byte[] ArrayTemporal(int bytes)
    {
        if (_temporal.Length < bytes)
            _temporal = new byte[bytes];

        return _temporal;
    }

    private static string NombreDe(IMMDevice dispositivo)
    {
        try
        {
            return dispositivo.FriendlyName ?? "(sin nombre)";
        }
        catch (Exception)
        {
            return "(sin nombre)";
        }
    }

    public void Dispose()
    {
        if (_corriendo)
        {
            try { _cliente.Stop(); } catch (Exception) { }
            _corriendo = false;
        }

        if (_captura is not null)
            Marshal.ReleaseComObject(_captura);

        if (_cliente is not null)
            Marshal.ReleaseComObject(_cliente);
    }
}
