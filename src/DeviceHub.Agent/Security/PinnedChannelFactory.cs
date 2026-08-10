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
