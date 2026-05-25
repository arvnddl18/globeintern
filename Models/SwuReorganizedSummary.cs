namespace SlotAd_Globe.Models;

public class SwuReorganizedSummary
{
    public int FilesProcessed { get; set; }
    public int TotalPolesExtracted { get; set; }
    public int PolesFromLastFile { get; set; }
    public List<string> SourceFileNames { get; set; } = [];
    public string? BatchId { get; set; }
}
