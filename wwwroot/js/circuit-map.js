(function (global) {
    'use strict';

    var DAVAO_CENTER = [7.0736, 125.6139];
    var DAVAO_ZOOM = 12;
    var OSRM_BASE = 'https://router.project-osrm.org/route/v1/driving';

    var COLORS = {
        default: '#fbbf24',
        circuit91_92: '#6366f1',
        circuit93: '#14b8a6',
        pin: '#6366f1'
    };

    var map = null;
    var tileLayer = null;
    var labelLayer = null;
    var pinsLayer = null;
    var routesLayer = null;
    var decorators = [];
    var renderGeneration = 0;

    function normCol(s) {
        return String(s || '').trim().toUpperCase().replace(/\s+/g, ' ');
    }

    function splitLines(val) {
        if (val == null || val === '') return [''];
        return String(val).split(/\r?\n/).map(function (x) { return x.trim(); });
    }

    function parseLatLong(str) {
        if (!str) return null;
        var m = String(str).match(/^\s*([+-]?\d+\.?\d*)\s*,\s*([+-]?\d+\.?\d*)\s*$/);
        if (!m) return null;
        var lat = parseFloat(m[1]);
        var lng = parseFloat(m[2]);
        if (!isFinite(lat) || !isFinite(lng)) return null;
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;
        return { lat: lat, lng: lng };
    }

    function parseItemNum(item) {
        var n = parseInt(String(item).replace(/[^\d]/g, ''), 10);
        return isFinite(n) ? n : 9999;
    }

    function resolveCircuitKey(ckt) {
        var u = String(ckt || '').toUpperCase();
        if (!u) return null;
        var has91 = u.indexOf('CIRCUIT NO 91') !== -1 || u.indexOf('CIRCUIT 91') !== -1;
        var has92 = u.indexOf('CIRCUIT NO 92') !== -1 || u.indexOf('CIRCUIT 92') !== -1;
        var has93 = u.indexOf('CIRCUIT NO 93') !== -1 || u.indexOf('CIRCUIT 93') !== -1;
        if (has93 && !has91 && !has92) return '93';
        if (has91 && has92) return '91-92';
        if (has93) return '93';
        if (has91) return '91-92';
        if (has92) return '91-92';
        return null;
    }

    function circuitLabel(key) {
        if (key === '93') return 'CIRCUIT NO 93';
        if (key === '91-92') return 'CIRCUIT NO 91 / CIRCUIT NO 92';
        if (key === 'default') return 'Route';
        return 'Route';
    }

    function colorForSegment(key) {
        if (key === '93') return COLORS.circuit93;
        if (key === '91-92') return COLORS.circuit91_92;
        return COLORS.default;
    }

    function findHeaderRow(rows) {
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            if (!row || !row.length) continue;
            var cols = row.map(normCol);
            var itemIdx = cols.indexOf('ITEM');
            var latIdx = cols.findIndex(function (c) { return c === 'LATLONG' || c === 'LAT LONG' || c === 'LAT/LONG'; });
            if (itemIdx !== -1 && latIdx !== -1) {
                return { index: i, itemIdx: itemIdx, cktIdx: cols.indexOf('CKT'), pnIdx: cols.indexOf('PN'), latIdx: latIdx };
            }
        }
        return null;
    }

    function parseSwuSheet(rows) {
        var header = findHeaderRow(rows);
        if (!header) {
            throw new Error('Could not find ITEM and LATLONG columns in the spreadsheet.');
        }

        var points = [];
        var currentCkt = '';
        var hasAnyCkt = false;

        for (var r = header.index + 1; r < rows.length; r++) {
            var row = rows[r];
            if (!row || !row.length) continue;

            var itemRaw = row[header.itemIdx] != null ? String(row[header.itemIdx]).trim() : '';
            var cktRaw = header.cktIdx >= 0 && row[header.cktIdx] != null ? String(row[header.cktIdx]).trim() : '';
            var pnRaw = header.pnIdx >= 0 && row[header.pnIdx] != null ? String(row[header.pnIdx]).trim() : '';
            var latRaw = row[header.latIdx] != null ? String(row[header.latIdx]).trim() : '';

            if (normCol(itemRaw) === 'ITEM') continue;

            if (cktRaw) {
                currentCkt = cktRaw;
                hasAnyCkt = true;
            }

            if (!latRaw) continue;

            var items = splitLines(itemRaw);
            var pns = splitLines(pnRaw);
            var lls = splitLines(latRaw);
            var count = Math.max(items.length, pns.length, lls.length);

            for (var j = 0; j < count; j++) {
                var llStr = lls[j] !== undefined && lls[j] !== '' ? lls[j] : (lls.length === 1 ? lls[0] : '');
                var coords = parseLatLong(llStr);
                if (!coords) continue;

                var itemVal = items[j] !== undefined && items[j] !== '' ? items[j] : (items[0] || String(points.length + 1));
                var pnVal = pns[j] !== undefined ? pns[j] : (pns[0] || '');
                var segKey = resolveCircuitKey(currentCkt);

                points.push({
                    item: itemVal,
                    itemNum: parseItemNum(itemVal),
                    pn: pnVal,
                    ckt: currentCkt,
                    segmentKey: segKey,
                    lat: coords.lat,
                    lng: coords.lng
                });
            }
        }

        if (!points.length) {
            throw new Error('No valid coordinates found in LATLONG column.');
        }

        points.sort(function (a, b) {
            if (a.itemNum !== b.itemNum) return a.itemNum - b.itemNum;
            return 0;
        });

        return { points: points, hasCircuits: hasAnyCkt };
    }

    /** Group consecutive points with the same circuit; routes never cross circuit boundaries. */
    function buildCircuitGroups(points, hasCircuits) {
        var groups = [];
        var current = null;

        points.forEach(function (p) {
            var key = hasCircuits ? (p.segmentKey || 'unknown') : 'default';
            if (!current || current.key !== key) {
                current = { key: key, points: [] };
                groups.push(current);
            }
            current.points.push(p);
        });

        return groups.filter(function (g) { return g.points.length > 0; });
    }

    function isDarkTheme() {
        return document.documentElement.getAttribute('data-theme') !== 'light';
    }

    function clearDecorators() {
        decorators.forEach(function (d) {
            try { if (map && d) map.removeLayer(d); } catch (e) { /* ignore */ }
        });
        decorators = [];
    }

    function clearLayers() {
        clearDecorators();
        if (pinsLayer) { pinsLayer.clearLayers(); }
        if (routesLayer) { routesLayer.clearLayers(); }
    }

    function midpointAlongPath(latlngs) {
        if (!latlngs.length) return null;
        if (latlngs.length === 1) return latlngs[0];
        var total = 0;
        var dists = [0];
        for (var i = 1; i < latlngs.length; i++) {
            total += latlngs[i - 1].distanceTo(latlngs[i]);
            dists.push(total);
        }
        if (total === 0) return latlngs[Math.floor(latlngs.length / 2)];
        var half = total / 2;
        for (var j = 1; j < dists.length; j++) {
            if (dists[j] >= half) {
                var t = (half - dists[j - 1]) / (dists[j] - dists[j - 1] || 1);
                var a = latlngs[j - 1];
                var b = latlngs[j];
                return L.latLng(a.lat + (b.lat - a.lat) * t, a.lng + (b.lng - a.lng) * t);
            }
        }
        return latlngs[latlngs.length - 1];
    }

    function addArrowPolyline(latlngs, color, weight, opacity) {
        if (latlngs.length < 2) return null;
        var line = L.polyline(latlngs, {
            color: color,
            weight: weight,
            opacity: opacity,
            lineCap: 'round',
            lineJoin: 'round'
        });
        routesLayer.addLayer(line);

        if (typeof L.polylineDecorator === 'function') {
            var deco = L.polylineDecorator(line, {
                patterns: [{
                    offset: '100%',
                    repeat: 0,
                    symbol: L.Symbol.arrowHead({
                        pixelSize: 12,
                        polygon: false,
                        pathOptions: { stroke: true, color: color, weight: weight + 1, opacity: 1 }
                    })
                }]
            });
            deco.addTo(map);
            decorators.push(deco);
        }
        return line;
    }

    function addCircuitLabel(latlng, text, bgColor) {
        var icon = L.divIcon({
            className: 'circuit-label-icon',
            html: '<span style="background:' + bgColor + ';">' + escapeHtml(text) + '</span>',
            iconSize: null,
            iconAnchor: [0, 14]
        });
        var marker = L.marker(latlng, { icon: icon, interactive: false, zIndexOffset: 500 });
        routesLayer.addLayer(marker);
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    /** Fetch street-aligned path through waypoints in order (OSRM). */
    function fetchStreetRoute(latlngs) {
        if (latlngs.length < 2) {
            return Promise.resolve(latlngs.slice());
        }

        var coordStr = latlngs.map(function (ll) {
            return ll.lng.toFixed(7) + ',' + ll.lat.toFixed(7);
        }).join(';');

        var url = OSRM_BASE + '/' + coordStr +
            '?overview=full&geometries=geojson&steps=false';

        return fetch(url).then(function (res) {
            if (!res.ok) throw new Error('Routing service unavailable (' + res.status + ').');
            return res.json();
        }).then(function (data) {
            if (!data || data.code !== 'Ok' || !data.routes || !data.routes[0]) {
                throw new Error(data && data.message ? data.message : 'Could not find a street route for these points.');
            }
            var geom = data.routes[0].geometry;
            if (!geom || !geom.coordinates || !geom.coordinates.length) {
                throw new Error('Empty route returned from routing service.');
            }
            return geom.coordinates.map(function (c) {
                return L.latLng(c[1], c[0]);
            });
        });
    }

    function drawPins(points) {
        points.forEach(function (p) {
            var marker = L.circleMarker([p.lat, p.lng], {
                radius: 6,
                fillColor: COLORS.pin,
                color: '#ffffff',
                weight: 2,
                opacity: 1,
                fillOpacity: 0.95
            });
            var popup = '<strong>Item ' + escapeHtml(p.item) + '</strong>';
            if (p.pn) popup += '<br>PN: ' + escapeHtml(p.pn);
            if (p.ckt) popup += '<br>CKT: ' + escapeHtml(p.ckt);
            popup += '<br>' + p.lat.toFixed(7) + ', ' + p.lng.toFixed(7);
            marker.bindPopup(popup);
            pinsLayer.addLayer(marker);
        });
    }

    function renderCircuitGroup(group) {
        var waypoints = group.points.map(function (p) { return L.latLng(p.lat, p.lng); });
        var color = colorForSegment(group.key);
        var label = circuitLabel(group.key);

        if (waypoints.length < 2) {
            if (waypoints.length === 1 && group.key !== 'unknown') {
                addCircuitLabel(waypoints[0], label, color);
            }
            return Promise.resolve({ fallback: false });
        }

        return fetchStreetRoute(waypoints).then(function (routed) {
            addArrowPolyline(routed, color, 5, 0.92);
            addCircuitLabel(midpointAlongPath(routed), label, color);
            return { fallback: false };
        }).catch(function () {
            addArrowPolyline(waypoints, color, 5, 0.75);
            addCircuitLabel(midpointAlongPath(waypoints), label, color);
            return { fallback: true };
        });
    }

    function renderPoints(data) {
        var gen = ++renderGeneration;
        clearLayers();

        var points = data.points;
        var groups = buildCircuitGroups(points, data.hasCircuits);

        drawPins(points);

        var allBounds = points.map(function (p) { return L.latLng(p.lat, p.lng); });
        if (allBounds.length) {
            map.fitBounds(L.latLngBounds(allBounds), { padding: [40, 40], maxZoom: 16 });
        }

        updateLegend(data.hasCircuits, points);

        if (groups.length === 0) return Promise.resolve();

        setStatus('<strong>Routing along streets…</strong><br>Drawing paths per circuit.', true);

        var chain = Promise.resolve();
        var routedCount = 0;
        var usedFallback = false;

        groups.forEach(function (group) {
            if (group.key === 'unknown') return;

            chain = chain.then(function () {
                if (gen !== renderGeneration) return;
                return renderCircuitGroup(group).then(function (result) {
                    routedCount++;
                    if (result && result.fallback) usedFallback = true;
                });
            });
        });

        return chain.then(function () {
            if (gen !== renderGeneration) return { usedFallback: usedFallback, routedCount: routedCount };
            return { usedFallback: usedFallback, routedCount: routedCount };
        });
    }

    function updateLegend(hasCircuits, points) {
        var legend = document.getElementById('circuit-map-legend');
        var leg91 = document.getElementById('circuit-legend-91-92');
        var leg93 = document.getElementById('circuit-legend-93');
        var legOverall = document.getElementById('circuit-legend-overall');
        if (!legend) return;

        var has91 = false;
        var has93 = false;
        if (hasCircuits && points) {
            points.forEach(function (p) {
                if (p.segmentKey === '91-92') has91 = true;
                if (p.segmentKey === '93') has93 = true;
            });
        }

        if (leg91) leg91.style.display = has91 ? 'flex' : 'none';
        if (leg93) leg93.style.display = has93 ? 'flex' : 'none';
        if (legOverall) legOverall.style.display = hasCircuits ? 'none' : 'flex';
        legend.classList.add('visible');
    }

    function setStatus(html, visible) {
        var el = document.getElementById('circuit-map-status');
        if (!el) return;
        el.innerHTML = html;
        el.classList.toggle('visible', !!visible);
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
        routesLayer = L.layerGroup().addTo(map);

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
                var wb = XLSX.read(data, { type: 'array' });
                var ws = wb.Sheets[wb.SheetNames[0]];
                var rows = XLSX.utils.sheet_to_json(ws, { header: 1, defval: '' });
                var parsed = parseSwuSheet(rows);

                setStatus(
                    '<strong>' + escapeHtml(file.name) + '</strong><br>' +
                    parsed.points.length + ' pin' + (parsed.points.length === 1 ? '' : 's') + ' — routing…',
                    true
                );

                renderPoints(parsed).then(function (routeResult) {
                    var circuitNote = parsed.hasCircuits
                        ? ' Street-aligned paths per circuit (no cross-circuit links).'
                        : ' Street-aligned path in item order.<br><em>No circuit labels in file.</em>';
                    if (routeResult && routeResult.usedFallback) {
                        circuitNote += ' Some segments used direct lines (routing unavailable).';
                    }
                    setStatus(
                        '<strong>' + escapeHtml(file.name) + '</strong><br>' +
                        parsed.points.length + ' pin' + (parsed.points.length === 1 ? '' : 's') + ' plotted.' +
                        circuitNote,
                        true
                    );
                }).catch(function (err) {
                    showError(err.message || 'Failed to draw routes.');
                });
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

    function init() {
        initMap();
        bindUpload();
        setStatus('Upload an SWU coordinate .xlsx file to plot pins and street-aligned routes per circuit.', true);
    }

    global.CircuitMap = {
        init: init,
        parseSwuSheet: parseSwuSheet,
        buildCircuitGroups: buildCircuitGroups
    };
})(window);
