using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IReportSessionStore
{
    /// <summary>Persists CSV and session; returns URL token.</summary>
    Task<string> CreateSessionFromCsvAsync(Stream csvStream, string? originalFileName = null, CancellationToken cancellationToken = default);

    bool IsValidTokenFormat(string token);

    bool TryGetCsvPath(string token, out string csvPath);

    Task<ReportSessionData?> LoadAsync(string token, CancellationToken cancellationToken = default);

    Task SaveFiltersAsync(string token, FilterOptionsViewModel filters, CancellationToken cancellationToken = default);

    Task SetCsvSourceKindAsync(string token, CsvSourceKind kind, CancellationToken cancellationToken = default);

    void CleanupExpiredSessions();

    /// <summary>Removes all KPI report uploads and archived snapshots for the user (DB + on-disk CSV dirs where present).</summary>
    Task DeleteAllReportHistoryForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
