namespace SlotAd_Globe.Models;

/// <summary>Slot adherence (compliance) rules — must match CsvProcessingService.ClassifyCompliance*.</summary>
public static class ReportComplianceRulesReference
{
    public const string AmSlotMarkerDefault = "AM Slot";
    public const string CompletionPmCutoff = "12:59 PM";

    public static Dictionary<string, object?> ForAssistantContext(
        string amSlotMarker,
        int amLapseCutoffHour,
        int pmLapseCutoffHour) =>
        new()
        {
            ["summary"] =
                "Slot adherence Pass/Fail applies to All Status KPI files with a completion date column. " +
                "N/A rows have no parseable completion datetime and are excluded from pass-rate denominators.",
            ["appointmentSlot"] = new Dictionary<string, object?>
            {
                ["am"] = $"Appointment datetime contains '{amSlotMarker}' (case-insensitive); otherwise PM.",
                ["pm"] = "Appointment datetime does not contain the AM slot marker."
            },
            ["passRules"] = new[]
            {
                "Delayed status → Fail (reason: Delayed).",
                "Cancelled with For Reschedule sub-status → Fail (reason: Cancelled (Reschedule)).",
                "Completion date ≠ appointment date → Fail (reason: CompletedWrongDate).",
                "Appointment AM but completion time at or after 12:59 PM → Fail (reason: SlotMismatch).",
                "Appointment AM + completion before 12:59 PM same day → Pass.",
                "Appointment PM + completion same day (any time that day, including before 12:59 PM) → Pass."
            },
            ["failRules"] = new[]
            {
                "Delayed",
                "Cancelled (Reschedule)",
                "CompletedWrongDate",
                "SlotMismatch (AM appointment completed in PM window)"
            },
            ["naRules"] = new[]
            {
                "No parseable completion datetime on the row."
            },
            ["passRateFormula"] =
                "Pass rate % = Pass / (Pass + Fail). N/A is excluded from the rate denominator (same as dashboard KPI FIELD COUNTS).",
            ["delayedLapse"] = new Dictionary<string, object?>
            {
                ["amLapseCutoffHour"] = amLapseCutoffHour,
                ["pmLapseCutoffHour"] = pmLapseCutoffHour,
                ["note"] = "Delayed rows can be lapsed when last update hour ≥ slot cutoff; lapsed is tracked separately from compliance tier."
            },
            ["assistantQueryHint"] =
                "For total Pass on a specific date (no AM/PM split): use slotAdherenceByDate entry for that date (pass field) or queryResults with compliance=Pass and appointmentDate=yyyy-MM-dd only. " +
                "For Pass split by AM/PM: use queryResults with compliance=Pass, appointmentDate=yyyy-MM-dd, and groupBy slot (do NOT set slot=AM filter). " +
                "breakdown.AM and breakdown.PM are the Pass counts per slot. matchedRows is the total Pass for that date."
        };
}
