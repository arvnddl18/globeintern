namespace SlotAd_Globe.Models;

public sealed class KpiCsvColumnProfile
{
    public string Name { get; set; } = "";
    public int NonEmptyRows { get; set; }
    public int DistinctValues { get; set; }
    public bool DistinctValuesCapped { get; set; }
    public List<KeyValuePair<string, int>> TopValues { get; set; } = [];
}

/// <summary>Single-pass scan of an uploaded KPI CSV for the report assistant.</summary>
public sealed class KpiCsvAssistantCatalog
{
    public KpiFileOverview Overview { get; set; } = new();
    public List<string> AllColumnNames { get; set; } = [];
    public List<KpiCsvColumnProfile> ColumnProfiles { get; set; } = [];
}
