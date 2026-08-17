using DeviceHub.Remote.Contracts;

namespace DeviceHub.RemoteHost.Files;

/// <summary>
/// Los archivos de la PC controlada, por el relay. Fase 24.
///
/// SIN TOPE DE TAMANO. El limite de 4 MB de la tuberia de comandos venia de que
/// alli el resultado acaba en una columna de MySQL; aqui los bytes van por el
/// stream que ya esta abierto y no tocan la base de datos.
///
/// Lo que SI tiene tope es el mensaje: 64 KiB por trozo, el mismo que el video y
/// por el mismo motivo -- por debajo del umbral del Large Object Heap.
///
/// Corre con los permisos del usuario conectado, que es quien lanzo el host. No
/// hay lista de rutas prohibidas: las ACL de Windows ya deciden, y una lista
/// propia daria una falsa sensacion de control sobre una sesion que ademas tiene
/// raton y teclado.
/// </summary>
public sealed class FileService : IDisposable
{
    /// <summary>Trozo de lectura. Coincide con el tope del protocolo.</summary>
    private const int Trozo = 60 * 1024;

    /// <summary>
    /// Subida en curso. Una a la vez: el visor manda un archivo y espera, y
    /// admitir varias solo multiplicaria los archivos a medias que hay que
    /// limpiar cuando la sesion se corta.
    /// </summary>
    private FileStream? _subida;
    private string _rutaSubida = string.Empty;

    /// <summary>
    /// Lista una carpeta. Ruta vacia = las unidades, que es por donde hay que
    /// empezar cuando no se sabe nada de la maquina de enfrente.
    /// </summary>
    public static FileList Listar(string ruta)
    {
        var lista = new FileList { Path = ruta };

        try
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                foreach (var unidad in DriveInfo.GetDrives().Where(u => u.IsReady))
                {
                    lista.Entries.Add(new FileEntry
                    {
                        Name = unidad.Name,
                        Directory = true
                    });
                }

                return lista;
            }

            var completa = Path.GetFullPath(ruta);
            lista.Path = completa;

            foreach (var directorio in Directory.EnumerateDirectories(completa))
            {
                lista.Entries.Add(new FileEntry
                {
                    Name = Path.GetFileName(directorio),
                    Directory = true,
                    ModifiedUs = Marca(directorio)
                });
            }

            foreach (var archivo in Directory.EnumerateFiles(completa))
            {
                // El tamano se lee por archivo y puede fallar solo (borrado a
                // mitad del recorrido). Que un archivo se esfume no debe tumbar
                // el listado entero.
                try
                {
                    var info = new FileInfo(archivo);

                    lista.Entries.Add(new FileEntry
                    {
                        Name = info.Name,
                        Size = (ulong)info.Length,
                        ModifiedUs = Marca(archivo)
                    });
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            lista.Entries.Clear();
            lista.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return lista;
    }

    private static long Marca(string ruta)
        => new DateTimeOffset(File.GetLastWriteTimeUtc(ruta), TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;

    /// <summary>
    /// Lee un archivo desde `offset` y entrega los trozos por `emitir`.
    ///
    /// Sincrono y desde un hilo propio: el que llama es el hilo de red, y leer
    /// medio giga alli dejaria la sesion sin atender pings, entrada ni
    /// portapapeles mientras dura.
    /// </summary>
    public static void Leer(
        string ruta, ulong offset, Action<FileChunk> emitir, CancellationToken cancellationToken)
    {
        try
        {
            using var archivo = new FileStream(
                ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var total = (ulong)archivo.Length;

            if (offset > total)
            {
                // El que pide dice tener mas de lo que hay: su copia es de otro
                // archivo, o el de aqui se recorto. Se empieza de cero.
                offset = 0;
            }

            archivo.Seek((long)offset, SeekOrigin.Begin);

            var bufer = new byte[Trozo];

            // Un archivo VACIO tambien tiene que llegar: sin esto la descarga se
            // queda esperando un ultimo trozo que nunca sale.
            if (total == 0)
            {
                emitir(new FileChunk { Path = ruta, Offset = 0, Total = 0, Last = true });
                return;
            }

            while (offset < total && !cancellationToken.IsCancellationRequested)
            {
                var leidos = archivo.Read(bufer, 0, bufer.Length);

                if (leidos <= 0)
                    break;

                emitir(new FileChunk
                {
                    Path = ruta,
                    Offset = offset,
                    Total = total,
                    Data = Google.Protobuf.ByteString.CopyFrom(bufer, 0, leidos),
                    Last = offset + (ulong)leidos >= total
                });

                offset += (ulong)leidos;
            }
        }
        catch (Exception ex)
        {
            emitir(new FileChunk
            {
                Path = ruta,
                Error = $"{ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Recibe un trozo de una subida y devuelve el acuse con lo que hay en disco.
    ///
    /// EL SONDEO: un trozo sin datos y sin `last` no escribe nada, solo contesta
    /// cuanto habia ya. Es como el visor averigua por donde reanudar sin tener
    /// que adivinarlo.
    /// </summary>
    public FileAck Escribir(FileChunk trozo)
    {
        try
        {
            if (trozo.Error.Length > 0)
            {
                // El emisor se rindio. Se cierra lo que haya SIN borrarlo: eso
                // parcial es exactamente lo que permite reanudar despues.
                Cerrar();
                return new FileAck { Path = trozo.Path, Error = trozo.Error };
            }

            if (_subida is null || !string.Equals(_rutaSubida, trozo.Path, StringComparison.OrdinalIgnoreCase))
            {
                Cerrar();

                var carpeta = Path.GetDirectoryName(trozo.Path);

                if (!string.IsNullOrEmpty(carpeta))
                    Directory.CreateDirectory(carpeta);

                _subida = new FileStream(trozo.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                _rutaSubida = trozo.Path;
            }

            if (trozo.Data.Length > 0)
            {
                // Se escribe DONDE dice el emisor, no al final. Reanudar puede
                // pedir un offset menor que lo que hay si el ultimo trozo llego a
                // medias, y entonces esos bytes se pisan.
                _subida.Seek((long)trozo.Offset, SeekOrigin.Begin);
                trozo.Data.WriteTo(_subida);
            }

            if (trozo.Last)
            {
                // Un archivo que se reescribe mas corto que el que habia dejaria
                // cola del anterior detras.
                _subida.SetLength(_subida.Position);
                _subida.Flush();
            }

            var recibidos = (ulong)_subida.Length;

            if (trozo.Last)
                Cerrar();

            return new FileAck { Path = trozo.Path, Received = recibidos };
        }
        catch (Exception ex)
        {
            Cerrar();
            return new FileAck { Path = trozo.Path, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private void Cerrar()
    {
        _subida?.Dispose();
        _subida = null;
        _rutaSubida = string.Empty;
    }

    public void Dispose() => Cerrar();
}
