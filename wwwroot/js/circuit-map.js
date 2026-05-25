(function (global) {
    'use strict';

    var DAVAO_CENTER = [7.0736, 125.6139];
    var DAVAO_ZOOM = 12;

    var COLORS = { pin: '#6366f1' };

    /** Distinct pin colors per table block in multi-table workbooks */
    var PIN_COLORS = [
        '#6366f1',
        '#10b981',
        '#f59e0b',
        '#ef4444',
        '#8b5cf6',
        '#06b6d4',
        '#ec4899',
        '#84cc16'
    ];

    var map = null;
    var tileLayer = null;
    var labelLayer = null;
    var pinsLayer = null;

    function normCol(s) {
        return String(s || '').trim().toUpperCase().replace(/\s+/g, ' ');
    }

    function cellText(cell) {
        if (!cell) return '';
        if (cell.w != null && String(cell.w).trim() !== '') return String(cell.w).trim();
        if (cell.v == null || cell.v === '') return '';
        return String(cell.v).trim();
    }

    /** Dense grid: column 0 = A, 1 = B, … so header indices match every data row. */
    function sheetToDenseRows(ws) {
        if (!ws) return [];
        var range = ws['!ref'] ? XLSX.utils.decode_range(ws['!ref']) : { s: { r: 0, c: 0 }, e: { r: 0, c: 0 } };
        
        // Safety: Recalculate max row/col to handle stale !ref properties from ClosedXML appends
        for (var key in ws) {
            if (key.charAt(0) === '!') continue;
            var cell = XLSX.utils.decode_cell(key);
            if (cell.r > range.e.r) range.e.r = cell.r;
            if (cell.c > range.e.c) range.e.c = cell.c;
        }

        var rows = [];
        for (var R = range.s.r; R <= range.e.r; R++) {
            var row = [];
            for (var C = range.s.c; C <= range.e.c; C++) {
                var addr = XLSX.utils.encode_cell({ r: R, c: C });
                row.push(cellText(ws[addr]));
            }
            rows.push(row);
        }
        return rows;
    }

    function splitLines(val) {
        if (val == null || val === '') return [''];
        return String(val).split(/\r?\n/).map(function (x) { return x.trim(); });
    }

    function parseLatLong(str) {
        if (!str) return null;
        var s = String(str).trim().replace(/\u00a0/g, ' ');
        var m = s.match(/^\s*([+-]?\d+(?:\.\d+)?)\s*[,;\s]\s*([+-]?\d+(?:\.\d+)?)\s*$/);
        if (!m) return null;
        var lat = parseFloat(m[1]);
        var lng = parseFloat(m[2]);
        if (!isFinite(lat) || !isFinite(lng)) return null;
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;
        return { lat: lat, lng: lng };
    }

    function parseLatLngColumns(latStr, lngStr) {
        var lat = parseFloat(String(latStr || '').replace(/,/g, ''));
        var lng = parseFloat(String(lngStr || '').replace(/,/g, ''));
        if (!isFinite(lat) || !isFinite(lng)) return null;
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;
        return { lat: lat, lng: lng };
    }

    function parseItemNum(item) {
        var n = parseInt(String(item).replace(/[^\d]/g, ''), 10);
        return isFinite(n) ? n : 9999;
    }

    function findColIndex(cols, names, allowPartial) {
        for (var i = 0; i < names.length; i++) {
            var idx = cols.indexOf(names[i]);
            if (idx !== -1) return idx;
        }
        if (!allowPartial) return -1;
        for (var j = 0; j < cols.length; j++) {
            var c = cols[j];
            for (var k = 0; k < names.length; k++) {
                if (names[k].length < 4) continue;
                if (c.indexOf(names[k]) !== -1) return j;
            }
        }
        return -1;
    }

    function findOuRemarksIdx(cols) {
        return findColIndex(cols, ['OU REMARKS', 'OU REMARK', 'OU_REMARKS', 'OUREMARKS'], true);
    }

    function findLocationIdx(cols) {
        return findColIndex(cols, ['LOCATION', 'LOC', 'AREA', 'SITE', 'PLACE'], true);
    }

    function findLatLongIdx(cols) {
        var idx = findColIndex(cols, ['LATLONG', 'LAT LONG', 'LAT/LONG', 'LAT_LON', 'COORDINATES', 'COORDS'], true);
        if (idx !== -1) return idx;
        return cols.findIndex(function (c) {
            return c.indexOf('LAT') !== -1 && c.indexOf('LONG') !== -1;
        });
    }

    function findLatLngIndices(cols) {
        var latIdx = findColIndex(cols, ['LATITUDE', 'LAT', 'NORTHING']);
        var lngIdx = findColIndex(cols, ['LONGITUDE', 'LNG', 'LON', 'LONG', 'EASTING']);
        if (latIdx !== -1 && lngIdx !== -1) return { latIdx: latIdx, lngIdx: lngIdx };
        return null;
    }

    var SWU_TITLE_RE = /SWU\s*P\s*0?\d+/i;

    function rowIsSwuTitle(row) {
        if (!row || !row.length) return false;
        for (var c = 0; c < row.length; c++) {
            if (SWU_TITLE_RE.test(String(row[c] || ''))) return true;
        }
        return false;
    }

    function buildHeaderFromRow(row, index) {
        var cols = row.map(normCol);
        var itemIdx = findColIndex(cols, ['ITEM', 'ITEM NO', 'ITEM NO.', '#'], false);
        if (itemIdx === -1) return null;
        var latLongIdx = findLatLongIdx(cols);
        var latLng = findLatLngIndices(cols);
        var pnIdx = findColIndex(cols, ['PN', 'POLE NO.', 'POLE NO', 'POLE NUMBER', 'POLE NO.', 'POLE#', 'POLE'], true);
        var base = {
            index: index,
            itemIdx: itemIdx,
            pnIdx: pnIdx,
            ouRemarksIdx: findOuRemarksIdx(cols),
            locationIdx: findLocationIdx(cols)
        };
        if (latLongIdx !== -1) {
            return Object.assign(base, { latLongIdx: latLongIdx, latIdx: -1, lngIdx: -1 });
        }
        if (latLng) {
            return Object.assign(base, { latLongIdx: -1, latIdx: latLng.latIdx, lngIdx: latLng.lngIdx });
        }
        return null;
    }

    function findHeaderRow(rows) {
        for (var i = 0; i < rows.length; i++) {
            var header = buildHeaderFromRow(rows[i], i);
            if (header) return header;
        }
        return null;
    }

    /** Every ITEM + LATLONG/LAT+LNG header row (reorganized exports repeat headers per table). */
    function findAllHeaderRows(rows) {
        var headers = [];
        for (var i = 0; i < rows.length; i++) {
            var header = buildHeaderFromRow(rows[i], i);
            if (header) headers.push(header);
        }
        return headers;
    }

    function parseSheetMeta(rows, headerIndex) {
        var swuCode = '';
        var location = '';
        var swuRe = /SWU\s*P\s*0?\d+/i;

        for (var r = 0; r < headerIndex; r++) {
            var row = rows[r];
            if (!row) continue;
            for (var c = 0; c < row.length; c++) {
                var cell = String(row[c] || '').trim();
                if (!cell) continue;
                var upper = normCol(cell);
                if (upper === 'CKT' || upper === 'ITEM' || upper === 'PN') continue;

                if (!swuCode && swuRe.test(cell)) {
                    swuCode = cell.match(swuRe)[0].replace(/\s+/g, ' ').replace(/\s*P\s*/i, ' P');
                }
                if (!location && cell.indexOf('(') !== -1 && !swuRe.test(cell)) {
                    location = cell;
                }
            }
        }

        if (!swuCode || !location) {
            var metaCells = [];
            for (var r2 = 0; r2 < headerIndex; r2++) {
                var row2 = rows[r2];
                if (!row2) continue;
                for (var c2 = 0; c2 < row2.length; c2++) {
                    var cell2 = String(row2[c2] || '').trim();
                    if (!cell2) continue;
                    var u2 = normCol(cell2);
                    if (u2 === 'CKT' || u2 === 'ITEM' || u2 === 'PN') continue;
                    metaCells.push(cell2);
                }
            }
            if (!swuCode && metaCells.length) {
                for (var mi = 0; mi < metaCells.length; mi++) {
                    if (swuRe.test(metaCells[mi])) {
                        swuCode = metaCells[mi].match(swuRe)[0].replace(/\s+/g, ' ').replace(/\s*P\s*/i, ' P');
                        break;
                    }
                }
                if (!swuCode) swuCode = metaCells[0];
            }
            if (!location && metaCells.length > 1) {
                for (var lj = 0; lj < metaCells.length; lj++) {
                    if (metaCells[lj] !== swuCode && metaCells[lj].indexOf('(') !== -1) {
                        location = metaCells[lj];
                        break;
                    }
                }
                if (!location) {
                    for (var lk = 0; lk < metaCells.length; lk++) {
                        if (metaCells[lk] !== swuCode) {
                            location = metaCells[lk];
                            break;
                        }
                    }
                }
            }
        }

        return { swuCode: swuCode, location: location };
    }

    function formatPnDisplay(pn) {
        var s = String(pn || '').trim();
        if (!s) return '';
        if (/^0+\d+$/.test(s)) {
            var stripped = s.replace(/^0+(?=\d)/, '');
            return stripped || s;
        }
        return s;
    }

    function formatOuRemarksForTitle(text) {
        var t = String(text || '').trim();
        if (!t) return '';
        t = t.replace(/^\*\s*/, '');
        t = t.replace(/^request\s+for\s+/i, '');
        return t.trim();
    }

    function buildPinDescription(meta, point) {
        // Unified-table format stores fileTitle directly on the point
        var fileTitle = point.fileTitle || meta.swuCode || '';
        var metaLoc   = point.fileTitle ? '' : (meta.location || ''); // skip redundant meta loc in new format
        var area      = String(point.location || '').trim();
        var remarks   = formatOuRemarksForTitle(point.ouRemarks);
        var pn        = formatPnDisplay(point.pn) || '\u2014';
        var item      = String(point.item || '').trim();
        var latLng    = (point.lat != null && point.lng != null)
            ? point.lat.toFixed(7) + ', ' + point.lng.toFixed(7)
            : '';

        // ---- plain text (used for tooltip) ----
        var textLines = [];
        if (fileTitle) textLines.push(fileTitle);
        if (metaLoc)   textLines.push(metaLoc);
        if (item)      textLines.push('Item: '     + item);
        textLines.push('PN: ' + pn);
        if (area)      textLines.push('Location: ' + area);
        if (latLng)    textLines.push(latLng);
        if (remarks)   textLines.push('OU REMARKS: ' + remarks);

        // ---- rich HTML (used for click-popup) ----
        var html = '<div class="circuit-pin-popup">';
        if (fileTitle) {
            html += '<div class="circuit-pin-line circuit-pin-swu" style="border-bottom:1px solid rgba(255,255,255,0.12);padding-bottom:4px;margin-bottom:4px">' +
                escapeHtml(fileTitle) + '</div>';
        }
        if (metaLoc) {
            html += '<div class="circuit-pin-line">' + escapeHtml(metaLoc) + '</div>';
        }
        if (item) {
            html += '<div class="circuit-pin-line"><span class="circuit-pin-label">Item:</span> ' +
                escapeHtml(item) + '</div>';
        }
        html += '<div class="circuit-pin-line circuit-pin-pn"><span class="circuit-pin-label">Pole No.:</span> ' +
            escapeHtml(pn) + '</div>';
        if (area) {
            html += '<div class="circuit-pin-line"><span class="circuit-pin-label">Location:</span> ' +
                escapeHtml(area) + '</div>';
        }
        if (latLng) {
            html += '<div class="circuit-pin-line" style="font-size:0.72rem;opacity:0.65">' +
                escapeHtml(latLng) + '</div>';
        }
        if (remarks) {
            html += '<div class="circuit-pin-line"><span class="circuit-pin-label">OU Remarks:</span> ' +
                escapeHtml(remarks) + '</div>';
        }
        html += '</div>';

        return { html: html, text: textLines.join('\n') };
    }

    /** @deprecated Use buildPinDescription */
    function buildPinTitle(meta, point) {
        return buildPinDescription(meta, point).text;
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function coordsFromRow(row, header) {
        if (header.latLongIdx >= 0) {
            var latRaw = row[header.latLongIdx] != null ? String(row[header.latLongIdx]).trim() : '';
            return latRaw ? parseLatLong(latRaw) : null;
        }
        if (header.latIdx >= 0 && header.lngIdx >= 0) {
            var latS = row[header.latIdx] != null ? String(row[header.latIdx]).trim() : '';
            var lngS = row[header.lngIdx] != null ? String(row[header.lngIdx]).trim() : '';
            return parseLatLngColumns(latS, lngS);
        }
        return null;
    }

    function parseSwuSheet(rows, endRow) {
        var header = findHeaderRow(rows);
        if (!header) {
            throw new Error('Could not find ITEM and coordinate columns (LATLONG or LAT/LNG) in the spreadsheet.');
        }

        var lastRow = endRow != null ? endRow : rows.length;
        var meta = parseSheetMeta(rows, header.index);
        var points = [];
        var currentOuBlock = '';
        var currentLocation = '';

        for (var r = header.index + 1; r < lastRow; r++) {
            var row = rows[r];
            if (!row || !row.length) continue;
            if (isRowEmpty(row)) continue;

            var itemRaw = row[header.itemIdx] != null ? String(row[header.itemIdx]).trim() : '';
            var pnRaw = header.pnIdx >= 0 && row[header.pnIdx] != null ? String(row[header.pnIdx]).trim() : '';
            var latRaw = header.latLongIdx >= 0 && row[header.latLongIdx] != null
                ? String(row[header.latLongIdx]).trim()
                : '';
            var ouRaw = header.ouRemarksIdx >= 0 && row[header.ouRemarksIdx] != null
                ? String(row[header.ouRemarksIdx]).trim()
                : '';
            var locRaw = header.locationIdx >= 0 && row[header.locationIdx] != null
                ? String(row[header.locationIdx]).trim()
                : '';
            var locParts = locRaw ? splitLines(locRaw) : [];
            if (locRaw) {
                for (var li = 0; li < locParts.length; li++) {
                    if (locParts[li]) {
                        currentLocation = locParts[li];
                        break;
                    }
                }
            }

            if (normCol(itemRaw) === 'ITEM') continue;

            if (buildHeaderFromRow(row, r)) continue;

            if (ouRaw) {
                if (ouRaw.charAt(0) === '*') {
                    currentOuBlock = ouRaw;
                } else {
                    currentOuBlock = currentOuBlock ? currentOuBlock + ' ' + ouRaw : ouRaw;
                }
            }

            var rowCoords = coordsFromRow(row, header);
            if (!rowCoords && !latRaw) {
                if (itemRaw && !/^\d+$/.test(itemRaw) && normCol(itemRaw) !== 'ITEM') {
                    currentLocation = itemRaw;
                }
                if (ouRaw && points.length) {
                    var prev = points[points.length - 1];
                    prev.ouRemarks = (prev.ouRemarks ? prev.ouRemarks + ' ' : '') + ouRaw;
                }
                continue;
            }

            var items = splitLines(itemRaw);
            var pns = splitLines(pnRaw);
            var lls = splitLines(latRaw);
            var count = Math.max(items.length, pns.length, lls.length, 1);

            for (var j = 0; j < count; j++) {
                var coords = null;
                if (header.latLongIdx >= 0 && lls.length) {
                    var llStr = lls[j] !== undefined && lls[j] !== '' ? lls[j] : (lls.length === 1 ? lls[0] : '');
                    coords = parseLatLong(llStr);
                } else if (rowCoords && count === 1) {
                    coords = rowCoords;
                } else if (header.latIdx >= 0 && header.lngIdx >= 0) {
                    var latParts = splitLines(row[header.latIdx] != null ? String(row[header.latIdx]) : '');
                    var lngParts = splitLines(row[header.lngIdx] != null ? String(row[header.lngIdx]) : '');
                    coords = parseLatLngColumns(
                        latParts[j] !== undefined ? latParts[j] : latParts[0],
                        lngParts[j] !== undefined ? lngParts[j] : lngParts[0]
                    );
                }
                if (!coords) continue;

                var itemVal = items[j] !== undefined && items[j] !== '' ? items[j] : (items[0] || String(points.length + 1));
                var pnVal = pns[j] !== undefined && pns[j] !== '' ? pns[j] : (pns[0] || '');
                var pointLoc = currentLocation;
                if (locParts[j]) {
                    pointLoc = locParts[j];
                } else if (locParts.length === 1 && locParts[0]) {
                    pointLoc = locParts[0];
                }

                points.push({
                    item: itemVal,
                    itemNum: parseItemNum(itemVal),
                    pn: pnVal,
                    ouRemarks: currentOuBlock,
                    location: pointLoc,
                    lat: coords.lat,
                    lng: coords.lng
                });
            }
        }

        if (!points.length) {
            throw new Error('No valid coordinates found in the spreadsheet.');
        }

        points.sort(function (a, b) {
            if (a.itemNum !== b.itemNum) return a.itemNum - b.itemNum;
            return 0;
        });

        return { points: points, meta: meta };
    }

    function isRowEmpty(row) {
        if (!row || !row.length) return true;
        for (var i = 0; i < row.length; i++) {
            if (String(row[i] || '').trim() !== '') return false;
        }
        return true;
    }

    /** Split sheet rows into blocks separated by fully empty rows (reorganized multi-table export). */
    function splitRowsIntoBlocks(rows) {
        var blocks = [];
        var current = [];
        for (var i = 0; i < rows.length; i++) {
            if (isRowEmpty(rows[i])) {
                if (current.length) {
                    blocks.push(current);
                    current = [];
                }
            } else {
                if (current.length && rowIsSwuTitle(rows[i]) && blockHasHeader(current)) {
                    blocks.push(current);
                    current = [];
                }
                current.push(rows[i]);
            }
        }
        if (current.length) blocks.push(current);
        return blocks;
    }

    function blockHasHeader(blockRows) {
        for (var i = 0; i < blockRows.length; i++) {
            if (buildHeaderFromRow(blockRows[i], i)) return true;
        }
        return false;
    }

    function extractBlockLabel(blockRows, meta) {
        for (var r = 0; r < Math.min(blockRows.length, 4); r++) {
            var row = blockRows[r];
            if (!row) continue;
            for (var c = 0; c < row.length; c++) {
                var cell = String(row[c] || '').trim();
                if (!cell) continue;
                if (SWU_TITLE_RE.test(cell)) {
                    var m = cell.match(SWU_TITLE_RE);
                    return m ? m[0].replace(/\s+/g, ' ').replace(/\s*P\s*/i, ' P') : cell;
                }
            }
        }
        if (meta && meta.swuCode) return meta.swuCode;
        if (meta && meta.location) return meta.location;
        return 'Table';
    }

    function splitBlockByHeaders(blockRows) {
        var headers = findAllHeaderRows(blockRows);
        if (headers.length <= 1) return [blockRows];

        var slices = [];
        for (var h = 0; h < headers.length; h++) {
            var header = headers[h];
            var start = h === 0 ? 0 : headers[h - 1].index;
            for (var t = header.index - 1; t >= start; t--) {
                if (isRowEmpty(blockRows[t])) break;
                if (rowIsSwuTitle(blockRows[t])) {
                    start = t;
                    break;
                }
            }

            var end = h < headers.length - 1 ? headers[h + 1].index : blockRows.length;
            while (end > header.index + 1 && isRowEmpty(blockRows[end - 1])) {
                end--;
            }
            if (h < headers.length - 1) {
                var beforeNext = headers[h + 1].index - 1;
                if (beforeNext > header.index && rowIsSwuTitle(blockRows[beforeNext])) {
                    end = beforeNext;
                }
            }

            var segment = blockRows.slice(start, end);
            if (segment.length) slices.push(segment);
        }

        return slices.length ? slices : [blockRows];
    }

    // ---------------------------------------------------------------------------
    // NEW: Unified single-table parser (FILE TITLE column format)
    // ---------------------------------------------------------------------------

    /** Return the column index of the FILE TITLE column, or -1. */
    function findFileTitleIdx(cols) {
        return findColIndex(cols, ['FILE TITLE', 'FILETITLE', 'FILE_TITLE'], true);
    }

    /**
     * Parse the new unified-table format produced by SwuPoleProcessingService.
     * The sheet has one header row: FILE TITLE | ITEM | POLE NO. | LATLONG | LOCATION
     * Rows are grouped by the FILE TITLE value; each unique value becomes a block
     * with its own pin color.
     *
     * Returns null if the sheet does not have a FILE TITLE column (legacy file).
     */
    function parseUnifiedSwuTable(rows) {
        var headerRowIdx  = -1;
        var fileTitleIdx  = -1;
        var itemIdx       = -1;
        var pnIdx         = -1;
        var latLongIdx    = -1;
        var locationIdx   = -1;

        // Find the header row that contains both FILE TITLE and a coordinate column
        for (var i = 0; i < rows.length; i++) {
            var cols = rows[i].map(normCol);
            var fti  = findFileTitleIdx(cols);
            if (fti === -1) continue;

            var ii  = findColIndex(cols, ['ITEM', 'ITEM NO', 'ITEM NO.', '#'], false);
            var lli = findLatLongIdx(cols);
            if (ii === -1 || lli === -1) continue;

            headerRowIdx = i;
            fileTitleIdx = fti;
            itemIdx      = ii;
            pnIdx        = findColIndex(cols, ['PN', 'POLE NO.', 'POLE NO', 'POLE NUMBER', 'POLE#', 'POLE'], true);
            latLongIdx   = lli;
            locationIdx  = findLocationIdx(cols);
            break;
        }

        if (headerRowIdx === -1) return null; // not unified format

        // Group data rows by FILE TITLE
        var groups     = {}; // fileTitle -> []
        var groupOrder = []; // insertion-order of unique titles

        for (var r = headerRowIdx + 1; r < rows.length; r++) {
            var row = rows[r];
            if (!row || isRowEmpty(row)) continue;

            var fileTitle = (row[fileTitleIdx] || '').trim();
            if (!fileTitle) continue;

            var latRaw = latLongIdx >= 0 ? (row[latLongIdx] || '').trim() : '';
            var coords = parseLatLong(latRaw);
            if (!coords) continue; // skip rows without valid coordinates

            if (!groups[fileTitle]) {
                groups[fileTitle] = [];
                groupOrder.push(fileTitle);
            }

            var itemVal = itemIdx    >= 0 ? (row[itemIdx]    || '').trim() : '';
            var pnVal   = pnIdx     >= 0 ? (row[pnIdx]      || '').trim() : '';
            var locVal  = locationIdx >= 0 ? (row[locationIdx] || '').trim() : '';

            groups[fileTitle].push({
                fileTitle : fileTitle,
                item      : itemVal,
                itemNum   : parseItemNum(itemVal),
                pn        : pnVal,
                location  : locVal,
                ouRemarks : '',
                lat       : coords.lat,
                lng       : coords.lng
            });
        }

        if (!groupOrder.length) return null;

        return groupOrder.map(function (title, idx) {
            var pts = groups[title];
            pts.sort(function (a, b) { return a.itemNum - b.itemNum; });
            return {
                points : pts,
                meta   : { swuCode: title, location: '' },
                label  : title,
                color  : PIN_COLORS[idx % PIN_COLORS.length]
            };
        });
    }

    // ---------------------------------------------------------------------------

    /**
     * Parse one or more SWU tables from a sheet.
     * Tries the new unified FILE TITLE format first; falls back to the
     * legacy blank-row-separated multi-table format.
     * @returns {Array<{points: Array, meta: object, label: string, color: string}>}
     */
    function parseSwuWorkbookMulti(rows) {
        // ---- Try new unified single-table format first ----
        var unified = parseUnifiedSwuTable(rows);
        if (unified && unified.length > 0) return unified;

        // ---- Fall back: legacy blank-row-separated blocks ----
        var parsedBlocks = [];

        var rowBlocks = splitRowsIntoBlocks(rows);
        rowBlocks.forEach(function (blockRows, blockIndex) {
            var headerSlices = splitBlockByHeaders(blockRows);
            headerSlices.forEach(function (sliceRows, sliceIndex) {
                try {
                    var parsed = parseSwuSheet(sliceRows);
                    if (!parsed.points.length) return;
                    parsedBlocks.push({
                        points: parsed.points,
                        meta  : parsed.meta,
                        label : extractBlockLabel(sliceRows, parsed.meta),
                        color : PIN_COLORS[parsedBlocks.length % PIN_COLORS.length]
                    });
                } catch (err) {
                    console.warn('SWU block ' + (blockIndex + 1) + '.' + (sliceIndex + 1) + ' skipped:', err.message);
                }
            });
        });

        if (parsedBlocks.length === 0) {
            var single = parseSwuSheet(rows);
            parsedBlocks.push({
                points: single.points,
                meta  : single.meta,
                label : extractBlockLabel(rows, single.meta),
                color : PIN_COLORS[0]
            });
        }

        return parsedBlocks;
    }

    function isDarkTheme() {
        return document.documentElement.getAttribute('data-theme') !== 'light';
    }

    var mapState = {
        blocks: [],
        markers: [],
        hiddenBlocks: {},
        searchQuery: ''
    };

    function clearLayers() {
        if (pinsLayer) pinsLayer.clearLayers();
    }

    function updateMapFilters() {
        if (!pinsLayer) return;
        pinsLayer.clearLayers();
        
        var visibleCount = 0;
        mapState.markers.forEach(function(m) {
            if (mapState.hiddenBlocks[m.blockIndex]) return;
            
            pinsLayer.addLayer(m.layer);
            visibleCount++;
        });

        var countEl = document.getElementById('circuit-map-total-count');
        if (countEl) countEl.innerText = visibleCount + ' pin' + (visibleCount === 1 ? '' : 's');
    }

    function renderBlocks(blocks) {
        clearLayers();
        mapState.blocks = blocks;
        mapState.markers = [];
        mapState.hiddenBlocks = {};
        
        var searchInput = document.getElementById('circuit-map-search-input');
        mapState.searchQuery = searchInput ? searchInput.value : '';

        var allBounds = [];
        var legendHtml = '';
        var totalPins = 0;

        blocks.forEach(function (block, idx) {
            var fill = block.color || COLORS.pin;
            totalPins += block.points.length;

            block.points.forEach(function (p) {
                var desc = buildPinDescription(block.meta, p);
                var marker = L.circleMarker([p.lat, p.lng], {
                    radius: 7,
                    fillColor: fill,
                    color: '#ffffff',
                    weight: 2,
                    opacity: 1,
                    fillOpacity: 0.95
                });
                marker.bindTooltip(desc.text, {
                    direction: 'top',
                    offset: [0, -8],
                    opacity: 0.95,
                    sticky: true,
                    className: 'circuit-pin-tooltip'
                });
                marker.bindPopup(desc.html, { maxWidth: 380, minWidth: 220 });

                mapState.markers.push({
                    layer: marker,
                    point: p,
                    blockIndex: idx,
                    blockLabel: block.label
                });

                allBounds.push(L.latLng(p.lat, p.lng));
            });

            legendHtml += '<label class="circuit-map-legend-item">' +
                '<input type="checkbox" class="circuit-map-legend-cb" data-block-idx="' + idx + '" checked />' +
                '<span class="circuit-map-legend-dot" style="background:' + escapeHtml(fill) + '"></span>' +
                '<span class="circuit-map-legend-label" title="' + escapeHtml(block.label) + '">' + escapeHtml(block.label) + '</span>' +
                '<span class="circuit-map-legend-count">' + block.points.length + '</span>' +
                '</label>';
        });

        var panel = document.getElementById('circuit-map-panel');
        if (panel) panel.classList.add('visible');
        
        var searchPanel = document.getElementById('circuit-map-search-panel');
        if (searchPanel) searchPanel.classList.add('visible');

        var legendContent = document.getElementById('circuit-map-legend-content');
        if (legendContent) {
            legendContent.innerHTML = legendHtml;
            var cbs = legendContent.querySelectorAll('.circuit-map-legend-cb');
            cbs.forEach(function(cb) {
                cb.addEventListener('change', function(e) {
                    var bIdx = parseInt(e.target.getAttribute('data-block-idx'), 10);
                    if (e.target.checked) {
                        delete mapState.hiddenBlocks[bIdx];
                    } else {
                        mapState.hiddenBlocks[bIdx] = true;
                    }
                    updateMapFilters();
                });
            });
        }

        updateMapFilters();

        if (allBounds.length) {
            map.fitBounds(L.latLngBounds(allBounds), { padding: [40, 40], maxZoom: 16 });
        }
    }

    function setStatus(html, visible) {
        // Kept for backward compatibility, but logic has moved to the panel
    }

    function showError(msg) {
        if (typeof showToast === 'function') {
            showToast(msg, 'error');
        } else {
            alert(msg);
        }
    }

    function applyTiles() {
        if (!map) return;
        var dark = isDarkTheme();
        var darkTiles = 'https://{s}.basemaps.cartocdn.com/dark_nolabels/{z}/{x}/{y}{r}.png';
        var lightTiles = 'https://{s}.basemaps.cartocdn.com/light_nolabels/{z}/{x}/{y}{r}.png';
        var darkLabels = 'https://{s}.basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}{r}.png';
        var lightLabels = 'https://{s}.basemaps.cartocdn.com/light_only_labels/{z}/{x}/{y}{r}.png';

        if (tileLayer) map.removeLayer(tileLayer);
        if (labelLayer) map.removeLayer(labelLayer);

        tileLayer = L.tileLayer(dark ? darkTiles : lightTiles, {
            attribution: '&copy; <a href="https://carto.com/">CartoDB</a> &copy; <a href="https://www.openstreetmap.org/copyright">OSM</a>',
            maxZoom: 18,
            subdomains: 'abcd'
        }).addTo(map);

        labelLayer = L.tileLayer(dark ? darkLabels : lightLabels, {
            maxZoom: 18,
            subdomains: 'abcd',
            pane: 'overlayPane',
            opacity: 0.7
        }).addTo(map);
    }

    function initMap() {
        var mapEl = document.getElementById('circuit-map');
        if (!mapEl || map) return;

        map = L.map(mapEl, {
            center: DAVAO_CENTER,
            zoom: DAVAO_ZOOM,
            zoomControl: false,
            attributionControl: true,
            preferCanvas: true
        });
        L.control.zoom({ position: 'bottomright' }).addTo(map);
        applyTiles();

        pinsLayer = L.layerGroup().addTo(map);

        setTimeout(function () { map.invalidateSize(); }, 100);

        var resizeTimer;
        window.addEventListener('resize', function () {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () {
                if (map) map.invalidateSize();
            }, 150);
        });

        var themeObserver = new MutationObserver(function () {
            applyTiles();
        });
        themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    }

    function handleFile(file) {
        if (!file) return;
        if (!/\.xlsx$/i.test(file.name)) {
            showError('Please upload an .xlsx file.');
            return;
        }

        var reader = new FileReader();
        reader.onload = function (ev) {
            try {
                if (typeof XLSX === 'undefined') {
                    throw new Error('XLSX library not loaded.');
                }
                var data = new Uint8Array(ev.target.result);
                var wb = XLSX.read(data, { type: 'array', cellText: true, cellDates: true });
                var ws = wb.Sheets[wb.SheetNames[0]];
                var rows = sheetToDenseRows(ws);
                var blocks = parseSwuWorkbookMulti(rows);

                if (!blocks.length) {
                    throw new Error('No tables with coordinates were found in this file.');
                }

                renderBlocks(blocks);

                var totalPins = blocks.reduce(function (n, b) { return n + b.points.length; }, 0);
                if (totalPins === 0) {
                    throw new Error('No valid coordinates found in the spreadsheet.');
                }
            } catch (err) {
                showError(err.message || 'Failed to parse the spreadsheet.');
            }
        };
        reader.onerror = function () {
            showError('Could not read the file.');
        };
        reader.readAsArrayBuffer(file);
    }

    function bindUpload() {
        var input = document.getElementById('circuit-map-file-input');
        if (!input) return;
        input.addEventListener('change', function (e) {
            var file = e.target.files && e.target.files[0];
            handleFile(file);
            e.target.value = '';
        });
    }

    function bindSearch() {
        var input = document.getElementById('circuit-map-search-input');
        var btn = document.getElementById('circuit-map-search-btn');
        if (!input) return;
        
        function executeSearch() {
            var q = input.value.trim().toLowerCase();
            if (!q) return;
            
            var found = null;
            for (var i = 0; i < mapState.markers.length; i++) {
                var m = mapState.markers[i];
                if (mapState.hiddenBlocks[m.blockIndex]) continue; // Skip hidden blocks
                
                var pnMatch = (m.point.pn || '').toLowerCase().indexOf(q) !== -1;
                var titleMatch = (m.blockLabel || '').toLowerCase().indexOf(q) !== -1;
                if (!titleMatch && m.point.fileTitle) {
                    titleMatch = m.point.fileTitle.toLowerCase().indexOf(q) !== -1;
                }
                
                if (pnMatch || titleMatch) {
                    found = m;
                    // Prioritize exact PN match
                    if ((m.point.pn || '').toLowerCase() === q) {
                        break;
                    }
                }
            }
            
            if (found) {
                map.flyTo([found.point.lat, found.point.lng], 18, { animate: true, duration: 1.5 });
                // Add a small delay to ensure the map has panned enough before opening the popup
                setTimeout(function() {
                    found.layer.openPopup();
                }, 300);
            } else {
                showError("No visible pin found matching: " + q);
            }
        }

        input.addEventListener('keydown', function(e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                executeSearch();
            }
        });
        
        if (btn) {
            btn.addEventListener('click', executeSearch);
        }
    }

    function init() {
        initMap();
        bindUpload();
        bindSearch();
    }

    global.CircuitMap = {
        init: init,
        parseSwuSheet: parseSwuSheet,
        parseSwuWorkbookMulti: parseSwuWorkbookMulti,
        parseUnifiedSwuTable: parseUnifiedSwuTable,
        findAllHeaderRows: findAllHeaderRows,
        splitRowsIntoBlocks: splitRowsIntoBlocks,
        sheetToDenseRows: sheetToDenseRows,
        buildPinDescription: buildPinDescription,
        buildPinTitle: buildPinTitle
    };
})(window);