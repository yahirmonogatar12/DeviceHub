using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeviceHub.Agent.Network;

public sealed record NicInfo(string Name, string Ip, string Mac, bool IsPrimary);

public static class NetworkInfo
{
    /// <summary>
    /// Cual es la IP que Windows usaria para hablar con el servidor.
    ///
    /// Reemplaza la lista negra de Hyper-V / VMware / VirtualBox / Tailscale por
    /// una pregunta a la tabla de rutas del propio sistema: siempre correcta y
    /// sin mantenimiento cuando aparezca el proximo adaptador virtual.
    ///
    /// UDP Connect no envia un solo byte, solo fija la ruta local.
    /// </summary>
    public static IPAddress? ResolvePrimaryIp(string serverHost)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(serverHost, 9); // discard port
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Todas las interfaces activas. Se reportan TODAS -- solo se marca cual es
    /// la primaria; el servidor guarda el conjunto completo.
    /// </summary>
    public static IReadOnlyList<NicInfo> Collect(string serverHost)
    {
        var primaryIp = ResolvePrimaryIp(serverHost)?.ToString();
        var result = new List<NicInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var mac = FormatMac(nic.GetPhysicalAddress());

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var ip = addr.Address.ToString();
                result.Add(new NicInfo(nic.Name, ip, mac, ip == primaryIp));
            }
        }

        return result;
    }

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? string.Empty : string.Join('-', bytes.Select(b => b.ToString("X2")));
    }

    public static long UptimeSeconds() => (long)(Environment.TickCount64 / 1000);
}
