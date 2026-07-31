// ============================================================================
// cabecera.js
// Reemplazo completo de FormHeadLine: Build Around + reshape manual (slice).
// Canvas interactivo: tap = marcar A/B sobre el contorno, drag = pan,
// rueda/pinch = zoom. La geometría corre en C#:
//   GET  /api/headland/state              → estado + geometría
//   POST /api/headland/open               → iniciar sesión (hdLine=contorno si vacía)
//   POST /api/headland/build   {distance} → offset Build Around (unidades display)
//   POST /api/headland/reset              → cabecera = contorno
//   POST /api/headland/off                → apaga la cabecera
//   POST /api/headland/section-controlled {on}
//   POST /api/headland/tap {e,n,mode,distance} → punto A/B + línea de corte
//   POST /api/headland/cancel-touch       → descartar toque/línea
//   POST /api/headland/extend {end,grow}  → extender/encoger extremo
//   POST /api/headland/clip               → cortar cabecera con la línea
//   POST /api/headland/undo               → volver al backup pre-corte
//   POST /api/headland/close              → suavizar + persistir + paneles
// Sin poll: la geometría solo cambia por acción del usuario; cada POST del
// flujo slice devuelve el estado completo. Sin contorno: banner + botones off.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/headland';

  var cv        = document.getElementById('cvPreview');
  var ctx       = cv.getContext('2d');
  var inpDist   = document.getElementById('inpDist');
  var unitLabel = document.getElementById('unitLabel');
  var btnWidth  = document.getElementById('btnToolWidth');
  var btnBuild  = document.getElementById('btnBuild');
  var btnReset  = document.getElementById('btnReset');
  var btnOff    = document.getElementById('btnOff');
  var chkSection= document.getElementById('chkSection');
  var statusEl  = document.getElementById('statusText');
  var warnBox   = document.getElementById('warnBox');

  var segCurve  = document.getElementById('segCurve');
  var segLine   = document.getElementById('segLine');
  var btnAPlus  = document.getElementById('btnAPlus');
  var btnAMinus = document.getElementById('btnAMinus');
  var btnBPlus  = document.getElementById('btnBPlus');
  var btnBMinus = document.getElementById('btnBMinus');
  var btnClip   = document.getElementById('btnClip');
  var btnUndo   = document.getElementById('btnUndo');
  var btnCancel = document.getElementById('btnCancelTouch');
  var btnExit   = document.getElementById('btnExit');

  var fence = [];      // [[e,n], ...] contorno exterior (compat)
  var fences = [];     // todos los contornos
  var bndSelect = 0;
  var headland = [];   // [[e,n], ...]
  var slice = [];      // línea de corte vigente
  var sliceMode = null;
  var aPoint = null, bPoint = null;
  var canUndo = false;
  var units = 'm';
  var toolWidthM = 0;
  var hasBoundary = false;
  var mode = 'curve';  // curva | recta (tipo de línea de corte)
  var closed = false;

  // Vista (pan/zoom) — solo afecta el dibujo, no el modelo.
  var view = { scale: 1, ox: 0, oy: 0, fitted: false };
  var pointers = {};
  var drag = null, pinch = null, moved = false;

  function setStatus(txt) {
    statusEl.textContent = txt;
  }

  function friendly(code) {
    switch (code) {
      case 'sin-contorno':   return 'Primero creá un contorno del lote.';
      case 'distancia-cero': return 'Con línea curva la distancia no puede ser 0.';
      case 'cruces':         return 'La línea no cruza la cabecera en 2 puntos. Extendé los extremos e intentá de nuevo.';
      case 'ui-error':       return 'PilotX no pudo procesar la acción.';
      default:               return code || '';
    }
  }

  // ── Canvas sizing ─────────────────────────────────────────────────────────
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
    var pts = [];
    for (var j = 0; j < fences.length; j++) pts = pts.concat(fences[j]);
    if (!pts.length) pts = fence.length ? fence : headland;
    if (!pts.length) return null;
    var minE = Infinity, maxE = -Infinity, minN = Infinity, maxN = -Infinity;
    for (var i = 0; i < pts.length; i++) {
      var e = pts[i][0], n = pts[i][1];
      if (e < minE) minE = e; if (e > maxE) maxE = e;
      if (n < minN) minN = n; if (n > maxN) maxN = n;
    }
    return { minE: minE, maxE: maxE, minN: minN, maxN: maxN };
  }

  function fitToBounds() {
    var b = bounds();
    var r = cv.getBoundingClientRect();
    if (!b || r.width < 2 || r.height < 2) { view.fitted = true; return; }
    var w = Math.max(1, b.maxE - b.minE);
    var h = Math.max(1, b.maxN - b.minN);
    var pad = 0.9;
    var scale = Math.min(r.width / w, r.height / h) * pad;
    view.scale = scale;
    // Centro del lote → centro del canvas. N crece hacia arriba (y invertido).
    var cE = (b.minE + b.maxE) / 2, cN = (b.minN + b.maxN) / 2;
    view.ox = r.width / 2 - cE * scale;
    view.oy = r.height / 2 + cN * scale;
    view.fitted = true;
  }

  // Modelo (E/N metros) → pantalla (px CSS) y viceversa.
  function toScreen(e, n) {
    return { x: e * view.scale + view.ox, y: -n * view.scale + view.oy };
  }
  function toField(x, y) {
    return { e: (x - view.ox) / view.scale, n: -(y - view.oy) / view.scale };
  }

  function drawPolyline(pts, color, width, close) {
    if (!pts.length) return;
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

  function drawDot(pt, color, rad) {
    if (!pt) return;
    var p = toScreen(pt[0], pt[1]);
    ctx.beginPath();
    ctx.arc(p.x, p.y, rad, 0, Math.PI * 2);
    ctx.fillStyle = '#101612';
    ctx.fill();
    ctx.beginPath();
    ctx.arc(p.x, p.y, rad - 2, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
  }

  function draw() {
    var r = cv.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);
    // contornos: seleccionado más claro, resto tierra
    if (fences.length) {
      for (var j = 0; j < fences.length; j++)
        drawPolyline(fences[j], j === bndSelect ? '#7d8a80' : '#a0764a', 2, true);
    } else {
      drawPolyline(fence, '#8a978c', 2, true);
    }
    drawPolyline(headland, '#4ABA3E', 3, true);              // cabecera verde
    if (slice.length)
      drawPolyline(slice, sliceMode === 'ab' ? '#c9302c' : '#3f9e35', 3, false);
    if (slice.length) {
      drawDot(slice[0], '#e8963c', 7);                       // extremo A
      drawDot(slice[slice.length - 1], '#5a8fd6', 7);        // extremo B
    }
    drawDot(aPoint, '#e8963c', 7);                           // toque A pendiente
    drawDot(bPoint, '#5a8fd6', 7);
  }

  // ── Estado ────────────────────────────────────────────────────────────────
  function applyState(s, keepView) {
    if (!s || s.has_field === false) {
      hasBoundary = false;
      fence = []; fences = []; headland = []; slice = [];
      aPoint = null; bPoint = null; canUndo = false;
      warnBox.textContent = 'Primero creá un contorno del lote para poder construir la cabecera.';
      warnBox.classList.add('show');
      setButtons(true);
      setStatus('sin contorno');
      view.fitted = false; draw();
      return;
    }
    hasBoundary = !!s.has_boundary;
    fence = s.fence || [];
    fences = s.fences || [];
    bndSelect = s.bnd_select || 0;
    headland = s.headland || [];
    slice = s.slice || [];
    sliceMode = s.slice_mode || null;
    aPoint = s.a_point || null;
    bPoint = s.b_point || null;
    canUndo = !!s.can_undo;
    units = s.units || 'm';
    toolWidthM = s.tool_width_m || 0;
    unitLabel.textContent = units;
    chkSection.checked = !!s.is_section_controlled;

    // Distancia precargada con el ancho de herramienta: arrancaba en 0 y
    // "Construir" con 0 hacía una cabecera de cero metros — sin error y sin
    // nada visible, "no la crea". Solo se precarga si el campo sigue en 0/
    // vacío (no pisar lo que el operario ya tipeó).
    if (toolWidthM > 0 && !(parseFloat(inpDist.value) > 0)) {
      var disp0 = units === 'ft' ? toolWidthM * 3.28084 : toolWidthM;
      inpDist.value = (Math.round(disp0 * 10) / 10).toString();
    }

    if (s.error) {
      warnBox.textContent = friendly(s.error);
      warnBox.classList.add('show');
    } else {
      warnBox.classList.toggle('show', !hasBoundary);
      warnBox.textContent = 'Primero creá un contorno del lote para poder construir la cabecera.';
    }

    setButtons(!hasBoundary);
    setStatus(s.is_headland_on ? 'cabecera activa' : 'sin cabecera');
    if (!keepView) { view.fitted = false; resize(); } else draw();
  }

  function setButtons(dis) {
    btnBuild.disabled = dis; btnWidth.disabled = dis;
    btnReset.disabled = dis; btnOff.disabled = dis;
    var noSlice = dis || !slice.length;
    btnAPlus.disabled = noSlice; btnAMinus.disabled = noSlice;
    btnBPlus.disabled = noSlice; btnBMinus.disabled = noSlice;
    btnClip.disabled = noSlice;
    btnUndo.disabled = dis || !canUndo;
    btnCancel.disabled = dis || (!slice.length && !aPoint);
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      return await res.json();
    } catch (e) {
      setStatus('sin conexión');
      return null;
    }
  }

  // build/reset/off devuelven el DTO viejo {ok, headland, is_headland_on}.
  function applyResult(data) {
    if (!data) return;
    if (data.headland) headland = data.headland;
    if (typeof data.is_headland_on === 'boolean')
      setStatus(data.is_headland_on ? 'cabecera activa' : 'sin cabecera');
    if (data.ok === false && data.error) {
      warnBox.textContent = friendly(data.error);
      warnBox.classList.add('show');
    }
    draw();
  }

  function distVal() {
    var d = parseFloat(inpDist.value);
    return isNaN(d) ? 0 : d;
  }

  // ── Controles Build Around ────────────────────────────────────────────────
  btnWidth.addEventListener('click', function () {
    if (!toolWidthM) return;
    var disp = units === 'ft' ? toolWidthM * 3.28084 : toolWidthM;
    inpDist.value = (Math.round(disp * 10) / 10).toString();
  });

  btnBuild.addEventListener('click', async function () {
    var d = distVal();
    // Construir con 0 es una cabecera de cero metros: nada visible y ningún
    // error. Fallback al ancho de herramienta; sin herramienta, avisar.
    if (!(d > 0)) {
      if (toolWidthM > 0) {
        d = toolWidthM;
        var disp = units === 'ft' ? d * 3.28084 : d;
        inpDist.value = (Math.round(disp * 10) / 10).toString();
      } else {
        setStatus('poné una distancia en metros primero');
        return;
      }
    }
    setStatus('construyendo…');
    applyResult(await post('/build', { distance: d }));
  });

  btnReset.addEventListener('click', async function () {
    applyResult(await post('/reset', {}));
    applyState(await post('/cancel-touch', {}), true);
  });

  btnOff.addEventListener('click', async function () {
    var data = await post('/off', {});
    if (data) { headland = data.headland || []; setStatus('sin cabecera'); draw(); }
  });

  chkSection.addEventListener('change', async function () {
    await post('/section-controlled', { on: chkSection.checked });
  });

  // ── Controles slice ───────────────────────────────────────────────────────
  function setMode(m) {
    mode = m;
    segCurve.classList.toggle('on', m === 'curve');
    segLine.classList.toggle('on', m === 'ab');
  }
  segCurve.addEventListener('click', function () { setMode('curve'); });
  segLine.addEventListener('click', function () { setMode('ab'); });

  function bindExtend(btn, end, grow) {
    btn.addEventListener('click', async function () {
      applyState(await post('/extend', { end: end, grow: grow }), true);
    });
  }
  bindExtend(btnAPlus, 'a', true);
  bindExtend(btnAMinus, 'a', false);
  bindExtend(btnBPlus, 'b', true);
  bindExtend(btnBMinus, 'b', false);

  btnClip.addEventListener('click', async function () {
    applyState(await post('/clip', {}), true);
  });

  btnUndo.addEventListener('click', async function () {
    applyState(await post('/undo', {}), true);
  });

  btnCancel.addEventListener('click', async function () {
    applyState(await post('/cancel-touch', {}), true);
  });

  function closeWidget() {
    if (closed) return;
    closed = true;
    try { navigator.sendBeacon(API + '/close', '{}'); } catch (e) { }
    try {
      if (window.chrome && window.chrome.webview)
        window.chrome.webview.postMessage('close-hub');
    } catch (e) { }
  }

  btnExit.addEventListener('click', closeWidget);
  window.addEventListener('pagehide', closeWidget);

  // ── Tap / pan / zoom ──────────────────────────────────────────────────────
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
      if (pinch.d > 4) {
        var factor = d / pinch.d;
        var r = cv.getBoundingClientRect();
        var mx = pinch.cx - r.left, my = pinch.cy - r.top;
        view.scale = pinch.scale * factor;
        view.ox = mx - (mx - pinch.ox) * factor;
        view.oy = my - (my - pinch.oy) * factor;
        draw();
      }
      moved = true;
      return;
    }

    if (drag) {
      var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (Math.abs(dx) > 6 || Math.abs(dy) > 6) moved = true;
      view.ox = drag.ox + dx;
      view.oy = drag.oy + dy;
      draw();
    }
  });

  async function endPointer(ev, isTap) {
    delete pointers[ev.pointerId];
    if (Object.keys(pointers).length < 2) pinch = null;
    if (Object.keys(pointers).length === 0) {
      var wasDrag = drag; drag = null;
      if (isTap && wasDrag && !moved && hasBoundary) {
        var r = cv.getBoundingClientRect();
        var f = toField(ev.clientX - r.left, ev.clientY - r.top);
        applyState(await post('/tap', { e: f.e, n: f.n, mode: mode, distance: distVal() }), true);
      }
    }
  }

  cv.addEventListener('pointerup', function (ev) { endPointer(ev, true); });
  cv.addEventListener('pointercancel', function (ev) { endPointer(ev, false); });

  cv.addEventListener('wheel', function (ev) {
    ev.preventDefault();
    var r = cv.getBoundingClientRect();
    var mx = ev.clientX - r.left, my = ev.clientY - r.top;
    var factor = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
    // Zoom centrado en el cursor.
    view.ox = mx - (mx - view.ox) * factor;
    view.oy = my - (my - view.oy) * factor;
    view.scale *= factor;
    draw();
  }, { passive: false });

  window.addEventListener('resize', resize);

  // ── Arranque ──────────────────────────────────────────────────────────────
  (async function () {
    setMode('curve');
    var data = await post('/open', {});
    if (data) applyState(data);
    else {
      setStatus('sin conexión');
      warnBox.textContent = 'Sin conexión con PilotX.';
      warnBox.classList.add('show');
    }
  })();
})();
