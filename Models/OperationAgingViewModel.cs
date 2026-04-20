namespace SlotAd_Globe.Models;

public class OperationAgingViewModel
{
    public string ReportToken { get; set; } = string.Empty;

    public int DetailPage { get; set; } = 1;
    public int DetailPageSize { get; set; } = 20;
    public int DetailTotalPages { get; set; }

    /// <summary>Bucket header labels (fixed order).</summary>
    public List<string> BucketLabels { get; set; } = [];

    /// <summary>Matrix rows for the bottom bucket table (e.g., Repair/Install/Other).</summary>
    public List<AgingBucketMatrixRow> BucketMatrixRows { get; set; } = [];

    /// <summary>Repair-only remarks pivot rows by aging bucket.</summary>
    public List<RepairRemarkBucketRow> RepairRemarkRows { get; set; } = [];
    public List<int> RepairRemarkGrandBucketTotals { get; set; } = [];
    public int RepairRemarkGrandTotal { get; set; }

    /// <summary>yyyy-MM for the day-to-day grid (last update date scope).</summary>
    public string SelectedMonth { get; set; } = string.Empty;

    public string SelectedMonthLabel { get; set; } = string.Empty;

    /// <summary>Year part of <see cref="SelectedMonth"/> (for day-to-day filters).</summary>
    public int SelectedDailyYear { get; set; }

    /// <summary>Month part of <see cref="SelectedMonth"/>, 1–12.</summary>
    public int SelectedDailyMonth { get; set; }

    /// <summary>Distinct years present in <see cref="AvailableMonths"/>.</summary>
    public List<int> AvailableDailyYears { get; set; } = [];

    public List<string> AvailableMonths { get; set; } = [];

    /// <summary>Order-create scope for aging KPIs, charts, remarks, and detail.</summary>
    public int SelectedAgingYear { get; set; }

    /// <summary>Reserved; aging scope is always full calendar year of <see cref="SelectedAgingYear"/>.</summary>
    public int? SelectedAgingMonth { get; set; }

    /// <summary>Years with at least one order in the CSV (Davao North, valid order create).</summary>
    public List<int> AvailableOrderScopeYears { get; set; } = [];

    /// <summary>Calendar year for order-create aging scope (e.g. "2026").</summary>
    public string AgingScopeLabel { get; set; } = string.Empty;

    /// <summary>Day column headers (e.g. "1", "2", …) aligned with daily value arrays.</summary>
    public List<string> DailyHeaderLabels { get; set; } = [];

    public List<DailyStatusReportRow> DailyStatusRows { get; set; } = [];

    public int ReadingYearScope { get; set; }
    public string CsvYearSummary { get; set; } = string.Empty;
    public string CsvStartMonthYear { get; set; } = string.Empty;
    public string CsvEndMonthYear { get; set; } = string.Empty;

    /// <summary>Current calendar year, Davao North, valid order create date.</summary>
    public int TotalOrdersYearScope { get; set; }

    public int DelayedCount { get; set; }
    public int PendingCount { get; set; }
    public int OngoingCount { get; set; }
    public int CompletedCount { get; set; }
    public int OtherStatusCount { get; set; }

    /// <summary>Ordered bucket label → count (full year scope).</summary>
    public IReadOnlyList<AgingBucketCount> BucketCounts { get; set; } = [];

    public IReadOnlyList<OperationAgingDetailRow> DetailRows { get; set; } = [];

    public int DetailRowTotal { get; set; }

    /// <summary>Chart.js donut: aging buckets.</summary>
    public List<string> DonutLabels { get; set; } = [];
    public List<int> DonutValues { get; set; } = [];

    /// <summary>Chart.js bar: status categories.</summary>
    public List<string> BarLabels { get; set; } = [];
    public List<int> BarValues { get; set; } = [];
}

public class AgingBucketCount
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class OperationAgingDetailRow
{
    public string AppointmentId { get; set; } = string.Empty;
    public string OrderCreateDateRaw { get; set; } = string.Empty;
    public int AgeDays { get; set; }
    public string AgingBucket { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Skillset { get; set; } = string.Empty;
    public string Territory { get; set; } = string.Empty;

    /// <summary>repair | install | other — client-side filter.</summary>
    public string SkillKind { get; set; } = "other";
}

public class DailyStatusReportRow
{
    public string MetricKey { get; set; } = string.Empty;
    public string MetricLabel { get; set; } = string.Empty;
    public string ColorKey { get; set; } = string.Empty;

    /// <summary>Per day column, same length as <see cref="OperationAgingViewModel.DailyHeaderLabels"/>.</summary>
    public List<int> DayValues { get; set; } = [];

    public int RowTotal { get; set; }
}

public class AgingBucketMatrixRow
{
    public string RowKey { get; set; } = string.Empty;
    public string RowLabel { get; set; } = string.Empty;
    public List<int> BucketCounts { get; set; } = [];
    public int Total { get; set; }
}

public class RepairRemarkBucketRow
{
    public string RemarkLabel { get; set; } = string.Empty;
    public List<int> BucketCounts { get; set; } = [];
    public int RepairTotal { get; set; }
    public int GrandTotal { get; set; }
}
