using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public sealed class ReportCsvQueryService : IReportCsvQueryService
{
    private readonly AppDbContext _db;
    private readonly IReportSessionStore _sessionStore;
    private readonly ICsvProcessingService _csvService;
    private readonly IMemoryCache _cache;

    public ReportCsvQueryService(
        AppDbContext db,
        IReportSessionStore sessionStore,
        ICsvProcessingService csvService,
        IMemoryCache cache)
    {
        _db = db;
        _sessionStore = sessionStore;
        _csvService = csvService;
        _cache = cache;
    }

    public async Task<ReportCsvQueryResult> ExecuteAsync(
        Guid userId,
        string token,
        string? view,
        ReportCsvQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await UserCanAccessKpiTokenAsync(userId, token, cancellationToken))
        {
            return new ReportCsvQueryResult
            {
                Ran = false,
                Note = "This report is not available for your account."
            };
        }

        if (!_sessionStore.TryGetCsvPath(token, out var csvPath))
        {
            return new ReportCsvQueryResult
            {
                Ran = false,
                Note =
                    "This saved dashboard is read-only and the original CSV is no longer on disk. Upload a fresh KPI file for row-level questions."
            };
        }

        var session = await _sessionStore.LoadAsync(token, cancellationToken);
        if (session is null)
        {
            return new ReportCsvQueryResult
            {
                Ran = false,
                Note = "Report session is incomplete."
            };
        }

        var activeView = !string.IsNullOrWhiteSpace(view)
            ? (string.Equals(view, "status", StringComparison.OrdinalIgnoreCase) ? "status" : "pending")
            : session.CsvSourceKind == CsvSourceKind.AllStatus ? "status" : "pending";

        var activeFilters = ReportSessionFilterResolver.GetSessionFiltersForView(session, activeView);
        var filterOptions = await GetFilterOptionsForDashboardAsync(token, csvPath, session, cancellationToken);
        var territoryFilters = ResolveTerritoryFiltersForKpi(
            session.CsvSourceKind,
            activeFilters.SelectedTerritories,
            filterOptions.AvailableTerritories);

        var sessionParams = new ReportCsvSessionFilterParams
        {
            DateFilterMode = activeFilters.DateFilterMode,
            SelectedDate = ReportSessionDate.ParseDateOrNull(activeFilters.SelectedDate),
            DateRangeStart = ReportSessionDate.ParseDateOrNull(activeFilters.DateRangeStart),
            DateRangeEnd = ReportSessionDate.ParseDateOrNull(activeFilters.DateRangeEnd),
            SelectedTerritories = territoryFilters,
            SelectedStatuses = activeFilters.SelectedStatuses,
            SelectedSubStatuses = activeFilters.SelectedSubStatuses,
            SelectedSkillsets = activeFilters.SelectedSkillsets,
            SelectedCustomerTypes = activeFilters.SelectedCustomerTypes,
            SelectedOrderCreateDates = activeFilters.SelectedOrderCreateDates
        };

        var cacheKey = BuildCacheKey(userId, token, activeView, sessionParams, request);
        if (_cache.TryGetValue(cacheKey, out ReportCsvQueryResult? cached) && cached is not null)
            return cached;

        var result = await _csvService.QueryKpiCsvAsync(
            csvPath,
            sessionParams,
            request.ExtraFilters,
            request.GroupBy,
            request.MaxSampleRows,
            request.InterpretedAs,
            cancellationToken);

        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
        });

        return result;
    }

    private static string BuildCacheKey(
        Guid userId,
        string token,
        string activeView,
        ReportCsvSessionFilterParams sessionParams,
        ReportCsvQueryRequest request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            token,
            activeView,
            sessionParams,
            request.ExtraFilters,
            request.GroupBy,
            request.MaxSampleRows
        });
        return $"rcq:{userId:N}:{payload.GetHashCode(StringComparison.Ordinal):x8}";
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
        CancellationToken cancellationToken)
    {
        if (session.CachedAvailableDates is { Count: > 0 })
        {
            return new FilterOptionsViewModel
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
        }

        await using var optStream = new FileStream(
            csvPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await _csvService.ExtractFilterOptionsAsync(optStream, token, cancellationToken);
    }

    private static List<string> ResolveTerritoryFiltersForKpi(
        CsvSourceKind sourceKind,
        IReadOnlyList<string> sessionTerritories,
        IReadOnlyList<string> availableTerritories)
    {
        if (sourceKind != CsvSourceKind.AllStatus)
            return [.. sessionTerritories];

        foreach (var a in availableTerritories)
        {
            if (string.Equals(a, "Davao North", StringComparison.OrdinalIgnoreCase))
                return [a];
        }

        return [.. sessionTerritories];
    }
}
