using SlotAd_Globe.Models;

namespace SlotAd_Globe.Data;

public class ReportDashboardArchiveEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public string Token { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public CsvSourceKind CsvSourceKind { get; set; }

    public DateTime UploadedUtc { get; set; }
    public DateTime EvictedUtc { get; set; }

    /// <summary>Serialized <see cref="ReportSessionData"/> at eviction.</summary>
    public string SessionJson { get; set; } = "{}";

    public string PendingKpiJson { get; set; } = "{}";
    public string StatusKpiJson { get; set; } = "{}";

    public byte[]? PendingFilteredXlsxBytes { get; set; }
    public byte[]? StatusFilteredXlsxBytes { get; set; }
    public byte[]? LegacyGenerateXlsxBytes { get; set; }
}
