// ============================================================================
// ab-rapido.js
// Reemplazo HTML de FormQuickAB: crea guías manejando en tres modos —
// Curva (grabar puntos), Línea AB (A + B) y A+ (A + rumbo manual).
//   GET  /api/quickab/state          (pollea a ~500 ms: replica timer1 nativo,
//                                     el punto B del preview sigue al tractor)
//   POST /api/quickab/{start,side,mark-a,mark-b,pause,heading,commit,save,cancel}
// El preview se dibuja sobre el mapa de PilotX; acá solo mandamos comandos.
// Si el operario cierra la ventana a mitad de sesión → /cancel (sendBeacon),
// igual que cerrar el WinForm nativo sin guardar.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/quickab';

  function $(id) { return document.getElementById(id); }

  var statusEl = $('statusText');
  var warnBox = $('warnBox');

  var paneChoose = $('paneChoose'), paneCapture = $('paneCapture'), paneName = $('paneName');
  var valInfo = $('valInfo'), hintCapture = $('hintCapture');
  var btnSide = $('btnSide');
  var rowHeading = $('rowHeading'), inpHeading = $('inpHeading');
  var btnMarkA = $('btnMarkA'), btnMarkB = $('btnMarkB');
  var rowPause = $('rowPause'), btnPause = $('btnPause');
  var rowCommit = $('rowCommit'), btnCommit = $('btnCommit');
  var inpName = $('inpName');

  var settled = false;     // true tras Guardar/Cancelar (no re-mandar en pagehide)
  var sessionOpen = false; // hay sesión en curso (capture/name)
  var lastState = null;

  function setStatus(t) { statusEl.textContent = t; }

  function show(el, on) { el.classList.toggle('hiddenTab', !on); }

  function applyState(s) {
    if (!s || s.ok === false) {
      setStatus('sin conexión');
      return;
    }
    lastState = s;
    warnBox.classList.toggle('show', !s.has_field);
    $('btnModeCurve').disabled = !s.has_field;
    $('btnModeAb').disabled = !s.has_field;
    $('btnModeAplus').disabled = !s.has_field;

    var phase = s.phase || 'choose';
    sessionOpen = phase !== 'choose';
    show(paneChoose, phase === 'choose');
    show(paneCapture, phase === 'capture');
    show(paneName, phase === 'name');

    btnSide.textContent = s.ref_right ? 'Derecha ▶' : '◀ Izquierda';

    if (phase === 'capture') {
      var mode = s.mode;
      show(rowHeading, mode === 'aplus');
      show(rowPause, mode === 'curve' && s.a_marked);
      show(rowCommit, (mode === 'ab' && s.a_marked) || (mode === 'aplus' && s.a_marked));

      if (mode === 'curve') {
        btnMarkA.textContent = s.a_marked ? '+ Punto' : '● Grabar A';
        btnMarkB.textContent = '■ Cerrar B';
        btnMarkB.disabled = !s.a_marked;
        btnPause.textContent = s.recording ? '⏸ Pausar' : '▶ Reanudar';
        valInfo.textContent = s.points + ' pts';
        hintCapture.textContent = s.a_marked
          ? (s.recording ? 'Grabando la curva mientras manejás…' : 'Grabación en pausa')
          : 'Marcá A donde arranca la curva';
      } else if (mode === 'ab') {
        btnMarkA.textContent = s.a_marked ? '● A ✓' : '● Marcar A';
        btnMarkB.textContent = s.b_marked ? '● B ✓' : '● Marcar B';
        btnMarkA.disabled = s.a_marked;
        btnMarkB.disabled = !s.a_marked;
        valInfo.textContent = fmtHeading(s.heading_deg);
        hintCapture.textContent = s.a_marked
          ? 'Manejá y marcá B (o Listo: B sigue al tractor)'
          : 'Marcá A donde arranca la línea';
      } else { // aplus
        btnMarkA.textContent = s.a_marked ? '● A ✓' : '● Marcar A';
        btnMarkB.disabled = true;
        btnMarkB.textContent = '—';
        valInfo.textContent = fmtHeading(s.heading_deg);
        hintCapture.textContent = s.a_marked
          ? 'Fijá el rumbo (o dejá el del GPS) y tocá Listo'
          : 'Marcá A y fijá el rumbo';
        if (document.activeElement !== inpHeading)
          inpHeading.value = (Math.round(s.heading_deg * 10) / 10).toFixed(1);
      }
    }

    if (phase === 'name' && document.activeElement !== inpName && !inpName.value)
      inpName.value = s.suggested_name || '';

    setStatus(s.has_field ? (phase === 'choose' ? 'listo' : 'en vivo') : 'sin lote');
    if (s.error) setStatus(s.error);
  }

  function fmtHeading(deg) {
    return (Math.round(deg * 10) / 10).toFixed(1) + '°';
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

  // ── Elegir modo ────────────────────────────────────────────────────────────
  $('btnModeCurve').addEventListener('click', async function () { applyState(await post('/start', { mode: 'curve' })); });
  $('btnModeAb').addEventListener('click', async function () { applyState(await post('/start', { mode: 'ab' })); });
  $('btnModeAplus').addEventListener('click', async function () { applyState(await post('/start', { mode: 'aplus' })); });

  // ── Captura ────────────────────────────────────────────────────────────────
  btnSide.addEventListener('click', async function () { applyState(await post('/side')); });
  btnMarkA.addEventListener('click', async function () { applyState(await post('/mark-a')); });
  btnMarkB.addEventListener('click', async function () { applyState(await post('/mark-b')); });
  btnPause.addEventListener('click', async function () { applyState(await post('/pause')); });
  btnCommit.addEventListener('click', async function () { applyState(await post('/commit')); });

  inpHeading.addEventListener('change', async function () {
    var v = parseFloat(String(inpHeading.value).replace(',', '.'));
    if (isFinite(v)) applyState(await post('/heading', { value: v }));
  });

  $('btnCancelCapture').addEventListener('click', async function () {
    applyState(await post('/cancel'));
  });

  // ── Nombre + guardar ───────────────────────────────────────────────────────
  $('btnSave').addEventListener('click', async function () {
    var s = await post('/save', { name: inpName.value });
    if (s && s.ok !== false && !s.error) { applyState(s); closeWidget(); }
    else applyState(s);
  });
  $('btnCancelName').addEventListener('click', async function () {
    inpName.value = '';
    applyState(await post('/cancel'));
  });

  // Cierre de ventana con sesión abierta → cancelar (limpia el preview del mapa).
  window.addEventListener('pagehide', function () {
    if (settled || !sessionOpen) return;
    try {
      var blob = new Blob(['{}'], { type: 'application/json' });
      if (navigator.sendBeacon) navigator.sendBeacon(API + '/cancel', blob);
      else fetch(API + '/cancel', { method: 'POST', keepalive: true, body: '{}' });
    } catch (e) { /* best-effort */ }
  });

  // ── Arranque ───────────────────────────────────────────────────────────────
  (async function () {
    applyState(await get('/state'));
    // 500 ms: replica el timer1 nativo — el punto B del preview sigue al tractor.
    setInterval(async function () { applyState(await get('/state')); }, 500);
  })();
})();
