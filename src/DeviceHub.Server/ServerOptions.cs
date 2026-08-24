namespace DeviceHub.Server;

public sealed class ServerOptions
{
    public const string SectionName = "DeviceHub";

    public int Port { get; set; } = 5443;

    public string DataDirectory { get; set; } = @"C:\ProgramData\ILSANSYSTEM\DeviceHubServer";

    /// <summary>
    /// Se puede sobreescribir con la variable de entorno DEVICEHUB_DB_CONNECTION
    /// para no dejar credenciales en un archivo versionado.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string DefaultSiteCode { get; set; } = "ILSAN-MTY";

    public string JwtIssuer { get; set; } = "devicehub";

    public int JwtHours { get; set; } = 12;

    /// <summary>Dias de metricas por minuto que se conservan (Fase 6).</summary>
    public int MetricsRetentionDays { get; set; } = 30;

    /// <summary>
    /// Carpeta con los anillos de despliegue del agente. Vacio = no se sirven.
    ///
    /// Es la MISMA carpeta que publish-update.ps1 escribe por SMB, leida aqui en
    /// local. El recurso compartido sigue siendo para quien publica; los agentes
    /// ya no lo necesitan.
    /// </summary>
    public string UpdatesPath { get; set; } = string.Empty;
}
