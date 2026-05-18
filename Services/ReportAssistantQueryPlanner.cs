using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SlotAd_Globe.Models;
using SlotAd_Globe.Options;

namespace SlotAd_Globe.Services;

public sealed class ReportAssistantQueryPlanner : IReportAssistantQueryPlanner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SlotPattern = new(
        @"\b(am|pm)\s*slot\b|\bfrom\s+(am|pm)\b|\b(am|pm)\s+of\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex BarangayPattern = new(
        @"\bbarangay\s+([A-Za-z0-9][A-Za-z0-9\s\-']{1,40})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex InLocationPattern = new(
        @"\bin\s+([A-Z][A-Za-z0-9\s\-']{2,40})\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex FollowUpPattern = new(
        @"\b(of it|from it|for it|that|those|them)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex RecurringTopicPattern = new(
        @"\brecurring\b|\bre-?occur|\breoccur",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CabinetIdPattern = new(
        @"\b([A-Z]{3}_\d{3}_[A-Z0-9_]+)\b",
        RegexOptions.None,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ServiceIdPattern = new(
        @"\bservice\s*id\s*(?:is\s*)?(\d{6,14})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CustomerNamedPattern = new(
        @"\bcustomer\s+named\s+([A-Za-z][A-Za-z\s.'\-]{2,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> StatusKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Completed", "Delayed", "Cancelled", "Open", "Pending", "Ongoing", "Unassigned"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<OpenRouterOptions> _options;
    private readonly ILogger<ReportAssistantQueryPlanner> _logger;

    public ReportAssistantQueryPlanner(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenRouterOptions> options,
        ILogger<ReportAssistantQueryPlanner> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<ReportAssistantQueryPlan> PlanAsync(
        string userMessage,
        IReadOnlyList<ReportAssistantChatMessageDto> conversationHistory,
        object summaryContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return NoQuery();

        var context = ExtractContextHints(summaryContext);
        if (context.DataScope is not ("kpi" or "kpi_dashboard" or "kpi_filter"))
            return NoQuery();

        var combinedText = BuildCombinedText(userMessage, conversationHistory);

        var recurringPlan = TryPlanRecurring(userMessage, combinedText, conversationHistory, context);
        if (recurringPlan is not null)
            return recurringPlan;

        var filters = new ReportCsvQueryFilters();
        var interpretedParts = new List<string>();

        ApplySlot(combinedText, filters, interpretedParts);
        ApplySkillset(combinedText, context.KnownSkillsets, filters, interpretedParts);
        ApplyStatus(combinedText, context.KnownStatuses, filters, interpretedParts);
        ApplyTerritory(combinedText, context.KnownTerritories, filters, interpretedParts);
        ApplyBarangayOrLocation(userMessage, combinedText, filters, interpretedParts);
        ApplyFollowUpContext(combinedText, conversationHistory, context, filters, interpretedParts);

        string? groupBy = null;
        if (string.IsNullOrWhiteSpace(filters.Slot)
            && SlotPattern.IsMatch(userMessage)
            && !string.IsNullOrWhiteSpace(filters.Skillset))
        {
            groupBy = "slot";
            interpretedParts.Add("group by slot");
        }

        if (!HasAnyExtraFilter(filters) && groupBy is null)
        {
            var llmPlan = await TryPlanWithLlmAsync(userMessage, conversationHistory, cancellationToken);
            if (llmPlan is not null)
                return llmPlan;
            return NoQuery();
        }

        var interpretedAs = interpretedParts.Count > 0
            ? string.Join("; ", interpretedParts)
            : "Query uploaded KPI CSV with parsed filters";

        return new ReportAssistantQueryPlan
        {
            ShouldQuery = true,
            QueryType = ReportAssistantQueryType.KpiCsv,
            KpiRequest = new ReportCsvQueryRequest
            {
                InterpretedAs = interpretedAs,
                ExtraFilters = filters,
                GroupBy = groupBy,
                MaxSampleRows = WantsSampleRows(userMessage) ? 10 : 0
            }
        };
    }

    private static ReportAssistantQueryPlan? TryPlanRecurring(
        string userMessage,
        string combinedText,
        IReadOnlyList<ReportAssistantChatMessageDto> history,
        ContextHints context)
    {
        if (!RecurringTopicPattern.IsMatch(combinedText)
            && !userMessage.Contains("cabinet", StringComparison.OrdinalIgnoreCase)
            && !userMessage.Contains("facility", StringComparison.OrdinalIgnoreCase)
            && !ServiceIdPattern.IsMatch(combinedText)
            && !CustomerNamedPattern.IsMatch(userMessage))
            return null;

        var filters = new ReportRecurringQueryFilters();
        var interpretedParts = new List<string>();

        var customerMatch = CustomerNamedPattern.Match(userMessage);
        if (customerMatch.Success)
        {
            filters.CustomerName = customerMatch.Groups[1].Value.Trim().TrimEnd('?');
            interpretedParts.Add($"customer={filters.CustomerName}");
        }

        var serviceMatch = ServiceIdPattern.Match(combinedText);
        if (serviceMatch.Success)
        {
            filters.ServiceId = serviceMatch.Groups[1].Value.Trim();
            interpretedParts.Add($"serviceId={filters.ServiceId}");
        }
        else
        {
            foreach (var m in Regex.Matches(combinedText, @"\b(\d{9,12})\b"))
            {
                if (m is Match match)
                {
                    filters.ServiceId = match.Groups[1].Value;
                    interpretedParts.Add($"serviceId={filters.ServiceId}");
                    break;
                }
            }
        }

        var cabinetMatch = CabinetIdPattern.Match(userMessage);
        if (!cabinetMatch.Success)
            cabinetMatch = CabinetIdPattern.Match(combinedText);
        if (cabinetMatch.Success)
        {
            filters.CabinetId = cabinetMatch.Groups[1].Value;
            interpretedParts.Add($"cabinet={filters.CabinetId}");
        }

        if (string.IsNullOrWhiteSpace(filters.CustomerName))
        {
            foreach (var name in context.KnownRecurringCustomers.OrderByDescending(n => n.Length))
            {
                if (ContainsWholePhrase(combinedText, name))
                {
                    filters.CustomerName = name;
                    interpretedParts.Add($"customer={name}");
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(filters.CabinetId))
        {
            foreach (var cab in context.KnownRecurringCabinets.OrderByDescending(c => c.Length))
            {
                if (combinedText.Contains(cab, StringComparison.OrdinalIgnoreCase))
                {
                    filters.CabinetId = cab;
                    interpretedParts.Add($"cabinet={cab}");
                    break;
                }
            }
        }

        if (!HasAnyRecurringFilter(filters))
        {
            if (!RecurringTopicPattern.IsMatch(combinedText))
                return null;

            return new ReportAssistantQueryPlan
            {
                ShouldQuery = true,
                QueryType = ReportAssistantQueryType.Recurring,
                RecurringRequest = new ReportRecurringQueryRequest
                {
                    InterpretedAs = "Summarize all recurring ticket instances",
                    MaxSampleRows = 5
                }
            };
        }

        string? groupBy = null;
        if (userMessage.Contains("how many times", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(filters.CustomerName))
            groupBy = "serviceid";

        return new ReportAssistantQueryPlan
        {
            ShouldQuery = true,
            QueryType = ReportAssistantQueryType.Recurring,
            RecurringRequest = new ReportRecurringQueryRequest
            {
                InterpretedAs = interpretedParts.Count > 0
                    ? string.Join("; ", interpretedParts)
                    : "Query recurring tickets",
                Filters = filters,
                GroupBy = groupBy,
                MaxSampleRows = WantsSampleRows(userMessage) ? 10 : 0
            }
        };
    }

    private static bool HasAnyRecurringFilter(ReportRecurringQueryFilters filters) =>
        !string.IsNullOrWhiteSpace(filters.CustomerName)
        || !string.IsNullOrWhiteSpace(filters.ServiceId)
        || !string.IsNullOrWhiteSpace(filters.CabinetId)
        || !string.IsNullOrWhiteSpace(filters.FacilityName)
        || !string.IsNullOrWhiteSpace(filters.Team)
        || !string.IsNullOrWhiteSpace(filters.Territory);

    private static ReportAssistantQueryPlan NoQuery() =>
        new() { ShouldQuery = false };

    private static bool HasAnyExtraFilter(ReportCsvQueryFilters filters) =>
        !string.IsNullOrWhiteSpace(filters.Skillset)
        || !string.IsNullOrWhiteSpace(filters.Status)
        || !string.IsNullOrWhiteSpace(filters.SubStatus)
        || !string.IsNullOrWhiteSpace(filters.Territory)
        || !string.IsNullOrWhiteSpace(filters.Slot)
        || !string.IsNullOrWhiteSpace(filters.AddressContains)
        || !string.IsNullOrWhiteSpace(filters.FacilityContains)
        || !string.IsNullOrWhiteSpace(filters.AppointmentId)
        || !string.IsNullOrWhiteSpace(filters.WorkOrderNumber);

    private static bool WantsSampleRows(string message) =>
        message.Contains("list", StringComparison.OrdinalIgnoreCase)
        || message.Contains("show me", StringComparison.OrdinalIgnoreCase)
        || message.Contains("sample", StringComparison.OrdinalIgnoreCase)
        || message.Contains("which appointments", StringComparison.OrdinalIgnoreCase);

    private static string BuildCombinedText(
        string userMessage,
        IReadOnlyList<ReportAssistantChatMessageDto> history)
    {
        var recent = history.TakeLast(6).Select(m => m.Content).Append(userMessage);
        return string.Join(' ', recent);
    }

    private static void ApplySlot(string text, ReportCsvQueryFilters filters, List<string> interpretedParts)
    {
        var match = SlotPattern.Match(text);
        if (!match.Success)
            return;

        var slot = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(slot))
            return;

        filters.Slot = slot.ToUpperInvariant();
        interpretedParts.Add($"slot={filters.Slot}");
    }

    private static void ApplySkillset(
        string text,
        IReadOnlyList<string> knownSkillsets,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        foreach (var skillset in knownSkillsets.OrderByDescending(s => s.Length))
        {
            if (ContainsWholePhrase(text, skillset))
            {
                filters.Skillset = skillset;
                interpretedParts.Add($"skillset={skillset}");
                return;
            }
        }

        if (ContainsWholePhrase(text, "repair"))
        {
            filters.Skillset = "Repair";
            interpretedParts.Add("skillset=Repair");
        }
        else if (ContainsWholePhrase(text, "install"))
        {
            filters.Skillset = "Install";
            interpretedParts.Add("skillset=Install");
        }
    }

    private static void ApplyStatus(
        string text,
        IReadOnlyList<string> knownStatuses,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        foreach (var status in knownStatuses.OrderByDescending(s => s.Length))
        {
            if (ContainsWholePhrase(text, status))
            {
                filters.Status = status;
                interpretedParts.Add($"status={status}");
                return;
            }
        }

        foreach (var status in StatusKeywords.OrderByDescending(s => s.Length))
        {
            if (ContainsWholePhrase(text, status))
            {
                filters.Status = status;
                interpretedParts.Add($"status={status}");
                return;
            }
        }
    }

    private static void ApplyTerritory(
        string text,
        IReadOnlyList<string> knownTerritories,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        foreach (var territory in knownTerritories.OrderByDescending(t => t.Length))
        {
            if (ContainsWholePhrase(text, territory))
            {
                filters.Territory = territory;
                interpretedParts.Add($"territory={territory}");
                return;
            }
        }
    }

    private static void ApplyBarangayOrLocation(
        string userMessage,
        string combinedText,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        var brgyMatch = BarangayPattern.Match(userMessage);
        if (brgyMatch.Success)
        {
            filters.AddressContains = brgyMatch.Groups[1].Value.Trim();
            interpretedParts.Add($"address contains {filters.AddressContains}");
            return;
        }

        if (userMessage.Contains("barangay", StringComparison.OrdinalIgnoreCase)
            || userMessage.Contains("heatmap", StringComparison.OrdinalIgnoreCase))
        {
            var inMatch = InLocationPattern.Match(userMessage);
            if (inMatch.Success)
            {
                filters.AddressContains = inMatch.Groups[1].Value.Trim().TrimEnd('?');
                interpretedParts.Add($"address contains {filters.AddressContains}");
            }
        }

        if (filters.AddressContains is null && combinedText.Contains("cabantian", StringComparison.OrdinalIgnoreCase))
        {
            filters.AddressContains = "CABANTIAN";
            interpretedParts.Add("address contains CABANTIAN");
        }
    }

    private static void ApplyFollowUpContext(
        string combinedText,
        IReadOnlyList<ReportAssistantChatMessageDto> history,
        ContextHints context,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        if (!FollowUpPattern.IsMatch(combinedText))
            return;

        if (!string.IsNullOrWhiteSpace(filters.Skillset))
            return;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i].Content;
            foreach (var skillset in context.KnownSkillsets.OrderByDescending(s => s.Length))
            {
                if (ContainsWholePhrase(msg, skillset))
                {
                    filters.Skillset = skillset;
                    interpretedParts.Add($"skillset={skillset} (from prior message)");
                    return;
                }
            }

            if (ContainsWholePhrase(msg, "repair"))
            {
                filters.Skillset = "Repair";
                interpretedParts.Add("skillset=Repair (from prior message)");
                return;
            }
        }
    }

    private async Task<ReportAssistantQueryPlan?> TryPlanWithLlmAsync(
        string userMessage,
        IReadOnlyList<ReportAssistantChatMessageDto> conversationHistory,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
            return null;

        var prompt =
            "Return ONLY JSON. Decide if the user needs a row-level query over an uploaded KPI CSV.\n" +
            "Schema: {\"noQuery\":true} OR {\"noQuery\":false,\"interpretedAs\":\"...\",\"extraFilters\":{\"skillset\":null,\"status\":null,\"subStatus\":null,\"territory\":null,\"slot\":null,\"addressContains\":null,\"facilityContains\":null},\"groupBy\":null,\"maxSampleRows\":0}\n" +
            "Use slot values AM or PM. Use addressContains for barangay/location questions.\n" +
            "If the question can be answered from dashboard totals only, return noQuery true.\n\n" +
            $"User question: {userMessage}";

        var client = _httpClientFactory.CreateClient("OpenRouter");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey.Trim());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = opts.Model,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = "You output JSON only." },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = prompt }
            },
            ["temperature"] = 0,
            ["max_tokens"] = 256
        };
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM query planner request failed");
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        var content = JsonNode.Parse(responseText)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content))
            return null;

        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = content.IndexOf('\n');
            var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                content = content[(firstNewline + 1)..lastFence].Trim();
        }

        LlmPlanResponse? plan;
        try
        {
            plan = JsonSerializer.Deserialize<LlmPlanResponse>(content, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }

        if (plan is null || plan.NoQuery || plan.ExtraFilters is null || !plan.ExtraFilters.HasAny())
            return null;

        return new ReportAssistantQueryPlan
        {
            ShouldQuery = true,
            QueryType = ReportAssistantQueryType.KpiCsv,
            KpiRequest = new ReportCsvQueryRequest
            {
                InterpretedAs = string.IsNullOrWhiteSpace(plan.InterpretedAs)
                    ? "LLM-planned CSV query"
                    : plan.InterpretedAs,
                ExtraFilters = plan.ExtraFilters.ToFilters(),
                GroupBy = NormalizeGroupBy(plan.GroupBy),
                MaxSampleRows = Math.Clamp(plan.MaxSampleRows, 0, 10)
            }
        };
    }

    private static string? NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return null;

        var g = groupBy.Trim().ToLowerInvariant();
        return g is "status" or "substatus" or "territory" or "skillset" or "slot" ? g : null;
    }

    private static bool ContainsWholePhrase(string haystack, string phrase)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(phrase))
            return false;

        return haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static ContextHints ExtractContextHints(object summaryContext)
    {
        var hints = new ContextHints();
        JsonElement root;
        try
        {
            var json = summaryContext is JsonElement element
                ? element.GetRawText()
                : JsonSerializer.Serialize(summaryContext, JsonOpts);
            root = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return hints;
        }

        if (root.TryGetProperty("dataScope", out var scopeEl))
            hints.DataScope = scopeEl.GetString() ?? "";
        if (root.TryGetProperty("page", out var pageEl))
            hints.DataScope = pageEl.GetString() ?? hints.DataScope;

        hints.KnownSkillsets = ReadStringKeys(root, "skillsetDistributionTop");
        hints.KnownStatuses = ReadStringKeys(root, "statusDistributionTop");
        hints.KnownTerritories = ReadStringKeys(root, "territoryDistributionTop");
        hints.KnownRecurringCabinets = ReadRankItemNames(root, "recurringTickets", "topCabinets");
        hints.KnownRecurringCustomers = ReadSampleFieldValues(root, "recurringTickets", "sampleRows", "customerName");
        return hints;
    }

    private static List<string> ReadRankItemNames(JsonElement root, string section, string arrayProp)
    {
        if (!root.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object)
            return [];
        if (!sec.TryGetProperty(arrayProp, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var names = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var nameEl))
            {
                var n = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(n))
                    names.Add(n);
            }
        }

        return names;
    }

    private static List<string> ReadSampleFieldValues(
        JsonElement root,
        string section,
        string arrayProp,
        string field)
    {
        if (!root.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object)
            return [];
        if (!sec.TryGetProperty(arrayProp, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty(field, out var fieldEl))
            {
                var v = fieldEl.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                    values.Add(v);
            }
        }

        return values.ToList();
    }

    private static List<string> ReadStringKeys(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return [];

        return obj.EnumerateObject().Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
    }

    private sealed class ContextHints
    {
        public string DataScope { get; set; } = "";
        public List<string> KnownSkillsets { get; set; } = [];
        public List<string> KnownStatuses { get; set; } = [];
        public List<string> KnownTerritories { get; set; } = [];
        public List<string> KnownRecurringCabinets { get; set; } = [];
        public List<string> KnownRecurringCustomers { get; set; } = [];
    }

    private sealed class LlmPlanResponse
    {
        public bool NoQuery { get; set; }
        public string? InterpretedAs { get; set; }
        public LlmExtraFilters? ExtraFilters { get; set; }
        public string? GroupBy { get; set; }
        public int MaxSampleRows { get; set; }
    }

    private sealed class LlmExtraFilters
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

        public bool HasAny() =>
            !string.IsNullOrWhiteSpace(Skillset)
            || !string.IsNullOrWhiteSpace(Status)
            || !string.IsNullOrWhiteSpace(SubStatus)
            || !string.IsNullOrWhiteSpace(Territory)
            || !string.IsNullOrWhiteSpace(Slot)
            || !string.IsNullOrWhiteSpace(AddressContains)
            || !string.IsNullOrWhiteSpace(FacilityContains)
            || !string.IsNullOrWhiteSpace(AppointmentId)
            || !string.IsNullOrWhiteSpace(WorkOrderNumber);

        public ReportCsvQueryFilters ToFilters() => new()
        {
            Skillset = NullIfBlank(Skillset),
            Status = NullIfBlank(Status),
            SubStatus = NullIfBlank(SubStatus),
            Territory = NullIfBlank(Territory),
            Slot = NullIfBlank(Slot)?.ToUpperInvariant(),
            AddressContains = NullIfBlank(AddressContains),
            FacilityContains = NullIfBlank(FacilityContains),
            AppointmentId = NullIfBlank(AppointmentId),
            WorkOrderNumber = NullIfBlank(WorkOrderNumber)
        };

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
