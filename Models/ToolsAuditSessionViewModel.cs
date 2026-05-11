namespace SlotAd_Globe.Models;

public class ToolsAuditSessionViewModel
{
    public Guid SessionId { get; set; }
    public string? OriginalFileName { get; set; }
    public DateOnly? AuditDate { get; set; }
    public DateOnly WeekStartDate { get; set; }
    public DateTime UploadedUtc { get; set; }

    public List<string> SelectedStatuses { get; set; } = [];
    public string SortBy { get; set; } = "none";
    public string SortDir { get; set; } = "desc";

    public List<ToolsAuditTechnicianSummaryRow> TechnicianSummary { get; set; } = [];
    /// <summary>Unfiltered tools summary (used for charts).</summary>
    public List<ToolsAuditToolSummaryRow> ToolSummaryAll { get; set; } = [];
    /// <summary>Filtered/sorted tools summary (used for Tools Summary table).</summary>
    public List<ToolsAuditToolSummaryRow> ToolSummary { get; set; } = [];
    public List<string> RawToolColumns { get; set; } = [];
    public List<ToolsAuditRawRow> RawRows { get; set; } = [];
}

public class ToolsAuditTechnicianSummaryRow
{
    public string TechnicianName { get; set; } = string.Empty;
    public int OkCount { get; set; }
    public int NoneCount { get; set; }
    public int DefectiveCount { get; set; }
    public int NaCount { get; set; }
    public int Total => OkCount + NoneCount + DefectiveCount + NaCount;
}

public class ToolsAuditToolSummaryRow
{
    public string ToolName { get; set; } = string.Empty;
    public int OkCount { get; set; }
    public int NoneCount { get; set; }
    public int DefectiveCount { get; set; }
    public int NaCount { get; set; }
    public int Total => OkCount + NoneCount + DefectiveCount + NaCount;
}

public class ToolsAuditRawRow
{
    public string TechnicianName { get; set; } = string.Empty;
    public Dictionary<string, ToolAuditCellViewModel> CellsByTool { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ToolAuditCellViewModel
{
    public string DisplayValue { get; set; } = "N/A";
    public string CssClass { get; set; } = "bg-slate-100 text-slate-700";
}

