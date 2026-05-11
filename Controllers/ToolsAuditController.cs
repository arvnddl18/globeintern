using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SlotAd_Globe.Models;
using SlotAd_Globe.Services;

namespace SlotAd_Globe.Controllers;

[Authorize]
[Route("[controller]")]
public class ToolsAuditController : Controller
{
    private readonly IToolsAuditService _toolsAudit;
    private readonly ILogger<ToolsAuditController> _logger;

    public ToolsAuditController(IToolsAuditService toolsAudit, ILogger<ToolsAuditController> logger)
    {
        _toolsAudit = toolsAudit;
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Challenge();

        var latest = await _toolsAudit.GetLatestSessionIdForUserAsync(userId, cancellationToken);
        if (latest.HasValue)
            return RedirectToAction(nameof(Session), new { id = latest.Value });
        return RedirectToAction(nameof(Upload));
    }

    [HttpGet("[action]")]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "Tools Audit Upload";
        ViewData["ActiveTab"] = "kpi";
        if (!TryGetCurrentUserId(out var userId))
            return Challenge();

        var history = await _toolsAudit.ListHistoryForUserAsync(userId, cancellationToken);
        return View(new ToolsAuditUploadViewModel { History = [.. history] });
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Upload(IFormFile? xlsxFile)
    {
        if (xlsxFile is null || xlsxFile.Length == 0)
        {
            TempData["Error"] = "Please choose an XLSX file.";
            return RedirectToAction(nameof(Upload));
        }

        var ext = Path.GetExtension(xlsxFile.FileName);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Please upload an .xlsx file (Excel).";
            return RedirectToAction(nameof(Upload));
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            TempData["Error"] = "You must be logged in.";
            return RedirectToAction(nameof(Upload));
        }

        try
        {
            await using var stream = xlsxFile.OpenReadStream();
            var sessionId = await _toolsAudit.ImportFromXlsxAsync(stream, xlsxFile.FileName, userId, HttpContext.RequestAborted);
            TempData["Success"] = "Tools audit uploaded successfully.";
            return RedirectToAction(nameof(Session), new { id = sessionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tools audit upload failed");
            TempData["Error"] = $"Upload failed: {ex.Message}";
            return RedirectToAction(nameof(Upload));
        }
    }

    [HttpGet("Session/{id:guid}")]
    public async Task<IActionResult> Session(
        Guid id,
        [FromQuery] string[]? statuses = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] string? partial = null)
    {
        ViewData["Title"] = "Tools Audit";
        ViewData["ActiveTab"] = "kpi";
        var vm = await _toolsAudit.GetSessionAsync(
            id,
            statuses,
            sortBy,
            sortDir,
            HttpContext.RequestAborted);
        if (vm is null)
            return NotFound();
        if (string.Equals(partial, "toolsSummary", StringComparison.OrdinalIgnoreCase))
            return PartialView("_ToolsSummary", vm);
        return View(vm);
    }

    private static bool TryGetCurrentUserId(HttpContext? http, out Guid userId)
    {
        userId = default;
        var id = http?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out userId);
    }

    private bool TryGetCurrentUserId(out Guid userId) => TryGetCurrentUserId(HttpContext, out userId);
}

