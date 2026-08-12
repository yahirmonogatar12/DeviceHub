using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceHub.Remote.Contracts;

/// <summary>
/// Lo que el agente le entrega al host recien lanzado por el named pipe.
///
/// POR QUE UN PIPE Y NO ARGUMENTOS. Los argumentos de un proceso los lee
/// cualquier usuario de esa maquina con abrir el administrador de tareas, y el
/// ticket es la credencial que da acceso a ver la pantalla. Por el pipe -- con
/// la ACL restringida al SID del usuario al que se lanzo -- no los ve nadie mas.
/// Lo unico que viaja por linea de comandos es el NOMBRE del pipe, que no abre
/// nada por si solo.
///
/// El pipe se queda abierto despues del saludo: es el canal de control por el
/// que el agente ordena STOP y el host reporta como le va.
/// </summary>
public sealed record RemoteHostHandshake
{
    public required string SessionId { get; init; }

    /// <summary>Secreto de un solo uso. No se escribe en disco, ni en un log, ni
    /// en <see cref="ToString"/>.</summary>
    public required string Ticket { get; init; }

    public required string ServerAddress { get; init; }

    /// <summary>El machine_id de DeviceHub, no el hostname de Windows: el ticket
    /// se ato a ese y son cosas distintas.</summary>
    public required string MachineId { get; init; }

    /// <summary>
    /// Pines SPKI del agente, tal cual estan en machine.json. El host valida el
    /// certificado del relay contra ellos en vez de confiar en la cadena de CA,
    /// que para un certificado autofirmado no dice nada.
    ///
    /// Vacio = validacion normal de TLS.
    /// </summary>
    public IReadOnlyList<string> PinnedKeys { get; init; } = [];

    /// <summary>Escotilla de laboratorio: no valida el certificado. Solo la
    /// enciende quien edite el appsettings del agente a mano.</summary>
    public bool AllowUntrusted { get; init; }

    public string ToLine() => JsonSerializer.Serialize(this, Opciones);

    public static RemoteHostHandshake? Parse(string? linea)
    {
        if (string.IsNullOrWhiteSpace(linea))
            return null;

        try
        {
            var saludo = JsonSerializer.Deserialize<RemoteHostHandshake>(linea, Opciones);

            return string.IsNullOrWhiteSpace(saludo?.SessionId)
                || string.IsNullOrWhiteSpace(saludo.Ticket)
                || string.IsNullOrWhiteSpace(saludo.ServerAddress)
                || string.IsNullOrWhiteSpace(saludo.MachineId)
                ? null
                : saludo;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sin esto el ToString() que genera el compilador para un record imprime
    /// TODAS las propiedades, ticket incluido. Basta un LogDebug descuidado en
    /// cualquier punto futuro para dejar la credencial en el visor de eventos.
    /// </summary>
    public override string ToString()
        => $"sesion {SessionId} maquina {MachineId} servidor {ServerAddress} ticket [oculto]";

    private static readonly JsonSerializerOptions Opciones = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Las palabras del canal de control, una por linea en UTF-8. Texto plano a
/// proposito: lo que viaja despues del saludo no es secreto y asi el pipe se
/// puede leer con cualquier cosa cuando haya que diagnosticar.
/// </summary>
public static class RemoteHostPipe
{
    /// <summary>Agente -> host: termina ya.</summary>
    public const string Stop = "STOP";

    /// <summary>Host -> agente: el saludo llego y la captura arranco.</summary>
    public const string Ready = "READY";

    /// <summary>Host -> agente: una linea de estado para el log. Nunca lleva
    /// secretos.</summary>
    public const string Status = "STATUS";

    /// <summary>Host -> agente: termino, con su codigo de salida.</summary>
    public const string Ended = "ENDED";
}
