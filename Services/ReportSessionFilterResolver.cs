using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public readonly record struct ViewFilterSnapshot(
    string DateFilterMode,
    string? SelectedDate,
    string? DateRangeStart,
    string? DateRangeEnd,
    List<string> SelectedTerritories,
    List<string> SelectedStatuses,
    List<string> SelectedSubStatuses,
    List<string> SelectedSkillsets,
    List<string> SelectedCustomerTypes,
    List<string> SelectedOrderCreateDates);

public static class ReportSessionFilterResolver
{
    public static ViewFilterSnapshot GetSessionFiltersForView(ReportSessionData session, string activeView)
    {
        var isStatus = string.Equals(activeView, "status", StringComparison.OrdinalIgnoreCase);
        var hasAnyPerTabSnapshot = session.HasStatusFilters || session.HasPendingFilters;
        if (isStatus && session.HasStatusFilters)
        {
            return new ViewFilterSnapshot(
                session.StatusDateFilterMode ?? "all",
                session.StatusSelectedDate,
                session.StatusDateRangeStart,
                session.StatusDateRangeEnd,
                session.StatusSelectedTerritories ?? [],
                session.StatusSelectedStatuses ?? [],
                session.StatusSelectedSubStatuses ?? [],
                session.StatusSelectedSkillsets ?? [],
                session.StatusSelectedCustomerTypes ?? [],
                session.StatusSelectedOrderCreateDates ?? []);
        }

        if (!isStatus && session.HasPendingFilters)
        {
            return new ViewFilterSnapshot(
                session.PendingDateFilterMode ?? "all",
                session.PendingSelectedDate,
                session.PendingDateRangeStart,
                session.PendingDateRangeEnd,
                session.PendingSelectedTerritories ?? [],
                session.PendingSelectedStatuses ?? [],
                session.PendingSelectedSubStatuses ?? [],
                session.PendingSelectedSkillsets ?? [],
                session.PendingSelectedCustomerTypes ?? [],
                session.PendingSelectedOrderCreateDates ?? []);
        }

        if (!hasAnyPerTabSnapshot)
        {
            var sourceDefaultView = session.CsvSourceKind == CsvSourceKind.AllStatus ? "status" : "pending";
            var isSourceDefaultView = string.Equals(activeView, sourceDefaultView, StringComparison.OrdinalIgnoreCase);
            if (!isSourceDefaultView)
            {
                return new ViewFilterSnapshot(
                    "all",
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    []);
            }
        }

        if (isStatus && session.HasPendingFilters && !session.HasStatusFilters)
        {
            return new ViewFilterSnapshot(
                "all",
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                [],
                []);
        }

        if (!isStatus && session.HasStatusFilters && !session.HasPendingFilters)
        {
            return new ViewFilterSnapshot(
                "all",
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                [],
                []);
        }

        return new ViewFilterSnapshot(
            session.DateFilterMode ?? "all",
            session.SelectedDate,
            session.DateRangeStart,
            session.DateRangeEnd,
            session.SelectedTerritories ?? [],
            session.SelectedStatuses ?? [],
            session.SelectedSubStatuses ?? [],
            session.SelectedSkillsets ?? [],
            session.SelectedCustomerTypes ?? [],
            session.SelectedOrderCreateDates ?? []);
    }
}
