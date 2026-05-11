using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SlotAd_Globe.Data;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public class ToolsAuditService : IToolsAuditService
{
    private readonly AppDbContext _db;

    public ToolsAuditService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> ImportFromXlsxAsync(
        Stream xlsxStream,
        string? originalFileName,
        Guid uploadedByUserId,
        CancellationToken cancellationToken = default)
    {
        using var workbook = new XLWorkbook(xlsxStream);
        var ws = workbook.Worksheets.FirstOrDefault(s =>
            string.Equals(s.Name?.Trim(), "Checklist_Input", StringComparison.OrdinalIgnoreCase));
        if (ws is null)
            throw new InvalidOperationException("Worksheet 'Checklist_Input' was not found.");

        var used = ws.RangeUsed();
        if (used is null)
            throw new InvalidOperationException("The uploaded file appears to be empty.");

        var headerRow = used.FirstRow();
        var headerTexts = headerRow.Cells().Select(c => (c.Address.ColumnNumber, Text: (c.GetString() ?? "").Trim())).ToList();
        var technicianCol = headerTexts.FirstOrDefault(x => string.Equals(x.Text, "Technician", StringComparison.OrdinalIgnoreCase)).ColumnNumber;
        var dateCol = headerTexts.FirstOrDefault(x => string.Equals(x.Text, "Date", StringComparison.OrdinalIgnoreCase)).ColumnNumber;
        if (technicianCol <= 0)
            throw new InvalidOperationException("Column 'Technician' was not found in row 1.");
        if (dateCol <= 0)
            throw new InvalidOperationException("Column 'Date' was not found in row 1.");

        var toolCols = headerTexts
            .Where(x => x.ColumnNumber != technicianCol && x.ColumnNumber != dateCol)
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => (x.ColumnNumber, ToolName: x.Text))
            .ToList();
        if (toolCols.Count == 0)
            throw new InvalidOperationException("No tool columns were detected (expected columns after Technician/Date).");

        DateOnly? firstDate = null;
        var entries = new List<ToolAuditEntryEntity>(capacity: 2048);

        foreach (var row in used.RowsUsed().Skip(1))
        {
            var tech = row.Cell(technicianCol).GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(tech))
                break;

            var dateCell = row.Cell(dateCol);
            var parsedDate = TryParseExcelDate(dateCell);
            if (firstDate is null && parsedDate is not null)
                firstDate = parsedDate;

            foreach (var (col, toolName) in toolCols)
            {
                var raw = row.Cell(col).GetString()?.Trim() ?? string.Empty;
                var status = MapStatus(raw);
                entries.Add(new ToolAuditEntryEntity
                {
                    Id = Guid.NewGuid(),
                    TechnicianName = tech,
                    ToolName = toolName,
                    Status = status,
                    RawValue = string.IsNullOrWhiteSpace(raw) ? null : raw
                });
            }
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("No technician rows were found under 'Checklist_Input'.");

        var auditDate = firstDate;
        var weekStart = NormalizeToMonday(auditDate ?? DateOnly.FromDateTime(DateTime.UtcNow));

        var session = new ToolAuditSessionEntity
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = uploadedByUserId,
            OriginalFileName = originalFileName,
            AuditDate = auditDate,
            WeekStartDate = weekStart,
            UploadedUtc = DateTime.UtcNow
        };

        // attach session id to entries
        foreach (var e in entries)
        {
            e.SessionId = session.Id;
        }

        _db.ToolAuditSessions.Add(session);
        _db.ToolAuditEntries.AddRange(entries);
        await _db.SaveChangesAsync(cancellationToken);

        return session.Id;
    }

    public async Task<Guid?> GetLatestSessionIdForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.ToolAuditSessions
            .AsNoTracking()
            .Where(s => s.UploadedByUserId == userId)
            .OrderByDescending(s => s.UploadedUtc)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToolsAuditHistoryItem>> ListHistoryForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ToolAuditSessions
            .AsNoTracking()
            .Where(s => s.UploadedByUserId == userId)
            .OrderByDescending(s => s.UploadedUtc)
            .Take(50)
            .Select(s => new ToolsAuditHistoryItem
            {
                SessionId = s.Id,
                OriginalFileName = s.OriginalFileName,
                UploadedUtc = s.UploadedUtc,
                AuditDate = s.AuditDate,
                WeekStartDate = s.WeekStartDate
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<ToolsAuditSessionViewModel?> GetSessionAsync(
        Guid sessionId,
        IReadOnlyCollection<string>? selectedStatuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedStatuses = NormalizeSelectedStatuses(selectedStatuses);
        sortBy = string.IsNullOrWhiteSpace(sortBy) ? "none" : sortBy.Trim().ToLowerInvariant();
        sortDir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

        var session = await _db.ToolAuditSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
            return null;

        var entries = await _db.ToolAuditEntries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync(cancellationToken);

        Func<ToolsAuditTechnicianSummaryRow, int> techMetric = sortBy switch
        {
            "ok" => r => r.OkCount,
            "def" or "defective" => r => r.DefectiveCount,
            "na" or "n/a" => r => r.NaCount,
            "name" => _ => 0,
            _ => r => r.NoneCount
        };
        Func<ToolsAuditToolSummaryRow, int> toolMetric = sortBy switch
        {
            "ok" => r => r.OkCount,
            "def" or "defective" => r => r.DefectiveCount,
            "na" or "n/a" => r => r.NaCount,
            "name" => _ => 0,
            _ => r => r.NoneCount
        };

        var techSummary = entries
            .GroupBy(e => e.TechnicianName)
            .Select(g => new ToolsAuditTechnicianSummaryRow
            {
                TechnicianName = g.Key,
                OkCount = g.Count(x => x.Status == ToolAuditStatus.Ok),
                NoneCount = g.Count(x => x.Status == ToolAuditStatus.None),
                DefectiveCount = g.Count(x => x.Status == ToolAuditStatus.Defective),
                NaCount = g.Count(x => x.Status == ToolAuditStatus.NotApplicable)
            })
            .OrderByDescending(x => x.NoneCount)
            .ThenBy(x => x.TechnicianName)
            .ToList();

        var toolSummaryAll = entries
            .GroupBy(e => e.ToolName)
            .Select(g => new ToolsAuditToolSummaryRow
            {
                ToolName = g.Key,
                OkCount = g.Count(x => x.Status == ToolAuditStatus.Ok),
                NoneCount = g.Count(x => x.Status == ToolAuditStatus.None),
                DefectiveCount = g.Count(x => x.Status == ToolAuditStatus.Defective),
                NaCount = g.Count(x => x.Status == ToolAuditStatus.NotApplicable)
            })
            .OrderByDescending(x => x.NoneCount)
            .ThenByDescending(x => x.DefectiveCount)
            .ThenBy(x => x.ToolName)
            .ToList();

        // Tools table only: optional status filter + sorting.
        var toolSummary = toolSummaryAll.ToList();
        if (normalizedStatuses.Count > 0 && normalizedStatuses.Count < 4)
        {
            toolSummary = toolSummary
                .Where(r => normalizedStatuses.Any(s => GetCount(r, s) > 0))
                .ToList();
        }

        // Sorting
        if (string.Equals(sortBy, "name", StringComparison.OrdinalIgnoreCase))
        {
            toolSummary = (sortDir == "asc"
                    ? toolSummary.OrderBy(r => r.ToolName, StringComparer.OrdinalIgnoreCase)
                    : toolSummary.OrderByDescending(r => r.ToolName, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            toolSummary = (sortDir == "asc"
                    ? toolSummary.OrderBy(toolMetric).ThenBy(r => r.ToolName, StringComparer.OrdinalIgnoreCase)
                    : toolSummary.OrderByDescending(toolMetric).ThenBy(r => r.ToolName, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        // Keep first-seen order so columns match the uploaded Excel layout.
        var rawToolColumns = entries
            .Select(e => e.ToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rawRows = entries
            .GroupBy(e => e.TechnicianName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cells = new Dictionary<string, ToolAuditCellViewModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in rawToolColumns)
                {
                    var entry = g.FirstOrDefault(x => string.Equals(x.ToolName, col, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        cells[col] = BuildRawCell(ToolAuditStatus.NotApplicable);
                        continue;
                    }

                    var displayValue = string.IsNullOrWhiteSpace(entry.RawValue)
                        ? StatusToDisplay(entry.Status)
                        : entry.RawValue!.Trim();
                    cells[col] = BuildRawCell(entry.Status, displayValue);
                }
                return new ToolsAuditRawRow
                {
                    TechnicianName = g.Key,
                    CellsByTool = cells
                };
            })
            .ToList();

        return new ToolsAuditSessionViewModel
        {
            SessionId = session.Id,
            OriginalFileName = session.OriginalFileName,
            AuditDate = session.AuditDate,
            WeekStartDate = session.WeekStartDate,
            UploadedUtc = session.UploadedUtc,
            SelectedStatuses = normalizedStatuses.Select(ToStatusKey).ToList(),
            SortBy = sortBy,
            SortDir = sortDir,
            TechnicianSummary = techSummary,
            ToolSummaryAll = toolSummaryAll,
            ToolSummary = toolSummary,
            RawToolColumns = rawToolColumns,
            RawRows = rawRows
        };
    }

    private static HashSet<ToolAuditStatus> NormalizeSelectedStatuses(IReadOnlyCollection<string>? selectedStatuses)
    {
        var set = new HashSet<ToolAuditStatus>();
        if (selectedStatuses is not null)
        {
            foreach (var raw in selectedStatuses)
            {
                if (TryParseStatusKey(raw, out var status))
                    set.Add(status);
            }
        }

        // Default: show all statuses.
        if (set.Count == 0)
        {
            set.Add(ToolAuditStatus.Ok);
            set.Add(ToolAuditStatus.None);
            set.Add(ToolAuditStatus.Defective);
            set.Add(ToolAuditStatus.NotApplicable);
        }
        return set;
    }

    private static bool TryParseStatusKey(string? raw, out ToolAuditStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var v = raw.Trim().ToLowerInvariant();
        status = v switch
        {
            "ok" or "yes" => ToolAuditStatus.Ok,
            "none" => ToolAuditStatus.None,
            "def" or "defective" => ToolAuditStatus.Defective,
            "na" or "n/a" => ToolAuditStatus.NotApplicable,
            _ => default
        };
        return v is "ok" or "yes" or "none" or "def" or "defective" or "na" or "n/a";
    }

    private static string ToStatusKey(ToolAuditStatus status) => status switch
    {
        ToolAuditStatus.Ok => "ok",
        ToolAuditStatus.None => "none",
        ToolAuditStatus.Defective => "defective",
        ToolAuditStatus.NotApplicable => "na",
        _ => "na"
    };

    private static int GetCount(ToolsAuditTechnicianSummaryRow r, ToolAuditStatus s) => s switch
    {
        ToolAuditStatus.Ok => r.OkCount,
        ToolAuditStatus.None => r.NoneCount,
        ToolAuditStatus.Defective => r.DefectiveCount,
        ToolAuditStatus.NotApplicable => r.NaCount,
        _ => 0
    };

    private static int GetCount(ToolsAuditToolSummaryRow r, ToolAuditStatus s) => s switch
    {
        ToolAuditStatus.Ok => r.OkCount,
        ToolAuditStatus.None => r.NoneCount,
        ToolAuditStatus.Defective => r.DefectiveCount,
        ToolAuditStatus.NotApplicable => r.NaCount,
        _ => 0
    };

    private static ToolAuditStatus MapStatus(string raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(v))
            return ToolAuditStatus.NotApplicable;

        v = v.Replace(".", "").Trim();
        return v.ToLowerInvariant() switch
        {
            "yes" => ToolAuditStatus.Ok,
            "ok" => ToolAuditStatus.Ok,
            "none" => ToolAuditStatus.None,
            "defective" => ToolAuditStatus.Defective,
            "n/a" => ToolAuditStatus.NotApplicable,
            "na" => ToolAuditStatus.NotApplicable,
            _ => ToolAuditStatus.NotApplicable
        };
    }

    private static ToolAuditCellViewModel BuildRawCell(ToolAuditStatus status, string? displayValue = null)
    {
        var css = status switch
        {
            ToolAuditStatus.Ok => "bg-emerald-100 text-emerald-900",
            ToolAuditStatus.None => "bg-rose-100 text-rose-900",
            ToolAuditStatus.Defective => "bg-amber-100 text-amber-900",
            _ => "bg-slate-100 text-slate-700"
        };
        return new ToolAuditCellViewModel
        {
            DisplayValue = string.IsNullOrWhiteSpace(displayValue) ? StatusToDisplay(status) : displayValue!,
            CssClass = css
        };
    }

    private static string StatusToDisplay(ToolAuditStatus status) => status switch
    {
        ToolAuditStatus.Ok => "Yes",
        ToolAuditStatus.None => "None",
        ToolAuditStatus.Defective => "Defective",
        _ => "N/A"
    };

    private static DateOnly NormalizeToMonday(DateOnly date)
    {
        // DayOfWeek: Sunday=0 ... Saturday=6
        var dow = (int)date.DayOfWeek;
        var daysSinceMonday = dow == 0 ? 6 : dow - 1;
        return date.AddDays(-daysSinceMonday);
    }

    private static DateOnly? TryParseExcelDate(IXLCell cell)
    {
        if (cell.IsEmpty())
            return null;

        if (cell.DataType == XLDataType.DateTime)
        {
            var dt = cell.GetDateTime();
            return DateOnly.FromDateTime(dt);
        }

        // Sometimes dates are stored as numbers (OADate)
        if (cell.DataType == XLDataType.Number)
        {
            var n = cell.GetDouble();
            try
            {
                var dt = DateTime.FromOADate(n);
                return DateOnly.FromDateTime(dt);
            }
            catch
            {
                return null;
            }
        }

        var s = cell.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        if (DateTime.TryParse(s, out var parsed))
            return DateOnly.FromDateTime(parsed);

        return null;
    }
}

