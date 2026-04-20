using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public class OperationalReportService : IOperationalReportService
{
    private static readonly Regex NumericRegex = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex MetricRegex = new(@"^\s*(-?\d+(?:\.\d+)?)\s*([A-Za-z%\/]+)?", RegexOptions.Compiled);

    private static CsvConfiguration CsvConfig => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = false,
        BadDataFound = null,
        MissingFieldFound = null,
        TrimOptions = TrimOptions.Trim
    };

    public async Task<OperationalReportPanelViewModel> BuildReportAsync(
        string csvFilePath,
        string sourceFileName,
        string? selectedPerformanceGroup,
        string periodFilter,
        string dateFilterMode,
        string? selectedDate,
        string? dateRangeStart,
        string? dateRangeEnd,
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadRowsAsync(csvFilePath, cancellationToken);
        if (rows.Count == 0)
            return new OperationalReportPanelViewModel();

        if (TryBuildAlarmDashboard(rows, sourceFileName, periodFilter, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd, out var alarmDashboard))
            return alarmDashboard;

        if (TryBuildPerformanceDashboard(rows, sourceFileName, selectedPerformanceGroup, periodFilter, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd, out var perfDashboard))
            return perfDashboard;

        return new OperationalReportPanelViewModel();
    }

    private static async Task<List<string[]>> ReadRowsAsync(string csvFilePath, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        await using var stream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        using var parser = new CsvParser(reader, CsvConfig);

        while (await parser.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = parser.Record;
            if (row is null)
                continue;
            rows.Add(row);
        }

        return rows;
    }

    private static bool TryBuildAlarmDashboard(
        IReadOnlyList<string[]> rows,
        string sourceFileName,
        string periodFilter,
        string dateFilterMode,
        string? selectedDate,
        string? dateRangeStart,
        string? dateRangeEnd,
        out OperationalReportPanelViewModel dashboard)
    {
        dashboard = new OperationalReportPanelViewModel();
        var headerIndex = FindHeaderRow(rows, "Ocurrence Time", "Level");
        if (headerIndex < 0)
            return false;

        var header = rows[headerIndex];
        var timeIdx = FindColumnIndex(header, "Ocurrence Time");
        if (timeIdx < 0)
            return false;

        var rawTimes = new List<DateTime>(rows.Count);
        for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (!TryGetCell(row, timeIdx, out var rawTime))
                continue;
            if (!DateTime.TryParseExact(rawTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurredAt))
                continue;
            rawTimes.Add(occurredAt);
        }

        if (rawTimes.Count == 0)
            return false;

        var effectivePeriod = NormalizePeriod(periodFilter);
        var maxTimestamp = rawTimes.Max();
        var countsByBucket = new SortedDictionary<DateTime, int>();
        foreach (var occurredAt in rawTimes)
        {
            if (!MatchesPeriodFilter(occurredAt, maxTimestamp, effectivePeriod))
                continue;
            if (!MatchesDateFilter(occurredAt.Date, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd))
                continue;

            var bucketTime = FloorToTenMinuteBucket(occurredAt);
            countsByBucket[bucketTime] = countsByBucket.GetValueOrDefault(bucketTime) + 1;
        }

        if (countsByBucket.Count == 0)
            return false;

        dashboard = new OperationalReportPanelViewModel
        {
            HasReport = true,
            SourceFileName = sourceFileName,
            ReportKind = OperationalReportKind.AlarmHistory,
            SelectedPeriod = effectivePeriod,
            DateFilterMode = NormalizeMode(dateFilterMode),
            SelectedDate = selectedDate,
            DateRangeStart = dateRangeStart,
            DateRangeEnd = dateRangeEnd,
            IntervalLabels = countsByBucket.Keys.Select(FormatTenMinuteBucketLabel).ToList(),
            IntervalValues = countsByBucket.Values.Select(value => (double)value).ToList()
        };

        return true;
    }

    private static bool TryBuildPerformanceDashboard(
        IReadOnlyList<string[]> rows,
        string sourceFileName,
        string? selectedPerformanceGroup,
        string periodFilter,
        string dateFilterMode,
        string? selectedDate,
        string? dateRangeStart,
        string? dateRangeEnd,
        out OperationalReportPanelViewModel dashboard)
    {
        dashboard = new OperationalReportPanelViewModel();
        var headerIndex = FindHeaderRow(rows, "Performance Group", "Performance Current Value", "Start Time");
        if (headerIndex < 0)
            return false;

        var header = rows[headerIndex];
        var groupIdx = FindColumnIndex(header, "Performance Group");
        var valueIdx = FindColumnIndex(header, "Performance Current Value");
        var timeIdx = FindColumnIndex(header, "Start Time");
        if (groupIdx < 0 || valueIdx < 0 || timeIdx < 0)
            return false;

        var availableGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawPoints = new List<(string Group, DateTime StartTime, double NumericValue, string Unit)>(rows.Count);

        for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (!TryGetCell(row, groupIdx, out var perfGroup) || string.IsNullOrWhiteSpace(perfGroup))
                continue;
            availableGroups.Add(perfGroup);

            if (!TryGetCell(row, timeIdx, out var startTimeRaw))
                continue;
            if (!DateTime.TryParseExact(startTimeRaw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime))
                continue;

            if (!TryGetCell(row, valueIdx, out var currentValueRaw))
                continue;
            if (!TryParseMetricValue(currentValueRaw, out var numericValue, out var unit))
                continue;

            rawPoints.Add((perfGroup, startTime, numericValue, unit));
        }

        if (availableGroups.Count == 0)
            return false;

        var resolvedGroup = ResolvePerformanceGroup(selectedPerformanceGroup, availableGroups);
        var effectivePeriod = NormalizePeriod(periodFilter);
        var maxTimestamp = rawPoints.Count > 0
            ? rawPoints.Max(item => item.StartTime)
            : (DateTime?)null;
        var pointsToAggregate = rawPoints
            .Where(item => string.IsNullOrWhiteSpace(resolvedGroup) || string.Equals(resolvedGroup, item.Group, StringComparison.OrdinalIgnoreCase))
            .Where(item => !maxTimestamp.HasValue || MatchesPeriodFilter(item.StartTime, maxTimestamp.Value, effectivePeriod))
            .Where(item => MatchesDateFilter(item.StartTime.Date, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd))
            .ToList();

        if (!pointsToAggregate.Any() && !string.IsNullOrWhiteSpace(resolvedGroup) && !string.Equals(resolvedGroup, selectedPerformanceGroup, StringComparison.OrdinalIgnoreCase))
        {
            pointsToAggregate = rawPoints
                .Where(item => string.Equals(resolvedGroup, item.Group, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var isTrafficAnalysis = string.Equals(resolvedGroup, "Traffic Analysis", StringComparison.OrdinalIgnoreCase);
        var throughputPoints = pointsToAggregate
            .Where(item => item.Unit == "mbit/s")
            .Select(item => (item.StartTime, item.NumericValue))
            .ToList();
        var percentPoints = pointsToAggregate
            .Where(item => item.Unit == "%")
            .Select(item => (item.StartTime, item.NumericValue))
            .ToList();
        var fallbackPoints = pointsToAggregate.Select(item => (item.StartTime, item.NumericValue)).ToList();

        var primarySeries = BuildRawOccurrenceSeries(throughputPoints.Any() ? throughputPoints : fallbackPoints);
        var secondarySeries = isTrafficAnalysis ? BuildRawOccurrenceSeries(percentPoints) : [];
        var primaryLabel = throughputPoints.Any() ? "Traffic Throughput (Mbit/s)" : "Performance Value";
        var secondaryLabel = secondarySeries.Count > 0 ? "Traffic Utilization (%)" : string.Empty;

        dashboard = new OperationalReportPanelViewModel
        {
            HasReport = primarySeries.Count > 0 || secondarySeries.Count > 0,
            SourceFileName = sourceFileName,
            ReportKind = OperationalReportKind.PerformanceHistory,
            SelectedPerformanceGroup = resolvedGroup,
            SelectedPeriod = effectivePeriod,
            DateFilterMode = NormalizeMode(dateFilterMode),
            SelectedDate = selectedDate,
            DateRangeStart = dateRangeStart,
            DateRangeEnd = dateRangeEnd,
            AvailablePerformanceGroups = availableGroups.OrderBy(value => value).ToList(),
            PrimaryMetricLabel = primaryLabel,
            SecondaryMetricLabel = secondaryLabel,
            IntervalLabels = primarySeries.Select(item => item.Label).ToList(),
            IntervalValues = primarySeries.Select(item => Math.Round(item.Value, 2)).ToList(),
            SecondaryIntervalLabels = secondarySeries.Select(item => item.Label).ToList(),
            SecondaryIntervalValues = secondarySeries.Select(item => Math.Round(item.Value, 2)).ToList()
        };

        return true;
    }

    private static string ResolvePerformanceGroup(string? selectedPerformanceGroup, IEnumerable<string> availableGroups)
    {
        if (!string.IsNullOrWhiteSpace(selectedPerformanceGroup))
        {
            var directMatch = availableGroups.FirstOrDefault(value =>
                string.Equals(value, selectedPerformanceGroup, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(directMatch))
                return directMatch;
        }

        var trafficGroup = availableGroups.FirstOrDefault(value =>
            string.Equals(value, "Traffic Analysis", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(trafficGroup))
            return trafficGroup;

        return availableGroups.OrderBy(value => value).FirstOrDefault() ?? string.Empty;
    }

    private static int FindHeaderRow(IReadOnlyList<string[]> rows, params string[] requiredColumns)
    {
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var hasAllColumns = requiredColumns.All(columnName => FindColumnIndex(row, columnName) >= 0);
            if (hasAllColumns)
                return rowIndex;
        }

        return -1;
    }

    private static int FindColumnIndex(IReadOnlyList<string> row, string columnName)
    {
        for (var index = 0; index < row.Count; index++)
        {
            if (string.Equals(row[index]?.Trim(), columnName, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static bool TryGetCell(IReadOnlyList<string> row, int index, out string value)
    {
        value = string.Empty;
        if (index < 0 || index >= row.Count)
            return false;
        value = row[index]?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryParseNumericValue(string rawValue, out double value)
    {
        value = 0;
        var match = NumericRegex.Match(rawValue);
        if (!match.Success)
            return false;

        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseMetricValue(string rawValue, out double value, out string unit)
    {
        value = 0;
        unit = "unknown";
        var match = MetricRegex.Match(rawValue);
        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        var rawUnit = (match.Groups[2].Value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawUnit))
            return true;

        if (rawUnit.Contains("gbit/s"))
        {
            value *= 1000d;
            unit = "mbit/s";
            return true;
        }

        if (rawUnit.Contains("kbit/s"))
        {
            value /= 1000d;
            unit = "mbit/s";
            return true;
        }

        if (rawUnit.Contains("mbit/s"))
        {
            unit = "mbit/s";
            return true;
        }

        if (rawUnit.Contains('%'))
        {
            unit = "%";
            return true;
        }

        if (rawUnit.Contains("dbm"))
        {
            unit = "dbm";
            return true;
        }

        return true;
    }

    private static DateTime FloorToTenMinuteBucket(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, (value.Minute / 10) * 10, 0);

    private static string FormatTenMinuteBucketLabel(DateTime bucketStart)
    {
        var bucketEnd = bucketStart.AddMinutes(9).AddSeconds(59);
        return $"{bucketStart:yyyy-MM-dd HH:mm} - {bucketEnd:HH:mm}";
    }

    private static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase))
            return "single";
        if (string.Equals(mode, "range", StringComparison.OrdinalIgnoreCase))
            return "range";
        return "all";
    }

    private static string NormalizePeriod(string? period)
    {
        if (string.Equals(period, "10min", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "10 min", StringComparison.OrdinalIgnoreCase))
            return "10min";
        if (string.Equals(period, "30min", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "30 min", StringComparison.OrdinalIgnoreCase))
            return "30min";
        if (string.Equals(period, "3hour", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "3 hour", StringComparison.OrdinalIgnoreCase))
            return "3hour";
        if (string.Equals(period, "6hour", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "6 hour", StringComparison.OrdinalIgnoreCase))
            return "6hour";
        if (string.Equals(period, "12hour", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "12 hour", StringComparison.OrdinalIgnoreCase))
            return "12hour";
        if (string.Equals(period, "1hour", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "hour", StringComparison.OrdinalIgnoreCase))
            return "1hour";
        if (string.Equals(period, "1day", StringComparison.OrdinalIgnoreCase)
            || string.Equals(period, "day", StringComparison.OrdinalIgnoreCase))
            return "1day";
        if (string.Equals(period, "week", StringComparison.OrdinalIgnoreCase))
            return "week";
        if (string.Equals(period, "month", StringComparison.OrdinalIgnoreCase))
            return "month";
        return "1hour";
    }

    private static bool MatchesDateFilter(
        DateTime valueDate,
        string? mode,
        string? selectedDate,
        string? dateRangeStart,
        string? dateRangeEnd)
    {
        var normalizedMode = NormalizeMode(mode);
        if (normalizedMode == "all")
            return true;

        if (normalizedMode == "single")
        {
            if (!DateOnly.TryParseExact(selectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wanted))
                return true;
            return DateOnly.FromDateTime(valueDate) == wanted;
        }

        var hasStart = DateOnly.TryParseExact(dateRangeStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
        var hasEnd = DateOnly.TryParseExact(dateRangeEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
        if (!hasStart || !hasEnd)
            return true;

        var candidate = DateOnly.FromDateTime(valueDate);
        return candidate >= start && candidate <= end;
    }

    private static bool MatchesPeriodFilter(DateTime candidateTime, DateTime maxTimestamp, string period)
    {
        var normalized = NormalizePeriod(period);
        var window = normalized switch
        {
            "10min" => TimeSpan.FromMinutes(10),
            "30min" => TimeSpan.FromMinutes(30),
            "1hour" => TimeSpan.FromHours(1),
            "3hour" => TimeSpan.FromHours(3),
            "6hour" => TimeSpan.FromHours(6),
            "12hour" => TimeSpan.FromHours(12),
            "1day" => TimeSpan.FromDays(1),
            "week" => TimeSpan.FromDays(7),
            "month" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromHours(1)
        };
        var start = maxTimestamp - window;
        return candidateTime >= start && candidateTime <= maxTimestamp;
    }

    private static List<(string Label, double Value)> BuildSeries(List<(DateTime StartTime, double NumericValue)> points)
    {
        return points
            .GroupBy(item => FloorToTenMinuteBucket(item.StartTime))
            .OrderBy(group => group.Key)
            .Select(group => (FormatTenMinuteBucketLabel(group.Key), group.Average(item => item.NumericValue)))
            .ToList();
    }

    private static List<(string Label, double Value)> BuildRawOccurrenceSeries(List<(DateTime StartTime, double NumericValue)> points)
    {
        return points
            .OrderBy(item => item.StartTime)
            .Select(item => (FormatOccurrenceLabel(item.StartTime), item.NumericValue))
            .ToList();
    }

    private static string FormatOccurrenceLabel(DateTime occurrenceTime) =>
        $"{occurrenceTime:yyyy-MM-dd HH:mm:ss}";
}
