using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SlotAd_Globe.Models;
using SlotAd_Globe.Options;

namespace SlotAd_Globe.Services;

public sealed class ReportAssistantService : IReportAssistantService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Regex GreetingOrSmallTalk = new(
        @"^\s*(hi|hello|hey|hallo|yo|sup|good\s*(morning|afternoon|evening)|thanks|thank\s*you|ty|thx|bye|goodbye|ok|okay|cool)\b[\s!?.]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IReportAssistantContextFactory _contextFactory;
    private readonly IReportAssistantQueryPlanner _queryPlanner;
    private readonly IReportCsvQueryService _csvQueryService;
    private readonly IReportRecurringQueryService _recurringQueryService;
    private readonly IOptions<OpenRouterOptions> _options;
    private readonly ILogger<ReportAssistantService> _logger;

    public ReportAssistantService(
        IHttpClientFactory httpClientFactory,
        IReportAssistantContextFactory contextFactory,
        IReportAssistantQueryPlanner queryPlanner,
        IReportCsvQueryService csvQueryService,
        IReportRecurringQueryService recurringQueryService,
        IOptions<OpenRouterOptions> options,
        ILogger<ReportAssistantService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _contextFactory = contextFactory;
        _queryPlanner = queryPlanner;
        _csvQueryService = csvQueryService;
        _recurringQueryService = recurringQueryService;
        _options = options;
        _logger = logger;
    }

    public async Task<ReportAssistantChatResponse> ChatAsync(
        Guid userId,
        ReportAssistantChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            return new ReportAssistantChatResponse
            {
                Reply =
                    "The report assistant is not configured yet. Add your OpenRouter API key to user secrets as OpenRouter:ApiKey (see appsettings for the OpenRouter section).",
                UsedModel = false,
                Error = "missing_api_key"
            };
        }

        var trimmed = request.Messages?
            .Where(m => m is { Role: not null, Content: not null })
            .Select(m => new ReportAssistantChatMessageDto
            {
                Role = m.Role.Trim(),
                Content = m.Content.Trim()
            })
            .ToList() ?? [];

        if (trimmed.Count == 0)
        {
            return new ReportAssistantChatResponse
            {
                Reply = "Ask a question about your report, or say hi.",
                UsedModel = false
            };
        }

        if (trimmed.Count > opts.MaxMessages)
        {
            trimmed = trimmed.TakeLast(opts.MaxMessages).ToList();
        }

        var lastUser = trimmed.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUser is not null && lastUser.Content.Length > opts.MaxUserMessageLength)
        {
            lastUser.Content = lastUser.Content[..opts.MaxUserMessageLength];
        }

        if (lastUser is not null && GreetingOrSmallTalk.IsMatch(lastUser.Content))
        {
            return new ReportAssistantChatResponse
            {
                Reply =
                    "Hi — I can answer questions about Slot Adherence, heatmaps, recurring tickets, and operational reports from your uploaded data. Try “How many recurring instances?” or “How many Repair appointments in AM slot?”",
                UsedModel = false
            };
        }

        object context;
        try
        {
            context = await _contextFactory.BuildContextAsync(
                userId,
                request.PageKind,
                request.Token,
                request.View,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report assistant context build failed");
            return new ReportAssistantChatResponse
            {
                Reply = "Something went wrong while loading your report snapshot. Try again in a moment.",
                UsedModel = false,
                Error = "context_failed"
            };
        }

        if (lastUser is not null
            && context is Dictionary<string, object?> contextDict
            && IsKpiPage(request.PageKind)
            && !string.IsNullOrWhiteSpace(request.Token))
        {
            try
            {
                var plan = await _queryPlanner.PlanAsync(
                    lastUser.Content,
                    trimmed,
                    context,
                    cancellationToken);

                if (plan.ShouldQuery && plan.QueryType == ReportAssistantQueryType.KpiCsv && plan.KpiRequest is not null)
                {
                    var queryResult = await _csvQueryService.ExecuteAsync(
                        userId,
                        request.Token,
                        request.View,
                        plan.KpiRequest,
                        cancellationToken);

                    contextDict["queryResults"] = new Dictionary<string, object?>
                    {
                        ["queryKind"] = "kpiCsv",
                        ["interpretedAs"] = queryResult.InterpretedAs ?? plan.KpiRequest.InterpretedAs,
                        ["matchedRows"] = queryResult.MatchedRows,
                        ["totalFilteredRows"] = queryResult.TotalFilteredRows,
                        ["breakdown"] = queryResult.Breakdown,
                        ["sampleRows"] = queryResult.SampleRows,
                        ["filtersApplied"] = queryResult.FiltersApplied,
                        ["note"] = queryResult.Note,
                        ["ran"] = queryResult.Ran
                    };
                }
                else if (plan.ShouldQuery
                         && plan.QueryType == ReportAssistantQueryType.Recurring
                         && plan.RecurringRequest is not null)
                {
                    var recurringResult = await _recurringQueryService.ExecuteAsync(
                        userId,
                        request.Token,
                        plan.RecurringRequest,
                        cancellationToken);

                    contextDict["queryResults"] = new Dictionary<string, object?>
                    {
                        ["queryKind"] = "recurringTickets",
                        ["interpretedAs"] = recurringResult.InterpretedAs ?? plan.RecurringRequest.InterpretedAs,
                        ["matchedRows"] = recurringResult.MatchedRows,
                        ["totalRecurringInstances"] = recurringResult.TotalRecurringInstances,
                        ["distinctServiceIds"] = recurringResult.DistinctServiceIds,
                        ["breakdown"] = recurringResult.Breakdown,
                        ["sampleRows"] = recurringResult.SampleRows,
                        ["filtersApplied"] = recurringResult.FiltersApplied,
                        ["note"] = recurringResult.Note,
                        ["ran"] = recurringResult.Ran
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Report assistant CSV query failed");
            }
        }

        var contextJson = JsonSerializer.Serialize(context, JsonOpts);
        const string systemPrompt =
            "You are a concise analytics assistant for a telecom field-operations KPI web app. " +
            "The current_report_context JSON includes all major report sections on the page: slotAdherence (totals), heatmapAnalysis, recurringTickets (total instances, top facilities/cabinets/teams, sample rows), recurringHeatmap, and on the Operational page alarm, performance, operationAging. " +
            "When queryResults is present, treat it as authoritative: queryKind kpiCsv = slot adherence row scan; queryKind recurringTickets = recurring-ticket instances from the full KPI CSV. " +
            "Prefer queryResults over summary fields for granular questions (customer name, service ID, cabinet, facility, team). " +
            "For recurring tickets, matchedRows is the count of recurring instances matching the filters; distinctServiceIds counts unique service IDs. " +
            "Otherwise use facts in current_report_context only. " +
            "If the answer is not in that JSON, say it is not in the current dataset. " +
            "Prefer short bullet lists or one short paragraph. Include specific numbers when they appear in the context. " +
            "Never invent territories, statuses, customers, cabinets, or counts. " +
            "If queryResults.note mentions address-based barangay search, mention that limitation when relevant.";

        var apiMessages = new List<object>
        {
            new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] = systemPrompt + "\n\ncurrent_report_context:\n" + contextJson
            }
        };

        foreach (var m in trimmed)
        {
            var role = m.Role.ToLowerInvariant();
            if (role is not ("user" or "assistant"))
                continue;
            if (string.IsNullOrWhiteSpace(m.Content))
                continue;
            apiMessages.Add(new Dictionary<string, string> { ["role"] = role, ["content"] = m.Content });
        }

        var client = _httpClientFactory.CreateClient("OpenRouter");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey.Trim());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = opts.Model,
            ["messages"] = apiMessages,
            ["temperature"] = opts.Temperature,
            ["max_tokens"] = opts.MaxCompletionTokens
        };
        var json = JsonSerializer.Serialize(payload, JsonOpts);

        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenRouter request failed");
            return new ReportAssistantChatResponse
            {
                Reply = "Could not reach the AI service. Check your network or try again shortly.",
                UsedModel = false,
                Error = "network"
            };
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter HTTP {Status}: {Body}", (int)response.StatusCode, responseText);
            return new ReportAssistantChatResponse
            {
                Reply = "The AI service returned an error. If you use a free model, try again later or switch model in configuration.",
                UsedModel = false,
                Error = "openrouter_http"
            };
        }

        try
        {
            var node = JsonNode.Parse(responseText);
            var content = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            content = content.Trim();
            if (string.IsNullOrEmpty(content))
            {
                return new ReportAssistantChatResponse
                {
                    Reply = "The model returned an empty reply. Try rephrasing your question.",
                    UsedModel = true,
                    Error = "empty_model"
                };
            }

            return new ReportAssistantChatResponse { Reply = content, UsedModel = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenRouter response parse failed");
            return new ReportAssistantChatResponse
            {
                Reply = "Received an unexpected response from the AI service.",
                UsedModel = false,
                Error = "parse"
            };
        }
    }

    private static bool IsKpiPage(ReportAssistantPageKind pageKind) =>
        pageKind is ReportAssistantPageKind.KpiDashboard or ReportAssistantPageKind.KpiFilter;
}
