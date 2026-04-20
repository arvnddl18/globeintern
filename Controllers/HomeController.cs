using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;
using SlotAd_Globe.Services;

namespace SlotAd_Globe.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IReportSessionStore _sessionStore;
    private readonly AppDbContext _db;

    public HomeController(ILogger<HomeController> logger, IReportSessionStore sessionStore, AppDbContext db)
    {
        _logger = logger;
        _sessionStore = sessionStore;
        _db = db;
    }

    [HttpGet("/")]
    [Authorize]
    public async Task<IActionResult> Index()
    {
        _sessionStore.CleanupExpiredSessions();
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return RedirectToAction(nameof(AccountController.Login), "Account");

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.LastReportUploadId is Guid lastId)
        {
            var last = await _db.ReportUploads.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == lastId && r.UserId == userId);
            if (last is not null)
                return RedirectToAction(nameof(ReportController.Dashboard), "Report", new { token = last.Token });
        }

        var latest = await _db.ReportUploads.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.UploadedUtc)
            .FirstOrDefaultAsync();
        if (latest is not null)
            return RedirectToAction(nameof(ReportController.Dashboard), "Report", new { token = latest.Token });

        return RedirectToAction(nameof(ReportController.Upload), "Report");
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
