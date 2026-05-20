namespace SlotAd_Globe.Models;

/// <summary>Daily slot-adherence counts (matches Status dashboard chart: scheduled / pass / fail).</summary>
public sealed class SlotAdherenceDayMetrics
{
    public int Scheduled { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
}
