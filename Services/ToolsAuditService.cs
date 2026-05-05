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

    public async Task<ToolsAuditSessionViewModel?> GetSessionAsync(
        Guid sessionId,
        string? statusFilter = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken cancellationToken = default)
    {
        statusFilter = string.IsNullOrWhiteSpace(statusFilter) ? null : statusFilter.Trim();
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
        if (TryParseStatusFilter(statusFilter, out var sf))
            toolSummary = toolSummary.Where(r => GetCount(r, sf) > 0).ToList();

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

        return new ToolsAuditSessionViewModel
        {
            SessionId = session.Id,
            OriginalFileName = session.OriginalFileName,
            AuditDate = session.AuditDate,
            WeekStartDate = session.WeekStartDate,
            UploadedUtc = session.UploadedUtc,
            StatusFilter = statusFilter,
            SortBy = sortBy,
            SortDir = sortDir,
            TechnicianSummary = techSummary,
            ToolSummaryAll = toolSummaryAll,
            ToolSummary = toolSummary
        };
    }

    private static bool TryParseStatusFilter(string? statusFilter, out ToolAuditStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(statusFilter))
            return false;
        var v = statusFilter.Trim().ToLowerInvariant();
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

