namespace SlotAd_Globe.Data;

public class ToolAuditSessionEntity
{
    public Guid Id { get; set; }

    public Guid UploadedByUserId { get; set; }
    public AppUser UploadedByUser { get; set; } = null!;

    public string? OriginalFileName { get; set; }

    /// <summary>Audit date found in the template (may be blank).</summary>
    public DateOnly? AuditDate { get; set; }

    /// <summary>Normalized Monday date for the audit week.</summary>
    public DateOnly WeekStartDate { get; set; }

    public DateTime UploadedUtc { get; set; }

    public List<ToolAuditEntryEntity> Entries { get; set; } = [];
}

