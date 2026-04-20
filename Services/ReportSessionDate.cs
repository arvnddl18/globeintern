using System.Globalization;

namespace SlotAd_Globe.Services;

public static class ReportSessionDate
{
    public static DateOnly? ParseDateOrNull(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
