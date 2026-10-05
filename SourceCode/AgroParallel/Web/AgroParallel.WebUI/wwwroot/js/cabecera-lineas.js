// ============================================================================
// cabecera-lineas.js
// Reemplazo HTML de FormHeadAche: constructor de cabecera por líneas.
// El canvas dibuja contornos + líneas construidas + cabecera + puntos A/B en
// E/N metros (fit-to-bounds, drag = pan, rueda/pinch = zoom, tap = marcar
// punto). La geometría corre en C# (/api/cabecera-lineas/*); acá solo viaja
// el estado completo tras cada acción — no hay poll.
// Al cerrar la ventana → /close (sendBeacon): FileSaveHeadLines + recálculo
// de isHeadlandOn, igual que cerrar el form nativo.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/cabecera-lineas';

  function $(id) { return document.getElementById(id); }

  var cv = $('cvMap'), ctx = cv.getContext('2d');
  var warnBox = $('warnBox'), inpDist = $('inpDist'), unitLabel = $('unitLabel');
  var selInfo = $('selInfo'), lblTool = $('lblTool'), chkSection = $('chkSection');
  var segCurve = $('segCurve'), segLine = $('segLine');

  var st = null;            // último estado del server
  var mode = 'curve';
  var widthMult = 0;        // multiplicador del botón "× ancho"
  var closed = false;

  // Vista (pan/zoom) — solo dibujo.
  var view = { scale: 1, ox: 0, oy: 0, fitted: false };
  var pointers = {};        // pointerId → {x,y}
  var drag = null;          // pan con 1 dedo
  var pinch = null;         // zoom con 2 dedos
  var moved = false;

  function friendly(err) {
    switch (err) {
      case 'sin-lote': return 'Abrí primero un lote.';
      case 'sin-contorno': return 'El lote no tiene contorno todavía.';
      case 'mismo-punto': return 'El punto A y el B son el mismo: tocá dos lugares distintos.';
      case 'una-sola-linea': return 'Hace falta más de una línea para construir la cabecera.';
      case 'cruces': return 'Las puntas tienen que cruzarse entre sí una sola vez. Extendé o acortá con A±/B±.';
      case 'ui-error': case 'no-state': case 'service-unavailable': return 'Sin conexión con PilotX.';
      default: return err || '';
    }
  }

  function warn(msg) {
    warnBox.textContent = msg || '';
    warnBox.classList.toggle('show', !!msg);
  }

  // ── Canvas ────────────────────────────────────────────────────────────────

  function resize() {
    var r = cv.getBoundingClientRect();
    var dpr = window.devicePixelRatio || 1;
    cv.width = Math.max(1, Math.round(r.width * dpr));
    cv.height = Math.max(1, Math.round(r.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (!view.fitted) fitToBounds();
    draw();
  }

  function bounds() {
    if (!st || !st.fences || !st.fences.length) return null;
    var minE = Infinity, maxE = -Infinity, minN = Infinity, maxN = -Infinity;
    for (var j = 0; j < st.fences.length; j++) {
      var f = st.fences[j];
      for (var i = 0; i < f.length; i++) {
        var e = f[i][0], n = f[i][1];
        if (e < minE) minE = e; if (e > maxE) maxE = e;
        if (n < minN) minN = n; if (n > maxN) maxN = n;
      }
    }
    return minE === Infinity ? null : { minE: minE, maxE: maxE, minN: minN, maxN: maxN };
  }

  function fitToBounds() {
    var b = bounds();
    var r = cv.getBoundingClientRect();
    if (!b || r.width < 2 || r.height < 2) { view.fitted = true; return; }
    var w = Math.max(1, b.maxE - b.minE);
    var h = Math.max(1, b.maxN - b.minN);
    var scale = Math.min(r.width / w, r.height / h) * 0.9;
    view.scale = scale;
    var cE = (b.minE + b.maxE) / 2, cN = (b.minN + b.maxN) / 2;
    view.ox = r.width / 2 - cE * scale;
    view.oy = r.height / 2 + cN * scale;
    view.fitted = true;
  }

  function toScreen(e, n) {
    return { x: e * view.scale + view.ox, y: -n * view.scale + view.oy };
  }

  function toField(x, y) {
    return { e: (x - view.ox) / view.scale, n: -(y - view.oy) / view.scale };
  }

  function poly(pts, color, width, close) {
    if (!pts || !pts.length) return;
    ctx.beginPath();
    for (var i = 0; i < pts.length; i++) {
      var p = toScreen(pts[i][0], pts[i][1]);
      if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
    }
    if (close) ctx.closePath();
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.stroke();
  }

  function dot(pt, radius, color) {
    if (!pt) return;
    var p = toScreen(pt[0], pt[1]);
    ctx.beginPath();
    ctx.arc(p.x, p.y, radius, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
  }

  function draw() {
    var r = cv.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);
    if (!st) return;

    // Contornos: seleccionado gris, resto marrón (paleta del form nativo).
    for (var j = 0; j < (st.fences || []).length; j++)
      poly(st.fences[j], j === st.bnd_select ? '#7d8a80' : '#a0764a', 2, true);

    // Líneas construidas: AB amarillo, curva verde; seleccionada magenta gruesa.
    var tracks = st.tracks || [];
    for (var t = 0; t < tracks.length; t++) {
      if (t === st.sel_idx) continue;
      poly(tracks[t].points, tracks[t].mode === 'ab' ? '#c9a227' : '#3f9e35', 1.5, false);
    }
    if (st.sel_idx > -1 && tracks[st.sel_idx]) {
      var sel = tracks[st.sel_idx].points;
      poly(sel, '#c026d3', 4, false);
      if (sel.length) {
        dot(sel[0], 9, '#101612'); dot(sel[0], 6, '#e8963c');            // punta A
        dot(sel[sel.length - 1], 9, '#101612');
        dot(sel[sel.length - 1], 6, '#5a8fd6');                          // punta B
      }
    }

    // Cabecera armada: amarillo grueso.
    poly(st.hd_line, '#d4a017', 5, false);

    // Puntos A/B tocados.
    if (st.a_point) { dot(st.a_point, 10, '#101612'); dot(st.a_point, 7, '#e8963c'); }
    if (st.b_point) { dot(st.b_point, 10, '#101612'); dot(st.b_point, 7, '#5a8fd6'); }
  }

  // ── Estado ────────────────────────────────────────────────────────────────

  function apply(s, keepView) {
    if (!s || s.ok === false) { warn(friendly(s && s.error)); return; }
    st = s;

    warn(!s.job_started ? friendly('sin-lote')
      : !s.has_boundary ? friendly('sin-contorno')
      : friendly(s.error));

    unitLabel.textContent = s.units || 'm';
    lblTool.textContent = 'Implemento: ' + (s.tool_width_display || 0).toFixed(1) + ' ' + (s.units || 'm');
    chkSection.checked = !!s.is_section_controlled;

    var tracks = s.tracks || [];
    selInfo.textContent = s.sel_idx > -1 && tracks[s.sel_idx]
      ? ('Línea ' + (s.sel_idx + 1) + '/' + tracks.length + ' · ' + (tracks[s.sel_idx].mode === 'ab' ? 'recta' : 'curva'))
      : (tracks.length ? tracks.length + ' líneas · ninguna seleccionada' : 'Sin líneas todavía');

    var noSel = !(s.sel_idx > -1);
    ['btnPrev', 'btnNext'].forEach(function (id) { $(id).disabled = !tracks.length; });
    ['btnDelTrack', 'btnAPlus', 'btnAMinus', 'btnBPlus', 'btnBMinus'].forEach(function (id) { $(id).disabled = noSel; });
    $('btnBuild').disabled = tracks.length < 2;

    if (!keepView) view.fitted = false;
    resize();
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

  function distVal() {
    var d = parseFloat(String(inpDist.value).replace(',', '.'));
    return isFinite(d) ? d : 0;
  }

  // ── Interacción canvas: tap = marcar, drag = pan, rueda/pinch = zoom ──────

  cv.addEventListener('pointerdown', function (ev) {
    pointers[ev.pointerId] = { x: ev.clientX, y: ev.clientY };
    var ids = Object.keys(pointers);
    if (ids.length === 1) {
      drag = { x: ev.clientX, y: ev.clientY, ox: view.ox, oy: view.oy };
      moved = false;
    } else if (ids.length === 2) {
      drag = null;
      var a = pointers[ids[0]], b = pointers[ids[1]];
      pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), scale: view.scale, ox: view.ox, oy: view.oy,
                cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 };
    }
    try { cv.setPointerCapture(ev.pointerId); } catch (e) { /* puntero ya liberado */ }
  });

  cv.addEventListener('pointermove', function (ev) {
    if (!pointers[ev.pointerId]) return;
    pointers[ev.pointerId] = { x: ev.clientX, y: ev.clientY };
    var ids = Object.keys(pointers);

    if (pinch && ids.length === 2) {
      var a = pointers[ids[0]], b = pointers[ids[1]];
      var d = Math.hypot(a.x - b.x, a.y - b.y);
      var factor = d / (pinch.d || 1);
      var r = cv.getBoundingClientRect();
      var mx = pinch.cx - r.left, my = pinch.cy - r.top;
      view.scale = pinch.scale * factor;
      view.ox = mx - (mx - pinch.ox) * factor;
      view.oy = my - (my - pinch.oy) * factor;
      moved = true;
      draw();
      return;
    }

    if (drag) {
      var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (Math.abs(dx) + Math.abs(dy) > 6) moved = true;
      view.ox = drag.ox + dx;
      view.oy = drag.oy + dy;
      draw();
    }
  });

  cv.addEventListener('pointerup', async function (ev) {
    delete pointers[ev.pointerId];
    if (Object.keys(pointers).length < 2) pinch = null;

    if (drag && !moved) {
      // Tap: marcar punto A/B en coords de campo.
      var r = cv.getBoundingClientRect();
      var f = toField(ev.clientX - r.left, ev.clientY - r.top);
      drag = null;
      apply(await post('/tap', { e: f.e, n: f.n, mode: mode, distance: distVal() }), true);
      return;
    }
    drag = null;
  });

  cv.addEventListener('pointercancel', function (ev) {
    delete pointers[ev.pointerId];
    drag = null; pinch = null;
  });

  cv.addEventListener('wheel', function (ev) {
    ev.preventDefault();
    var r = cv.getBoundingClientRect();
    var mx = ev.clientX - r.left, my = ev.clientY - r.top;
    var factor = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
    view.ox = mx - (mx - view.ox) * factor;
    view.oy = my - (my - view.oy) * factor;
    view.scale *= factor;
    draw();
  }, { passive: false });

  // ── Controles ─────────────────────────────────────────────────────────────

  function setMode(m) {
    mode = m;
    segCurve.classList.toggle('on', m === 'curve');
    segLine.classList.toggle('on', m === 'ab');
  }
  segCurve.addEventListener('click', function () { setMode('curve'); });
  segLine.addEventListener('click', function () { setMode('ab'); });

  // "× ancho": cicla 1×, 2×, 3×, 0 y carga la distancia (ancho útil display).
  $('btnWidthX').addEventListener('click', function () {
    if (!st) return;
    widthMult = (widthMult % 3) + 1;
    inpDist.value = (Math.round(st.tool_width_display * widthMult * 10) / 10).toString();
    $('btnWidthX').textContent = '× ancho (' + widthMult + ')';
  });

  $('btnPrev').addEventListener('click', async function () { apply(await post('/cycle', { dir: -1 }), true); });
  $('btnNext').addEventListener('click', async function () { apply(await post('/cycle', { dir: 1 }), true); });
  $('btnDelTrack').addEventListener('click', async function () { apply(await post('/delete-track'), true); });

  $('btnAPlus').addEventListener('click', async function () { apply(await post('/extend', { end: 'a', grow: true }), true); });
  $('btnAMinus').addEventListener('click', async function () { apply(await post('/extend', { end: 'a', grow: false }), true); });
  $('btnBPlus').addEventListener('click', async function () { apply(await post('/extend', { end: 'b', grow: true }), true); });
  $('btnBMinus').addEventListener('click', async function () { apply(await post('/extend', { end: 'b', grow: false }), true); });

  $('btnCancelTouch').addEventListener('click', async function () { apply(await post('/cancel-touch'), true); });

  $('btnBuild').addEventListener('click', async function () { apply(await post('/build'), true); });
  $('btnReset').addEventListener('click', async function () { apply(await post('/reset'), true); });

  chkSection.addEventListener('change', async function () {
    await post('/section-controlled', { on: chkSection.checked });
  });

  function closeWidget() {
    var wv = (window.chrome && window.chrome.webview) || null;
    try { if (wv) wv.postMessage('close-hub'); else window.close(); } catch (e) { /* nada */ }
  }

  $('btnOff').addEventListener('click', async function () {
    await post('/off');
    closed = true;
    try { navigator.sendBeacon(API + '/close', new Blob(['{}'], { type: 'application/json' })); } catch (e) { }
    closeWidget();
  });

  $('btnExit').addEventListener('click', async function () {
    closed = true;
    await post('/close');
    closeWidget();
  });

  // Cierre con la X de la ventana → mismo cierre que el form nativo.
  window.addEventListener('pagehide', function () {
    if (closed) return;
    closed = true;
    try {
      var blob = new Blob(['{}'], { type: 'application/json' });
      if (navigator.sendBeacon) navigator.sendBeacon(API + '/close', blob);
      else fetch(API + '/close', { method: 'POST', keepalive: true, body: '{}' });
    } catch (e) { /* best-effort */ }
  });

  window.addEventListener('resize', resize);

  // ── Arranque: abrir sesión de edición ─────────────────────────────────────
  (async function () { apply(await post('/open')); })();
})();
