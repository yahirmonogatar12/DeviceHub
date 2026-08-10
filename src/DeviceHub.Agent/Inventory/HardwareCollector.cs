using System.Management;
using System.Security.Cryptography;
using System.Text;
using DeviceHub.Contracts;

namespace DeviceHub.Agent.Inventory;

public static class HardwareCollector
{
    public static HardwareInventory Collect()
    {
        var inventory = new HardwareInventory();

        // Suma entre sockets: en una PC de planta hay uno, pero sumar es igual de
        // barato que asumirlo.
        ForEach("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", cpu =>
        {
            inventory.CpuModel = Text(cpu["Name"]);
            inventory.CpuCores += (int)Number(cpu["NumberOfCores"]);
            inventory.CpuThreads += (int)Number(cpu["NumberOfLogicalProcessors"]);
        });

        ForEach("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", system =>
            inventory.TotalMemoryBytes = Number(system["TotalPhysicalMemory"]));

        var gpus = new List<string>();
        ForEach("SELECT Name FROM Win32_VideoController", gpu =>
        {
            var name = Text(gpu["Name"]);
            if (name.Length > 0)
                gpus.Add(name);
        });

        // Orden estable, igual que los discos: WMI no garantiza el mismo orden
        // entre consultas y eso solo moveria el hash sin que cambie nada real.
        //
        // ponytail: los adaptadores de video VIRTUALES (SuperDisplay, DisplayLink,
        // el display virtual de RustDesk) si generan un HARDWARE_CHANGED legitimo
        // pero ruidoso al cargarse o descargarse. No se filtran por nombre porque
        // seria la misma lista negra que se evito para las NIC. Revisar si la
        // Fase 10 instala un display virtual en todas las PCs.
        gpus.Sort(StringComparer.Ordinal);
        inventory.GpuModel = string.Join(", ", gpus);

        ForEach("SELECT Manufacturer, Product FROM Win32_BaseBoard", board =>
            inventory.Motherboard = $"{Text(board["Manufacturer"])} {Text(board["Product"])}".Trim());

        ForEach("SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber FROM Win32_BIOS", bios =>
        {
            inventory.BiosVersion = $"{Text(bios["Manufacturer"])} {Text(bios["SMBIOSBIOSVersion"])}".Trim();
            inventory.BiosSerial = Text(bios["SerialNumber"]);
        });

        ForEach("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem", os =>
        {
            inventory.OsCaption = Text(os["Caption"]);
            inventory.OsVersion = Text(os["Version"]);
            inventory.OsBuild = Text(os["BuildNumber"]);
        });

        CollectDisks(inventory);

        inventory.Hash = ComputeHash(inventory);
        return inventory;
    }

    private static void CollectDisks(HardwareInventory inventory)
    {
        var disks = new List<DiskInfo>();

        ForEach("SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive", disk =>
        {
            // Se excluyen USB y medios removibles a proposito: en planta las
            // memorias entran y salen todo el dia, y si contaran, cada una
            // cambiaria el hash y generaria un HARDWARE_CHANGED falso.
            var interfaceType = Text(disk["InterfaceType"]);
            var mediaType = Text(disk["MediaType"]);

            if (interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase))
                return;

            if (mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase))
                return;

            disks.Add(new DiskInfo { Model = Text(disk["Model"]), SizeBytes = Number(disk["Size"]) });
        });

        // Orden estable: WMI no garantiza el mismo orden entre consultas y eso
        // solo cambiaria el hash sin que cambie nada real.
        inventory.Disks.AddRange(disks.OrderBy(d => d.Model, StringComparer.Ordinal).ThenBy(d => d.SizeBytes));
    }

    /// <summary>
    /// Hash del contenido, sin incluir el propio campo Hash. Es lo que decide si
    /// hay algo nuevo que enviar. Pura y determinista: es la parte que se testea.
    /// </summary>
    public static string ComputeHash(HardwareInventory inventory)
    {
        var builder = new StringBuilder()
            .Append(inventory.CpuModel).Append('|')
            .Append(inventory.CpuCores).Append('|')
            .Append(inventory.CpuThreads).Append('|')
            .Append(inventory.TotalMemoryBytes).Append('|')
            .Append(inventory.GpuModel).Append('|')
            .Append(inventory.Motherboard).Append('|')
            .Append(inventory.BiosVersion).Append('|')
            .Append(inventory.BiosSerial).Append('|')
            .Append(inventory.OsCaption).Append('|')
            .Append(inventory.OsVersion).Append('|')
            .Append(inventory.OsBuild);

        foreach (var disk in inventory.Disks)
            builder.Append('|').Append(disk.Model).Append(':').Append(disk.SizeBytes);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void ForEach(string query, Action<ManagementBaseObject> action)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var item in searcher.Get())
            {
                using (item)
                    action(item);
            }
        }
        catch
        {
            // Un WMI roto no debe tumbar el agente ni cortar el heartbeat: el
            // inventario sale incompleto y ya. La maquina sigue visible.
        }
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;

    private static long Number(object? value) => value is null ? 0L : Convert.ToInt64(value);
}
