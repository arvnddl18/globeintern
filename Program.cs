using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SlotAd_Globe.Authorization;
using SlotAd_Globe.Data;
using SlotAd_Globe.Options;
using SlotAd_Globe.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(dataDir);

var reportSessionsSection = builder.Configuration.GetSection(ReportSessionOptions.SectionName);
builder.Services.Configure<ReportSessionOptions>(reportSessionsSection);
var maxRequestBodyBytes = reportSessionsSection.GetValue<long?>(nameof(ReportSessionOptions.MaxRequestBodyBytes));
if (maxRequestBodyBytes is null or <= 0)
    maxRequestBodyBytes = new ReportSessionOptions().MaxRequestBodyBytes;

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxRequestBodyBytes.Value;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = 1024 * 1024;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is missing. Set it in appsettings.json or user secrets.");
    options.UseSqlServer(connectionString, sql =>
    {
        sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(4), null);
        sql.CommandTimeout(300);
    });
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireClaim(AdminClaimTypes.IsAdmin, "true"));
});

builder.Services.AddScoped<IReportSessionStore, DatabaseReportSessionStore>();
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<ICsvProcessingService, CsvProcessingService>();
builder.Services.AddScoped<IOperationalReportService, OperationalReportService>();
builder.Services.AddScoped<IReportDashboardArchiveRecorder, ReportDashboardArchiveRecorder>();
builder.Services.AddScoped<IToolsAuditService, ToolsAuditService>();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxRequestBodyBytes.Value;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
    // Large multipart uploads can pause briefly while writing to disk; the default minimum rate can drop the connection.
    options.Limits.MinRequestBodyDataRate = null;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    DbSeeder.SeedAsync(db, cfg).GetAwaiter().GetResult();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// #region agent log
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var isReportDashboard = path.StartsWith("/Report/Dashboard", StringComparison.OrdinalIgnoreCase);
    await next();
    if (!isReportDashboard)
        return;
    try
    {
        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var line = JsonSerializer.Serialize(new
        {
            sessionId = "22a3ab",
            hypothesisId = "H1-H2-H5",
            location = "Program.cs:ReportDashboardMiddleware",
            message = "request_completed",
            data = new
            {
                method = context.Request.Method,
                path,
                status = context.Response.StatusCode
            },
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await File.AppendAllTextAsync(Path.Combine(env.ContentRootPath, "debug-22a3ab.log"), line + "\n");
    }
    catch
    {
        /* ignore debug log failures */
    }
});
// #endregion

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Report}/{action=Upload}/{id?}");
app.MapControllers();

app.Run();
