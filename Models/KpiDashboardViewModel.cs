namespace SlotAd_Globe.Models;

public class KpiDashboardViewModel
{
    /// <summary>Magic-link token for bookmarking and POST actions.</summary>
    public string ReportToken { get; set; } = string.Empty;
    public string DateFilterMode { get; set; } = "all";
    public string? SelectedDate { get; set; }
    public string? DateRangeStart { get; set; }
    public string? DateRangeEnd { get; set; }
    public List<string> SelectedTerritories { get; set; } = [];
    public List<string> SelectedStatuses { get; set; } = [];
    public List<string> SelectedSubStatuses { get; set; } = [];
    public List<string> SelectedSkillsets { get; set; } = [];
    public List<string> SelectedOrderCreateDates { get; set; } = [];

    public List<string> AvailableDates { get; set; } = [];
    public List<string> AvailableTerritories { get; set; } = [];
    public List<string> AvailableStatuses { get; set; } = [];
    public List<string> AvailableSubStatuses { get; set; } = [];
    public List<string> AvailableSkillsets { get; set; } = [];
    public List<string> AvailableOrderCreateDates { get; set; } = [];

    public int TotalAppointments { get; set; }
    public int UniqueTerritoriesCount { get; set; }
    public int UniqueSkillsetsCount { get; set; }
    public string DateRangeDisplay { get; set; } = string.Empty;

    public Dictionary<string, int> StatusDistribution { get; set; } = new();
    public Dictionary<string, int> SubStatusDistribution { get; set; } = new();
    public Dictionary<string, int> TerritoryDistribution { get; set; } = new();
    public Dictionary<string, int> SkillsetDistribution { get; set; } = new();
    public Dictionary<string, int> AppointmentsByDate { get; set; } = new();

    public int AmSlotCount { get; set; }
    public int PmSlotCount { get; set; }

    /// <summary>Per-skillset AM/PM slot counts for the current filter set.</summary>
    public Dictionary<string, Dictionary<string, int>> SkillsetBySlot { get; set; } = new();

    public int DelayedCount { get; set; }
    public int LapsedCount { get; set; }
    public int ForVisitSubStatusCount { get; set; }
    public int ForRescheduleSubStatusCount { get; set; }
    public int RepairSkillsetCount { get; set; }
    public int CompletedStatusCount { get; set; }

    /// <summary>All Status compliance (slot / completion rules). N/A rows excluded from pass rate denominator.</summary>
    public int CompliancePassCount { get; set; }
    public int ComplianceFailCount { get; set; }
    public int ComplianceNaCount { get; set; }
    public int CompliancePassAmCount { get; set; }
    public int CompliancePassPmCount { get; set; }
    public int ComplianceFailAmCount { get; set; }
    public int ComplianceFailPmCount { get; set; }
    public Dictionary<string, int> ComplianceFailReasons { get; set; } = new();

    /// <summary>Per appointment-date scheduled / pass / fail for slot adherence chart and assistant.</summary>
    public Dictionary<string, SlotAdherenceDayMetrics> SlotAdherenceByDate { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Top delay reasons aggregated from the delayreason column, ordered by count descending.
    /// Empty when the column is absent from the source file.
    /// </summary>
    public List<KeyValuePair<string, int>> TopDelayReasons { get; set; } = [];

    /// <summary>pending | status — set by controller for tabs.</summary>
    public string ActiveDashboardView { get; set; } = "pending";

    public CsvSourceKind CsvSourceKind { get; set; } = CsvSourceKind.Pending;
    public bool ComplianceMetricsAvailable { get; set; }

    public List<Dictionary<string, string>> PreviewRows { get; set; } = [];
    public int TotalFilteredRows { get; set; }

    /// <summary>Recent uploads for the signed-in user (dashboard history picker).</summary>
    public List<ReportHistoryItem> ReportHistory { get; set; } = [];

    /// <summary>True when the source CSV was removed (FIFO); filters cannot be changed.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Per-NAP dot coordinates extracted from lat/lng columns in the CSV.
    /// Each float[3]: [latitude, longitude, dateInt] where dateInt = (year-2000)*10000 + month*100 + day.
    /// Empty when the CSV does not contain recognised coordinate columns.
    /// Capped at 12 000 dots to keep page size reasonable.
    /// </summary>
    public List<float[]> NapDots { get; set; } = [];

    /// <summary>
    /// Facility names parallel to <see cref="NapDots"/>. Index i corresponds to NapDots[i].
    /// Empty string when the column is absent or the value is blank.
    /// </summary>
    public List<string> NapDotNames { get; set; } = [];

    /// <summary>
    /// Raw dpid values parallel to <see cref="NapDots"/>. Index i corresponds to NapDots[i].
    /// Empty string when the dpid column is absent or the value is blank.
    /// Used client-side to filter pins against an uploaded NAP reference file.
    /// </summary>
    public List<string> NapDotDpids { get; set; } = [];

    /// <summary>
    /// Skillset values parallel to <see cref="NapDots"/>. Index i corresponds to NapDots[i].
    /// Empty string when the column is absent. Used client-side to recompute Repair/Install counts
    /// after a NAP reference filter is applied.
    /// </summary>
    public List<string> NapDotSkillsets { get; set; } = [];

    /// <summary>
    /// Territory values parallel to <see cref="NapDots"/>. Index i corresponds to NapDots[i].
    /// Used client-side for the independent Heatmap territory filter.
    /// </summary>
    public List<string> NapDotTerritories { get; set; } = [];

    /// <summary>
    /// Status values parallel to <see cref="NapDots"/>. Index i corresponds to NapDots[i].
    /// Used client-side for the independent Heatmap status filter.
    /// </summary>
    public List<string> NapDotStatuses { get; set; } = [];

    /// <summary>True when the uploaded CSV contained recognisable latitude/longitude columns.</summary>
    public bool HasCoordinates { get; set; }

    // ── Heatmap-specific data: always unfiltered (not affected by Slot Adherence filters) ──

    public List<float[]> HeatmapNapDots { get; set; } = [];
    public List<string> HeatmapNapDotNames { get; set; } = [];
    public List<string> HeatmapNapDotDpids { get; set; } = [];
    public List<string> HeatmapNapDotSkillsets { get; set; } = [];
    public List<string> HeatmapNapDotTerritories { get; set; } = [];
    public List<string> HeatmapNapDotStatuses { get; set; } = [];
    public bool HeatmapHasCoordinates { get; set; }
    public int HeatmapTotalAppointments { get; set; }
    public int HeatmapRepairCount { get; set; }
    public int HeatmapInstallCount { get; set; }
    public Dictionary<string, int> HeatmapTerritoryDistribution { get; set; } = new();
    public Dictionary<string, int> HeatmapAppointmentsByDate { get; set; } = new();

    // ── Heatmap join rows (full appointment-level rows, not limited to geocoded points) ──
    public List<int> HeatmapJoinDateInts { get; set; } = [];
    public List<string> HeatmapJoinDpids { get; set; } = [];
    public List<string> HeatmapJoinFixDescriptions { get; set; } = [];
    public List<string> HeatmapJoinTerritories { get; set; } = [];
    public List<string> HeatmapJoinSkillsets { get; set; } = [];
    public List<string> HeatmapJoinStatuses { get; set; } = [];

    // ── Recurring Heatmap-specific data (independent from Total NAPs heatmap) ──
    public List<int> RecurringHeatmapDateInts { get; set; } = [];
    public List<string> RecurringHeatmapFacilityNames { get; set; } = [];
    public List<string> RecurringHeatmapDpids { get; set; } = [];
    public List<string> RecurringHeatmapServiceIds { get; set; } = [];
    public List<string> RecurringHeatmapCustomerNames { get; set; } = [];
    public List<string> RecurringHeatmapAddresses { get; set; } = [];
    public List<string> RecurringHeatmapTerritories { get; set; } = [];
    public List<string> RecurringHeatmapInitialDates { get; set; } = [];
    public List<string> RecurringHeatmapInitialWorkOrders { get; set; } = [];
    public List<string> RecurringHeatmapInitialSkillsets { get; set; } = [];
    public List<string> RecurringHeatmapRecurringDates { get; set; } = [];
    public List<string> RecurringHeatmapRecurringWorkOrders { get; set; } = [];
    public List<string> RecurringHeatmapRecurringSkillsets { get; set; } = [];
    public List<string> RecurringHeatmapRecurringStatuses { get; set; } = [];
    public List<int> RecurringHeatmapGaps { get; set; } = [];
    public int RecurringHeatmapTotalAppointments { get; set; }
    public int RecurringHeatmapRepairCount { get; set; }
    public int RecurringHeatmapInstallCount { get; set; }

    // ── Recurring Tickets: service IDs that had Install/Repair then another Repair later ──
    public List<RecurringTicketRow> RecurringTickets { get; set; } = [];
}
