using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IReportAssistantContextFactory
{
    Task<object> BuildContextAsync(
        Guid userId,
        ReportAssistantPageKind pageKind,
        string? token,
        string? view,
        CancellationToken cancellationToken = default);
}
