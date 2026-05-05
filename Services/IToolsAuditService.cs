using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IToolsAuditService
{
    Task<Guid> ImportFromXlsxAsync(
        Stream xlsxStream,
        string? originalFileName,
        Guid uploadedByUserId,
        CancellationToken cancellationToken = default);

    Task<ToolsAuditSessionViewModel?> GetSessionAsync(
        Guid sessionId,
        IReadOnlyCollection<string>? selectedStatuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken cancellationToken = default);
}

