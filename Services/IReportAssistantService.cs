using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IReportAssistantService
{
    Task<ReportAssistantChatResponse> ChatAsync(
        Guid userId,
        ReportAssistantChatRequest request,
        CancellationToken cancellationToken = default);
}
