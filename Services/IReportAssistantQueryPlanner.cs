using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IReportAssistantQueryPlanner
{
    Task<ReportAssistantQueryPlan> PlanAsync(
        string userMessage,
        IReadOnlyList<ReportAssistantChatMessageDto> conversationHistory,
        object summaryContext,
        CancellationToken cancellationToken = default);
}
