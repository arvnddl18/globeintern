using System.Globalization;
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
        @"\b(am|pm)\s*slot\b|\bin\s+(am|pm)\s+slot\b|\bon\s+(am|pm)\b|\bfrom\s+(am|pm)\b|\b(am|pm)\s+of\b",
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

    private static readonly Regex IsoDatePattern = new(
        @"\b(\d{4}-\d{2}-\d{2})\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SlashDatePattern = new(
        @"\b(\d{1,2})[\/\-](\d{1,2})[\/\-](\d{2,4})\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DateAnalyticsPattern = new(
        @"\b(busiest|highest|most appointments|peak day|per day|by date|each date|which date|compare dates|appointment dates?|daily)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex MonthDayPattern = new(
        @"\b(?:on\s+)?(?:the\s+)?(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)\s+(\d{1,2})(?:st|nd|rd|th)?(?:\s*,?\s*(\d{4}))?\b" +
        @"|\b(\d{1,2})(?:st|nd|rd|th)?\s+of\s+(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)(?:\s*,?\s*(\d{4}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex AmPmSlotBreakdownPattern = new(
        @"\b(am|pm)\b.*\b(am|pm)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex AmTokenPattern = new(
        @"\bam\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex PmTokenPattern = new(
        @"\bpm\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex SlotAdherencePassFailDatePattern = new(
        @"\b(?:pass(?:ed)?|fail(?:ed)?)\b.*\b(?:january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec|\d{4}-\d{2}-\d{2}|\d{1,2}[\/\-]\d{1,2})" +
        @"|\b(?:january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec|\d{4}-\d{2}-\d{2})\b.*\b(?:pass(?:ed)?|fail(?:ed)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(150));

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

        var combinedText = NormalizeWordNumbers(BuildCombinedText(userMessage, conversationHistory));
        var normalizedUserMessage = NormalizeWordNumbers(userMessage);

        var recurringPlan = TryPlanRecurring(userMessage, combinedText, conversationHistory, context);
        if (recurringPlan is not null)
            return recurringPlan;

        var filters = new ReportCsvQueryFilters();
        var interpretedParts = new List<string>();
        var wantsAmPmBreakdown = WantsAmPmSlotBreakdown(normalizedUserMessage);

        var complianceDateQuestion = LooksLikeComplianceCountOnDate(normalizedUserMessage);

        ApplySlot(normalizedUserMessage, filters, interpretedParts, skipSlotFilter: wantsAmPmBreakdown);
        if (string.IsNullOrWhiteSpace(filters.Slot) && !complianceDateQuestion)
            ApplySlot(combinedText, filters, interpretedParts, skipSlotFilter: wantsAmPmBreakdown);

        ApplySkillset(normalizedUserMessage, context.KnownSkillsets, filters, interpretedParts);
        if (string.IsNullOrWhiteSpace(filters.Skillset) && !complianceDateQuestion)
            ApplySkillset(combinedText, context.KnownSkillsets, filters, interpretedParts);

        ApplyStatus(normalizedUserMessage, context.KnownStatuses, filters, interpretedParts);
        if (string.IsNullOrWhiteSpace(filters.Status) && !complianceDateQuestion)
            ApplyStatus(combinedText, context.KnownStatuses, filters, interpretedParts);

        ApplyTerritory(normalizedUserMessage, context.KnownTerritories, filters, interpretedParts);
        if (string.IsNullOrWhiteSpace(filters.Territory) && !complianceDateQuestion)
            ApplyTerritory(combinedText, context.KnownTerritories, filters, interpretedParts);
        ApplyAppointmentDateForMessage(normalizedUserMessage, combinedText, context, filters, interpretedParts);
        ApplyOrderCreateDate(combinedText, context.KnownOrderCreateDates, filters, interpretedParts);
        ApplyCompliance(normalizedUserMessage, filters, interpretedParts);
        ApplyCatalogFieldFilters(combinedText, context, filters, interpretedParts);
        ApplyBarangayOrLocation(userMessage, combinedText, filters, interpretedParts);
        ApplyFollowUpContext(combinedText, conversationHistory, context, filters, interpretedParts);

        string? groupBy = null;
        if (string.IsNullOrWhiteSpace(filters.Slot)
            && SlotPattern.IsMatch(normalizedUserMessage)
            && !string.IsNullOrWhiteSpace(filters.Skillset))
        {
            groupBy = "slot";
            interpretedParts.Add("group by slot");
        }
        else if (DateAnalyticsPattern.IsMatch(normalizedUserMessage))
        {
            groupBy = "date";
            interpretedParts.Add("group by appointment date");
        }

        ApplySlotBreakdownGroupBy(normalizedUserMessage, filters, ref groupBy, interpretedParts);

        if (groupBy == "slot")
            ClearSlotFilterForBreakdown(filters, interpretedParts);

        if (WantsAmPmSlotBreakdown(normalizedUserMessage))
        {
            EnrichAmPmSkillsetDateFilters(normalizedUserMessage, combinedText, context, filters, interpretedParts);
            groupBy ??= "slot";
            if (!interpretedParts.Any(p => p.StartsWith("group by", StringComparison.OrdinalIgnoreCase)))
                interpretedParts.Add("group by AM/PM slot");
            ClearSlotFilterForBreakdown(filters, interpretedParts);
        }

        RestrictFiltersForComplianceDateQuestion(normalizedUserMessage, context, filters, interpretedParts, ref groupBy);

        if (!HasAnyExtraFilter(filters) && groupBy is null)
        {
            var llmPlan = await TryPlanWithLlmAsync(normalizedUserMessage, conversationHistory, context, cancellationToken);
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
        || !string.IsNullOrWhiteSpace(filters.WorkOrderNumber)
        || !string.IsNullOrWhiteSpace(filters.AppointmentDate)
        || !string.IsNullOrWhiteSpace(filters.OrderCreateDate)
        || !string.IsNullOrWhiteSpace(filters.Compliance)
        || !string.IsNullOrWhiteSpace(filters.CustomerNameContains)
        || !string.IsNullOrWhiteSpace(filters.ServiceIdNumber)
        || !string.IsNullOrWhiteSpace(filters.TeamContains)
        || !string.IsNullOrWhiteSpace(filters.DelayCode)
        || !string.IsNullOrWhiteSpace(filters.Technology)
        || !string.IsNullOrWhiteSpace(filters.CustomerType)
        || !string.IsNullOrWhiteSpace(filters.Queue)
        || !string.IsNullOrWhiteSpace(filters.CabinetId)
        || !string.IsNullOrWhiteSpace(filters.ContractorName)
        || !string.IsNullOrWhiteSpace(filters.SourceSystem)
        || filters.ColumnContains.Count > 0;

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

    private static bool WantsAmPmSlotBreakdown(string userMessage) =>
        AmPmSlotBreakdownPattern.IsMatch(userMessage);

    private static bool HasAmToken(string text) => AmTokenPattern.IsMatch(text);

    private static bool HasPmToken(string text) => PmTokenPattern.IsMatch(text);

    private static bool LooksLikeComplianceCountOnDate(string userMessage) =>
        SlotAdherencePassFailDatePattern.IsMatch(userMessage)
        || (ApplyComplianceWouldMatch(userMessage) && MonthDayPattern.IsMatch(userMessage))
        || (ApplyComplianceWouldMatch(userMessage) && IsoDatePattern.IsMatch(userMessage));

    private static bool ApplyComplianceWouldMatch(string text)
    {
        var asksFail = ContainsWholePhrase(text, "failed")
                       || Regex.IsMatch(text, @"\bfail(?:ed|ure)?\b", RegexOptions.IgnoreCase);
        var asksPass = ContainsWholePhrase(text, "passed")
                       || Regex.IsMatch(text, @"\bpass(?:ed)?\b", RegexOptions.IgnoreCase);
        return asksFail || asksPass;
    }

    private static bool MessageMentionsSkillset(string userMessage, IReadOnlyList<string> knownSkillsets)
    {
        if (ContainsWholePhrase(userMessage, "repair") || ContainsWholePhrase(userMessage, "install"))
            return true;

        return knownSkillsets.Any(s => ContainsWholePhrase(userMessage, s));
    }

    private static bool MessageMentionsStatus(string userMessage, IReadOnlyList<string> knownStatuses)
    {
        foreach (var status in knownStatuses.OrderByDescending(s => s.Length))
        {
            if (ContainsWholePhrase(userMessage, status))
                return true;
        }

        return StatusKeywords.Any(s => ContainsWholePhrase(userMessage, s));
    }

    private static bool MessageMentionsSlot(string userMessage) =>
        SlotPattern.IsMatch(userMessage) || HasAmToken(userMessage) || HasPmToken(userMessage);

    private static void RestrictFiltersForComplianceDateQuestion(
        string userMessage,
        ContextHints context,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts,
        ref string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(filters.Compliance) || string.IsNullOrWhiteSpace(filters.AppointmentDate))
            return;

        if (WantsAmPmSlotBreakdown(userMessage))
            return;

        if (!MessageMentionsSkillset(userMessage, context.KnownSkillsets))
        {
            filters.Skillset = null;
            interpretedParts.RemoveAll(p => p.StartsWith("skillset=", StringComparison.OrdinalIgnoreCase));
        }

        if (!MessageMentionsStatus(userMessage, context.KnownStatuses))
        {
            filters.Status = null;
            interpretedParts.RemoveAll(p => p.StartsWith("status=", StringComparison.OrdinalIgnoreCase));
        }

        if (!MessageMentionsSlot(userMessage))
        {
            filters.Slot = null;
            interpretedParts.RemoveAll(p => p.StartsWith("slot=", StringComparison.OrdinalIgnoreCase));
        }

        if (groupBy == "slot" && !HasAmToken(userMessage) && !HasPmToken(userMessage))
        {
            groupBy = null;
            interpretedParts.RemoveAll(p => p.StartsWith("group by", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void EnrichAmPmSkillsetDateFilters(
        string userMessage,
        string combinedText,
        ContextHints context,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        if (string.IsNullOrWhiteSpace(filters.Skillset))
        {
            if (ContainsWholePhrase(userMessage, "repair"))
            {
                filters.Skillset = "Repair";
                interpretedParts.Add("skillset=Repair");
            }
            else if (ContainsWholePhrase(userMessage, "install"))
            {
                filters.Skillset = "Install";
                interpretedParts.Add("skillset=Install");
            }
        }

        if (string.IsNullOrWhiteSpace(filters.AppointmentDate))
        {
            var dateParts = new List<string>();
            ApplyAppointmentDate(userMessage, context, filters, dateParts);
            if (string.IsNullOrWhiteSpace(filters.AppointmentDate))
                ApplyAppointmentDate(combinedText, context, filters, dateParts);
            interpretedParts.AddRange(dateParts);
        }
    }

    private static void ClearSlotFilterForBreakdown(ReportCsvQueryFilters filters, List<string> interpretedParts)
    {
        if (string.IsNullOrWhiteSpace(filters.Slot))
            return;

        filters.Slot = null;
        interpretedParts.RemoveAll(p => p.StartsWith("slot=", StringComparison.OrdinalIgnoreCase));
        interpretedParts.Add("group by AM/PM (no single-slot filter)");
    }

    private static void ApplySlot(
        string text,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts,
        bool skipSlotFilter = false)
    {
        if (skipSlotFilter)
            return;

        var match = SlotPattern.Match(text);
        if (!match.Success)
            return;

        string? slot = null;
        for (var i = 1; i < match.Groups.Count; i++)
        {
            var g = match.Groups[i].Value;
            if (g.Equals("am", StringComparison.OrdinalIgnoreCase)
                || g.Equals("pm", StringComparison.OrdinalIgnoreCase))
            {
                slot = g.ToUpperInvariant();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(slot))
            return;

        filters.Slot = slot;
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

    private static void ApplyAppointmentDateForMessage(
        string userMessage,
        string combinedText,
        ContextHints context,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        ApplyAppointmentDate(userMessage, context, filters, interpretedParts);
        if (!string.IsNullOrWhiteSpace(filters.AppointmentDate))
            return;

        var fallbackParts = new List<string>();
        ApplyAppointmentDate(combinedText, context, filters, fallbackParts);
        interpretedParts.AddRange(fallbackParts);
    }

    private static void ApplyAppointmentDate(
        string text,
        ContextHints context,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        var knownDates = context.KnownAppointmentDates;

        foreach (Match m in IsoDatePattern.Matches(text))
        {
            var iso = m.Groups[1].Value;
            if (knownDates.Count == 0 || knownDates.Contains(iso, StringComparer.OrdinalIgnoreCase))
            {
                filters.AppointmentDate = iso;
                interpretedParts.Add($"appointmentDate={iso}");
                return;
            }
        }

        foreach (var known in knownDates.OrderByDescending(d => d.Length))
        {
            if (text.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                filters.AppointmentDate = known;
                interpretedParts.Add($"appointmentDate={known}");
                return;
            }
        }

        var fromMonthDay = TryResolveMonthDayFromText(text, knownDates, context.FileDateMin, context.FileDateMax);
        if (fromMonthDay is not null)
        {
            filters.AppointmentDate = fromMonthDay;
            interpretedParts.Add($"appointmentDate={fromMonthDay}");
            return;
        }

        foreach (Match m in SlashDatePattern.Matches(text))
        {
            if (!TryParseSlashDateGroups(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, out var iso))
                continue;

            if (knownDates.Count == 0 || knownDates.Contains(iso, StringComparer.OrdinalIgnoreCase))
            {
                filters.AppointmentDate = iso;
                interpretedParts.Add($"appointmentDate={iso}");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(filters.AppointmentDate)
            && !string.IsNullOrWhiteSpace(context.ActiveSelectedDate)
            && MonthDayPattern.IsMatch(text)
            && DateMentionMatchesIso(context.ActiveSelectedDate, text))
        {
            filters.AppointmentDate = context.ActiveSelectedDate;
            interpretedParts.Add($"appointmentDate={context.ActiveSelectedDate} (matches dashboard filter)");
        }
    }

    private static string? TryResolveMonthDayFromText(
        string text,
        IReadOnlyList<string> knownDates,
        string? fileDateMin,
        string? fileDateMax)
    {
        var match = MonthDayPattern.Match(text);
        if (!match.Success)
            return null;

        if (!TryParseMonthDayMatch(match, out var month, out var day, out var explicitYear))
            return null;

        var candidates = new List<string>();
        foreach (var d in knownDates)
        {
            if (!DateOnly.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                continue;
            if (dt.Month == month && dt.Day == day)
                candidates.Add(d);
        }

        if (candidates.Count == 0)
        {
            var year = explicitYear ?? InferYearForMonthDay(month, day, fileDateMin, fileDateMax);
            if (year is null)
                return null;
            try
            {
                return new DateOnly(year.Value, month, day).ToString("yyyy-MM-dd");
            }
            catch
            {
                return null;
            }
        }

        if (explicitYear.HasValue)
        {
            var target = $"{explicitYear.Value:0000}-{month:00}-{day:00}";
            return candidates.FirstOrDefault(c => string.Equals(c, target, StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault();
        }

        if (candidates.Count == 1)
            return candidates[0];

        if (DateOnly.TryParse(fileDateMax, CultureInfo.InvariantCulture, DateTimeStyles.None, out var maxInFile))
        {
            var inMaxYear = candidates
                .Where(c => DateOnly.TryParse(c, out var dt) && dt.Year == maxInFile.Year)
                .OrderByDescending(c => c, StringComparer.Ordinal)
                .FirstOrDefault();
            if (inMaxYear is not null)
                return inMaxYear;
        }

        return candidates.OrderByDescending(c => c, StringComparer.Ordinal).First();
    }

    private static bool TryParseMonthDayMatch(Match match, out int month, out int day, out int? explicitYear)
    {
        month = 0;
        day = 0;
        explicitYear = null;

        if (match.Groups[1].Success && match.Groups[2].Success)
        {
            if (!TryParseMonthName(match.Groups[1].Value, out month))
                return false;
            if (!int.TryParse(match.Groups[2].Value, out day))
                return false;
            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var y))
                explicitYear = y;
            return true;
        }

        if (match.Groups[4].Success && match.Groups[5].Success)
        {
            if (!int.TryParse(match.Groups[4].Value, out day))
                return false;
            if (!TryParseMonthName(match.Groups[5].Value, out month))
                return false;
            if (match.Groups[6].Success && int.TryParse(match.Groups[6].Value, out var y))
                explicitYear = y;
            return true;
        }

        return false;
    }

    private static bool TryParseMonthName(string raw, out int month)
    {
        month = 0;
        var key = raw.Trim().TrimEnd('.').ToLowerInvariant();
        return MonthNameToNumber.TryGetValue(key, out month);
    }

    private static int? InferYearForMonthDay(int month, int day, string? fileDateMin, string? fileDateMax)
    {
        if (DateOnly.TryParse(fileDateMax, CultureInfo.InvariantCulture, DateTimeStyles.None, out var max))
            return max.Year;
        if (DateOnly.TryParse(fileDateMin, CultureInfo.InvariantCulture, DateTimeStyles.None, out var min))
            return min.Year;
        return DateTime.Today.Year;
    }

    private static bool DateMentionMatchesIso(string isoDate, string text)
    {
        if (!DateOnly.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;

        return TryResolveMonthDayFromText(text, [isoDate], isoDate, isoDate) == isoDate;
    }

    private static readonly Dictionary<string, int> MonthNameToNumber = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["jan"] = 1,
        ["february"] = 2, ["feb"] = 2,
        ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["may"] = 5,
        ["june"] = 6, ["jun"] = 6,
        ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9,
        ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12
    };

    private static void ApplyCatalogFieldFilters(
        string text,
        ContextHints context,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        ApplyCatalogColumnValue(text, context, "delaycode", v =>
        {
            filters.DelayCode = v;
            interpretedParts.Add($"delayCode={v}");
        });
        ApplyCatalogColumnValue(text, context, "technology", v =>
        {
            filters.Technology = v;
            interpretedParts.Add($"technology={v}");
        });
        ApplyCatalogColumnValue(text, context, "customertype", v =>
        {
            filters.CustomerType = v;
            interpretedParts.Add($"customerType={v}");
        });
        ApplyCatalogColumnValue(text, context, "queue", v =>
        {
            filters.Queue = v;
            interpretedParts.Add($"queue={v}");
        });
        ApplyCatalogColumnValue(text, context, "contractorname", v =>
        {
            filters.ContractorName = v;
            interpretedParts.Add($"contractorName={v}");
        });
        ApplyCatalogColumnValue(text, context, "source", v =>
        {
            filters.SourceSystem = v;
            interpretedParts.Add($"source={v}");
        });

        if (string.IsNullOrWhiteSpace(filters.CabinetId))
        {
            var cabinetMatch = CabinetIdPattern.Match(text);
            if (cabinetMatch.Success)
            {
                filters.CabinetId = cabinetMatch.Groups[1].Value;
                interpretedParts.Add($"cabinet={filters.CabinetId}");
            }
        }

        var serviceMatch = ServiceIdPattern.Match(text);
        if (serviceMatch.Success && string.IsNullOrWhiteSpace(filters.ServiceIdNumber))
        {
            filters.ServiceIdNumber = serviceMatch.Groups[1].Value.Trim();
            interpretedParts.Add($"serviceId={filters.ServiceIdNumber}");
        }

        if (string.IsNullOrWhiteSpace(filters.CustomerNameContains))
        {
            var customerMatch = CustomerNamedPattern.Match(text);
            if (customerMatch.Success)
            {
                filters.CustomerNameContains = customerMatch.Groups[1].Value.Trim().TrimEnd('?');
                interpretedParts.Add($"customerName contains {filters.CustomerNameContains}");
            }
        }

        if (string.IsNullOrWhiteSpace(filters.TeamContains))
        {
            foreach (var teamName in context.GetColumnTopValues("team").OrderByDescending(t => t.Length))
            {
                if (teamName.Length < 4)
                    continue;
                if (ContainsWholePhrase(text, teamName))
                {
                    filters.TeamContains = teamName;
                    interpretedParts.Add($"team contains {teamName}");
                    break;
                }
            }
        }
    }

    private static void ApplyCatalogColumnValue(
        string text,
        ContextHints context,
        string columnKey,
        Action<string> apply)
    {
        foreach (var value in context.GetColumnTopValues(columnKey).OrderByDescending(v => v.Length))
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
                continue;
            if (ContainsWholePhrase(text, value))
            {
                apply(value);
                return;
            }
        }
    }

    private static void ApplyCompliance(
        string text,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        var asksFail = ContainsWholePhrase(text, "failed")
                       || Regex.IsMatch(text, @"\bfail(?:ed|ure)?\b", RegexOptions.IgnoreCase);
        var asksPass = ContainsWholePhrase(text, "passed")
                       || Regex.IsMatch(text, @"\bpass(?:ed)?\b", RegexOptions.IgnoreCase);

        if (asksFail)
        {
            filters.Compliance = "Fail";
            interpretedParts.Add("compliance=Fail");
            return;
        }

        if (asksPass)
        {
            filters.Compliance = "Pass";
            interpretedParts.Add("compliance=Pass");
            return;
        }

        if (text.Contains("n/a", StringComparison.OrdinalIgnoreCase)
            || ContainsWholePhrase(text, "not applicable"))
        {
            filters.Compliance = "N/A";
            interpretedParts.Add("compliance=N/A");
        }
    }

    private static void ApplySlotBreakdownGroupBy(
        string userMessage,
        ReportCsvQueryFilters filters,
        ref string? groupBy,
        List<string> interpretedParts)
    {
        if (groupBy is not null)
            return;

        if (!HasAmToken(userMessage) || !HasPmToken(userMessage))
            return;

        if (!string.IsNullOrWhiteSpace(filters.Compliance)
            || userMessage.Contains("slot", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(filters.AppointmentDate))
        {
            groupBy = "slot";
            interpretedParts.Add("group by AM/PM slot");
        }
    }

    private static string NormalizeWordNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var result = text;
        foreach (var (word, digit) in WordNumberReplacements.OrderByDescending(kv => kv.Key.Length))
        {
            result = Regex.Replace(
                result,
                $@"\b{Regex.Escape(word)}\b",
                digit,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return result;
    }

    private static readonly IReadOnlyList<KeyValuePair<string, string>> WordNumberReplacements =
    [
        new("thirty-first", "31"), new("thirty first", "31"),
        new("thirtieth", "30"), new("thirty", "30"),
        new("twenty-ninth", "29"), new("twenty ninth", "29"),
        new("twenty-eighth", "28"), new("twenty eighth", "28"),
        new("twenty-seventh", "27"), new("twenty seventh", "27"),
        new("twenty-sixth", "26"), new("twenty sixth", "26"),
        new("twenty-fifth", "25"), new("twenty fifth", "25"),
        new("twenty-fourth", "24"), new("twenty fourth", "24"),
        new("twenty-third", "23"), new("twenty third", "23"),
        new("twenty-second", "22"), new("twenty second", "22"),
        new("twenty-first", "21"), new("twenty first", "21"),
        new("twentieth", "20"), new("twenty", "20"),
        new("nineteenth", "19"), new("nineteen", "19"),
        new("eighteenth", "18"), new("eighteen", "18"),
        new("seventeenth", "17"), new("seventeen", "17"),
        new("sixteenth", "16"), new("sixteen", "16"),
        new("fifteenth", "15"), new("fifteen", "15"),
        new("fourteenth", "14"), new("fourteen", "14"),
        new("thirteenth", "13"), new("thirteen", "13"),
        new("twelfth", "12"), new("twelve", "12"),
        new("eleventh", "11"), new("eleven", "11"),
        new("tenth", "10"), new("ten", "10"),
        new("ninth", "9"), new("nine", "9"),
        new("eighth", "8"), new("eight", "8"),
        new("seventh", "7"), new("seven", "7"),
        new("sixth", "6"), new("six", "6"),
        new("fifth", "5"), new("five", "5"),
        new("fourth", "4"), new("four", "4"),
        new("third", "3"), new("three", "3"),
        new("second", "2"), new("two", "2"),
        new("first", "1"), new("one", "1")
    ];

    private static void ApplyOrderCreateDate(
        string text,
        IReadOnlyList<string> knownOrderCreateDates,
        ReportCsvQueryFilters filters,
        List<string> interpretedParts)
    {
        if (!text.Contains("order create", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("ordercreatedate", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("created on", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var raw in knownOrderCreateDates.OrderByDescending(d => d.Length))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (text.Contains(raw, StringComparison.OrdinalIgnoreCase))
            {
                filters.OrderCreateDate = raw;
                interpretedParts.Add($"orderCreateDate={raw}");
                return;
            }
        }
    }

    private static bool TryParseSlashDateGroups(string part1, string part2, string part3, out string iso)
    {
        iso = "";
        if (!int.TryParse(part1, out var a) || !int.TryParse(part2, out var b) || !int.TryParse(part3, out var y))
            return false;

        if (y < 100)
            y += 2000;

        int month;
        int day;
        if (a > 12 && b <= 12)
        {
            day = a;
            month = b;
        }
        else if (b > 12 && a <= 12)
        {
            month = a;
            day = b;
        }
        else
        {
            month = a;
            day = b;
        }

        try
        {
            iso = new DateOnly(y, month, day).ToString("yyyy-MM-dd");
            return true;
        }
        catch
        {
            return false;
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
        ContextHints context,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
            return null;

        var dateHint = context.KnownAppointmentDates.Count > 0
            ? string.Join(", ", context.KnownAppointmentDates.Take(12))
            : "unknown";
        var prompt =
            "Return ONLY JSON. Decide if the user needs a row-level query over an uploaded KPI CSV.\n" +
            "Schema: {\"noQuery\":true} OR {\"noQuery\":false,\"interpretedAs\":\"...\",\"extraFilters\":{\"skillset\":null,\"status\":null,\"subStatus\":null,\"territory\":null,\"slot\":null,\"addressContains\":null,\"facilityContains\":null,\"appointmentDate\":null,\"orderCreateDate\":null,\"compliance\":null},\"groupBy\":null,\"maxSampleRows\":0}\n" +
            "Use slot values AM or PM. Use addressContains for barangay/location questions. Use appointmentDate as yyyy-MM-dd for a specific appointment day (e.g. March 5 → pick matching date from sample list).\n" +
            "Use compliance Pass, Fail, or N/A for passed/failed compliance questions (All Status files).\n" +
            "Use groupBy slot when user wants AM and PM counts; groupBy date for busiest day questions.\n" +
            "For combined filters (date + compliance + slot breakdown), return noQuery false with all extraFilters set and groupBy slot.\n" +
            "If the question can be answered from dashboard totals only, return noQuery true.\n\n" +
            $"Sample appointment dates in file: {dateHint}\n" +
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

        var extra = plan.ExtraFilters.ToFilters();
        if (string.IsNullOrWhiteSpace(extra.AppointmentDate) && MonthDayPattern.IsMatch(userMessage))
        {
            var resolved = TryResolveMonthDayFromText(
                userMessage,
                context.KnownAppointmentDates,
                context.FileDateMin,
                context.FileDateMax);
            if (resolved is not null)
                extra.AppointmentDate = resolved;
        }

        if (string.IsNullOrWhiteSpace(extra.Compliance))
        {
            var complianceParts = new List<string>();
            ApplyCompliance(userMessage, extra, complianceParts);
        }

        var llmGroupBy = NormalizeGroupBy(plan.GroupBy);
        if (llmGroupBy is null)
        {
            var gbParts = new List<string>();
            ApplySlotBreakdownGroupBy(userMessage, extra, ref llmGroupBy, gbParts);
        }

        if (WantsAmPmSlotBreakdown(userMessage) || llmGroupBy == "slot")
        {
            if (llmGroupBy is null)
                llmGroupBy = "slot";
            extra.Slot = null;
        }

        return new ReportAssistantQueryPlan
        {
            ShouldQuery = true,
            QueryType = ReportAssistantQueryType.KpiCsv,
            KpiRequest = new ReportCsvQueryRequest
            {
                InterpretedAs = string.IsNullOrWhiteSpace(plan.InterpretedAs)
                    ? "LLM-planned CSV query"
                    : plan.InterpretedAs,
                ExtraFilters = extra,
                GroupBy = llmGroupBy ?? NormalizeGroupBy(plan.GroupBy),
                MaxSampleRows = Math.Clamp(plan.MaxSampleRows, 0, 10)
            }
        };
    }

    private static string? NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return null;

        var g = groupBy.Trim().ToLowerInvariant();
        return g is "status" or "substatus" or "territory" or "skillset" or "slot" or "date"
            or "team" or "delaycode" or "technology" or "customertype" or "queue"
            or "contractorname" or "source"
            ? g
            : null;
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
        hints.KnownAppointmentDates = ReadNestedStringArray(root, "dataset", "availableAppointmentDates");
        if (hints.KnownAppointmentDates.Count == 0)
            hints.KnownAppointmentDates = ReadNestedStringKeys(root, "dataset", "appointmentsByDateInFile");
        hints.KnownOrderCreateDates = ReadNestedStringArray(root, "dataset", "availableOrderCreateDates");
        hints.ColumnTopValues = ReadCsvColumnProfiles(root);

        if (root.TryGetProperty("activeFilters", out var activeFilters)
            && activeFilters.ValueKind == JsonValueKind.Object
            && activeFilters.TryGetProperty("selectedDate", out var selDate))
            hints.ActiveSelectedDate = selDate.GetString();

        if (root.TryGetProperty("dataset", out var dataset)
            && dataset.ValueKind == JsonValueKind.Object
            && dataset.TryGetProperty("appointmentDateRangeInFile", out var range)
            && range.ValueKind == JsonValueKind.Object)
        {
            if (range.TryGetProperty("min", out var minEl))
                hints.FileDateMin = minEl.GetString();
            if (range.TryGetProperty("max", out var maxEl))
                hints.FileDateMax = maxEl.GetString();
        }

        return hints;
    }

    private static Dictionary<string, List<string>> ReadCsvColumnProfiles(JsonElement root)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("csvCatalog", out var catalog) || catalog.ValueKind != JsonValueKind.Object)
            return map;
        if (!catalog.TryGetProperty("columnProfiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var profile in profiles.EnumerateArray())
        {
            if (!profile.TryGetProperty("column", out var colEl))
                continue;
            var col = colEl.GetString();
            if (string.IsNullOrWhiteSpace(col))
                continue;

            var values = new List<string>();
            if (profile.TryGetProperty("topValues", out var tops) && tops.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tops.EnumerateArray())
                {
                    if (item.TryGetProperty("value", out var valEl))
                    {
                        var val = valEl.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            values.Add(val);
                    }
                }
            }

            map[col] = values;
            var key = col.Replace(" ", "").Replace("_", "");
            if (!map.ContainsKey(key))
                map[key] = values;
        }

        return map;
    }

    private static List<string> ReadNestedStringArray(JsonElement root, string section, string arrayProp)
    {
        if (!root.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object)
            return [];
        if (!sec.TryGetProperty(arrayProp, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var v = item.GetString();
            if (!string.IsNullOrWhiteSpace(v))
                values.Add(v);
        }

        return values;
    }

    private static List<string> ReadNestedStringKeys(JsonElement root, string section, string objectProp)
    {
        if (!root.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object)
            return [];
        if (!sec.TryGetProperty(objectProp, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return [];

        return obj.EnumerateObject().Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
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
        public List<string> KnownAppointmentDates { get; set; } = [];
        public List<string> KnownOrderCreateDates { get; set; } = [];
        public string? ActiveSelectedDate { get; set; }
        public string? FileDateMin { get; set; }
        public string? FileDateMax { get; set; }
        public Dictionary<string, List<string>> ColumnTopValues { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> GetColumnTopValues(string columnKey)
        {
            if (ColumnTopValues.TryGetValue(columnKey, out var exact))
                return exact;

            var normalized = columnKey.Replace(" ", "").Replace("_", "");
            if (ColumnTopValues.TryGetValue(normalized, out var norm))
                return norm;

            return [];
        }
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
        public string? AppointmentDate { get; set; }
        public string? OrderCreateDate { get; set; }
        public string? Compliance { get; set; }

        public bool HasAny() =>
            !string.IsNullOrWhiteSpace(Skillset)
            || !string.IsNullOrWhiteSpace(Status)
            || !string.IsNullOrWhiteSpace(SubStatus)
            || !string.IsNullOrWhiteSpace(Territory)
            || !string.IsNullOrWhiteSpace(Slot)
            || !string.IsNullOrWhiteSpace(AddressContains)
            || !string.IsNullOrWhiteSpace(FacilityContains)
            || !string.IsNullOrWhiteSpace(AppointmentId)
            || !string.IsNullOrWhiteSpace(WorkOrderNumber)
            || !string.IsNullOrWhiteSpace(AppointmentDate)
            || !string.IsNullOrWhiteSpace(OrderCreateDate)
            || !string.IsNullOrWhiteSpace(Compliance);

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
            WorkOrderNumber = NullIfBlank(WorkOrderNumber),
            AppointmentDate = NullIfBlank(AppointmentDate),
            OrderCreateDate = NullIfBlank(OrderCreateDate),
            Compliance = NullIfBlank(Compliance)
        };

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
