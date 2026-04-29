namespace SlotAd_Globe.Models;

public class CleanedDataSummary
{
    public int TotalRowsProcessed { get; set; }
    public int NewRowsAdded { get; set; }
    public int DuplicateRowsSkipped { get; set; }
    public int TotalCleanedRowsNow { get; set; }
}
