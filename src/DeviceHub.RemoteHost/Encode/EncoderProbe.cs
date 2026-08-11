using SharpGen.Runtime;
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
            Console.WriteLine("Codificadores H.264 (hardware primero):");
            Console.WriteLine();

            var encontrados = 0;

            foreach (var (nombre, hardware, activate) in Enumerate())
            {
                encontrados++;
                Console.WriteLine($"  [{(hardware ? "hardware" : "software")}] {nombre}");

                using (activate)
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
    /// SORTANDFILTER pone los de hardware delante, que es lo que queremos; se
    /// piden ademas los sincronos y asincronos porque los MFT de hardware suelen
    /// ser asincronos y sin esa bandera no aparecen.
    /// </summary>
    internal static IEnumerable<(string Name, bool Hardware, IMFActivate Activate)> Enumerate()
    {
        const uint SyncMft = 0x1, AsyncMft = 0x2, Hardware = 0x4, SortAndFilter = 0x40;

        var salida = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        using var coleccion = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            SyncMft | AsyncMft | Hardware | SortAndFilter,
            null,
            salida);

        foreach (var activate in coleccion)
        {
            var nombre = Attribute(activate, FriendlyName) ?? "(sin nombre)";
            var esHardware = Attribute(activate, HardwareUrl) is not null;

            yield return (nombre, esHardware, activate);
        }
    }

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
