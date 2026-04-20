using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SlotAd_Globe.Authorization;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;
using SlotAd_Globe.Services;

namespace SlotAd_Globe.Controllers;

public class AccountController : Controller
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountController> _logger;

    public AccountController(AppDbContext db, IConfiguration configuration, ILogger<AccountController> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["AllowRegistration"] = _configuration.GetValue("Auth:AllowRegistration", false);
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        ViewData["AllowRegistration"] = _configuration.GetValue("Auth:AllowRegistration", false);
        if (!ModelState.IsValid)
            return View(model);

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.UserName == model.UserName.Trim(),
            cancellationToken);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            ViewData["AllowRegistration"] = _configuration.GetValue("Auth:AllowRegistration", false);
            return View(model);
        }

        if (!string.IsNullOrEmpty(user.PasswordSalt))
        {
            if (!Sha256PasswordHasher.Verify(model.Password, user.PasswordSalt, user.PasswordHash))
            {
                ModelState.AddModelError(string.Empty, "Invalid username or password.");
                ViewData["AllowRegistration"] = _configuration.GetValue("Auth:AllowRegistration", false);
                return View(model);
            }
        }
        else
        {
            var hasher = new PasswordHasher<AppUser>();
            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, model.Password);
            if (result == PasswordVerificationResult.Failed)
            {
                ModelState.AddModelError(string.Empty, "Invalid username or password.");
                ViewData["AllowRegistration"] = _configuration.GetValue("Auth:AllowRegistration", false);
                return View(model);
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, model.Password);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName)
        };
        if (user.IsAdmin)
            claims.Add(new Claim(AdminClaimTypes.IsAdmin, "true"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal);

        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        return RedirectToAction(nameof(ReportController.Upload), "Report");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        if (!_configuration.GetValue("Auth:AllowRegistration", false))
            return NotFound();
        return View(new RegisterViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Auth:AllowRegistration", false))
            return NotFound();

        if (!ModelState.IsValid)
            return View(model);

        var name = model.UserName.Trim();
        if (await _db.Users.AnyAsync(u => u.UserName == name, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.UserName), "That username is already taken.");
            return View(model);
        }

        var hasher = new PasswordHasher<AppUser>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = name,
            CreatedUtc = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, model.Password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Registered user {UserName}", name);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return RedirectToAction(nameof(ReportController.Upload), "Report");
    }
}
