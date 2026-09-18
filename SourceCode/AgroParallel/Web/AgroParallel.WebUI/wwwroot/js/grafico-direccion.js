// ============================================================================
// grafico-direccion.js
// Reemplazo HTML del WinForms FormGraphSteer. Grafica en vivo el ángulo de
// dirección real (WAS, °) y el ángulo seteado por el guiado (°) en un buffer
// rodante de 120 muestras, centrado en cero (la dirección es simétrica ±).
// Solo lectura: los botones de escala reescalan el eje Y del lado cliente
// (igual que hacían btnGainUp/Down/Auto), sin tocar nada en PilotX.
// Fuente: GET /api/aog/graph-steer (snake_case AgpJson:
// actual_steer_deg, set_steer_deg). Muestrea a 5 Hz.
// ============================================================================

(function () {
  'use strict';

  var MAX_POINTS = 120;
  var POLL_MS = 200;

  var canvas   = document.getElementById('steerCanvas');
  var ctx      = canvas.getContext('2d');
  var valAct   = document.getElementById('valAct');
  var valSet   = document.getElementById('valSet');
  var axisMax  = document.getElementById('axisMax');
  var axisMin  = document.getElementById('axisMin');
  var statusEl = document.getElementById('statusText');

  // Buffers rodantes.
  var actual = [];
  var setpt  = [];

  // Escala del eje Y. yMax = ±yScale (°); autoScale ajusta a datos.
  var yScale = 40;
  var autoScale = false;

  // ── Escala fija del canvas al tamaño real (nitidez en HiDPI) ──────────────
  function fitCanvas() {
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth, h = canvas.clientHeight;
    canvas.width  = Math.max(1, Math.round(w * dpr));
    canvas.height = Math.max(1, Math.round(h * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  window.addEventListener('resize', function () { fitCanvas(); draw(); });

  function css(varName, fallback) {
    var v = getComputedStyle(document.documentElement).getPropertyValue(varName);
    return (v && v.trim()) || fallback;
  }

  function currentScale() {
    if (!autoScale) return yScale;
    // Auto: mayor magnitud presente, con un piso de 5 para que no colapse.
    var m = 5;
    for (var i = 0; i < actual.length; i++) m = Math.max(m, Math.abs(actual[i]));
    for (var j = 0; j < setpt.length; j++)  m = Math.max(m, Math.abs(setpt[j]));
    return Math.ceil(m / 5) * 5;
  }

  function drawSeries(data, scale, color, w, h) {
    if (data.length < 2) return;
    ctx.beginPath();
    ctx.lineWidth = 2;
    ctx.strokeStyle = color;
    for (var i = 0; i < data.length; i++) {
      var x = (i / (MAX_POINTS - 1)) * w;
      var y = h / 2 - (data[i] / scale) * (h / 2);
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
  }

  function draw() {
    var w = canvas.clientWidth, h = canvas.clientHeight;
    var scale = currentScale();

    ctx.clearRect(0, 0, w, h);

    // Línea cero + tercios (grilla suave).
    ctx.strokeStyle = css('--agp-border', '#c5cfc5');
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, h / 2); ctx.lineTo(w, h / 2); ctx.stroke();
    ctx.globalAlpha = 0.5;
    [0.25, 0.75].forEach(function (f) {
      ctx.beginPath(); ctx.moveTo(0, h * f); ctx.lineTo(w, h * f); ctx.stroke();
    });
    ctx.globalAlpha = 1;

    drawSeries(actual, scale, css('--agp-accent', '#4aba3e'), w, h);
    drawSeries(setpt,  scale, '#3D87C6', w, h);

    axisMax.textContent = scale;
    axisMin.textContent = '-' + scale;
  }

  var lastOk = false;
  async function poll() {
    try {
      var r = await fetch('/api/aog/graph-steer', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();

      var a = Number(d.actual_steer_deg) || 0;
      var s = Number(d.set_steer_deg) || 0;

      actual.push(a); if (actual.length > MAX_POINTS) actual.shift();
      setpt.push(s);  if (setpt.length > MAX_POINTS)  setpt.shift();

      valAct.textContent = a.toFixed(1);
      valSet.textContent = s.toFixed(1);

      if (!lastOk) { statusEl.textContent = 'en vivo'; lastOk = true; }
      draw();
    } catch (e) {
      if (lastOk) { statusEl.textContent = 'sin conexión'; lastOk = false; }
    }
  }

  // ── Controles de escala (solo cliente) ────────────────────────────────────
  document.getElementById('btnGainUp').addEventListener('click', function () {
    autoScale = false;
    yScale = Math.min(180, yScale * 2);
    draw();
  });
  document.getElementById('btnGainDown').addEventListener('click', function () {
    autoScale = false;
    yScale = Math.max(5, Math.round(yScale / 2));
    draw();
  });
  document.getElementById('btnGainAuto').addEventListener('click', function () {
    autoScale = !autoScale;
    draw();
  });

  // ── Ciclo de vida ─────────────────────────────────────────────────────────
  var handle = null;
  function start() { if (!handle) handle = setInterval(poll, POLL_MS); }
  function stop()  { if (handle) { clearInterval(handle); handle = null; } }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stop();
    else { poll(); start(); }
  });

  fitCanvas();
  draw();
  poll();
  start();
})();
