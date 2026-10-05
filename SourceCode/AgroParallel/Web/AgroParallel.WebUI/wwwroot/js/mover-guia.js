// ============================================================================
// mover-guia.js
// Reemplazo HTML de FormNudge (guía activa) y FormRefNudge (referencia).
// El movimiento se ve sobre el mapa de PilotX; acá solo mandamos comandos y
// refrescamos con el estado que devuelve C#:
//   GET  /api/nudge/state          POST /api/nudge/{move,half,zero,pivot,step,close}
//   POST /api/nudge/ref/{open,move,half,step,save,cancel}
// Semántica nativa: cerrar la solapa Guía persiste (FormClosing → FileSaveTracks);
// en Referencia, Guardar persiste y Cancelar restaura el backup. Si el operario
// cierra la ventana sin decidir, replicamos btnExit nativo (ref/save) o /close.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/nudge';

  function $(id) { return document.getElementById(id); }

  var statusEl = $('statusText');
  var warnBox = $('warnBox');

  var tabTrack = $('tabTrack'), tabRef = $('tabRef');
  var paneTrack = $('paneTrack'), paneRef = $('paneRef');

  var valOffset = $('valOffset'), inpStep = $('inpStep'), unitStep = $('unitStep');
  var valMoved = $('valMoved'), inpStepRef = $('inpStepRef'), unitStepRef = $('unitStepRef');

  var refMode = /[?&]tab=ref\b/.test(location.search);
  var refActive = false;
  var settled = false; // true tras Guardar/Cancelar/Cerrar (no re-mandar en pagehide)
  var units = 'cm';

  function setStatus(t) { statusEl.textContent = t; }

  // "‹ 12 cm" / "12 cm ›" / "0 cm" — mismo formato que lblOffset nativo.
  function fmtOffset(v) {
    if (v < 0) return '\u2039 ' + (-v) + ' ' + units;
    if (v > 0) return v + ' ' + units + ' \u203A';
    return '0 ' + units;
  }

  function applyState(s) {
    if (!s || s.ok === false) {
      setStatus('sin conexión');
      return;
    }
    units = s.units || 'cm';
    unitStep.textContent = units;
    unitStepRef.textContent = units;

    warnBox.classList.toggle('show', !s.has_track);
    document.querySelectorAll('.ng-move .btn').forEach(function (b) {
      b.disabled = !s.has_track;
    });

    valOffset.textContent = fmtOffset(Number(s.offset_display));
    valMoved.textContent = fmtOffset(Number(s.ref_moved_display));

    // No pisar el input mientras el operario lo edita con el teclado.
    if (document.activeElement !== inpStep) inpStep.value = s.step_display;
    if (document.activeElement !== inpStepRef) inpStepRef.value = s.step_ref_display;

    refActive = !!s.ref_active;
    setStatus(s.has_track ? 'en vivo' : 'sin guía');
  }

  async function get(path) {
    try { return await (await fetch(API + path, { cache: 'no-store' })).json(); }
    catch (e) { setStatus('sin conexión'); return null; }
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      return await res.json();
    } catch (e) { setStatus('sin conexión'); return null; }
  }

  function closeWidget() {
    settled = true;
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv) wv.postMessage('close-hub');
    } catch (e) { /* fuera de WebView2: queda abierto */ }
  }

  // ── Solapas ────────────────────────────────────────────────────────────────
  function showTab(ref) {
    refMode = ref;
    tabTrack.classList.toggle('on', !ref);
    tabRef.classList.toggle('on', ref);
    paneTrack.classList.toggle('hiddenTab', ref);
    paneRef.classList.toggle('hiddenTab', !ref);
    if (ref) post('/ref/open').then(applyState); // abre sesión (backup) si no está
  }
  tabTrack.addEventListener('click', function () { showTab(false); });
  tabRef.addEventListener('click', function () { showTab(true); });

  // ── Guía activa ────────────────────────────────────────────────────────────
  $('btnLeft').addEventListener('click', async function () { applyState(await post('/move', { dir: -1 })); });
  $('btnRight').addEventListener('click', async function () { applyState(await post('/move', { dir: 1 })); });
  $('btnHalfLeft').addEventListener('click', async function () { applyState(await post('/half', { dir: -1 })); });
  $('btnHalfRight').addEventListener('click', async function () { applyState(await post('/half', { dir: 1 })); });
  $('btnZero').addEventListener('click', async function () { applyState(await post('/zero')); });
  $('btnPivot').addEventListener('click', async function () { applyState(await post('/pivot')); });

  inpStep.addEventListener('change', async function () {
    var v = parseFloat(String(inpStep.value).replace(',', '.'));
    if (isFinite(v) && v >= 0) applyState(await post('/step', { value: v }));
  });

  $('btnCloseTrack').addEventListener('click', async function () {
    await post('/close');
    closeWidget();
  });

  // ── Referencia ─────────────────────────────────────────────────────────────
  $('btnRefLeft').addEventListener('click', async function () { applyState(await post('/ref/move', { dir: -1 })); });
  $('btnRefRight').addEventListener('click', async function () { applyState(await post('/ref/move', { dir: 1 })); });
  $('btnRefHalfLeft').addEventListener('click', async function () { applyState(await post('/ref/half', { dir: -1 })); });
  $('btnRefHalfRight').addEventListener('click', async function () { applyState(await post('/ref/half', { dir: 1 })); });

  inpStepRef.addEventListener('change', async function () {
    var v = parseFloat(String(inpStepRef.value).replace(',', '.'));
    if (isFinite(v) && v >= 0) applyState(await post('/ref/step', { value: v }));
  });

  $('btnRefSave').addEventListener('click', async function () {
    await post('/ref/save');
    closeWidget();
  });
  $('btnRefCancel').addEventListener('click', async function () {
    await post('/ref/cancel');
    closeWidget();
  });

  // Cierre de ventana sin decidir: btnExit nativo guarda → ref/save si hay
  // sesión, /close (FileSaveTracks) si no. Best-effort con sendBeacon.
  window.addEventListener('pagehide', function () {
    if (settled) return;
    try {
      var path = refActive ? '/ref/save' : '/close';
      var blob = new Blob(['{}'], { type: 'application/json' });
      if (navigator.sendBeacon) navigator.sendBeacon(API + path, blob);
      else fetch(API + path, { method: 'POST', keepalive: true, body: '{}' });
    } catch (e) { /* best-effort */ }
  });

  // ── Arranque ───────────────────────────────────────────────────────────────
  (async function () {
    applyState(await get('/state'));
    if (refMode) showTab(true);
    // Poll liviano: el offset puede cambiar desde el mapa/las barras.
    setInterval(async function () { applyState(await get('/state')); }, 700);
  })();
})();
