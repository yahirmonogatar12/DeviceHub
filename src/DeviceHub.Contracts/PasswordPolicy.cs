namespace DeviceHub.Contracts;

/// <summary>
/// Requisitos minimos de contrasena.
///
/// Vive en Contracts para que el dashboard avise ANTES de enviar y el servidor
/// vuelva a comprobarlo al recibir. La validacion del cliente es comodidad; la
/// del servidor es la que cuenta.
///
/// Se exige longitud y no un zoo de simbolos: "Ilsan2026!" cumple cualquier
/// regla de mayusculas-numeros-simbolos y es adivinable; una frase larga no.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    private static readonly string[] Forbidden =
    [
        "password", "contrasena", "12345678", "qwerty", "admin", "devicehub", "ilsan"
    ];

    public static bool IsValid(string? password, out string error)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumLength)
        {
            error = $"La contrasena debe tener al menos {MinimumLength} caracteres";
            return false;
        }

        var lowered = password.ToLowerInvariant();

        if (Forbidden.Any(lowered.Contains))
        {
            error = "La contrasena contiene una palabra demasiado obvia";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
