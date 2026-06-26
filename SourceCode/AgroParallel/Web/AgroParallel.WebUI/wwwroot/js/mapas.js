// ============================================================================
// mapas.js — preview de mapas del lote (Hub).
//
// Capas (todas toggleables):
//   - Heatmap VistaX (polígonos coloreados por "clase" 1-4)
//   - Puntos por surco VistaX (círculos chicos)
//   - Boundary del lote (línea blanca)
//   - Cabecera/Headland (línea gris)
//
// Sin tiles online — fondo plano #16181A (vía CSS). El zoom/pan funciona igual
// porque Leaflet sigue manejando la proyección Web Mercator internamente.
//
// Endpoints consumidos:
//   GET /api/mapas/sesiones
//   GET /api/mapas/sesion/{ts}/heatmap
//   GET /api/mapas/sesion/{ts}/puntos
//   GET /api/mapas/boundary
//   GET /api/mapas/headland
// ============================================================================
(function () {
  'use strict';

  if (typeof L === 'undefined') {
    console.error('[mapas] Leaflet no cargó. Falta vendor/leaflet/leaflet.js — ver README.');
    var el = document.getElementById('mapaLeaflet');
    if (el) el.innerHTML = '<div class="empty">Leaflet no está instalado.<br/>Ver <code>wwwroot/vendor/leaflet/README.txt</code>.</div>';
    return;
  }

  // ---------- Estado ----------
  var state = {
    map: null,
    layers: { heatmap: null, puntos: null, boundary: null, headland: null },
    sesionActual: null,
    loteBbox: null,
    sesionBbox: null
  };

  // ---------- Mapa ----------
  function initMap() {
    // crs: standard EPSG:3857. Sin tileLayer → fondo CSS plano.
    var map = L.map('mapaLeaflet', {
      preferCanvas: true,   // mejor performance con miles de polígonos
      zoomControl: true,
      attributionControl: false,
      worldCopyJump: false
    }).setView([0, 0], 2);
    state.map = map;
  }

  // ---------- Estilos ----------
  function colorClase(c) {
    switch (Number(c)) {
      case 1: return '#2ecc40';
      case 2: return '#ffdc00';
      case 3: return '#ff851b';
      case 4: return '#ff4136';
      default: return '#7d8b80';
    }
  }

  function styleHeatmap(feature) {
    var clase = (feature.properties && feature.properties.clase) || 0;
    var col = colorClase(clase);
    return {
      color: col,
      weight: 0,            // sin borde — celdas se ven contiguas
      fillColor: col,
      fillOpacity: 0.75
    };
  }

  function styleBoundary() {
    return { color: '#ffffff', weight: 2, opacity: 0.9, fillOpacity: 0 };
  }

  function styleHeadland() {
    return { color: '#7d8b80', weight: 1.5, opacity: 0.8, dashArray: '4 4', fillOpacity: 0 };
  }

  function pointToCircle(feature, latlng) {
    var alerta = feature.properties && feature.properties.alerta;
    var spm = feature.properties && feature.properties.spm;
    var col = alerta ? '#ff4136' : '#4BA63F';
    return L.circleMarker(latlng, {
      radius: 2,
      color: col,
      weight: 0,
      fillColor: col,
      fillOpacity: 0.85
    }).bindTooltip(
      'surco ' + (feature.properties.surco || '–') + ' · ' +
      'SPM ' + (spm != null ? Number(spm).toFixed(0) : '–'),
      { sticky: true }
    );
  }

  // ---------- Loaders ----------
  async function fetchJsonOrNull(url) {
    try {
      var r = await fetch(url, { cache: 'no-store' });
      if (!r.ok) return null;
      return await r.json();
    } catch (e) { return null; }
  }

  async function loadSesiones() {
    var data = await fetchJsonOrNull('/api/mapas/sesiones');
    var sel = document.getElementById('mpSesion');
    var meta = document.getElementById('mpSesionMeta');
    document.getElementById('mpLote').textContent =
      'Lote · ' + (data && data.lote ? data.lote : '–');

    if (!data || !data.sesiones || data.sesiones.length === 0) {
      sel.innerHTML = '<option value="">— sin sesiones —</option>';
      meta.textContent = 'No hay sesiones VistaX para este lote.';
      state.sesionActual = null;
      return;
    }

    sel.innerHTML = data.sesiones.map(function (s) {
      var lbl = s.fechaIso || s.ts;
      return '<option value="' + s.ts + '">' + lbl + '</option>';
    }).join('');

    // Default = la más reciente (primera en la lista, viene ordenada desc).
    sel.value = data.sesiones[0].ts;
    state.sesionActual = data.sesiones[0];
    updateSesionMeta();
  }

  function updateSesionMeta() {
    var meta = document.getElementById('mpSesionMeta');
    var s = state.sesionActual;
    if (!s) { meta.textContent = '–'; return; }
    var parts = [];
    if (s.hasHeatmap) parts.push(s.heatmapCeldas + ' celdas heatmap');
    if (s.hasPuntos) parts.push(s.puntos + ' puntos');
    meta.textContent = parts.length ? parts.join(' · ') : 'sin datos exportados';
  }

  async function loadLayer(name, url, builder) {
    // Limpia capa previa.
    if (state.layers[name]) {
      state.map.removeLayer(state.layers[name]);
      state.layers[name] = null;
    }
    if (!url) return null;
    var data = await fetchJsonOrNull(url);
    if (!data || !data.features || data.features.length === 0) return null;

    var layer = builder(data);
    state.layers[name] = layer;
    // Solo agrega al mapa si el checkbox está marcado.
    if (isLayerEnabled(name)) layer.addTo(state.map);
    return data.bbox || null;
  }

  function buildHeatmapLayer(data) {
    return L.geoJSON(data, { style: styleHeatmap, renderer: L.canvas() });
  }
  function buildPuntosLayer(data) {
    return L.geoJSON(data, { pointToLayer: pointToCircle, renderer: L.canvas() });
  }
  function buildBoundaryLayer(data) {
    return L.geoJSON(data, { style: styleBoundary });
  }
  function buildHeadlandLayer(data) {
    return L.geoJSON(data, { style: styleHeadland });
  }

  function isLayerEnabled(name) {
    var map = { heatmap: 'layHeatmap', puntos: 'layPuntos', boundary: 'layBoundary', headland: 'layHeadland' };
    var el = document.getElementById(map[name]);
    return el ? el.checked : false;
  }

  function applyLayerToggle(name) {
    var layer = state.layers[name];
    if (!layer) return;
    if (isLayerEnabled(name)) {
      if (!state.map.hasLayer(layer)) layer.addTo(state.map);
    } else {
      if (state.map.hasLayer(layer)) state.map.removeLayer(layer);
    }
  }

  // ---------- Sesión load (heatmap + puntos) ----------
  async function loadSesionLayers(ts) {
    state.sesionBbox = null;
    if (!ts) {
      // Limpiar capas de sesión.
      if (state.layers.heatmap) { state.map.removeLayer(state.layers.heatmap); state.layers.heatmap = null; }
      if (state.layers.puntos)  { state.map.removeLayer(state.layers.puntos);  state.layers.puntos = null; }
      return;
    }
    var bboxH = await loadLayer('heatmap', '/api/mapas/sesion/' + encodeURIComponent(ts) + '/heatmap', buildHeatmapLayer);
    var bboxP = await loadLayer('puntos',  '/api/mapas/sesion/' + encodeURIComponent(ts) + '/puntos',  buildPuntosLayer);
    state.sesionBbox = mergeBbox(bboxH, bboxP);
  }

  // ---------- Lote (boundary + headland) ----------
  async function loadLoteLayers() {
    state.loteBbox = null;
    var bboxB = await loadLayer('boundary', '/api/mapas/boundary', buildBoundaryLayer);
    var bboxH = await loadLayer('headland', '/api/mapas/headland', buildHeadlandLayer);
    state.loteBbox = mergeBbox(bboxB, bboxH);
  }

  function mergeBbox(a, b) {
    if (!a) return b;
    if (!b) return a;
    return [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[2], b[2]), Math.max(a[3], b[3])];
  }

  function fitToBbox(bbox) {
    if (!bbox) return;
    // bbox = [minLon, minLat, maxLon, maxLat] → Leaflet [[lat,lon],[lat,lon]]
    state.map.fitBounds([[bbox[1], bbox[0]], [bbox[3], bbox[2]]], { padding: [20, 20] });
  }

  // ---------- Wiring ----------
  function wireUi() {
    document.getElementById('mpSesion').addEventListener('change', async function (e) {
      var ts = e.target.value;
      // Update sesion actual desde la lista cargada (necesitamos la meta).
      var data = await fetchJsonOrNull('/api/mapas/sesiones');
      state.sesionActual = (data && data.sesiones || []).filter(function (s) { return s.ts === ts; })[0] || null;
      updateSesionMeta();
      await loadSesionLayers(ts);
      fitToBbox(state.sesionBbox || state.loteBbox);
    });

    ['layHeatmap', 'layPuntos', 'layBoundary', 'layHeadland'].forEach(function (id) {
      document.getElementById(id).addEventListener('change', function () {
        var name = id.replace(/^lay/, '').toLowerCase();
        applyLayerToggle(name);
      });
    });

    document.getElementById('mpFit').addEventListener('click', function () {
      fitToBbox(state.loteBbox || state.sesionBbox);
    });
    document.getElementById('mpFitSesion').addEventListener('click', function () {
      fitToBbox(state.sesionBbox || state.loteBbox);
    });
  }

  // ---------- Boot ----------
  async function boot() {
    initMap();
    wireUi();
    await loadLoteLayers();
    await loadSesiones();
    if (state.sesionActual) await loadSesionLayers(state.sesionActual.ts);
    // Fit inicial: prioridad a la sesión activa, fallback al lote.
    fitToBbox(state.sesionBbox || state.loteBbox);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
