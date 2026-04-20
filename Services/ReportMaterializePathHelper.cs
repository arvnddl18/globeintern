using Microsoft.AspNetCore.Hosting;
using SlotAd_Globe.Options;

namespace SlotAd_Globe.Services;

/// <summary>Resolves the directory where uploaded CSVs are materialized (same rules as <see cref="DatabaseReportSessionStore"/>).</summary>
public static class ReportMaterializePathHelper
{
    public static string GetMaterializeRoot(IWebHostEnvironment env, ReportSessionOptions options)
    {
        var configured = options.MaterializedCsvDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(env.ContentRootPath, configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SlotAd-Globe",
            "report_materialize");
    }
}
