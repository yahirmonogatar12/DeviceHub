using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceHub.Contracts;

namespace DeviceHub.Server.Security;

/// <summary>
/// Certificado TLS del servidor. Se autogenera en el primer arranque para que
/// desplegar no dependa de montar una PKI en una LAN cerrada.
///
/// Los agentes NO validan cadena ni vencimiento: fijan la clave publica (SPKI).
/// Por eso la vigencia larga es segura y renovar reutilizando el mismo par de
/// claves no mueve el pin ni desconecta a nadie.
/// </summary>
public static class CertificateProvider
{
    public static X509Certificate2 LoadOrCreate(string directory, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server.pfx");

        if (File.Exists(path))
        {
            var existing = Load(path);
            logger.LogInformation("Certificado cargado de {Path}", path);
            LogPin(existing, logger);
            return existing;
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=DeviceHub Server ({Environment.MachineName})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], true)); // serverAuth
        request.CertificateExtensions.Add(BuildSubjectAlternativeNames());

        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        // Password nula a proposito: guardarla junto al archivo seria teatro. Lo
        // que protege la clave es la ACL del directorio en ProgramData.
        File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx));

        var certificate = Load(path);
        logger.LogWarning("Certificado autogenerado en {Path}", path);
        LogPin(certificate, logger);
        return certificate;
    }

    private static X509Certificate2 Load(string path)
        => X509CertificateLoader.LoadPkcs12FromFile(
            path, null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

    private static void LogPin(X509Certificate2 certificate, ILogger logger)
        => logger.LogInformation(
            "Pin SPKI del servidor: {Pin}  <- este es el valor que debe llevar el instalador del agente",
            PublicKeyPin.Compute(certificate));

    private static X509Extension BuildSubjectAlternativeNames()
    {
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddDnsName(Environment.MachineName);
        builder.AddDnsName("localhost");
        builder.AddIpAddress(IPAddress.Loopback);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address.Address))
                {
                    builder.AddIpAddress(address.Address);
                }
            }
        }

        return builder.Build();
    }
}
