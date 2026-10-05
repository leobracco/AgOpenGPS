// ============================================================================
// banderas.js
// Reemplazo HTML de FormFlags + FormEnterFlag: lista de banderas con distancia
// live, notas editables, alta por posición actual o lat/lon manual, e
// import/export CSV (diálogos nativos del lado PilotX).
//   GET  /api/flags/state   (pollea a ~500 ms: replica el timer1 del form)
//   POST /api/flags/{pick,delete,notes,add,close,import,export}
// Con ?add=1 arranca directo en el pane de alta (ex menú "bandera por lat/lon").
// Al cerrar la ventana → /close (sendBeacon): deselecciona + guarda, igual
// que el btnExit del WinForm nativo.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/flags';

  function $(id) { return document.getElementById(id); }

  var countPill = $('countPill'), warnBox = $('warnBox');
  var paneList = $('paneList'), paneAdd = $('paneAdd');
  var flagList = $('flagList'), emptyMsg = $('emptyMsg');
  var inpNotes = $('inpNotes');
  var inpLat = $('inpLat'), inpLon = $('inpLon');

  var closed = false;      // true tras cierre explícito (no re-mandar en pagehide)
  var addOpen = false;     // pane de alta visible
  var latLonTouched = false; // el operario editó lat/lon (no pisar con la posición)
  var lastState = null;

  function warn(msg) {
    warnBox.textContent = msg || '';
    warnBox.classList.toggle('show', !!msg);
  }

  function fmtDist(m) {
    if (!isFinite(m)) return '—';
    if (m >= 1000) return (m / 1000).toFixed(2) + ' km';
    return m.toFixed(1) + ' m';
  }

  function render(s) {
    if (!s || s.ok === false) { warn('Sin conexión con PilotX.'); return; }
    lastState = s;

    var flags = s.flags || [];
    countPill.textContent = String(flags.length);
    warn(!s.has_field ? 'Abrí primero un lote para usar banderas.' : (s.error || ''));

    // Lista (se reconstruye entera: son pocas filas).
    flagList.querySelectorAll('.fl-row').forEach(function (r) { r.remove(); });
    emptyMsg.style.display = flags.length ? 'none' : '';
    flags.forEach(function (f) {
      var row = document.createElement('div');
      row.className = 'fl-row' + (f.number === s.picked ? ' sel' : '');
      row.innerHTML =
        '<span class="fl-dot c' + (f.color || 0) + '"></span>' +
        '<span class="fl-name"></span>' +
        '<span class="fl-dist">' + fmtDist(f.distance_m) + '</span>';
      row.querySelector('.fl-name').textContent = f.notes || ('#' + f.id);
      row.addEventListener('click', async function () {
        render(await post('/pick', { number: f.number }));
      });
      flagList.appendChild(row);
    });

    // Notas de la seleccionada (no pisar mientras se edita).
    var sel = flags.find(function (f) { return f.number === s.picked; });
    var hasSel = !!sel;
    $('btnDelete').disabled = !hasSel;
    inpNotes.disabled = !hasSel;
    if (document.activeElement !== inpNotes)
      inpNotes.value = hasSel ? (sel.notes || '') : '';

    $('btnNew').disabled = !s.has_field;
    $('btnImport').disabled = !s.has_field;
    $('btnExport').disabled = !flags.length;

    // Prefill lat/lon con la posición actual hasta que el operario la toque.
    if (addOpen && !latLonTouched &&
        document.activeElement !== inpLat && document.activeElement !== inpLon) {
      inpLat.value = (s.cur_lat || 0).toFixed(7);
      inpLon.value = (s.cur_lon || 0).toFixed(7);
    }
  }

  async function get(path) {
    try { return await (await fetch(API + path, { cache: 'no-store' })).json(); }
    catch (e) { warn('Sin conexión con PilotX.'); return null; }
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      return await res.json();
    } catch (e) { warn('Sin conexión con PilotX.'); return null; }
  }

  function showAdd(on) {
    addOpen = on;
    latLonTouched = false;
    paneList.style.display = on ? 'none' : 'flex';
    paneAdd.classList.toggle('hiddenTab', !on);
    paneAdd.style.display = on ? 'flex' : 'none';
  }

  // ── Lista ──────────────────────────────────────────────────────────────────
  inpNotes.addEventListener('change', async function () {
    render(await post('/notes', { notes: inpNotes.value }));
  });

  $('btnDelete').addEventListener('click', async function () {
    render(await post('/delete'));
  });

  $('btnNew').addEventListener('click', function () { showAdd(true); });

  $('btnImport').addEventListener('click', async function () {
    render(await post('/import'));
  });
  $('btnExport').addEventListener('click', async function () {
    render(await post('/export'));
  });

  // ── Alta ───────────────────────────────────────────────────────────────────
  inpLat.addEventListener('input', function () { latLonTouched = true; });
  inpLon.addEventListener('input', function () { latLonTouched = true; });

  $('btnUseCurrent').addEventListener('click', function () {
    latLonTouched = false;
    if (lastState) {
      inpLat.value = (lastState.cur_lat || 0).toFixed(7);
      inpLon.value = (lastState.cur_lon || 0).toFixed(7);
    }
  });

  async function addFlag(color) {
    var lat = parseFloat(String(inpLat.value).replace(',', '.'));
    var lon = parseFloat(String(inpLon.value).replace(',', '.'));
    var body = latLonTouched && isFinite(lat) && isFinite(lon)
      ? { lat: lat, lon: lon, color: color, use_current: false }
      : { color: color, use_current: true };
    var s = await post('/add', body);
    if (s && s.ok !== false && !s.error) showAdd(false);
    render(s);
  }

  $('btnAddRed').addEventListener('click', function () { addFlag(0); });
  $('btnAddGreen').addEventListener('click', function () { addFlag(1); });
  $('btnAddYellow').addEventListener('click', function () { addFlag(2); });

  $('btnCancelAdd').addEventListener('click', function () { showAdd(false); });

  // Cierre de ventana → deseleccionar + guardar (ex btnExit de FormFlags).
  window.addEventListener('pagehide', function () {
    if (closed) return;
    closed = true;
    try {
      var blob = new Blob(['{}'], { type: 'application/json' });
      if (navigator.sendBeacon) navigator.sendBeacon(API + '/close', blob);
      else fetch(API + '/close', { method: 'POST', keepalive: true, body: '{}' });
    } catch (e) { /* best-effort */ }
  });

  // ── Arranque ───────────────────────────────────────────────────────────────
  (async function () {
    if (new URLSearchParams(location.search).get('add') === '1') showAdd(true);
    render(await get('/state'));
    // 500 ms: replica el timer1 nativo — distancias siguen al tractor.
    setInterval(async function () { render(await get('/state')); }, 500);
  })();
})();
