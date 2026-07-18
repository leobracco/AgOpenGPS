// ============================================================================
// contorno.js
// Reemplazo HTML de FormBoundary + FormBoundaryPlayer: lista de contornos con
// drive-thru y borrado, creación por manejo/KML/Google Earth/mapa/desde guías,
// y grabación manejando con stats live (puntos/ha) + offset/lado/antena.
//   GET  /api/contorno/state           (poll 500 ms en vista lista)
//   GET  /api/contorno/record/status   (poll 500 ms en vista grabación)
//   POST /api/contorno/{drive-thru,delete,delete-all,import-kml,google-earth,
//         mapa,from-tracks,record/*}
// Confirmaciones destructivas: doble tap (el botón pasa a "¿Seguro?").
// Al cerrar la ventana con grabación activa → record/cancel (sendBeacon),
// igual que abandonar el player nativo sin guardar.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/contorno';

  function $(id) { return document.getElementById(id); }

  var statePill = $('statePill'), warnBox = $('warnBox');
  var paneList = $('paneList'), paneChoose = $('paneChoose'), paneRec = $('paneRec');
  var bndList = $('bndList'), emptyMsg = $('emptyMsg');
  var inpOffset = $('inpOffset');

  var view = 'list';        // list | choose | rec
  var selected = -1;        // índice de contorno seleccionado
  var closed = false;
  var lastRec = null;
  var busy = false;         // no pisar la vista mientras corre un diálogo nativo

  // Doble-tap de confirmación: {btnId: timeoutHandle}
  var confirms = {};

  function warn(msg) {
    warnBox.textContent = msg || '';
    warnBox.classList.toggle('show', !!msg);
  }

  function friendly(err) {
    switch (err) {
      case 'sin-lote': return 'Abrí primero un lote.';
      case 'herramienta-angosta': return 'El implemento es demasiado angosto.';
      case 'borrar-internos-primero': return 'Borrá primero los contornos internos.';
      case 'pocos-puntos': return 'Muy pocos puntos: manejá el borde antes de terminar.';
      case 'kml-invalido': return 'No se pudo leer el KML.';
      case 'google-earth-error': return 'No se pudo abrir Google Earth.';
      case 'sin-grabacion': return 'No hay grabación en curso.';
      case 'indice-invalido': return 'Contorno inexistente.';
      default: return err || '';
    }
  }

  function askConfirm(btn, label) {
    // Primer tap: el botón pregunta. Segundo tap dentro de 3 s: confirma.
    if (confirms[btn.id]) {
      clearTimeout(confirms[btn.id]);
      delete confirms[btn.id];
      btn.textContent = label;
      return true;
    }
    var orig = btn.textContent;
    btn.textContent = '¿Seguro?';
    confirms[btn.id] = setTimeout(function () {
      delete confirms[btn.id];
      btn.textContent = orig;
    }, 3000);
    return false;
  }

  function show(v) {
    view = v;
    paneList.classList.toggle('hiddenTab', v !== 'list');
    paneChoose.classList.toggle('hiddenTab', v !== 'choose');
    paneRec.classList.toggle('hiddenTab', v !== 'rec');
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

  // ── Vista lista ────────────────────────────────────────────────────────────

  function renderState(s) {
    if (!s || s.ok === false) { warn('Sin conexión con PilotX.'); return; }

    var items = s.boundaries || [];
    statePill.textContent = String(items.length);
    statePill.classList.toggle('rec', !!s.recording);
    if (s.recording) statePill.textContent = 'REC';

    warn(!s.job_started ? 'Abrí primero un lote.' : friendly(s.error));

    if (selected >= items.length) selected = -1;

    bndList.querySelectorAll('.bd-row').forEach(function (r) { r.remove(); });
    emptyMsg.style.display = items.length ? 'none' : '';

    items.forEach(function (b, n) {
      var row = document.createElement('div');
      row.className = 'bd-row' + (b.index === selected ? ' sel' : '');
      var name = b.is_outer ? 'Exterior' : 'Interno ' + b.index;
      row.innerHTML =
        '<span class="bd-name">' + name +
        ' <span style="color:var(--agp-text-muted);font-size:smaller;">(' + b.points + ' pts)</span></span>' +
        (b.is_outer ? '' :
          '<button class="bd-thru' + (b.is_drive_thru ? ' on' : '') + '">Cruzar: ' +
          (b.is_drive_thru ? 'Sí' : 'No') + '</button>') +
        '<span class="bd-area">' + (b.area_ha || 0).toFixed(2) + ' ha</span>';
      row.addEventListener('click', function () {
        selected = (selected === b.index) ? -1 : b.index;
        renderState(s);
      });
      var thru = row.querySelector('.bd-thru');
      if (thru) thru.addEventListener('click', async function (ev) {
        ev.stopPropagation();
        renderState(await post('/drive-thru', { index: b.index, value: !b.is_drive_thru }));
      });
      bndList.appendChild(row);
    });

    // Regla nativa: el exterior solo se borra si es el único.
    var canDelete = selected > 0 || (selected === 0 && items.length === 1);
    $('btnDelete').disabled = !canDelete;
    $('btnCreate').disabled = !s.job_started;
  }

  $('btnDelete').addEventListener('click', async function () {
    if (!askConfirm(this, '🗑 Borrar')) return;
    var s = await post('/delete', { index: selected });
    selected = -1;
    renderState(s);
  });

  $('btnCreate').addEventListener('click', function () { show('choose'); });

  // ── Vista crear ────────────────────────────────────────────────────────────

  $('btnBackChoose').addEventListener('click', function () { show('list'); });

  $('btnDrive').addEventListener('click', async function () {
    var r = await post('/record/start');
    if (r && r.ok !== false && !r.error) { renderRec(r); show('rec'); }
    else warn(friendly(r && r.error));
  });

  async function nativeAction(path, body) {
    // Diálogo/form nativo del lado PilotX: bloquear el poll mientras corre.
    busy = true;
    var s = await post(path, body);
    busy = false;
    if (s && !s.error) show('list');
    renderState(s);
  }

  $('btnKmlOne').addEventListener('click', function () { nativeAction('/import-kml', { multi: false }); });
  $('btnKmlMulti').addEventListener('click', function () { nativeAction('/import-kml', { multi: true }); });
  $('btnGoogleEarth').addEventListener('click', function () { nativeAction('/google-earth'); });
  $('btnMapa').addEventListener('click', function () { nativeAction('/mapa'); });
  $('btnFromTracks').addEventListener('click', function () { nativeAction('/from-tracks'); });

  // ── Vista grabación ────────────────────────────────────────────────────────

  function renderRec(r) {
    if (!r || r.ok === false) { warn('Sin conexión con PilotX.'); return; }
    lastRec = r;

    warn(friendly(r.error));
    statePill.textContent = 'REC';
    statePill.classList.add('rec');

    $('recPts').textContent = String(r.points || 0);
    $('recArea').textContent = (r.area_ha || 0).toFixed(2);

    if (document.activeElement !== inpOffset)
      inpOffset.value = String(Math.round(r.offset_cm || 0));

    $('btnSide').textContent = r.right_side ? 'Derecha' : 'Izquierda';
    $('btnAtPivot').textContent = r.at_pivot ? 'Antena' : 'Implemento';
    $('btnSectionRec').textContent = 'Solo con secciones: ' + (r.section_rec ? 'Sí' : 'No');

    var rec = !r.paused;
    $('btnRecPause').textContent = rec ? '⏸ Pausa' : '⏺ Grabar';
    // Punto manual y deshacer solo en pausa (regla del player nativo).
    $('btnAddPoint').disabled = rec;
    $('btnUndo').disabled = rec;
  }

  inpOffset.addEventListener('change', async function () {
    var cm = parseFloat(String(inpOffset.value).replace(',', '.'));
    if (isFinite(cm)) renderRec(await post('/record/set', { offset_cm: cm }));
  });

  $('btnSide').addEventListener('click', async function () {
    renderRec(await post('/record/set', { right_side: !(lastRec && lastRec.right_side) }));
  });
  $('btnAtPivot').addEventListener('click', async function () {
    renderRec(await post('/record/set', { at_pivot: !(lastRec && lastRec.at_pivot) }));
  });
  $('btnSectionRec').addEventListener('click', async function () {
    renderRec(await post('/record/set', { section_rec: !(lastRec && lastRec.section_rec) }));
  });

  $('btnRecPause').addEventListener('click', async function () {
    renderRec(await post('/record/pause'));
  });
  $('btnAddPoint').addEventListener('click', async function () {
    renderRec(await post('/record/add-point'));
  });
  $('btnUndo').addEventListener('click', async function () {
    renderRec(await post('/record/undo'));
  });
  $('btnRestart').addEventListener('click', async function () {
    if (!askConfirm(this, 'Reiniciar')) return;
    renderRec(await post('/record/restart'));
  });
  $('btnCancelRec').addEventListener('click', async function () {
    if (!askConfirm(this, '✖ Cancelar')) return;
    await post('/record/cancel');
    show('list');
    renderState(await get('/state'));
  });
  $('btnSaveRec').addEventListener('click', async function () {
    if (!askConfirm(this, '✔ Terminar')) return;
    var r = await post('/record/save');
    if (r && r.error) { renderRec(r); return; }
    show('list');
    renderState(await get('/state'));
  });

  // Cierre de ventana con grabación activa → cancelar (no dejar el modo
  // grabación prendido en PilotX sin UI que lo controle).
  window.addEventListener('pagehide', function () {
    if (closed || view !== 'rec') return;
    closed = true;
    try {
      var blob = new Blob(['{}'], { type: 'application/json' });
      if (navigator.sendBeacon) navigator.sendBeacon(API + '/record/cancel', blob);
      else fetch(API + '/record/cancel', { method: 'POST', keepalive: true, body: '{}' });
    } catch (e) { /* best-effort */ }
  });

  // ── Arranque + poll ────────────────────────────────────────────────────────
  (async function () {
    var s = await get('/state');
    // Si PilotX ya está grabando (widget reabierto), ir directo a la grabación.
    if (s && s.recording) { renderRec(await get('/record/status')); show('rec'); }
    else renderState(s);

    setInterval(async function () {
      if (busy) return;
      if (view === 'rec') renderRec(await get('/record/status'));
      else if (view === 'list') renderState(await get('/state'));
    }, 500);
  })();
})();
