using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.IO.Compression;

namespace DeviceHub.Agent.Updater;

/// <summary>
/// Auto-actualizacion desde un recurso compartido de red (Fase 16).
///
/// AVISO DE DISENO: un recurso compartido como origen de actualizaciones es una
/// via de compromiso de cadena de suministro. Quien pueda ESCRIBIR ahi ejecuta
/// codigo como SYSTEM en todas las PCs de planta. El SHA256 vive en el mismo
/// recurso, asi que protege contra corrupcion, no contra manipulacion: si
/// alguien cambia el zip tambien cambia el hash.
///
/// El control real es la firma Authenticode del ejecutable extraido, verificada
/// contra un thumbprint fijado en la configuracion del agente. Sin eso, la
/// seguridad de toda la flota es la ACL del recurso compartido, y el agente lo
/// dice en el log a nivel WARNING en cada comprobacion.
/// </summary>
public sealed class UpdateService(AgentOptions options, ILogger<UpdateService> logger)
{
    public const string ManifestFile = "update.json";
    public const string HealthFile = "health.json";

    /// <summary>
    /// Tiempo que espera el actualizador a que la version nueva se conecte antes
    /// de dar marcha atras. Si el agente nuevo no reporta salud, la PC quedaria
    /// invisible y habria que ir fisicamente: por eso hay rollback.
    /// </summary>
    public static readonly TimeSpan HealthDeadline = TimeSpan.FromMinutes(5);

    public bool TryCheckAndStage(Version currentVersion)
    {
        if (string.IsNullOrWhiteSpace(options.UpdateShare))
            return false;

        try
        {
            var manifestPath = Path.Combine(options.UpdateShare, ManifestFile);

            if (!File.Exists(manifestPath))
                return false;

            var manifest = UpdateManifest.Parse(File.ReadAllText(manifestPath));

            if (!UpdateDecision.ShouldApply(currentVersion, manifest, out var reason))
            {
                logger.LogDebug("Sin actualizacion: {Reason}", reason);
                return false;
            }

            logger.LogWarning("Actualizacion disponible: {Reason}", reason);
            return Stage(manifest!);
        }
        catch (Exception ex)
        {
            // El recurso caido no puede tumbar al agente: seguir monitorizando
            // importa mas que actualizarse hoy.
            logger.LogWarning("No se pudo comprobar el recurso de actualizacion: {Message}", ex.Message);
            return false;
        }
    }

    private bool Stage(UpdateManifest manifest)
    {
        var source = Path.Combine(options.UpdateShare, manifest.File);

        if (!File.Exists(source))
        {
            logger.LogError("El manifiesto apunta a {File}, que no existe en el recurso", manifest.File);
            return false;
        }

        var staging = Path.Combine(Path.GetTempPath(), $"devicehub-update-{manifest.Version}");
        Directory.CreateDirectory(staging);

        var localZip = Path.Combine(staging, manifest.File);
        File.Copy(source, localZip, overwrite: true);

        // Se verifica sobre la COPIA LOCAL, no sobre el recurso: comprobar en red
        // y luego extraer del mismo sitio deja una ventana para cambiarlo en medio.
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(localZip))).ToLowerInvariant();

        if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Hash del paquete no coincide. Esperado {Expected}, obtenido {Actual}",
                manifest.Sha256, actual);

            Directory.Delete(staging, recursive: true);
            return false;
        }

        var payload = Path.Combine(staging, "payload");

        if (Directory.Exists(payload))
            Directory.Delete(payload, recursive: true);

        ZipFile.ExtractToDirectory(localZip, payload);

        var executable = Path.Combine(payload, "DeviceHub.Agent.exe");

        if (!File.Exists(executable))
        {
            logger.LogError("El paquete no contiene DeviceHub.Agent.exe");
            Directory.Delete(staging, recursive: true);
            return false;
        }

        if (!VerifySignature(executable))
        {
            Directory.Delete(staging, recursive: true);
            return false;
        }

        LaunchUpdater(payload, manifest.Version);
        return true;
    }

    /// <summary>
    /// Firma Authenticode contra un thumbprint fijado.
    ///
    /// No basta con "tiene una firma valida": cualquiera puede firmar con un
    /// certificado propio. Tiene que ser EL certificado esperado.
    /// </summary>
    private bool VerifySignature(string executable)
    {
        if (string.IsNullOrWhiteSpace(options.UpdatePublisherThumbprint))
        {
            logger.LogWarning(
                "ACTUALIZANDO SIN VERIFICAR FIRMA. Con UpdatePublisherThumbprint vacio, la unica " +
                "proteccion de toda la flota es la ACL de {Share}: quien pueda escribir ahi ejecuta " +
                "codigo como SYSTEM en cada PC.", options.UpdateShare);

            return true;
        }

        try
        {
            // SYSLIB0057 marca obsoleta la CARGA de certificados por constructor,
            // y X509CertificateLoader es el reemplazo -- pero no cubre extraer el
            // firmante Authenticode de un PE. La alternativa seria parsear la
            // estructura WIN_CERTIFICATE a mano, que es peor en todos los sentidos.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(executable);
#pragma warning restore SYSLIB0057
            using var signer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

            var expected = options.UpdatePublisherThumbprint.Replace(" ", string.Empty);

            if (!string.Equals(signer.Thumbprint, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Firmante inesperado: {Actual}, se esperaba {Expected}", signer.Thumbprint, expected);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("El paquete no esta firmado o la firma es ilegible: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Lanza el actualizador y devuelve el control.
    ///
    /// UN SERVICIO NO PUEDE REEMPLAZAR SU PROPIO BINARIO EN EJECUCION. El
    /// actualizador tiene que ser un proceso separado que sobreviva a la
    /// detencion del servicio; si no, el auto-update no funciona y hay que ir PC
    /// por PC. El script se genera aqui, no se descarga: correria como SYSTEM.
    /// </summary>
    private void LaunchUpdater(string payload, string version)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var backupDir = installDir + ".anterior";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"devicehub-update-{version}.ps1");
        var healthPath = Path.Combine(options.DataDirectory, HealthFile);

        File.WriteAllText(scriptPath, $$"""
            $ErrorActionPreference = 'Stop'
            $servicio  = 'DeviceHubAgent'
            $instalado = '{{installDir}}'
            $respaldo  = '{{backupDir}}'
            $nuevo     = '{{payload}}'
            $salud     = '{{healthPath}}'

            Stop-Service $servicio -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5

            # RemoteHost es OTRO proceso: parar el servicio no lo mata, y su .exe
            # esta dentro de la carpeta que hay que mover.
            Get-Process DeviceHub.RemoteHost -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1

            if (Test-Path $respaldo) { Remove-Item $respaldo -Recurse -Force }

            # Si el intercambio falla a medias, la PC se queda con el servicio
            # PARADO y hay que ir fisicamente a levantarla. Se deshace lo que se
            # llego a hacer y se vuelve a arrancar lo que habia.
            try {
                Move-Item $instalado $respaldo
                Move-Item $nuevo $instalado
            }
            catch {
                if (-not (Test-Path $instalado)) { Move-Item $respaldo $instalado }
                Start-Service $servicio
                exit 1
            }

            if (Test-Path $salud) { Remove-Item $salud -Force }
            Start-Service $servicio

            # El agente nuevo escribe health.json al conectar. Si no aparece, la PC
            # quedaria invisible y habria que ir fisicamente: se da marcha atras.
            $limite = (Get-Date).AddMinutes({{HealthDeadline.TotalMinutes:0}})
            while ((Get-Date) -lt $limite) {
                if (Test-Path $salud) { exit 0 }
                Start-Sleep -Seconds 10
            }

            Stop-Service $servicio -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5
            Remove-Item $instalado -Recurse -Force
            Move-Item $respaldo $instalado
            Start-Service $servicio
            exit 1
            """);

        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });

        logger.LogWarning("Actualizador lanzado para la version {Version}. El servicio se detendra en breve.", version);
    }

    /// <summary>
    /// Marca de salud que el actualizador espera. Se escribe al conectar, no al
    /// arrancar: un agente que arranca pero no llega al servidor esta tan roto
    /// como uno que no arranca.
    /// </summary>
    public void WriteHealthMarker(Version version)
    {
        try
        {
            Directory.CreateDirectory(options.DataDirectory);

            File.WriteAllText(Path.Combine(options.DataDirectory, HealthFile),
                $$"""{"version":"{{version}}","connectedAtUtc":"{{DateTime.UtcNow:O}}"}""");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo escribir la marca de salud");
        }
    }
}
