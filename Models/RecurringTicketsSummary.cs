namespace SlotAd_Globe.Models;

public class RecurringTicketsSummary
{
    public int TotalRecurringTickets { get; set; }
    
    public List<TopRankItem> TopNaps { get; set; } = new();
    public List<TopRankItem> TopCabinets { get; set; } = new();
    public List<TopRankItem> TopTechTeams { get; set; } = new();
}

public class TopRankItem
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}
