using System.ComponentModel.DataAnnotations;

namespace SlotAd_Globe.Models;

public sealed class SlotAdherenceExportRequest
{
    [Required]
    public string ReportToken { get; set; } = string.Empty;

    public string? View { get; set; }

    public string? ChartImagesJson { get; set; }
}

public sealed class SlotAdherenceChartImage
{
    public string ChartKey { get; set; } = string.Empty;
    public string ChartTitle { get; set; } = string.Empty;
    public string DataUrl { get; set; } = string.Empty;
}
