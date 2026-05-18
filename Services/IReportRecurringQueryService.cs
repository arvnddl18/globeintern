using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IReportRecurringQueryService
{
    Task<ReportRecurringQueryResult> ExecuteAsync(
        Guid userId,
        string token,
        ReportRecurringQueryRequest request,
        CancellationToken cancellationToken = default);
}
