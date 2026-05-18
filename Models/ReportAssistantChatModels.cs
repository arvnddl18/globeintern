namespace SlotAd_Globe.Models;

public class ReportAssistantChatMessageDto
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class ReportAssistantChatRequest
{
    public List<ReportAssistantChatMessageDto> Messages { get; set; } = [];
    public ReportAssistantPageKind PageKind { get; set; }
    public string? Token { get; set; }
    public string? View { get; set; }
}

public class ReportAssistantChatResponse
{
    public string Reply { get; set; } = "";
    public bool UsedModel { get; set; }
    public string? Error { get; set; }
}

public class ReportAssistantContextResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public object? Context { get; set; }
}
