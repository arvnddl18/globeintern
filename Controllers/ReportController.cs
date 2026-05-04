using System.Globalization;
using System.Security.Claims;
using CsvHelper;
using CsvHelper.Configuration;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;
using SlotAd_Globe.Options;
using SlotAd_Globe.Services;

namespace SlotAd_Globe.Controllers;

[Authorize]
[Route("[controller]")]
public class ReportController : Controller
{
    private readonly ICsvProcessingService _csvService;
    private readonly IReportSessionStore _sessionStore;
    private readonly IOperationalReportService _operationalService;
    private readonly AppDbContext _db;
    private readonly ILogger<ReportController> _logger;
    private readonly ReportSessionOptions _sessionOptions;
    private readonly IWebHostEnvironment _hostEnv;
    private readonly IConfiguration _configuration;

    public ReportController(
        ICsvProcessingService csvService,
        IReportSessionStore sessionStore,
        IOperationalReportService operationalService,
        AppDbContext db,
        ILogger<ReportController> logger,
        IOptions<ReportSessionOptions> sessionOptions,
        IWebHostEnvironment hostEnv,
        IConfiguration configuration)
    {
        _csvService = csvService;
        _sessionStore = sessionStore;
        _operationalService = operationalService;
        _db = db;
        _logger = logger;
        _sessionOptions = sessionOptions.Value;
        _hostEnv = hostEnv;
        _configuration = configuration;
    }

    // #region agent log
    private void AgentDebugLog(string hypothesisId, string location, string message, object? data = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                sessionId = "22a3ab",
                hypothesisId,
                location,
                message,
                data,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            System.IO.File.AppendAllText(
                Path.Combine(_hostEnv.ContentRootPath, "debug-22a3ab.log"),
                line + "\n");
        }
        catch
        {
            /* ignore debug log failures */
        }
    }
    // #endregion

    [HttpGet("[action]")]
    public IActionResult Upload()
    {
        _sessionStore.CleanupExpiredSessions();
        return View(new CsvUploadViewModel());
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadRawDataCleaner(CsvUploadViewModel model)
    {
        if (!ModelState.IsValid)
            return View("Upload", model);

        if (model.CsvFile is null || model.CsvFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.CsvFile), "The uploaded file is empty.");
            return View("Upload", model);
        }

        try
        {
            await using var uploadStream = model.CsvFile.OpenReadStream();
            var summary = await _csvService.CleanAndAppendRawDataAsync(uploadStream, HttpContext.RequestAborted);
            
            TempData["CleanDataSummary"] = JsonSerializer.Serialize(summary);

            return RedirectToAction(nameof(CleanedDataExport));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Raw Data Cleaner upload");
            ModelState.AddModelError(string.Empty, $"Error processing CSV: {ex.Message}");
            return View("Upload", model);
        }
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    public IActionResult ClearStoredData()
    {
        try
        {
            var cleanedDataDir = Path.Combine(_configuration.GetValue<string>("ReportSessions:ReportsDirectory") ?? "App_Data/reports");
            var cleanedDataPath = Path.Combine(cleanedDataDir, "CleanedDataMaster.csv");
            if (System.IO.File.Exists(cleanedDataPath))
            {
                System.IO.File.Delete(cleanedDataPath);
                TempData["Success"] = "Stored master data has been cleared successfully. You can now upload from a clean state.";
            }
            else
            {
                TempData["Success"] = "No stored master data found. You are already at a clean state.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing stored data");
            TempData["Error"] = $"Error clearing data: {ex.Message}";
        }
        return RedirectToAction(nameof(Upload));
    }

    [HttpGet("[action]")]
    public IActionResult CleanedDataExport()
    {
        if (TempData["CleanDataSummary"] is string summaryJson)
        {
            var summary = JsonSerializer.Deserialize<CleanedDataSummary>(summaryJson);
            return View(summary);
        }
        return View(new CleanedDataSummary());
    }

    [HttpGet("[action]")]
    public IActionResult DownloadCleanedDataCsv()
    {
        var cleanedDataDir = Path.Combine(_configuration.GetValue<string>("ReportSessions:ReportsDirectory") ?? "App_Data/reports");
        var cleanedDataPath = Path.Combine(cleanedDataDir, "CleanedDataMaster.csv");

        if (!System.IO.File.Exists(cleanedDataPath))
        {
            TempData["Error"] = "No cleaned data available.";
            return RedirectToAction(nameof(CleanedDataExport));
        }

        var fs = new FileStream(cleanedDataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return File(fs, "text/csv", "CleanedDataMaster.csv");
    }

    [HttpGet("[action]")]
    public async Task<IActionResult> DownloadCleanedDataXlsx()
    {
        var cleanedDataDir = Path.Combine(_configuration.GetValue<string>("ReportSessions:ReportsDirectory") ?? "App_Data/reports");
        var cleanedDataPath = Path.Combine(cleanedDataDir, "CleanedDataMaster.csv");

        if (!System.IO.File.Exists(cleanedDataPath))
        {
            TempData["Error"] = "No cleaned data available.";
            return RedirectToAction(nameof(CleanedDataExport));
        }

        using var fs = new FileStream(cleanedDataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        using var csv = new CsvReader(sr, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true, BadDataFound = null });

        var ms = new MemoryStream();
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var ws = workbook.Worksheets.Add("CleanedData");

        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord;
        if (headers != null)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }
        }

        int rowNum = 2;
        while (await csv.ReadAsync())
        {
            for (int i = 0; i < (headers?.Length ?? 0); i++)
            {
                ws.Cell(rowNum, i + 1).Value = csv.GetField(i);
            }
            rowNum++;
        }

        ws.Columns().AdjustToContents();
        workbook.SaveAs(ms);
        ms.Position = 0;

        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "CleanedDataMaster.xlsx");
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadKpi(IFormFile? csvFile)
    {
        if (csvFile is null || csvFile.Length == 0)
        {
            TempData["Error"] = "The uploaded file is empty.";
            return RedirectToAction(nameof(Dashboard));
        }

        try
        {
            _sessionStore.CleanupExpiredSessions();
            await using var uploadStream = csvFile.OpenReadStream();
            var token = await _sessionStore.CreateSessionFromCsvAsync(
                uploadStream,
                csvFile.FileName,
                HttpContext.RequestAborted);

            if (!_sessionStore.TryGetCsvPath(token, out var csvPath))
            {
                TempData["Error"] = "Could not store the uploaded file.";
                return RedirectToAction(nameof(Dashboard));
            }

            var detectedKind = await _csvService.DetectCsvSourceKindAsync(
                csvPath,
                csvFile.FileName,
                HttpContext.RequestAborted);
            await _sessionStore.SetCsvSourceKindAsync(token, detectedKind, HttpContext.RequestAborted);

            FilterOptionsViewModel filterOptions;
            await using (var readStream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                filterOptions = await _csvService.ExtractFilterOptionsAsync(readStream, token, HttpContext.RequestAborted);
            }

            ApplyDavaoNorthDefaultForAllStatus(filterOptions, detectedKind);

            ApplyDetectedCsvKindFilterDefaults(filterOptions, detectedKind);
            filterOptions.ActiveDashboardView = detectedKind == CsvSourceKind.AllStatus ? "status" : "pending";

            await _sessionStore.SaveFiltersAsync(token, filterOptions, HttpContext.RequestAborted);

            return RedirectToAction(nameof(Filter), new { token });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing uploaded KPI CSV");
            TempData["Error"] = $"Error processing CSV: {ex.Message}";
            return RedirectToAction(nameof(Dashboard));
        }
    }

    [HttpGet("Filter/{token}")]
    public async Task<IActionResult> Filter(string token)
    {
        if (!_sessionStore.IsValidTokenFormat(token))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (_sessionStore.TryGetCsvPath(token, out var csvPath))
        {
            var session = await _sessionStore.LoadAsync(token);

            FilterOptionsViewModel vm;
            if (HasCachedFilterOptions(session))
            {
                vm = FilterOptionsFromSessionCache(session!, token);
            }
            else
            {
                await using var readStream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                vm = await _csvService.ExtractFilterOptionsAsync(readStream, token, HttpContext.RequestAborted);
            }

            if (session is not null)
                ApplySessionToFilter(vm, session);

            if (session?.CsvSourceKind == CsvSourceKind.AllStatus)
                ApplyDavaoNorthDefaultForAllStatus(vm, CsvSourceKind.AllStatus);

            vm.ActiveDashboardView = session?.CsvSourceKind == CsvSourceKind.AllStatus ? "status" : "pending";

            ViewBag.CsvSourceKind = session?.CsvSourceKind ?? CsvSourceKind.Pending;
            return View(vm);
        }

        if (TryGetCurrentUserId(out var userId)
            && await _db.ReportDashboardArchives.AsNoTracking()
                .AnyAsync(a => a.Token == token && a.UserId == userId, HttpContext.RequestAborted))
        {
            TempData["ArchivedFilterNotice"] =
                "This historical report has fixed filters. Use the dashboard to view and export.";
            return RedirectToAction(nameof(Dashboard), new { token });
        }

        TempData["Error"] = "Report not found or expired. Please upload again.";
        return RedirectToAction(nameof(Upload));
    }

    [HttpGet("OperationalDashboard")]
    public async Task<IActionResult> OperationalDashboard(
        [FromQuery] string? group = null,
        [FromQuery] string? alarmPeriod = null,
        [FromQuery] string? alarmMode = null,
        [FromQuery] string? alarmDate = null,
        [FromQuery] string? alarmStart = null,
        [FromQuery] string? alarmEnd = null,
        [FromQuery] string? perfPeriod = null,
        [FromQuery] string? perfMode = null,
        [FromQuery] string? perfDate = null,
        [FromQuery] string? perfStart = null,
        [FromQuery] string? perfEnd = null,
        [FromQuery] string? token = null,
        [FromQuery] string? month = null,
        [FromQuery] int? dailyYear = null,
        [FromQuery] int? dailyMonth = null,
        [FromQuery] int? dailyDay = null,
        [FromQuery] int? agingYear = null,
        [FromQuery] int? agingMonth = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? detailSort = null)
    {
        if (!TryGetCurrentUserId(out var userId))
            return RedirectToAction(nameof(Upload));

        var model = new OperationalDashboardViewModel();
        var latestAlarmToken = await GetLatestOperationalTokenByKindAsync(userId, OperationalReportKind.AlarmHistory, HttpContext.RequestAborted);
        var latestPerformanceToken = await GetLatestOperationalTokenByKindAsync(userId, OperationalReportKind.PerformanceHistory, HttpContext.RequestAborted);
        model.LatestAlarmToken = latestAlarmToken ?? string.Empty;
        model.LatestPerformanceToken = latestPerformanceToken ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(latestAlarmToken)
            && _sessionStore.TryGetCsvPath(latestAlarmToken, out var alarmCsvPath))
        {
            var alarmSession = await _sessionStore.LoadAsync(latestAlarmToken, HttpContext.RequestAborted);
            if (alarmSession is not null && alarmSession.OperationalReportKind == OperationalReportKind.AlarmHistory)
            {
                var resolvedAlarmPeriod = string.IsNullOrWhiteSpace(alarmPeriod) ? alarmSession.OperationalAlarmPeriodFilter : alarmPeriod;
                var resolvedAlarmMode = string.IsNullOrWhiteSpace(alarmMode) ? alarmSession.OperationalAlarmDateFilterMode : alarmMode;
                var resolvedAlarmDate = string.IsNullOrWhiteSpace(alarmDate) ? alarmSession.OperationalAlarmSelectedDate : alarmDate;
                var resolvedAlarmStart = string.IsNullOrWhiteSpace(alarmStart) ? alarmSession.OperationalAlarmDateRangeStart : alarmStart;
                var resolvedAlarmEnd = string.IsNullOrWhiteSpace(alarmEnd) ? alarmSession.OperationalAlarmDateRangeEnd : alarmEnd;
                var alarmReport = await _operationalService.BuildReportAsync(
                    alarmCsvPath,
                    GetDisplayName(latestAlarmToken),
                    null,
                    resolvedAlarmPeriod ?? "1hour",
                    resolvedAlarmMode ?? "all",
                    resolvedAlarmDate,
                    resolvedAlarmStart,
                    resolvedAlarmEnd,
                    HttpContext.RequestAborted);
                alarmReport.ReportToken = latestAlarmToken;
                model.AlarmReport = alarmReport;
                await SaveOperationalStateAsync(latestAlarmToken, alarmSession, alarmReport, HttpContext.RequestAborted);
            }
        }

        if (!string.IsNullOrWhiteSpace(latestPerformanceToken)
            && _sessionStore.TryGetCsvPath(latestPerformanceToken, out var performanceCsvPath))
        {
            var performanceSession = await _sessionStore.LoadAsync(latestPerformanceToken, HttpContext.RequestAborted);
            if (performanceSession is not null && performanceSession.OperationalReportKind == OperationalReportKind.PerformanceHistory)
            {
                var selectedGroup = string.IsNullOrWhiteSpace(group)
                    ? performanceSession.OperationalSelectedPerformanceGroup
                    : group;
                var resolvedPerfDate = string.IsNullOrWhiteSpace(perfDate) ? performanceSession.OperationalPerformanceSelectedDate : perfDate;
                var resolvedPerfStart = string.IsNullOrWhiteSpace(perfStart) ? performanceSession.OperationalPerformanceDateRangeStart : perfStart;
                var resolvedPerfEnd = string.IsNullOrWhiteSpace(perfEnd) ? performanceSession.OperationalPerformanceDateRangeEnd : perfEnd;
                var resolvedPerfPeriod = string.IsNullOrWhiteSpace(perfPeriod) ? performanceSession.OperationalPerformancePeriodFilter : perfPeriod;
                var resolvedPerfMode = ResolveDateFilterMode(
                    perfMode,
                    resolvedPerfDate,
                    resolvedPerfStart,
                    resolvedPerfEnd,
                    performanceSession.OperationalPerformanceDateFilterMode);
                var performanceReport = await _operationalService.BuildReportAsync(
                    performanceCsvPath,
                    GetDisplayName(latestPerformanceToken),
                    selectedGroup,
                    resolvedPerfPeriod ?? "1hour",
                    resolvedPerfMode ?? "all",
                    resolvedPerfDate,
                    resolvedPerfStart,
                    resolvedPerfEnd,
                    HttpContext.RequestAborted);
                performanceReport.ReportToken = latestPerformanceToken;
                model.PerformanceReport = performanceReport;
                await SaveOperationalStateAsync(latestPerformanceToken, performanceSession, performanceReport, HttpContext.RequestAborted);
            }
        }

        var resolvedMonth = month;
        if (dailyYear is not null && dailyMonth is not null)
            resolvedMonth = $"{dailyYear.Value}-{Math.Clamp(dailyMonth.Value, 1, 12):D2}";

        var agingToken = await ResolveAgingReportTokenAsync(userId, token, HttpContext.RequestAborted);
        if (agingToken is not null && _sessionStore.TryGetCsvPath(agingToken, out var agingCsvPath))
        {
            try
            {
                model.OperationAging = await _csvService.ComputeOperationAgingAsync(
                    agingCsvPath,
                    agingToken,
                    resolvedMonth,
                    agingYear,
                    agingMonth,
                    page,
                    pageSize,
                    detailSort,
                    dailyDay,
                    HttpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Expected when user changes filters quickly or navigates away.
                _logger.LogDebug("Operation aging request was canceled for token {Token}", agingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing operation aging for token {Token}", agingToken);
            }
        }

        model.IsFirstVisit = !model.HasReport;

        return View(model);
    }

    [HttpPost("OperationalDashboard/Upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadOperational(IFormFile? csvFile)
    {
        if (csvFile is null || csvFile.Length == 0)
        {
            TempData["Error"] = "Please choose a CSV or XLSX file.";
            return RedirectToAction(nameof(OperationalDashboard));
        }

        try
        {
            await using var uploadStream = csvFile.OpenReadStream();
            var token = await _sessionStore.CreateSessionFromCsvAsync(uploadStream, csvFile.FileName, HttpContext.RequestAborted);
            if (!_sessionStore.TryGetCsvPath(token, out var csvPath))
            {
                TempData["Error"] = "Could not store the uploaded file.";
                return RedirectToAction(nameof(OperationalDashboard));
            }

            var report = await _operationalService.BuildReportAsync(csvPath, csvFile.FileName, null, "1hour", "all", null, null, null, HttpContext.RequestAborted);
            if (!report.HasReport)
            {
                TempData["Error"] = "Unable to parse the uploaded file. Please upload Alarm History or Performance History exports.";
                return RedirectToAction(nameof(OperationalDashboard));
            }

            report.ReportToken = token;
            var session = await _sessionStore.LoadAsync(token, HttpContext.RequestAborted) ?? new ReportSessionData { CreatedUtc = DateTime.UtcNow };
            await SaveOperationalStateAsync(token, session, report, HttpContext.RequestAborted);
            return RedirectToAction(nameof(OperationalDashboard), new { group = report.SelectedPerformanceGroup });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing operational upload");
            TempData["Error"] = $"Error processing upload: {ex.Message}";
            return RedirectToAction(nameof(OperationalDashboard));
        }
    }

    /// <summary>
    /// Tokenless entry point for the KPI tab — redirects to the user's most recent active or
    /// archived session, or falls back to Upload if nothing is found.
    /// </summary>
    [HttpGet("Dashboard")]
    public async Task<IActionResult> DashboardRedirect(CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentUserId(out var userId))
            return RedirectToAction(nameof(Upload));

        // Prefer the most recent live upload (CSV still on disk / in-memory session).
        var latestUpload = await _db.ReportUploads
            .AsNoTracking()
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.UploadedUtc)
            .Select(u => u.Token)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestUpload is not null && _sessionStore.TryGetCsvPath(latestUpload, out _))
            return RedirectToAction(nameof(Dashboard), new { token = latestUpload });

        // Fall back to the most recent archived session.
        var latestArchive = await _db.ReportDashboardArchives
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.UploadedUtc)
            .Select(a => a.Token)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestArchive is not null)
            return RedirectToAction(nameof(Dashboard), new { token = latestArchive });

        return RedirectToAction(nameof(Upload));
    }

    [HttpGet("Dashboard/{token}")]
    public async Task<IActionResult> Dashboard(string? token, string? view = null)
    {
        // #region agent log
        AgentDebugLog("H1", "ReportController.Dashboard GET", "action_entered", new { tokenSegmentLen = token?.Length });
        // #endregion
        if (string.IsNullOrEmpty(token) || !_sessionStore.IsValidTokenFormat(token))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (_sessionStore.TryGetCsvPath(token, out var csvPath))
        {
            var session = await _sessionStore.LoadAsync(token);
            if (session is null)
            {
                TempData["Error"] = "Report session is incomplete. Please upload again.";
                return RedirectToAction(nameof(Upload));
            }

            try
            {
                return await DashboardLiveAsync(token, view, csvPath, session, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing KPI dashboard for token");
                TempData["Error"] = $"Error computing dashboard: {ex.Message}";
                return RedirectToAction(nameof(Upload));
            }
        }

        var archive = await _db.ReportDashboardArchives.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Token == token && a.UserId == userId, HttpContext.RequestAborted);
        if (archive is null)
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        try
        {
            return await DashboardFromArchiveAsync(token, view, archive, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading archived KPI dashboard for token");
            TempData["Error"] = $"Error loading dashboard: {ex.Message}";
            return RedirectToAction(nameof(Upload));
        }
    }

    private async Task<IActionResult> DashboardLiveAsync(
        string token,
        string? view,
        string csvPath,
        ReportSessionData session,
        CancellationToken cancellationToken)
    {
        var sourceKind = session.CsvSourceKind;
        string activeView;
        if (!string.IsNullOrWhiteSpace(view))
            activeView = string.Equals(view, "status", StringComparison.OrdinalIgnoreCase) ? "status" : "pending";
        else
            activeView = sourceKind == CsvSourceKind.AllStatus ? "status" : "pending";

        var filterOptions = await GetFilterOptionsForDashboardAsync(token, csvPath, session, cancellationToken);

        var activeFilters = ReportSessionFilterResolver.GetSessionFiltersForView(session, activeView);
        var mode = activeFilters.DateFilterMode;
        DateOnly? singleDate = ReportSessionDate.ParseDateOrNull(activeFilters.SelectedDate);
        DateOnly? rangeStart = ReportSessionDate.ParseDateOrNull(activeFilters.DateRangeStart);
        DateOnly? rangeEnd = ReportSessionDate.ParseDateOrNull(activeFilters.DateRangeEnd);

        var territoryFilters = ResolveTerritoryFiltersForKpi(sourceKind, activeFilters.SelectedTerritories, filterOptions.AvailableTerritories);

        var filterArgs = (
            Territories: territoryFilters,
            Statuses: activeFilters.SelectedStatuses,
            SubStatuses: activeFilters.SelectedSubStatuses,
            Skillsets: activeFilters.SelectedSkillsets,
            OrderCreateDates: activeFilters.SelectedOrderCreateDates);

        KpiDashboardViewModel kpi;

        if (activeView == "status")
        {
            kpi = await _csvService.ComputeAllStatusComplianceKpiAsync(
                csvPath,
                mode,
                singleDate,
                rangeStart,
                rangeEnd,
                filterArgs.Territories,
                filterArgs.Statuses,
                filterArgs.SubStatuses,
                filterArgs.Skillsets,
                filterArgs.OrderCreateDates);

            if (sourceKind != CsvSourceKind.AllStatus)
            {
                TempData["ComplianceNotice"] =
                    "This file was detected as All Pending. Compliance rules still apply where completion time is present; other rows may show N/A.";
            }
        }
        else
        {
            kpi = await _csvService.ComputeKpiAsync(
                csvPath,
                mode,
                singleDate,
                rangeStart,
                rangeEnd,
                filterArgs.Territories,
                filterArgs.Statuses,
                filterArgs.SubStatuses,
                filterArgs.Skillsets,
                filterArgs.OrderCreateDates);
        }

        kpi.ReportToken = token!;
        kpi.DateFilterMode = mode;
        kpi.SelectedDate = activeFilters.SelectedDate;
        kpi.DateRangeStart = activeFilters.DateRangeStart;
        kpi.DateRangeEnd = activeFilters.DateRangeEnd;
        kpi.SelectedTerritories = territoryFilters;
        kpi.SelectedStatuses = filterArgs.Statuses;
        kpi.SelectedSubStatuses = filterArgs.SubStatuses;
        kpi.SelectedSkillsets = filterArgs.Skillsets;
        kpi.SelectedOrderCreateDates = filterArgs.OrderCreateDates;

        kpi.AvailableDates = filterOptions.AvailableDates;
        kpi.AvailableTerritories = filterOptions.AvailableTerritories;
        kpi.AvailableStatuses = filterOptions.AvailableStatuses;
        kpi.AvailableSubStatuses = filterOptions.AvailableSubStatuses;
        kpi.AvailableSkillsets = filterOptions.AvailableSkillsets;
        kpi.AvailableOrderCreateDates = filterOptions.AvailableOrderCreateDates;

        kpi.ActiveDashboardView = activeView;
        kpi.CsvSourceKind = sourceKind;
        kpi.IsReadOnly = false;

        /* ── Patch heatmap fields with unfiltered data ── */
        try
        {
            var hm = await _csvService.ExtractHeatmapSnapshotAsync(csvPath);
            kpi.HeatmapNapDots            = hm.HeatmapNapDots;
            kpi.HeatmapNapDotNames        = hm.HeatmapNapDotNames;
            kpi.HeatmapNapDotDpids        = hm.HeatmapNapDotDpids;
            kpi.HeatmapNapDotSkillsets    = hm.HeatmapNapDotSkillsets;
            kpi.HeatmapNapDotTerritories  = hm.HeatmapNapDotTerritories;
            kpi.HeatmapNapDotStatuses     = hm.HeatmapNapDotStatuses;
            kpi.HeatmapHasCoordinates     = hm.HeatmapHasCoordinates;
            kpi.HeatmapTotalAppointments  = hm.HeatmapTotalAppointments;
            kpi.HeatmapRepairCount        = hm.HeatmapRepairCount;
            kpi.HeatmapInstallCount       = hm.HeatmapInstallCount;
            kpi.HeatmapTerritoryDistribution = hm.HeatmapTerritoryDistribution;
            kpi.HeatmapAppointmentsByDate    = hm.HeatmapAppointmentsByDate;
            kpi.HeatmapJoinDateInts          = hm.HeatmapJoinDateInts;
            kpi.HeatmapJoinDpids             = hm.HeatmapJoinDpids;
            kpi.HeatmapJoinFixDescriptions   = hm.HeatmapJoinFixDescriptions;
            kpi.HeatmapJoinTerritories       = hm.HeatmapJoinTerritories;
            kpi.HeatmapJoinSkillsets         = hm.HeatmapJoinSkillsets;
            kpi.HeatmapJoinStatuses          = hm.HeatmapJoinStatuses;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract unfiltered heatmap snapshot; falling back to SA-filtered data.");
            kpi.HeatmapNapDots            = kpi.NapDots;
            kpi.HeatmapNapDotNames        = kpi.NapDotNames;
            kpi.HeatmapNapDotDpids        = kpi.NapDotDpids;
            kpi.HeatmapNapDotSkillsets    = kpi.NapDotSkillsets;
            kpi.HeatmapNapDotTerritories  = kpi.NapDotTerritories;
            kpi.HeatmapNapDotStatuses     = kpi.NapDotStatuses;
            kpi.HeatmapHasCoordinates     = kpi.HasCoordinates;
            kpi.HeatmapTotalAppointments  = kpi.TotalAppointments;
            kpi.HeatmapRepairCount        = kpi.RepairSkillsetCount;
            kpi.HeatmapInstallCount       = kpi.SkillsetDistribution
                .Where(kv => kv.Key.Contains("Install", StringComparison.OrdinalIgnoreCase))
                .Sum(kv => kv.Value);
            kpi.HeatmapTerritoryDistribution = kpi.TerritoryDistribution;
            kpi.HeatmapAppointmentsByDate    = kpi.AppointmentsByDate;
            kpi.HeatmapJoinDateInts          = [];
            kpi.HeatmapJoinDpids             = [];
            kpi.HeatmapJoinFixDescriptions   = [];
            kpi.HeatmapJoinTerritories       = [];
            kpi.HeatmapJoinSkillsets         = [];
            kpi.HeatmapJoinStatuses          = [];
        }

        await PopulateDashboardContextAsync(token!, kpi, cancellationToken);
        return View("Dashboard", kpi);
    }

    private async Task<IActionResult> DashboardFromArchiveAsync(
        string token,
        string? view,
        ReportDashboardArchiveEntity archive,
        CancellationToken cancellationToken)
    {
        ReportSessionData session;
        try
        {
            session = JsonSerializer.Deserialize<ReportSessionData>(archive.SessionJson, ReportSessionJson.Options)
                      ?? new ReportSessionData { CreatedUtc = archive.UploadedUtc };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid archived session JSON for token {Token}", token);
            TempData["Error"] = "Report session is incomplete. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        var sourceKind = session.CsvSourceKind;
        string activeView;
        if (!string.IsNullOrWhiteSpace(view))
            activeView = string.Equals(view, "status", StringComparison.OrdinalIgnoreCase) ? "status" : "pending";
        else
            activeView = sourceKind == CsvSourceKind.AllStatus ? "status" : "pending";

        var kpiJson = activeView == "status" ? archive.StatusKpiJson : archive.PendingKpiJson;
        var kpi = JsonSerializer.Deserialize<KpiDashboardViewModel>(kpiJson, ReportKpiJson.Options) ?? new KpiDashboardViewModel();

        var filterOptions = HasCachedFilterOptions(session)
            ? FilterOptionsFromSessionCache(session, token)
            : new FilterOptionsViewModel { ReportToken = token };

        var activeFilters = ReportSessionFilterResolver.GetSessionFiltersForView(session, activeView);
        var mode = activeFilters.DateFilterMode;

        kpi.ReportToken = token;
        kpi.DateFilterMode = mode;
        kpi.SelectedDate = activeFilters.SelectedDate;
        kpi.DateRangeStart = activeFilters.DateRangeStart;
        kpi.DateRangeEnd = activeFilters.DateRangeEnd;
        kpi.SelectedTerritories = ResolveTerritoryFiltersForKpi(
            sourceKind,
            activeFilters.SelectedTerritories,
            filterOptions.AvailableTerritories);
        kpi.SelectedStatuses = activeFilters.SelectedStatuses;
        kpi.SelectedSubStatuses = activeFilters.SelectedSubStatuses;
        kpi.SelectedSkillsets = activeFilters.SelectedSkillsets;
        kpi.SelectedOrderCreateDates = activeFilters.SelectedOrderCreateDates;

        kpi.AvailableDates = filterOptions.AvailableDates;
        kpi.AvailableTerritories = filterOptions.AvailableTerritories;
        kpi.AvailableStatuses = filterOptions.AvailableStatuses;
        kpi.AvailableSubStatuses = filterOptions.AvailableSubStatuses;
        kpi.AvailableSkillsets = filterOptions.AvailableSkillsets;
        kpi.AvailableOrderCreateDates = filterOptions.AvailableOrderCreateDates;

        kpi.ActiveDashboardView = activeView;
        kpi.CsvSourceKind = sourceKind;
        kpi.IsReadOnly = true;

        if (activeView == "status" && sourceKind != CsvSourceKind.AllStatus)
        {
            TempData["ComplianceNotice"] =
                "This file was detected as All Pending. Compliance rules still apply where completion time is present; other rows may show N/A.";
        }

        /* ── Archived dashboards: heatmap fields may be absent in old JSON; fall back to SA data ── */
        if (kpi.HeatmapNapDots.Count == 0)
        {
            kpi.HeatmapNapDots            = kpi.NapDots;
            kpi.HeatmapNapDotNames        = kpi.NapDotNames;
            kpi.HeatmapNapDotDpids        = kpi.NapDotDpids;
            kpi.HeatmapNapDotSkillsets    = kpi.NapDotSkillsets;
            kpi.HeatmapNapDotTerritories  = kpi.NapDotTerritories;
            kpi.HeatmapNapDotStatuses     = kpi.NapDotStatuses;
            kpi.HeatmapHasCoordinates     = kpi.HasCoordinates;
            kpi.HeatmapTotalAppointments  = kpi.TotalAppointments;
            kpi.HeatmapRepairCount        = kpi.RepairSkillsetCount;
            kpi.HeatmapInstallCount       = kpi.SkillsetDistribution
                .Where(kv => kv.Key.Contains("Install", StringComparison.OrdinalIgnoreCase))
                .Sum(kv => kv.Value);
            kpi.HeatmapTerritoryDistribution = kpi.TerritoryDistribution;
            kpi.HeatmapAppointmentsByDate    = kpi.AppointmentsByDate;
        }

        await PopulateDashboardContextAsync(token, kpi, cancellationToken);
        return View("Dashboard", kpi);
    }

    private static bool TryGetCurrentUserId(HttpContext? http, out Guid userId)
    {
        userId = default;
        var id = http?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out userId);
    }

    private bool TryGetCurrentUserId(out Guid userId) => TryGetCurrentUserId(HttpContext, out userId);

    private async Task<bool> IsArchivedOnlyTokenAsync(string token, Guid userId, CancellationToken cancellationToken = default)
    {
        if (await _db.ReportUploads.AnyAsync(r => r.Token == token && r.UserId == userId, cancellationToken))
            return false;
        return await _db.ReportDashboardArchives.AnyAsync(a => a.Token == token && a.UserId == userId, cancellationToken);
    }

    [HttpPost("Dashboard")]
    [HttpPost("Dashboard/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dashboard(string? token, FilterOptionsViewModel model)
    {
        // #region agent log
        AgentDebugLog("H1", "ReportController.Dashboard POST", "action_entered", new { routeTokenLen = token?.Length, bodyReportTokenLen = model.ReportToken.Length });
        // #endregion
        if (!string.IsNullOrEmpty(token) && !string.Equals(token, model.ReportToken, StringComparison.Ordinal))
        {
            TempData["Error"] = "Invalid filter submission.";
            return RedirectToAction(nameof(Upload));
        }

        if (!_sessionStore.IsValidTokenFormat(model.ReportToken))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (TryGetCurrentUserId(out var postUserId)
            && await IsArchivedOnlyTokenAsync(model.ReportToken, postUserId, HttpContext.RequestAborted))
        {
            TempData["Error"] = "This report is archived; filters cannot be changed.";
            var rv = string.Equals(model.ActiveDashboardView, "status", StringComparison.OrdinalIgnoreCase)
                ? "status"
                : "pending";
            return RedirectToAction(nameof(Dashboard), new { token = model.ReportToken, view = rv });
        }

        if (!_sessionStore.TryGetCsvPath(model.ReportToken, out _))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Invalid filter submission.";
            return RedirectToAction(nameof(Filter), new { token = model.ReportToken });
        }

        try
        {
            var postSession = await _sessionStore.LoadAsync(model.ReportToken);
            if (postSession is null)
            {
                TempData["Error"] = "Report session is incomplete. Please upload again.";
                return RedirectToAction(nameof(Upload));
            }

            _sessionStore.TryGetCsvPath(model.ReportToken, out var postCsvPath);
            var postFilterOpts = await GetFilterOptionsForDashboardAsync(
                model.ReportToken,
                postCsvPath!,
                postSession,
                HttpContext.RequestAborted);
            model.SelectedTerritories = ResolveTerritoryFiltersForKpi(
                postSession.CsvSourceKind,
                model.SelectedTerritories ?? [],
                postFilterOpts.AvailableTerritories);

            await _sessionStore.SaveFiltersAsync(model.ReportToken, model, HttpContext.RequestAborted);
            var returnView = string.Equals(model.ActiveDashboardView, "status", StringComparison.OrdinalIgnoreCase)
                ? "status"
                : "pending";
            return RedirectToAction(nameof(Dashboard), new { token = model.ReportToken, view = returnView });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving filters");
            TempData["Error"] = $"Error saving filters: {ex.Message}";
            return RedirectToAction(nameof(Filter), new { token = model.ReportToken });
        }
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(FilterOptionsViewModel model)
    {
        if (!_sessionStore.IsValidTokenFormat(model.ReportToken))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (_sessionStore.TryGetCsvPath(model.ReportToken, out var csvPath))
        {
            var session = await _sessionStore.LoadAsync(model.ReportToken);
            if (session is null)
            {
                TempData["Error"] = "Report session is incomplete. Please upload again.";
                return RedirectToAction(nameof(Upload));
            }

            try
            {
                var mode = session.DateFilterMode ?? "all";
                DateOnly? singleDate = ReportSessionDate.ParseDateOrNull(session.SelectedDate);
                DateOnly? rangeStart = ReportSessionDate.ParseDateOrNull(session.DateRangeStart);
                DateOnly? rangeEnd = ReportSessionDate.ParseDateOrNull(session.DateRangeEnd);

                var xlsxStream = await _csvService.GenerateXlsxAsync(
                    csvPath,
                    mode,
                    singleDate,
                    rangeStart,
                    rangeEnd,
                    session.SelectedTerritories ?? [],
                    session.SelectedStatuses ?? [],
                    session.SelectedSubStatuses ?? [],
                    session.SelectedSkillsets ?? []);

                var fileName = mode switch
                {
                    "single" when singleDate.HasValue => $"PendingReport_{singleDate.Value:yyyy-MM-dd}.xlsx",
                    "range" => $"PendingReport_{rangeStart?.ToString("yyyy-MM-dd") ?? "start"}_to_{rangeEnd?.ToString("yyyy-MM-dd") ?? "end"}.xlsx",
                    _ => "PendingReport_AllDates.xlsx"
                };

                return File(
                    xlsxStream,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating XLSX report");
                TempData["Error"] = $"Error generating report: {ex.Message}";
                return RedirectToAction(nameof(Filter), new { token = model.ReportToken });
            }
        }

        var archive = await _db.ReportDashboardArchives.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Token == model.ReportToken && a.UserId == userId, HttpContext.RequestAborted);
        if (archive?.LegacyGenerateXlsxBytes is not { Length: > 0 })
        {
            TempData["Error"] = "Report not found or export is unavailable. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        ReportSessionData archSession;
        try
        {
            archSession = JsonSerializer.Deserialize<ReportSessionData>(archive.SessionJson, ReportSessionJson.Options)
                          ?? new ReportSessionData();
        }
        catch (JsonException)
        {
            TempData["Error"] = "Report session is incomplete. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        var modeA = archSession.DateFilterMode ?? "all";
        DateOnly? sd = ReportSessionDate.ParseDateOrNull(archSession.SelectedDate);
        DateOnly? rs = ReportSessionDate.ParseDateOrNull(archSession.DateRangeStart);
        DateOnly? re = ReportSessionDate.ParseDateOrNull(archSession.DateRangeEnd);
        var fileNameA = modeA switch
        {
            "single" when sd.HasValue => $"PendingReport_{sd.Value:yyyy-MM-dd}.xlsx",
            "range" => $"PendingReport_{rs?.ToString("yyyy-MM-dd") ?? "start"}_to_{re?.ToString("yyyy-MM-dd") ?? "end"}.xlsx",
            _ => "PendingReport_AllDates.xlsx"
        };

        return File(archive.LegacyGenerateXlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileNameA);
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportSlotAdherenceCsv(SlotAdherenceExportRequest request)
    {
        if (!_sessionStore.IsValidTokenFormat(request.ReportToken))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        try
        {
            var (kpi, isArchived) = await BuildExportKpiAsync(request.ReportToken, request.View, userId, HttpContext.RequestAborted);
            var csvStream = await _csvService.GenerateSlotAdherenceCsvAsync(kpi);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var archiveTag = isArchived ? "_archived" : string.Empty;
            var fileName = $"SlotAdherence_{kpi.ActiveDashboardView}_{stamp}{archiveTag}.csv";
            return File(csvStream, "text/csv; charset=utf-8", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Slot Adherence CSV export");
            TempData["Error"] = $"Error exporting Slot Adherence CSV: {ex.Message}";
            return RedirectToAction(nameof(Dashboard), new { token = request.ReportToken, view = request.View });
        }
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportSlotAdherenceExcel(SlotAdherenceExportRequest request)
    {
        if (!_sessionStore.IsValidTokenFormat(request.ReportToken))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            TempData["Error"] = "Report not found or expired. Please upload again.";
            return RedirectToAction(nameof(Upload));
        }

        try
        {
            var (kpi, isArchived) = await BuildExportKpiAsync(request.ReportToken, request.View, userId, HttpContext.RequestAborted);
            var chartImages = ParseChartImages(request.ChartImagesJson);
            var xlsxStream = await _csvService.GenerateSlotAdherenceVisualXlsxAsync(kpi, chartImages);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var archiveTag = isArchived ? "_archived" : string.Empty;
            var fileName = $"SlotAdherence_{kpi.ActiveDashboardView}_{stamp}{archiveTag}.xlsx";
            return File(xlsxStream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Slot Adherence Excel export");
            TempData["Error"] = $"Error exporting Slot Adherence Excel: {ex.Message}";
            return RedirectToAction(nameof(Dashboard), new { token = request.ReportToken, view = request.View });
        }
    }

    private async Task<(KpiDashboardViewModel Kpi, bool IsArchived)> BuildExportKpiAsync(
        string token,
        string? requestedView,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (_sessionStore.TryGetCsvPath(token, out var csvPath))
        {
            var session = await _sessionStore.LoadAsync(token);
            if (session is null)
                throw new InvalidOperationException("Report session is incomplete.");

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

            var territoryFilters = ResolveTerritoryFiltersForKpi(sourceKind, activeFilters.SelectedTerritories, filterOptions.AvailableTerritories);

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

            kpi.ReportToken = token;
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
            return (kpi, false);
        }

        var archive = await _db.ReportDashboardArchives
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Token == token && a.UserId == userId, cancellationToken);
        if (archive is null)
            throw new InvalidOperationException("Report not found or expired.");

        var archiveSession = JsonSerializer.Deserialize<ReportSessionData>(archive.SessionJson, ReportSessionJson.Options)
                             ?? throw new InvalidOperationException("Archived report session is incomplete.");
        var archiveSourceKind = archiveSession.CsvSourceKind;
        var activeViewArchive = !string.IsNullOrWhiteSpace(requestedView)
            ? (string.Equals(requestedView, "status", StringComparison.OrdinalIgnoreCase) ? "status" : "pending")
            : archiveSourceKind == CsvSourceKind.AllStatus ? "status" : "pending";
        var archiveKpiJson = activeViewArchive == "status" ? archive.StatusKpiJson : archive.PendingKpiJson;
        var archivedKpi = JsonSerializer.Deserialize<KpiDashboardViewModel>(archiveKpiJson, ReportKpiJson.Options) ?? new KpiDashboardViewModel();
        archivedKpi.ReportToken = token;
        archivedKpi.ActiveDashboardView = activeViewArchive;
        archivedKpi.CsvSourceKind = archiveSourceKind;
        return (archivedKpi, true);
    }

    private static IReadOnlyCollection<SlotAdherenceChartImage> ParseChartImages(string? chartImagesJson)
    {
        if (string.IsNullOrWhiteSpace(chartImagesJson))
            return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<SlotAdherenceChartImage>>(chartImagesJson)
                         ?? [];
            return parsed
                .Where(x => !string.IsNullOrWhiteSpace(x.DataUrl))
                .Take(8)
                .Select(x => new SlotAdherenceChartImage
                {
                    ChartKey = (x.ChartKey ?? string.Empty).Trim(),
                    ChartTitle = (x.ChartTitle ?? string.Empty).Trim(),
                    DataUrl = (x.DataUrl ?? string.Empty).Trim()
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task PopulateDashboardContextAsync(string token, KpiDashboardViewModel kpi, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return;

        var retention = Math.Max(1, _sessionOptions.RetentionDays);
        var cutoff = DateTime.UtcNow.AddDays(-retention);

        var uploadItems = await _db.ReportUploads
            .AsNoTracking()
            .Where(r => r.UserId == userId && r.UploadedUtc >= cutoff)
            .Select(r => new ReportHistoryItem
            {
                Token = r.Token,
                OriginalFileName = r.OriginalFileName,
                UploadedUtc = r.UploadedUtc,
                CsvSourceKind = r.CsvSourceKind,
                IsArchived = false
            })
            .ToListAsync(cancellationToken);

        var archiveItems = await _db.ReportDashboardArchives
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.UploadedUtc >= cutoff)
            .Select(a => new ReportHistoryItem
            {
                Token = a.Token,
                OriginalFileName = a.OriginalFileName,
                UploadedUtc = a.UploadedUtc,
                CsvSourceKind = a.CsvSourceKind,
                IsArchived = true
            })
            .ToListAsync(cancellationToken);

        kpi.ReportHistory = uploadItems
            .Concat(archiveItems)
            .OrderByDescending(h => h.UploadedUtc)
            .Take(50)
            .ToList();

        var upload = await _db.ReportUploads.FirstOrDefaultAsync(
            r => r.Token == token && r.UserId == userId,
            cancellationToken);
        if (upload is null)
            return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return;

        user.LastReportUploadId = upload.Id;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<FilterOptionsViewModel> GetFilterOptionsForDashboardAsync(
        string token,
        string csvPath,
        ReportSessionData session,
        CancellationToken cancellationToken = default)
    {
        if (HasCachedFilterOptions(session))
            return FilterOptionsFromSessionCache(session, token);

        await using var optStream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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

    private static void ApplySessionToFilter(FilterOptionsViewModel vm, ReportSessionData session)
    {
        vm.DateFilterMode = session.DateFilterMode ?? "all";
        vm.SelectedDate = session.SelectedDate;
        vm.DateRangeStart = session.DateRangeStart;
        vm.DateRangeEnd = session.DateRangeEnd;
        vm.SelectedTerritories = session.SelectedTerritories ?? [];
        vm.SelectedStatuses = session.SelectedStatuses ?? [];
        vm.SelectedSubStatuses = session.SelectedSubStatuses ?? [];
        vm.SelectedSkillsets = session.SelectedSkillsets ?? [];
        vm.SelectedOrderCreateDates = session.SelectedOrderCreateDates ?? [];
    }

    /// <summary>
    /// Pre-selects Status / SubStatus checkboxes on the filter page based on detected CSV kind (All Pending vs All Status).
    /// Uses values from CsvMapping when present and matches actual option strings from the CSV.
    /// </summary>
    private void ApplyDetectedCsvKindFilterDefaults(FilterOptionsViewModel opts, CsvSourceKind kind)
    {
        if (kind == CsvSourceKind.Pending)
        {
            var delayedValue = _configuration["CsvMapping:DelayedStatusValue"] ?? "Delayed";
            var delayed = MatchAvailableOption(opts.AvailableStatuses, delayedValue);
            if (delayed is not null && !opts.SelectedStatuses.Contains(delayed, StringComparer.OrdinalIgnoreCase))
                opts.SelectedStatuses.Add(delayed);

            var pendingDefaultSubStatuses = new[] { "For Visit", "ForVisit", "ForReschedule" };
            foreach (var wantedSubStatus in pendingDefaultSubStatuses)
            {
                var matchedSubStatus = MatchAvailableOption(opts.AvailableSubStatuses, wantedSubStatus);
                if (matchedSubStatus is not null && !opts.SelectedSubStatuses.Contains(matchedSubStatus, StringComparer.OrdinalIgnoreCase))
                    opts.SelectedSubStatuses.Add(matchedSubStatus);
            }

            var repairSkillset = MatchAvailableOption(opts.AvailableSkillsets, "Repair");
            if (repairSkillset is not null && !opts.SelectedSkillsets.Contains(repairSkillset, StringComparer.OrdinalIgnoreCase))
                opts.SelectedSkillsets.Add(repairSkillset);
        }
        else if (kind == CsvSourceKind.AllStatus)
        {
            var completedValue = _configuration["CsvMapping:CompletedStatusValue"] ?? "Completed";
            var completed = MatchAvailableOption(opts.AvailableStatuses, completedValue);
            if (completed is not null)
                opts.SelectedStatuses = [completed];

            var repairSkillset = MatchAvailableOption(opts.AvailableSkillsets, "Repair");
            if (repairSkillset is not null && !opts.SelectedSkillsets.Contains(repairSkillset, StringComparer.OrdinalIgnoreCase))
                opts.SelectedSkillsets.Add(repairSkillset);
        }
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

    /// <summary>
    /// All Status exports are scoped to Davao North: select the exact territory label from the CSV (case-insensitive match).
    /// </summary>
    private void ApplyDavaoNorthDefaultForAllStatus(FilterOptionsViewModel opts, CsvSourceKind kind)
    {
        if (kind != CsvSourceKind.AllStatus)
            return;

        var davao = MatchAvailableOption(opts.AvailableTerritories, "Davao North");
        if (davao is not null)
            opts.SelectedTerritories = [davao];

        var install = MatchAvailableOption(opts.AvailableSkillsets, "Install");
        var repair = MatchAvailableOption(opts.AvailableSkillsets, "Repair");
        var skillsets = new List<string>();
        if (install is not null) skillsets.Add(install);
        if (repair is not null) skillsets.Add(repair);
        if (skillsets.Count > 0)
            opts.SelectedSkillsets = skillsets;

        var cancelled = MatchAvailableOption(opts.AvailableStatuses, "Cancelled");
        var completed = MatchAvailableOption(opts.AvailableStatuses, "Completed");
        var statuses = new List<string>();
        if (cancelled is not null) statuses.Add(cancelled);
        if (completed is not null) statuses.Add(completed);
        if (statuses.Count > 0)
            opts.SelectedStatuses = statuses;
    }

    /// <summary>
    /// For All Status sessions, KPI and exports always filter to Davao North when that territory exists in the file.
    /// </summary>
    private List<string> ResolveTerritoryFiltersForKpi(
        CsvSourceKind sourceKind,
        IReadOnlyList<string> sessionTerritories,
        IReadOnlyList<string> availableTerritories)
    {
        if (sourceKind != CsvSourceKind.AllStatus)
            return [.. sessionTerritories];

        var davao = MatchAvailableOption(availableTerritories, "Davao North");
        return davao is not null ? [davao] : [.. sessionTerritories];
    }

    /// <summary>
    /// CSV token for Operation Aging: explicit <paramref name="requestedToken"/> if valid for user and materialized; else latest upload with CSV on disk.
    /// </summary>
    private async Task<string?> ResolveAgingReportTokenAsync(
        Guid userId,
        string? requestedToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedToken)
            && _sessionStore.IsValidTokenFormat(requestedToken)
            && await _db.ReportUploads.AsNoTracking()
                .AnyAsync(u => u.Token == requestedToken && u.UserId == userId, cancellationToken)
            && _sessionStore.TryGetCsvPath(requestedToken, out _))
            return requestedToken;

        var latestUpload = await _db.ReportUploads
            .AsNoTracking()
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.UploadedUtc)
            .Select(u => u.Token)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestUpload is not null && _sessionStore.TryGetCsvPath(latestUpload, out _))
            return latestUpload;

        return null;
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

        foreach (var token in candidateTokens)
        {
            if (!_sessionStore.TryGetCsvPath(token, out _))
                continue;

            var session = await _sessionStore.LoadAsync(token, cancellationToken);
            if (session?.OperationalReportKind == requiredKind)
                return token;
        }

        return null;
    }

    private async Task SaveOperationalStateAsync(
        string token,
        ReportSessionData session,
        OperationalReportPanelViewModel report,
        CancellationToken cancellationToken)
    {
        session.OperationalReportKind = report.ReportKind;
        session.OperationalSelectedPerformanceGroup = string.IsNullOrWhiteSpace(report.SelectedPerformanceGroup)
            ? null
            : report.SelectedPerformanceGroup;
        if (report.ReportKind == OperationalReportKind.AlarmHistory)
        {
            session.OperationalAlarmPeriodFilter = string.IsNullOrWhiteSpace(report.SelectedPeriod) ? "1hour" : report.SelectedPeriod;
            session.OperationalAlarmDateFilterMode = string.IsNullOrWhiteSpace(report.DateFilterMode) ? "all" : report.DateFilterMode;
            session.OperationalAlarmSelectedDate = report.SelectedDate;
            session.OperationalAlarmDateRangeStart = report.DateRangeStart;
            session.OperationalAlarmDateRangeEnd = report.DateRangeEnd;
        }
        else if (report.ReportKind == OperationalReportKind.PerformanceHistory)
        {
            session.OperationalPerformancePeriodFilter = string.IsNullOrWhiteSpace(report.SelectedPeriod) ? "1hour" : report.SelectedPeriod;
            session.OperationalPerformanceDateFilterMode = string.IsNullOrWhiteSpace(report.DateFilterMode) ? "all" : report.DateFilterMode;
            session.OperationalPerformanceSelectedDate = report.SelectedDate;
            session.OperationalPerformanceDateRangeStart = report.DateRangeStart;
            session.OperationalPerformanceDateRangeEnd = report.DateRangeEnd;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(userId, out var parsedUserId))
            return;

        var upload = await _db.ReportUploads.FirstOrDefaultAsync(
            item => item.Token == token && item.UserId == parsedUserId,
            cancellationToken);
        if (upload is null)
            return;

        upload.SessionJson = JsonSerializer.Serialize(session, ReportSessionJson.Options);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private string GetDisplayName(string token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(userId, out var parsedUserId))
            return "Operational report";

        var upload = _db.ReportUploads
            .AsNoTracking()
            .FirstOrDefault(item => item.Token == token && item.UserId == parsedUserId);
        return upload?.OriginalFileName ?? "Operational report";
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
        {
            return "single";
        }

        if (string.Equals(fallbackMode, "range", StringComparison.OrdinalIgnoreCase)
            && hasRangeStart && hasRangeEnd)
        {
            return "range";
        }

        return "all";
    }
    [HttpGet("Dashboard/{token}/RecurringTickets")]
    public async Task<IActionResult> GetRecurringTickets(
        string token,
        string filterMode = "all",
        string? selectedDate = null,
        string? dateRangeStart = null,
        string? dateRangeEnd = null,
        int page = 1,
        int pageSize = 20,
        int? minGap = null,
        int? maxGap = null)
    {
        if (!_sessionStore.TryGetCsvPath(token, out var csvPath))
            return NotFound("Session expired or CSV not found.");

        DateOnly? s = string.IsNullOrEmpty(selectedDate) ? null : DateOnly.Parse(selectedDate);
        DateOnly? rs = string.IsNullOrEmpty(dateRangeStart) ? null : DateOnly.Parse(dateRangeStart);
        DateOnly? re = string.IsNullOrEmpty(dateRangeEnd) ? null : DateOnly.Parse(dateRangeEnd);

        var (items, total, summary) = await _csvService.GetPaginatedRecurringTicketsAsync(
            csvPath, filterMode, s, rs, re, page, pageSize, minGap, maxGap, HttpContext.RequestAborted);

        return Json(new { items, total, summary });
    }

    [HttpGet("Dashboard/{token}/ExportRecurringTickets")]
    public async Task<IActionResult> ExportRecurringTickets(
        string token,
        string format = "csv",
        string filterMode = "all",
        string? selectedDate = null,
        string? dateRangeStart = null,
        string? dateRangeEnd = null,
        int? minGap = null,
        int? maxGap = null)
    {
        if (!_sessionStore.TryGetCsvPath(token, out var csvPath))
            return NotFound("Session expired or CSV not found.");

        DateOnly? s = string.IsNullOrEmpty(selectedDate) ? null : DateOnly.Parse(selectedDate);
        DateOnly? rs = string.IsNullOrEmpty(dateRangeStart) ? null : DateOnly.Parse(dateRangeStart);
        DateOnly? re = string.IsNullOrEmpty(dateRangeEnd) ? null : DateOnly.Parse(dateRangeEnd);

        var filtered = await _csvService.GetFilteredRecurringTicketsAsync(
            csvPath, filterMode, s, rs, re, minGap, maxGap, HttpContext.RequestAborted);

        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var stream = await _csvService.GenerateRecurringTicketsXlsxAsync(filtered);
            return File(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"RecurringTickets_{token}.xlsx");
        }
        else
        {
            var stream = await _csvService.GenerateRecurringTicketsCsvAsync(filtered);
            return File(stream, "text/csv", $"RecurringTickets_{token}.csv");
        }
    }
}
