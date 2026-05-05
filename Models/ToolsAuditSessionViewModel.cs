namespace SlotAd_Globe.Models;

public class ToolsAuditSessionViewModel
{
    public Guid SessionId { get; set; }
    public string? OriginalFileName { get; set; }
    public DateOnly? AuditDate { get; set; }
    public DateOnly WeekStartDate { get; set; }
    public DateTime UploadedUtc { get; set; }

    public string? StatusFilter { get; set; }
    public string SortBy { get; set; } = "none";
    public string SortDir { get; set; } = "desc";

    public List<ToolsAuditTechnicianSummaryRow> TechnicianSummary { get; set; } = [];
    public List<ToolsAuditToolSummaryRow> ToolSummary { get; set; } = [];
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

