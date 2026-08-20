using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Modo --encoders: que codificadores H.264 hay en esta maquina y que aceptan.
///
/// Existe porque el formato de entrada NO se puede dar por supuesto. Algunos MFT
/// de hardware tragan BGRA y convierten por dentro; otros exigen NV12 y hay que
/// convertir antes. Preguntarlo cuesta menos que descubrirlo en planta.
/// </summary>
public static class EncoderProbe
{
    public static int Run()
    {
        MediaFactory.MFStartup(true).CheckError();

        try
        {
            // Por GPU primero: es como los elige el encoder de verdad, y ver la
            // lista de cada adaptador por separado es lo que delata un filtro que
            // no filtra.
            using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
            {
                for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success && adapter is not null; a++)
                {
                    using (adapter)
                    {
                        var suyos = EnumerateForAdapter(adapter.Description1.Luid);

                        Console.WriteLine($"GPU {a}: {adapter.Description.Description.Trim()}");

                        foreach (var (nombre, hardware, activate) in suyos)
                        {
                            Console.WriteLine($"  [{(hardware ? "hardware" : "software")}] {nombre}");
                            activate.Dispose();
                        }

                        if (suyos.Count == 0)
                            Console.WriteLine("  (ninguno)");

                        Console.WriteLine();
                    }
                }
            }

            Console.WriteLine("Todos los codificadores H.264 de la maquina (hardware primero):");
            Console.WriteLine();

            var encontrados = 0;

            using var lista = Enumerate();

            foreach (var (nombre, hardware, activate) in lista.Items)
            {
                encontrados++;
                Console.WriteLine($"  [{(hardware ? "hardware" : "software")}] {nombre}");

                {
                    IMFTransform? transform = null;

                    try
                    {
                        transform = activate.ActivateObject<IMFTransform>();
                        Unlock(transform);

                        foreach (var formato in SupportedInputs(transform))
                            Console.WriteLine($"      {formato}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"      no se pudo activar: {ex.Message}");
                    }
                    finally
                    {
                        transform?.Dispose();
                    }
                }

                Console.WriteLine();
            }

            if (encontrados == 0)
            {
                Console.Error.WriteLine("Ningun codificador H.264 en esta maquina.");
                Console.Error.WriteLine("En Windows Server, Media Foundation es una caracteristica opcional que no viene instalada.");
                return 2;
            }

            return 0;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>
    /// Los codificadores encontrados Y la coleccion que los sostiene.
    ///
    /// Van juntos a proposito: los IMFActivate son hijos de la coleccion y mueren
    /// con ella. Enumerarlos de forma perezosa y soltar la coleccion al terminar
    /// devuelve una lista de objetos COM ya liberados, que fallan con un
    /// NullReferenceException dentro de Vortice sin decir por que.
    /// </summary>
    internal sealed record EncoderList(
        IMFActivateCollection Collection,
        IReadOnlyList<(string Name, bool Hardware, IMFActivate Activate)> Items) : IDisposable
    {
        public void Dispose() => Collection.Dispose();
    }

    /// <summary>
    /// SORTANDFILTER pone los de hardware delante, que es lo que queremos; se
    /// piden ademas los sincronos y asincronos porque los MFT de hardware suelen
    /// ser asincronos y sin esa bandera no aparecen.
    /// </summary>
    internal static EncoderList Enumerate(Guid? salidaPedida = null)
    {
        var salida = salidaPedida is { } g
            ? new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = g }
            : OutputH264;

        var coleccion = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder, EnumFlags, null, salida);

        var encontrados = new List<(string, bool, IMFActivate)>();

        foreach (var activate in coleccion)
            encontrados.Add(Describe(activate));

        return new EncoderList(coleccion, encontrados);
    }

    /// <summary>
    /// Los codificadores de UNA GPU concreta, identificada por su LUID.
    ///
    /// El LUID es el identificador exacto del adaptador DXGI, y MFT_ENUM_ADAPTER_LUID
    /// existe justamente para esto. Es mejor criterio que el ID de fabricante, que
    /// ademas esta documentado como opcional: dos NVIDIA en la misma maquina
    /// comparten fabricante y no comparten LUID.
    ///
    /// Devuelve una lista vacia si el filtro no encuentra nada; el llamante decide
    /// si cae al criterio de respaldo.
    /// </summary>
    internal static List<(string Name, bool Hardware, IMFActivate Activate)> EnumerateForAdapter(
        Luid luid, Guid? salidaPedida = null)
    {
        var encontrados = new List<(string, bool, IMFActivate)>();

        using var filtro = MediaFactory.MFCreateAttributes(1);

        // Blob de 8 bytes con la estructura LUID, no un UINT64. Puesto como
        // entero el atributo se acepta sin queja y el filtro no filtra: todas
        // las GPU devuelven la lista completa, que es como se descubrio.
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), luid.LowPart);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), luid.HighPart);
        filtro.SetBlob(MftEnumAdapterLuid, bytes);

        var salida = salidaPedida is { } g
            ? new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = g }
            : OutputH264;

        MediaFactory.MFTEnum2(
            TransformCategoryGuids.VideoEncoder, EnumFlags, null, salida, filtro,
            out var arreglo, out var cuantos);

        if (arreglo == IntPtr.Zero)
            return encontrados;

        try
        {
            for (var i = 0; i < cuantos; i++)
                encontrados.Add(Describe(new IMFActivate(Marshal.ReadIntPtr(arreglo, i * IntPtr.Size))));
        }
        finally
        {
            Marshal.FreeCoTaskMem(arreglo);
        }

        return encontrados;
    }

    private static (string, bool, IMFActivate) Describe(IMFActivate activate)
        => (Attribute(activate, FriendlyName) ?? "(sin nombre)",
            Attribute(activate, HardwareUrl) is not null,
            activate);

    /// <summary>El LUID viaja como un UINT64 con la parte alta arriba.</summary>
    private static ulong Pack(Luid luid) => ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;

    /// <summary>
    /// SORTANDFILTER pone los de hardware delante; se piden sincronos y
    /// asincronos porque los de hardware suelen ser asincronos y sin esa bandera
    /// no aparecen.
    /// </summary>
    private const uint EnumFlags = 0x1 | 0x2 | 0x4 | 0x40;

    private static RegisterTypeInfo OutputH264 => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = VideoFormatGuids.H264
    };

    // GUID sacado de mfapi.h del SDK de Windows, no de memoria: un GUID
    // equivocado aqui no da error, simplemente se ignora el filtro y todo
    // aparenta funcionar.
    private static readonly Guid MftEnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");

    /// <summary>
    /// Que formatos de entrada acepta de verdad, probando a fijarlos.
    ///
    /// Hay que fijar la SALIDA primero: un codificador H.264 no sabe que
    /// entradas admite hasta saber que tiene que producir, y preguntarle antes
    /// devuelve error. Es la razon de que el primer sondeo no listara ninguna.
    /// </summary>
    private static IEnumerable<string> SupportedInputs(IMFTransform transform)
    {
        using var salida = MediaFactory.MFCreateMediaType();
        salida.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        salida.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        salida.Set(MediaTypeAttributeKeys.AvgBitrate, 6_000_000u);
        salida.Set(MediaTypeAttributeKeys.FrameSize, Pack(1920, 1080));
        salida.Set(MediaTypeAttributeKeys.FrameRate, Pack(60, 1));
        salida.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // Progressive

        string? fallo = null;

        try
        {
            transform.SetOutputType(0, salida, 0);
        }
        catch (SharpGenException ex)
        {
            fallo = ex.Message.Split('\n')[0];
        }

        if (fallo is not null)
        {
            yield return $"no acepta H.264 1080p60: {fallo}";
            yield break;
        }

        foreach (var (nombre, formato) in new[]
                 {
                     ("NV12", VideoFormatGuids.NV12),
                     ("ARGB32", VideoFormatGuids.Argb32),
                     ("RGB32", VideoFormatGuids.Rgb32)
                 })
        {
            using var entrada = MediaFactory.MFCreateMediaType();
            entrada.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            entrada.Set(MediaTypeAttributeKeys.Subtype, formato);
            entrada.Set(MediaTypeAttributeKeys.FrameSize, Pack(1920, 1080));
            entrada.Set(MediaTypeAttributeKeys.FrameRate, Pack(60, 1));
            entrada.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);

            var aceptado = true;

            try
            {
                transform.SetInputType(0, entrada, 0);
            }
            catch (SharpGenException)
            {
                aceptado = false;
            }

            yield return $"entrada {nombre,-7} {(aceptado ? "SI" : "no")}";
        }
    }

    /// <summary>FrameSize y FrameRate son dos UINT32 empaquetados en un UINT64.</summary>
    private static ulong Pack(uint alto, uint bajo) => ((ulong)alto << 32) | bajo;

    /// <summary>
    /// Los MFT de hardware son asincronos y vienen bloqueados: sin esto, activarlos
    /// funciona pero cualquier uso posterior falla.
    /// </summary>
    private static void Unlock(IMFTransform transform)
    {
        try
        {
            transform.Attributes?.Set(AsyncUnlock, 1u);
        }
        catch (SharpGenException)
        {
            // Los sincronos no tienen esa clave, y no la necesitan.
        }
    }

    private static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    // GUID a mano: Vortice no expone estas dos claves de MFTEnumEx.
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid HardwareUrl = new("2fb866ac-b078-4942-ab6c-003d05cda674");

    private static string? Attribute(IMFActivate activate, Guid key)
    {
        try
        {
            return activate.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }
}
