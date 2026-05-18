using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SlotAd_Globe.Models;
using SlotAd_Globe.Services;

namespace SlotAd_Globe.Controllers;

[Authorize]
[Route("Report/Assistant")]
[EnableRateLimiting("report-assistant")]
public class ReportAssistantController : Controller
{
    private readonly IReportAssistantService _assistant;
    private readonly IReportAssistantContextFactory _contextFactory;

    public ReportAssistantController(
        IReportAssistantService assistant,
        IReportAssistantContextFactory contextFactory)
    {
        _assistant = assistant;
        _contextFactory = contextFactory;
    }

    [HttpPost("Chat")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Chat([FromBody] ReportAssistantChatRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new ReportAssistantChatResponse { Error = "invalid_body", Reply = "Invalid request." });

        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _assistant.ChatAsync(userId, request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("Context")]
    public async Task<IActionResult> Context(
        [FromQuery] ReportAssistantPageKind pageKind,
        [FromQuery] string? token,
        [FromQuery] string? view,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            var ctx = await _contextFactory.BuildContextAsync(userId, pageKind, token, view, cancellationToken);
            return Ok(new ReportAssistantContextResponse { Ok = true, Context = ctx });
        }
        catch (Exception)
        {
            return Ok(new ReportAssistantContextResponse { Ok = false, Error = "context_failed" });
        }
    }

    private bool TryGetUserId(out Guid userId)
    {
        userId = default;
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out userId);
    }
}
