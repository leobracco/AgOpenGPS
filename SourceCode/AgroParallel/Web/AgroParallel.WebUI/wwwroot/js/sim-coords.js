// ============================================================================
// sim-coords.js
// Reemplazo HTML del WinForms FormSimCoords. Reubica el simulador a una lat/lon
// (teletransporte). Lee el estado inicial de GET /api/aog/sim-coords (snake_case
// AgpJson: latitude, longitude, sim_on, job_started) y aplica por
// POST /api/aog/guidance/command con cmd = sim_coords_<lat>_<lon>.
// Sólo se puede aplicar con el simulador encendido y sin lote abierto (mismos
// guards que el OK del formulario original; el backend los revalida igual).
// ============================================================================

(function () {
  'use strict';

  var inLat    = document.getElementById('inLat');
  var inLon    = document.getElementById('inLon');
  var btnApply = document.getElementById('btnApply');
  var btnReset = document.getElementById('btnReset');
  var warnBox  = document.getElementById('warnBox');
  var statusEl = document.getElementById('statusText');

  // Estado del backend (para saber si se puede aplicar).
  var simOn = false, jobStarted = false;
  var savedLat = 0, savedLon = 0;

  // Normaliza coma decimal → punto (el teclado numérico ofrece ambas).
  function parseNum(raw) {
    if (raw == null) return NaN;
    return Number(String(raw).trim().replace(',', '.'));
  }

  function fmt(n) {
    // Hasta 7 decimales, sin ceros de más ni separador de miles.
    return Number(n).toFixed(7).replace(/\.?0+$/, '');
  }

  function validState() {
    var lat = parseNum(inLat.value);
    var lon = parseNum(inLon.value);
    var latOk = isFinite(lat) && lat >= -90 && lat <= 90;
    var lonOk = isFinite(lon) && lon >= -180 && lon <= 180;
    inLat.classList.toggle('bad', !latOk && inLat.value !== '');
    inLon.classList.toggle('bad', !lonOk && inLon.value !== '');
    return { lat: lat, lon: lon, ok: latOk && lonOk };
  }

  // Warning + habilitación del botón según guards del backend + validez de campos.
  function refreshApply() {
    var v = validState();
    var reason = '';
    if (jobStarted) reason = 'Cerrá el lote abierto antes de reubicar el simulador.';
    else if (!simOn) reason = 'Encendé el simulador para poder reubicarlo.';

    if (reason) { warnBox.textContent = reason; warnBox.hidden = false; }
    else { warnBox.hidden = true; }

    btnApply.disabled = !!reason || !v.ok;
  }

  // ── Estado inicial ────────────────────────────────────────────────────────
  async function loadState() {
    try {
      var r = await fetch('/api/aog/sim-coords', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();
      savedLat = Number(d.latitude) || 0;
      savedLon = Number(d.longitude) || 0;
      simOn = !!d.sim_on;
      jobStarted = !!d.job_started;
      inLat.value = fmt(savedLat);
      inLon.value = fmt(savedLon);
      statusEl.textContent = 'en vivo';
      refreshApply();
    } catch (e) {
      statusEl.textContent = 'sin conexión';
      warnBox.textContent = 'Sin conexión con PilotX.';
      warnBox.hidden = false;
      btnApply.disabled = true;
    }
  }

  async function apply() {
    var v = validState();
    if (!v.ok) return;
    var cmd = 'sim_coords_' + fmt(v.lat) + '_' + fmt(v.lon);
    btnApply.disabled = true;
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (data.ok) {
        statusEl.textContent = 'aplicado';
      } else {
        statusEl.textContent = 'rechazado';
        console.warn('[sim-coords] comando rechazado:', cmd, data.error);
        // Reconsultar por si cambió sim_on/job_started en el backend.
        await loadState();
      }
    } catch (e) {
      statusEl.textContent = 'sin conexión';
      console.warn('[sim-coords] sin conexión:', e.message);
    } finally {
      refreshApply();
    }
  }

  // ── Controles ─────────────────────────────────────────────────────────────
  inLat.addEventListener('input', refreshApply);
  inLon.addEventListener('input', refreshApply);
  btnApply.addEventListener('click', apply);
  btnReset.addEventListener('click', function () {
    inLat.value = fmt(savedLat);
    inLon.value = fmt(savedLon);
    refreshApply();
  });

  loadState();
})();
