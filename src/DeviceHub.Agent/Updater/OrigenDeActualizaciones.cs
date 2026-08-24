using DeviceHub.Agent.Security;

namespace DeviceHub.Agent.Updater;

/// <summary>
/// De donde salen el manifiesto y el paquete. Dos operaciones, nada mas: leer un
/// texto pequeño y traerse un archivo grande a disco.
/// </summary>
public interface IOrigenDeActualizaciones
{
    /// <summary>El texto de update.json, o null si no se pudo leer.</summary>
    string? LeerManifiesto();

    /// <summary>Deja el paquete en <paramref name="destino"/>. False si no pudo.</summary>
    bool Traer(string archivo, string destino);
}

/// <summary>
/// El recurso compartido de siempre.
///
/// Sigue existiendo porque en la maquina que HOSPEDA el recurso funciona
/// perfectamente -- ahi es un disco local -- y porque es el unico camino
/// disponible mientras el servidor todavia no sirve el endpoint.
/// </summary>
public sealed class PorRecurso(string carpeta) : IOrigenDeActualizaciones
{
    public string? LeerManifiesto()
    {
        var ruta = Path.Combine(carpeta, UpdateService.ManifestFile);
        return File.Exists(ruta) ? File.ReadAllText(ruta) : null;
    }

    public bool Traer(string archivo, string destino)
    {
        var origen = Path.Combine(carpeta, archivo);

        if (!File.Exists(origen))
            return false;

        File.Copy(origen, destino, overwrite: true);
        return true;
    }

    public override string ToString() => carpeta;
}

/// <summary>
/// El propio servidor, por el mismo puerto y con el mismo certificado pinado que
/// el heartbeat.
///
/// CON RESPALDO, y el orden importa: si el servidor todavia no tiene el endpoint
/// contesta 404, y sin respaldo esa PC se quedaria sin actualizarse por haber
/// intentado la via nueva. El respaldo se usa solo cuando el servidor no
/// contesta, nunca para "mejorar" una respuesta que si llego.
/// </summary>
public sealed class PorServidor(
    PinnedChannelFactory canales,
    string baseUrl,
    IOrigenDeActualizaciones? respaldo,
    ILogger logger) : IOrigenDeActualizaciones
{
    public string? LeerManifiesto()
    {
        try
        {
            using var http = canales.CreateHttp();
            var respuesta = http.GetAsync($"{baseUrl}/{UpdateService.ManifestFile}").GetAwaiter().GetResult();

            if (respuesta.IsSuccessStatusCode)
                return respuesta.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            logger.LogInformation(
                "El servidor no sirve actualizaciones ({Codigo}); se prueba el recurso compartido",
                (int)respuesta.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                "No se pudo pedir el manifiesto al servidor ({Mensaje}); se prueba el recurso compartido",
                ex.Message);
        }

        return respaldo?.LeerManifiesto();
    }

    public bool Traer(string archivo, string destino)
    {
        try
        {
            using var http = canales.CreateHttp();
            var respuesta = http.GetAsync($"{baseUrl}/{archivo}").GetAwaiter().GetResult();

            if (respuesta.IsSuccessStatusCode)
            {
                // A disco segun llega. Un paquete son cuarenta megas y meterlos
                // en un byte[] antes de escribirlos serian cuarenta megas en el
                // Large Object Heap de un servicio que vive meses.
                using var entrada = respuesta.Content.ReadAsStream();
                using var salida = File.Create(destino);
                entrada.CopyTo(salida);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudo bajar {Archivo} del servidor: {Mensaje}", archivo, ex.Message);
        }

        return respaldo?.Traer(archivo, destino) ?? false;
    }

    public override string ToString() => baseUrl;
}
