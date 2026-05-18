using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public sealed class ReportRecurringQueryService : IReportRecurringQueryService
{
    private readonly AppDbContext _db;
    private readonly IReportSessionStore _sessionStore;
    private readonly ICsvProcessingService _csvService;
    private readonly IMemoryCache _cache;

    public ReportRecurringQueryService(
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

    public async Task<ReportRecurringQueryResult> ExecuteAsync(
        Guid userId,
        string token,
        ReportRecurringQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await UserCanAccessKpiTokenAsync(userId, token, cancellationToken))
        {
            return new ReportRecurringQueryResult
            {
                Ran = false,
                Note = "This report is not available for your account."
            };
        }

        if (!_sessionStore.TryGetCsvPath(token, out var csvPath))
        {
            return new ReportRecurringQueryResult
            {
                Ran = false,
                Note =
                    "This saved dashboard is read-only and the original CSV is no longer on disk. Upload a fresh KPI file for recurring-ticket questions."
            };
        }

        var cacheKey = BuildCacheKey(userId, token, request);
        if (_cache.TryGetValue(cacheKey, out ReportRecurringQueryResult? cached) && cached is not null)
            return cached;

        var result = await _csvService.QueryRecurringTicketsAsync(
            csvPath,
            request.Filters,
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

    private static string BuildCacheKey(Guid userId, string token, ReportRecurringQueryRequest request)
    {
        var payload = JsonSerializer.Serialize(new { token, request.Filters, request.GroupBy, request.MaxSampleRows });
        return $"rrq:{userId:N}:{payload.GetHashCode(StringComparison.Ordinal):x8}";
    }

    private async Task<bool> UserCanAccessKpiTokenAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        if (await _db.ReportUploads.AnyAsync(u => u.Token == token && u.UserId == userId, cancellationToken))
            return true;
        return await _db.ReportDashboardArchives.AnyAsync(a => a.Token == token && a.UserId == userId, cancellationToken);
    }
}
