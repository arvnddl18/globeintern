namespace SlotAd_Globe.Models;

public class TechnicianProductivityRow
{
    public string TechnicianName { get; set; } = "";
    public bool IsRescueTechnician { get; set; }
    public string Status { get; set; } = "";
    public DateTime? CheckInTime { get; set; }
    public DateTime? CheckOutTime { get; set; }

    public int TotalWorkOrdersToday { get; set; }
    public int Ongoing { get; set; }
    public int Pending { get; set; }
    public int Completed { get; set; }
    public int Cancelled { get; set; }
    public int Delayed { get; set; }
    public int OnHold { get; set; }
    public int RescheduledWithNewAppointmentDate { get; set; }
    public int DelayedWithNewAppointmentDate { get; set; }
}