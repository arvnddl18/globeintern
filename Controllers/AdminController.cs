using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;
using SlotAd_Globe.Services;

namespace SlotAd_Globe.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("[controller]")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext db, ILogger<AdminController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("[action]")]
    public IActionResult CreateUser()
    {
        return View(new AdminCreateUserViewModel());
    }

    [HttpPost("[action]")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(AdminCreateUserViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(model);

        var name = model.UserName.Trim();
        if (await _db.Users.AnyAsync(u => u.UserName == name, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.UserName), "That username is already taken.");
            return View(model);
        }

        var (hashB64, saltB64) = Sha256PasswordHasher.CreateHash(model.Password);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = name,
            PasswordHash = hashB64,
            PasswordSalt = saltB64,
            IsAdmin = model.GrantAdmin,
            CreatedUtc = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin created user {UserName} (SHA-256, IsAdmin={IsAdmin})", name, user.IsAdmin);

        TempData["Success"] = $"User “{name}” was created. Password is stored as SHA-256 (with salt).";
        return RedirectToAction(nameof(CreateUser));
    }
}
