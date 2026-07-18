// ============================================================================
// tracks.js — Gestor de guías unificado (FormBuildTracks + FormABDraw).
// Lista CRUD + canvas interactivo (tap A/B en contorno para crear curva/AB,
// drag = pan, rueda/pinch = zoom). Sin poll.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/tracks';

  var cv      = document.getElementById('cvMap');
  var ctx     = cv.getContext('2d');
  var listEl  = document.getElementById('trackList');

  var btnMoveUp = document.getElementById('btnMoveUp');
  var btnMoveDn = document.getElementById('btnMoveDn');
  var btnSwapAB = document.getElementById('btnSwapAB');
  var btnToggleAll = document.getElementById('btnToggleAll');
  var btnDuplicate = document.getElementById('btnDuplicate');
  var btnRename = document.getElementById('btnRename');
  var btnDelete = document.getElementById('btnDelete');
  var btnCancel = document.getElementById('btnCancel');
  var btnUse = document.getElementById('btnUse');

  var btnMakeCurve = document.getElementById('btnMakeCurve');
  var btnMakeAB = document.getElementById('btnMakeAB');
  var btnMakeBndCurve = document.getElementById('btnMakeBndCurve');
  var btnExtendA = document.getElementById('btnExtendA');
  var btnExtendB = document.getElementById('btnExtendB');
  var btnCancelTouch = document.getElementById('btnCancelTouch');

  var inputPane = document.getElementById('inputPane');
  var inpName = document.getElementById('inpName');
  var btnInputOk = document.getElementById('btnInputOk');
  var btnInputCancel = document.getElementById('btnInputCancel');

  var state = {};
  var closed = false;
  var inputAction = null;
  var allVisible = true;

  var view = { scale: 1, ox: 0, oy: 0, fitted: false };
  var pointers = {}, drag = null, pinch = null, moved = false;

  // ── Helpers ───────────────────────────────────────────────────────────
  function toScreen(e, n) { return { x: e * view.scale + view.ox, y: -n * view.scale + view.oy }; }
  function toField(x, y) { return { e: (x - view.ox) / view.scale, n: -(y - view.oy) / view.scale }; }

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
      if (pts[i][0] < minE) minE = pts[i][0]; if (pts[i][0] > maxE) maxE = pts[i][0];
      if (pts[i][1] < minN) minN = pts[i][1]; if (pts[i][1] > maxN) maxN = pts[i][1];
    }
    var r = cv.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) { view.fitted = true; return; }
    var scale = Math.min(r.width / Math.max(1, maxE - minE), r.height / Math.max(1, maxN - minN)) * 0.9;
    view.scale = scale;
    view.ox = r.width / 2 - ((minE + maxE) / 2) * scale;
    view.oy = r.height / 2 + ((minN + maxN) / 2) * scale;
    view.fitted = true;
  }

  function drawPoly(pts, color, width, close) {
    if (!pts || !pts.length) return;
    ctx.beginPath();
    for (var i = 0; i < pts.length; i++) {
      var p = toScreen(pts[i][0], pts[i][1]);
      if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
    }
    if (close) ctx.closePath();
    ctx.strokeStyle = color; ctx.lineWidth = width; ctx.stroke();
  }

  function drawDot(pt, color, rad) {
    if (!pt) return;
    var p = toScreen(pt[0], pt[1]);
    ctx.beginPath(); ctx.arc(p.x, p.y, rad, 0, Math.PI * 2);
    ctx.fillStyle = '#101612'; ctx.fill();
    ctx.beginPath(); ctx.arc(p.x, p.y, rad - 2, 0, Math.PI * 2);
    ctx.fillStyle = color; ctx.fill();
  }

  function draw() {
    var r = cv.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);

    var fences = state.fences || [];
    for (var j = 0; j < fences.length; j++)
      drawPoly(fences[j], j === (state.bnd_select || 0) ? '#7d8a80' : '#a0764a', 2, true);

    var geoms = state.track_geoms || [];
    for (var t = 0; t < geoms.length; t++) {
      var g = geoms[t];
      var isSel = t === state.selected_idx;
      var col = g.mode === 'ab' ? '#e03333' : g.mode === 'bnd_curve' ? '#b3802a' : '#33e033';
      drawPoly(g.points, col, isSel ? 4 : 2, g.mode === 'bnd_curve');
      if (g.mode !== 'ab' && g.points && g.points.length > 1) {
        drawDot(g.points[0], '#e8963c', isSel ? 6 : 4);
        drawDot(g.points[g.points.length - 1], '#5a8fd6', isSel ? 6 : 4);
      }
    }

    drawDot(state.a_point, '#e8963c', 7);
    drawDot(state.b_point, '#5a8fd6', 7);
  }

  // ── Estado ────────────────────────────────────────────────────────────
  async function post(path, body) {
    try {
      var res = await fetch(API + path, { method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {}) });
      return await res.json();
    } catch (e) { return null; }
  }

  function apply(s, keepView) {
    if (!s) return;
    state = s;
    renderList();
    btnMakeCurve.disabled = !s.can_make_line;
    btnMakeAB.disabled = !s.can_make_line;
    btnMakeBndCurve.disabled = !!s.has_boundary_curve;
    var isCurve = s.selected_idx >= 0 && s.track_geoms && s.track_geoms[s.selected_idx]
      && (s.track_geoms[s.selected_idx].mode === 'curve' || s.track_geoms[s.selected_idx].mode === 'bnd_curve');
    btnExtendA.disabled = !isCurve;
    btnExtendB.disabled = !isCurve;
    if (!keepView) { view.fitted = false; resize(); } else draw();
  }

  function renderList() {
    listEl.innerHTML = '';
    var tracks = state.tracks || [];
    for (var i = 0; i < tracks.length; i++) {
      var t = tracks[i];
      var item = document.createElement('div');
      item.className = 'tk-item' + (i === state.selected_idx ? ' selected' : '');
      item.dataset.idx = i;

      var vis = document.createElement('div');
      vis.className = 'tk-vis ' + (t.is_visible ? 'on' : 'off');
      vis.dataset.idx = i;
      vis.addEventListener('click', onToggleVis);

      var mode = document.createElement('span');
      mode.className = 'tk-mode';
      mode.textContent = t.mode;

      var name = document.createElement('span');
      name.className = 'tk-name';
      name.textContent = t.name;

      item.appendChild(vis);
      item.appendChild(mode);
      item.appendChild(name);

      if (t.is_active) {
        var act = document.createElement('span');
        act.className = 'tk-active';
        act.textContent = '●';
        item.appendChild(act);
      }

      item.addEventListener('click', onSelect);
      listEl.appendChild(item);
    }
  }

  function onToggleVis(ev) { ev.stopPropagation(); post('/toggle-visibility', { index: parseInt(ev.currentTarget.dataset.idx) }).then(function(s){apply(s,true)}); }
  function onSelect(ev) { post('/select', { index: parseInt(ev.currentTarget.dataset.idx) }).then(function(s){apply(s,true)}); }

  btnMoveUp.addEventListener('click', function () { post('/move-up').then(function(s){apply(s,true)}); });
  btnMoveDn.addEventListener('click', function () { post('/move-down').then(function(s){apply(s,true)}); });
  btnSwapAB.addEventListener('click', function () { post('/swap-ab').then(function(s){apply(s,true)}); });
  btnDelete.addEventListener('click', function () { post('/delete').then(function(s){apply(s,true)}); });
  btnToggleAll.addEventListener('click', function () { allVisible = !allVisible; post('/toggle-all', { visible: allVisible }).then(function(s){apply(s,true)}); });

  btnMakeCurve.addEventListener('click', function () { post('/make-curve').then(function(s){apply(s,true)}); });
  btnMakeAB.addEventListener('click', function () { post('/make-ab').then(function(s){apply(s,true)}); });
  btnMakeBndCurve.addEventListener('click', function () { post('/make-boundary-curve').then(function(s){apply(s,true)}); });
  btnExtendA.addEventListener('click', function () { post('/extend-a').then(function(s){apply(s,true)}); });
  btnExtendB.addEventListener('click', function () { post('/extend-b').then(function(s){apply(s,true)}); });
  btnCancelTouch.addEventListener('click', function () { post('/cancel-touch').then(function(s){apply(s,true)}); });

  btnDuplicate.addEventListener('click', function () {
    if (state.selected_idx < 0) return;
    inputAction = 'duplicate';
    inpName.value = ((state.tracks || [])[state.selected_idx] || {}).name + ' Copia';
    inputPane.classList.add('show');
  });
  btnRename.addEventListener('click', function () {
    if (state.selected_idx < 0) return;
    inputAction = 'rename';
    inpName.value = ((state.tracks || [])[state.selected_idx] || {}).name;
    inputPane.classList.add('show');
  });
  btnInputOk.addEventListener('click', function () {
    inputPane.classList.remove('show');
    if (inputAction === 'duplicate') post('/duplicate', { name: inpName.value.trim() }).then(function(s){apply(s,true)});
    else if (inputAction === 'rename') post('/rename', { name: inpName.value.trim() }).then(function(s){apply(s,true)});
    inputAction = null;
  });
  btnInputCancel.addEventListener('click', function () { inputPane.classList.remove('show'); inputAction = null; });

  function closeWidget(endpoint) {
    if (closed) return; closed = true;
    try { navigator.sendBeacon(API + endpoint, '{}'); } catch (e) { }
    try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('close-hub'); } catch (e) { }
  }

  btnUse.addEventListener('click', function () { closeWidget('/use'); });
  btnCancel.addEventListener('click', function () { closeWidget('/cancel'); });
  window.addEventListener('pagehide', function () { closeWidget('/use'); });

  // ── Tap / pan / zoom ──────────────────────────────────────────────────
  cv.addEventListener('pointerdown', function (ev) {
    pointers[ev.pointerId] = { x: ev.clientX, y: ev.clientY };
    var ids = Object.keys(pointers);
    if (ids.length === 1) { drag = { x: ev.clientX, y: ev.clientY, ox: view.ox, oy: view.oy }; moved = false; }
    else if (ids.length === 2) {
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
        var f = d / pinch.d, r = cv.getBoundingClientRect(), mx = pinch.cx - r.left, my = pinch.cy - r.top;
        view.scale = pinch.scale * f; view.ox = mx - (mx - pinch.ox) * f; view.oy = my - (my - pinch.oy) * f; draw();
      }
      moved = true; return;
    }
    if (drag) {
      var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (Math.abs(dx) > 6 || Math.abs(dy) > 6) moved = true;
      view.ox = drag.ox + dx; view.oy = drag.oy + dy; draw();
    }
  });

  async function endPointer(ev, isTap) {
    delete pointers[ev.pointerId];
    if (Object.keys(pointers).length < 2) pinch = null;
    if (Object.keys(pointers).length === 0) {
      var wasDrag = drag; drag = null;
      if (isTap && wasDrag && !moved) {
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
    var r = cv.getBoundingClientRect(), mx = ev.clientX - r.left, my = ev.clientY - r.top;
    var f = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
    view.ox = mx - (mx - view.ox) * f; view.oy = my - (my - view.oy) * f; view.scale *= f; draw();
  }, { passive: false });

  window.addEventListener('resize', resize);

  (async function () { var data = await post('/open'); if (data) apply(data); })();
})();
