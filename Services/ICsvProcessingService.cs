using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface ICsvProcessingService
{
    Task<CsvSourceKind> DetectCsvSourceKindAsync(
        string csvFilePath,
        string? originalFileName,
        CancellationToken cancellationToken = default);

    Task<FilterOptionsViewModel> ExtractFilterOptionsAsync(
        Stream csvStream, string reportToken, CancellationToken cancellationToken = default);

    Task<MemoryStream> GenerateXlsxAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets);

    Task<KpiDashboardViewModel> ComputeKpiAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates);

    Task<KpiDashboardViewModel> ComputeAllStatusComplianceKpiAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates);

    Task<MemoryStream> GenerateFilteredXlsxAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates);

    Task<MemoryStream> GenerateSlotAdherenceCsvAsync(KpiDashboardViewModel kpi);

    Task<MemoryStream> GenerateSlotAdherenceVisualXlsxAsync(
        KpiDashboardViewModel kpi,
        IReadOnlyCollection<SlotAdherenceChartImage> chartImages);

    /// <summary>
    /// Extracts heatmap data from the CSV with NO Slot Adherence filters applied.
    /// Returns a partial <see cref="KpiDashboardViewModel"/> with only the Heatmap* fields populated.
    /// </summary>
    Task<KpiDashboardViewModel> ExtractHeatmapSnapshotAsync(string csvFilePath);

    /// <summary>
    /// Operation aging: Davao North, order-create KPIs/charts/detail by calendar year (<paramref name="agingMonthParam"/> ignored).
    /// </summary>
    Task<OperationAgingViewModel> ComputeOperationAgingAsync(
        string csvFilePath,
        string reportToken,
        string? selectedMonthParam,
        int? agingYearParam,
        int? agingMonthParam,
        int detailPage = 1,
        int detailPageSize = 20,
        int? dailyFocusDay = null,
        CancellationToken cancellationToken = default);

    Task<CleanedDataSummary> CleanAndAppendRawDataAsync(
        Stream rawStream,
        CancellationToken cancellationToken = default);
}
