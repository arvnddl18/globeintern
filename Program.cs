using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlotAd_Globe.Authorization;
using SlotAd_Globe.Data;
using SlotAd_Globe.Options;
using SlotAd_Globe.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(dataDir);

builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

builder.Services.AddMemoryCache();
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.SectionName));
builder.Services.AddHttpClient("OpenRouter", (sp, client) =>
{
    var o = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
    var baseUrl = (o.BaseUrl ?? "https://openrouter.ai/api/v1/").TrimEnd('/') + "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.RequestTimeoutSeconds, 30, 600));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("report-assistant", httpContext =>
    {
        var uid = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var partition = uid ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 40,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

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
builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddScoped<ICsvProcessingService, CsvProcessingService>();
builder.Services.AddScoped<ISwuPoleProcessingService, SwuPoleProcessingService>();
builder.Services.AddScoped<IOperationalReportService, OperationalReportService>();
builder.Services.AddScoped<IReportDashboardArchiveRecorder, ReportDashboardArchiveRecorder>();
builder.Services.AddScoped<IToolsAuditService, ToolsAuditService>();
builder.Services.AddScoped<IReportAssistantContextFactory, ReportAssistantContextFactory>();
builder.Services.AddScoped<IReportCsvQueryService, ReportCsvQueryService>();
builder.Services.AddScoped<IReportRecurringQueryService, ReportRecurringQueryService>();
builder.Services.AddScoped<IReportAssistantQueryPlanner, ReportAssistantQueryPlanner>();
builder.Services.AddScoped<IReportAssistantService, ReportAssistantService>();

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
app.UseRateLimiter();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Report}/{action=Upload}/{id?}");
app.MapControllers();

app.Run();
