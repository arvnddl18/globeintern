using SlotAd_Globe.Data;

namespace SlotAd_Globe.Services;

public interface IReportDashboardArchiveRecorder
{
    /// <summary>
    /// Persists dashboard snapshots and exports for an upload about to be FIFO-evicted.
    /// Throws if CSV is missing or computation fails (caller should roll back the transaction).
    /// </summary>
    Task RecordSnapshotBeforeEvictionAsync(
        AppDbContext db,
        ReportUploadEntity victim,
        string materializeRoot,
        CancellationToken cancellationToken = default);
}
