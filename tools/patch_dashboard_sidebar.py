from pathlib import Path

p = Path("Views/Report/Dashboard.cshtml")
text = p.read_text(encoding="utf-8")

# Remove duplicate KPI cards inside slot-adherence (after Filters locked header)
dup_start = (
    '            <i data-lucide="lock" style="width:12px; height:12px;"></i> Filters locked\n'
    "        </span>\n"
    "        }\n"
    "    </div>\n"
    "\n"
    '<section class="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-8 fade-in" style="animation-delay: 0.1s;">'
)
if dup_start in text:
    i = text.index(dup_start)
    j = text.index("@if (Model.ActiveDashboardView == \"status\")", i)
    keep = (
        '            <i data-lucide="lock" style="width:12px; height:12px;"></i> Filters locked\n'
        "        </span>\n"
        "        }\n"
    "    </div>\n"
    "\n"
    )
    text = text[:i] + keep + text[j:]
    print("Removed duplicate KPI cards")

# Close slot panel + open kpi-counts before KPI Field Counts section
kpi_sec = '<section class="mb-8 fade-in" style="animation-delay: 0.18s;">\n    @{\n        var netDelayedCount'
if 'id="panel-kpi-counts"' not in text and kpi_sec in text:
    text = text.replace(
        kpi_sec,
        '</div>\n</div><!-- #panel-slot-adherence -->\n\n'
        '<motion id="panel-kpi-counts" class="dash-panel" data-dash-panel="kpi-counts">\n'
        + kpi_sec,
        1,
    )
    text = text.replace(
        '<motion id="panel-kpi-counts"',
        '<div id="panel-kpi-counts"',
    )
    print("panel-kpi-counts")

# Close kpi-counts, open data-preview
data_sec = '<section class="mb-8 fade-in" style="animation-delay: 0.25s;">\n    <div class="rounded-xl overflow-hidden"'
if 'id="panel-data-preview"' not in text and data_sec in text:
    text = text.replace(
        data_sec,
        '</div><!-- #panel-kpi-counts -->\n\n'
        '<div id="panel-data-preview" class="dash-panel" data-dash-panel="data-preview">\n'
        + data_sec,
        1,
    )
    print("panel-data-preview")

# Close data-preview, open heatmap
hm_marker = (
    "<!-- ═══════════════════════════════════════════════════════════════════════════\n"
    "     HEATMAP ANALYSIS CONTAINER"
)
if 'id="panel-heatmap"' not in text and hm_marker in text:
    text = text.replace(
        hm_marker,
        '</motion><!-- #panel-data-preview -->\n\n'
        '<div id="panel-heatmap" class="dash-panel" data-dash-panel="heatmap">\n'
        + hm_marker,
        1,
    )
    text = text.replace('</motion><!-- #panel-data-preview -->', '</div><!-- #panel-data-preview -->')
    print("panel-heatmap")

# Close heatmap, open recurring
rt_marker = (
    "<!-- ═══════════════════════════════════════════════════════════════════════════\n"
    "     START: Recurring Tickets Report"
)
if 'id="panel-recurring"' not in text and rt_marker in text:
    text = text.replace(
        rt_marker,
        '</div><!-- #panel-heatmap -->\n\n'
        '<div id="panel-recurring" class="dash-panel" data-dash-panel="recurring">\n'
        + rt_marker,
        1,
    )
    print("panel-recurring")

# Close dash layout before bottom upload CTA
upload_sec = '<section class="flex flex-wrap items-center justify-center gap-4 mb-4 fade-in" style="animation-delay: 0.3s;">'
if upload_sec in text and "#dash-app" not in text:
    text = text.replace(
        upload_sec,
        '</div><!-- #panel-recurring -->\n    </div><!-- .dash-main -->\n</div><!-- #dash-app -->\n\n'
        + upload_sec,
        1,
    )
    print("closed dash-app")

p.write_text(text, encoding="utf-8")
print("Done")
