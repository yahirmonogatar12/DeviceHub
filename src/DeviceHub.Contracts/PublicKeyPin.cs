using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeviceHub.Contracts;

/// <summary>
/// Pin de clave publica (SPKI), no thumbprint del certificado.
///
/// Renovar un certificado vencido reutilizando el mismo par de claves NO cambia
/// este valor, asi que la renovacion anual deja de ser un evento capaz de dejar
/// a toda la planta sin conexion. Solo un cambio real de clave exige la rotacion
/// multi-pin de cuatro pasos.
///
/// Vive en Contracts porque lo necesitan servidor (publicar el pin) y agente
/// (validarlo). ponytail: tres lineas compartidas no justifican un proyecto.
/// </summary>
public static class PublicKeyPin
{
    public static string Compute(X509Certificate2 certificate)
        => Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
}
