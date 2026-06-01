namespace SlotAd_Globe.Options;

public class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "poolside/laguna-xs.2:free";
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaxMessages { get; set; } = 24;
    public int MaxUserMessageLength { get; set; } = 4000;
    public int MaxCompletionTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.2;
}
