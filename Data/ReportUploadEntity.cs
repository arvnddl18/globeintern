using SlotAd_Globe.Models;

namespace SlotAd_Globe.Data;

public class ReportUploadEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public string Token { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public CsvSourceKind CsvSourceKind { get; set; }
    /// <summary>Legacy backup of file bytes; new uploads leave this null and keep CSV on disk only.</summary>
    public byte[]? CsvContent { get; set; }

    /// <summary>Serialized <see cref="ReportSessionData"/>.</summary>
    public string SessionJson { get; set; } = "{}";

    public DateTime UploadedUtc { get; set; }
}
