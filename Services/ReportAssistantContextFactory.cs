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
    private const int PreviewColCap = 22;
    private const int ColumnCatalogCap = 90;
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
            ReportAssistantPageKind.SwuReorganizedExport => UploadHint("swu_reorganized_export"),
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

        FilterOptionsViewModel? filterOptions = null;
        KpiFileOverview? fileOverview = null;
        KpiCsvAssistantCatalog? csvCatalog = null;
        string? sourceFileName = null;
        DateTime? uploadedUtc = null;

        if (!isArchived && _sessionStore.TryGetCsvPath(token!, out var csvPathForMeta))
        {
            var sessionForOpts = await _sessionStore.LoadAsync(token!, cancellationToken);
            if (sessionForOpts is not null)
            {
                filterOptions = await GetFilterOptionsForDashboardAsync(
                    token!,
                    csvPathForMeta,
                    sessionForOpts,
                    cancellationToken);
                kpi.AvailableDates = filterOptions.AvailableDates;
                kpi.AvailableTerritories = filterOptions.AvailableTerritories;
                kpi.AvailableStatuses = filterOptions.AvailableStatuses;
                kpi.AvailableSubStatuses = filterOptions.AvailableSubStatuses;
                kpi.AvailableSkillsets = filterOptions.AvailableSkillsets;
                kpi.AvailableCustomerTypes = filterOptions.AvailableCustomerTypes;
                kpi.AvailableOrderCreateDates = filterOptions.AvailableOrderCreateDates;
            }

            try
            {
                csvCatalog = await _csvService.ExtractKpiCsvAssistantCatalogAsync(csvPathForMeta, cancellationToken);
                fileOverview = csvCatalog.Overview;
            }
            catch
            {
                // CSV catalog is optional; dashboard totals still apply.
            }

            var uploadMeta = await _db.ReportUploads.AsNoTracking()
                .Where(u => u.Token == token && u.UserId == userId)
                .Select(u => new { u.OriginalFileName, u.UploadedUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (uploadMeta is not null)
            {
                sourceFileName = uploadMeta.OriginalFileName;
                uploadedUtc = uploadMeta.UploadedUtc;
            }
        }

        var fingerprint = BuildKpiFingerprint(token!, activeView, kpi, isArchived);
        var cacheKey = $"rac-kpi:{userId:N}:{fingerprint}";
        if (_cache.TryGetValue(cacheKey, out Dictionary<string, object?>? cached) && cached is not null)
            return cached;

        var slim = MapKpiToContext(
            pageKind,
            activeView,
            isArchived,
            kpi,
            filterOptions,
            fileOverview,
            csvCatalog,
            sourceFileName,
            uploadedUtc);
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
                    activeFilters.SelectedCustomerTypes,
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
                    activeFilters.SelectedCustomerTypes,
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
            kpi.SelectedCustomerTypes = activeFilters.SelectedCustomerTypes;
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
        archivedKpi.SelectedCustomerTypes = archiveActiveFilters.SelectedCustomerTypes;
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
            kpi.SelectedCustomerTypes,
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
            string.Join(',', f.SelectedCustomerTypes.Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(',', f.SelectedOrderCreateDates.Order(StringComparer.OrdinalIgnoreCase)));
    }

    private Dictionary<string, object?> MapKpiToContext(
        ReportAssistantPageKind pageKind,
        string activeView,
        bool isArchived,
        KpiDashboardViewModel k,
        FilterOptionsViewModel? filterOptions,
        KpiFileOverview? fileOverview,
        KpiCsvAssistantCatalog? csvCatalog,
        string? sourceFileName,
        DateTime? uploadedUtc)
    {
        var page = pageKind == ReportAssistantPageKind.KpiFilter ? "kpi_filter" : "kpi_dashboard";
        var slotAdherence = BuildSlotAdherenceTotals(k);
        var fileByDate = fileOverview?.AppointmentsByDate ?? new Dictionary<string, int>();
        var availableDates = filterOptions?.AvailableDates ?? k.AvailableDates;

        return new Dictionary<string, object?>
        {
            ["page"] = page,
            ["dataScope"] = "kpi",
            ["isArchivedReadOnly"] = isArchived,
            ["activeDashboardView"] = activeView,
            ["csvSourceKind"] = k.CsvSourceKind.ToString(),
            ["dataset"] = new Dictionary<string, object?>
            {
                ["sourceFileName"] = sourceFileName,
                ["uploadedUtc"] = uploadedUtc?.ToString("O"),
                ["totalCsvRows"] = fileOverview?.TotalCsvRows,
                ["rowsWithAppointmentDate"] = fileOverview?.RowsWithAppointmentDate,
                ["appointmentDateRangeInFile"] = new Dictionary<string, object?>
                {
                    ["min"] = fileOverview?.AppointmentDateMin ?? availableDates.LastOrDefault(),
                    ["max"] = fileOverview?.AppointmentDateMax ?? availableDates.FirstOrDefault(),
                    ["distinctDateCount"] = fileOverview?.DistinctAppointmentDates ?? availableDates.Count
                },
                ["appointmentsByDateInFile"] = TopCounts(fileByDate, DateSeriesTopN),
                ["availableAppointmentDates"] = CapList(availableDates, ListCap),
                ["availableTerritories"] = CapList(filterOptions?.AvailableTerritories ?? k.AvailableTerritories, ListCap),
                ["availableStatuses"] = CapList(filterOptions?.AvailableStatuses ?? k.AvailableStatuses, ListCap),
                ["availableSubStatuses"] = CapList(filterOptions?.AvailableSubStatuses ?? k.AvailableSubStatuses, ListCap),
                ["availableSkillsets"] = CapList(filterOptions?.AvailableSkillsets ?? k.AvailableSkillsets, ListCap),
                ["availableCustomerTypes"] = CapList(filterOptions?.AvailableCustomerTypes ?? k.AvailableCustomerTypes, ListCap),
                ["availableOrderCreateDates"] = CapList(
                    filterOptions?.AvailableOrderCreateDates ?? k.AvailableOrderCreateDates,
                    ListCap)
            },
            ["activeFilters"] = new Dictionary<string, object?>
            {
                ["summary"] = DescribeActiveFilters(k),
                ["dateFilterMode"] = k.DateFilterMode,
                ["selectedDate"] = k.SelectedDate,
                ["dateRangeStart"] = k.DateRangeStart,
                ["dateRangeEnd"] = k.DateRangeEnd,
                ["dateRangeDisplay"] = k.DateRangeDisplay,
                ["selectedTerritories"] = CapList(k.SelectedTerritories, ListCap),
                ["selectedStatuses"] = CapList(k.SelectedStatuses, ListCap),
                ["selectedSubStatuses"] = CapList(k.SelectedSubStatuses, ListCap),
                ["selectedSkillsets"] = CapList(k.SelectedSkillsets, ListCap),
                ["selectedCustomerTypes"] = CapList(k.SelectedCustomerTypes, ListCap),
                ["selectedOrderCreateDates"] = CapList(k.SelectedOrderCreateDates, ListCap),
                ["note"] =
                    "slotAdherence and appointmentsByDateFiltered reflect these dashboard filters. dataset.appointmentsByDateInFile is the full uploaded CSV (unfiltered by dashboard date)."
            },
            ["slotAdherence"] = slotAdherence,
            ["totals"] = slotAdherence,
            ["statusDistributionTop"] = TopCounts(k.StatusDistribution, DistTopN),
            ["subStatusDistributionTop"] = TopCounts(k.SubStatusDistribution, DistTopN),
            ["territoryDistributionTop"] = TopCounts(k.TerritoryDistribution, DistTopN),
            ["skillsetDistributionTop"] = TopCounts(k.SkillsetDistribution, DistTopN),
            ["appointmentsByDateFiltered"] = TopCounts(k.AppointmentsByDate, DateSeriesTopN),
            ["appointmentsByDateTop"] = TopCounts(k.AppointmentsByDate, DateSeriesTopN),
            ["slotAdherenceByDate"] = MapSlotAdherenceByDate(k),
            ["slotAdherenceByDateNote"] =
                "Daily scheduled/pass/fail for active dashboard filters — same rules as the slot adherence chart (Pass/Fail from complianceRules). " +
                "For 'how many passed on March 5', use the pass value for that yyyy-MM-dd date here, or queryResults with compliance=Pass and appointmentDate (no skillset/slot unless the user asked for them).",
            ["complianceFailReasonsTop"] = TopCounts(k.ComplianceFailReasons, DistTopN),
            ["topDelayReasons"] = k.TopDelayReasons.Take(20).Select(p => new Dictionary<string, object?>
            {
                ["reason"] = p.Key,
                ["count"] = p.Value
            }).ToList(),
            ["skillsetBySlot"] = k.SkillsetBySlot,
            ["skillsetBySlotNote"] =
                "AM/PM appointment counts per skillset for active dashboard filters only. For Pass/Fail by AM/PM on a specific date, use queryResults with compliance filter and groupBy slot.",
            ["complianceRules"] = k.ComplianceMetricsAvailable
                ? ReportComplianceRulesReference.ForAssistantContext(
                    ReportComplianceRulesReference.AmSlotMarkerDefault,
                    13,
                    24)
                : null,
            ["csvCatalog"] = csvCatalog is null ? null : MapCsvCatalog(csvCatalog),
            ["complianceBySlot"] = k.ComplianceMetricsAvailable
                ? new Dictionary<string, object?>
                {
                    ["forActiveDashboardFilters"] = true,
                    ["pass"] = new Dictionary<string, object?>
                    {
                        ["am"] = k.CompliancePassAmCount,
                        ["pm"] = k.CompliancePassPmCount,
                        ["total"] = k.CompliancePassCount
                    },
                    ["fail"] = new Dictionary<string, object?>
                    {
                        ["am"] = k.ComplianceFailAmCount,
                        ["pm"] = k.ComplianceFailPmCount,
                        ["total"] = k.ComplianceFailCount
                    },
                    ["na"] = k.ComplianceNaCount,
                    ["passRatePercent"] = k.CompliancePassCount + k.ComplianceFailCount > 0
                        ? Math.Round(
                            (double)k.CompliancePassCount / (k.CompliancePassCount + k.ComplianceFailCount) * 100,
                            1)
                        : (double?)null,
                    ["passRateFormula"] = "Pass / (Pass + Fail); N/A excluded"
                }
                : null,
            ["queryableFields"] = new[]
            {
                "appointmentDate (yyyy-MM-dd)", "compliance (Pass/Fail/N/A)", "orderCreateDate", "skillset", "status",
                "subStatus", "territory", "AM/PM slot", "customeraddress", "facilityname", "appointmentid", "workordernumber"
            },
            ["previewRowsSample"] = k.PreviewRows.Take(PreviewRowCap).Select(row =>
            {
                var take = row.Take(PreviewColCap).ToDictionary(e => e.Key, e => e.Value);
                return (object)take;
            }).ToList()
        };
    }

    private static Dictionary<string, object?> MapCsvCatalog(KpiCsvAssistantCatalog catalog)
    {
        var profiles = catalog.ColumnProfiles
            .Take(ColumnCatalogCap)
            .Select(p => new Dictionary<string, object?>
            {
                ["column"] = p.Name,
                ["nonEmptyRows"] = p.NonEmptyRows,
                ["distinctValues"] = p.DistinctValues,
                ["distinctValuesCapped"] = p.DistinctValuesCapped,
                ["topValues"] = p.TopValues.Select(kv => new Dictionary<string, object?>
                {
                    ["value"] = kv.Key.Length > 80 ? kv.Key[..80] + "…" : kv.Key,
                    ["count"] = kv.Value
                }).ToList()
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["totalColumns"] = catalog.AllColumnNames.Count,
            ["allColumnNames"] = catalog.AllColumnNames.Count <= 220
                ? catalog.AllColumnNames
                : CapList(catalog.AllColumnNames, 220),
            ["columnProfiles"] = profiles,
            ["note"] =
                "Full KPI CSV schema for this upload. Use columnProfiles topValues for distinct field values; " +
                "use row queries (queryResults) for counts filtered by any column via columnContains or named filters (team, delayCode, technology, etc.)."
        };
    }

    private static List<Dictionary<string, object?>> MapSlotAdherenceByDate(KpiDashboardViewModel k)
    {
        if (!k.ComplianceMetricsAvailable || k.SlotAdherenceByDate.Count == 0)
            return [];

        return k.SlotAdherenceByDate
            .OrderByDescending(kv => kv.Key, StringComparer.Ordinal)
            .Take(DateSeriesTopN)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new Dictionary<string, object?>
            {
                ["date"] = kv.Key,
                ["scheduled"] = kv.Value.Scheduled,
                ["pass"] = kv.Value.Pass,
                ["fail"] = kv.Value.Fail
            })
            .ToList();
    }

    private static Dictionary<string, object?> BuildSlotAdherenceTotals(KpiDashboardViewModel k) =>
        new()
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
        };

    private static string DescribeActiveFilters(KpiDashboardViewModel k)
    {
        var parts = new List<string> { $"view={k.ActiveDashboardView}", $"csvKind={k.CsvSourceKind}" };

        parts.Add(k.DateFilterMode switch
        {
            "single" when !string.IsNullOrWhiteSpace(k.SelectedDate) => $"appointment date = {k.SelectedDate}",
            "range" when !string.IsNullOrWhiteSpace(k.DateRangeStart) && !string.IsNullOrWhiteSpace(k.DateRangeEnd)
                => $"appointment dates {k.DateRangeStart} to {k.DateRangeEnd}",
            _ => "all appointment dates in file"
        });

        if (k.SelectedTerritories.Count > 0)
            parts.Add($"territories: {string.Join(", ", k.SelectedTerritories.Take(8))}{(k.SelectedTerritories.Count > 8 ? "…" : "")}");
        if (k.SelectedStatuses.Count > 0)
            parts.Add($"statuses: {string.Join(", ", k.SelectedStatuses.Take(8))}");
        if (k.SelectedSubStatuses.Count > 0)
            parts.Add($"sub-statuses: {string.Join(", ", k.SelectedSubStatuses.Take(8))}");
        if (k.SelectedSkillsets.Count > 0)
            parts.Add($"skillsets: {string.Join(", ", k.SelectedSkillsets.Take(8))}");
        if (k.SelectedCustomerTypes.Count > 0)
            parts.Add($"customer types: {string.Join(", ", k.SelectedCustomerTypes.Take(8))}");
        if (k.SelectedOrderCreateDates.Count > 0)
            parts.Add($"order create dates: {string.Join(", ", k.SelectedOrderCreateDates.Take(5))}");

        return string.Join("; ", parts);
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
            AvailableCustomerTypes = session.CachedAvailableCustomerTypes ?? [],
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
