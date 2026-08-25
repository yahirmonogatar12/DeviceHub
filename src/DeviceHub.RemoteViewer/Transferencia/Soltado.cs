using System.IO;

namespace DeviceHub.RemoteViewer.Transferencia;

// El espacio se llama Transferencia y no Archivos porque `Archivos` ya es el
// ListBox del panel en SesionRemota, y ahi el nombre del control gana al del
// espacio de nombres.

/// <summary>
/// Que hacer con lo que alguien suelta en la ventana del visor.
///
/// Clase suelta y sin nada de WPF, por el mismo motivo que Escalado: para probar
/// un metodo de una clase que hereda de UserControl, el proyecto de pruebas
/// tendria que activar UseWPF entero. Lo que hay aqui son las reglas -- que
/// entra, que no, y con que nombre llega al otro lado -- y se prueban solas.
/// </summary>
public static class Soltado
{
    /// <summary>Lo que hay que subir, o por que no se puede.</summary>
    public sealed record Plan(IReadOnlyList<(string Local, string Remoto)> Subidas, string? Queja);

    /// <summary>
    /// <paramref name="archivos"/> son las rutas que existen y son ARCHIVOS;
    /// <paramref name="habiaCarpetas"/> dice si ademas se solto alguna carpeta,
    /// que todavia no se soportan -- igual que en el resto de la Fase 24.
    ///
    /// El destino sale de la carpeta abierta en el panel. No se inventa: el host
    /// corre como SYSTEM, asi que %USERPROFILE% apunta al perfil de SYSTEM y no
    /// al del usuario sentado en esa PC, y los archivos acabarian donde nadie los
    /// va a buscar.
    /// </summary>
    public static Plan Preparar(
        IReadOnlyList<string> archivos, bool habiaCarpetas, string carpetaRemota)
    {
        if (archivos.Count == 0)
        {
            return new Plan([], habiaCarpetas
                ? "Carpetas todavia no: arrastra los archivos sueltos."
                : "No se pudo leer nada de lo que soltaste.");
        }

        if (string.IsNullOrWhiteSpace(carpetaRemota))
        {
            return new Plan([],
                $"Elige primero en que carpeta de la PC remota van los {archivos.Count} archivos.");
        }

        // SOLO EL NOMBRE, nunca la ruta de aqui. Combinar la ruta local entera
        // mandaria "C:\Users\..." dentro de la carpeta remota, y de paso es lo
        // que convierte un nombre con ".." en un escape de la carpeta elegida:
        // GetFileName ya lo deja en nada.
        var subidas = archivos
            .Select(local => (local, Path.Combine(carpetaRemota, Path.GetFileName(local))))
            .Where(par => par.Item2.Length > carpetaRemota.Length)
            .ToList();

        return subidas.Count == 0
            ? new Plan([], "Ninguno de esos archivos tiene un nombre que se pueda usar.")
            : new Plan(subidas, null);
    }
}
