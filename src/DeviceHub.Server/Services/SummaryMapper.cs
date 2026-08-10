using DeviceHub.Contracts;
using DeviceHub.Server.Data;
using Google.Protobuf.WellKnownTypes;

namespace DeviceHub.Server.Services;

public static class SummaryMapper
{
    public static MachineSummary ToSummary(MachineRow row, DateTime nowUtc)
    {
        var summary = new MachineSummary
        {
            MachineId = row.Id,
            SiteCode = row.SiteCode,
            MachineCode = row.MachineCode,
            DisplayName = row.DisplayName ?? string.Empty,
            Hostname = row.Hostname ?? string.Empty,
            Area = row.Area ?? string.Empty,
            Line = row.Line ?? string.Empty,
            Station = row.Station ?? string.Empty,
            CurrentIp = row.CurrentIp ?? string.Empty,
            PrimaryMac = row.PrimaryMac ?? string.Empty,
            LoggedUser = row.LoggedUser ?? string.Empty,
            AgentVersion = row.AgentVersion ?? string.Empty,
            UptimeSeconds = row.UptimeSeconds ?? 0,
            // Derivado, nunca leido de una columna.
            Status = StatusCalculator.Compute(row.LastSeen, nowUtc),
            IdentityState = Map.Identity(row.IdentityState),
            CpuPercent = row.CpuPercent ?? 0,
            MemoryPercent = row.MemoryPercent ?? 0,
            DiskFreePercent = row.DiskFreePercent ?? 0,
            RemoteAvailable = row.RemoteAvailable
        };

        if (row.LastSeen is not null)
            summary.LastSeen = Timestamp.FromDateTime(Db.AsUtc(row.LastSeen.Value));

        if (row.MetricsAt is not null)
            summary.MetricsAt = Timestamp.FromDateTime(Db.AsUtc(row.MetricsAt.Value));

        return summary;
    }

    public static HardwareInventory ToProto(HardwareRow row)
    {
        var inventory = new HardwareInventory
        {
            Hash = row.Hash,
            CpuModel = row.CpuModel ?? string.Empty,
            CpuCores = row.CpuCores ?? 0,
            CpuThreads = row.CpuThreads ?? 0,
            TotalMemoryBytes = row.TotalMemoryBytes ?? 0,
            GpuModel = row.GpuModel ?? string.Empty,
            Motherboard = row.Motherboard ?? string.Empty,
            BiosVersion = row.BiosVersion ?? string.Empty,
            BiosSerial = row.BiosSerial ?? string.Empty,
            OsCaption = row.OsCaption ?? string.Empty,
            OsVersion = row.OsVersion ?? string.Empty,
            OsBuild = row.OsBuild ?? string.Empty
        };

        inventory.Disks.AddRange(DiskJson.Deserialize(row.Disks));
        return inventory;
    }

    public static CommandEntry ToProto(CommandRow row)
    {
        var entry = new CommandEntry
        {
            CommandId = row.Id,
            MachineId = row.MachineId,
            Type = System.Enum.TryParse<CommandType>(row.CommandType, out var type) ? type : CommandType.Unspecified,
            Status = System.Enum.TryParse<CommandStatus>(row.Status, ignoreCase: true, out var status) ? status : CommandStatus.Unspecified,
            RequestedBy = row.RequestedBy,
            RequestedAt = Timestamp.FromDateTime(Db.AsUtc(row.RequestedAt)),
            ExpiresAt = Timestamp.FromDateTime(Db.AsUtc(row.ExpiresAt)),
            Result = row.Result ?? string.Empty,
            ErrorCode = row.ErrorCode ?? string.Empty
        };

        if (row.CompletedAt is not null)
            entry.CompletedAt = Timestamp.FromDateTime(Db.AsUtc(row.CompletedAt.Value));

        return entry;
    }

    /// <summary>Lo que baja al agente por el stream.</summary>
    public static CommandRequest ToRequest(CommandRow row)
    {
        var request = new CommandRequest
        {
            CommandId = row.Id,
            Type = System.Enum.TryParse<CommandType>(row.CommandType, out var type) ? type : CommandType.Unspecified,
            ExpiresAt = Timestamp.FromDateTime(Db.AsUtc(row.ExpiresAt))
        };

        foreach (var (key, value) in CommandRepository.ParseParameters(row.ParametersJson))
            request.Parameters[key] = value;

        return request;
    }

    public static IpHistoryEntry ToProto(HistoryRow row)
    {
        var entry = new IpHistoryEntry
        {
            Ip = row.Ip,
            Mac = row.Mac ?? string.Empty,
            ValidFrom = Timestamp.FromDateTime(Db.AsUtc(row.ValidFrom))
        };

        if (row.ValidTo is not null)
            entry.ValidTo = Timestamp.FromDateTime(Db.AsUtc(row.ValidTo.Value));

        return entry;
    }

    public static PlacementHistoryEntry ToProto(PlacementRow row)
    {
        var entry = new PlacementHistoryEntry
        {
            SiteCode = row.SiteCode,
            MachineCode = row.MachineCode,
            Area = row.Area ?? string.Empty,
            Line = row.Line ?? string.Empty,
            Station = row.Station ?? string.Empty,
            ChangedBy = row.ChangedBy,
            ValidFrom = Timestamp.FromDateTime(Db.AsUtc(row.ValidFrom))
        };

        if (row.ValidTo is not null)
            entry.ValidTo = Timestamp.FromDateTime(Db.AsUtc(row.ValidTo.Value));

        return entry;
    }
}
