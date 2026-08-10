using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceHub.Agent.Updater;

/// <summary>
/// `update.json` en el recurso compartido.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    /// <summary>Nombre del .zip, relativo al recurso. Nunca una ruta.</summary>
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("notes")] public string? Notes { get; set; }

    public static UpdateManifest? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class UpdateDecision
{
    /// <summary>
    /// Decide si un manifiesto se aplica. Pura: es la parte que se testea.
    ///
    /// Se rechaza cualquier version que no sea ESTRICTAMENTE mayor. Permitir
    /// bajar de version convertiria el recurso compartido en una forma de
    /// reintroducir una vulnerabilidad ya corregida en toda la planta.
    /// </summary>
    public static bool ShouldApply(Version current, UpdateManifest? manifest, out string reason)
    {
        if (manifest is null)
        {
            reason = "Manifiesto ilegible";
            return false;
        }

        if (!Version.TryParse(manifest.Version, out var candidate))
        {
            reason = $"Version invalida en el manifiesto: '{manifest.Version}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.File) || string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            reason = "El manifiesto no trae archivo o hash";
            return false;
        }

        // El nombre no puede llevar ruta: `..\..\evil.zip` saldria del recurso.
        if (manifest.File.Contains('\\') || manifest.File.Contains('/') || manifest.File.Contains(".."))
        {
            reason = "El nombre de archivo del manifiesto no puede contener ruta";
            return false;
        }

        if (candidate <= current)
        {
            reason = $"Ya en {current}, el manifiesto ofrece {candidate}";
            return false;
        }

        reason = $"{current} -> {candidate}";
        return true;
    }
}
