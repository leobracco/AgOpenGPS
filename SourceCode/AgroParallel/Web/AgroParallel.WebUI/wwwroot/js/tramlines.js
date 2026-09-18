// ============================================================================
// tramlines.js — Reemplazo de FormTramLine.
// Canvas interactivo: tap = corte 3-step (A/B/lado), drag = pan, rueda/pinch
// = zoom. Sin poll: cada POST devuelve el estado completo.
//   POST /api/tramlines/open            → iniciar sesión
//   POST /api/tramlines/cycle  {dir}    → ciclar guía
//   POST /api/tramlines/swap            → cambiar lado
//   POST /api/tramlines/passes {passes} → cantidad
//   POST /api/tramlines/start-pass {start_pass}
//   POST /api/tramlines/outer  {on}     → outer tram bnd
//   POST /api/tramlines/alpha  {alpha}  → opacidad
//   POST /api/tramlines/add             → agregar trams nuevos
//   POST /api/tramlines/delete-all      → borrar todos
//   POST /api/tramlines/tap    {e,n}    → 3-tap cut
//   POST /api/tramlines/cancel-touch    → descartar toques
//   POST /api/tramlines/close           → guardar y salir
//   POST /api/tramlines/cancel          → revertir y salir
// ============================================================================

(function () {
  'use strict';

  var API = '/api/tramlines';

  var cv      = document.getElementById('cvMap');
  var ctx     = cv.getContext('2d');
  var warnBox = document.getElementById('warnBox');
  var hintEl  = document.getElementById('hintText');
  var selEl   = document.getElementById('selTrack');
  var lblPass = document.getElementById('lblPasses');
  var lblStart= document.getElementById('lblStart');
  var lblInfo = document.getElementById('lblInfo');

  var btnPrev = document.getElementById('btnPrev');
  var btnNext = document.getElementById('btnNext');
  var btnSwap = document.getElementById('btnSwap');
  var btnPassesDn = document.getElementById('btnPassesDn');
  var btnPassesUp = document.getElementById('btnPassesUp');
  var btnStartDn = document.getElementById('btnStartDn');
  var btnStartUp = document.getElementById('btnStartUp');
  var chkOuter = document.getElementById('chkOuter');
  var btnAdd  = document.getElementById('btnAdd');
  var btnCancelTouch = document.getElementById('btnCancelTouch');
  var btnAlphaDn = document.getElementById('btnAlphaDn');
  var btnAlphaUp = document.getElementById('btnAlphaUp');
  var btnDeleteAll = document.getElementById('btnDeleteAll');
  var btnCancel = document.getElementById('btnCancel');
  var btnExit = document.getElementById('btnExit');

  var state = {};
  var closed = false;

  var view = { scale: 1, ox: 0, oy: 0, fitted: false };
  var pointers = {}, drag = null, pinch = null, moved = false;

  // ── Helpers ───────────────────────────────────────────────────────────────
  function toScreen(e, n) {
    return { x: e * view.scale + view.ox, y: -n * view.scale + view.oy };
  }
  function toField(x, y) {
    return { e: (x - view.ox) / view.scale, n: -(y - view.oy) / view.scale };
  }

  function resize() {
    var r = cv.getBoundingClientRect();
    var dpr = window.devicePixelRatio || 1;
    cv.width = Math.max(1, Math.round(r.width * dpr));
    cv.height = Math.max(1, Math.round(r.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (!view.fitted) fitToBounds();
    draw();
  }

  function fitToBounds() {
    var pts = [];
    var fences = state.fences || [];
    for (var j = 0; j < fences.length; j++) pts = pts.concat(fences[j]);
    if (!pts.length) { view.fitted = true; return; }
    var minE = Infinity, maxE = -Infinity, minN = Infinity, maxN = -Infinity;
    for (var i = 0; i < pts.length; i++) {
      var e = pts[i][0], n = pts[i][1];
      if (e < minE) minE = e; if (e > maxE) maxE = e;
      if (n < minN) minN = n; if (n > maxN) maxN = n;
    }
    var r = cv.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) { view.fitted = true; return; }
    var w = Math.max(1, maxE - minE), h = Math.max(1, maxN - minN);
    var scale = Math.min(r.width / w, r.height / h) * 0.9;
    view.scale = scale;
    var cE = (minE + maxE) / 2, cN = (minN + maxN) / 2;
    view.ox = r.width / 2 - cE * scale;
    view.oy = r.height / 2 + cN * scale;
    view.fitted = true;
  }

  function drawPoly(pts, color, width, close, alpha) {
    if (!pts || !pts.length) return;
    ctx.beginPath();
    for (var i = 0; i < pts.length; i++) {
      var p = toScreen(pts[i][0], pts[i][1]);
      if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
    }
    if (close) ctx.closePath();
    ctx.globalAlpha = alpha != null ? alpha : 1;
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.stroke();
    ctx.globalAlpha = 1;
  }

  function drawDot(pt, color, rad) {
    if (!pt) return;
    var p = toScreen(pt[0], pt[1]);
    ctx.beginPath();
    ctx.arc(p.x, p.y, rad, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
  }

  function draw() {
    var r = cv.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);

    // Contornos
    var fences = state.fences || [];
    for (var j = 0; j < fences.length; j++)
      drawPoly(fences[j], j === 0 ? '#7d8a80' : '#a0764a', 2, true);

    // Outer/inner bnd
    drawPoly(state.outer_bnd, '#d4a833', 2, true);
    drawPoly(state.inner_bnd, '#d4a833', 2, true);

    // Tracks (guías)
    var tracks = state.tracks || [];
    for (var t = 0; t < tracks.length; t++) {
      var trk = tracks[t];
      var isSel = t === state.sel_idx;
      var col = trk.mode === 'ab' ? '#e03333' : '#33e033';
      drawPoly(trk.points, col, isSel ? 4 : 2, false);
    }

    // Saved trams
    var alpha = state.alpha || 1;
    var saved = state.saved_trams || [];
    for (var s = 0; s < saved.length; s++)
      drawPoly(saved[s], '#ba85a2', 3, false, alpha);

    // New trams (preview)
    var newT = state.new_trams || [];
    for (var n = 0; n < newT.length; n++)
      drawPoly(newT[n], '#f0f0f0', 1.5, false);

    // Cut points
    drawDot(state.pt_a, '#ff0000', 6);
    drawDot(state.pt_b, '#00ff00', 6);
    if (state.cut_step === 2 && state.pt_a && state.pt_b) {
      ctx.beginPath();
      var pa = toScreen(state.pt_a[0], state.pt_a[1]);
      var pb = toScreen(state.pt_b[0], state.pt_b[1]);
      ctx.moveTo(pa.x, pa.y);
      ctx.lineTo(pb.x, pb.y);
      ctx.strokeStyle = '#ff4444';
      ctx.lineWidth = 3;
      ctx.stroke();
    }
  }

  // ── Estado ────────────────────────────────────────────────────────────────
  function apply(s, keepView) {
    if (!s) return;
    state = s;

    if (s.error) {
      warnBox.textContent = friendly(s.error);
      warnBox.classList.add('show');
    } else {
      warnBox.classList.remove('show');
    }

    var tracks = s.tracks || [];
    if (s.sel_idx >= 0 && s.sel_idx < tracks.length) {
      var t = tracks[s.sel_idx];
      selEl.textContent = (s.sel_idx + 1) + '/' + tracks.length + ' · ' + t.name + ' (' + t.mode + ')';
    } else {
      selEl.textContent = 'Sin líneas';
    }

    lblPass.textContent = s.passes;
    lblStart.textContent = s.start_pass;
    chkOuter.checked = !!s.is_outer;
    lblInfo.textContent = 'Track: ' + (s.track_width_display || 0).toFixed(1) + ' ' + s.units
      + ' · Tram: ' + (s.tram_width_display || 0).toFixed(1) + ' ' + s.units
      + ' · Impl: ' + (s.tool_width_display || 0).toFixed(1) + ' ' + s.units;

    var step = s.cut_step || 0;
    hintEl.textContent = step === 0 ? 'Elegí una guía · ajustá pasadas · Agregar'
      : step === 1 ? 'Tocá el punto B de corte'
      : 'Tocá el lado a eliminar';

    if (!keepView) { view.fitted = false; resize(); } else draw();
  }

  function friendly(code) {
    switch (code) {
      case 'sin-contorno': return 'Necesitás un contorno para construir tramlines.';
      case 'sin-guias':    return 'No hay líneas de guiado AB/Curva visibles.';
      default:             return code || '';
    }
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      return await res.json();
    } catch (e) { return null; }
  }

  // ── Controles ───────────────────────────────────────────────────────────
  btnPrev.addEventListener('click', async function () { apply(await post('/cycle', { dir: -1 }), true); });
  btnNext.addEventListener('click', async function () { apply(await post('/cycle', { dir: 1 }), true); });
  btnSwap.addEventListener('click', async function () { apply(await post('/swap'), true); });

  btnPassesUp.addEventListener('click', async function () {
    apply(await post('/passes', { passes: (state.passes || 2) + 1 }), true);
  });
  btnPassesDn.addEventListener('click', async function () {
    apply(await post('/passes', { passes: Math.max(1, (state.passes || 2) - 1) }), true);
  });
  btnStartUp.addEventListener('click', async function () {
    apply(await post('/start-pass', { start_pass: (state.start_pass || 0) + 1 }), true);
  });
  btnStartDn.addEventListener('click', async function () {
    apply(await post('/start-pass', { start_pass: Math.max(0, (state.start_pass || 0) - 1) }), true);
  });

  chkOuter.addEventListener('change', async function () {
    apply(await post('/outer', { on: chkOuter.checked }), true);
  });

  btnAdd.addEventListener('click', async function () { apply(await post('/add'), true); });
  btnDeleteAll.addEventListener('click', async function () { apply(await post('/delete-all'), true); });
  btnCancelTouch.addEventListener('click', async function () { apply(await post('/cancel-touch'), true); });

  btnAlphaDn.addEventListener('click', async function () {
    apply(await post('/alpha', { alpha: Math.max(0.2, (state.alpha || 1) - 0.1) }), true);
  });
  btnAlphaUp.addEventListener('click', async function () {
    apply(await post('/alpha', { alpha: Math.min(1, (state.alpha || 1) + 0.1) }), true);
  });

  function closeWidget(endpoint) {
    if (closed) return;
    closed = true;
    try { navigator.sendBeacon(API + endpoint, '{}'); } catch (e) { }
    try {
      if (window.chrome && window.chrome.webview)
        window.chrome.webview.postMessage('close-hub');
    } catch (e) { }
  }

  btnExit.addEventListener('click', function () { closeWidget('/close'); });
  btnCancel.addEventListener('click', function () { closeWidget('/cancel'); });
  window.addEventListener('pagehide', function () { closeWidget('/close'); });

  // ── Tap / pan / zoom ──────────────────────────────────────────────────
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
    try { cv.setPointerCapture(ev.pointerId); } catch (e) { }
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
      if (isTap && wasDrag && !moved && state.has_boundary) {
        var r = cv.getBoundingClientRect();
        var f = toField(ev.clientX - r.left, ev.clientY - r.top);
        apply(await post('/tap', { e: f.e, n: f.n }), true);
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
    view.ox = mx - (mx - view.ox) * factor;
    view.oy = my - (my - view.oy) * factor;
    view.scale *= factor;
    draw();
  }, { passive: false });

  window.addEventListener('resize', resize);

  // ── Arranque ──────────────────────────────────────────────────────────
  (async function () {
    var data = await post('/open');
    if (data) apply(data);
  })();
})();
