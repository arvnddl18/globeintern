namespace SlotAd_Globe.Models;

/// <summary>Serializable filter snapshot stored next to the uploaded CSV.</summary>
public class ReportSessionData
{
    public DateTime CreatedUtc { get; set; }

    /// <summary>Detected at upload; preserved when saving filters.</summary>
    public CsvSourceKind CsvSourceKind { get; set; } = CsvSourceKind.Pending;

    /// <summary>Cached from ExtractFilterOptionsAsync to avoid re-scanning CSV on dashboard.</summary>
    public List<string>? CachedAvailableDates { get; set; }
    public List<string>? CachedAvailableTerritories { get; set; }
    public List<string>? CachedAvailableStatuses { get; set; }
    public List<string>? CachedAvailableSubStatuses { get; set; }
    public List<string>? CachedAvailableSkillsets { get; set; }
    public List<string>? CachedAvailableOrderCreateDates { get; set; }

    public string DateFilterMode { get; set; } = "all";
    public string? SelectedDate { get; set; }
    public string? DateRangeStart { get; set; }
    public string? DateRangeEnd { get; set; }

    public List<string> SelectedTerritories { get; set; } = [];
    public List<string> SelectedStatuses { get; set; } = [];
    public List<string> SelectedSubStatuses { get; set; } = [];
    public List<string> SelectedSkillsets { get; set; } = [];
    public List<string> SelectedOrderCreateDates { get; set; } = [];

    /// <summary>Per-tab saved filter snapshots for dashboard tab switching.</summary>
    public bool HasPendingFilters { get; set; }
    public string PendingDateFilterMode { get; set; } = "all";
    public string? PendingSelectedDate { get; set; }
    public string? PendingDateRangeStart { get; set; }
    public string? PendingDateRangeEnd { get; set; }
    public List<string> PendingSelectedTerritories { get; set; } = [];
    public List<string> PendingSelectedStatuses { get; set; } = [];
    public List<string> PendingSelectedSubStatuses { get; set; } = [];
    public List<string> PendingSelectedSkillsets { get; set; } = [];
    public List<string> PendingSelectedOrderCreateDates { get; set; } = [];

    public bool HasStatusFilters { get; set; }
    public string StatusDateFilterMode { get; set; } = "all";
    public string? StatusSelectedDate { get; set; }
    public string? StatusDateRangeStart { get; set; }
    public string? StatusDateRangeEnd { get; set; }
    public List<string> StatusSelectedTerritories { get; set; } = [];
    public List<string> StatusSelectedStatuses { get; set; } = [];
    public List<string> StatusSelectedSubStatuses { get; set; } = [];
    public List<string> StatusSelectedSkillsets { get; set; } = [];
    public List<string> StatusSelectedOrderCreateDates { get; set; } = [];

    // Operational dashboard report state (10-minute traffic analysis).
    public OperationalReportKind OperationalReportKind { get; set; } = OperationalReportKind.Unknown;
    public string? OperationalSelectedPerformanceGroup { get; set; }
    public string OperationalAlarmDateFilterMode { get; set; } = "all";
    public string OperationalAlarmPeriodFilter { get; set; } = "1hour";
    public string? OperationalAlarmSelectedDate { get; set; }
    public string? OperationalAlarmDateRangeStart { get; set; }
    public string? OperationalAlarmDateRangeEnd { get; set; }
    public string OperationalPerformanceDateFilterMode { get; set; } = "all";
    public string OperationalPerformancePeriodFilter { get; set; } = "1hour";
    public string? OperationalPerformanceSelectedDate { get; set; }
    public string? OperationalPerformanceDateRangeStart { get; set; }
    public string? OperationalPerformanceDateRangeEnd { get; set; }
}
