namespace SlotAd_Globe.Models;

/// <summary>
/// Represents a service ID that had an initial Install or Repair ticket
/// followed by another Repair ticket at a later date.
/// </summary>
public class RecurringTicketRow
{
    public string ServiceIdNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerAddress { get; set; } = "";
    public string Territory { get; set; } = "";
    public string FacilityName { get; set; } = "";
    public string DpId { get; set; } = "";
    public string CabinetId { get; set; } = "";
    public string Team { get; set; } = "";

    /// <summary>First Install/Repair ticket date (formatted).</summary>
    public string InitialTicketDate { get; set; } = "";
    public string InitialSkillset { get; set; } = "";
    public string InitialStatus { get; set; } = "";
    public string InitialAppointmentId { get; set; } = "";
    public string InitialWorkOrderNumber { get; set; } = "";

    /// <summary>The later Repair ticket date (formatted).</summary>
    public string RecurringTicketDate { get; set; } = "";
    public string RecurringSkillset { get; set; } = "";
    public string RecurringStatus { get; set; } = "";
    public string RecurringAppointmentId { get; set; } = "";
    public string RecurringWorkOrderNumber { get; set; } = "";

    /// <summary>Days between the initial ticket and the recurring ticket.</summary>
    public int DaysBetween { get; set; }
}
