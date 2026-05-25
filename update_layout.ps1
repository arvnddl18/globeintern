$dashboard = Get-Content "c:\Users\user\Desktop\globeees\globeintern\Views\Report\Dashboard.cshtml" -Raw
$lines = $dashboard -split "`r`n"
if ($lines.Count -lt 2) { $lines = $dashboard -split "`n" }
$css = $lines[1667..1938] -join "`r`n"

$opFile = "c:\Users\user\Desktop\globeees\globeintern\Views\Report\OperationalDashboard.cshtml"
$opContent = Get-Content $opFile -Raw
$opLines = $opContent -split "`r`n"
if ($opLines.Count -lt 2) { $opLines = $opContent -split "`n" }

$idxStyleEnd = [array]::IndexOf($opLines, "</style>")
$idxHeaderStart = [array]::IndexOf($opLines, '<header class="text-center mb-10 slide-up">')
$idxSectionScripts = [array]::IndexOf($opLines, "@section Scripts {")

Write-Host "Style End: $idxStyleEnd, Header: $idxHeaderStart, Scripts: $idxSectionScripts"

if ($idxStyleEnd -ge 0 -and $idxHeaderStart -ge 0 -and $idxSectionScripts -ge 0) {
    $newLines = @()
    $newLines += $opLines[0..($idxStyleEnd-1)]
    $newLines += $lines[1667..1938]
    $newLines += "</style>"

    $newLines += $opLines[($idxStyleEnd+1)..($idxHeaderStart-1)]

    $newLines += '<div class="dash-app" id="dash-app">'
    $newLines += '    <button type="button" id="dash-sidebar-expand" class="dash-sidebar-expand-btn" aria-label="Expand sidebar" title="Expand sidebar">'
    $newLines += '        <i data-lucide="chevrons-right" style="width:16px;height:16px;"></i>'
    $newLines += '    </button>'
    $newLines += '    <div class="dash-sidebar-backdrop" id="dash-sidebar-backdrop" aria-hidden="true"></div>'
    $newLines += '    <aside class="dash-sidebar" id="dash-sidebar" aria-label="Dashboard sections">'
    $newLines += '        <div class="dash-sidebar-brand" style="display:flex;align-items:flex-start;gap:0.5rem;">'
    $newLines += '            <div style="flex:1;min-width:0;">'
    $newLines += '                <p>Navigation</p>'
    $newLines += '                <h2>Operational sections</h2>'
    $newLines += '            </div>'
    $newLines += '            <button type="button" id="dash-sidebar-collapse" class="dash-sidebar-collapse-btn flex-shrink-0" title="Collapse sidebar" aria-label="Collapse sidebar">'
    $newLines += '                <i data-lucide="chevrons-left" style="width:14px;height:14px;"></i>'
    $newLines += '            </button>'
    $newLines += '        </div>'
    $newLines += '        <nav class="dash-sidebar-nav" id="dash-sidebar-nav">'
    $newLines += '            <button type="button" class="dash-nav-item is-active" data-dash-panel="operational" aria-current="page">'
    $newLines += '                <i data-lucide="activity"></i><span>Operational Dashboard</span>'
    $newLines += '            </button>'
    $newLines += '        </nav>'
    $newLines += '        <div class="dash-sidebar-foot">SlotAd Globe &middot; Operational</div>'
    $newLines += '    </aside>'
    $newLines += '    <div class="dash-main">'
    $newLines += '        <header class="dash-page-header mb-8 slide-up">'
    $newLines += '            <div class="flex flex-col md:flex-row items-center justify-between gap-4">'
    $newLines += '                <div class="text-center md:text-left">'
    $newLines += '                    <h1 class="text-3xl sm:text-4xl lg:text-5xl font-bold tracking-tight mb-2" style="color: #f0f0f8;">'
    $newLines += '                        Operational Dashboard'
    $newLines += '                    </h1>'
    $newLines += '                    <p class="text-base sm:text-lg" style="color: #7a7a9e;">'
    $newLines += '                        Real-time operational metrics and field activity overview'
    $newLines += '                    </p>'
    $newLines += '                </div>'
    $newLines += '                <div>'
    $newLines += '                </div>'
    $newLines += '            </div>'
    $newLines += '        </header>'
    $newLines += '        <button type="button" id="dash-mobile-toggle" class="dash-mobile-toggle inline-flex items-center gap-2 px-3 py-2 rounded-lg text-xs font-semibold cursor-pointer"'
    $newLines += '                style="color: #c8c8e0; background: rgba(99,102,241,0.12); border: 1px solid #2a2a4a;">'
    $newLines += '            <i data-lucide="menu" style="width:14px;height:14px;"></i> Sections'
    $newLines += '        </button>'
    $newLines += '        <div id="panel-operational" class="dash-panel is-active" data-dash-panel="operational">'

    $newLines += $opLines[($idxHeaderStart+8)..($idxSectionScripts-1)]

    $newLines += "        </div>"
    $newLines += "    </div>"
    $newLines += "</div>"
    $newLines += ""

    $newLines += $opLines[$idxSectionScripts..($opLines.Count-1)]

    $finalContent = $newLines -join "`r`n"
    $jsSnippet = "            // Sidebar toggle logic (copied from Dashboard layout)
            const sidebar = document.getElementById('dash-sidebar');
            const backdrop = document.getElementById('dash-sidebar-backdrop');
            const collapseBtn = document.getElementById('dash-sidebar-collapse');
            const expandBtn = document.getElementById('dash-sidebar-expand');
            const mobileToggle = document.getElementById('dash-mobile-toggle');
            const rootLayout = document.querySelector('.dash-layout-root');

            function toggleMobile() {
                if(sidebar) sidebar.classList.toggle('is-open');
                if(backdrop) backdrop.classList.toggle('is-open');
                if(sidebar) document.body.style.overflow = sidebar.classList.contains('is-open') ? 'hidden' : '';
            }

            function collapseDesktop() {
                if(rootLayout) rootLayout.classList.add('dash-sidebar-collapsed');
                if(sidebar) sidebar.style.transform = 'translateX(-105%)';
            }

            function expandDesktop() {
                if(rootLayout) rootLayout.classList.remove('dash-sidebar-collapsed');
                if(sidebar) sidebar.style.transform = 'translateX(0)';
            }

            if (mobileToggle) mobileToggle.addEventListener('click', toggleMobile);
            if (backdrop) backdrop.addEventListener('click', toggleMobile);
            if (collapseBtn) collapseBtn.addEventListener('click', collapseDesktop);
            if (expandBtn) expandBtn.addEventListener('click', expandDesktop);"

    # Because lines might be split differently, replace with regex or exactly
    $finalContent = $finalContent -replace '(?s)        \}\)\(\);\r?\n    </script>', "        })();`r`n`r`n$jsSnippet`r`n    </script>"

    Set-Content -Path $opFile -Value $finalContent -Encoding UTF8
    Write-Host "Updated OperationalDashboard.cshtml successfully!"
} else {
    Write-Host "Failed to find indices!"
}
