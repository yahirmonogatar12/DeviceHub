using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DeviceHub.Contracts;
using Grpc.Net.Client;

namespace DeviceHub.Agent.Security;

/// <summary>
/// Canal gRPC que valida al servidor por pin de clave publica (SPKI), no por
/// cadena de CA ni por thumbprint del certificado.
///
/// El pin es un CONJUNTO, no un valor unico: durante la rotacion el agente
/// acepta {A, B} y el servidor no cambia de certificado hasta que el 100% de las
/// maquinas ONLINE reportan tener B. Sin ventana de caida.
/// </summary>
public sealed class PinnedChannelFactory(ILogger<PinnedChannelFactory> logger)
{
    private readonly HashSet<string> _pins = new(StringComparer.Ordinal);
    private volatile bool _trustOnFirstUse;

    /// <summary>Ultimo pin observado durante un TOFU, para persistirlo.</summary>
    public string? ObservedPin { get; private set; }

    public void SetPins(IEnumerable<string> pins)
    {
        lock (_pins)
        {
            _pins.Clear();
            foreach (var pin in pins.Where(p => !string.IsNullOrWhiteSpace(p)))
                _pins.Add(pin);

            // ponytail: sin ningun pin cargado (primera instalacion) se confia en
            // el primer certificado visto, como SSH. La ventana de exposicion es
            // la del codigo de enrolamiento, que dura minutos. Si el instalador
            // trae el pin, esto nunca se activa.
            _trustOnFirstUse = _pins.Count == 0;
        }

        if (_trustOnFirstUse)
            logger.LogWarning("Sin pin configurado: se confiara en el primer certificado del servidor (TOFU)");
    }

    public GrpcChannel Create(string address)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate)
            }
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
    }

    /// <summary>
    /// Un HttpClient con la MISMA validacion por pin que el canal gRPC.
    ///
    /// Lo usa el actualizador para bajarse el paquete del propio servidor. Que
    /// comparta la validacion no es comodidad: si se descargara codigo que se va
    /// a ejecutar como SYSTEM con una comprobacion de certificado mas floja que
    /// la del heartbeat, el eslabon debil seria justamente el que mas pesa.
    /// </summary>
    public HttpClient CreateHttp()
        => new(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate)
            }
        })
        {
            Timeout = TimeSpan.FromMinutes(10),

            // El servidor escucha en HTTP/2 por gRPC. HttpClient pide HTTP/1.1
            // salvo que se le diga otra cosa, asi que sin esto la descarga
            // fallaba contra un servidor que solo aceptara h2 -- y el agente se
            // iba al respaldo por SMB sin que se notara.
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

    private bool Validate(X509Certificate? certificate)
    {
        if (certificate is null)
            return false;

        using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var pin = PublicKeyPin.Compute(cert);

        lock (_pins)
        {
            if (_pins.Contains(pin))
                return true;

            if (_trustOnFirstUse)
            {
                ObservedPin = pin;
                _pins.Add(pin);
                _trustOnFirstUse = false;
                logger.LogWarning("TOFU: pin del servidor fijado en {Pin}", pin);
                return true;
            }
        }

        logger.LogError("Pin del servidor rechazado ({Pin}). Se requiere un recovery code emitido por un administrador.", pin);
        return false;
    }
}
