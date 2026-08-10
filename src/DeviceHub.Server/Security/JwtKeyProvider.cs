using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace DeviceHub.Server.Security;

/// <summary>
/// Clave de firma de los JWT del dashboard. Se genera al primer arranque y se
/// guarda junto al certificado; no hay ningun default en el repositorio que
/// alguien pueda olvidar cambiar.
/// </summary>
public sealed class JwtKeyProvider
{
    public JwtKeyProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jwt.key");

        if (!File.Exists(path))
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(64));

        SigningKey = new SymmetricSecurityKey(File.ReadAllBytes(path));
    }

    public SymmetricSecurityKey SigningKey { get; }
}
