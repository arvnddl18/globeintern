using System.IO;

namespace SlotAd_Globe.Options;

public class ReportSessionOptions
{
    public const string SectionName = "ReportSessions";

    /// <summary>Directory under content root where report CSVs and session JSON are stored.</summary>
    public string ReportsDirectory { get; set; } = Path.Combine("App_Data", "reports");

    /// <summary>Sessions older than this are deleted on cleanup (upload / scheduled touch).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Global cap per <see cref="SlotAd_Globe.Models.CsvSourceKind"/> (All Pending vs All Status). Oldest uploads are removed when exceeded.
    /// </summary>
    public int MaxCsvUploadsPerKindGlobal { get; set; } = 2;

    /// <summary>
    /// Where uploaded CSV files are stored on disk (per session token). If empty, uses
    /// <c>%LocalAppData%\SlotAd-Globe\report_materialize</c> so uploads are not written through
    /// OneDrive/Desktop sync folders (which can throttle I/O to tens of KB/s and make the browser upload crawl).
    /// Set an absolute path or a path under the site content root for a custom location.
    /// </summary>
    public string? MaterializedCsvDirectory { get; set; }

    /// <summary>
    /// Maximum HTTP request body size for multipart CSV uploads (Kestrel + form limits).
    /// When hosting on IIS, set <c>system.webServer/security/requestFiltering/requestLimits/@maxAllowedContentLength</c>
    /// in <c>web.config</c> to the same value in bytes (or higher) or IIS returns 413 before the app runs.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 1_073_741_824L;
}
