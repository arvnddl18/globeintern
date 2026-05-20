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
                Reply = "Ask me anything — about your report or general topics.",
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
                    "Hi — I can help with general questions and with your report data on this page (Slot Adherence, heatmaps, recurring tickets, operational metrics). Try “What is 1+1?” or “How many recurring instances in this file?”",
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

        if (context is Dictionary<string, object?> contextDict
            && lastUser is not null
            && IsKpiPage(request.PageKind))
        {
            var slotResolved = ReportAssistantSlotAdherenceResolver.TryResolve(lastUser.Content, contextDict);
            if (slotResolved is { IsSimpleTotalOnDate: true })
            {
                var filterSummary = ReportAssistantSlotAdherenceResolver.GetActiveFiltersSummary(contextDict);
                return new ReportAssistantChatResponse
                {
                    Reply = ReportAssistantSlotAdherenceResolver.FormatReply(slotResolved, filterSummary),
                    UsedModel = false
                };
            }

            if (slotResolved is not null)
            {
                contextDict["slotAdherenceAnswer"] = new Dictionary<string, object?>
                {
                    ["appointmentDate"] = slotResolved.AppointmentDateIso,
                    ["complianceTier"] = slotResolved.Tier,
                    ["count"] = slotResolved.Count,
                    ["scheduled"] = slotResolved.Scheduled,
                    ["pass"] = slotResolved.Pass,
                    ["fail"] = slotResolved.Fail,
                    ["instruction"] =
                        $"Mandatory: user asked how many {slotResolved.Tier}. Reply with count={slotResolved.Count} only. " +
                        $"Never use scheduled ({slotResolved.Scheduled}) or appointmentsByDateFiltered as Pass count."
                };
            }

            if (!string.IsNullOrWhiteSpace(request.Token))
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
                            ["note"] = queryResult.Note ?? "matchedRows is the row count matching all filters in filtersApplied.",
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
        }

        var contextJson = JsonSerializer.Serialize(context, JsonOpts);
        const string systemPrompt =
            "You are a helpful assistant embedded in a telecom field-operations KPI web app. " +
            "Answer general questions (math, definitions, explanations, how-to, coding, brainstorming) using your own knowledge — do not refuse them because they are unrelated to reports. " +
            "When the user asks about their uploaded data, KPIs, dashboards, heatmaps, recurring tickets, alarms, or operational metrics, use ONLY facts from current_report_context and queryResults below. " +
            "The current_report_context JSON includes: dataset (full-file date range, appointmentsByDateInFile, all available filter values), csvCatalog (allColumnNames + columnProfiles with topValues for every meaningful CSV column in the upload), activeFilters, slotAdherence/totals, slotAdherenceByDate (daily scheduled/pass/fail — same as the slot adherence chart), complianceRules, complianceBySlot, appointmentsByDateFiltered, distributions, heatmapAnalysis, recurringTickets, recurringHeatmap, and on the Operational page alarm, performance, operationAging. " +
            "Use csvCatalog.columnProfiles to see allowed values for team, delaycode, technology, customertype, queue, contractorname, source, skillset, status, etc. " +
            "Use dataset for questions about dates in the uploaded file; use activeFilters to explain what the dashboard is currently showing vs the whole file. " +
            "When queryResults is present with ran true, queryResults.matchedRows is the exact answer for 'how many' questions matching filtersApplied — state that number directly; do not say the intersection is unavailable. " +
            "For total Pass or Fail on a specific appointment date (without AM/PM split), prefer slotAdherenceByDate for that date's pass/fail field, or queryResults.matchedRows with only compliance and appointmentDate in filtersApplied — appointmentsByDateFiltered is scheduled volume, not Pass count. " +
            "When queryResults.breakdown is present with groupBy slot, breakdown.AM and breakdown.PM are the authoritative counts per slot for the compliance filter used (Pass or Fail) — report both; never say AM/PM is unavailable if breakdown exists. matchedRows is the total across both slots. " +
            "Never derive failed AM/PM counts by subtracting passed from totals; run or use queryResults with compliance=Fail and groupBy slot. " +
            "complianceRules and complianceBySlot in context describe the same Pass/Fail formulas as the Status dashboard (Delayed→Fail; AM appt completed ≥12:59 PM→Fail SlotMismatch; same-day completion otherwise Pass; N/A without completion time). " +
            "queryKind kpiCsv = row scan (supports appointmentDate, compliance Pass/Fail/N/A, orderCreateDate, skillset, status, territory, slot filter OR groupBy slot for AM+PM split); queryKind recurringTickets = recurring-ticket instances. " +
            "complianceBySlot.pass/fail am/pm are totals for active dashboard filters only; for a specific date use queryResults with appointmentDate + compliance + groupBy slot (never slot=AM when user asks for both AM and PM). " +
            "skillsetBySlot is appointment volume by skillset and slot, not compliance Pass/Fail. " +
            "When the user asks for AM and PM counts for a skillset on a date (e.g. Repair on March 5), use queryResults.breakdown.AM and breakdown.PM — do not say the split is unavailable if queryResults ran. " +
            "Prefer queryResults over summary fields for granular report questions (specific date + skillset + slot, customer name, service ID, cabinet, facility, team). " +
            "For recurring tickets, matchedRows is the count of recurring instances matching the filters; distinctServiceIds counts unique service IDs. " +
            "For report questions, if the needed fact is not in current_report_context or queryResults, say it is not available in the loaded dataset and suggest uploading or opening the relevant KPI/operational page — do not guess numbers. " +
            "Never invent territories, statuses, customers, cabinets, or counts for report-specific answers. " +
            "For mixed questions, answer the general part normally and the data part only from context. " +
            "Prefer short bullet lists or one short paragraph unless the user asks for more detail. Include specific numbers from context when answering report questions. " +
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
