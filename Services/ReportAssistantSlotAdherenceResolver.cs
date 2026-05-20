using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SlotAd_Globe.Services;

/// <summary>Deterministic Pass/Fail-on-date answers from slotAdherenceByDate (matches dashboard chart).</summary>
public static class ReportAssistantSlotAdherenceResolver
{
    private static readonly Regex MonthDayPattern = new(
        @"\b(?:on\s+)?(?:the\s+)?(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)\s+(\d{1,2})(?:st|nd|rd|th)?(?:\s*,?\s*(\d{4}))?\b" +
        @"|\b(\d{1,2})(?:st|nd|rd|th)?\s+of\s+(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)(?:\s*,?\s*(\d{4}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex IsoDatePattern = new(
        @"\b(\d{4}-\d{2}-\d{2})\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex AmPmBothPattern = new(
        @"\b(am|pm)\b.*\b(am|pm)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public sealed class ResolvedAnswer
    {
        public required string AppointmentDateIso { get; init; }
        public required string Tier { get; init; }
        public int Count { get; init; }
        public int Scheduled { get; init; }
        public int Fail { get; init; }
        public int Pass { get; init; }
        public bool IsSimpleTotalOnDate { get; init; }
    }

    public static ResolvedAnswer? TryResolve(
        string userMessage,
        IReadOnlyDictionary<string, object?> context)
    {
        if (string.IsNullOrWhiteSpace(userMessage)
            || !context.TryGetValue("slotAdherenceByDate", out var raw))
            return null;

        var byDate = ParseByDateList(raw!);
        if (byDate.Count == 0)
            return null;

        if (!TryParseComplianceTier(userMessage, out var tier))
            return null;

        if (!TryResolveAppointmentDate(userMessage, byDate.Keys, context, out var isoDate))
            return null;

        if (!byDate.TryGetValue(isoDate, out var day))
            return null;

        if (!IsSimpleTotalOnDateQuestion(userMessage))
            return null;

        var count = string.Equals(tier, "Fail", StringComparison.OrdinalIgnoreCase) ? day.Fail : day.Pass;

        return new ResolvedAnswer
        {
            AppointmentDateIso = isoDate,
            Tier = tier,
            Count = count,
            Scheduled = day.Scheduled,
            Fail = day.Fail,
            Pass = day.Pass,
            IsSimpleTotalOnDate = true
        };
    }

    public static string FormatReply(ResolvedAnswer answer, string? activeFiltersSummary)
    {
        var dateDisplay = DateOnly.TryParse(answer.AppointmentDateIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)
            : answer.AppointmentDateIso;

        var tierVerb = string.Equals(answer.Tier, "Fail", StringComparison.OrdinalIgnoreCase) ? "failed" : "passed";

        var reply =
            $"{answer.Count} appointments {tierVerb} on {dateDisplay} in the slot-adherence report " +
            $"(Pass/Fail compliance tier; scheduled that day: {answer.Scheduled}, fail: {answer.Fail}).";

        if (!string.IsNullOrWhiteSpace(activeFiltersSummary))
            reply += " Dashboard filters: " + activeFiltersSummary.Trim().TrimEnd('.') + ".";

        return reply;
    }

    public static string? GetActiveFiltersSummary(IReadOnlyDictionary<string, object?> context)
    {
        if (!context.TryGetValue("activeFilters", out var raw) || raw is not Dictionary<string, object?> af)
            return null;

        return af.TryGetValue("summary", out var summary) ? summary?.ToString() : null;
    }

    private static bool IsSimpleTotalOnDateQuestion(string userMessage)
    {
        if (AmPmBothPattern.IsMatch(userMessage))
            return false;

        if (ContainsWholePhrase(userMessage, "repair") || ContainsWholePhrase(userMessage, "install"))
            return false;

        if (Regex.IsMatch(userMessage, @"\b(am|pm)\s*slot\b", RegexOptions.IgnoreCase))
            return false;

        return true;
    }

    private static bool TryParseComplianceTier(string text, out string tier)
    {
        tier = "";

        if (Regex.IsMatch(text, @"\bfail(?:ed|ure)?\b", RegexOptions.IgnoreCase))
        {
            tier = "Fail";
            return true;
        }

        if (ContainsWholePhrase(text, "passed") || Regex.IsMatch(text, @"\bpass(?:ed)?\b", RegexOptions.IgnoreCase))
        {
            tier = "Pass";
            return true;
        }

        return false;
    }

    private static bool TryResolveAppointmentDate(
        string text,
        IEnumerable<string> knownDates,
        IReadOnlyDictionary<string, object?> context,
        out string isoDate)
    {
        isoDate = "";

        var knownList = knownDates.ToList();

        foreach (Match m in IsoDatePattern.Matches(text))
        {
            var iso = m.Groups[1].Value;
            if (knownList.Count == 0 || knownList.Contains(iso, StringComparer.OrdinalIgnoreCase))
            {
                isoDate = iso;
                return true;
            }
        }

        foreach (var known in knownList.OrderByDescending(d => d.Length))
        {
            if (text.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                isoDate = known;
                return true;
            }
        }

        string? fileMin = null;
        string? fileMax = null;
        if (context.TryGetValue("dataset", out var dsRaw) && dsRaw is Dictionary<string, object?> ds
            && ds.TryGetValue("appointmentDateRangeInFile", out var rangeRaw)
            && rangeRaw is Dictionary<string, object?> range)
        {
            fileMin = range.GetValueOrDefault("min")?.ToString();
            fileMax = range.GetValueOrDefault("max")?.ToString();
        }

        var match = MonthDayPattern.Match(text);
        if (!match.Success)
            return false;

        if (!TryParseMonthDayMatch(match, out var month, out var day, out var explicitYear))
            return false;

        var candidates = knownList
            .Where(d => DateOnly.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                        && dt.Month == month && dt.Day == day)
            .ToList();

        if (candidates.Count == 0)
        {
            var year = explicitYear ?? InferYear(month, day, fileMin, fileMax);
            if (year is null)
                return false;
            try
            {
                isoDate = new DateOnly(year.Value, month, day).ToString("yyyy-MM-dd");
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (explicitYear.HasValue)
        {
            var target = $"{explicitYear.Value:0000}-{month:00}-{day:00}";
            isoDate = candidates.FirstOrDefault(c => string.Equals(c, target, StringComparison.OrdinalIgnoreCase))
                      ?? candidates.OrderByDescending(c => c, StringComparer.Ordinal).First();
            return true;
        }

        if (candidates.Count == 1)
        {
            isoDate = candidates[0];
            return true;
        }

        if (DateOnly.TryParse(fileMax, CultureInfo.InvariantCulture, DateTimeStyles.None, out var maxInFile))
        {
            var inMaxYear = candidates
                .Where(c => DateOnly.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                            && dt.Year == maxInFile.Year)
                .OrderByDescending(c => c, StringComparer.Ordinal)
                .FirstOrDefault();
            if (inMaxYear is not null)
            {
                isoDate = inMaxYear;
                return true;
            }
        }

        isoDate = candidates.OrderByDescending(c => c, StringComparer.Ordinal).First();
        return true;
    }

    private static int? InferYear(int month, int day, string? fileMin, string? fileMax)
    {
        if (DateOnly.TryParse(fileMax, CultureInfo.InvariantCulture, DateTimeStyles.None, out var max)
            && DateOnly.TryParse(fileMin, CultureInfo.InvariantCulture, DateTimeStyles.None, out var min))
        {
            foreach (var year in Enumerable.Range(min.Year, max.Year - min.Year + 1).Reverse())
            {
                try
                {
                    var candidate = new DateOnly(year, month, day);
                    if (candidate >= min && candidate <= max)
                        return year;
                }
                catch
                {
                    // invalid day for month
                }
            }
        }

        return fileMax is not null && DateOnly.TryParse(fileMax, out var mx) ? mx.Year : DateTime.UtcNow.Year;
    }

    private static Dictionary<string, (int Scheduled, int Pass, int Fail)> ParseByDateList(object raw)
    {
        var result = new Dictionary<string, (int Scheduled, int Pass, int Fail)>(StringComparer.Ordinal);

        if (raw is not IEnumerable<object> items)
            return result;

        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> row)
                continue;

            var date = row.GetValueOrDefault("date")?.ToString();
            if (string.IsNullOrWhiteSpace(date))
                continue;

            result[date] = (
                ToInt(row.GetValueOrDefault("scheduled")),
                ToInt(row.GetValueOrDefault("pass")),
                ToInt(row.GetValueOrDefault("fail")));
        }

        return result;
    }

    private static int ToInt(object? value) =>
        value switch
        {
            int i => i,
            long l => (int)l,
            JsonElement je when je.TryGetInt32(out var n) => n,
            _ => int.TryParse(value?.ToString(), out var p) ? p : 0
        };

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

    private static bool TryParseMonthName(string token, out int month)
    {
        month = 0;
        var key = token.Trim().ToLowerInvariant();
        return key switch
        {
            "january" or "jan" => (month = 1) == 1,
            "february" or "feb" => (month = 2) == 2,
            "march" or "mar" => (month = 3) == 3,
            "april" or "apr" => (month = 4) == 4,
            "may" => (month = 5) == 5,
            "june" or "jun" => (month = 6) == 6,
            "july" or "jul" => (month = 7) == 7,
            "august" or "aug" => (month = 8) == 8,
            "september" or "sep" or "sept" => (month = 9) == 9,
            "october" or "oct" => (month = 10) == 10,
            "november" or "nov" => (month = 11) == 11,
            "december" or "dec" => (month = 12) == 12,
            _ => false
        };
    }

    private static bool ContainsWholePhrase(string text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;
        return Regex.IsMatch(
            text,
            $@"\b{Regex.Escape(phrase)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
