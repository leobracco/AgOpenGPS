// ============================================================================
// cabecera.js
// Reemplazo del flujo "Build Around" de FormHeadLine. El canvas es preview
// read-only: dibuja el contorno (fence) y la cabecera (headland) en E/N metros,
// con fit-to-bounds + pan (drag) + zoom (rueda). La geometría corre en C#:
//   GET  /api/headland/state              → estado inicial + geometría
//   POST /api/headland/build   {distance} → offset (distancia en unidades display)
//   POST /api/headland/reset              → cabecera = contorno
//   POST /api/headland/off                → apaga la cabecera
//   POST /api/headland/section-controlled {on}
// Sin contorno: banner + Construir deshabilitado.
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

  var fence = [];      // [[e,n], ...]
  var headland = [];   // [[e,n], ...]
  var units = 'm';
  var toolWidthM = 0;
  var hasBoundary = false;

  // Vista (pan/zoom) — solo afecta el dibujo, no el modelo.
  var view = { scale: 1, ox: 0, oy: 0, fitted: false };
  var drag = null;

  function setStatus(txt) {
    statusEl.textContent = txt;
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
    var pts = fence.length ? fence : headland;
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

  // Modelo (E/N metros) → pantalla (px CSS).
  function toScreen(e, n) {
    return { x: e * view.scale + view.ox, y: -n * view.scale + view.oy };
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

  function draw() {
    var r = cv.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);
    drawPolyline(fence, '#8a978c', 2, true);    // contorno gris
    drawPolyline(headland, '#4ABA3E', 3, true); // cabecera verde
  }

  // ── Estado ────────────────────────────────────────────────────────────────
  function applyState(s) {
    if (!s || s.has_field === false) {
      hasBoundary = false;
      fence = []; headland = [];
      warnBox.classList.add('show');
      btnBuild.disabled = true; btnWidth.disabled = true; btnReset.disabled = true; btnOff.disabled = true;
      setStatus('sin contorno');
      view.fitted = false; draw();
      return;
    }
    hasBoundary = !!s.has_boundary;
    fence = s.fence || [];
    headland = s.headland || [];
    units = s.units || 'm';
    toolWidthM = s.tool_width_m || 0;
    unitLabel.textContent = units;
    chkSection.checked = !!s.is_section_controlled;
    warnBox.classList.toggle('show', !hasBoundary);
    var dis = !hasBoundary;
    btnBuild.disabled = dis; btnWidth.disabled = dis; btnReset.disabled = dis; btnOff.disabled = dis;
    setStatus(s.is_headland_on ? 'cabecera activa' : 'sin cabecera');
    view.fitted = false;
    resize();
  }

  async function loadState() {
    try {
      var res = await fetch(API + '/state');
      var data = await res.json();
      applyState(data);
    } catch (e) {
      setStatus('sin conexión');
      warnBox.textContent = 'Sin conexión con PilotX.';
      warnBox.classList.add('show');
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
    } catch (e) {
      setStatus('sin conexión');
      return null;
    }
  }

  function applyResult(data) {
    if (!data) return;
    if (data.headland) headland = data.headland;
    if (typeof data.is_headland_on === 'boolean')
      setStatus(data.is_headland_on ? 'cabecera activa' : 'sin cabecera');
    if (data.ok === false && data.error) console.warn('[cabecera]', data.error);
    draw();
  }

  // ── Controles ───────────────────────────────────────────────────────────────
  btnWidth.addEventListener('click', function () {
    if (!toolWidthM) return;
    var disp = units === 'ft' ? toolWidthM * 3.28084 : toolWidthM;
    inpDist.value = (Math.round(disp * 10) / 10).toString();
  });

  btnBuild.addEventListener('click', async function () {
    var d = parseFloat(inpDist.value);
    if (isNaN(d) || d < 0) d = 0;
    setStatus('construyendo…');
    applyResult(await post('/build', { distance: d }));
  });

  btnReset.addEventListener('click', async function () {
    applyResult(await post('/reset', {}));
  });

  btnOff.addEventListener('click', async function () {
    var data = await post('/off', {});
    if (data) { headland = data.headland || []; setStatus('sin cabecera'); draw(); }
  });

  chkSection.addEventListener('change', async function () {
    await post('/section-controlled', { on: chkSection.checked });
  });

  // ── Pan / zoom (solo vista) ─────────────────────────────────────────────────
  cv.addEventListener('pointerdown', function (ev) {
    drag = { x: ev.clientX, y: ev.clientY, ox: view.ox, oy: view.oy };
    cv.setPointerCapture(ev.pointerId);
  });
  cv.addEventListener('pointermove', function (ev) {
    if (!drag) return;
    view.ox = drag.ox + (ev.clientX - drag.x);
    view.oy = drag.oy + (ev.clientY - drag.y);
    draw();
  });
  cv.addEventListener('pointerup', function () { drag = null; });
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

  // ── Arranque ────────────────────────────────────────────────────────────────
  loadState();
})();
