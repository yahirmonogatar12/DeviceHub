using System.Security.Cryptography;
using System.Text;

namespace DeviceHub.Server.Security;

/// <summary>
/// Generacion y verificacion de secretos. Nada se guarda en claro (regla 4):
/// tokens y codigos viven hasheados, las passwords con PBKDF2.
/// </summary>
public static class Secrets
{
    // Sin I, L, O, 0, 1: un tecnico va a teclear esto mirando una pantalla.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private const int Pbkdf2Iterations = 210_000; // OWASP, PBKDF2-HMAC-SHA256
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>Token permanente de maquina. Se devuelve una vez y se guarda hasheado.</summary>
    public static string NewMachineToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Codigo de enrolamiento con formato ENROLL-XXXX-XXXX.</summary>
    public static string NewEnrollmentCode()
        => $"ENROLL-{RandomBlock(4)}-{RandomBlock(4)}";

    /// <summary>Password inicial del admin. Legible porque se teclea una vez.</summary>
    public static string NewReadablePassword()
        => string.Join('-', Enumerable.Range(0, 4).Select(_ => RandomBlock(4)));

    public static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Comparacion en tiempo fijo de dos hashes hex.</summary>
    public static bool FixedTimeEqualsHex(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    private static string RandomBlock(int length)
        => string.Concat(Enumerable.Range(0, length)
            .Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]));
}
