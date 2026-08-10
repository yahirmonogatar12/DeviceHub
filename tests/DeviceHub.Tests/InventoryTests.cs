using DeviceHub.Agent.Inventory;
using DeviceHub.Contracts;
using Xunit;

namespace DeviceHub.Tests;

public class InventoryCadenceTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
    private const string Hash = "abc123";

    [Fact]
    public void First_run_always_sends()
        => Assert.True(InventoryCadence.ShouldSend(Hash, null, null, Now));

    /// <summary>
    /// El caso que justifica todo: el hardware no cambio y no toca reenvio, asi
    /// que el inventario NO viaja. Si viajara en cada latido serian 2.880 envios
    /// diarios por PC de datos identicos.
    /// </summary>
    [Fact]
    public void Unchanged_hardware_within_the_window_sends_nothing()
        => Assert.False(InventoryCadence.ShouldSend(Hash, Hash, Now.AddHours(-1), Now));

    [Fact]
    public void Changed_hardware_sends_immediately()
        => Assert.True(InventoryCadence.ShouldSend("nuevo", Hash, Now.AddMinutes(-1), Now));

    [Theory]
    [InlineData(11.9, false)]
    [InlineData(12.0, true)]
    [InlineData(48.0, true)]
    public void Periodic_resend_after_twelve_hours(double hoursAgo, bool expected)
        => Assert.Equal(expected, InventoryCadence.ShouldSend(Hash, Hash, Now.AddHours(-hoursAgo), Now));

    [Fact]
    public void Unspecified_kind_is_treated_as_utc()
    {
        // El valor viene de machine.json deserializado: puede llegar sin Kind.
        var unspecified = DateTime.SpecifyKind(Now.AddHours(-1), DateTimeKind.Unspecified);
        Assert.False(InventoryCadence.ShouldSend(Hash, Hash, unspecified, Now));
    }
}

public class HardwareHashTests
{
    private static HardwareInventory Sample()
    {
        var inventory = new HardwareInventory
        {
            CpuModel = "Intel Core i5-9500",
            CpuCores = 6,
            CpuThreads = 6,
            TotalMemoryBytes = 17_179_869_184,
            GpuModel = "Intel UHD 630",
            Motherboard = "ASUS PRIME",
            BiosVersion = "AMI 1.20",
            BiosSerial = "PF2K9L3M",
            OsCaption = "Windows 11 Pro",
            OsVersion = "10.0.26200",
            OsBuild = "26200"
        };

        inventory.Disks.Add(new DiskInfo { Model = "Samsung SSD 870", SizeBytes = 500_107_862_016 });
        return inventory;
    }

    [Fact]
    public void Same_hardware_gives_the_same_hash()
        => Assert.Equal(HardwareCollector.ComputeHash(Sample()), HardwareCollector.ComputeHash(Sample()));

    [Fact]
    public void More_ram_changes_the_hash()
    {
        var upgraded = Sample();
        upgraded.TotalMemoryBytes = 34_359_738_368;

        Assert.NotEqual(HardwareCollector.ComputeHash(Sample()), HardwareCollector.ComputeHash(upgraded));
    }

    [Fact]
    public void A_new_disk_changes_the_hash()
    {
        var expanded = Sample();
        expanded.Disks.Add(new DiskInfo { Model = "WD Blue", SizeBytes = 1_000_204_886_016 });

        Assert.NotEqual(HardwareCollector.ComputeHash(Sample()), HardwareCollector.ComputeHash(expanded));
    }

    [Fact]
    public void A_windows_update_changes_the_hash()
    {
        var patched = Sample();
        patched.OsBuild = "26300";

        Assert.NotEqual(HardwareCollector.ComputeHash(Sample()), HardwareCollector.ComputeHash(patched));
    }

    /// <summary>
    /// El campo Hash no entra en su propio calculo: si entrara, guardar el
    /// resultado cambiaria el hash y el inventario se enviaria para siempre.
    /// </summary>
    [Fact]
    public void The_hash_field_itself_is_not_part_of_the_hash()
    {
        var inventory = Sample();
        var first = HardwareCollector.ComputeHash(inventory);

        inventory.Hash = first;

        Assert.Equal(first, HardwareCollector.ComputeHash(inventory));
    }
}
