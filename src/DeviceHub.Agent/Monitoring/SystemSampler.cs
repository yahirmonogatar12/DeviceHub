using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace DeviceHub.Agent.Monitoring;

/// <summary>
/// Muestreo del sistema cada 5 s.
///
/// CPU y memoria van por P/Invoke y no por PerformanceCounter ni WMI: esto corre
/// cada 5 segundos durante meses. PerformanceCounter tarda segundos en la primera
/// lectura y en PCs con los contadores corruptos -- cosa nada rara en planta --
/// simplemente falla; WMI cuesta cientos de milisegundos por consulta. Las dos
/// llamadas nativas cuestan microsegundos.
/// </summary>
public sealed class SystemSampler
{
    private long _prevIdle, _prevKernel, _prevUser;
    private long _prevRx, _prevTx;
    private DateTime _prevAtUtc;

    public SystemSampler()
    {
        // Se ceban los contadores en el constructor para que la primera muestra
        // real, 5 s despues, ya tenga un delta valido en vez de un 0% falso.
        ReadCpuTimes(out _prevIdle, out _prevKernel, out _prevUser);
        (_prevRx, _prevTx) = ReadNetworkTotals();
        _prevAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Porcentaje de CPU ocupada a partir de los deltas de GetSystemTimes.
    ///
    /// El tiempo de kernel YA INCLUYE el idle: el total es kernel + user, y lo
    /// ocupado es ese total menos idle. Restar idle del total es el error clasico
    /// aqui, y da porcentajes inflados.
    /// </summary>
    public static double ComputeCpuPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        var total = kernelDelta + userDelta;

        if (total <= 0)
            return 0;

        var busy = total - idleDelta;
        return Math.Clamp(busy * 100d / total, 0, 100);
    }

    public RawSample Sample()
    {
        var now = DateTime.UtcNow;

        ReadCpuTimes(out var idle, out var kernel, out var user);
        var cpu = ComputeCpuPercent(idle - _prevIdle, kernel - _prevKernel, user - _prevUser);
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);

        var (rx, tx) = ReadNetworkTotals();
        var elapsed = Math.Max((now - _prevAtUtc).TotalSeconds, 0.001);
        var rxRate = (long)Math.Max(0, (rx - _prevRx) / elapsed);
        var txRate = (long)Math.Max(0, (tx - _prevTx) / elapsed);
        (_prevRx, _prevTx, _prevAtUtc) = (rx, tx, now);

        return new RawSample(cpu, ReadMemoryPercent(), ReadMinimumFreeDiskPercent(), rxRate, txRate);
    }

    /// <summary>El disco mas apretado. Un C: al 2% no debe quedar escondido tras un D: al 90%.</summary>
    private static double ReadMinimumFreeDiskPercent()
    {
        var minimum = 100d;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                    continue;

                minimum = Math.Min(minimum, drive.TotalFreeSpace * 100d / drive.TotalSize);
            }
            catch (IOException)
            {
                // Unidad que desaparecio entre GetDrives y la lectura.
            }
        }

        return minimum;
    }

    private static (long Rx, long Tx) ReadNetworkTotals()
    {
        long rx = 0, tx = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                var statistics = nic.GetIPv4Statistics();
                rx += statistics.BytesReceived;
                tx += statistics.BytesSent;
            }
            catch (NetworkInformationException)
            {
                // Adaptador que se deshabilito mientras se recorria la lista.
            }
        }

        return (rx, tx);
    }

    private static void ReadCpuTimes(out long idle, out long kernel, out long user)
    {
        if (!GetSystemTimes(out idle, out kernel, out user))
            idle = kernel = user = 0;
    }

    private static double ReadMemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    // DllImport y no LibraryImport a proposito: el generador de LibraryImport
    // exige <AllowUnsafeBlocks> en TODO el proyecto, y no vale ampliar esa
    // superficie por dos firmas completamente blittables.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad; // ya viene en 0-100
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
