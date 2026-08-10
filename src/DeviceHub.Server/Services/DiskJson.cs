using System.Text.Json;
using DeviceHub.Contracts;

namespace DeviceHub.Server.Services;

/// <summary>
/// Los discos viajan a MySQL como columna JSON: es una lista que se lee entera o
/// no se lee, y una tabla hija seria una junta mas para cero consultas relacionales.
/// </summary>
public static class DiskJson
{
    private sealed record Disk(string Model, long SizeBytes);

    public static string Serialize(HardwareInventory inventory)
        => JsonSerializer.Serialize(inventory.Disks.Select(d => new Disk(d.Model, d.SizeBytes)));

    public static IEnumerable<DiskInfo> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Disk>>(json)?
                .Select(d => new DiskInfo { Model = d.Model, SizeBytes = d.SizeBytes }) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
