using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface IOperationalReportService
{
    Task<OperationalReportPanelViewModel> BuildReportAsync(
        string csvFilePath,
        string sourceFileName,
        string? selectedPerformanceGroup,
        string periodFilter,
        string dateFilterMode,
        string? selectedDate,
        string? dateRangeStart,
        string? dateRangeEnd,
        CancellationToken cancellationToken = default);
}
