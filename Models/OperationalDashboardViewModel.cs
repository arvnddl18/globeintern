namespace SlotAd_Globe.Models;

public class OperationalDashboardViewModel
{
    public bool HasReport => AlarmReport.HasReport || PerformanceReport.HasReport;
    public bool IsFirstVisit { get; set; } = true;
    public string LatestAlarmToken { get; set; } = string.Empty;
    public string LatestPerformanceToken { get; set; } = string.Empty;
    public OperationalReportPanelViewModel AlarmReport { get; set; } = new();
    public OperationalReportPanelViewModel PerformanceReport { get; set; } = new();

    /// <summary>KPI-style appointment aging panel; null when no CSV or computation failed.</summary>
    public OperationAgingViewModel? OperationAging { get; set; }
}

public class OperationalReportPanelViewModel
{
    public bool HasReport { get; set; }
    public string ReportToken { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public OperationalReportKind ReportKind { get; set; } = OperationalReportKind.Unknown;
    public string SelectedPerformanceGroup { get; set; } = string.Empty;
    public string SelectedPeriod { get; set; } = "1hour";
    public string DateFilterMode { get; set; } = "all";
    public string? SelectedDate { get; set; }
    public string? DateRangeStart { get; set; }
    public string? DateRangeEnd { get; set; }
    public List<string> AvailablePerformanceGroups { get; set; } = [];
    public string PrimaryMetricLabel { get; set; } = "Performance Value";
    public List<string> IntervalLabels { get; set; } = [];
    public List<double> IntervalValues { get; set; } = [];
    public string SecondaryMetricLabel { get; set; } = string.Empty;
    public List<string> SecondaryIntervalLabels { get; set; } = [];
    public List<double> SecondaryIntervalValues { get; set; } = [];
}
