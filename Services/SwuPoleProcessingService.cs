using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public sealed class SwuPoleProcessingService : ISwuPoleProcessingService
{
    private static readonly Regex SwuCodeRegex = new(@"SWU\s*P\s*0?\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LatLongRegex = new(@"^\s*([+-]?\d+\.?\d*)\s*,\s*([+-]?\d+\.?\d*)\s*$", RegexOptions.Compiled);
    private static readonly Regex NumericItemRegex = new(@"^\d+$", RegexOptions.Compiled);

    private readonly string _reportsDirectory;

    public SwuPoleProcessingService(IConfiguration configuration)
    {
        _reportsDirectory = configuration.GetValue<string>("ReportSessions:ReportsDirectory") ?? "App_Data/reports";
        Directory.CreateDirectory(_reportsDirectory);
    }

    public string GetBatchFilePath(string batchId) =>
        Path.Combine(_reportsDirectory, $"SwuReorganizedBatch_{SanitizeBatchId(batchId)}.xlsx");

    public void ClearBatch(string batchId)
    {
        var path = GetBatchFilePath(batchId);
        if (File.Exists(path))
            File.Delete(path);
    }

    public bool BatchFileExists(string batchId) => File.Exists(GetBatchFilePath(batchId));

    public async Task<SwuReorganizedSummary> ReorganizeAndAppendToBatchAsync(
        Stream xlsxStream,
        string originalFileName,
        string batchId,
        bool isFirstInBatch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rows = ReadDenseGrid(xlsxStream);
        var parsed = ParseSwuSheet(rows);
        var titleRow = ExtractTitleRow(rows) ?? BuildTitleFromMeta(parsed.Meta);

        var batchPath = GetBatchFilePath(batchId);
        if (isFirstInBatch)
            ClearBatch(batchId);

        await Task.Run(() => AppendTableBlock(batchPath, isFirstInBatch, titleRow, parsed.Points), cancellationToken);

        return new SwuReorganizedSummary
        {
            FilesProcessed = 1,
            TotalPolesExtracted = parsed.Points.Count,
            PolesFromLastFile = parsed.Points.Count,
            SourceFileNames = [originalFileName],
            BatchId = batchId
        };
    }

    private static string SanitizeBatchId(string batchId)
    {
        var sanitized = string.Concat(batchId.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        return string.IsNullOrEmpty(sanitized) ? "default" : sanitized;
    }

    private static string[][] ReadDenseGrid(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidOperationException("The workbook contains no worksheets.");

        var usedRange = ws.RangeUsed();
        if (usedRange == null)
            return [];

        var firstRow = usedRange.FirstRow().RowNumber();
        var lastRow = usedRange.LastRow().RowNumber();
        var firstCol = usedRange.FirstColumn().ColumnNumber();
        var lastCol = usedRange.LastColumn().ColumnNumber();

        var grid = new string[lastRow - firstRow + 1][];
        for (var r = firstRow; r <= lastRow; r++)
        {
            var rowIndex = r - firstRow;
            grid[rowIndex] = new string[lastCol - firstCol + 1];
            for (var c = firstCol; c <= lastCol; c++)
            {
                var cell = ws.Cell(r, c);
                var text = cell.GetFormattedString().Trim();
                if (string.IsNullOrEmpty(text) && cell.TryGetValue(out double num))
                    text = num.ToString(CultureInfo.InvariantCulture);
                grid[rowIndex][c - firstCol] = text;
            }
        }

        return grid;
    }

    private static string? ExtractTitleRow(string[][] rows)
    {
        if (rows.Length == 0) return null;
        var first = string.Join(" ", rows[0].Where(c => !string.IsNullOrWhiteSpace(c))).Trim();
        if (string.IsNullOrEmpty(first)) return null;
        return SwuCodeRegex.IsMatch(first) ? first : null;
    }

    private static string BuildTitleFromMeta(SwuSheetMeta meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.SwuCode) && !string.IsNullOrWhiteSpace(meta.Location))
            return $"{meta.SwuCode} {meta.Location}";
        if (!string.IsNullOrWhiteSpace(meta.SwuCode))
            return meta.SwuCode;
        return "SWU Pole Data";
    }

    private static void AppendTableBlock(string batchPath, bool isFirstInBatch, string titleRow, List<SwuPolePoint> points)
    {
        using var workbook = isFirstInBatch || !File.Exists(batchPath)
            ? new XLWorkbook()
            : new XLWorkbook(batchPath);

        IXLWorksheet ws;
        int nextRow;
        if (isFirstInBatch || workbook.Worksheets.Count == 0)
        {
            ws = workbook.Worksheets.Add("Reorganized");
            nextRow = 1;

            // Write header row once
            ws.Cell(nextRow, 1).Value = "FILE TITLE";
            ws.Cell(nextRow, 2).Value = "ITEM";
            ws.Cell(nextRow, 3).Value = "POLE NO.";
            ws.Cell(nextRow, 4).Value = "LATLONG";
            ws.Cell(nextRow, 5).Value = "LOCATION";
            nextRow++;
        }
        else
        {
            ws = workbook.Worksheets.First();
            nextRow = ws.LastRowUsed()?.RowNumber() + 1 ?? 2;
        }

        foreach (var p in points)
        {
            ws.Cell(nextRow, 1).Value = titleRow;
            ws.Cell(nextRow, 2).Value = p.Item;
            ws.Cell(nextRow, 3).Value = p.Pn;
            ws.Cell(nextRow, 4).Value = $"{p.Lat.ToString(CultureInfo.InvariantCulture)}, {p.Lng.ToString(CultureInfo.InvariantCulture)}";
            ws.Cell(nextRow, 5).Value = p.Location;
            nextRow++;
        }

        ws.Columns().AdjustToContents();
        workbook.SaveAs(batchPath);
    }

    private static SwuParsedSheet ParseSwuSheet(string[][] rows)
    {
        var header = FindHeaderRow(rows)
            ?? throw new InvalidOperationException("Could not find ITEM and coordinate columns (LATLONG or LAT/LNG) in the spreadsheet.");

        var meta = ParseSheetMeta(rows, header.Index);
        var points = new List<SwuPolePoint>();
        var currentOuBlock = "";
        var currentLocation = "";
        string? pendingItem = null;

        for (var r = header.Index + 1; r < rows.Length; r++)
        {
            var row = rows[r];
            if (row == null || row.Length == 0) continue;

            var itemRaw = GetCell(row, header.ItemIdx);
            var pnRaw = header.PnIdx >= 0 ? GetCell(row, header.PnIdx) : "";
            var latRaw = header.LatLongIdx >= 0 ? GetCell(row, header.LatLongIdx) : "";
            var ouRaw = header.OuRemarksIdx >= 0 ? GetCell(row, header.OuRemarksIdx) : "";
            var locRaw = header.LocationIdx >= 0 ? GetCell(row, header.LocationIdx) : "";

            var locParts = string.IsNullOrEmpty(locRaw) ? [] : SplitLines(locRaw);
            if (locParts.Length > 0)
            {
                foreach (var part in locParts)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        currentLocation = part;
                        break;
                    }
                }
            }

            if (NormCol(itemRaw) == "ITEM") continue;

            if (!string.IsNullOrWhiteSpace(ouRaw))
            {
                currentOuBlock = ouRaw.StartsWith('*') ? ouRaw : (string.IsNullOrEmpty(currentOuBlock) ? ouRaw : currentOuBlock + " " + ouRaw);
            }

            var rowCoords = CoordsFromRow(row, header);
            var hasLatLong = !string.IsNullOrWhiteSpace(latRaw);
            var hasPole = !string.IsNullOrWhiteSpace(pnRaw);

            if (!rowCoords.HasValue && !hasLatLong)
            {
                if (!string.IsNullOrWhiteSpace(itemRaw) && !IsNumericItem(itemRaw) && !hasPole)
                {
                    currentLocation = itemRaw.Trim();
                    pendingItem = null;
                    continue;
                }

                if (IsNumericItem(itemRaw) && !hasPole)
                {
                    pendingItem = itemRaw;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(ouRaw) && points.Count > 0)
                {
                    var prev = points[^1];
                    prev.OuRemarks = string.IsNullOrEmpty(prev.OuRemarks) ? ouRaw : prev.OuRemarks + " " + ouRaw;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(itemRaw) && pendingItem != null)
                itemRaw = pendingItem;
            else if (!string.IsNullOrWhiteSpace(itemRaw) && IsNumericItem(itemRaw))
                pendingItem = null;

            var items = SplitLines(itemRaw);
            var pns = SplitLines(pnRaw);
            var lls = SplitLines(latRaw);
            var count = Math.Max(Math.Max(items.Length, pns.Length), Math.Max(lls.Length, 1));

            for (var j = 0; j < count; j++)
            {
                (double lat, double lng)? coords = null;
                if (header.LatLongIdx >= 0 && lls.Length > 0)
                {
                    var llStr = j < lls.Length && !string.IsNullOrEmpty(lls[j]) ? lls[j] : (lls.Length == 1 ? lls[0] : "");
                    coords = ParseLatLong(llStr);
                }
                else if (rowCoords.HasValue && count == 1)
                {
                    coords = rowCoords;
                }
                else if (header.LatIdx >= 0 && header.LngIdx >= 0)
                {
                    var latParts = SplitLines(GetCell(row, header.LatIdx));
                    var lngParts = SplitLines(GetCell(row, header.LngIdx));
                    var latS = j < latParts.Length && !string.IsNullOrEmpty(latParts[j]) ? latParts[j] : latParts.ElementAtOrDefault(0) ?? "";
                    var lngS = j < lngParts.Length && !string.IsNullOrEmpty(lngParts[j]) ? lngParts[j] : lngParts.ElementAtOrDefault(0) ?? "";
                    coords = ParseLatLngColumns(latS, lngS);
                }

                if (!coords.HasValue) continue;

                var itemVal = j < items.Length && !string.IsNullOrEmpty(items[j]) ? items[j]
                    : (items.Length > 0 && !string.IsNullOrEmpty(items[0]) ? items[0] : (points.Count + 1).ToString(CultureInfo.InvariantCulture));
                var pnVal = j < pns.Length && !string.IsNullOrEmpty(pns[j]) ? pns[j] : (pns.Length > 0 ? pns[0] : "");
                var pointLoc = currentLocation;
                if (j < locParts.Length && !string.IsNullOrWhiteSpace(locParts[j]))
                    pointLoc = locParts[j];
                else if (locParts.Length == 1 && !string.IsNullOrWhiteSpace(locParts[0]))
                    pointLoc = locParts[0];

                points.Add(new SwuPolePoint
                {
                    Item = itemVal,
                    ItemNum = ParseItemNum(itemVal),
                    Pn = pnVal,
                    OuRemarks = currentOuBlock,
                    Location = pointLoc,
                    Lat = coords.Value.lat,
                    Lng = coords.Value.lng
                });
            }

            pendingItem = null;
        }

        if (points.Count == 0)
            throw new InvalidOperationException("No valid coordinates found in the spreadsheet.");

        points.Sort((a, b) => a.ItemNum.CompareTo(b.ItemNum));
        return new SwuParsedSheet(points, meta);
    }

    private static SwuSheetHeader? FindHeaderRow(string[][] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row == null || row.Length == 0) continue;
            var cols = row.Select(NormCol).ToArray();
            var itemIdx = FindColIndex(cols, ["ITEM", "ITEM NO", "ITEM NO.", "#"], false);
            if (itemIdx < 0) continue;

            var latLongIdx = FindLatLongIdx(cols);
            var latLng = FindLatLngIndices(cols);
            if (latLongIdx >= 0)
            {
                return new SwuSheetHeader(i, itemIdx,
                    FindColIndex(cols, ["PN", "POLE NO", "POLE NUMBER", "POLE NO.", "POLE#", "POLE"], true),
                    latLongIdx, -1, -1,
                    FindColIndex(cols, ["OU REMARKS", "OU REMARK", "OU_REMARKS", "OUREMARKS"], true),
                    FindColIndex(cols, ["LOCATION", "LOC", "AREA", "SITE", "PLACE"], true));
            }

            if (latLng != null)
            {
                return new SwuSheetHeader(i, itemIdx,
                    FindColIndex(cols, ["PN", "POLE NO", "POLE NUMBER", "POLE NO.", "POLE#", "POLE"], true),
                    -1, latLng.Value.latIdx, latLng.Value.lngIdx,
                    FindColIndex(cols, ["OU REMARKS", "OU REMARK", "OU_REMARKS", "OUREMARKS"], true),
                    FindColIndex(cols, ["LOCATION", "LOC", "AREA", "SITE", "PLACE"], true));
            }
        }

        return null;
    }

    private static SwuSheetMeta ParseSheetMeta(string[][] rows, int headerIndex)
    {
        var swuCode = "";
        var location = "";

        for (var r = 0; r < headerIndex; r++)
        {
            var row = rows[r];
            if (row == null) continue;
            foreach (var cell in row)
            {
                if (string.IsNullOrWhiteSpace(cell)) continue;
                var upper = NormCol(cell);
                if (upper is "CKT" or "ITEM" or "PN") continue;

                if (string.IsNullOrEmpty(swuCode) && SwuCodeRegex.IsMatch(cell))
                    swuCode = NormalizeSwuCode(SwuCodeRegex.Match(cell).Value);

                if (string.IsNullOrEmpty(location) && cell.Contains('(') && !SwuCodeRegex.IsMatch(cell))
                    location = cell.Trim();
            }
        }

        if (string.IsNullOrEmpty(swuCode) || string.IsNullOrEmpty(location))
        {
            var metaCells = new List<string>();
            for (var r = 0; r < headerIndex; r++)
            {
                var row = rows[r];
                if (row == null) continue;
                foreach (var cell in row)
                {
                    if (string.IsNullOrWhiteSpace(cell)) continue;
                    var upper = NormCol(cell);
                    if (upper is "CKT" or "ITEM" or "PN") continue;
                    metaCells.Add(cell.Trim());
                }
            }

            if (string.IsNullOrEmpty(swuCode))
            {
                foreach (var cell in metaCells)
                {
                    if (SwuCodeRegex.IsMatch(cell))
                    {
                        swuCode = NormalizeSwuCode(SwuCodeRegex.Match(cell).Value);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(swuCode) && metaCells.Count > 0)
                    swuCode = metaCells[0];
            }

            if (string.IsNullOrEmpty(location))
            {
                foreach (var cell in metaCells)
                {
                    if (cell != swuCode && cell.Contains('('))
                    {
                        location = cell;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(location))
                {
                    foreach (var cell in metaCells)
                    {
                        if (cell != swuCode)
                        {
                            location = cell;
                            break;
                        }
                    }
                }
            }
        }

        return new SwuSheetMeta(swuCode, location);
    }

    private static string NormalizeSwuCode(string code) =>
        Regex.Replace(code, @"\s*P\s*", " P", RegexOptions.IgnoreCase).Trim();

    private static (double lat, double lng)? CoordsFromRow(string[] row, SwuSheetHeader header)
    {
        if (header.LatLongIdx >= 0)
        {
            var latRaw = GetCell(row, header.LatLongIdx);
            return ParseLatLong(latRaw);
        }

        if (header.LatIdx >= 0 && header.LngIdx >= 0)
            return ParseLatLngColumns(GetCell(row, header.LatIdx), GetCell(row, header.LngIdx));

        return null;
    }

    private static (double lat, double lng)? ParseLatLong(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return null;
        var m = LatLongRegex.Match(str);
        if (!m.Success) return null;
        var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var lng = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;
        return (lat, lng);
    }

    private static (double lat, double lng)? ParseLatLngColumns(string latStr, string lngStr)
    {
        if (!double.TryParse(latStr.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            return null;
        if (!double.TryParse(lngStr.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
            return null;
        if (!double.IsFinite(lat) || !double.IsFinite(lng)) return null;
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;
        return (lat, lng);
    }

    private static int FindLatLongIdx(string[] cols)
    {
        var idx = FindColIndex(cols, ["LATLONG", "LAT LONG", "LAT/LONG", "LAT_LON", "COORDINATES", "COORDS"], true);
        if (idx >= 0) return idx;
        for (var j = 0; j < cols.Length; j++)
        {
            if (cols[j].Contains("LAT") && cols[j].Contains("LONG"))
                return j;
        }
        return -1;
    }

    private static (int latIdx, int lngIdx)? FindLatLngIndices(string[] cols)
    {
        var latIdx = FindColIndex(cols, ["LATITUDE", "LAT", "NORTHING"], false);
        var lngIdx = FindColIndex(cols, ["LONGITUDE", "LNG", "LON", "LONG", "EASTING"], false);
        return latIdx >= 0 && lngIdx >= 0 ? (latIdx, lngIdx) : null;
    }

    private static int FindColIndex(string[] cols, string[] names, bool allowPartial)
    {
        foreach (var name in names)
        {
            var idx = Array.IndexOf(cols, name);
            if (idx >= 0) return idx;
        }

        if (!allowPartial) return -1;
        for (var j = 0; j < cols.Length; j++)
        {
            foreach (var name in names)
            {
                if (name.Length < 4) continue;
                if (cols[j].Contains(name, StringComparison.Ordinal))
                    return j;
            }
        }

        return -1;
    }

    private static string NormCol(string s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant().Replace("  ", " ", StringComparison.Ordinal);

    private static string GetCell(string[] row, int idx) =>
        idx >= 0 && idx < row.Length ? (row[idx] ?? "").Trim() : "";

    private static string[] SplitLines(string? val) =>
        string.IsNullOrEmpty(val) ? [] : val.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).Select(x => x.Trim()).ToArray();

    private static bool IsNumericItem(string item) => NumericItemRegex.IsMatch(item.Trim());

    private static int ParseItemNum(string item)
    {
        var digits = new string(item.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 9999;
    }

    private sealed class SwuSheetHeader(
        int index, int itemIdx, int pnIdx, int latLongIdx, int latIdx, int lngIdx, int ouRemarksIdx, int locationIdx)
    {
        public int Index { get; } = index;
        public int ItemIdx { get; } = itemIdx;
        public int PnIdx { get; } = pnIdx;
        public int LatLongIdx { get; } = latLongIdx;
        public int LatIdx { get; } = latIdx;
        public int LngIdx { get; } = lngIdx;
        public int OuRemarksIdx { get; } = ouRemarksIdx;
        public int LocationIdx { get; } = locationIdx;
    }

    private sealed record SwuSheetMeta(string SwuCode, string Location);

    private sealed record SwuParsedSheet(List<SwuPolePoint> Points, SwuSheetMeta Meta);

    private sealed class SwuPolePoint
    {
        public string Item { get; set; } = "";
        public int ItemNum { get; set; }
        public string Pn { get; set; } = "";
        public string OuRemarks { get; set; } = "";
        public string Location { get; set; } = "";
        public double Lat { get; set; }
        public double Lng { get; set; }
    }
}
