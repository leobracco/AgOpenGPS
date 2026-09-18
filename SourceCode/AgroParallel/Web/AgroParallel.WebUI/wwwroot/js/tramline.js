// ============================================================================
// tramline.js
// Reemplazo HTML del WinForms FormTram ("Tramlines simples"). Ajusta pasadas /
// modo / transparencia / swap AB de las huellas de rueda. La vista previa la
// dibuja PilotX sobre el mapa principal (tram.displayMode). Acá solo mandamos
// comandos y refrescamos el panel con el estado que devuelve C#:
//   GET  /api/tram-simple/state           → Open (fija modo + construye) + estado
//   POST /api/tram-simple/passes {passes} → cambia pasadas + reconstruye
//   POST /api/tram-simple/alpha  {percent}→ transparencia
//   POST /api/tram-simple/mode   {mode}   → modo de generación + reconstruye
//   POST /api/tram-simple/swap            → invierte A↔B + reconstruye
//   POST /api/tram-simple/commit {save}   → guardar / descartar
// Si el operario sale sin guardar, mandamos commit{save:false} para no dejar la
// preview colgada en el mapa (igual que suavizar-ab).
// ============================================================================

(function () {
  'use strict';

  var API = '/api/tram-simple';

  // Modo: valor C# ↔ etiqueta ↔ ciclo del botón.
  var MODES = ['All', 'FillTracks', 'BoundaryTracks'];
  var MODE_LABEL = { All: 'Todo', FillTracks: 'Relleno', BoundaryTracks: 'Contorno' };

  var warnBox  = document.getElementById('warnBox');
  var statusEl = document.getElementById('statusText');
  var btnLess  = document.getElementById('btnLess');
  var btnMore  = document.getElementById('btnMore');
  var valPasses= document.getElementById('valPasses');
  var btnMode  = document.getElementById('btnMode');
  var rngAlpha = document.getElementById('rngAlpha');
  var valAlpha = document.getElementById('valAlpha');
  var btnSwap  = document.getElementById('btnSwap');
  var valTool  = document.getElementById('valTool');
  var valTram  = document.getElementById('valTram');
  var valTrack = document.getElementById('valTrack');
  var btnCancel= document.getElementById('btnCancel');
  var btnSave  = document.getElementById('btnSave');

  var passes = 1;
  var mode = 'All';
  var units = 'm';
  var hasBoundary = false;
  var settled = false; // true tras guardar/cancelar (no re-mandar discard al salir)
  var lastOk = true;

  function setStatus(txt) { statusEl.textContent = txt; }

  function fmt(v) { return (Math.round(v * 100) / 100).toFixed(2) + ' ' + units; }

  function render() {
    valPasses.textContent = passes;
    btnMode.textContent = MODE_LABEL[mode] || mode;
    btnMode.disabled = !hasBoundary;
    valAlpha.textContent = rngAlpha.value + '%';
  }

  function applyState(s) {
    if (!s || s.ok === false) {
      setStatus('sin conexión');
      warnBox.textContent = 'Sin conexión con PilotX.';
      warnBox.classList.add('show');
      return;
    }
    if (!s.has_track) {
      warnBox.textContent = 'Activá primero una línea de guía (AB o curva) para armar los tramlines.';
      warnBox.classList.add('show');
      btnLess.disabled = btnMore.disabled = btnMode.disabled = true;
      btnSwap.disabled = btnSave.disabled = rngAlpha.disabled = true;
      setStatus('sin guía');
      return;
    }
    warnBox.classList.remove('show');
    passes = s.passes;
    mode = s.mode;
    units = s.units || 'm';
    hasBoundary = !!s.has_boundary;
    rngAlpha.value = s.alpha_percent;
    valTool.textContent = fmt(s.tool_width_display);
    valTram.textContent = fmt(s.tram_width_display);
    valTrack.textContent = fmt(s.track_width_display);
    setStatus(s.is_curve ? 'curva · en vivo' : 'AB · en vivo');
    render();
  }

  async function get(path) {
    try {
      var res = await fetch(API + path);
      var data = await res.json();
      if (!lastOk) { lastOk = true; }
      return data;
    } catch (e) {
      if (lastOk) { setStatus('sin conexión'); lastOk = false; }
      return null;
    }
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      var data = await res.json();
      if (!lastOk) { lastOk = true; }
      return data;
    } catch (e) {
      if (lastOk) { setStatus('sin conexión'); lastOk = false; }
      return null;
    }
  }

  // ── Controles ───────────────────────────────────────────────────────────────
  btnLess.addEventListener('click', async function () {
    applyState(await post('/passes', { passes: Math.max(1, passes - 1) }));
  });
  btnMore.addEventListener('click', async function () {
    applyState(await post('/passes', { passes: passes + 1 }));
  });

  btnMode.addEventListener('click', async function () {
    var i = MODES.indexOf(mode);
    var next = MODES[(i + 1) % MODES.length];
    applyState(await post('/mode', { mode: next }));
  });

  // Alpha: no reconstruye geometría, solo redibuja → mandamos al soltar/mover.
  var alphaTimer = null;
  rngAlpha.addEventListener('input', function () {
    valAlpha.textContent = rngAlpha.value + '%';
    if (alphaTimer) clearTimeout(alphaTimer);
    alphaTimer = setTimeout(function () {
      post('/alpha', { percent: parseInt(rngAlpha.value, 10) });
    }, 120);
  });

  btnSwap.addEventListener('click', async function () {
    setStatus('invirtiendo…');
    applyState(await post('/swap', {}));
  });

  btnCancel.addEventListener('click', async function () {
    var data = await post('/commit', { save: false });
    if (data && data.ok) { settled = true; setStatus('cancelado'); }
  });
  btnSave.addEventListener('click', async function () {
    var data = await post('/commit', { save: true });
    if (data && data.ok) { settled = true; setStatus('guardado'); }
  });

  // Al salir sin guardar: descartar la preview para no dejarla colgada en el mapa.
  window.addEventListener('pagehide', function () {
    if (settled) return;
    try {
      var body = JSON.stringify({ save: false });
      if (navigator.sendBeacon) {
        navigator.sendBeacon(API + '/commit',
          new Blob([body], { type: 'application/json' }));
      } else {
        fetch(API + '/commit', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: body, keepalive: true
        });
      }
    } catch (e) { /* best-effort */ }
  });

  // ── Arranque ──────────────────────────────────────────────────────────────
  (async function () {
    applyState(await get('/state'));
  })();
})();
