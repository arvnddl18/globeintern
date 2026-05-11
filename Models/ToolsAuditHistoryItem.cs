namespace SlotAd_Globe.Models;

public class ToolsAuditHistoryItem
{
    public Guid SessionId { get; set; }
    public string? OriginalFileName { get; set; }
    public DateTime UploadedUtc { get; set; }
    public DateOnly? AuditDate { get; set; }
    public DateOnly WeekStartDate { get; set; }
}
