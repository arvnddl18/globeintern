using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IReportCsvQueryService
{
    Task<ReportCsvQueryResult> ExecuteAsync(
        Guid userId,
        string token,
        string? view,
        ReportCsvQueryRequest request,
        CancellationToken cancellationToken = default);
}
