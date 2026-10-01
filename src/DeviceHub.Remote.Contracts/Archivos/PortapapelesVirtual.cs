using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeviceHub.Remote.Contracts;
using System.Runtime.Versioning;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace DeviceHub.Archivos;

/// <summary>
/// Copiar en una PC y pegar en la otra con el Ctrl+V de siempre, cuantos
/// archivos y carpetas sean. Es lo que hace RustDesk, y lo que hace el
/// Escritorio Remoto de Windows: el cliprdr de FreeRDP.
///
/// NO SE TRANSFIERE NADA POR ADELANTADO. En el portapapeles del que pega se pone
/// una PROMESA -- FileGroupDescriptorW + FileContents, lo mismo que deja Outlook
/// al arrastrar un adjunto -- y es el Explorador, al pegar, quien pide la lista
/// y luego cada archivo a trozos. Esos trozos se traen en ese momento de la PC
/// donde se copio, y el Explorador los escribe donde se pego, con su ventana de
/// progreso de siempre. Sin temporal, sin boton y sin esperar a que pase todo.
///
/// EN LOS DOS SENTIDOS, y por eso vive aqui y no en un lado: en la PC remota
/// lo usa el ayudante de RemoteHost (un proceso aparte que corre como el
/// usuario, porque RemoteHost es SYSTEM -- RustDesk lo resuelve igual, con su
/// proceso "cm"), y en la del tecnico lo usa el visor. Quien lo usa solo pone
/// <c>pedir</c>: de donde salen los trozos.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PortapapelesVirtual
{
    private readonly Func<string, ulong, FileChunk> _pedir;
    private readonly ConcurrentQueue<Action> _trabajo = new();
    private readonly AutoResetEvent _hayTrabajo = new(false);
    private readonly Thread _hilo;
    private volatile bool _fin;
    private DatosVirtuales? _actual;

    /// <param name="pedir">Trae un trozo de un archivo de la otra PC, desde un
    /// byte dado. Bloquea: lo llama el Explorador mientras pega.</param>
    public PortapapelesVirtual(Func<string, ulong, FileChunk> pedir)
    {
        _pedir = pedir;

        // STA y con su propio bucle de mensajes: el portapapeles OLE crea una
        // ventana oculta en el hilo que lo pone, y las llamadas del Explorador
        // entran como mensajes a esa ventana. Sin bombearlos no llega ninguna.
        _hilo = new Thread(Bucle) { IsBackground = true, Name = "portapapeles virtual" };
        _hilo.SetApartmentState(ApartmentState.STA);
        _hilo.Start();
    }

    /// <summary>
    /// Pone la promesa. Vuelve cuando YA esta puesta: el Ctrl+V que el tecnico
    /// pulsa detras no puede adelantarsele. Null si salio bien.
    /// </summary>
    public string? Ofrecer(ClipboardFiles anuncio)
    {
        // LAS CARPETAS PRIMERO: el Explorador crea cada entrada en el orden de
        // la lista, y un archivo cuya carpeta aun no existe no tiene donde caer.
        //
        // Y fuera lo que no cabe: el nombre de cada entrada es un MAX_PATH
        // fijo. ponytail: se pierden las rutas de mas de 259 caracteres; si
        // aparecen, hay que trocear el pegado por carpetas.
        var lista = anuncio.Directories.Select(c => new Entrada(string.Empty, c, 0, Carpeta: true))
            .Concat(anuncio.Entries.Select(e => new Entrada(e.Path, e.Relative, e.Size, Carpeta: false)))
            .Where(e => e.Relativa.Length is > 0 and < 260)
            .ToList();

        string? error = "el hilo del portapapeles no contesto";
        var hecho = new ManualResetEventSlim();

        Hacer(() =>
        {
            try
            {
                var datos = new DatosVirtuales(lista, _pedir);
                var hr = OleSetClipboard(datos);

                if (hr == 0)
                {
                    _actual = datos;
                    error = null;
                }
                else
                {
                    error = $"OleSetClipboard fallo: 0x{hr:X8}";
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            hecho.Set();
        });

        hecho.Wait(TimeSpan.FromSeconds(5));
        return error;
    }

    /// <summary>Quita la promesa si sigue siendo lo que hay copiado. Despues de
    /// esto ya no hay quien sirva los trozos, y dejarla seria un pegado que
    /// falla a medias.</summary>
    public void Cerrar()
    {
        Hacer(() =>
        {
            if (_actual is not null && OleIsCurrentClipboard(_actual) == 0)
                OleSetClipboard(null);

            _actual = null;
            _fin = true;
        });

        _hilo.Join(TimeSpan.FromSeconds(2));
    }

    private void Hacer(Action accion)
    {
        _trabajo.Enqueue(accion);
        _hayTrabajo.Set();
    }

    private void Bucle()
    {
        OleInitialize(IntPtr.Zero);

        var esperas = new[] { _hayTrabajo.SafeWaitHandle.DangerousGetHandle() };

        while (!_fin)
        {
            MsgWaitForMultipleObjectsEx(1, esperas, Infinito, QsAllInput, MwmoInputAvailable);

            while (PeekMessage(out var mensaje, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref mensaje);
                DispatchMessage(ref mensaje);
            }

            while (_trabajo.TryDequeue(out var accion))
                accion();
        }

        OleUninitialize();
    }

    // --------------------------------------------------------- Win32 / OLE

    private const uint Infinito = 0xFFFFFFFF;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Mensaje
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Privado;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reservado);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(IDataObject? datos);

    [DllImport("ole32.dll")]
    private static extern int OleIsCurrentClipboard(IDataObject datos);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint cuantos, IntPtr[] esperas, uint milisegundos, uint despertar, uint banderas);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Mensaje mensaje, IntPtr hwnd, uint desde, uint hasta, uint quitar);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Mensaje mensaje);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Mensaje mensaje);
}

/// <summary>Una linea de la lista que lee el Explorador: un archivo, con su
/// ruta en la PC de origen, o una carpeta, que no tiene ni ruta ni bytes.</summary>
public sealed record Entrada(string Ruta, string Relativa, ulong Tamano, bool Carpeta);

/// <summary>
/// El otro medio: servir un trozo de lo que se anuncio cuando el Explorador de
/// enfrente lo pide. Igual en los dos lados.
/// </summary>
public static class TrozoDeArchivo
{
    /// <summary>60 KB, como el resto de la transferencia: por debajo del tope de
    /// 64 KB del relay y del umbral del Large Object Heap.</summary>
    public const int Tamano = 60 * 1024;

    /// <summary>
    /// Hasta <see cref="Tamano"/> bytes de <paramref name="ruta"/> desde
    /// <paramref name="desde"/>. Un fallo va DENTRO del trozo, no como
    /// excepcion: al otro lado hay un Explorador esperando una respuesta.
    ///
    /// ponytail: abre el archivo en cada trozo. Son microsegundos contra el ida
    /// y vuelta de la red; si algun dia pesa, guardar el ultimo abierto.
    /// </summary>
    public static FileChunk Leer(string ruta, ulong desde, ulong total)
    {
        var trozo = new FileChunk { Path = ruta, Offset = desde, Total = total };

        try
        {
            using var archivo = new FileStream(
                ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            archivo.Seek((long)desde, SeekOrigin.Begin);

            var bufer = new byte[Tamano];
            var leidos = archivo.ReadAtLeast(bufer, bufer.Length, throwOnEndOfStream: false);

            trozo.Data = Google.Protobuf.ByteString.CopyFrom(bufer, 0, leidos);
            trozo.Last = desde + (ulong)leidos >= total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            trozo.Error = $"{Path.GetFileName(ruta)}: {ex.Message}";
        }

        return trozo;
    }
}

/// <summary>
/// La lista que lee el Explorador al pegar: un FILEGROUPDESCRIPTORW.
///
/// Aparte y publica para poder probarla: es aritmetica de bytes sobre una
/// estructura de Windows, y un desplazamiento mal puesto no da error -- da
/// nombres cortados o tamanos absurdos en el dialogo de copia.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DescriptorDeArchivos
{
    /// <summary>sizeof(FILEDESCRIPTORW).</summary>
    public const int Tamano = 592;

    private const uint FdAttributes = 0x0004;
    private const uint FdFileSize = 0x0040;
    private const uint FdProgressUi = 0x4000;

    public const uint AtributoCarpeta = 0x10;
    public const uint AtributoNormal = 0x80;

    /// <summary>
    /// cItems y detras un FILEDESCRIPTORW por pieza, en el MISMO orden: el
    /// Explorador pide luego el contenido de cada archivo por su posicion aqui.
    ///
    /// ponytail: sin fecha de modificacion (FD_WRITESTIME); lo pegado sale con
    /// la de hoy. Si importa, el anuncio tendria que traerla.
    /// </summary>
    public static byte[] Construir(IReadOnlyList<Entrada> piezas)
    {
        var bytes = new byte[4 + piezas.Count * Tamano];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)piezas.Count);

        for (var i = 0; i < piezas.Count; i++)
        {
            var pieza = piezas[i];
            var d = bytes.AsSpan(4 + i * Tamano, Tamano);

            BinaryPrimitives.WriteUInt32LittleEndian(d, FdAttributes | FdFileSize | FdProgressUi);
            BinaryPrimitives.WriteUInt32LittleEndian(d[36..], pieza.Carpeta ? AtributoCarpeta : AtributoNormal);
            BinaryPrimitives.WriteUInt32LittleEndian(d[64..], (uint)(pieza.Tamano >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(d[68..], (uint)pieza.Tamano);

            // cFileName es WCHAR[260] y tiene que acabar en cero: 259 como mucho.
            var nombre = pieza.Relativa.AsSpan(0, Math.Min(pieza.Relativa.Length, 259));
            Encoding.Unicode.GetBytes(nombre, d[72..]);
        }

        return bytes;
    }
}

/// <summary>El IDataObject que queda en el portapapeles de alla.</summary>
[ComVisible(true)]
[SupportedOSPlatform("windows")]
public sealed class DatosVirtuales(IReadOnlyList<Entrada> piezas, Func<string, ulong, FileChunk> pedir)
    : IDataObject
{
    private static readonly short CfDescriptor = Formato("FileGroupDescriptorW");
    private static readonly short CfContenido = Formato("FileContents");

    // "Copiar", no "mover": sin esto el Explorador puede tratarlo como cortar y
    // pedir que se borre el origen, que esta en otra PC.
    private static readonly short CfEfecto = Formato("Preferred DropEffect");

    private const int DropEffectCopy = 1;

    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int DvELIndex = unchecked((int)0x80040068);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const int DataSSameFormatEtc = 0x00040130;

    public void GetData(ref FORMATETC formato, out STGMEDIUM medio)
    {
        medio = default;

        if ((formato.cfFormat == CfDescriptor || formato.cfFormat == CfEfecto) && Pide(formato, TYMED.TYMED_HGLOBAL))
        {
            medio.tymed = TYMED.TYMED_HGLOBAL;
            medio.unionmember = Global(formato.cfFormat == CfDescriptor
                ? DescriptorDeArchivos.Construir(piezas)
                : BitConverter.GetBytes(DropEffectCopy));

            return;
        }

        if (formato.cfFormat == CfContenido && Pide(formato, TYMED.TYMED_ISTREAM))
        {
            if (formato.lindex < 0 || formato.lindex >= piezas.Count || piezas[formato.lindex].Carpeta)
                throw new COMException("Indice de archivo fuera de la lista", DvELIndex);

            medio.tymed = TYMED.TYMED_ISTREAM;
            medio.unionmember = Marshal.GetComInterfaceForObject<FlujoRemoto, IStream>(
                new FlujoRemoto(piezas[formato.lindex], pedir));

            return;
        }

        throw new COMException("Formato no ofrecido", DvEFormatEtc);
    }

    public int QueryGetData(ref FORMATETC formato)
        => (formato.cfFormat == CfDescriptor || formato.cfFormat == CfEfecto) && Pide(formato, TYMED.TYMED_HGLOBAL)
           || formato.cfFormat == CfContenido && Pide(formato, TYMED.TYMED_ISTREAM)
            ? 0
            : DvEFormatEtc;

    public IEnumFORMATETC EnumFormatEtc(DATADIR direccion)
    {
        if (direccion != DATADIR.DATADIR_GET)
            throw new COMException("Solo lectura", ENotImpl);

        FORMATETC[] formatos =
        [
            Entrada(CfDescriptor, TYMED.TYMED_HGLOBAL),
            Entrada(CfContenido, TYMED.TYMED_ISTREAM),
            Entrada(CfEfecto, TYMED.TYMED_HGLOBAL)
        ];

        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)formatos.Length, formatos, out var enumerador));
        return enumerador;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC entrada, out FORMATETC salida)
    {
        salida = entrada;
        salida.ptd = IntPtr.Zero;
        return DataSSameFormatEtc;
    }

    /// <summary>Se acepta y se tira. El Explorador escribe aqui al terminar
    /// ("Performed DropEffect", "Paste Succeeded"), y rechazarlo le haria creer
    /// que el pegado fallo.</summary>
    public void SetData(ref FORMATETC formato, ref STGMEDIUM medio, bool liberar)
    {
        if (liberar)
            ReleaseStgMedium(ref medio);
    }

    public void GetDataHere(ref FORMATETC formato, ref STGMEDIUM medio)
        => throw new COMException("No soportado", ENotImpl);

    public int DAdvise(ref FORMATETC formato, ADVF avisos, IAdviseSink sumidero, out int conexion)
    {
        conexion = 0;
        return OleEAdviseNotSupported;
    }

    public void DUnadvise(int conexion) => throw new COMException("No soportado", OleEAdviseNotSupported);

    public int EnumDAdvise(out IEnumSTATDATA enumerador)
    {
        enumerador = null!;
        return OleEAdviseNotSupported;
    }

    private static bool Pide(FORMATETC formato, TYMED soporte)
        => (formato.tymed & soporte) != 0 && formato.dwAspect == DVASPECT.DVASPECT_CONTENT;

    private static FORMATETC Entrada(short formato, TYMED soporte) => new()
    {
        cfFormat = formato,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = soporte
    };

    private static short Formato(string nombre) => unchecked((short)RegisterClipboardFormat(nombre));

    /// <summary>Un HGLOBAL movible con <paramref name="bytes"/>. Lo libera quien
    /// lo recibe, con ReleaseStgMedium.</summary>
    private static IntPtr Global(byte[] bytes)
    {
        var mango = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);

        if (mango == IntPtr.Zero)
            throw new OutOfMemoryException("GlobalAlloc");

        Marshal.Copy(bytes, 0, GlobalLock(mango), bytes.Length);
        GlobalUnlock(mango);
        return mango;
    }

    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string nombre);

    [DllImport("shell32.dll")]
    private static extern int SHCreateStdEnumFmtEtc(uint cuantos, FORMATETC[] formatos, out IEnumFORMATETC enumerador);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medio);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint banderas, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr mango);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr mango);
}

/// <summary>
/// El contenido de UN archivo, tal como lo lee el Explorador al pegar. Cada Read
/// trae de la PC del tecnico lo que falte.
/// </summary>
[ComVisible(true)]
[SupportedOSPlatform("windows")]
public sealed class FlujoRemoto(Entrada pieza, Func<string, ulong, FileChunk> pedir) : IStream
{
    private const int StgEReadFault = unchecked((int)0x8003001E);
    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int StgEAccessDenied = unchecked((int)0x80030005);
    private const int ENotImpl = unchecked((int)0x80004001);

    private ulong _posicion;

    /// <summary>El ultimo trozo traido. El Explorador lee en bloques que no
    /// coinciden con los de 60 KB; sin guardar el sobrante habria que volver a
    /// pedir casi cada trozo dos veces.</summary>
    private FileChunk? _ultimo;

    public void Read(byte[] destino, int cuantos, IntPtr leidos)
    {
        var hechos = 0;

        while (hechos < cuantos && _posicion < pieza.Tamano)
        {
            if (_ultimo is null
                || _posicion < _ultimo.Offset
                || _posicion >= _ultimo.Offset + (ulong)_ultimo.Data.Length)
            {
                _ultimo = pedir(pieza.Ruta, _posicion);

                if (_ultimo.Error.Length > 0)
                    throw new COMException(_ultimo.Error, StgEReadFault);

                // Mas corto que lo anunciado: el archivo cambio por el camino.
                // Cortarlo en silencio dejaria un archivo truncado que parece
                // bueno.
                if (_ultimo.Offset != _posicion || _ultimo.Data.Length == 0)
                    throw new COMException($"{pieza.Relativa} cambio mientras se copiaba", StgEReadFault);
            }

            var desde = (int)(_posicion - _ultimo.Offset);
            var cuanto = Math.Min(_ultimo.Data.Length - desde, cuantos - hechos);

            _ultimo.Data.Span.Slice(desde, cuanto).CopyTo(destino.AsSpan(hechos));
            hechos += cuanto;
            _posicion += (ulong)cuanto;
        }

        if (leidos != IntPtr.Zero)
            Marshal.WriteInt32(leidos, hechos);
    }

    public void Seek(long desplazamiento, int origen, IntPtr nueva)
    {
        var posicion = origen switch
        {
            0 => desplazamiento,
            1 => (long)_posicion + desplazamiento,
            2 => (long)pieza.Tamano + desplazamiento,
            _ => throw new COMException("Origen invalido", StgEInvalidFunction)
        };

        if (posicion < 0)
            throw new COMException("Antes del principio", StgEInvalidFunction);

        _posicion = (ulong)posicion;

        if (nueva != IntPtr.Zero)
            Marshal.WriteInt64(nueva, posicion);
    }

    public void Stat(out STATSTG estado, int banderas) => estado = new STATSTG
    {
        type = 2, // STGTY_STREAM
        cbSize = (long)pieza.Tamano,

        // STATFLAG_NONAME: quien pregunta no quiere el nombre (y no lo liberaria).
        pwcsName = (banderas & 1) != 0 ? null! : Path.GetFileName(pieza.Relativa)
    };

    public void Write(byte[] origen, int cuantos, IntPtr escritos)
        => throw new COMException("Solo lectura", StgEAccessDenied);

    public void Commit(int banderas) { }

    public void Revert() { }

    public void SetSize(long tamano) => throw new COMException("Solo lectura", StgEAccessDenied);

    public void CopyTo(IStream destino, long cuantos, IntPtr leidos, IntPtr escritos)
        => throw new COMException("No soportado", ENotImpl);

    public void LockRegion(long desde, long cuantos, int tipo) => throw new COMException("No soportado", ENotImpl);

    public void UnlockRegion(long desde, long cuantos, int tipo) => throw new COMException("No soportado", ENotImpl);

    public void Clone(out IStream copia) => throw new COMException("No soportado", ENotImpl);
}
