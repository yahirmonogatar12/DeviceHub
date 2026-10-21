using System.Net;
using Grpc.Core;

namespace DeviceHub.Server.Security;

/// <summary>
/// La IP de quien llama, SIN el puerto.
///
/// `ServerCallContext.Peer` devuelve "ipv4:192.168.1.50:54321", con el puerto
/// efimero de esa conexion. Usarlo como clave del limite de login era no tener
/// limite: cada canal nuevo es otro puerto y otra clave, asi que quien abra una
/// conexion por intento no llega nunca a los cinco fallos. Y en la auditoria
/// la columna source_ip -- VARCHAR(45) -- recibia la cadena entera, que no se
/// puede agrupar por origen y que en IPv6 ni siquiera cabe.
/// </summary>
public static class Origen
{
    public static string? Ip(ServerCallContext context)
    {
        try
        {
            var ip = context.GetHttpContext().Connection.RemoteIpAddress;

            if (ip is not null)
                return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        }
        catch (InvalidOperationException)
        {
            // Sin HttpContext -- un host de pruebas --: se recorta el Peer.
        }

        return DePeer(context.Peer);
    }

    /// <summary>
    /// "ipv4:1.2.3.4:5678" -> "1.2.3.4", "ipv6:[::1]:5678" -> "::1". Lo que no
    /// tenga esa forma se devuelve tal cual: mejor un origen raro en la
    /// auditoria que ninguno.
    /// </summary>
    public static string? DePeer(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer))
            return null;

        var resto = peer;

        if (resto.StartsWith("ipv4:", StringComparison.OrdinalIgnoreCase)
            || resto.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase))
        {
            resto = resto[5..];
        }

        if (resto.StartsWith('['))
        {
            var cierre = resto.IndexOf(']');

            if (cierre > 1)
                return resto[1..cierre];
        }

        // Puerto solo con UN separador: con varios es una IPv6 sin corchetes, y
        // cortar por el ultimo se comeria un trozo de la direccion.
        var dosPuntos = resto.IndexOf(':');

        if (dosPuntos > 0 && dosPuntos == resto.LastIndexOf(':')
            && IPAddress.TryParse(resto[..dosPuntos], out var ip))
        {
            return ip.ToString();
        }

        return IPAddress.TryParse(resto, out ip) ? ip.ToString() : peer;
    }
}
