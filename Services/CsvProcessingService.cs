using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public class CsvProcessingService : ICsvProcessingService
{
    private readonly IConfiguration _config;
    private readonly ILogger<CsvProcessingService> _logger;

    public CsvProcessingService(IConfiguration config, ILogger<CsvProcessingService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private string Col(string key) =>
        _config[$"CsvMapping:{key}"] ?? throw new InvalidOperationException($"Missing CsvMapping:{key}");

    private string ColOr(string key, string fallback) =>
        _config[$"CsvMapping:{key}"] ?? fallback;

    private static CsvConfiguration DefaultCsvConfig => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        BadDataFound = null,
        MissingFieldFound = null,
        TrimOptions = TrimOptions.Trim
    };

    public async Task<FilterOptionsViewModel> ExtractFilterOptionsAsync(
        Stream csvStream, string reportToken, CancellationToken cancellationToken = default)
    {
        var dates = new HashSet<DateOnly>();
        var territories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skillsets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderCreateDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var appointmentDateCol = Col("AppointmentDateColumn");
        var territoryCol = Col("TerritoryColumn");
        var statusCol = Col("StatusColumn");
        var subStatusCol = Col("SubStatusColumn");
        var skillsetCol = Col("SkillsetColumn");
        var orderCreateDateCol = Col("OrderCreateDateColumn");

        var yieldEvery = int.TryParse(_config["CsvMapping:FilterExtractionYieldEveryRows"], out var yr)
            ? Math.Clamp(yr, 500, 100_000)
            : 8000;

        const int readerBuffer = 1024 * 1024;
        using var reader = new StreamReader(csvStream, Encoding.UTF8, true, readerBuffer);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        var rowCount = 0;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;
            if (rowCount % yieldEvery == 0)
                await Task.Yield();

            var rawDate = csv.GetField(appointmentDateCol) ?? "";
            if (!TryExtractDate(rawDate, out var rowDate))
                continue;

            dates.Add(rowDate);
            AddIfNotEmpty(territories, csv.GetField(territoryCol));
            AddIfNotEmpty(statuses, csv.GetField(statusCol));
            AddIfNotEmpty(subStatuses, csv.GetField(subStatusCol));
            AddIfNotEmpty(skillsets, csv.GetField(skillsetCol));
            AddIfNotEmpty(orderCreateDates, csv.GetField(orderCreateDateCol));
        }

        return new FilterOptionsViewModel
        {
            ReportToken = reportToken,
            AvailableDates = dates.OrderByDescending(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToList(),
            AvailableTerritories = territories.OrderBy(x => x).ToList(),
            AvailableStatuses = statuses.OrderBy(x => x).ToList(),
            AvailableSubStatuses = subStatuses.OrderBy(x => x).ToList(),
            AvailableSkillsets = skillsets.OrderBy(x => x).ToList(),
            AvailableOrderCreateDates = orderCreateDates.OrderByDescending(x => x).ToList()
        };
    }

    public async Task<CsvSourceKind> DetectCsvSourceKindAsync(
        string csvFilePath,
        string? originalFileName,
        CancellationToken cancellationToken = default)
    {
        var completionCol = _config["CsvMapping:CompletionDateColumn"] ?? "completiondate";
        var statusCol = Col("StatusColumn");
        var completedValue = _config["CsvMapping:CompletedStatusValue"] ?? "Completed";
        var sampleRows = int.TryParse(_config["CsvMapping:CsvKindSampleRows"], out var sr) ? Math.Clamp(sr, 50, 5000) : 800;
        var minFrac = double.TryParse(_config["CsvMapping:CsvKindAllStatusMinParseableFraction"], NumberStyles.Float, CultureInfo.InvariantCulture, out var mf) ? mf : 0.12;
        var minCompleted = int.TryParse(_config["CsvMapping:CsvKindAllStatusMinCompletedInSample"], out var mc) ? mc : 5;

        var fn = originalFileName ?? "";
        if (fn.Contains("ALL STATUS", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("CSV kind AllStatus (filename hint: ALL STATUS)");
            return CsvSourceKind.AllStatus;
        }

        await using var fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        if (!await csv.ReadAsync())
        {
            _logger.LogInformation("CSV kind Pending (empty file)");
            return CsvSourceKind.Pending;
        }

        csv.ReadHeader();
        var header = csv.HeaderRecord;
        if (header is null || !header.Any(h => string.Equals(h, completionCol, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("CSV kind Pending (no {Col} column)", completionCol);
            return CsvSourceKind.Pending;
        }

        var completedIdx = Array.FindIndex(header, h => string.Equals(h, statusCol, StringComparison.OrdinalIgnoreCase));
        var completionIdx = Array.FindIndex(header, h => string.Equals(h, completionCol, StringComparison.OrdinalIgnoreCase));
        if (completedIdx < 0 || completionIdx < 0)
        {
            _logger.LogInformation("CSV kind Pending (missing status or completion column index)");
            return CsvSourceKind.Pending;
        }

        var samples = 0;
        var parseableCompletion = 0;
        var completedCount = 0;

        while (samples < sampleRows && await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples++;
            var status = csv.GetField(completedIdx) ?? "";
            var rawComp = csv.GetField(completionIdx) ?? "";
            if (string.Equals(status, completedValue, StringComparison.OrdinalIgnoreCase))
                completedCount++;
            if (TryParseCompletionDateTime(rawComp, out _))
                parseableCompletion++;
        }

        if (samples == 0)
        {
            _logger.LogInformation("CSV kind Pending (no data rows in sample)");
            return CsvSourceKind.Pending;
        }

        var parseableFraction = (double)parseableCompletion / samples;
        var allStatusByData = completedCount >= minCompleted || parseableFraction >= minFrac;

        if (fn.Contains("PENDING", StringComparison.OrdinalIgnoreCase) && !allStatusByData)
        {
            _logger.LogInformation(
                "CSV kind Pending (filename PENDING, data: completed={Completed}, parseableFrac={Frac:F2})",
                completedCount, parseableFraction);
            return CsvSourceKind.Pending;
        }

        if (fn.Contains("STATUS", StringComparison.OrdinalIgnoreCase) && !fn.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("CSV kind AllStatus (filename contains STATUS)");
            return CsvSourceKind.AllStatus;
        }

        var kind = allStatusByData ? CsvSourceKind.AllStatus : CsvSourceKind.Pending;
        _logger.LogInformation(
            "CSV kind {Kind} (sample={Samples}, completed={Completed}, parseableCompletion={Parseable}, parseableFrac={Frac:F2})",
            kind, samples, completedCount, parseableCompletion, parseableFraction);
        return kind;
    }

    public async Task<KpiDashboardViewModel> ComputeKpiAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates)
    {
        var appointmentDateCol = Col("AppointmentDateColumn");
        var lastUpdateCol = Col("LastUpdateDateColumn");
        var territoryCol = Col("TerritoryColumn");
        var statusCol = Col("StatusColumn");
        var subStatusCol = Col("SubStatusColumn");
        var skillsetCol = Col("SkillsetColumn");
        var appointmentIdCol = Col("AppointmentIdColumn");
        var workOrderCol = ColOr("WorkOrderColumn", appointmentIdCol);
        var orderCreateDateCol = Col("OrderCreateDateColumn");
        var delayedValue = Col("DelayedStatusValue");
        var amSlotMarker = Col("AmSlotMarker");
        var amLapseCutoff = int.Parse(Col("AmLapseCutoffHour"));
        var pmLapseCutoff = int.Parse(Col("PmLapseCutoffHour"));

        var territorySet = ToSet(selectedTerritories);
        var statusSet = ToSet(selectedStatuses);
        var subStatusSet = ToSet(selectedSubStatuses);
        var skillsetSet = ToSet(selectedSkillsets);
        var orderCreateDateSet = ToSet(selectedOrderCreateDates);

        var statusDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subStatusDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var territoryDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skillsetDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dateCounters = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var uniqueTerritories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueSkillsets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int totalRows = 0, amCount = 0, pmCount = 0, delayedCount = 0, lapsedCount = 0;
        int forVisitSubStatusCount = 0, forRescheduleSubStatusCount = 0, repairSkillsetCount = 0, completedStatusCount = 0;
        DateOnly? minDate = null, maxDate = null;

        var previewRows = new List<Dictionary<string, string>>();

        using var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        /* ── Coordinate + facility-name column detection ── */
        var headers    = csv.HeaderRecord;
        var latCol     = FindCoordColumn(headers, _config["CsvMapping:LatitudeColumn"],    "latitude", "lat");
        var lngCol     = FindCoordColumn(headers, _config["CsvMapping:LongitudeColumn"],   "longitude", "lng", "lon", "long");
        var facilityCol = FindCoordColumn(headers, _config["CsvMapping:FacilityNameColumn"], "facilityname", "facility_name", "facility", "name");
        var dpidCol    = FindCoordColumn(headers, null, "dpid");
        var hasCoords  = latCol is not null && lngCol is not null;
        var maxDots    = int.TryParse(_config["CsvMapping:NapDotsMaxRows"], out var _md) ? _md : 12_000;
        var napDots    = new List<float[]>(capacity: Math.Min(maxDots, 4096));
        var napDotNames = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotDpids = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotSkillsets = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotTerritories = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotStatuses = new List<string>(capacity: Math.Min(maxDots, 4096));


        while (await csv.ReadAsync())
        {
            var rawAppointmentDate = csv.GetField(appointmentDateCol) ?? "";
            if (!TryExtractDate(rawAppointmentDate, out var rowDate))
                continue;

            if (!MatchesDateFilter(rowDate, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd))
                continue;

            var territory = csv.GetField(territoryCol) ?? "";
            var status = (csv.GetField(statusCol) ?? "").Trim();
            var subStatus = csv.GetField(subStatusCol) ?? "";
            var skillset = csv.GetField(skillsetCol) ?? "";
            var orderCreateDate = csv.GetField(orderCreateDateCol) ?? "";
            var appointmentId = csv.GetField(workOrderCol) ?? csv.GetField(appointmentIdCol) ?? "";
            var rawLastUpdate = csv.GetField(lastUpdateCol) ?? "";

            if (!MatchesFilter(territorySet, territory)) continue;
            if (!MatchesFilter(statusSet, status)) continue;
            if (!MatchesFilter(subStatusSet, subStatus)) continue;
            if (!MatchesFilter(skillsetSet, skillset)) continue;
            if (!MatchesFilter(orderCreateDateSet, orderCreateDate)) continue;

            totalRows++;

            /* ── Extract per-NAP coordinates + facility name + dpid + skillset ── */
            if (hasCoords && napDots.Count < maxDots)
            {
                var rawLat = csv.GetField(latCol!) ?? "";
                var rawLng = csv.GetField(lngCol!) ?? "";
                if (TryParseCoord(rawLat, out var lat) && TryParseCoord(rawLng, out var lng))
                {
                    napDots.Add([lat, lng, EncodeDateInt(rowDate)]);
                    napDotNames.Add(facilityCol is not null ? (csv.GetField(facilityCol) ?? "").Trim() : "");
                    napDotDpids.Add(dpidCol is not null ? (csv.GetField(dpidCol) ?? "").Trim() : "");
                    napDotSkillsets.Add(skillset);
                    napDotTerritories.Add(territory);
                    napDotStatuses.Add(status);
                }
            }

            var dateKey = rowDate.ToString("yyyy-MM-dd");
            dateCounters[dateKey] = dateCounters.GetValueOrDefault(dateKey) + 1;

            if (minDate is null || rowDate < minDate) minDate = rowDate;
            if (maxDate is null || rowDate > maxDate) maxDate = rowDate;

            IncrementDist(statusDist, status);
            IncrementDist(subStatusDist, subStatus);
            IncrementDist(territoryDist, territory);
            IncrementDist(skillsetDist, skillset);
            uniqueTerritories.Add(territory);
            uniqueSkillsets.Add(skillset);
            if (IsSubStatusForVisit(subStatus))
                forVisitSubStatusCount++;
            if (IsNormalizedMatch(subStatus, "ForReschedule"))
                forRescheduleSubStatusCount++;
            if (IsNormalizedMatch(skillset, "Repair"))
                repairSkillsetCount++;
            if (IsNormalizedMatch(status, "Completed"))
                completedStatusCount++;

            var isAmSlot = rawAppointmentDate.Contains(amSlotMarker, StringComparison.OrdinalIgnoreCase);
            if (isAmSlot) amCount++;
            else pmCount++;

            var isDelayed = string.Equals(status, delayedValue, StringComparison.OrdinalIgnoreCase);
            var rowIsLapsed = false;

            if (isDelayed)
            {
                delayedCount++;
                if (TryExtractTime(rawLastUpdate, out var updateHour))
                {
                    var cutoff = isAmSlot ? amLapseCutoff : pmLapseCutoff;
                    if (updateHour >= cutoff)
                    {
                        lapsedCount++;
                        rowIsLapsed = true;
                    }
                }
            }

            previewRows.Add(new Dictionary<string, string>
            {
                ["AppointmentID"] = appointmentId,
                ["AppointmentDate"] = rawAppointmentDate,
                ["Skillset"] = skillset,
                ["Status"] = status,
                ["SubStatus"] = subStatus,
                ["Territory"] = territory,
                ["OrderCreateDate"] = orderCreateDate,
                ["LastUpdateDate"] = rawLastUpdate,
                ["_isDelayed"] = isDelayed ? "1" : "0",
                ["_isLapsed"] = rowIsLapsed ? "1" : "0"
            });
        }

        var dateRangeDisplay = (minDate, maxDate) switch
        {
            (null, _) => "No data",
            var (mn, mx) when mn == mx => mn.Value.ToString("MMM dd, yyyy"),
            var (mn, mx) => $"{mn!.Value:MMM dd, yyyy} - {mx!.Value:MMM dd, yyyy}"
        };

        _logger.LogInformation(
            "KPI computed ({Mode}): total={Total}, AM={Am}, PM={Pm}, delayed={Delayed}, lapsed={Lapsed}",
            dateFilterMode, totalRows, amCount, pmCount, delayedCount, lapsedCount);

        return new KpiDashboardViewModel
        {
            TotalAppointments = totalRows,
            UniqueTerritoriesCount = uniqueTerritories.Count,
            UniqueSkillsetsCount = uniqueSkillsets.Count,
            DateRangeDisplay = dateRangeDisplay,
            StatusDistribution = statusDist,
            SubStatusDistribution = subStatusDist,
            TerritoryDistribution = territoryDist,
            SkillsetDistribution = skillsetDist,
            AppointmentsByDate = new Dictionary<string, int>(dateCounters),
            AmSlotCount = amCount,
            PmSlotCount = pmCount,
            DelayedCount = delayedCount,
            LapsedCount = lapsedCount,
            ForVisitSubStatusCount = forVisitSubStatusCount,
            ForRescheduleSubStatusCount = forRescheduleSubStatusCount,
            RepairSkillsetCount = repairSkillsetCount,
            CompletedStatusCount = completedStatusCount,
            PreviewRows = previewRows,
            TotalFilteredRows = totalRows,
            NapDots = napDots,
            NapDotNames = napDotNames,
            NapDotDpids = napDotDpids,
            NapDotSkillsets = napDotSkillsets,
            NapDotTerritories = napDotTerritories,
            NapDotStatuses = napDotStatuses,
            HasCoordinates = hasCoords
        };
    }

    public async Task<KpiDashboardViewModel> ComputeAllStatusComplianceKpiAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates)
    {
        var appointmentDateCol = Col("AppointmentDateColumn");
        var lastUpdateCol = Col("LastUpdateDateColumn");
        var territoryCol = Col("TerritoryColumn");
        var statusCol = Col("StatusColumn");
        var subStatusCol = Col("SubStatusColumn");
        var skillsetCol = Col("SkillsetColumn");
        var appointmentIdCol = Col("AppointmentIdColumn");
        var orderCreateDateCol = Col("OrderCreateDateColumn");
        var delayedValue = Col("DelayedStatusValue");
        var completedValue = Col("CompletedStatusValue");
        var completionDateCol = Col("CompletionDateColumn");
        var amSlotMarker = Col("AmSlotMarker");
        var amLapseCutoff = int.Parse(Col("AmLapseCutoffHour"));
        var pmLapseCutoff = int.Parse(Col("PmLapseCutoffHour"));

        var territorySet = ToSet(selectedTerritories);
        var statusSet = ToSet(selectedStatuses);
        var subStatusSet = ToSet(selectedSubStatuses);
        var skillsetSet = ToSet(selectedSkillsets);
        var orderCreateDateSet = ToSet(selectedOrderCreateDates);

        var statusDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subStatusDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var territoryDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skillsetDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dateCounters = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var uniqueTerritories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueSkillsets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failReasons = new Dictionary<string, int>(StringComparer.Ordinal);

        int totalRows = 0, amCount = 0, pmCount = 0, delayedCount = 0, lapsedCount = 0;
        int forVisitSubStatusCount = 0, forRescheduleSubStatusCount = 0, repairSkillsetCount = 0, completedStatusCount = 0;
        int passCount = 0, failCount = 0, naCount = 0;
        DateOnly? minDate = null, maxDate = null;

        var previewRows = new List<Dictionary<string, string>>();

        await using var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        /* ── Coordinate + facility-name column detection ── */
        var headers     = csv.HeaderRecord;
        var latCol      = FindCoordColumn(headers, _config["CsvMapping:LatitudeColumn"],    "latitude", "lat");
        var lngCol      = FindCoordColumn(headers, _config["CsvMapping:LongitudeColumn"],   "longitude", "lng", "lon", "long");
        var facilityCol  = FindCoordColumn(headers, _config["CsvMapping:FacilityNameColumn"], "facilityname", "facility_name", "facility", "name");
        var dpidCol2    = FindCoordColumn(headers, null, "dpid");
        var hasCoords   = latCol is not null && lngCol is not null;
        var maxDots     = int.TryParse(_config["CsvMapping:NapDotsMaxRows"], out var _md2) ? _md2 : 12_000;
        var napDots     = new List<float[]>(capacity: Math.Min(maxDots, 4096));
        var napDotNames = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotDpids2 = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotSkillsets2 = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotTerritories2 = new List<string>(capacity: Math.Min(maxDots, 4096));
        var napDotStatuses2 = new List<string>(capacity: Math.Min(maxDots, 4096));


        while (await csv.ReadAsync())
        {
            var rawAppointmentDate = csv.GetField(appointmentDateCol) ?? "";
            if (!TryExtractDate(rawAppointmentDate, out var rowDate))
                continue;

            if (!MatchesDateFilter(rowDate, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd))
                continue;

            var territory = csv.GetField(territoryCol) ?? "";
            var status = csv.GetField(statusCol) ?? "";
            var subStatus = csv.GetField(subStatusCol) ?? "";
            var skillset = csv.GetField(skillsetCol) ?? "";
            var orderCreateDate = csv.GetField(orderCreateDateCol) ?? "";
            var appointmentId = csv.GetField(appointmentIdCol) ?? "";
            var rawLastUpdate = csv.GetField(lastUpdateCol) ?? "";
            var rawCompletion = csv.GetField(completionDateCol) ?? "";

            if (!MatchesFilter(territorySet, territory)) continue;
            if (!MatchesFilter(statusSet, status)) continue;
            if (!MatchesFilter(subStatusSet, subStatus)) continue;
            if (!MatchesFilter(skillsetSet, skillset)) continue;
            if (!MatchesFilter(orderCreateDateSet, orderCreateDate)) continue;

            totalRows++;

            /* ── Extract per-NAP coordinates + facility name + dpid + skillset ── */
            if (hasCoords && napDots.Count < maxDots)
            {
                var rawLat = csv.GetField(latCol!) ?? "";
                var rawLng = csv.GetField(lngCol!) ?? "";
                if (TryParseCoord(rawLat, out var lat) && TryParseCoord(rawLng, out var lng))
                {
                    napDots.Add([lat, lng, EncodeDateInt(rowDate)]);
                    napDotNames.Add(facilityCol is not null ? (csv.GetField(facilityCol) ?? "").Trim() : "");
                    napDotDpids2.Add(dpidCol2 is not null ? (csv.GetField(dpidCol2) ?? "").Trim() : "");
                    napDotSkillsets2.Add(skillset);
                    napDotTerritories2.Add(territory);
                    napDotStatuses2.Add(status);
                }
            }

            var dateKey = rowDate.ToString("yyyy-MM-dd");
            dateCounters[dateKey] = dateCounters.GetValueOrDefault(dateKey) + 1;

            if (minDate is null || rowDate < minDate) minDate = rowDate;
            if (maxDate is null || rowDate > maxDate) maxDate = rowDate;

            IncrementDist(statusDist, status);
            IncrementDist(subStatusDist, subStatus);
            IncrementDist(territoryDist, territory);
            IncrementDist(skillsetDist, skillset);
            uniqueTerritories.Add(territory);
            uniqueSkillsets.Add(skillset);
            if (IsSubStatusForVisit(subStatus))
                forVisitSubStatusCount++;
            if (IsNormalizedMatch(subStatus, "ForReschedule"))
                forRescheduleSubStatusCount++;
            if (IsNormalizedMatch(skillset, "Repair"))
                repairSkillsetCount++;
            if (IsNormalizedMatch(status, completedValue))
                completedStatusCount++;

            var isAmSlot = rawAppointmentDate.Contains(amSlotMarker, StringComparison.OrdinalIgnoreCase);
            if (isAmSlot) amCount++;
            else pmCount++;

            var isDelayed = string.Equals(status, delayedValue, StringComparison.OrdinalIgnoreCase);
            var rowIsLapsed = false;

            if (isDelayed)
            {
                delayedCount++;
                if (TryExtractTime(rawLastUpdate, out var updateHour))
                {
                    var cutoff = isAmSlot ? amLapseCutoff : pmLapseCutoff;
                    if (updateHour >= cutoff)
                    {
                        lapsedCount++;
                        rowIsLapsed = true;
                    }
                }
            }

            var (tier, reason) = ClassifyCompliance(
                isDelayed,
                rowDate,
                isAmSlot,
                rawCompletion);

            string complianceLabel;
            switch (tier)
            {
                case "Pass":
                    passCount++;
                    complianceLabel = "Pass";
                    break;
                case "Fail":
                    failCount++;
                    complianceLabel = "Fail";
                    if (!string.IsNullOrEmpty(reason))
                        IncrementDist(failReasons, reason);
                    break;
                default:
                    naCount++;
                    complianceLabel = "N/A";
                    break;
            }

            previewRows.Add(new Dictionary<string, string>
            {
                ["AppointmentID"] = appointmentId,
                ["AppointmentDate"] = rawAppointmentDate,
                ["Skillset"] = skillset,
                ["Status"] = status,
                ["SubStatus"] = subStatus,
                ["Territory"] = territory,
                ["OrderCreateDate"] = orderCreateDate,
                ["LastUpdateDate"] = rawLastUpdate,
                ["CompletionDate"] = rawCompletion,
                ["_isDelayed"] = isDelayed ? "1" : "0",
                ["_isLapsed"] = rowIsLapsed ? "1" : "0",
                ["Compliance"] = complianceLabel,
                ["ComplianceReason"] = string.IsNullOrEmpty(reason) ? "" : reason
            });
        }

        var dateRangeDisplay = (minDate, maxDate) switch
        {
            (null, _) => "No data",
            var (mn, mx) when mn == mx => mn.Value.ToString("MMM dd, yyyy"),
            var (mn, mx) => $"{mn!.Value:MMM dd, yyyy} - {mx!.Value:MMM dd, yyyy}"
        };

        _logger.LogInformation(
            "All Status KPI ({Mode}): total={Total}, pass={Pass}, fail={Fail}, na={Na}, AM={Am}, PM={Pm}, delayed={Delayed}, lapsed={Lapsed}",
            dateFilterMode, totalRows, passCount, failCount, naCount, amCount, pmCount, delayedCount, lapsedCount);

        return new KpiDashboardViewModel
        {
            TotalAppointments = totalRows,
            UniqueTerritoriesCount = uniqueTerritories.Count,
            UniqueSkillsetsCount = uniqueSkillsets.Count,
            DateRangeDisplay = dateRangeDisplay,
            StatusDistribution = statusDist,
            SubStatusDistribution = subStatusDist,
            TerritoryDistribution = territoryDist,
            SkillsetDistribution = skillsetDist,
            AppointmentsByDate = new Dictionary<string, int>(dateCounters),
            AmSlotCount = amCount,
            PmSlotCount = pmCount,
            DelayedCount = delayedCount,
            LapsedCount = lapsedCount,
            ForVisitSubStatusCount = forVisitSubStatusCount,
            ForRescheduleSubStatusCount = forRescheduleSubStatusCount,
            RepairSkillsetCount = repairSkillsetCount,
            CompletedStatusCount = completedStatusCount,
            CompliancePassCount = passCount,
            ComplianceFailCount = failCount,
            ComplianceNaCount = naCount,
            ComplianceFailReasons = failReasons,
            ComplianceMetricsAvailable = true,
            PreviewRows = previewRows,
            TotalFilteredRows = totalRows,
            NapDots = napDots,
            NapDotNames = napDotNames,
            NapDotDpids = napDotDpids2,
            NapDotSkillsets = napDotSkillsets2,
            NapDotTerritories = napDotTerritories2,
            NapDotStatuses = napDotStatuses2,
            HasCoordinates = hasCoords
        };
    }

    private static (string Tier, string Reason) ClassifyCompliance(
        bool isDelayed,
        DateOnly appointmentDate,
        bool appointmentIsAm,
        string rawCompletion)
    {
        if (isDelayed)
            return ("Fail", "Delayed");

        var hasCompletion = TryParseCompletionDateTime(rawCompletion, out var cdt);

        if (hasCompletion)
        {
            var completionDate = DateOnly.FromDateTime(cdt);
            if (completionDate != appointmentDate)
                return ("Fail", "CompletedWrongDate");

            // Business rule: completion at or after 12:59 PM is treated as PM.
            var completionTime = TimeOnly.FromDateTime(cdt);
            var completionIsAm = completionTime < new TimeOnly(12, 59);
            // Mentor formula:
            // AM -> PM = Fail
            // AM -> AM, PM -> AM, PM -> PM = Pass
            if (appointmentIsAm && !completionIsAm)
                return ("Fail", "SlotMismatch");
            return ("Pass", "");
        }

        return ("N/A", "");
    }

    public async Task<MemoryStream> GenerateFilteredXlsxAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates)
    {
        var appointmentDateCol = Col("AppointmentDateColumn");
        var lastUpdateCol = Col("LastUpdateDateColumn");
        var territoryCol = Col("TerritoryColumn");
        var statusCol = Col("StatusColumn");
        var subStatusCol = Col("SubStatusColumn");
        var skillsetCol = Col("SkillsetColumn");
        var appointmentIdCol = Col("AppointmentIdColumn");
        var orderCreateDateCol = Col("OrderCreateDateColumn");
        var delayedValue = Col("DelayedStatusValue");
        var amSlotMarker = Col("AmSlotMarker");
        var amLapseCutoff = int.Parse(Col("AmLapseCutoffHour"));
        var pmLapseCutoff = int.Parse(Col("PmLapseCutoffHour"));

        var territorySet = ToSet(selectedTerritories);
        var statusSet = ToSet(selectedStatuses);
        var subStatusSet = ToSet(selectedSubStatuses);
        var skillsetSet = ToSet(selectedSkillsets);
        var orderCreateDateSet = ToSet(selectedOrderCreateDates);

        var rows = new List<FilteredRow>();
        bool hasDelayed = false;

        using (var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        using (var reader = new StreamReader(fileStream))
        using (var csv = new CsvReader(reader, DefaultCsvConfig))
        {
            await csv.ReadAsync();
            csv.ReadHeader();

            while (await csv.ReadAsync())
            {
                var rawAppointmentDate = csv.GetField(appointmentDateCol) ?? "";
                if (!TryExtractDate(rawAppointmentDate, out var rowDate))
                    continue;

                if (!MatchesDateFilter(rowDate, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd))
                    continue;

                var territory = csv.GetField(territoryCol) ?? "";
                var status = csv.GetField(statusCol) ?? "";
                var subStatus = csv.GetField(subStatusCol) ?? "";
                var skillset = csv.GetField(skillsetCol) ?? "";
                var orderCreateDate = csv.GetField(orderCreateDateCol) ?? "";
                var appointmentId = csv.GetField(appointmentIdCol) ?? "";
                var rawLastUpdate = csv.GetField(lastUpdateCol) ?? "";

                if (!MatchesFilter(territorySet, territory)) continue;
                if (!MatchesFilter(statusSet, status)) continue;
                if (!MatchesFilter(subStatusSet, subStatus)) continue;
                if (!MatchesFilter(skillsetSet, skillset)) continue;
                if (!MatchesFilter(orderCreateDateSet, orderCreateDate)) continue;

                var isDelayed = string.Equals(status, delayedValue, StringComparison.OrdinalIgnoreCase);
                if (isDelayed) hasDelayed = true;

                var isAmSlot = rawAppointmentDate.Contains(amSlotMarker, StringComparison.OrdinalIgnoreCase);
                var cutoff = isAmSlot ? amLapseCutoff : pmLapseCutoff;
                var isLapsed = isDelayed
                    && TryExtractTime(rawLastUpdate, out var updateHour)
                    && updateHour >= cutoff;

                rows.Add(new FilteredRow
                {
                    AppointmentId = appointmentId,
                    AppointmentDate = rawAppointmentDate,
                    Skillset = skillset,
                    Status = status,
                    SubStatus = subStatus,
                    Territory = territory,
                    OrderCreateDate = orderCreateDate,
                    LastUpdateDate = rawLastUpdate,
                    IsDelayed = isDelayed,
                    IsLapsed = isLapsed,
                    SortDate = rowDate
                });
            }
        }

        _logger.LogInformation("Filtered XLSX export: {Count} rows, hasDelayed={HasDelayed}", rows.Count, hasDelayed);

        var kpiSnapshot = ComputeKpiFromFilteredRows(rows, amSlotMarker);

        return BuildFilteredXlsx(
            rows,
            hasDelayed,
            kpiSnapshot,
            dateFilterMode,
            selectedDate,
            dateRangeStart,
            dateRangeEnd,
            selectedTerritories,
            selectedStatuses,
            selectedSubStatuses,
            selectedSkillsets,
            selectedOrderCreateDates);
    }

    public async Task<MemoryStream> GenerateXlsxAsync(
        string tempFilePath,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets)
    {
        var appointmentDateCol = Col("AppointmentDateColumn");
        var lastUpdateCol = Col("LastUpdateDateColumn");
        var territoryCol = Col("TerritoryColumn");
        var statusCol = Col("StatusColumn");
        var subStatusCol = Col("SubStatusColumn");
        var skillsetCol = Col("SkillsetColumn");
        var appointmentIdCol = Col("AppointmentIdColumn");
        var delayedValue = Col("DelayedStatusValue");
        var amSlotMarker = Col("AmSlotMarker");
        var amLapseCutoff = int.Parse(Col("AmLapseCutoffHour"));
        var pmLapseCutoff = int.Parse(Col("PmLapseCutoffHour"));

        var territorySet = new HashSet<string>(selectedTerritories, StringComparer.OrdinalIgnoreCase);
        var statusSet = new HashSet<string>(selectedStatuses, StringComparer.OrdinalIgnoreCase);
        var subStatusSet = new HashSet<string>(selectedSubStatuses, StringComparer.OrdinalIgnoreCase);
        var skillsetSet = new HashSet<string>(selectedSkillsets, StringComparer.OrdinalIgnoreCase);

        var amRows = new List<(string AppointmentId, DateOnly Date, bool IsLapsed)>();
        var pmRows = new List<(string AppointmentId, DateOnly Date, bool IsLapsed)>();

        using (var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        using (var reader = new StreamReader(fileStream))
        using (var csv = new CsvReader(reader, DefaultCsvConfig))
        {
            await csv.ReadAsync();
            csv.ReadHeader();

            while (await csv.ReadAsync())
            {
                var rawAppointmentDate = csv.GetField(appointmentDateCol) ?? "";

                if (!TryExtractDate(rawAppointmentDate, out var rowDate))
                    continue;

                if (!MatchesDateFilter(rowDate, dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd))
                    continue;

                var territory = csv.GetField(territoryCol) ?? "";
                var status = csv.GetField(statusCol) ?? "";
                var subStatus = csv.GetField(subStatusCol) ?? "";
                var skillset = csv.GetField(skillsetCol) ?? "";

                if (territorySet.Count > 0 && !territorySet.Contains(territory))
                    continue;
                if (statusSet.Count > 0 && !statusSet.Contains(status))
                    continue;
                if (subStatusSet.Count > 0 && !subStatusSet.Contains(subStatus))
                    continue;
                if (skillsetSet.Count > 0 && !skillsetSet.Contains(skillset))
                    continue;

                if (!string.Equals(status, delayedValue, StringComparison.OrdinalIgnoreCase))
                    continue;

                var appointmentId = csv.GetField(appointmentIdCol) ?? "";
                var isAmSlot = rawAppointmentDate.Contains(amSlotMarker, StringComparison.OrdinalIgnoreCase);

                var rawLastUpdate = csv.GetField(lastUpdateCol) ?? "";
                var cutoff = isAmSlot ? amLapseCutoff : pmLapseCutoff;
                var isLapsed = TryExtractTime(rawLastUpdate, out var updateHour) && updateHour >= cutoff;

                if (isAmSlot)
                    amRows.Add((appointmentId, rowDate, isLapsed));
                else
                    pmRows.Add((appointmentId, rowDate, isLapsed));
            }
        }

        _logger.LogInformation(
            "Generated report ({Mode}): AM rows={AmCount}, PM rows={PmCount}",
            dateFilterMode, amRows.Count, pmRows.Count);

        return BuildXlsx(dateFilterMode, selectedDate, dateRangeStart, dateRangeEnd, amRows, pmRows);
    }

    public Task<MemoryStream> GenerateSlotAdherenceCsvAsync(KpiDashboardViewModel kpi)
    {
        var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 65536, leaveOpen: true))
        {
            static string CsvEscape(string value)
            {
                var raw = value ?? string.Empty;
                if (raw.Contains("\""))
                    raw = raw.Replace("\"", "\"\"");
                if (raw.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                    return $"\"{raw}\"";
                return raw;
            }

            static void WriteCsvRow(StreamWriter w, IEnumerable<string> cells)
            {
                w.WriteLine(string.Join(",", cells.Select(CsvEscape)));
            }

            writer.WriteLine("Slot Adherence Export");
            writer.WriteLine();
            WriteCsvRow(writer, ["Metric", "Value"]);
            WriteCsvRow(writer, ["Total Appointments", kpi.TotalAppointments.ToString(CultureInfo.InvariantCulture)]);
            WriteCsvRow(writer, ["Unique Territories", kpi.UniqueTerritoriesCount.ToString(CultureInfo.InvariantCulture)]);
            WriteCsvRow(writer, ["Unique Skillsets", kpi.UniqueSkillsetsCount.ToString(CultureInfo.InvariantCulture)]);
            WriteCsvRow(writer, ["Date Range", kpi.DateRangeDisplay]);
            WriteCsvRow(writer, ["AM Slot Count", kpi.AmSlotCount.ToString(CultureInfo.InvariantCulture)]);
            WriteCsvRow(writer, ["PM Slot Count", kpi.PmSlotCount.ToString(CultureInfo.InvariantCulture)]);
            WriteCsvRow(writer, ["Delayed Count", kpi.DelayedCount.ToString(CultureInfo.InvariantCulture)]);
            WriteCsvRow(writer, ["Lapsed Count", kpi.LapsedCount.ToString(CultureInfo.InvariantCulture)]);
            if (kpi.ActiveDashboardView == "status")
            {
                WriteCsvRow(writer, ["Compliance Pass", kpi.CompliancePassCount.ToString(CultureInfo.InvariantCulture)]);
                WriteCsvRow(writer, ["Compliance Fail", kpi.ComplianceFailCount.ToString(CultureInfo.InvariantCulture)]);
                WriteCsvRow(writer, ["Compliance N/A", kpi.ComplianceNaCount.ToString(CultureInfo.InvariantCulture)]);
            }

            static void WriteDistributionBlock(StreamWriter w, string title, Dictionary<string, int> dist)
            {
                w.WriteLine();
                w.WriteLine(title);
                WriteCsvRow(w, ["Name", "Count"]);
                foreach (var kv in dist.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    WriteCsvRow(w, [kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture)]);
            }

            WriteDistributionBlock(writer, "Status Distribution", kpi.StatusDistribution);
            WriteDistributionBlock(writer, "SubStatus Distribution", kpi.SubStatusDistribution);
            WriteDistributionBlock(writer, "Territory Distribution", kpi.TerritoryDistribution);
            WriteDistributionBlock(writer, "Skillset Distribution", kpi.SkillsetDistribution);

            writer.WriteLine();
            writer.WriteLine("Data Preview (Full Filtered Rows)");
            var headers = DeterminePreviewHeaders(kpi);
            WriteCsvRow(writer, headers);
            foreach (var row in kpi.PreviewRows)
            {
                var values = headers.Select(h => row.TryGetValue(h, out var value) ? value : string.Empty);
                WriteCsvRow(writer, values);
            }
            writer.Flush();
        }

        stream.Position = 0;
        return Task.FromResult(stream);
    }

    public Task<MemoryStream> GenerateSlotAdherenceVisualXlsxAsync(
        KpiDashboardViewModel kpi,
        IReadOnlyCollection<SlotAdherenceChartImage> chartImages)
    {
        var workbook = new XLWorkbook();
        var summarySheet = workbook.Worksheets.Add("Slot Adherence");
        var dataSheet = workbook.Worksheets.Add("Data Preview");

        var row = 1;
        summarySheet.Cell(row, 1).Value = "Slot Adherence Report Export";
        summarySheet.Range(row, 1, row, 4).Merge();
        summarySheet.Cell(row, 1).Style.Font.Bold = true;
        summarySheet.Cell(row, 1).Style.Font.FontSize = 16;
        summarySheet.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E0E7FF");
        row += 2;

        summarySheet.Cell(row, 1).Value = "View";
        summarySheet.Cell(row, 2).Value = kpi.ActiveDashboardView;
        row++;
        summarySheet.Cell(row, 1).Value = "Date Range";
        summarySheet.Cell(row, 2).Value = kpi.DateRangeDisplay;
        row += 2;

        summarySheet.Cell(row, 1).Value = "KPI";
        summarySheet.Cell(row, 2).Value = "Value";
        summarySheet.Range(row, 1, row, 2).Style.Font.Bold = true;
        summarySheet.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#DBEAFE");
        row++;

        void WriteMetric(string name, string value)
        {
            summarySheet.Cell(row, 1).Value = name;
            summarySheet.Cell(row, 2).Value = value;
            row++;
        }

        WriteMetric("Total Appointments", kpi.TotalAppointments.ToString(CultureInfo.InvariantCulture));
        WriteMetric("Unique Territories", kpi.UniqueTerritoriesCount.ToString(CultureInfo.InvariantCulture));
        WriteMetric("Unique Skillsets", kpi.UniqueSkillsetsCount.ToString(CultureInfo.InvariantCulture));
        WriteMetric("AM Slot Count", kpi.AmSlotCount.ToString(CultureInfo.InvariantCulture));
        WriteMetric("PM Slot Count", kpi.PmSlotCount.ToString(CultureInfo.InvariantCulture));
        WriteMetric("Delayed Count", kpi.DelayedCount.ToString(CultureInfo.InvariantCulture));
        WriteMetric("Lapsed Count", kpi.LapsedCount.ToString(CultureInfo.InvariantCulture));
        if (kpi.ActiveDashboardView == "status")
        {
            WriteMetric("Compliance Pass", kpi.CompliancePassCount.ToString(CultureInfo.InvariantCulture));
            WriteMetric("Compliance Fail", kpi.ComplianceFailCount.ToString(CultureInfo.InvariantCulture));
            WriteMetric("Compliance N/A", kpi.ComplianceNaCount.ToString(CultureInfo.InvariantCulture));
        }

        row += 1;
        WriteDistributionTable(summarySheet, ref row, "Status Distribution", kpi.StatusDistribution);
        WriteDistributionTable(summarySheet, ref row, "Territory Distribution", kpi.TerritoryDistribution);
        WriteDistributionTable(summarySheet, ref row, "Skillset Distribution", kpi.SkillsetDistribution);

        row += 1;
        summarySheet.Cell(row, 1).Value = "Chart Snapshots";
        summarySheet.Cell(row, 1).Style.Font.Bold = true;
        row++;
        foreach (var chart in chartImages.Take(8))
        {
            if (!TryDecodeDataUrl(chart.DataUrl, out var imageBytes))
                continue;
            summarySheet.Cell(row, 1).Value = string.IsNullOrWhiteSpace(chart.ChartTitle) ? chart.ChartKey : chart.ChartTitle;
            row++;
            using var imageStream = new MemoryStream(imageBytes, writable: false);
            summarySheet.AddPicture(imageStream)
                .MoveTo(summarySheet.Cell(row, 1))
                .WithSize(900, 280);
            row += 16;
        }

        var previewHeaders = DeterminePreviewHeaders(kpi);
        for (var i = 0; i < previewHeaders.Count; i++)
            dataSheet.Cell(1, i + 1).Value = previewHeaders[i];
        var dataHeader = dataSheet.Range(1, 1, 1, previewHeaders.Count);
        dataHeader.Style.Font.Bold = true;
        dataHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E7EB");
        dataSheet.SheetView.FreezeRows(1);

        for (var rowIndex = 0; rowIndex < kpi.PreviewRows.Count; rowIndex++)
        {
            var src = kpi.PreviewRows[rowIndex];
            for (var colIndex = 0; colIndex < previewHeaders.Count; colIndex++)
            {
                var key = previewHeaders[colIndex];
                dataSheet.Cell(rowIndex + 2, colIndex + 1).Value = src.TryGetValue(key, out var val) ? val : string.Empty;
            }
        }

        summarySheet.Columns(1, 4).AdjustToContents();
        dataSheet.Columns(1, Math.Min(10, previewHeaders.Count)).AdjustToContents();

        var output = new MemoryStream();
        workbook.SaveAs(output);
        output.Position = 0;
        return Task.FromResult(output);
    }

    private static void WriteDistributionTable(IXLWorksheet sheet, ref int row, string title, Dictionary<string, int> values)
    {
        sheet.Cell(row, 1).Value = title;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        row++;
        sheet.Cell(row, 1).Value = "Name";
        sheet.Cell(row, 2).Value = "Count";
        sheet.Range(row, 1, row, 2).Style.Font.Bold = true;
        sheet.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F4F6");
        row++;
        foreach (var kv in values.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            sheet.Cell(row, 1).Value = kv.Key;
            sheet.Cell(row, 2).Value = kv.Value;
            row++;
        }
        row++;
    }

    private static List<string> DeterminePreviewHeaders(KpiDashboardViewModel kpi)
    {
        string[] preferredHeaders =
        [
            "AppointmentID", "AppointmentDate", "Skillset", "Status", "SubStatus", "Territory",
            "OrderCreateDate", "LastUpdateDate", "CompletionDate", "Compliance", "ComplianceReason"
        ];
        var availableKeys = new HashSet<string>(
            kpi.PreviewRows.SelectMany(row => row.Keys).Where(k => !k.StartsWith("_", StringComparison.Ordinal)),
            StringComparer.OrdinalIgnoreCase);
        var ordered = preferredHeaders.Where(availableKeys.Contains).ToList();
        ordered.AddRange(availableKeys.Where(k => !ordered.Contains(k, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    private static bool TryDecodeDataUrl(string dataUrl, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(dataUrl))
            return false;
        var markerIndex = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;
        var base64 = dataUrl[(markerIndex + "base64,".Length)..].Trim();
        if (base64.Length == 0 || base64.Length > 1_500_000)
            return false;
        try
        {
            data = Convert.FromBase64String(base64);
            return data.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record FilteredDatasetKpi(
        int TotalAppointments,
        int UniqueTerritoriesCount,
        int UniqueSkillsetsCount,
        string DateRangeDisplay,
        int AmSlotCount,
        int PmSlotCount,
        int DelayedCount,
        int LapsedCount,
        Dictionary<string, int> StatusDistribution,
        Dictionary<string, int> SubStatusDistribution,
        Dictionary<string, int> TerritoryDistribution,
        Dictionary<string, int> SkillsetDistribution,
        Dictionary<string, int> AppointmentsByDate);

    private static FilteredDatasetKpi ComputeKpiFromFilteredRows(List<FilteredRow> rows, string amSlotMarker)
    {
        var statusDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subStatusDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var territoryDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skillsetDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dateCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        var uniqueTerritories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueSkillsets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DateOnly? minDate = null, maxDate = null;
        var amCount = 0;
        var pmCount = 0;
        var delayedCount = 0;
        var lapsedCount = 0;

        foreach (var r in rows)
        {
            IncrementDist(statusDist, r.Status);
            IncrementDist(subStatusDist, r.SubStatus);
            IncrementDist(territoryDist, r.Territory);
            IncrementDist(skillsetDist, r.Skillset);

            var dateKey = r.SortDate.ToString("yyyy-MM-dd");
            dateCounters[dateKey] = dateCounters.GetValueOrDefault(dateKey) + 1;

            if (minDate is null || r.SortDate < minDate) minDate = r.SortDate;
            if (maxDate is null || r.SortDate > maxDate) maxDate = r.SortDate;

            uniqueTerritories.Add(r.Territory);
            uniqueSkillsets.Add(r.Skillset);

            var isAm = r.AppointmentDate.Contains(amSlotMarker, StringComparison.OrdinalIgnoreCase);
            if (isAm) amCount++;
            else pmCount++;

            if (r.IsDelayed) delayedCount++;
            if (r.IsLapsed) lapsedCount++;
        }

        var dateRangeDisplay = (minDate, maxDate) switch
        {
            (null, _) => "No data",
            var (mn, mx) when mn == mx => mn.Value.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture),
            var (mn, mx) => $"{mn!.Value:MMM dd, yyyy} - {mx!.Value:MMM dd, yyyy}"
        };

        return new FilteredDatasetKpi(
            rows.Count,
            uniqueTerritories.Count,
            uniqueSkillsets.Count,
            dateRangeDisplay,
            amCount,
            pmCount,
            delayedCount,
            lapsedCount,
            statusDist,
            subStatusDist,
            territoryDist,
            skillsetDist,
            dateCounters);
    }

    private static MemoryStream BuildFilteredXlsx(
        List<FilteredRow> rows,
        bool hasDelayed,
        FilteredDatasetKpi kpi,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates)
    {
        var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Filtered Data");

        string[] headers = hasDelayed
            ? ["AppointmentID", "AppointmentDate", "Skillset", "Status", "SubStatus", "Territory", "OrderCreateDate", "LastUpdateDate", "Lapse"]
            : ["AppointmentID", "AppointmentDate", "Skillset", "Status", "SubStatus", "Territory", "OrderCreateDate", "LastUpdateDate"];

        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thick;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        var sorted = rows.OrderBy(r => r.SortDate).ThenBy(r => r.AppointmentId).ToList();

        var lapsedFill = XLColor.FromArgb(255, 252, 220, 220);
        var delayedFill = XLColor.FromArgb(255, 220, 252, 231);

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var row = i + 2;
            ws.Cell(row, 1).Value = r.AppointmentId;
            ws.Cell(row, 2).Value = r.AppointmentDate;
            ws.Cell(row, 3).Value = r.Skillset;
            ws.Cell(row, 4).Value = r.Status;
            ws.Cell(row, 5).Value = r.SubStatus;
            ws.Cell(row, 6).Value = r.Territory;
            ws.Cell(row, 7).Value = r.OrderCreateDate;
            ws.Cell(row, 8).Value = r.LastUpdateDate;

            if (hasDelayed)
                ws.Cell(row, 9).Value = r.IsLapsed ? "Lapsed" : "";

            if (r.IsLapsed)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = lapsedFill;
            else if (r.IsDelayed)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = delayedFill;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        var summaryWs = wb.Worksheets.Add("Dashboard Summary");
        WriteDashboardSummarySheet(
            summaryWs,
            kpi,
            dateFilterMode,
            selectedDate,
            dateRangeStart,
            dateRangeEnd,
            selectedTerritories,
            selectedStatuses,
            selectedSubStatuses,
            selectedSkillsets,
            selectedOrderCreateDates,
            hasDelayed,
            sorted);

        summaryWs.Columns().AdjustToContents();

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static void WriteDashboardSummarySheet(
        IXLWorksheet ws,
        FilteredDatasetKpi kpi,
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyCollection<string> selectedTerritories,
        IReadOnlyCollection<string> selectedStatuses,
        IReadOnlyCollection<string> selectedSubStatuses,
        IReadOnlyCollection<string> selectedSkillsets,
        IReadOnlyCollection<string> selectedOrderCreateDates,
        bool hasDelayed,
        List<FilteredRow> sortedRows)
    {
        var row = 1;
        ws.Cell(row, 1).Value = "Dashboard Summary";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 14;
        ws.Range(row, 1, row, 4).Merge();
        row += 2;

        ws.Cell(row, 1).Value = "Applied filters";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;

        void WriteFilterRow(string label, string value)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = value;
            row++;
        }

        WriteFilterRow("Date filter mode", dateFilterMode);
        WriteFilterRow("Selected date", selectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        WriteFilterRow("Range start", dateRangeStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        WriteFilterRow("Range end", dateRangeEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        WriteFilterRow("Territories", FormatFilterList(selectedTerritories));
        WriteFilterRow("Statuses", FormatFilterList(selectedStatuses));
        WriteFilterRow("SubStatuses", FormatFilterList(selectedSubStatuses));
        WriteFilterRow("Skillsets", FormatFilterList(selectedSkillsets));
        WriteFilterRow("Order create dates", FormatFilterList(selectedOrderCreateDates));
        row++;

        ws.Cell(row, 1).Value = "KPI summary";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;

        WriteFilterRow("Appointments in export", kpi.TotalAppointments.ToString(CultureInfo.InvariantCulture));
        WriteFilterRow("Date span (appointments)", kpi.DateRangeDisplay);
        WriteFilterRow("Unique territories", kpi.UniqueTerritoriesCount.ToString(CultureInfo.InvariantCulture));
        WriteFilterRow("Unique skillsets", kpi.UniqueSkillsetsCount.ToString(CultureInfo.InvariantCulture));
        WriteFilterRow("AM slot rows", kpi.AmSlotCount.ToString(CultureInfo.InvariantCulture));
        WriteFilterRow("PM slot rows", kpi.PmSlotCount.ToString(CultureInfo.InvariantCulture));
        WriteFilterRow("Delayed rows", kpi.DelayedCount.ToString(CultureInfo.InvariantCulture));
        WriteFilterRow("Lapsed rows", kpi.LapsedCount.ToString(CultureInfo.InvariantCulture));
        row++;

        static void WriteDistribution(IXLWorksheet w, ref int r, string title, Dictionary<string, int> dist)
        {
            w.Cell(r, 1).Value = title;
            w.Cell(r, 1).Style.Font.Bold = true;
            r++;
            w.Cell(r, 1).Value = "Name";
            w.Cell(r, 2).Value = "Count";
            w.Range(r, 1, r, 2).Style.Font.Bold = true;
            w.Range(r, 1, r, 2).Style.Fill.BackgroundColor = XLColor.LightGray;
            r++;
            foreach (var kv in dist.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                w.Cell(r, 1).Value = kv.Key;
                w.Cell(r, 2).Value = kv.Value;
                r++;
            }

            r++;
        }

        WriteDistribution(ws, ref row, "Status distribution", kpi.StatusDistribution);
        WriteDistribution(ws, ref row, "SubStatus distribution", kpi.SubStatusDistribution);
        WriteDistribution(ws, ref row, "Territory distribution", kpi.TerritoryDistribution);
        WriteDistribution(ws, ref row, "Skillset distribution", kpi.SkillsetDistribution);

        ws.Cell(row, 1).Value = "Appointments by appointment date";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;
        ws.Cell(row, 1).Value = "Date";
        ws.Cell(row, 2).Value = "Count";
        ws.Range(row, 1, row, 2).Style.Font.Bold = true;
        ws.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;
        foreach (var kv in kpi.AppointmentsByDate.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            ws.Cell(row, 1).Value = kv.Key;
            ws.Cell(row, 2).Value = kv.Value;
            row++;
        }

        row++;
        ws.Cell(row, 1).Value = "Filtered rows (same as \"Filtered Data\" sheet)";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;

        string[] rowHeaders = hasDelayed
            ? ["AppointmentID", "AppointmentDate", "Skillset", "Status", "SubStatus", "Territory", "OrderCreateDate", "LastUpdateDate", "Lapse"]
            : ["AppointmentID", "AppointmentDate", "Skillset", "Status", "SubStatus", "Territory", "OrderCreateDate", "LastUpdateDate"];
        for (var c = 0; c < rowHeaders.Length; c++)
            ws.Cell(row, c + 1).Value = rowHeaders[c];
        ws.Range(row, 1, row, rowHeaders.Length).Style.Font.Bold = true;
        ws.Range(row, 1, row, rowHeaders.Length).Style.Fill.BackgroundColor = XLColor.LightGray;
        var embeddedTableHeaderRow = row;
        row++;

        var lapsedFill = XLColor.FromArgb(255, 252, 220, 220);
        var delayedFill = XLColor.FromArgb(255, 220, 252, 231);

        foreach (var r in sortedRows)
        {
            var col = 1;
            ws.Cell(row, col++).Value = r.AppointmentId;
            ws.Cell(row, col++).Value = r.AppointmentDate;
            ws.Cell(row, col++).Value = r.Skillset;
            ws.Cell(row, col++).Value = r.Status;
            ws.Cell(row, col++).Value = r.SubStatus;
            ws.Cell(row, col++).Value = r.Territory;
            ws.Cell(row, col++).Value = r.OrderCreateDate;
            ws.Cell(row, col++).Value = r.LastUpdateDate;
            if (hasDelayed)
                ws.Cell(row, col).Value = r.IsLapsed ? "Lapsed" : "";

            if (r.IsLapsed)
                ws.Range(row, 1, row, rowHeaders.Length).Style.Fill.BackgroundColor = lapsedFill;
            else if (r.IsDelayed)
                ws.Range(row, 1, row, rowHeaders.Length).Style.Fill.BackgroundColor = delayedFill;
            row++;
        }

        ws.SheetView.FreezeRows(embeddedTableHeaderRow);
    }

    private static string FormatFilterList(IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
            return "(all)";
        return string.Join(", ", values);
    }

    private static bool MatchesFilter(HashSet<string> filterSet, string value) =>
        filterSet.Count == 0 || filterSet.Contains(value);

    private static HashSet<string> ToSet(IReadOnlyCollection<string> items) =>
        new(items, StringComparer.OrdinalIgnoreCase);

    private static void IncrementDist(Dictionary<string, int> dict, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        dict[key] = dict.GetValueOrDefault(key) + 1;
    }

    private static bool MatchesDateFilter(
        DateOnly rowDate, string mode, DateOnly? single, DateOnly? rangeStart, DateOnly? rangeEnd)
    {
        return mode switch
        {
            "single" when single.HasValue => rowDate == single.Value,
            "range" => (!rangeStart.HasValue || rowDate >= rangeStart.Value)
                    && (!rangeEnd.HasValue || rowDate <= rangeEnd.Value),
            "monthly" when single.HasValue => rowDate.Year == single.Value.Year && rowDate.Month == single.Value.Month,
            _ => true
        };
    }

    private static MemoryStream BuildXlsx(
        string dateFilterMode,
        DateOnly? selectedDate,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        List<(string AppointmentId, DateOnly Date, bool IsLapsed)> amRows,
        List<(string AppointmentId, DateOnly Date, bool IsLapsed)> pmRows)
    {
        var wb = new XLWorkbook();

        var titlePrefix = dateFilterMode switch
        {
            "single" when selectedDate.HasValue =>
                $"For {selectedDate.Value.ToString("MMMM dd, yyyy", CultureInfo.InvariantCulture)}",
            "range" =>
                $"For {FormatDateOrOpen(dateRangeStart, "Start")} to {FormatDateOrOpen(dateRangeEnd, "End")}",
            _ => "For All Dates"
        };

        bool showDateColumn = dateFilterMode != "single";

        WriteSheet(wb, "AM Slot", $"{titlePrefix} - AM Slot", amRows, showDateColumn);
        WriteSheet(wb, "PM Slot", $"{titlePrefix} - PM Slot", pmRows, showDateColumn);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static string FormatDateOrOpen(DateOnly? date, string fallback) =>
        date.HasValue
            ? date.Value.ToString("MMMM dd, yyyy", CultureInfo.InvariantCulture)
            : fallback;

    private static void WriteSheet(
        XLWorkbook wb,
        string sheetName,
        string titleText,
        List<(string AppointmentId, DateOnly Date, bool IsLapsed)> rows,
        bool showDateColumn)
    {
        var ws = wb.Worksheets.Add(sheetName);
        int colCount = showDateColumn ? 4 : 3;
        string lastColLetter = showDateColumn ? "D" : "C";

        ws.Cell("A1").Value = titleText;
        ws.Range($"A1:{lastColLetter}1").Merge();
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        ws.Cell("A2").Value = "For Pending File";
        ws.Range($"A2:{lastColLetter}2").Merge();
        ws.Cell("A2").Style.Font.Bold = true;

        int col = 1;
        ws.Cell(3, col++).Value = "AppointmentId";
        if (showDateColumn)
            ws.Cell(3, col++).Value = "Date";
        ws.Cell(3, col++).Value = "Delay";
        ws.Cell(3, col).Value = "Lapse";

        var headerRange = ws.Range($"A3:{lastColLetter}3");
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thick;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        var sorted = rows.OrderBy(r => r.Date).ThenBy(r => r.AppointmentId).ToList();
        var lapsedFill = XLColor.FromArgb(255, 252, 220, 220);
        var delayedFill = XLColor.FromArgb(255, 220, 252, 231);

        for (var i = 0; i < sorted.Count; i++)
        {
            var row = i + 4;
            var (appointmentId, date, isLapsed) = sorted[i];

            col = 1;
            ws.Cell(row, col++).Value = appointmentId;
            if (showDateColumn)
                ws.Cell(row, col++).Value = date.ToString("yyyy-MM-dd");
            ws.Cell(row, col++).Value = isLapsed ? "" : "Delayed";
            ws.Cell(row, col).Value = isLapsed ? "Lapsed" : "";

            var rowFill = isLapsed ? lapsedFill : delayedFill;
            ws.Range(row, 1, row, colCount).Style.Fill.BackgroundColor = rowFill;
        }

        ws.Columns().AdjustToContents();
    }

    private static bool TryExtractDate(string raw, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var datePart = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool TryExtractTime(string raw, out int hour)
    {
        hour = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            hour = dt.Hour;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the first header whose trimmed name matches <paramref name="configuredName"/> (if set)
    /// or any of the <paramref name="fallbacks"/> (case-insensitive auto-detect).
    /// </summary>
    private static string? FindCoordColumn(string[]? headers, string? configuredName, params string[] fallbacks)
    {
        if (headers is null || headers.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            var m = Array.Find(headers, h => string.Equals(h.Trim(), configuredName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        foreach (var candidate in fallbacks)
        {
            var m = Array.Find(headers, h => string.Equals(h.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }
        return null;
    }

    private static bool TryParseCoord(string? raw, out float result)
    {
        result = 0f;
        return !string.IsNullOrWhiteSpace(raw)
            && float.TryParse(raw.AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>Encodes a date as (year-2000)*10000 + month*100 + day (fits in float32 exactly up to year 2167).</summary>
    private static float EncodeDateInt(DateOnly d) => (d.Year - 2000) * 10_000 + d.Month * 100 + d.Day;

    private static bool TryParseCompletionDateTime(string raw, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool IsSubStatusForVisit(string value) =>
        IsNormalizedMatch(value, "ForVisit") || IsNormalizedMatch(value, "For Visit");

    private static bool IsNormalizedMatch(string value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expected))
            return false;
        return NormalizeValue(value).Equals(NormalizeValue(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        return string.Concat(trimmed.Where(c => !char.IsWhiteSpace(c)));
    }

    private static void AddIfNotEmpty(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            set.Add(value.Trim());
    }

    private static string? FindRemarksColumn(string[]? headers, string? configuredName)
    {
        if (headers is null || headers.Length == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            var configured = headers.FirstOrDefault(h =>
                string.Equals(h.Trim(), configuredName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }

        var preferred = new[]
        {
            "remarks",
            "remark",
            "dispatchremarks",
            "substatusremarks",
            "reason",
            "reasonremarks",
            "workremarks",
            "resolutionremarks"
        };

        foreach (var candidate in preferred)
        {
            var exact = headers.FirstOrDefault(h =>
                string.Equals(NormalizeValue(h), NormalizeValue(candidate), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
                return exact;
        }

        var hasRemark = headers.FirstOrDefault(h =>
            NormalizeValue(h).Contains("remark", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(hasRemark))
            return hasRemark;

        var hasReason = headers.FirstOrDefault(h =>
            NormalizeValue(h).Contains("reason", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(hasReason) ? null : hasReason;
    }

    private static string? FindConfiguredOrPreferredColumn(string[]? headers, string? configuredName, params string[] preferredNames)
    {
        if (headers is null || headers.Length == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            var configured = headers.FirstOrDefault(h =>
                string.Equals(NormalizeValue(h), NormalizeValue(configuredName), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }

        foreach (var preferred in preferredNames)
        {
            if (string.IsNullOrWhiteSpace(preferred))
                continue;

            var exact = headers.FirstOrDefault(h =>
                string.Equals(NormalizeValue(h), NormalizeValue(preferred), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
                return exact;
        }

        return null;
    }

    /// <summary>
    /// Scans the CSV with NO Slot Adherence filters and returns only the data needed
    /// by the Heatmap section. Used to keep the heatmap independent of SA filter selections.
    /// </summary>
    public async Task<KpiDashboardViewModel> ExtractHeatmapSnapshotAsync(string csvFilePath)
    {
        var appointmentDateCol = Col("AppointmentDateColumn");
        var territoryCol       = Col("TerritoryColumn");
        var statusCol          = Col("StatusColumn");
        var skillsetCol        = Col("SkillsetColumn");

        var terrDist  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dateDist  = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0, repair = 0, install = 0;

        var maxDots   = int.TryParse(_config["CsvMapping:NapDotsMaxRows"], out var _md) ? _md : 12_000;
        var dots      = new List<float[]>(Math.Min(maxDots, 4096));
        var names     = new List<string>(Math.Min(maxDots, 4096));
        var dpids     = new List<string>(Math.Min(maxDots, 4096));
        var skillsets = new List<string>(Math.Min(maxDots, 4096));
        var terrs     = new List<string>(Math.Min(maxDots, 4096));
        var statuses  = new List<string>(Math.Min(maxDots, 4096));

        using var fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader     = new StreamReader(fileStream);
        using var csv        = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        var headers     = csv.HeaderRecord;
        var latCol      = FindCoordColumn(headers, _config["CsvMapping:LatitudeColumn"],    "latitude", "lat");
        var lngCol      = FindCoordColumn(headers, _config["CsvMapping:LongitudeColumn"],   "longitude", "lng", "lon", "long");
        var facilityCol = FindCoordColumn(headers, _config["CsvMapping:FacilityNameColumn"], "facilityname", "facility_name", "facility", "name");
        var dpidCol     = FindCoordColumn(headers, null, "dpid");
        var fixDescCol  = FindCoordColumn(headers, null, "fixdescription");
        var hasCoords   = latCol is not null && lngCol is not null;
        var maxJoinRows = int.TryParse(_config["CsvMapping:HeatmapJoinMaxRows"], out var _mjr) ? _mjr : 120_000;
        var joinDateInts = new List<int>(Math.Min(maxJoinRows, 8_192));
        var joinDpids = new List<string>(Math.Min(maxJoinRows, 8_192));
        var joinFixDescriptions = new List<string>(Math.Min(maxJoinRows, 8_192));
        var joinTerritories = new List<string>(Math.Min(maxJoinRows, 8_192));
        var joinSkillsets = new List<string>(Math.Min(maxJoinRows, 8_192));
        var joinStatuses = new List<string>(Math.Min(maxJoinRows, 8_192));

        while (await csv.ReadAsync())
        {
            var rawDate = csv.GetField(appointmentDateCol) ?? "";
            if (!TryExtractDate(rawDate, out var rowDate)) continue;

            total++;
            var territory = csv.GetField(territoryCol) ?? "";
            var status    = csv.GetField(statusCol) ?? "";
            var skillset  = csv.GetField(skillsetCol) ?? "";

            IncrementDist(terrDist, territory);
            var dateKey = rowDate.ToString("yyyy-MM-dd");
            dateDist[dateKey] = dateDist.GetValueOrDefault(dateKey) + 1;

            var skLower = skillset.ToLowerInvariant();
            if (skLower.Contains("repair",  StringComparison.Ordinal)) repair++;
            if (skLower.Contains("install", StringComparison.Ordinal)) install++;

            if (joinDateInts.Count < maxJoinRows)
            {
                joinDateInts.Add((int)EncodeDateInt(rowDate));
                joinDpids.Add(dpidCol is not null ? (csv.GetField(dpidCol) ?? "").Trim() : "");
                joinFixDescriptions.Add(fixDescCol is not null ? (csv.GetField(fixDescCol) ?? "").Trim() : "");
                joinTerritories.Add(territory);
                joinSkillsets.Add(skillset);
                joinStatuses.Add(status);
            }

            if (hasCoords && dots.Count < maxDots)
            {
                var rawLat = csv.GetField(latCol!) ?? "";
                var rawLng = csv.GetField(lngCol!) ?? "";
                if (TryParseCoord(rawLat, out var lat) && TryParseCoord(rawLng, out var lng))
                {
                    dots.Add([lat, lng, EncodeDateInt(rowDate)]);
                    names.Add(facilityCol is not null ? (csv.GetField(facilityCol) ?? "").Trim() : "");
                    dpids.Add(dpidCol is not null ? (csv.GetField(dpidCol) ?? "").Trim() : "");
                    skillsets.Add(skillset);
                    terrs.Add(territory);
                    statuses.Add(status);
                }
            }
        }

        return new KpiDashboardViewModel
        {
            HeatmapNapDots            = dots,
            HeatmapNapDotNames        = names,
            HeatmapNapDotDpids        = dpids,
            HeatmapNapDotSkillsets    = skillsets,
            HeatmapNapDotTerritories  = terrs,
            HeatmapNapDotStatuses     = statuses,
            HeatmapHasCoordinates     = hasCoords,
            HeatmapTotalAppointments  = total,
            HeatmapRepairCount        = repair,
            HeatmapInstallCount       = install,
            HeatmapTerritoryDistribution = new Dictionary<string, int>(terrDist),
            HeatmapAppointmentsByDate    = new Dictionary<string, int>(dateDist),
            HeatmapJoinDateInts          = joinDateInts,
            HeatmapJoinDpids             = joinDpids,
            HeatmapJoinFixDescriptions   = joinFixDescriptions,
            HeatmapJoinTerritories       = joinTerritories,
            HeatmapJoinSkillsets         = joinSkillsets,
            HeatmapJoinStatuses          = joinStatuses
        };
    }

    private sealed class OperationAgingCsvMetadata
    {
        public HashSet<int> OrderCreateYears { get; } = [];
        public DateOnly? OrderCreateMin { get; set; }
        public DateOnly? OrderCreateMax { get; set; }
        public DateOnly? LastUpdateMin { get; set; }
        public DateOnly? LastUpdateMax { get; set; }
    }

    private static List<string> BuildMonthKeyRangeFromLastUpdate(DateOnly? minLu, DateOnly? maxLu)
    {
        var list = new List<string>();
        if (minLu is null || maxLu is null)
            return list;
        var start = new DateOnly(minLu.Value.Year, minLu.Value.Month, 1);
        var end = new DateOnly(maxLu.Value.Year, maxLu.Value.Month, 1);
        if (start > end)
            return list;
        for (var d = start; d <= end; d = d.AddMonths(1))
            list.Add($"{d.Year}-{d.Month:D2}");
        return list;
    }

    private async Task<OperationAgingCsvMetadata> ScanOperationAgingCsvMetadataAsync(
        string csvFilePath,
        CancellationToken cancellationToken)
    {
        var territoryCol = Col("TerritoryColumn");
        var orderCreateCol = Col("OrderCreateDateColumn");
        var lastUpdateCol = Col("LastUpdateDateColumn");
        var meta = new OperationAgingCsvMetadata();

        var yieldEvery = int.TryParse(_config["CsvMapping:FilterExtractionYieldEveryRows"], out var yr)
            ? Math.Clamp(yr, 500, 100_000)
            : 8000;
        var rowCount = 0;

        await using var fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;
            if (rowCount % yieldEvery == 0)
                await Task.Yield();

            var territory = csv.GetField(territoryCol) ?? "";
            if (!IsDavaoNorthTerritory(territory))
                continue;

            var rawOrderCreate = csv.GetField(orderCreateCol) ?? "";
            if (TryParseOrderCreateDate(rawOrderCreate, out var orderCreateDate))
            {
                meta.OrderCreateYears.Add(orderCreateDate.Year);
                meta.OrderCreateMin = meta.OrderCreateMin is not { } oMin || orderCreateDate < oMin ? orderCreateDate : oMin;
                meta.OrderCreateMax = meta.OrderCreateMax is not { } oMax || orderCreateDate > oMax ? orderCreateDate : oMax;
            }

            var rawLastUpdate = csv.GetField(lastUpdateCol) ?? "";
            if (TryParseCsvDateLoose(rawLastUpdate, out var lastUpDate))
            {
                meta.LastUpdateMin = meta.LastUpdateMin is not { } lMin || lastUpDate < lMin ? lastUpDate : lMin;
                meta.LastUpdateMax = meta.LastUpdateMax is not { } lMax || lastUpDate > lMax ? lastUpDate : lMax;
            }
        }

        return meta;
    }

    public async Task<OperationAgingViewModel> ComputeOperationAgingAsync(
        string csvFilePath,
        string reportToken,
        string? selectedMonthParam,
        int? agingYearParam,
        int? agingMonthParam,
        int detailPage = 1,
        int detailPageSize = 20,
        string? detailSort = null,
        int? dailyFocusDay = null,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        var meta = await ScanOperationAgingCsvMetadataAsync(csvFilePath, cancellationToken);

        var safePage = Math.Max(1, detailPage);
        var safePageSize = Math.Clamp(detailPageSize, 10, 20);
        var safeDetailSort = string.Equals(detailSort, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

        var territoryCol = Col("TerritoryColumn");
        var statusCol = Col("StatusColumn");
        var orderCreateCol = Col("OrderCreateDateColumn");
        var lastUpdateCol = Col("LastUpdateDateColumn");
        var appointmentDateCol = Col("AppointmentDateColumn");
        var workOrderConfiguredCol = ColOr("WorkOrderColumn", "workordernumber");
        var skillsetCol = Col("SkillsetColumn");

        var delayedV = _config["CsvMapping:DelayedStatusValue"] ?? "Delayed";
        var pendingV = _config["CsvMapping:PendingStatusValue"] ?? "Pending";
        var ongoingV = _config["CsvMapping:OngoingStatusValue"] ?? "Ongoing";
        var completedV = _config["CsvMapping:CompletedStatusValue"] ?? "Completed";
        var unassignedV = _config["CsvMapping:UnassignedStatusValue"] ?? "Unassigned";
        var cancelledV = _config["CsvMapping:CancelledStatusValue"] ?? "Cancelled";

        var availableMonths = BuildMonthKeyRangeFromLastUpdate(meta.LastUpdateMin, meta.LastUpdateMax);
        if (availableMonths.Count == 0)
            availableMonths.Add($"{today.Year}-{today.Month:D2}");

        DateOnly monthStart;
        if (!string.IsNullOrWhiteSpace(selectedMonthParam)
            && selectedMonthParam.Length >= 7
            && DateOnly.TryParseExact(selectedMonthParam.AsSpan(0, 7), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth))
        {
            monthStart = new DateOnly(parsedMonth.Year, parsedMonth.Month, 1);
        }
        else
        {
            monthStart = new DateOnly(today.Year, today.Month, 1);
        }

        var selectedMonthStr = $"{monthStart.Year:D4}-{monthStart.Month:D2}";
        if (!availableMonths.Contains(selectedMonthStr, StringComparer.Ordinal))
        {
            availableMonths.Add(selectedMonthStr);
            availableMonths.Sort(StringComparer.Ordinal);
        }

        var availableDailyYears = availableMonths
            .Select(m => int.Parse(m.AsSpan(0, 4), CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(y => y)
            .ToList();
        if (availableDailyYears.Count == 0)
            availableDailyYears.Add(today.Year);
        if (!availableDailyYears.Contains(monthStart.Year))
        {
            availableDailyYears.Add(monthStart.Year);
            availableDailyYears.Sort();
        }

        var scopeYear = agingYearParam ?? today.Year;
        if (scopeYear < 1 || scopeYear > 9999)
            scopeYear = today.Year;

        _ = agingMonthParam;

        var availableOrderScopeYears = meta.OrderCreateYears.Count == 0
            ? new List<int> { today.Year }
            : meta.OrderCreateYears.OrderBy(y => y).ToList();
        if (!availableOrderScopeYears.Contains(scopeYear))
        {
            availableOrderScopeYears.Add(scopeYear);
            availableOrderScopeYears.Sort();
        }

        var agingScopeLabel = scopeYear.ToString(CultureInfo.InvariantCulture);

        var monthLabel = monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        var monthEnd = new DateOnly(monthStart.Year, monthStart.Month, DateTime.DaysInMonth(monthStart.Year, monthStart.Month));
        var firstDayCurrentMonth = new DateOnly(today.Year, today.Month, 1);
        var isFutureMonth = monthStart > firstDayCurrentMonth;

        DateOnly lastVisibleDay;
        if (isFutureMonth)
            lastVisibleDay = monthStart;
        else if (monthStart.Year == today.Year && monthStart.Month == today.Month)
            lastVisibleDay = today;
        else
            lastVisibleDay = monthEnd;

        var dailyHeaders = new List<string>();
        var dayCount = 0;
        var dailyFocusDayOptions = new List<int>();
        if (!isFutureMonth)
        {
            for (var d = new DateOnly(monthStart.Year, monthStart.Month, 1); d <= lastVisibleDay; d = d.AddDays(1))
            {
                dailyHeaders.Add(d.Day.ToString(CultureInfo.InvariantCulture));
                dayCount++;
            }

            for (var di = 1; di <= dayCount; di++)
                dailyFocusDayOptions.Add(di);
        }

        var bucketOrder = new[] { "0-2 Days", "2-4 Days", "4-7 Days", "7-15 Days", "16-30 Days", "Above 30 Days" };
        var bucketCounts = bucketOrder.ToDictionary(b => b, _ => 0, StringComparer.Ordinal);
        var bucketIndex = bucketOrder
            .Select((label, idx) => (label, idx))
            .ToDictionary(x => x.label, x => x.idx, StringComparer.Ordinal);

        var matrix = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["repair"] = new int[bucketOrder.Length],
            ["install"] = new int[bucketOrder.Length],
            ["other"] = new int[bucketOrder.Length]
        };
        var repairRemarks = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        var dailyTotal = new int[dayCount];
        var dailyDelayed = new int[dayCount];
        var dailyPending = new int[dayCount];
        var dailyOngoing = new int[dayCount];
        var dailyUnassigned = new int[dayCount];
        var dailyCancelled = new int[dayCount];
        var dailyCompleted = new int[dayCount];

        int totalYear = 0,
            delayed = 0,
            pending = 0,
            ongoing = 0,
            unassigned = 0,
            cancelled = 0,
            completed = 0,
            other = 0;
        var allDetailRows = new List<OperationAgingDetailRow>();
        DateOnly? csvStartDate = null;
        DateOnly? csvEndDate = null;
        var csvYears = new HashSet<int>();

        await using var fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();
        var workOrderCol = FindConfiguredOrPreferredColumn(csv.HeaderRecord, workOrderConfiguredCol,
            "workordernumber", "work order number", "work_order_number", "workorder", "work order");
        var remarkCol = FindRemarksColumn(csv.HeaderRecord, _config["CsvMapping:RemarksColumn"]);

        var yieldEvery = int.TryParse(_config["CsvMapping:FilterExtractionYieldEveryRows"], out var yr2)
            ? Math.Clamp(yr2, 500, 100_000)
            : 8000;
        var rowCount = 0;

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;
            if (rowCount % yieldEvery == 0)
                await Task.Yield();

            var territory = csv.GetField(territoryCol) ?? "";
            if (!IsDavaoNorthTerritory(territory))
                continue;

            var rawOrderCreate = csv.GetField(orderCreateCol) ?? "";
            if (!TryParseOrderCreateDate(rawOrderCreate, out var orderCreateDate))
                continue;

            csvYears.Add(orderCreateDate.Year);
            if (csvStartDate is null || orderCreateDate < csvStartDate.Value)
                csvStartDate = orderCreateDate;
            if (csvEndDate is null || orderCreateDate > csvEndDate.Value)
                csvEndDate = orderCreateDate;

            var status = csv.GetField(statusCol) ?? "";
            var skillset = csv.GetField(skillsetCol) ?? "";
            var rawLastUpdate = csv.GetField(lastUpdateCol) ?? "";
            var rawAppointmentDate = csv.GetField(appointmentDateCol) ?? "";
            var appointmentId = !string.IsNullOrWhiteSpace(workOrderCol)
                ? (csv.GetField(workOrderCol) ?? "")
                : "";
            var isRepairSkillset = string.Equals(ClassifySkillKind(skillset), "repair", StringComparison.OrdinalIgnoreCase);

            if (orderCreateDate.Year == scopeYear)
            {
                totalYear++;
                var slaDays = Math.Max(0, today.DayNumber - orderCreateDate.DayNumber);
                var bucket = ClassifyAgingBucket(slaDays);
                if (bucketCounts.ContainsKey(bucket))
                    bucketCounts[bucket]++;
                if (bucketIndex.TryGetValue(bucket, out var bIdx))
                {
                    var sk = ClassifySkillKind(skillset);
                    if (!matrix.TryGetValue(sk, out var row))
                    {
                        row = new int[bucketOrder.Length];
                        matrix[sk] = row;
                    }
                    row[bIdx]++;
                }

                if (bucketIndex.TryGetValue(bucket, out var rrIdx)
                    && string.Equals(ClassifySkillKind(skillset), "repair", StringComparison.OrdinalIgnoreCase))
                {
                    var rawRemark = remarkCol is not null ? (csv.GetField(remarkCol) ?? "") : "";
                    var remarkLabel = string.IsNullOrWhiteSpace(rawRemark) ? "(blank)" : rawRemark.Trim();
                    if (!repairRemarks.TryGetValue(remarkLabel, out var remarkBuckets))
                    {
                        remarkBuckets = new int[bucketOrder.Length];
                        repairRemarks[remarkLabel] = remarkBuckets;
                    }

                    remarkBuckets[rrIdx]++;
                }

                var cat = ClassifyStatusCategory(status, unassignedV, cancelledV, delayedV, pendingV, ongoingV);
                switch (cat)
                {
                    case AgingStatusCategory.Unassigned: unassigned++; break;
                    case AgingStatusCategory.Cancelled: cancelled++; break;
                    case AgingStatusCategory.Delayed: delayed++; break;
                    case AgingStatusCategory.Pending: pending++; break;
                    case AgingStatusCategory.Ongoing: ongoing++; break;
                    default:
                        other++;
                        if (StatusEquals(status, completedV))
                            completed++;
                        break;
                }

                allDetailRows.Add(new OperationAgingDetailRow
                {
                    WorkOrder = appointmentId.Trim(),
                    OrderCreateDateRaw = rawOrderCreate.Trim(),
                    AgeDays = slaDays,
                    AgingBucket = bucket,
                    Status = status,
                    Skillset = skillset,
                    Territory = territory,
                    SkillKind = ClassifySkillKind(skillset)
                });
            }

            var rawDailyDate = string.IsNullOrWhiteSpace(rawAppointmentDate) ? rawLastUpdate : rawAppointmentDate;
            if (isRepairSkillset && dayCount > 0 && TryParseCsvDateLoose(rawDailyDate, out var lastUpDate))
            {
                if (lastUpDate.Year == monthStart.Year && lastUpDate.Month == monthStart.Month
                    && lastUpDate <= lastVisibleDay && lastUpDate >= new DateOnly(monthStart.Year, monthStart.Month, 1))
                {
                    var dayIndex = lastUpDate.Day - 1;
                    if (dayIndex >= 0 && dayIndex < dayCount)
                    {
                        dailyTotal[dayIndex]++;
                        var dcat = ClassifyStatusCategory(status, unassignedV, cancelledV, delayedV, pendingV, ongoingV);
                        switch (dcat)
                        {
                            case AgingStatusCategory.Unassigned: dailyUnassigned[dayIndex]++; break;
                            case AgingStatusCategory.Cancelled: dailyCancelled[dayIndex]++; break;
                            case AgingStatusCategory.Delayed: dailyDelayed[dayIndex]++; break;
                            case AgingStatusCategory.Pending: dailyPending[dayIndex]++; break;
                            case AgingStatusCategory.Ongoing: dailyOngoing[dayIndex]++; break;
                            default:
                                if (StatusEquals(status, completedV))
                                    dailyCompleted[dayIndex]++;
                                break;
                        }
                    }
                }
            }
        }

        if (safeDetailSort == "asc")
        {
            allDetailRows.Sort(static (a, b) =>
                a.AgeDays != b.AgeDays
                    ? a.AgeDays.CompareTo(b.AgeDays)
                    : string.Compare(a.WorkOrder, b.WorkOrder, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            allDetailRows.Sort(static (a, b) =>
                b.AgeDays != a.AgeDays
                    ? b.AgeDays.CompareTo(a.AgeDays)
                    : string.Compare(a.WorkOrder, b.WorkOrder, StringComparison.OrdinalIgnoreCase));
        }

        var detailTotal = allDetailRows.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(detailTotal / (double)safePageSize));
        if (safePage > totalPages) safePage = totalPages;
        var skip = (safePage - 1) * safePageSize;
        var pageRows = allDetailRows.Skip(skip).Take(safePageSize).ToList();

        var bucketList = bucketOrder.Select(b => new AgingBucketCount { Label = b, Count = bucketCounts[b] }).ToList();

        var donutLabels = bucketOrder.ToList();
        var donutValues = bucketOrder.Select(b => bucketCounts[b]).ToList();

        var barLabels = new List<string>
        {
            "Delayed", "Pending", "Ongoing", "Unassigned", "Cancelled", "Other"
        };
        var barValues = new List<int> { delayed, pending, ongoing, unassigned, cancelled, other };
        var nonMappedStatusLabel = "Completed";

        static int Sum(int[] xs)
        {
            var s = 0;
            foreach (var v in xs) s += v;
            return s;
        }

        var dailyRows = new List<DailyStatusReportRow>
        {
            NewDailyRow("delayed", "Delayed", "delayed", dailyDelayed),
            NewDailyRow("pending", "Pending", "pending", dailyPending),
            NewDailyRow("ongoing", "Ongoing", "ongoing", dailyOngoing),
            NewDailyRow("unassigned", "Unassigned", "unassigned", dailyUnassigned),
            NewDailyRow("cancelled", "Cancelled", "cancelled", dailyCancelled),
            NewDailyRow("completed", "Completed", "completed", dailyCompleted)
        };

        int? selectedDailyFocusDay = null;
        if (dailyFocusDay is { } focusDay && dayCount > 0 && focusDay >= 1 && focusDay <= dayCount)
        {
            selectedDailyFocusDay = focusDay;
            var colIdx = focusDay - 1;
            dailyHeaders = new List<string> { dailyHeaders[colIdx] };
            foreach (var row in dailyRows)
            {
                var v = row.DayValues[colIdx];
                row.DayValues = new List<int> { v };
                row.RowTotal = v;
            }
        }

        DailyStatusReportRow NewDailyRow(string key, string label, string colorKey, int[] days)
        {
            var list = days.Select(d => d).ToList();
            return new DailyStatusReportRow
            {
                MetricKey = key,
                MetricLabel = label,
                ColorKey = colorKey,
                DayValues = list,
                RowTotal = Sum(days)
            };
        }

        var matrixRows = new List<AgingBucketMatrixRow>
        {
            new() { RowKey = "repair", RowLabel = "Repair",  BucketCounts = matrix["repair"].ToList(),  Total = Sum(matrix["repair"]) },
            new() { RowKey = "install", RowLabel = "Install", BucketCounts = matrix["install"].ToList(), Total = Sum(matrix["install"]) },
            new() { RowKey = "other", RowLabel = "Other",   BucketCounts = matrix["other"].ToList(),   Total = Sum(matrix["other"]) }
        };

        var totalBuckets = new int[bucketOrder.Length];
        for (var i = 0; i < bucketOrder.Length; i++)
            totalBuckets[i] = matrix["repair"][i] + matrix["install"][i] + matrix["other"][i];
        matrixRows.Add(new AgingBucketMatrixRow
        {
            RowKey = "total",
            RowLabel = "Total",
            BucketCounts = totalBuckets.ToList(),
            Total = Sum(totalBuckets)
        });

        var repairRemarkRows = repairRemarks
            .Select(kv => new RepairRemarkBucketRow
            {
                RemarkLabel = kv.Key,
                BucketCounts = kv.Value.ToList(),
                RepairTotal = Sum(kv.Value),
                GrandTotal = Sum(kv.Value)
            })
            .OrderByDescending(r => r.RepairTotal)
            .ThenBy(r => r.RemarkLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var repairRemarkBucketTotals = new int[bucketOrder.Length];
        for (var i = 0; i < bucketOrder.Length; i++)
            repairRemarkBucketTotals[i] = repairRemarkRows.Sum(r => r.BucketCounts[i]);
        var repairRemarkGrandTotal = repairRemarkBucketTotals.Sum();

        var territoryBucketLabels = new List<string>
        {
            "0 Day",
            "1 Day",
            "2 Days",
            "3 Days",
            "4-7 Days",
            "8-15 Days",
            "16-30 Days",
            "Above 30 Days"
        };
        var territoryBucketCounts = new int[territoryBucketLabels.Count];
        foreach (var detail in allDetailRows)
        {
            var idx = ClassifyTerritoryBucketIndex(detail.AgeDays);
            territoryBucketCounts[idx]++;
        }
        var territoryBucketRows = new List<TerritoryAgingBucketRow>
        {
            new()
            {
                TerritoryLabel = "Davao North",
                BucketCounts = territoryBucketCounts.ToList(),
                Total = territoryBucketCounts.Sum()
            }
        };
        var territoryBucketGrandTotals = territoryBucketCounts.ToList();
        var territoryBucketGrandTotal = territoryBucketGrandTotals.Sum();

        var csvYearSummary = csvYears.Count == 0
            ? "N/A"
            : string.Join(", ", csvYears.OrderBy(y => y).Select(y => y.ToString(CultureInfo.InvariantCulture)));
        var csvStartMonthYear = csvStartDate?.ToString("MMM yyyy", CultureInfo.InvariantCulture) ?? "N/A";
        var csvEndMonthYear = csvEndDate?.ToString("MMM yyyy", CultureInfo.InvariantCulture) ?? "N/A";

        return new OperationAgingViewModel
        {
            ReportToken = reportToken,
            DetailPage = safePage,
            DetailPageSize = safePageSize,
            DetailTotalPages = totalPages,
            DetailSort = safeDetailSort,
            BucketLabels = bucketOrder.ToList(),
            BucketMatrixRows = matrixRows,
            RepairRemarkRows = repairRemarkRows,
            RepairRemarkGrandBucketTotals = repairRemarkBucketTotals.ToList(),
            RepairRemarkGrandTotal = repairRemarkGrandTotal,
            SelectedMonth = selectedMonthStr,
            SelectedMonthLabel = monthLabel,
            AvailableMonths = availableMonths,
            DailyHeaderLabels = dailyHeaders,
            DailyFocusDayOptions = dailyFocusDayOptions,
            SelectedDailyFocusDay = selectedDailyFocusDay,
            DailyStatusRows = dailyRows,
            ReadingYearScope = scopeYear,
            SelectedDailyYear = monthStart.Year,
            SelectedDailyMonth = monthStart.Month,
            AvailableDailyYears = availableDailyYears,
            SelectedAgingYear = scopeYear,
            SelectedAgingMonth = null,
            AvailableOrderScopeYears = availableOrderScopeYears,
            AgingScopeLabel = agingScopeLabel,
            CsvYearSummary = csvYearSummary,
            CsvStartMonthYear = csvStartMonthYear,
            CsvEndMonthYear = csvEndMonthYear,
            TotalOrdersYearScope = totalYear,
            DelayedCount = delayed,
            PendingCount = pending,
            OngoingCount = ongoing,
            UnassignedCount = unassigned,
            CancelledCount = cancelled,
            CompletedCount = completed,
            OtherStatusCount = other,
            NonMappedStatusLabel = nonMappedStatusLabel,
            BucketCounts = bucketList,
            DetailRows = pageRows,
            DetailRowTotal = detailTotal,
            DonutLabels = donutLabels,
            DonutValues = donutValues,
            BarLabels = barLabels,
            BarValues = barValues,
            TerritoryBucketLabels = territoryBucketLabels,
            TerritoryBucketRows = territoryBucketRows,
            TerritoryBucketGrandTotals = territoryBucketGrandTotals,
            TerritoryBucketGrandTotal = territoryBucketGrandTotal
        };
    }

    private static string ClassifySkillKind(string skillset)
    {
        var s = skillset.ToLowerInvariant();
        if (s.Contains("repair", StringComparison.Ordinal)) return "repair";
        if (s.Contains("install", StringComparison.Ordinal)) return "install";
        return "other";
    }

    private enum AgingStatusCategory
    {
        Unassigned,
        Cancelled,
        Delayed,
        Pending,
        Ongoing,
        Other
    }

    /// <summary>
    /// Maps Delayed / Pending / Ongoing / Unassigned / Cancelled from CSV; completed and any other string → <see cref="AgingStatusCategory.Other"/>.
    /// </summary>
    private static AgingStatusCategory ClassifyStatusCategory(
        string status,
        string unassignedV,
        string cancelledV,
        string delayedV,
        string pendingV,
        string ongoingV)
    {
        if (StatusEquals(status, unassignedV))
            return AgingStatusCategory.Unassigned;
        if (StatusEquals(status, cancelledV))
            return AgingStatusCategory.Cancelled;
        if (StatusEquals(status, delayedV))
            return AgingStatusCategory.Delayed;
        if (StatusEquals(status, pendingV))
            return AgingStatusCategory.Pending;
        if (StatusEquals(status, ongoingV))
            return AgingStatusCategory.Ongoing;
        return AgingStatusCategory.Other;
    }

    private static bool StatusEquals(string left, string right) =>
        string.Equals(NormalizeStatusToken(left), NormalizeStatusToken(right), StringComparison.Ordinal);

    private static string NormalizeStatusToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Normalize status labels from CSV/config: trim + collapse spaces + lowercase.
        var compact = Regex.Replace(value.Trim(), @"\s+", " ");
        return compact.ToLowerInvariant();
    }

    private static string ClassifyAgingBucket(int ageDays)
    {
        var a = Math.Max(0, ageDays);
        if (a < 2) return "0-2 Days";
        if (a < 4) return "2-4 Days";
        if (a < 7) return "4-7 Days";
        if (a < 15) return "7-15 Days";
        if (a < 30) return "16-30 Days";
        return "Above 30 Days";
    }

    private static int ClassifyTerritoryBucketIndex(int ageDays)
    {
        var a = Math.Max(0, ageDays);
        if (a == 0) return 0;
        if (a == 1) return 1;
        if (a == 2) return 2;
        if (a == 3) return 3;
        if (a <= 7) return 4;
        if (a <= 15) return 5;
        if (a <= 30) return 6;
        return 7;
    }

    private static bool IsDavaoNorthTerritory(string? territory)
    {
        if (string.IsNullOrWhiteSpace(territory))
            return false;
        return string.Equals(
            NormalizeValue(territory),
            NormalizeValue("Davao North"),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseOrderCreateDate(string raw, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var datePart = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;
        return DateOnly.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool TryParseCsvDateLoose(string raw, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (TryExtractDate(raw, out result))
            return true;
        if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = DateOnly.FromDateTime(dt);
            return true;
        }
        return false;
    }

    public async Task<CleanedDataSummary> CleanAndAppendRawDataAsync(Stream rawStream, CancellationToken cancellationToken = default)
    {
        var requiredHeaders = new[]
        {
            "source", "workordernumber", "workordertype", "appointmentid", "appointmentdate",
            "customername", "customeraddress", "customertype", "customersubtype", "serviceidnumber",
            "accountnumber", "skillset", "status", "substatus", "fix description", "mainplan",
            "territory", "facilityname", "longitude", "latitude", "reasoncode", "cabinetid",
            "cabinettype", "cabinetaddress", "cabinetport", "lcpname", "dpid", "ppoeusername",
            "userid", "ordercreatedate", "createdate", "lastupdatedate", "completiondate", "protocol", "team"
        };

        var cleanedDataDir = Path.Combine(_config.GetValue<string>("ReportSessions:ReportsDirectory") ?? "App_Data/reports");
        Directory.CreateDirectory(cleanedDataDir);
        var cleanedDataPath = Path.Combine(cleanedDataDir, "CleanedDataMaster.csv");

        var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalCleanedRowsNow = 0;

        if (File.Exists(cleanedDataPath))
        {
            using var fs = new FileStream(cleanedDataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            using var existingCsv = new CsvReader(sr, DefaultCsvConfig);
            
            await existingCsv.ReadAsync();
            existingCsv.ReadHeader();
            while (await existingCsv.ReadAsync())
            {
                var id = existingCsv.GetField("appointmentid");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    existingIds.Add(id);
                }
                totalCleanedRowsNow++;
            }
        }

        using var rawReader = new StreamReader(rawStream, Encoding.UTF8, true, 1024 * 1024);
        using var csv = new CsvReader(rawReader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        var headerIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actualHeaders = csv.HeaderRecord;
        if (actualHeaders != null)
        {
            for (int i = 0; i < actualHeaders.Length; i++)
            {
                var normalizedHeader = actualHeaders[i].Replace(" ", "").Replace("_", "").ToLowerInvariant();
                foreach (var req in requiredHeaders)
                {
                    var normReq = req.Replace(" ", "").Replace("_", "").ToLowerInvariant();
                    if (normalizedHeader == normReq)
                    {
                        headerIndices[req] = i;
                        break;
                    }
                }
            }
        }

        var territoryIdx = headerIndices.GetValueOrDefault("territory", -1);
        var appointmentIdIdx = headerIndices.GetValueOrDefault("appointmentid", -1);

        int totalProcessed = 0;
        int newAdded = 0;
        int duplicates = 0;

        var fileExists = File.Exists(cleanedDataPath);
        using var outStream = new FileStream(cleanedDataPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(outStream, Encoding.UTF8);
        using var outCsv = new CsvWriter(writer, DefaultCsvConfig);

        if (!fileExists)
        {
            foreach (var h in requiredHeaders)
            {
                outCsv.WriteField(h);
            }
            await outCsv.NextRecordAsync();
        }

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalProcessed++;

            if (territoryIdx == -1 || appointmentIdIdx == -1) continue;

            var territory = csv.GetField(territoryIdx) ?? "";
            if (territory.IndexOf("DAVAO NORTH", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var id = csv.GetField(appointmentIdIdx) ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;

            if (existingIds.Contains(id))
            {
                duplicates++;
                continue;
            }

            existingIds.Add(id);
            newAdded++;
            totalCleanedRowsNow++;

            foreach (var h in requiredHeaders)
            {
                if (headerIndices.TryGetValue(h, out int idx))
                {
                    outCsv.WriteField(csv.GetField(idx));
                }
                else
                {
                    outCsv.WriteField("");
                }
            }
            await outCsv.NextRecordAsync();
        }

        return new CleanedDataSummary
        {
            TotalRowsProcessed = totalProcessed,
            NewRowsAdded = newAdded,
            DuplicateRowsSkipped = duplicates,
            TotalCleanedRowsNow = totalCleanedRowsNow
        };
    }

    /// <summary>
    /// Analyses ticket history grouped by service ID.
    /// For each service ID with 2+ tickets, finds the earliest Install/Repair
    /// and each subsequent Repair ticket, producing a RecurringTicketRow.
    /// </summary>
    private static List<RecurringTicketRow> BuildRecurringTickets(
        Dictionary<string, List<(DateOnly Date, string Skillset, string Status, string AppointmentId, string WorkOrder, string CustomerName, string CustomerAddress, string Territory, string FacilityName, string DpId, string CabinetId, string Team)>> serviceTickets)
    {
        var result = new List<RecurringTicketRow>();

        foreach (var (svcId, tickets) in serviceTickets)
        {
            if (tickets.Count < 2) continue;

            var sorted = tickets.OrderBy(t => t.Date).ToList();
            var initial = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                var later = sorted[i];
                // Only flag if the later ticket is a Repair
                if (later.Skillset.IndexOf("Repair", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Must be a different day
                var gap = later.Date.DayNumber - initial.Date.DayNumber;
                if (gap <= 0) continue;

                result.Add(new RecurringTicketRow
                {
                    ServiceIdNumber       = svcId,
                    CustomerName          = initial.CustomerName,
                    CustomerAddress       = initial.CustomerAddress,
                    Territory             = initial.Territory,
                    FacilityName          = initial.FacilityName,
                    DpId                  = initial.DpId,
                    CabinetId             = initial.CabinetId,
                    Team                  = initial.Team,
                    InitialTicketDate     = initial.Date.ToString("yyyy-MM-dd"),
                    InitialSkillset       = initial.Skillset,
                    InitialStatus         = initial.Status,
                    InitialAppointmentId  = initial.AppointmentId,
                    InitialWorkOrderNumber = initial.WorkOrder,
                    RecurringTicketDate   = later.Date.ToString("yyyy-MM-dd"),
                    RecurringSkillset     = later.Skillset,
                    RecurringStatus       = later.Status,
                    RecurringAppointmentId = later.AppointmentId,
                    RecurringWorkOrderNumber = later.WorkOrder,
                    DaysBetween           = gap
                });
            }
        }

        return result.OrderByDescending(r => r.DaysBetween).ToList();
    }

    public async Task<(List<RecurringTicketRow> Items, int TotalCount, RecurringTicketsSummary Summary)> GetPaginatedRecurringTicketsAsync(
        string csvFilePath,
        string filterMode = "all",
        DateOnly? selectedDate = null,
        DateOnly? dateRangeStart = null,
        DateOnly? dateRangeEnd = null,
        int page = 1,
        int pageSize = 20,
        int? minGap = null,
        int? maxGap = null,
        CancellationToken cancellationToken = default)
    {
        var allFiltered = await GetFilteredRecurringTicketsAsync(csvFilePath, filterMode, selectedDate, dateRangeStart, dateRangeEnd, minGap, maxGap, cancellationToken);
        var paged = allFiltered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var summary = new RecurringTicketsSummary
        {
            TotalRecurringTickets = allFiltered.Count
        };

        if (allFiltered.Count > 0)
        {
            summary.TopNaps = allFiltered
                .Where(r => !string.IsNullOrWhiteSpace(r.FacilityName))
                .GroupBy(r => r.FacilityName)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new TopRankItem { Name = g.Key, Count = g.Count() })
                .ToList();

            summary.TopCabinets = allFiltered
                .Where(r => !string.IsNullOrWhiteSpace(r.CabinetId))
                .GroupBy(r => r.CabinetId)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new TopRankItem { Name = g.Key, Count = g.Count() })
                .ToList();

            summary.TopTechTeams = allFiltered
                .Where(r => !string.IsNullOrWhiteSpace(r.Team))
                .GroupBy(r => r.Team)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new TopRankItem { Name = g.Key, Count = g.Count() })
                .ToList();
        }

        return (paged, allFiltered.Count, summary);
    }

    public async Task<List<RecurringTicketRow>> GetFilteredRecurringTicketsAsync(
        string csvFilePath,
        string filterMode = "all",
        DateOnly? selectedDate = null,
        DateOnly? dateRangeStart = null,
        DateOnly? dateRangeEnd = null,
        int? minGap = null,
        int? maxGap = null,
        CancellationToken cancellationToken = default)
    {
        var all = await BuildAllRecurringTicketsInternalAsync(csvFilePath, cancellationToken);
        return all.Where(r => {
            if (!DateOnly.TryParse(r.RecurringTicketDate, out var d)) return false;
            
            bool dateMatches = MatchesDateFilter(d, filterMode, selectedDate, dateRangeStart, dateRangeEnd);
            if (!dateMatches) return false;

            if (minGap.HasValue && r.DaysBetween < minGap.Value) return false;
            if (maxGap.HasValue && r.DaysBetween > maxGap.Value) return false;

            return true;
        }).ToList();
    }

    private async Task<List<RecurringTicketRow>> BuildAllRecurringTicketsInternalAsync(string csvFilePath, CancellationToken cancellationToken)
    {
        using var fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, DefaultCsvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();

        var headers = csv.HeaderRecord;
        var appointmentDateCol = Col("AppointmentDateColumn");
        var skillsetCol = Col("SkillsetColumn");
        var statusCol = Col("StatusColumn");
        var appointmentIdCol = Col("AppointmentIdColumn");
        var workOrderCol = ColOr("WorkOrderColumn", appointmentIdCol);
        
        var serviceIdCol = FindCoordColumn(headers, null, "serviceidnumber", "service_id_number", "serviceid");
        var customerNameCol = FindCoordColumn(headers, null, "customername", "customer_name");
        var customerAddrCol = FindCoordColumn(headers, null, "customeraddress", "customer_address");
        var territoryCol = Col("TerritoryColumn");
        var facilityCol = FindCoordColumn(headers, _config["CsvMapping:FacilityNameColumn"], "facilityname", "facility_name", "facility", "name");
        var dpidCol = FindCoordColumn(headers, null, "dpid");
        var cabinetCol = FindCoordColumn(headers, null, "cabinetid", "cabinet_id", "cabinet");
        var teamCol = FindCoordColumn(headers, null, "team", "team_name", "tech_team");

        var serviceTickets = new Dictionary<string, List<(DateOnly Date, string Skillset, string Status, string AppointmentId, string WorkOrder, string CustomerName, string CustomerAddress, string Territory, string FacilityName, string DpId, string CabinetId, string Team)>>(StringComparer.OrdinalIgnoreCase);

        while (await csv.ReadAsync())
        {
            var rawDate = csv.GetField(appointmentDateCol) ?? "";
            if (!TryExtractDate(rawDate, out var rowDate)) continue;

            var skillset = csv.GetField(skillsetCol) ?? "";
            var status = (csv.GetField(statusCol) ?? "").Trim();
            var appointmentId = csv.GetField(appointmentIdCol) ?? "";
            var workOrder = csv.GetField(workOrderCol) ?? appointmentId;
            var territory = csv.GetField(territoryCol) ?? "";

            if (serviceIdCol is not null)
            {
                var svcId = (csv.GetField(serviceIdCol) ?? "").Trim();
                if (!string.IsNullOrEmpty(svcId) && (skillset.IndexOf("Install", StringComparison.OrdinalIgnoreCase) >= 0 || skillset.IndexOf("Repair", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (!serviceTickets.TryGetValue(svcId, out var list))
                    {
                        list = new();
                        serviceTickets[svcId] = list;
                    }
                    list.Add((
                        Date: rowDate,
                        Skillset: skillset,
                        Status: status,
                        AppointmentId: appointmentId,
                        WorkOrder: workOrder,
                        CustomerName: customerNameCol is not null ? (csv.GetField(customerNameCol) ?? "").Trim() : "",
                        CustomerAddress: customerAddrCol is not null ? (csv.GetField(customerAddrCol) ?? "").Trim() : "",
                        Territory: territory,
                        FacilityName: facilityCol is not null ? (csv.GetField(facilityCol) ?? "").Trim() : "",
                        DpId: dpidCol is not null ? (csv.GetField(dpidCol) ?? "").Trim() : "",
                        CabinetId: cabinetCol is not null ? (csv.GetField(cabinetCol) ?? "").Trim() : "",
                        Team: teamCol is not null ? (csv.GetField(teamCol) ?? "").Trim() : ""
                    ));
                }
            }
        }

        return BuildRecurringTickets(serviceTickets);
    }

    public async Task<MemoryStream> GenerateRecurringTicketsCsvAsync(List<RecurringTicketRow> rows)
    {
        var ms = new MemoryStream();
        await using (var sw = new StreamWriter(ms, Encoding.UTF8, 1024, leaveOpen: true))
        await using (var csv = new CsvWriter(sw, CultureInfo.InvariantCulture))
        {
            csv.WriteField("Service ID");
            csv.WriteField("Customer Name");
            csv.WriteField("Address");
            csv.WriteField("Territory");
            csv.WriteField("Facility");
            csv.WriteField("Initial Date");
            csv.WriteField("Initial WO#");
            csv.WriteField("Initial Skillset");
            csv.WriteField("Recurring Date");
            csv.WriteField("Recurring WO#");
            csv.WriteField("Recurring Skillset");
            csv.WriteField("Gap (Days)");
            await csv.NextRecordAsync();

            foreach (var r in rows)
            {
                csv.WriteField(r.ServiceIdNumber);
                csv.WriteField(r.CustomerName);
                csv.WriteField(r.CustomerAddress);
                csv.WriteField(r.Territory);
                csv.WriteField(r.FacilityName);
                csv.WriteField(r.InitialTicketDate);
                csv.WriteField(r.InitialWorkOrderNumber);
                csv.WriteField(r.InitialSkillset);
                csv.WriteField(r.RecurringTicketDate);
                csv.WriteField(r.RecurringWorkOrderNumber);
                csv.WriteField(r.RecurringSkillset);
                csv.WriteField(r.DaysBetween);
                await csv.NextRecordAsync();
            }
        }
        ms.Position = 0;
        return ms;
    }

    public async Task<MemoryStream> GenerateRecurringTicketsXlsxAsync(List<RecurringTicketRow> rows)
    {
        var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Recurring Tickets");

        var headers = new[] { "Service ID", "Customer Name", "Address", "Territory", "Facility", "Initial Date", "Initial WO#", "Initial Skillset", "Recurring Date", "Recurring WO#", "Recurring Skillset", "Gap (Days)" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#16162a");
        headerRange.Style.Font.FontColor = XLColor.White;

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            ws.Cell(i + 2, 1).Value = r.ServiceIdNumber;
            ws.Cell(i + 2, 2).Value = r.CustomerName;
            ws.Cell(i + 2, 3).Value = r.CustomerAddress;
            ws.Cell(i + 2, 4).Value = r.Territory;
            ws.Cell(i + 2, 5).Value = r.FacilityName;
            ws.Cell(i + 2, 6).Value = r.InitialTicketDate;
            ws.Cell(i + 2, 7).Value = r.InitialWorkOrderNumber;
            ws.Cell(i + 2, 8).Value = r.InitialSkillset;
            ws.Cell(i + 2, 9).Value = r.RecurringTicketDate;
            ws.Cell(i + 2, 10).Value = r.RecurringWorkOrderNumber;
            ws.Cell(i + 2, 11).Value = r.RecurringSkillset;
            ws.Cell(i + 2, 12).Value = r.DaysBetween;
        }

        ws.Columns().AdjustToContents();
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private sealed class FilteredRow
    {
        public string AppointmentId { get; init; } = "";
        public string AppointmentDate { get; init; } = "";
        public string Skillset { get; init; } = "";
        public string Status { get; init; } = "";
        public string SubStatus { get; init; } = "";
        public string Territory { get; init; } = "";
        public string OrderCreateDate { get; init; } = "";
        public string LastUpdateDate { get; init; } = "";
        public bool IsDelayed { get; init; }
        public bool IsLapsed { get; init; }
        public DateOnly SortDate { get; init; }
    }
}
