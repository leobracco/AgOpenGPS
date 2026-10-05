// ============================================================================
// stormx.js — UI de StormX (estación meteorológica móvil).
// Conecta con /api/stormx/config (CRUD umbrales+nodos) y /api/stormx/nodos.
// Mientras el firmware StormX no publique MQTT, los KPIs van a quedar en "—",
// pero el wiring de la página queda listo para activarse el día que aparezcan
// los topics agp/storm/{uid}/status_live.
// ============================================================================

(function () {
  'use strict';

  var kpiWind   = document.getElementById('kpiWind');
  var kpiDir    = document.getElementById('kpiDir');
  var kpiTemp   = document.getElementById('kpiTemp');
  var kpiDeltaT = document.getElementById('kpiDeltaT');
  var kpiHum    = document.getElementById('kpiHum');
  var kpiPress  = document.getElementById('kpiPress');
  var kpiAdvice = document.getElementById('kpiAdvice');
  var kpiAge    = document.getElementById('kpiAge');
  var okPill    = document.getElementById('okPill');
  var nodosList = document.getElementById('nodosList');

  var cfg = null;
  var nodos = [];
  var lastSample = null;
  var lastSampleAt = 0;

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }
  function fmtNum(v, decimals) {
    if (v == null || isNaN(v)) return '—';
    return Number(v).toFixed(decimals || 0);
  }

  // Verdict según umbrales operativos. Devuelve { ok, label, color }.
  function verdict(sample, lim) {
    if (!sample || !lim) return { ok: null, label: '—', color: 'var(--agp-text-muted)' };
    var wind = sample.wind_ms;
    var temp = sample.temp_c;
    var hum  = sample.hum_pct;
    var dt   = sample.delta_t_c;
    var fails = [];
    if (wind != null && wind > lim.wind_max_ms) fails.push('viento alto');
    if (wind != null && wind < lim.wind_min_ms) fails.push('viento muy bajo');
    if (hum  != null && hum  < lim.hum_min_pct) fails.push('humedad baja');
    if (temp != null && temp > lim.temp_max_c)  fails.push('temp alta');
    if (dt   != null && dt   > lim.delta_t_max_c) fails.push('Δ-T alto');
    if (fails.length === 0) return { ok: true, label: 'PULVERIZAR OK', color: 'var(--agp-state-ok)' };
    return { ok: false, label: 'NO PULVERIZAR · ' + fails.join(', '), color: 'var(--agp-state-warn,#E0A93C)' };
  }

  function ageStr(ts) {
    if (!ts) return '—';
    var s = Math.max(0, Math.floor((Date.now() - ts) / 1000));
    if (s < 60) return s + 's atrás';
    if (s < 3600) return Math.floor(s / 60) + ' min atrás';
    return Math.floor(s / 3600) + ' h atrás';
  }

  function renderSample() {
    var lim = cfg && cfg.limits ? cfg.limits : null;
    var s = lastSample;
    if (kpiWind)   kpiWind.textContent   = s ? fmtNum(s.wind_ms, 1) : '—';
    if (kpiDir)    kpiDir.textContent    = s && s.wind_dir != null ? fmtNum(s.wind_dir, 0) + ' °' : '— °';
    if (kpiTemp)   kpiTemp.textContent   = s ? fmtNum(s.temp_c, 1) : '—';
    if (kpiDeltaT) kpiDeltaT.textContent = s && s.delta_t_c != null ? fmtNum(s.delta_t_c, 1) + ' °C' : '— °C';
    if (kpiHum)    kpiHum.textContent    = s ? fmtNum(s.hum_pct, 0) : '—';
    if (kpiPress)  kpiPress.textContent  = s && s.press_hpa != null ? fmtNum(s.press_hpa, 0) + ' hPa' : '— hPa';

    var v = verdict(s, lim);
    if (kpiAdvice) {
      kpiAdvice.textContent = v.label;
      kpiAdvice.style.color = v.color;
    }
    if (okPill) {
      okPill.style.color = v.color;
      var dot = okPill.querySelector('.dot');
      if (dot) dot.style.background = v.color;
      // Texto del pill (sin tocar el dot).
      var lbl = v.ok === null ? 'Condiciones —' : (v.ok ? 'Condiciones OK' : 'Condiciones malas');
      // El pill tiene <span class="dot"></span> + texto adyacente.
      // Reemplazamos sólo el textNode posterior al dot.
      var node = okPill.lastChild;
      if (node && node.nodeType === 3) node.nodeValue = ' ' + lbl;
      else okPill.appendChild(document.createTextNode(' ' + lbl));
    }
    if (kpiAge) kpiAge.textContent = ageStr(lastSampleAt);
  }

  function renderNodos() {
    if (!nodosList) return;
    if (!nodos || nodos.length === 0) {
      nodosList.innerHTML = '<div class="subtitle">' +
        'Buscando nodos en LAN... La estación StormX publica telemetría en ' +
        '<code>agp/storm/&lt;uid&gt;/status_live</code> al broker MQTT del PC.' +
        '</div>';
      return;
    }
    var rows = nodos.map(function (n) {
      var dot = n.online ? '●' : '○';
      var color = n.online ? 'var(--agp-state-ok)' : 'var(--agp-text-muted)';
      return '<div class="row" style="padding: var(--agp-sp-2) 0">' +
              '<span style="color:' + color + '">' + dot + '</span> ' +
              '<strong style="color:var(--agp-text); margin-left:8px">' + escapeHtml(n.uid || '—') + '</strong>' +
              '<span class="subtitle" style="margin-left:8px">' + escapeHtml(n.ip || '') + ' · fw ' + escapeHtml(n.firmware || '?') + '</span>' +
            '</div>';
    }).join('');
    nodosList.innerHTML = rows;
  }

  async function loadCfg() {
    try {
      var res = await fetch('/api/stormx/config', { cache: 'no-store' });
      cfg = await res.json();
    } catch (e) { cfg = null; }
  }
  async function pollNodos() {
    try {
      var res = await fetch('/api/stormx/nodos', { cache: 'no-store' });
      var body = await res.json();
      nodos = (body && body.nodos) ? body.nodos : [];
      renderNodos();
    } catch (e) { /* offline */ }
  }

  // Consumir telemetría live del StormXLiveService. El endpoint devuelve un
  // snapshot por nodo + limits efectivos. Tomamos el primer nodo online como
  // "lectura activa" — la página actual muestra un solo set de KPIs.
  async function pollLive() {
    try {
      var res = await fetch('/api/stormx/live', { cache: 'no-store' });
      var body = await res.json();
      if (body && body.limits && !cfg) cfg = { limits: body.limits };
      if (body && body.nodos && body.nodos.length > 0) {
        var n = body.nodos.find(function (x) { return x.online; }) || body.nodos[0];
        if (n && n.online) {
          lastSample = {
            wind_ms: n.wind_ms,
            wind_dir: n.wind_dir,
            temp_c: n.temp_c,
            hum_pct: n.hum_pct,
            press_hpa: n.press_hpa,
            delta_t_c: n.delta_t_c
          };
          lastSampleAt = Date.now();
        }
      }
    } catch (e) { /* offline */ }
  }

  async function tick() { await pollLive(); renderSample(); }

  var tickHandle = null;
  var pollNodosHandle = null;
  function startPolling() {
    if (!tickHandle) tickHandle = setInterval(tick, 1000);
    if (!pollNodosHandle) pollNodosHandle = setInterval(pollNodos, 3000);
  }
  function stopPolling() {
    if (tickHandle) { clearInterval(tickHandle); tickHandle = null; }
    if (pollNodosHandle) { clearInterval(pollNodosHandle); pollNodosHandle = null; }
  }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stopPolling();
    else { tick(); pollNodos(); startPolling(); }
  });

  (async function init() {
    await loadCfg();
    await pollNodos();
    renderSample();
    startPolling();
  })();
})();
