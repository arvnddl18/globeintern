using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlotAd_Globe.Models;
using SlotAd_Globe.Options;

namespace SlotAd_Globe.Services;

public class ReportSessionStore : IReportSessionStore
{
    private const string SourceFileName = "source.csv";
    private const string SessionFileName = "session.json";
    private readonly IWebHostEnvironment _env;
    private readonly ReportSessionOptions _options;
    private readonly ILogger<ReportSessionStore> _logger;

    public ReportSessionStore(
        IWebHostEnvironment env,
        IOptions<ReportSessionOptions> options,
        ILogger<ReportSessionStore> logger)
    {
        _env = env;
        _options = options.Value;
        _logger = logger;
    }

    private string RootPath => Path.Combine(_env.ContentRootPath, _options.ReportsDirectory);

    public bool IsValidTokenFormat(string token) =>
        !string.IsNullOrEmpty(token)
        && token.Length == 64
        && token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    public async Task<string> CreateSessionFromCsvAsync(Stream csvStream, string? originalFileName = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootPath);

        var token = CreateToken();
        var dir = Path.Combine(RootPath, token);
        Directory.CreateDirectory(dir);

        var csvPath = Path.Combine(dir, SourceFileName);
        await using (var fs = new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            await csvStream.CopyToAsync(fs, cancellationToken);
        }

        var data = new ReportSessionData { CreatedUtc = DateTime.UtcNow };
        await WriteSessionAsync(dir, data, cancellationToken);

        _logger.LogInformation("Created report session {Token}", token);
        return token;
    }

    public bool TryGetCsvPath(string token, out string csvPath)
    {
        csvPath = "";
        if (!IsValidTokenFormat(token))
            return false;

        var path = Path.Combine(RootPath, token, SourceFileName);
        if (!File.Exists(path))
            return false;

        csvPath = path;
        return true;
    }

    public async Task<ReportSessionData?> LoadAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(token))
            return null;

        var dir = Path.Combine(RootPath, token);
        var sessionPath = Path.Combine(dir, SessionFileName);
        if (!File.Exists(sessionPath) || !File.Exists(Path.Combine(dir, SourceFileName)))
            return null;

        await using var fs = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<ReportSessionData>(fs, ReportSessionJson.Options, cancellationToken);
    }

    public async Task SaveFiltersAsync(string token, FilterOptionsViewModel filters, CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(token))
            throw new ArgumentException("Invalid token", nameof(token));

        var dir = Path.Combine(RootPath, token);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Session not found: {token}");

        var existing = await LoadAsync(token, cancellationToken) ?? new ReportSessionData { CreatedUtc = DateTime.UtcNow };

        existing.DateFilterMode = filters.DateFilterMode ?? "all";
        existing.SelectedDate = filters.SelectedDate;
        existing.DateRangeStart = filters.DateRangeStart;
        existing.DateRangeEnd = filters.DateRangeEnd;
        existing.SelectedTerritories = filters.SelectedTerritories?.ToList() ?? [];
        existing.SelectedStatuses = filters.SelectedStatuses?.ToList() ?? [];
        existing.SelectedSubStatuses = filters.SelectedSubStatuses?.ToList() ?? [];
        existing.SelectedSkillsets = filters.SelectedSkillsets?.ToList() ?? [];
        existing.SelectedCustomerTypes = filters.SelectedCustomerTypes?.ToList() ?? [];
        existing.SelectedOrderCreateDates = filters.SelectedOrderCreateDates?.ToList() ?? [];

        existing.CachedAvailableDates = filters.AvailableDates?.ToList();
        existing.CachedAvailableTerritories = filters.AvailableTerritories?.ToList();
        existing.CachedAvailableStatuses = filters.AvailableStatuses?.ToList();
        existing.CachedAvailableSubStatuses = filters.AvailableSubStatuses?.ToList();
        existing.CachedAvailableSkillsets = filters.AvailableSkillsets?.ToList();
        existing.CachedAvailableCustomerTypes = filters.AvailableCustomerTypes?.ToList();
        existing.CachedAvailableOrderCreateDates = filters.AvailableOrderCreateDates?.ToList();

        await WriteSessionAsync(dir, existing, cancellationToken);
    }

    public async Task SetCsvSourceKindAsync(string token, CsvSourceKind kind, CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(token))
            throw new ArgumentException("Invalid token", nameof(token));

        var dir = Path.Combine(RootPath, token);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Session not found: {token}");

        var existing = await LoadAsync(token, cancellationToken) ?? new ReportSessionData { CreatedUtc = DateTime.UtcNow };
        existing.CsvSourceKind = kind;
        await WriteSessionAsync(dir, existing, cancellationToken);
    }

    public Task DeleteAllReportHistoryForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "DeleteAllReportHistoryForUserAsync({UserId}) is a no-op for file-backed ReportSessionStore",
            userId);
        return Task.CompletedTask;
    }

    public void CleanupExpiredSessions()
    {
        if (!Directory.Exists(RootPath))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        foreach (var dir in Directory.GetDirectories(RootPath))
        {
            try
            {
                var sessionPath = Path.Combine(dir, SessionFileName);
                if (!File.Exists(sessionPath))
                    continue;

                var json = File.ReadAllText(sessionPath);
                var data = JsonSerializer.Deserialize<ReportSessionData>(json, ReportSessionJson.Options);
                if (data is null || data.CreatedUtc >= cutoff)
                    continue;

                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Removed expired report session directory {Dir}", Path.GetFileName(dir));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup skip for {Dir}", dir);
            }
        }
    }

    private async Task WriteSessionAsync(string sessionDir, ReportSessionData data, CancellationToken cancellationToken)
    {
        var sessionPath = Path.Combine(sessionDir, SessionFileName);
        await using var fs = new FileStream(sessionPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fs, data, ReportSessionJson.Options, cancellationToken);
    }

    private static string CreateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
