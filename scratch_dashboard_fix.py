import sys
import re

file_path = r'c:\Users\user\Desktop\globeees\globeintern\Views\Report\Dashboard.cshtml'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix warning CS8714 by adding `?? ""` to GetValueOrDefault calls inside GroupBy
content = content.replace(
    'r.GetValueOrDefault("Territory")) ? "(Unknown)" : r.GetValueOrDefault("Territory")).ToDictionary',
    'r.GetValueOrDefault("Territory")) ? "(Unknown)" : (r.GetValueOrDefault("Territory") ?? "")).ToDictionary'
)

# Replace all `r.GetValueOrDefault("...", "")` inside GroupBy to `(r.GetValueOrDefault("...") ?? "")`
for key in ["ComplianceReason", "Status", "Skillset", "Territory", "DelayReason", "SubStatus"]:
    content = content.replace(
        f'r.GetValueOrDefault("{key}", "")).ToDictionary',
        f'(r.GetValueOrDefault("{key}") ?? "")).ToDictionary'
    )

# Also add delayReasonAm and delayReasonPm to the pending view
pending_search = """var territoryPm = _pmRows.GroupBy(r => (r.GetValueOrDefault("Territory") ?? "")).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);"""
pending_replacement = pending_search + """

            var delayReasonAm = _amRows.Where(r => r.GetValueOrDefault("_isDelayed") == "1").GroupBy(r => (r.GetValueOrDefault("DelayReason") ?? "")).ToDictionary(g => g.Key, g => g.Count());
            var delayReasonPm = _pmRows.Where(r => r.GetValueOrDefault("_isDelayed") == "1").GroupBy(r => (r.GetValueOrDefault("DelayReason") ?? "")).ToDictionary(g => g.Key, g => g.Count());"""

content = content.replace(pending_search, pending_replacement)

# Also fix the line where drPct pending view had an issue if it existed
content = content.replace('var drPct = (double)kv.Value / delayTotalPending * 100;', 'var drPct = (double)kv.Value / delayTotal * 100;')

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print("Done")
