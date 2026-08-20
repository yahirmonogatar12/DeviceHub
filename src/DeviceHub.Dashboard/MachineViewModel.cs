using CommunityToolkit.Mvvm.ComponentModel;
using DeviceHub.Contracts;

namespace DeviceHub.Dashboard;

public sealed class MachineViewModel(MachineSummary summary) : ObservableObject
{
    private MachineSummary _summary = summary;

    public MachineSummary Summary => _summary;

    public string MachineId => _summary.MachineId;
    public string SiteCode => _summary.SiteCode;
    public string MachineCode => _summary.MachineCode;
    public string DisplayName => _summary.DisplayName;
    public string Hostname => _summary.Hostname;
    public string Area => _summary.Area;
    public string Line => _summary.Line;
    public string Station => _summary.Station;
    public string CurrentIp => _summary.CurrentIp;
    public string PrimaryMac => _summary.PrimaryMac;
    public string LoggedUser => _summary.LoggedUser;
    public string AgentVersion => _summary.AgentVersion;

    public bool HasConflict => _summary.IdentityState == IdentityState.Conflict;

    /// <summary>
    /// El servidor ya resolvio el conflicto; esta fila todavia no lo sabe.
    ///
    /// La lista se entera por el stream de WatchMachines, y ese solo trae
    /// novedades cuando el agente vuelve a hablar -- lo que con una PC apagada
    /// puede tardar horas. Sin esto el aviso rojo se queda en pantalla despues de
    /// pulsar el boton, y parece que el boton no hizo nada.
    /// </summary>
    public void ConflictoResuelto()
    {
        if (!HasConflict)
            return;

        _summary.IdentityState = IdentityState.Ok;
        OnPropertyChanged(nameof(HasConflict));
    }

    /// <summary>Si se puede controlar. Con que motor y con que ID es cosa del
    /// servidor: al tecnico le basta el boton.</summary>
    public bool RemoteAvailable => _summary.RemoteAvailable;

    /// <summary>
    /// Se pregunta por MetricsAt y no por el valor: un 0% de CPU es una lectura
    /// legitima de una maquina inactiva, no ausencia de datos.
    /// </summary>
    public bool HasMetrics => _summary.MetricsAt is not null;

    public string Cpu => HasMetrics ? $"{_summary.CpuPercent:0}%" : "-";
    public string Memory => HasMetrics ? $"{_summary.MemoryPercent:0}%" : "-";
    public string DiskFree => HasMetrics ? $"{_summary.DiskFreePercent:0}%" : "-";

    /// <summary>Recalculado en el cliente, no el que empujo el servidor.</summary>
    public MachineStatus Status => StatusCalculator.Compute(LastSeenUtc, DateTime.UtcNow);

    public string StatusText => Status switch
    {
        MachineStatus.Online => "ONLINE",
        MachineStatus.Unreachable => "UNREACHABLE",
        _ => "OFFLINE"
    };

    public DateTime? LastSeenUtc => _summary.LastSeen?.ToDateTime();

    /// <summary>Unico punto donde se convierte a hora local (regla global: todo en UTC).</summary>
    public string LastSeenLocal => LastSeenUtc is null
        ? "-"
        : DateTime.SpecifyKind(LastSeenUtc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string LastSeenAgo
    {
        get
        {
            if (LastSeenUtc is null)
                return "nunca";

            var age = DateTime.UtcNow - DateTime.SpecifyKind(LastSeenUtc.Value, DateTimeKind.Utc);
            return age < TimeSpan.FromMinutes(1)
                ? $"hace {Math.Max(0, (int)age.TotalSeconds)} s"
                : $"hace {FormatSpan(age)}";
        }
    }

    public string Uptime => _summary.UptimeSeconds <= 0
        ? "-"
        : FormatSpan(TimeSpan.FromSeconds(_summary.UptimeSeconds));

    public void Update(MachineSummary summary)
    {
        _summary = summary;
        RefreshAll();
    }

    /// <summary>Lo llama el timer de la UI: el paso del tiempo cambia el estado
    /// aunque no llegue ningun mensaje.</summary>
    public void RefreshDerived()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastSeenAgo));
    }

    private void RefreshAll() => OnPropertyChanged(string.Empty);

    private static string FormatSpan(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m";
}
