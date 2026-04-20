using System.Text.Json;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;
using SlotAd_Globe.Options;

namespace SlotAd_Globe.Services;

public class ReportDashboardArchiveRecorder : IReportDashboardArchiveRecorder
{
    private const string SourceFileName = "source.csv";
    private const int MaxPreviewRows = 150;

    private readonly ICsvProcessingService _csv;
    private readonly ILogger<ReportDashboardArchiveRecorder> _logger;

    public ReportDashboardArchiveRecorder(
        ICsvProcessingService csv,
        ILogger<ReportDashboardArchiveRecorder> logger)
    {
        _csv = csv;
        _logger = logger;
    }

    public async Task RecordSnapshotBeforeEvictionAsync(
        AppDbContext db,
        ReportUploadEntity victim,
        string materializeRoot,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveCsvPath(victim, materializeRoot, out var csvPath))
            throw new InvalidOperationException($"Cannot archive report {victim.Token}: CSV not found on disk or in database.");

        ReportSessionData session;
        try
        {
            session = JsonSerializer.Deserialize<ReportSessionData>(victim.SessionJson, ReportSessionJson.Options)
                      ?? new ReportSessionData { CreatedUtc = victim.UploadedUtc };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid session JSON for token {victim.Token}.", ex);
        }

        var pendingFilters = ReportSessionFilterResolver.GetSessionFiltersForView(session, "pending");
        var statusFilters = ReportSessionFilterResolver.GetSessionFiltersForView(session, "status");

        var pendingKpi = await _csv.ComputeKpiAsync(
            csvPath,
            pendingFilters.DateFilterMode,
            ReportSessionDate.ParseDateOrNull(pendingFilters.SelectedDate),
            ReportSessionDate.ParseDateOrNull(pendingFilters.DateRangeStart),
            ReportSessionDate.ParseDateOrNull(pendingFilters.DateRangeEnd),
            pendingFilters.SelectedTerritories,
            pendingFilters.SelectedStatuses,
            pendingFilters.SelectedSubStatuses,
            pendingFilters.SelectedSkillsets,
            pendingFilters.SelectedOrderCreateDates);

        var statusKpi = await _csv.ComputeAllStatusComplianceKpiAsync(
            csvPath,
            statusFilters.DateFilterMode,
            ReportSessionDate.ParseDateOrNull(statusFilters.SelectedDate),
            ReportSessionDate.ParseDateOrNull(statusFilters.DateRangeStart),
            ReportSessionDate.ParseDateOrNull(statusFilters.DateRangeEnd),
            statusFilters.SelectedTerritories,
            statusFilters.SelectedStatuses,
            statusFilters.SelectedSubStatuses,
            statusFilters.SelectedSkillsets,
            statusFilters.SelectedOrderCreateDates);

        TrimKpiPreview(pendingKpi);
        TrimKpiPreview(statusKpi);

        var now = DateTime.UtcNow;
        pendingKpi.ReportHistory = [];
        statusKpi.ReportHistory = [];

        var pendingJson = JsonSerializer.Serialize(pendingKpi, ReportKpiJson.Options);
        var statusJson = JsonSerializer.Serialize(statusKpi, ReportKpiJson.Options);

        byte[] pendingBytes;
        await using (var pendingXlsx = await _csv.GenerateFilteredXlsxAsync(
            csvPath,
            pendingFilters.DateFilterMode,
            ReportSessionDate.ParseDateOrNull(pendingFilters.SelectedDate),
            ReportSessionDate.ParseDateOrNull(pendingFilters.DateRangeStart),
            ReportSessionDate.ParseDateOrNull(pendingFilters.DateRangeEnd),
            pendingFilters.SelectedTerritories,
            pendingFilters.SelectedStatuses,
            pendingFilters.SelectedSubStatuses,
            pendingFilters.SelectedSkillsets,
            pendingFilters.SelectedOrderCreateDates))
        {
            pendingBytes = pendingXlsx.ToArray();
        }

        byte[] statusBytes;
        await using (var statusXlsx = await _csv.GenerateFilteredXlsxAsync(
            csvPath,
            statusFilters.DateFilterMode,
            ReportSessionDate.ParseDateOrNull(statusFilters.SelectedDate),
            ReportSessionDate.ParseDateOrNull(statusFilters.DateRangeStart),
            ReportSessionDate.ParseDateOrNull(statusFilters.DateRangeEnd),
            statusFilters.SelectedTerritories,
            statusFilters.SelectedStatuses,
            statusFilters.SelectedSubStatuses,
            statusFilters.SelectedSkillsets,
            statusFilters.SelectedOrderCreateDates))
        {
            statusBytes = statusXlsx.ToArray();
        }

        var mode = session.DateFilterMode ?? "all";
        byte[] legacyBytes;
        await using (var legacyXlsx = await _csv.GenerateXlsxAsync(
            csvPath,
            mode,
            ReportSessionDate.ParseDateOrNull(session.SelectedDate),
            ReportSessionDate.ParseDateOrNull(session.DateRangeStart),
            ReportSessionDate.ParseDateOrNull(session.DateRangeEnd),
            session.SelectedTerritories ?? [],
            session.SelectedStatuses ?? [],
            session.SelectedSubStatuses ?? [],
            session.SelectedSkillsets ?? []))
        {
            legacyBytes = legacyXlsx.ToArray();
        }

        db.ReportDashboardArchives.Add(new ReportDashboardArchiveEntity
        {
            Id = Guid.NewGuid(),
            UserId = victim.UserId,
            Token = victim.Token,
            OriginalFileName = victim.OriginalFileName,
            CsvSourceKind = victim.CsvSourceKind,
            UploadedUtc = victim.UploadedUtc,
            EvictedUtc = now,
            SessionJson = victim.SessionJson,
            PendingKpiJson = pendingJson,
            StatusKpiJson = statusJson,
            PendingFilteredXlsxBytes = pendingBytes,
            StatusFilteredXlsxBytes = statusBytes,
            LegacyGenerateXlsxBytes = legacyBytes
        });

        _logger.LogInformation(
            "Recorded dashboard archive for FIFO victim token {Token} user {UserId}",
            victim.Token,
            victim.UserId);
    }

    private static bool TryResolveCsvPath(ReportUploadEntity entity, string materializeRoot, out string csvPath)
    {
        csvPath = "";
        var dir = Path.Combine(materializeRoot, entity.Token);
        var path = Path.Combine(dir, SourceFileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            csvPath = path;
            return true;
        }

        if (entity.CsvContent is not { Length: > 0 })
            return false;

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, entity.CsvContent);
        csvPath = path;
        return true;
    }

    private static void TrimKpiPreview(KpiDashboardViewModel kpi)
    {
        if (kpi.PreviewRows.Count <= MaxPreviewRows)
            return;
        kpi.PreviewRows = kpi.PreviewRows.Take(MaxPreviewRows).ToList();
    }
}
