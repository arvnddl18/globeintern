using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public sealed class ReportAssistantContextFactory : IReportAssistantContextFactory
{
    private const int DistTopN = 25;
    private const int DateSeriesTopN = 40;
    private const int PreviewRowCap = 15;
    private const int PreviewColCap = 14;
    private const int ListCap = 60;
    private const int OperationalSeriesCap = 96;
    private const int RecurringSampleCap = 8;
    private const int RecurringRankCap = 10;

    private readonly AppDbContext _db;
    private readonly IReportSessionStore _sessionStore;
    private readonly ICsvProcessingService _csvService;
    private readonly IOperationalReportService _operationalService;
    private readonly IMemoryCache _cache;

    public ReportAssistantContextFactory(
        AppDbContext db,
        IReportSessionStore sessionStore,
        ICsvProcessingService csvService,
        IOperationalReportService operationalService,
        IMemoryCache cache)
    {
        _db = db;
        _sessionStore = sessionStore;
        _csvService = csvService;
        _operationalService = operationalService;
        _cache = cache;
    }

    public async Task<object> BuildContextAsync(
        Guid userId,
        ReportAssistantPageKind pageKind,
        string? token,
        string? view,
        CancellationToken cancellationToken = default)
    {
        return pageKind switch
        {
            ReportAssistantPageKind.Upload => UploadHint("upload"),
            ReportAssistantPageKind.CleanedDataExport => UploadHint("cleaned_data_export"),
            ReportAssistantPageKind.Operational => await BuildOperationalAsync(userId, cancellationToken),
            ReportAssistantPageKind.KpiDashboard or ReportAssistantPageKind.KpiFilter =>
                await BuildKpiAsync(userId, pageKind, token, view, cancellationToken),
            _ => UploadHint("unknown")
        };
    }

    private static Dictionary<string, object?> UploadHint(string page)
    {
        return new Dictionary<string, object?>
        {
            ["page"] = page,
            ["dataScope"] = "none",
            ["hint"] =
                "No KPI file is loaded on this page. Open the KPI tab, upload a KPI CSV, then use the assistant from Dashboard or Filter for numbers from your file."
        };
    }

    private async Task<object> BuildKpiAsync(
        Guid userId,
        ReportAssistantPageKind pageKind,
        string? token,
        string? view,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || !_sessionStore.IsValidTokenFormat(token))
        {
            return new Dictionary<string, object?>
            {
                ["page"] = pageKind == ReportAssistantPageKind.KpiFilter ? "kpi_filter" : "kpi_dashboard",
                ["dataScope"] = "none",
                ["hint"] = "No report token is loaded. Upload a KPI file or open a saved dashboard link."
            };
        }

        if (!await UserCanAccessKpiTokenAsync(userId, token, cancellationToken))
        {
            return new Dictionary<string, object?>
            {
                ["page"] = "kpi",
                ["dataScope"] = "denied",
                ["hint"] = "This report is not available for your account."
            };
        }

        var resolved = await ResolveKpiAsync(userId, token, view, cancellationToken);
        if (!resolved.Ok)
        {
            return new Dictionary<string, object?>
            {
                ["page"] = pageKind == ReportAssistantPageKind.KpiFilter ? "kpi_filter" : "kpi_dashboard",
                ["dataScope"] = "error",
                ["hint"] = resolved.Error ?? "Could not load KPI context."
            };
        }

        var (kpi, isArchived, activeView) = (resolved.Kpi, resolved.IsArchived, resolved.ActiveView);
        var fingerprint = BuildKpiFingerprint(token!, activeView, kpi, isArchived);
        var cacheKey = $"rac-kpi:{userId:N}:{fingerprint}";
        if (_cache.TryGetValue(cacheKey, out Dictionary<string, object?>? cached) && cached is not null)
            return cached;

        var slim = MapKpiToContext(pageKind, activeView, isArchived, kpi);
        slim["reportSections"] = new[]
        {
            "slotAdherence",
            "heatmapAnalysis",
            "recurringTickets",
            "recurringHeatmap"
        };

        if (!isArchived && _sessionStore.TryGetCsvPath(token!, out var csvPathForSections))
        {
            try
            {
                await EnrichKpiReportSectionsAsync(slim, csvPathForSections, cancellationToken);
            }
            catch
            {
                slim["recurringTickets"] = new Dictionary<string, object?>
                {
                    ["error"] = "Could not load recurring tickets summary."
                };
            }
        }

        _cache.Set(cacheKey, slim, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
        });
        return slim;
    }

    private async Task EnrichKpiReportSectionsAsync(
        Dictionary<string, object?> slim,
        string csvPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var hm = await _csvService.ExtractHeatmapSnapshotAsync(csvPath);
            slim["heatmapAnalysis"] = new Dictionary<string, object?>
            {
                ["totalAppointments"] = hm.HeatmapTotalAppointments,
                ["repairCount"] = hm.HeatmapRepairCount,
                ["installCount"] = hm.HeatmapInstallCount,
                ["territoryDistributionTop"] = TopCounts(hm.HeatmapTerritoryDistribution, DistTopN),
                ["hasCoordinates"] = hm.HeatmapHasCoordinates
            };
        }
        catch
        {
            slim["heatmapAnalysis"] = new Dictionary<string, object?> { ["unavailable"] = true };
        }

        try
        {
            var rhm = await _csvService.ExtractRecurringHeatmapSnapshotAsync(csvPath, cancellationToken);
            slim["recurringHeatmap"] = new Dictionary<string, object?>
            {
                ["totalRecurringInstances"] = rhm.RecurringHeatmapTotalAppointments,
                ["repairCount"] = rhm.RecurringHeatmapRepairCount,
                ["installCount"] = rhm.RecurringHeatmapInstallCount
            };
        }
        catch
        {
            slim["recurringHeatmap"] = new Dictionary<string, object?> { ["unavailable"] = true };
        }

        var (sampleRows, _, summary) = await _csvService.GetPaginatedRecurringTicketsAsync(
            csvPath,
            filterMode: "all",
            page: 1,
            pageSize: RecurringSampleCap,
            cancellationToken: cancellationToken);

        slim["recurringTickets"] = new Dictionary<string, object?>
        {
            ["description"] =
                "Recurring tickets: service IDs with an initial Install/Repair followed by a later Repair on a different day.",
            ["totalRecurringInstances"] = summary.TotalRecurringTickets,
            ["topFacilities"] = RankItemsSlim(summary.TopNaps, RecurringRankCap),
            ["topCabinets"] = RankItemsSlim(summary.TopCabinets, RecurringRankCap),
            ["topTeams"] = RankItemsSlim(summary.TopTechTeams, RecurringRankCap),
            ["sampleRows"] = sampleRows.Select(RecurringRowSlim).ToList()
        };
    }

    private static List<Dictionary<string, object?>> RankItemsSlim(List<TopRankItem> items, int cap) =>
        items.Take(cap).Select(i => new Dictionary<string, object?>
        {
            ["name"] = i.Name,
            ["count"] = i.Count
        }).ToList();

    private static Dictionary<string, object?> RecurringRowSlim(RecurringTicketRow r) =>
        new()
        {
            ["serviceId"] = r.ServiceIdNumber,
            ["customerName"] = r.CustomerName,
            ["territory"] = r.Territory,
            ["facilityName"] = r.FacilityName,
            ["cabinetId"] = r.CabinetId,
            ["team"] = r.Team,
            ["initialTicketDate"] = r.InitialTicketDate,
            ["initialSkillset"] = r.InitialSkillset,
            ["recurringTicketDate"] = r.RecurringTicketDate,
            ["recurringSkillset"] = r.RecurringSkillset,
            ["daysBetween"] = r.DaysBetween
        };

    private readonly record struct KpiResolveResult(
        bool Ok,
        KpiDashboardViewModel Kpi,
        bool IsArchived,
        string ActiveView,
        string? Error);

    private async Task<KpiResolveResult> ResolveKpiAsync(
        Guid userId,
        string token,
        string? requestedView,
        CancellationToken cancellationToken)
    {
        if (_sessionStore.TryGetCsvPath(token, out var csvPath))
        {
            var session = await _sessionStore.LoadAsync(token, cancellationToken);
            if (session is null)
                return new KpiResolveResult(false, new KpiDashboardViewModel(), false, "pending", "Report session is incomplete.");

            var sourceKind = session.CsvSourceKind;
            var activeView = !string.IsNullOrWhiteSpace(requestedView)
                ? (string.Equals(requestedView, "status", StringComparison.OrdinalIgnoreCase) ? "status" : "pending")
                : sourceKind == CsvSourceKind.AllStatus ? "status" : "pending";

            var filterOptions = await GetFilterOptionsForDashboardAsync(token, csvPath, session, cancellationToken);
            var activeFilters = ReportSessionFilterResolver.GetSessionFiltersForView(session, activeView);
            var mode = activeFilters.DateFilterMode;
            DateOnly? singleDate = ReportSessionDate.ParseDateOrNull(activeFilters.SelectedDate);
            DateOnly? rangeStart = ReportSessionDate.ParseDateOrNull(activeFilters.DateRangeStart);
            DateOnly? rangeEnd = ReportSessionDate.ParseDateOrNull(activeFilters.DateRangeEnd);

            var territoryFilters = ResolveTerritoryFiltersForKpi(
                sourceKind,
                activeFilters.SelectedTerritories,
                filterOptions.AvailableTerritories);

            KpiDashboardViewModel kpi = activeView == "status"
                ? await _csvService.ComputeAllStatusComplianceKpiAsync(
                    csvPath,
                    mode,
                    singleDate,
                    rangeStart,
                    rangeEnd,
                    territoryFilters,
                    activeFilters.SelectedStatuses,
                    activeFilters.SelectedSubStatuses,
                    activeFilters.SelectedSkillsets,
                    activeFilters.SelectedOrderCreateDates)
                : await _csvService.ComputeKpiAsync(
                    csvPath,
                    mode,
                    singleDate,
                    rangeStart,
                    rangeEnd,
                    territoryFilters,
                    activeFilters.SelectedStatuses,
                    activeFilters.SelectedSubStatuses,
                    activeFilters.SelectedSkillsets,
                    activeFilters.SelectedOrderCreateDates);

            kpi.ReportToken = "";
            kpi.ActiveDashboardView = activeView;
            kpi.CsvSourceKind = sourceKind;
            kpi.DateFilterMode = mode;
            kpi.SelectedDate = activeFilters.SelectedDate;
            kpi.DateRangeStart = activeFilters.DateRangeStart;
            kpi.DateRangeEnd = activeFilters.DateRangeEnd;
            kpi.SelectedTerritories = territoryFilters;
            kpi.SelectedStatuses = activeFilters.SelectedStatuses;
            kpi.SelectedSubStatuses = activeFilters.SelectedSubStatuses;
            kpi.SelectedSkillsets = activeFilters.SelectedSkillsets;
            kpi.SelectedOrderCreateDates = activeFilters.SelectedOrderCreateDates;
            return new KpiResolveResult(true, kpi, false, activeView, null);
        }

        var archive = await _db.ReportDashboardArchives
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Token == token && a.UserId == userId, cancellationToken);
        if (archive is null)
            return new KpiResolveResult(false, new KpiDashboardViewModel(), false, "pending", "Report not found or expired.");

        var archiveSession = JsonSerializer.Deserialize<ReportSessionData>(archive.SessionJson, ReportSessionJson.Options)
                             ?? new ReportSessionData { CreatedUtc = archive.UploadedUtc };
        var archiveSourceKind = archiveSession.CsvSourceKind;
        var activeViewArchive = !string.IsNullOrWhiteSpace(requestedView)
            ? (string.Equals(requestedView, "status", StringComparison.OrdinalIgnoreCase) ? "status" : "pending")
            : archiveSourceKind == CsvSourceKind.AllStatus ? "status" : "pending";
        var archiveKpiJson = activeViewArchive == "status" ? archive.StatusKpiJson : archive.PendingKpiJson;
        var archivedKpi = JsonSerializer.Deserialize<KpiDashboardViewModel>(archiveKpiJson, ReportKpiJson.Options) ?? new KpiDashboardViewModel();

        var archiveActiveFilters = ReportSessionFilterResolver.GetSessionFiltersForView(archiveSession, activeViewArchive);
        archivedKpi.ReportToken = "";
        archivedKpi.ActiveDashboardView = activeViewArchive;
        archivedKpi.CsvSourceKind = archiveSourceKind;
        archivedKpi.DateFilterMode = archiveActiveFilters.DateFilterMode;
        archivedKpi.SelectedDate = archiveActiveFilters.SelectedDate;
        archivedKpi.DateRangeStart = archiveActiveFilters.DateRangeStart;
        archivedKpi.DateRangeEnd = archiveActiveFilters.DateRangeEnd;
        archivedKpi.SelectedTerritories = ResolveTerritoryFiltersForKpi(
            archiveSourceKind,
            archiveActiveFilters.SelectedTerritories,
            archivedKpi.AvailableTerritories);
        archivedKpi.SelectedStatuses = archiveActiveFilters.SelectedStatuses;
        archivedKpi.SelectedSubStatuses = archiveActiveFilters.SelectedSubStatuses;
        archivedKpi.SelectedSkillsets = archiveActiveFilters.SelectedSkillsets;
        archivedKpi.SelectedOrderCreateDates = archiveActiveFilters.SelectedOrderCreateDates;
        archivedKpi.IsReadOnly = true;
        return new KpiResolveResult(true, archivedKpi, true, activeViewArchive, null);
    }

    private static string BuildKpiFingerprint(
        string token,
        string activeView,
        KpiDashboardViewModel kpi,
        bool isArchived)
    {
        var f = new ViewFilterSnapshot(
            kpi.DateFilterMode,
            kpi.SelectedDate,
            kpi.DateRangeStart,
            kpi.DateRangeEnd,
            kpi.SelectedTerritories,
            kpi.SelectedStatuses,
            kpi.SelectedSubStatuses,
            kpi.SelectedSkillsets,
            kpi.SelectedOrderCreateDates);

        return string.Join('\u001f',
            token,
            activeView,
            isArchived,
            f.DateFilterMode,
            f.SelectedDate ?? "",
            f.DateRangeStart ?? "",
            f.DateRangeEnd ?? "",
            string.Join(',', f.SelectedTerritories.Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(',', f.SelectedStatuses.Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(',', f.SelectedSubStatuses.Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(',', f.SelectedSkillsets.Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(',', f.SelectedOrderCreateDates.Order(StringComparer.OrdinalIgnoreCase)));
    }

    private Dictionary<string, object?> MapKpiToContext(
        ReportAssistantPageKind pageKind,
        string activeView,
        bool isArchived,
        KpiDashboardViewModel k)
    {
        var page = pageKind == ReportAssistantPageKind.KpiFilter ? "kpi_filter" : "kpi_dashboard";
        return new Dictionary<string, object?>
        {
            ["page"] = page,
            ["dataScope"] = "kpi",
            ["isArchivedReadOnly"] = isArchived,
            ["activeDashboardView"] = activeView,
            ["csvSourceKind"] = k.CsvSourceKind.ToString(),
            ["dateFilterMode"] = k.DateFilterMode,
            ["selectedDate"] = k.SelectedDate,
            ["dateRangeStart"] = k.DateRangeStart,
            ["dateRangeEnd"] = k.DateRangeEnd,
            ["dateRangeDisplay"] = k.DateRangeDisplay,
            ["selectedTerritories"] = CapList(k.SelectedTerritories, ListCap),
            ["selectedStatuses"] = CapList(k.SelectedStatuses, ListCap),
            ["selectedSubStatuses"] = CapList(k.SelectedSubStatuses, ListCap),
            ["selectedSkillsets"] = CapList(k.SelectedSkillsets, ListCap),
            ["selectedOrderCreateDates"] = CapList(k.SelectedOrderCreateDates, ListCap),
            ["totals"] = new Dictionary<string, object?>
            {
                ["totalAppointments"] = k.TotalAppointments,
                ["totalFilteredRows"] = k.TotalFilteredRows,
                ["uniqueTerritories"] = k.UniqueTerritoriesCount,
                ["uniqueSkillsets"] = k.UniqueSkillsetsCount,
                ["amSlotCount"] = k.AmSlotCount,
                ["pmSlotCount"] = k.PmSlotCount,
                ["delayedCount"] = k.DelayedCount,
                ["lapsedCount"] = k.LapsedCount,
                ["forVisitSubStatusCount"] = k.ForVisitSubStatusCount,
                ["forRescheduleSubStatusCount"] = k.ForRescheduleSubStatusCount,
                ["repairSkillsetCount"] = k.RepairSkillsetCount,
                ["completedStatusCount"] = k.CompletedStatusCount,
                ["complianceMetricsAvailable"] = k.ComplianceMetricsAvailable,
                ["compliancePass"] = k.CompliancePassCount,
                ["complianceFail"] = k.ComplianceFailCount,
                ["complianceNa"] = k.ComplianceNaCount
            },
            ["statusDistributionTop"] = TopCounts(k.StatusDistribution, DistTopN),
            ["subStatusDistributionTop"] = TopCounts(k.SubStatusDistribution, DistTopN),
            ["territoryDistributionTop"] = TopCounts(k.TerritoryDistribution, DistTopN),
            ["skillsetDistributionTop"] = TopCounts(k.SkillsetDistribution, DistTopN),
            ["appointmentsByDateTop"] = TopCounts(k.AppointmentsByDate, DateSeriesTopN),
            ["complianceFailReasonsTop"] = TopCounts(k.ComplianceFailReasons, DistTopN),
            ["topDelayReasons"] = k.TopDelayReasons.Take(20).Select(p => new Dictionary<string, object?>
            {
                ["reason"] = p.Key,
                ["count"] = p.Value
            }).ToList(),
            ["skillsetBySlot"] = k.SkillsetBySlot,
            ["availableColumns"] = new[]
            {
                "appointmentdate", "skillset", "status", "substatus", "territory",
                "customeraddress", "facilityname", "appointmentid", "workordernumber", "ordercreatedate"
            },
            ["previewRowsSample"] = k.PreviewRows.Take(PreviewRowCap).Select(row =>
            {
                var take = row.Take(PreviewColCap).ToDictionary(e => e.Key, e => e.Value);
                return (object)take;
            }).ToList()
        };
    }

    private async Task<object> BuildOperationalAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cacheKey = $"rac-op:{userId:N}";
        if (_cache.TryGetValue(cacheKey, out Dictionary<string, object?>? cached) && cached is not null)
            return cached;

        var alarmToken = await GetLatestOperationalTokenByKindAsync(userId, OperationalReportKind.AlarmHistory, cancellationToken);
        var perfToken = await GetLatestOperationalTokenByKindAsync(userId, OperationalReportKind.PerformanceHistory, cancellationToken);

        OperationalReportPanelViewModel? alarmReport = null;
        OperationalReportPanelViewModel? perfReport = null;

        if (!string.IsNullOrWhiteSpace(alarmToken)
            && _sessionStore.TryGetCsvPath(alarmToken, out var alarmCsvPath))
        {
            var alarmSession = await _sessionStore.LoadAsync(alarmToken, cancellationToken);
            if (alarmSession is not null && alarmSession.OperationalReportKind == OperationalReportKind.AlarmHistory)
            {
                var period = string.IsNullOrWhiteSpace(alarmSession.OperationalAlarmPeriodFilter) ? "1hour" : alarmSession.OperationalAlarmPeriodFilter;
                var mode = string.IsNullOrWhiteSpace(alarmSession.OperationalAlarmDateFilterMode) ? "all" : alarmSession.OperationalAlarmDateFilterMode;
                alarmReport = await _operationalService.BuildReportAsync(
                    alarmCsvPath,
                    await GetOriginalFileNameAsync(alarmToken, userId, cancellationToken),
                    null,
                    period,
                    mode,
                    alarmSession.OperationalAlarmSelectedDate,
                    alarmSession.OperationalAlarmDateRangeStart,
                    alarmSession.OperationalAlarmDateRangeEnd,
                    cancellationToken);
                alarmReport.ReportToken = "";
            }
        }

        if (!string.IsNullOrWhiteSpace(perfToken)
            && _sessionStore.TryGetCsvPath(perfToken, out var perfCsvPath))
        {
            var perfSession = await _sessionStore.LoadAsync(perfToken, cancellationToken);
            if (perfSession is not null && perfSession.OperationalReportKind == OperationalReportKind.PerformanceHistory)
            {
                var group = string.IsNullOrWhiteSpace(perfSession.OperationalSelectedPerformanceGroup)
                    ? null
                    : perfSession.OperationalSelectedPerformanceGroup;
                var resolvedPerfDate = perfSession.OperationalPerformanceSelectedDate;
                var resolvedPerfStart = perfSession.OperationalPerformanceDateRangeStart;
                var resolvedPerfEnd = perfSession.OperationalPerformanceDateRangeEnd;
                var resolvedPerfPeriod = string.IsNullOrWhiteSpace(perfSession.OperationalPerformancePeriodFilter)
                    ? "1hour"
                    : perfSession.OperationalPerformancePeriodFilter;
                var resolvedPerfMode = ResolveDateFilterMode(
                    null,
                    resolvedPerfDate,
                    resolvedPerfStart,
                    resolvedPerfEnd,
                    perfSession.OperationalPerformanceDateFilterMode);

                perfReport = await _operationalService.BuildReportAsync(
                    perfCsvPath,
                    await GetOriginalFileNameAsync(perfToken, userId, cancellationToken),
                    group,
                    resolvedPerfPeriod,
                    resolvedPerfMode,
                    resolvedPerfDate,
                    resolvedPerfStart,
                    resolvedPerfEnd,
                    cancellationToken);
                perfReport.ReportToken = "";
            }
        }

        OperationAgingViewModel? operationAging = null;
        var agingToken = await GetLatestKpiCsvTokenAsync(userId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(agingToken)
            && _sessionStore.TryGetCsvPath(agingToken, out var agingCsvPath))
        {
            try
            {
                operationAging = await _csvService.ComputeOperationAgingAsync(
                    agingCsvPath,
                    agingToken,
                    selectedMonthParam: null,
                    agingYearParam: null,
                    agingMonthParam: null,
                    detailPage: 1,
                    detailPageSize: 10,
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Operation aging is optional on the operational page.
            }
        }

        var root = new Dictionary<string, object?>
        {
            ["page"] = "operational",
            ["dataScope"] = "operational",
            ["reportSections"] = new[] { "alarmHistory", "performanceHistory", "operationAging" },
            ["hint"] =
                "Summaries from Alarm History, Performance History, and Operation Aging (latest KPI CSV) for your account.",
            ["alarm"] = alarmReport is null ? null : PanelSlim(alarmReport),
            ["performance"] = perfReport is null ? null : PanelSlim(perfReport),
            ["operationAging"] = operationAging is null ? null : OperationAgingSlim(operationAging)
        };

        _cache.Set(cacheKey, root, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
        });
        return root;
    }

    private static Dictionary<string, object?> OperationAgingSlim(OperationAgingViewModel a) =>
        new()
        {
            ["agingScopeLabel"] = a.AgingScopeLabel,
            ["totalOrdersYearScope"] = a.TotalOrdersYearScope,
            ["delayedCount"] = a.DelayedCount,
            ["pendingCount"] = a.PendingCount,
            ["ongoingCount"] = a.OngoingCount,
            ["unassignedCount"] = a.UnassignedCount,
            ["cancelledCount"] = a.CancelledCount,
            ["completedCount"] = a.CompletedCount,
            ["bucketLabels"] = a.BucketLabels,
            ["bucketMatrixRows"] = a.BucketMatrixRows.Take(12).Select(row => new Dictionary<string, object?>
            {
                ["label"] = row.RowLabel,
                ["bucketCounts"] = row.BucketCounts,
                ["total"] = row.Total
            }).ToList(),
            ["repairRemarkGrandTotal"] = a.RepairRemarkGrandTotal,
            ["detailSample"] = a.DetailRows.Take(8).Select(row => new Dictionary<string, object?>
            {
                ["workOrder"] = row.WorkOrder,
                ["skillset"] = row.Skillset,
                ["status"] = row.Status,
                ["territory"] = row.Territory,
                ["orderCreateDate"] = row.OrderCreateDateRaw,
                ["agingBucket"] = row.AgingBucket,
                ["ageDays"] = row.AgeDays
            }).ToList()
        };

    private async Task<string?> GetLatestKpiCsvTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var candidateTokens = await _db.ReportUploads
            .AsNoTracking()
            .Where(upload => upload.UserId == userId)
            .OrderByDescending(upload => upload.UploadedUtc)
            .Select(upload => upload.Token)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var t in candidateTokens)
        {
            if (!_sessionStore.TryGetCsvPath(t, out _))
                continue;

            var session = await _sessionStore.LoadAsync(t, cancellationToken);
            if (session?.OperationalReportKind is OperationalReportKind.AlarmHistory
                or OperationalReportKind.PerformanceHistory)
                continue;

            return t;
        }

        return null;
    }

    private static Dictionary<string, object?> PanelSlim(OperationalReportPanelViewModel r)
    {
        var n = Math.Min(r.IntervalLabels.Count, r.IntervalValues.Count);
        n = Math.Min(n, OperationalSeriesCap);
        var labels = r.IntervalLabels.Take(n).ToList();
        var values = r.IntervalValues.Take(n).ToList();
        var sn = Math.Min(r.SecondaryIntervalLabels.Count, r.SecondaryIntervalValues.Count);
        sn = Math.Min(sn, OperationalSeriesCap);
        var slim = new Dictionary<string, object?>
        {
            ["hasReport"] = r.HasReport,
            ["sourceFileName"] = r.SourceFileName,
            ["reportKind"] = r.ReportKind.ToString(),
            ["selectedPerformanceGroup"] = r.SelectedPerformanceGroup,
            ["selectedPeriod"] = r.SelectedPeriod,
            ["dateFilterMode"] = r.DateFilterMode,
            ["selectedDate"] = r.SelectedDate,
            ["dateRangeStart"] = r.DateRangeStart,
            ["dateRangeEnd"] = r.DateRangeEnd,
            ["primaryMetricLabel"] = r.PrimaryMetricLabel,
            ["intervalLabels"] = labels,
            ["intervalValues"] = values,
            ["secondaryMetricLabel"] = r.SecondaryMetricLabel,
            ["secondaryIntervalLabels"] = r.SecondaryIntervalLabels.Take(sn).ToList(),
            ["secondaryIntervalValues"] = r.SecondaryIntervalValues.Take(sn).ToList(),
            ["availablePerformanceGroups"] = CapList(r.AvailablePerformanceGroups, ListCap)
        };
        return slim;
    }

    private async Task<string> GetOriginalFileNameAsync(string token, Guid userId, CancellationToken cancellationToken)
    {
        var name = await _db.ReportUploads.AsNoTracking()
            .Where(u => u.Token == token && u.UserId == userId)
            .Select(u => u.OriginalFileName)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? "Operational report" : name!;
    }

    private async Task<string?> GetLatestOperationalTokenByKindAsync(
        Guid userId,
        OperationalReportKind requiredKind,
        CancellationToken cancellationToken)
    {
        var candidateTokens = await _db.ReportUploads
            .AsNoTracking()
            .Where(upload => upload.UserId == userId)
            .OrderByDescending(upload => upload.UploadedUtc)
            .Select(upload => upload.Token)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var t in candidateTokens)
        {
            if (!_sessionStore.TryGetCsvPath(t, out _))
                continue;

            var session = await _sessionStore.LoadAsync(t, cancellationToken);
            if (session?.OperationalReportKind == requiredKind)
                return t;
        }

        return null;
    }

    private async Task<bool> UserCanAccessKpiTokenAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        if (await _db.ReportUploads.AnyAsync(u => u.Token == token && u.UserId == userId, cancellationToken))
            return true;
        return await _db.ReportDashboardArchives.AnyAsync(a => a.Token == token && a.UserId == userId, cancellationToken);
    }

    private async Task<FilterOptionsViewModel> GetFilterOptionsForDashboardAsync(
        string token,
        string csvPath,
        ReportSessionData session,
        CancellationToken cancellationToken = default)
    {
        if (HasCachedFilterOptions(session))
            return FilterOptionsFromSessionCache(session, token);

        await using var optStream = new FileStream(
            csvPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await _csvService.ExtractFilterOptionsAsync(optStream, token, cancellationToken);
    }

    private static bool HasCachedFilterOptions(ReportSessionData? session) =>
        session?.CachedAvailableDates is { Count: > 0 };

    private static FilterOptionsViewModel FilterOptionsFromSessionCache(ReportSessionData session, string token) =>
        new()
        {
            ReportToken = token,
            AvailableDates = session.CachedAvailableDates ?? [],
            AvailableTerritories = session.CachedAvailableTerritories ?? [],
            AvailableStatuses = session.CachedAvailableStatuses ?? [],
            AvailableSubStatuses = session.CachedAvailableSubStatuses ?? [],
            AvailableSkillsets = session.CachedAvailableSkillsets ?? [],
            AvailableOrderCreateDates = session.CachedAvailableOrderCreateDates ?? []
        };

    private static List<string> CapList(IReadOnlyList<string> list, int max) =>
        list.Count <= max ? [.. list] : [.. list.Take(max)];

    private static Dictionary<string, int> TopCounts(Dictionary<string, int> source, int n) =>
        source
            .OrderByDescending(kv => kv.Value)
            .Take(n)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static List<string> ResolveTerritoryFiltersForKpi(
        CsvSourceKind sourceKind,
        IReadOnlyList<string> sessionTerritories,
        IReadOnlyList<string> availableTerritories)
    {
        if (sourceKind != CsvSourceKind.AllStatus)
            return [.. sessionTerritories];

        var davao = MatchAvailableOption(availableTerritories, "Davao North");
        return davao is not null ? [davao] : [.. sessionTerritories];
    }

    private static string? MatchAvailableOption(IReadOnlyList<string> available, string desired)
    {
        foreach (var a in available)
        {
            if (string.Equals(a, desired, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        return null;
    }

    private static string ResolveDateFilterMode(
        string? requestedMode,
        string? selectedDate,
        string? rangeStart,
        string? rangeEnd,
        string? fallbackMode)
    {
        if (!string.IsNullOrWhiteSpace(requestedMode))
            return requestedMode;

        var hasRangeStart = !string.IsNullOrWhiteSpace(rangeStart);
        var hasRangeEnd = !string.IsNullOrWhiteSpace(rangeEnd);
        if (hasRangeStart && hasRangeEnd)
            return "range";

        if (!string.IsNullOrWhiteSpace(selectedDate))
            return "single";

        if (string.Equals(fallbackMode, "single", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(selectedDate))
            return "single";

        if (string.Equals(fallbackMode, "range", StringComparison.OrdinalIgnoreCase)
            && hasRangeStart && hasRangeEnd)
            return "range";

        return "all";
    }
}
