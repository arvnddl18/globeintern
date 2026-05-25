namespace SlotAd_Globe.Models;

public sealed class ReportCsvQueryFilters
{
    public string? Skillset { get; set; }
    public string? Status { get; set; }
    public string? SubStatus { get; set; }
    public string? Territory { get; set; }
    public string? Slot { get; set; }
    public string? AddressContains { get; set; }
    public string? FacilityContains { get; set; }
    public string? AppointmentId { get; set; }
    public string? WorkOrderNumber { get; set; }
    /// <summary>yyyy-MM-dd — narrows to this appointment date (ignores session date filter).</summary>
    public string? AppointmentDate { get; set; }
    public string? OrderCreateDate { get; set; }
    /// <summary>Pass, Fail, or N/A — requires All Status CSV with completion date column.</summary>
    public string? Compliance { get; set; }
    public string? CustomerNameContains { get; set; }
    public string? ServiceIdNumber { get; set; }
    public string? TeamContains { get; set; }
    public string? DelayCode { get; set; }
    public string? Technology { get; set; }
    public string? CustomerType { get; set; }
    public string? Queue { get; set; }
    public string? CabinetId { get; set; }
    public string? ContractorName { get; set; }
    public string? SourceSystem { get; set; }
    /// <summary>Case-insensitive column name → substring to match (for uncommon CSV columns).</summary>
    public Dictionary<string, string> ColumnContains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReportCsvSessionFilterParams
{
    public string DateFilterMode { get; set; } = "all";
    public DateOnly? SelectedDate { get; set; }
    public DateOnly? DateRangeStart { get; set; }
    public DateOnly? DateRangeEnd { get; set; }
    public List<string> SelectedTerritories { get; set; } = [];
    public List<string> SelectedStatuses { get; set; } = [];
    public List<string> SelectedSubStatuses { get; set; } = [];
    public List<string> SelectedSkillsets { get; set; } = [];
    public List<string> SelectedCustomerTypes { get; set; } = [];
    public List<string> SelectedOrderCreateDates { get; set; } = [];
}

public sealed class ReportCsvQueryRequest
{
    public string? InterpretedAs { get; set; }
    public ReportCsvQueryFilters ExtraFilters { get; set; } = new();
    public string? GroupBy { get; set; }
    public int MaxSampleRows { get; set; }
}

public sealed class ReportCsvQueryResult
{
    public bool Ran { get; set; }
    public string? InterpretedAs { get; set; }
    public int TotalFilteredRows { get; set; }
    public int MatchedRows { get; set; }
    public Dictionary<string, int>? Breakdown { get; set; }
    public List<Dictionary<string, string>>? SampleRows { get; set; }
    public Dictionary<string, object?>? FiltersApplied { get; set; }
    public string? Note { get; set; }
}

public enum ReportAssistantQueryType
{
    None,
    KpiCsv,
    Recurring
}

public sealed class ReportRecurringQueryFilters
{
    public string? CustomerName { get; set; }
    public string? ServiceId { get; set; }
    public string? CabinetId { get; set; }
    public string? FacilityName { get; set; }
    public string? Team { get; set; }
    public string? Territory { get; set; }
}

public sealed class ReportRecurringQueryRequest
{
    public string? InterpretedAs { get; set; }
    public ReportRecurringQueryFilters Filters { get; set; } = new();
    public string? GroupBy { get; set; }
    public int MaxSampleRows { get; set; }
}

public sealed class ReportRecurringQueryResult
{
    public bool Ran { get; set; }
    public string? InterpretedAs { get; set; }
    public int TotalRecurringInstances { get; set; }
    public int MatchedRows { get; set; }
    public int DistinctServiceIds { get; set; }
    public Dictionary<string, int>? Breakdown { get; set; }
    public List<Dictionary<string, string>>? SampleRows { get; set; }
    public Dictionary<string, object?>? FiltersApplied { get; set; }
    public string? Note { get; set; }
}

public sealed class ReportAssistantQueryPlan
{
    public bool ShouldQuery { get; set; }
    public ReportAssistantQueryType QueryType { get; set; }
    public ReportCsvQueryRequest? KpiRequest { get; set; }
    public ReportRecurringQueryRequest? RecurringRequest { get; set; }
}
