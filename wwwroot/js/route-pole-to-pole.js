(function (global) {
    'use strict';

    var DAVAO_CENTER = [7.0736, 125.6139];
    var DAVAO_ZOOM = 12;

    var PHASE_COLORS = [
        '#6366f1', '#10b981', '#f59e0b', '#ef4444',
        '#8b5cf6', '#06b6d4', '#ec4899', '#84cc16'
    ];

    var map = null;
    var tileLayer = null;
    var polylinesLayer = null;
    var markersLayer = null;
    var phaseRoutes = [];
    var CACHE_KEY = 'routeMapData';
    var phaseLayersMap = {};
    var hiddenPhases = {};

    function init() {
        if (!document.getElementById('route-map')) return;

        map = L.map('route-map', {
            center: DAVAO_CENTER,
            zoom: DAVAO_ZOOM,
            zoomControl: false,
            attributionControl: false
        });

        L.control.zoom({ position: 'bottomright' }).addTo(map);

        var isDark = document.documentElement.getAttribute('data-theme') !== 'light';
        var darkUrl = 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png';
        var lightUrl = 'https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png';

        tileLayer = L.tileLayer(isDark ? darkUrl : lightUrl, {
            subdomains: 'abcd',
            maxZoom: 19
        }).addTo(map);

        polylinesLayer = L.featureGroup().addTo(map);
        markersLayer = L.featureGroup().addTo(map);

        var fileInput = document.getElementById('route-map-file-input');
        if (fileInput) {
            fileInput.addEventListener('change', handleFileUpload);
        }

        // Theme observer
        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                if (mutation.attributeName === 'data-theme') {
                    var newIsDark = document.documentElement.getAttribute('data-theme') !== 'light';
                    tileLayer.setUrl(newIsDark ? darkUrl : lightUrl);
                }
            });
        });
        observer.observe(document.documentElement, { attributes: true });

        // Load cached data on startup
        loadFromCache();
    }

    function handleFileUpload(e) {
        var file = e.target.files[0];
        if (!file) return;

        var reader = new FileReader();
        reader.onload = function (e) {
            var data = new Uint8Array(e.target.result);
            var workbook = XLSX.read(data, { type: 'array' });
            var firstSheetName = workbook.SheetNames[0];
            var worksheet = workbook.Sheets[firstSheetName];

            // Expected columns: PHASE, ROUTE, COORDINATES
            var json = XLSX.utils.sheet_to_json(worksheet, { header: 1 });
            parseRouteData(json);
        };
        reader.readAsArrayBuffer(file);
    }

    function parseLatLong(str) {
        if (!str) return null;
        var s = String(str).trim().replace(/\u00a0/g, ' ');
        var m = s.match(/^\s*([+-]?\d+(?:\.\d+)?)\s*[,;\s]\s*([+-]?\d+(?:\.\d+)?)\s*$/);
        if (!m) return null;
        var lat = parseFloat(m[1]);
        var lng = parseFloat(m[2]);
        if (!isFinite(lat) || !isFinite(lng)) return null;
        return { lat: lat, lng: lng };
    }

    function parseRouteData(rows) {
        var routes = [];
        var headerIndex = -1;

        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            if (row && row.length > 0 && String(row[0]).toUpperCase().includes('PHASE')) {
                headerIndex = i;
                break;
            }
        }

        var startRow = headerIndex !== -1 ? headerIndex + 1 : 1;
        var uniquePhases = [];

        for (var i = startRow; i < rows.length; i++) {
            var row = rows[i];
            if (!row || !row.length) continue;

            var phase = String(row[0] || '').trim();
            var routeDesc = String(row[1] || '').trim();
            if (!phase && !routeDesc) continue;

            var coords = [];
            for (var c = 2; c < row.length; c++) {
                var coordStr = String(row[c] || '').trim();
                var latLng = parseLatLong(coordStr);
                if (latLng) {
                    coords.push(latLng);
                }
            }

            if (coords.length > 0) {
                if (uniquePhases.indexOf(phase) === -1) {
                    uniquePhases.push(phase);
                }
                routes.push({
                    phase: phase,
                    route: routeDesc,
                    coordinates: coords
                });
            }
        }
        hiddenPhases = {};
        renderRoutes(routes, uniquePhases);
    }

    async function fetchRouteFromOSRM(coords) {
        if (coords.length < 2) return coords;
        var allGeometry = [];

        for (var i = 0; i < coords.length - 1; i += 99) {
            var chunk = coords.slice(i, i + 100);
            var coordStr = chunk.map(function (c) { return c.lng + ',' + c.lat; }).join(';');
            var url = 'https://router.project-osrm.org/route/v1/driving/' + coordStr + '?overview=full&geometries=geojson';

            try {
                var response = await fetch(url);
                var data = await response.json();
                if (data && data.code === 'Ok' && data.routes && data.routes.length > 0) {
                    var geometryCoords = data.routes[0].geometry.coordinates;
                    var latLngs = geometryCoords.map(function (c) { return { lat: c[1], lng: c[0] }; });
                    if (allGeometry.length > 0) {
                        latLngs.shift();
                    }
                    allGeometry = allGeometry.concat(latLngs);
                } else {
                    if (allGeometry.length > 0) chunk.shift();
                    allGeometry = allGeometry.concat(chunk);
                }
            } catch (err) {
                console.error('OSRM fetch error:', err);
                if (allGeometry.length > 0) chunk.shift();
                allGeometry = allGeometry.concat(chunk);
            }
            // Small delay to prevent rate limiting
            await new Promise(function (resolve) { setTimeout(resolve, 200); });
        }
        return allGeometry;
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function getBearing(start, end) {
        var lat1 = start.lat * Math.PI / 180;
        var lon1 = start.lng * Math.PI / 180;
        var lat2 = end.lat * Math.PI / 180;
        var lon2 = end.lng * Math.PI / 180;

        var y = Math.sin(lon2 - lon1) * Math.cos(lat2);
        var x = Math.cos(lat1) * Math.sin(lat2) -
            Math.sin(lat1) * Math.cos(lat2) * Math.cos(lon2 - lon1);

        var brng = Math.atan2(y, x);
        return ((brng * 180 / Math.PI) + 360) % 360;
    }

    async function renderRoutes(routes, uniquePhases, preloadedGeometries) {
        polylinesLayer.clearLayers();
        markersLayer.clearLayers();
        phaseRoutes = routes;
        phaseLayersMap = {}; // Reset the map layers tracking
        var cachedGeometries = preloadedGeometries || {};

        var legendHtml = '';
        uniquePhases.forEach(function (phase, idx) {
            var color = PHASE_COLORS[idx % PHASE_COLORS.length];
            var phaseCount = routes.filter(function (r) { return r.phase === phase; }).length;
            var isHidden = !!hiddenPhases[phase];

            legendHtml += '<label class="circuit-map-legend-item" data-phase="' + escapeHtml(phase) + '" style="' + (isHidden ? 'opacity: 0.35;' : '') + '">' +
                '<span class="circuit-map-legend-dot" style="background:' + escapeHtml(color) + '; border-radius: 2px;" title="' + (isHidden ? 'Click to show phase' : 'Click to hide phase') + '"></span>' +
                '<span class="circuit-map-legend-label" title="' + escapeHtml(phase) + '">Phase ' + escapeHtml(phase) + '</span>' +
                '<span class="circuit-map-legend-count">' + phaseCount + ' routes</span>' +
                '</label>';
        });

        var legendContent = document.getElementById('route-map-legend-content');
        if (legendContent) {
            legendContent.innerHTML = legendHtml;
            
            // Add click listeners to color boxes (the dots)
            var dots = legendContent.querySelectorAll('.circuit-map-legend-dot');
            dots.forEach(function (dot) {
                dot.style.cursor = 'pointer'; // Ensure cursor is pointer
                dot.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var item = dot.closest('.circuit-map-legend-item');
                    var phase = item.getAttribute('data-phase');
                    togglePhaseVisibility(phase, dot);
                });
            });
        }

        var totalCount = document.getElementById('route-map-total-count');
        if (totalCount) totalCount.innerText = uniquePhases.length + ' phases';

        var allBounds = [];

        // Use a standard for loop since we will use await
        for (var rIdx = 0; rIdx < routes.length; rIdx++) {
            var routeData = routes[rIdx];
            var phaseIdx = uniquePhases.indexOf(routeData.phase);
            var color = PHASE_COLORS[phaseIdx % PHASE_COLORS.length];

            // Fetch routed path (use cached geometry if available)
            var routedGeometry;
            if (cachedGeometries[rIdx]) {
                routedGeometry = cachedGeometries[rIdx];
            } else {
                routedGeometry = await fetchRouteFromOSRM(routeData.coordinates);
                cachedGeometries[rIdx] = routedGeometry;
            }
            var routedLatLngs = routedGeometry.map(function (c) { return [c.lat, c.lng]; });

            var polyline = L.polyline(routedLatLngs, {
                color: color,
                weight: 4,
                opacity: 0.8
            });

            var popupHtml = '<div class="circuit-pin-popup" style="min-width: 200px;">' +
                '<div class="circuit-pin-line circuit-pin-swu" style="border-bottom:1px solid rgba(255,255,255,0.12);padding-bottom:4px;margin-bottom:4px;color:' + color + '">Phase ' + escapeHtml(routeData.phase) + '</div>' +
                '<div class="circuit-pin-line"><span class="circuit-pin-label">Route:</span> ' + escapeHtml(routeData.route) + '</div>' +
                '<div class="circuit-pin-line"><span class="circuit-pin-label">Points:</span> ' + routeData.coordinates.length + ' coordinates</div>' +
                '</div>';

            polyline.bindPopup(popupHtml);
            
            polyline.parentLayerGroup = polylinesLayer;
            if (!phaseLayersMap[routeData.phase]) {
                phaseLayersMap[routeData.phase] = [];
            }
            phaseLayersMap[routeData.phase].push(polyline);

            if (!hiddenPhases[routeData.phase]) {
                polylinesLayer.addLayer(polyline);
            }

            // Draw marker pins for the ORIGINAL coordinates
            routeData.coordinates.forEach(function (c, i) {
                var isEnd = (i === 0 || i === routeData.coordinates.length - 1);
                var marker = L.circleMarker([c.lat, c.lng], {
                    radius: isEnd ? 6 : 4,
                    fillColor: isEnd ? '#ffffff' : color,
                    color: color,
                    weight: 2,
                    opacity: 1,
                    fillOpacity: 1
                });

                var pointLabel = '';
                if (i === 0) pointLabel = 'Start Point';
                else if (i === routeData.coordinates.length - 1) pointLabel = 'End Point';
                else pointLabel = 'Point ' + (i + 1) + ' of ' + routeData.coordinates.length;

                var markerPopup = '<div class="circuit-pin-popup">' +
                    '<div class="circuit-pin-line circuit-pin-swu" style="color:' + color + '">Phase ' + escapeHtml(routeData.phase) + '</div>' +
                    '<div class="circuit-pin-line">' + pointLabel + '</div>' +
                    '<div class="circuit-pin-line" style="font-size:0.72rem;opacity:0.65">' + c.lat.toFixed(7) + ', ' + c.lng.toFixed(7) + '</div>' +
                    '</div>';
                marker.bindPopup(markerPopup);
                
                marker.parentLayerGroup = markersLayer;
                phaseLayersMap[routeData.phase].push(marker);

                if (!hiddenPhases[routeData.phase]) {
                    markersLayer.addLayer(marker);
                }

                allBounds.push(L.latLng(c.lat, c.lng));
            });

            // Add directional arrows in the middle and quarter points of the routed path
            if (routedGeometry.length > 2) {
                var steps = [
                    Math.floor(routedGeometry.length * 0.25),
                    Math.floor(routedGeometry.length * 0.5),
                    Math.floor(routedGeometry.length * 0.75)
                ];

                steps.forEach(function (idx) {
                    if (idx >= 0 && idx < routedGeometry.length - 1) {
                        var p1 = routedGeometry[idx];
                        var p2 = routedGeometry[idx + 1];
                        var bearing = getBearing(p1, p2);

                        var arrowIcon = L.divIcon({
                            className: 'route-arrow-icon',
                            html: '<div style="transform: rotate(' + (bearing - 90) + 'deg); color: ' + color + '; font-size: 18px; font-weight: bold; line-height: 1; text-shadow: 0 0 2px #111, 0 0 2px #111; width: 18px; height: 18px; display: flex; align-items: center; justify-content: center;">➤</div>',
                            iconSize: [18, 18],
                            iconAnchor: [9, 9]
                        });

                        var arrowMarker = L.marker([p1.lat, p1.lng], { icon: arrowIcon, interactive: false });
                        
                        arrowMarker.parentLayerGroup = markersLayer;
                        phaseLayersMap[routeData.phase].push(arrowMarker);

                        if (!hiddenPhases[routeData.phase]) {
                            markersLayer.addLayer(arrowMarker);
                        }
                    }
                });
            }
        }

        var panel = document.getElementById('route-map-panel');
        if (panel) panel.classList.add('visible');

        if (allBounds.length > 0) {
            map.fitBounds(L.latLngBounds(allBounds), { padding: [50, 50], maxZoom: 18 });
        }

        // Save to cache after rendering (include resolved geometries)
        saveToCache(routes, uniquePhases, cachedGeometries);
    }

    function togglePhaseVisibility(phase, dot) {
        var isHidden = !hiddenPhases[phase];
        if (isHidden) {
            hiddenPhases[phase] = true;
        } else {
            delete hiddenPhases[phase];
        }

        var layers = phaseLayersMap[phase] || [];
        layers.forEach(function (layer) {
            if (isHidden) {
                if (layer.parentLayerGroup) {
                    layer.parentLayerGroup.removeLayer(layer);
                }
            } else {
                if (layer.parentLayerGroup) {
                    layer.parentLayerGroup.addLayer(layer);
                }
            }
        });

        var item = dot.closest('.circuit-map-legend-item');
        if (item) {
            if (isHidden) {
                item.style.opacity = '0.35';
                dot.setAttribute('title', 'Click to show phase');
            } else {
                item.style.opacity = '1';
                dot.setAttribute('title', 'Click to hide phase');
            }
        }
    }

    function saveToServer(cacheData) {
        fetch('/Report/SaveRouteMapData', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(cacheData)
        })
        .then(function (r) {
            if (!r.ok) console.error('Failed to save route map data to server');
        })
        .catch(function (err) {
            console.error('Error saving route map data to server:', err);
        });
    }

    function saveToCache(routes, uniquePhases, geometries) {
        try {
            var cacheData = {
                routes: routes,
                uniquePhases: uniquePhases,
                geometries: geometries
            };
            localStorage.setItem(CACHE_KEY, JSON.stringify(cacheData));
            saveToServer(cacheData);
        } catch (e) {
            console.warn('Failed to save route map data to localStorage:', e);
        }
    }

    function loadFromCache() {
        fetch('/Report/GetRouteMapData')
            .then(function (r) {
                if (r.ok) return r.json();
                throw new Error('Not found on server');
            })
            .then(function (cacheData) {
                if (cacheData && cacheData.routes && cacheData.routes.length) {
                    renderRoutes(cacheData.routes, cacheData.uniquePhases, cacheData.geometries || {});
                }
            })
            .catch(function (err) {
                console.log("Failed to load route map from server, checking local storage...", err);
                var cached = localStorage.getItem(CACHE_KEY);
                if (cached) {
                    try {
                        var cacheData = JSON.parse(cached);
                        if (cacheData && cacheData.routes && cacheData.routes.length) {
                            renderRoutes(cacheData.routes, cacheData.uniquePhases, cacheData.geometries || {});
                        }
                    } catch (err2) {
                        console.error('Failed to parse cached route map data:', err2);
                    }
                }
            });
    }

    function invalidateSize() {
        if (map) {
            setTimeout(function () { map.invalidateSize(); }, 200);
        }
    }

    global.RouteMap = {
        init: init,
        invalidateSize: invalidateSize
    };

})(window);
