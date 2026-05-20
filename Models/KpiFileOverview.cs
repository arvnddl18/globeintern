namespace SlotAd_Globe.Models;

/// <summary>Unfiltered scan of an uploaded KPI CSV for the report assistant.</summary>
public sealed class KpiFileOverview
{
    public int TotalCsvRows { get; set; }
    public int RowsWithAppointmentDate { get; set; }
    public string? AppointmentDateMin { get; set; }
    public string? AppointmentDateMax { get; set; }
    public int DistinctAppointmentDates { get; set; }
    public Dictionary<string, int> AppointmentsByDate { get; set; } = new();
}
