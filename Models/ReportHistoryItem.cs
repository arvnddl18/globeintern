namespace SlotAd_Globe.Models;

public class ReportHistoryItem
{
    public string Token { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public DateTime UploadedUtc { get; set; }
    public CsvSourceKind CsvSourceKind { get; set; }
    public bool IsArchived { get; set; }
}
