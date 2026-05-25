namespace SlotAd_Globe.Models;

public class FilterOptionsViewModel
{
    /// <summary>Magic-link token identifying the uploaded report on disk.</summary>
    public string ReportToken { get; set; } = string.Empty;

    public List<string> AvailableDates { get; set; } = [];
    public List<string> AvailableTerritories { get; set; } = [];
    public List<string> AvailableStatuses { get; set; } = [];
    public List<string> AvailableSubStatuses { get; set; } = [];
    public List<string> AvailableSkillsets { get; set; } = [];
    public List<string> AvailableCustomerTypes { get; set; } = [];
    public List<string> AvailableOrderCreateDates { get; set; } = [];

    // "all" | "single" | "range"
    public string DateFilterMode { get; set; } = "all";
    public string? SelectedDate { get; set; }
    public string? DateRangeStart { get; set; }
    public string? DateRangeEnd { get; set; }

    public List<string> SelectedTerritories { get; set; } = [];
    public List<string> SelectedStatuses { get; set; } = [];
    public List<string> SelectedSubStatuses { get; set; } = [];
    public List<string> SelectedSkillsets { get; set; } = [];
    public List<string> SelectedCustomerTypes { get; set; } = [];
    public List<string> SelectedOrderCreateDates { get; set; } = [];

    /// <summary>When posting from dashboard filter modal: pending | status.</summary>
    public string ActiveDashboardView { get; set; } = "pending";
}
