using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlotAd_Globe.Services;

internal static class ReportSessionJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
