namespace DeviceHub.Server.Updates;

/// <summary>
/// Los paquetes del agente, servidos por el MISMO puerto que todo lo demas.
///
/// POR QUE EXISTE ESTO. El auto-update se hizo contra un recurso SMB, y la unica
/// PC de la flota que consiguio actualizarse sola fue la unica donde ese
/// "recurso" es un disco local: el propio servidor. Las demas llevaban meses en
/// 1.60, 1.48 y 1.12 hablando con el servidor cada treinta segundos sin problema
/// -- SMB entre subredes no llega, y un servicio LocalSystem que sale a un UNC
/// remoto en un grupo de trabajo se autentica como anonimo.
///
/// El agente ya tiene un canal autenticado, cifrado y con el certificado pinado
/// contra esta misma maquina. Usar un segundo transporte para actualizarse era
/// darse otra oportunidad de fallar, y se aprovecho.
///
/// SE SIRVE SIN AUTENTICAR, a proposito: el paquete no lleva secretos --
/// publish-update.ps1 le quita el appsettings.json justamente para eso -- y
/// quien pueda llegar al 5443 ya podia leer el recurso compartido. Lo que si
/// importa es quien puede ESCRIBIR ahi: eso es ejecucion como SYSTEM en cada PC,
/// y lo gobierna la ACL de la carpeta, no este endpoint.
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdates(this WebApplication app, string raiz)
    {
        var completa = Path.GetFullPath(raiz);

        app.MapGet("/updates/{anillo}/{archivo}", (string anillo, string archivo) =>
        {
            if (!EsNombre(anillo) || !EsNombre(archivo))
                return Results.NotFound();

            var ruta = Path.GetFullPath(Path.Combine(completa, anillo, archivo));

            // Cinturon y tirantes. EsNombre ya no deja pasar separadores ni "..",
            // pero esto se comprueba sobre la ruta YA NORMALIZADA, que es lo unico
            // que de verdad demuestra que no se salio de la carpeta.
            if (!ruta.StartsWith(completa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            if (!File.Exists(ruta))
                return Results.NotFound();

            // El manifiesto como JSON y no como flujo de bytes: la primera
            // pregunta cuando una PC no se actualiza es "que anuncia el
            // servidor", y con octet-stream el navegador lo descarga en vez de
            // enseñarlo.
            var tipo = ruta.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "application/octet-stream";

            return Results.File(ruta, tipo);
        }).AllowAnonymous();

        app.Logger.LogInformation("Actualizaciones del agente servidas desde {Ruta}", completa);
    }

    /// <summary>Un nombre, nunca una ruta.</summary>
    public static bool EsNombre(string valor)
        => valor.Length is > 0 and <= 128
           && valor != "." && valor != ".."
           && !valor.Contains("..", StringComparison.Ordinal)
           && valor.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
