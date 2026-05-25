using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;
using SlotAd_Globe.Options;

namespace SlotAd_Globe.Services;

public class DatabaseReportSessionStore : IReportSessionStore
{
    private const string SourceFileName = "source.csv";
    private const int CopyBufferBytes = 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IWebHostEnvironment _env;
    private readonly ReportSessionOptions _options;
    private readonly ILogger<DatabaseReportSessionStore> _logger;
    private readonly IReportDashboardArchiveRecorder _archiveRecorder;

    public DatabaseReportSessionStore(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        IWebHostEnvironment env,
        IOptions<ReportSessionOptions> options,
        IReportDashboardArchiveRecorder archiveRecorder,
        ILogger<DatabaseReportSessionStore> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _env = env;
        _options = options.Value;
        _archiveRecorder = archiveRecorder;
        _logger = logger;
    }

    /// <summary>Prefer LocalApplicationData over project folder — OneDrive sync can throttle writes and stall the HTTP upload.</summary>
    private string MaterializeRoot => ReportMaterializePathHelper.GetMaterializeRoot(_env, _options);

    public bool IsValidTokenFormat(string token) =>
        !string.IsNullOrEmpty(token)
        && token.Length == 64
        && token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private Guid? TryGetCurrentUserId()
    {
        var id = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var g) ? g : null;
    }

    private Guid RequireUserId()
    {
        var g = TryGetCurrentUserId();
        if (!g.HasValue)
            throw new InvalidOperationException("Authenticated user required.");
        return g.Value;
    }

    public async Task<string> CreateSessionFromCsvAsync(Stream csvStream, string? originalFileName = null, CancellationToken cancellationToken = default)
    {
        var userId = RequireUserId();
        var token = CreateToken();
        var now = DateTime.UtcNow;

        // Stream to disk first. Do not duplicate into SQL (varbinary) — large inserts block the pipeline and are unnecessary when the file is on disk.
        // XLSX uploads are transparently converted to CSV here so that all downstream processing stays format-agnostic.
        var dir = Path.Combine(MaterializeRoot, token);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SourceFileName);
        var isXlsx = originalFileName?.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ?? false;
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (isXlsx)
                await ConvertXlsxToCsvAsync(csvStream, fs, cancellationToken);
            else
                await csvStream.CopyToAsync(fs, CopyBufferBytes, cancellationToken);
        }

        var sessionData = new ReportSessionData { CreatedUtc = now };
        var json = JsonSerializer.Serialize(sessionData, ReportSessionJson.Options);

        _db.ReportUploads.Add(new ReportUploadEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = token,
            OriginalFileName = string.IsNullOrWhiteSpace(originalFileName) ? null : originalFileName.Trim(),
            CsvSourceKind = CsvSourceKind.Pending,
            CsvContent = null,
            SessionJson = json,
            UploadedUtc = now
        });

        await _db.SaveChangesAsync(cancellationToken);
        await EnforceGlobalFifoForKindAsync(CsvSourceKind.Pending, cancellationToken);
        _logger.LogInformation("Created report session {Token} for user {UserId}", token, userId);
        return token;
    }

    public bool TryGetCsvPath(string token, out string csvPath)
    {
        csvPath = "";
        if (!IsValidTokenFormat(token))
            return false;

        var userId = TryGetCurrentUserId();
        if (!userId.HasValue)
            return false;

        var entity = _db.ReportUploads.AsNoTracking()
            .FirstOrDefault(r => r.Token == token && r.UserId == userId.Value);
        if (entity is null)
            return false;

        var dir = Path.Combine(MaterializeRoot, token);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SourceFileName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (entity.CsvContent is not { Length: > 0 })
            {
                _logger.LogWarning("CSV missing on disk and no DB blob for token {Token}", token);
                return false;
            }

            File.WriteAllBytes(path, entity.CsvContent);
            _logger.LogDebug("Materialized CSV for token {Token}", token);
        }

        csvPath = path;
        return true;
    }

    public async Task<ReportSessionData?> LoadAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(token))
            return null;

        var userId = TryGetCurrentUserId();
        if (!userId.HasValue)
            return null;

        var entity = await _db.ReportUploads.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Token == token && r.UserId == userId.Value, cancellationToken);
        if (entity is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ReportSessionData>(entity.SessionJson, ReportSessionJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid session JSON for token {Token}", token);
            return null;
        }
    }

    public async Task SaveFiltersAsync(string token, FilterOptionsViewModel filters, CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(token))
            throw new ArgumentException("Invalid token", nameof(token));

        var userId = RequireUserId();
        var entity = await _db.ReportUploads.FirstOrDefaultAsync(
            r => r.Token == token && r.UserId == userId,
            cancellationToken);
        if (entity is null)
            throw new DirectoryNotFoundException($"Session not found: {token}");

        var existing = JsonSerializer.Deserialize<ReportSessionData>(entity.SessionJson, ReportSessionJson.Options)
                       ?? new ReportSessionData { CreatedUtc = DateTime.UtcNow };

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

        var activeView = string.Equals(filters.ActiveDashboardView, "status", StringComparison.OrdinalIgnoreCase)
            ? "status"
            : "pending";
        if (activeView == "status")
        {
            existing.HasStatusFilters = true;
            existing.StatusDateFilterMode = existing.DateFilterMode;
            existing.StatusSelectedDate = existing.SelectedDate;
            existing.StatusDateRangeStart = existing.DateRangeStart;
            existing.StatusDateRangeEnd = existing.DateRangeEnd;
            existing.StatusSelectedTerritories = existing.SelectedTerritories.ToList();
            existing.StatusSelectedStatuses = existing.SelectedStatuses.ToList();
            existing.StatusSelectedSubStatuses = existing.SelectedSubStatuses.ToList();
            existing.StatusSelectedSkillsets = existing.SelectedSkillsets.ToList();
            existing.StatusSelectedCustomerTypes = existing.SelectedCustomerTypes.ToList();
            existing.StatusSelectedOrderCreateDates = existing.SelectedOrderCreateDates.ToList();
        }
        else
        {
            existing.HasPendingFilters = true;
            existing.PendingDateFilterMode = existing.DateFilterMode;
            existing.PendingSelectedDate = existing.SelectedDate;
            existing.PendingDateRangeStart = existing.DateRangeStart;
            existing.PendingDateRangeEnd = existing.DateRangeEnd;
            existing.PendingSelectedTerritories = existing.SelectedTerritories.ToList();
            existing.PendingSelectedStatuses = existing.SelectedStatuses.ToList();
            existing.PendingSelectedSubStatuses = existing.SelectedSubStatuses.ToList();
            existing.PendingSelectedSkillsets = existing.SelectedSkillsets.ToList();
            existing.PendingSelectedCustomerTypes = existing.SelectedCustomerTypes.ToList();
            existing.PendingSelectedOrderCreateDates = existing.SelectedOrderCreateDates.ToList();
        }

        existing.CachedAvailableDates = filters.AvailableDates?.ToList();
        existing.CachedAvailableTerritories = filters.AvailableTerritories?.ToList();
        existing.CachedAvailableStatuses = filters.AvailableStatuses?.ToList();
        existing.CachedAvailableSubStatuses = filters.AvailableSubStatuses?.ToList();
        existing.CachedAvailableSkillsets = filters.AvailableSkillsets?.ToList();
        existing.CachedAvailableCustomerTypes = filters.AvailableCustomerTypes?.ToList();
        existing.CachedAvailableOrderCreateDates = filters.AvailableOrderCreateDates?.ToList();

        entity.SessionJson = JsonSerializer.Serialize(existing, ReportSessionJson.Options);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCsvSourceKindAsync(string token, CsvSourceKind kind, CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(token))
            throw new ArgumentException("Invalid token", nameof(token));

        var userId = RequireUserId();
        var entity = await _db.ReportUploads.FirstOrDefaultAsync(
            r => r.Token == token && r.UserId == userId,
            cancellationToken);
        if (entity is null)
            throw new DirectoryNotFoundException($"Session not found: {token}");

        var existing = JsonSerializer.Deserialize<ReportSessionData>(entity.SessionJson, ReportSessionJson.Options)
                       ?? new ReportSessionData { CreatedUtc = DateTime.UtcNow };
        existing.CsvSourceKind = kind;
        entity.CsvSourceKind = kind;
        entity.SessionJson = JsonSerializer.Serialize(existing, ReportSessionJson.Options);
        await _db.SaveChangesAsync(cancellationToken);
        await EnforceGlobalFifoForKindAsync(kind, cancellationToken);
    }

    /// <summary>
    /// Keeps at most <see cref="ReportSessionOptions.MaxCsvUploadsPerKindGlobal"/> uploads per kind globally;
    /// removes oldest (by <see cref="SlotAd_Globe.Data.ReportUploadEntity.UploadedUtc"/>).
    /// </summary>
    private async Task EnforceGlobalFifoForKindAsync(CsvSourceKind kind, CancellationToken cancellationToken = default)
    {
        var max = _options.MaxCsvUploadsPerKindGlobal;
        if (max <= 0)
            return;

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var victims = await _db.ReportUploads
                .Where(r => r.CsvSourceKind == kind)
                .OrderByDescending(r => r.UploadedUtc)
                .Skip(max)
                .ToListAsync(cancellationToken);

            if (victims.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }

            var victimIds = victims.Select(v => v.Id).ToHashSet();
            var usersToClear = await _db.Users
                .Where(u => u.LastReportUploadId != null && victimIds.Contains(u.LastReportUploadId.Value))
                .ToListAsync(cancellationToken);
            foreach (var u in usersToClear)
                u.LastReportUploadId = null;

            foreach (var v in victims)
            {
                await _archiveRecorder.RecordSnapshotBeforeEvictionAsync(_db, v, MaterializeRoot, cancellationToken);
                TryDeleteMaterializedDir(v.Token);
                _db.ReportUploads.Remove(v);
                _logger.LogInformation(
                    "Removed report upload {Token} (global FIFO cap for kind {Kind})",
                    v.Token,
                    kind);
            }

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    public void CleanupExpiredSessions()
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        var expired = _db.ReportUploads.Where(r => r.UploadedUtc < cutoff).ToList();
        if (expired.Count == 0)
            return;

        var expiredIds = expired.Select(e => e.Id).ToHashSet();
        var usersToClear = _db.Users.Where(u => u.LastReportUploadId != null && expiredIds.Contains(u.LastReportUploadId.Value)).ToList();
        foreach (var u in usersToClear)
            u.LastReportUploadId = null;

        foreach (var e in expired)
        {
            TryDeleteMaterializedDir(e.Token);
            _db.ReportUploads.Remove(e);
            _logger.LogInformation("Removed expired report upload {Token}", e.Token);
        }

        var expiredArchives = _db.ReportDashboardArchives.Where(a => a.UploadedUtc < cutoff).ToList();
        foreach (var a in expiredArchives)
        {
            _db.ReportDashboardArchives.Remove(a);
            _logger.LogInformation("Removed expired report dashboard archive {Token}", a.Token);
        }

        _db.SaveChanges();
    }

    public async Task DeleteAllReportHistoryForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var uploads = await _db.ReportUploads
            .Where(r => r.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var u in uploads)
        {
            TryDeleteMaterializedDir(u.Token);
            _db.ReportUploads.Remove(u);
        }

        var archives = await _db.ReportDashboardArchives
            .Where(a => a.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var a in archives)
        {
            TryDeleteMaterializedDir(a.Token);
            _db.ReportDashboardArchives.Remove(a);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is not null)
            user.LastReportUploadId = null;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted all report history for user {UserId}", userId);
    }

    private void TryDeleteMaterializedDir(string token)
    {
        try
        {
            var dir = Path.Combine(MaterializeRoot, token);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete materialize dir for {Token}", token);
        }
    }

    /// <summary>
    /// Converts an XLSX stream to UTF-8 CSV written into <paramref name="csvOutputStream"/>.
    /// Selects the worksheet with the most rows so that summary/filter sheets that Excel
    /// sometimes prepends (e.g. a 3-row "Sheet3" before a 3000-row data sheet) are skipped.
    /// ClosedXML is CPU-bound so the workbook read is offloaded to the thread pool via Task.Run.
    /// </summary>
    private async Task ConvertXlsxToCsvAsync(Stream xlsxStream, Stream csvOutputStream, CancellationToken cancellationToken)
    {
        // ClosedXML requires a seekable stream; buffer into MemoryStream when necessary.
        MemoryStream? tempBuffer = null;
        if (!xlsxStream.CanSeek)
        {
            tempBuffer = new MemoryStream();
            await xlsxStream.CopyToAsync(tempBuffer, cancellationToken);
            tempBuffer.Position = 0;
        }

        var sourceStream = tempBuffer ?? xlsxStream;

        // Use UTF-8 without BOM so CsvHelper downstream reads cleanly.
        await using var writer = new StreamWriter(csvOutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);

        await Task.Run(() =>
        {
            using var workbook = new XLWorkbook(sourceStream);

            // Pick the worksheet that has the most data rows. XLSX exports from reporting
            // tools often include a small pivot/filter sheet before the main data sheet;
            // taking First() would silently select the wrong (empty) sheet.
            var ws = workbook.Worksheets
                .OrderByDescending(s => s.LastRowUsed()?.RowNumber() ?? 0)
                .First();

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow == 0 || lastCol == 0)
            {
                _logger.LogWarning("XLSX conversion: selected sheet '{Sheet}' appears empty ({Sheets} sheets total)", ws.Name, workbook.Worksheets.Count);
                return;
            }

            _logger.LogInformation("XLSX conversion: using sheet '{Sheet}' ({Rows} rows × {Cols} cols)", ws.Name, lastRow, lastCol);

            var sb = new StringBuilder();
            for (var r = 1; r <= lastRow; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.Clear();
                var xlRow = ws.Row(r);
                for (var c = 1; c <= lastCol; c++)
                {
                    if (c > 1) sb.Append(',');
                    sb.Append(EscapeCsvField(GetCellStringValue(xlRow.Cell(c))));
                }
                writer.WriteLine(sb.ToString());
            }
        }, cancellationToken);

        await writer.FlushAsync(cancellationToken);

        if (tempBuffer is not null)
            await tempBuffer.DisposeAsync();
    }

    /// <summary>
    /// Returns a CSV-safe string for a cell, handling each ClosedXML data type explicitly
    /// to avoid scientific-notation output for large integers and locale-specific date strings.
    /// </summary>
    private static string GetCellStringValue(IXLCell cell)
    {
        var val = cell.Value;
        return val.Type switch
        {
            XLDataType.Blank => string.Empty,
            XLDataType.Boolean => val.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.Number => FormatXlsxNumber(val.GetNumber()),
            XLDataType.Text => val.GetText(),
            XLDataType.DateTime => val.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => val.GetTimeSpan().ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Formats a number without scientific notation. Integer-valued doubles (e.g. large IDs)
    /// are cast to long so they render as "9180000000000000000" rather than "9.18E+18".
    /// </summary>
    private static string FormatXlsxNumber(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
            return string.Empty;

        if (number == Math.Floor(number)
            && number >= long.MinValue
            && number < (double)long.MaxValue)
        {
            return ((long)number).ToString(CultureInfo.InvariantCulture);
        }

        return number.ToString("G15", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string CreateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
