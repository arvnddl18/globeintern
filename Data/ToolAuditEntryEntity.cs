namespace SlotAd_Globe.Data;

public class ToolAuditEntryEntity
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }
    public ToolAuditSessionEntity Session { get; set; } = null!;

    public string TechnicianName { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;

    public ToolAuditStatus Status { get; set; }

    /// <summary>Original cell text from the sheet (for debugging).</summary>
    public string? RawValue { get; set; }
}

