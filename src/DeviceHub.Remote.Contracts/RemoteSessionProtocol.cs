namespace DeviceHub.Remote.Contracts;

/// <summary>
/// Version del protocolo y limites duros de tamano.
///
/// Estan aqui, y no dentro del relay o del viewer, porque los tienen que aplicar
/// los tres extremos: quien trocea, quien reenvia y quien reensambla. Un limite
/// que solo comprueba uno de los tres no es un limite.
/// </summary>
public static class RemoteSessionProtocol
{
    /// <summary>
    /// Se versiona desde el primer commit. El paquete del .proto es
    /// `devicehub.remote.v1` y esto viaja ademas en cada sobre: cambiar el
    /// formato sin poder detectarlo deja al viewer interpretando basura.
    /// </summary>
    public const uint Version = 1;

    /// <summary>
    /// 64 KiB por mensaje.
    ///
    /// El umbral del Large Object Heap son 85 000 bytes: por encima de eso cada
    /// chunk se reserva en el LOH, que no se compacta, y a 30 fps eso es
    /// fragmentacion continua en un proceso que tiene que durar un turno entero.
    /// </summary>
    public const int MaxChunkBytes = 64 * 1024;

    /// <summary>
    /// Techo del frame reensamblado. Un IDR de 1080p con movimiento ronda 1-2 MB;
    /// 8 MB deja margen de sobra sin dejar la puerta abierta.
    /// </summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Se comprueba ANTES de reservar el buffer de reensamblado. Sin esto, un
    /// `chunk_count` inventado es una peticion de memoria arbitraria: el emisor
    /// elige cuanta RAM reserva el receptor.
    /// </summary>
    public const int MaxChunksPerFrame = MaxFrameBytes / MaxChunkBytes;

    /// <summary>
    /// El ticket es de 256 bits en hexadecimal o base64; 256 caracteres sobra.
    ///
    /// Se comprueba aunque TODAVIA NO SE VALIDE el contenido -- eso llega en la
    /// Fase 6. Sin limite, el primer mensaje de una conexion sin autenticar
    /// permitiria mandar megabytes al servidor antes de que nadie mire quien es.
    /// </summary>
    public const int MaxTicketChars = 256;

    /// <summary>Un identificador de sesion es un GUID o similar.</summary>
    public const int MaxSessionIdChars = 128;

    /// <summary>Texto de cierre y de error. Va a la auditoria, no al cable en
    /// bucle.</summary>
    public const int MaxDetailChars = 512;
}
