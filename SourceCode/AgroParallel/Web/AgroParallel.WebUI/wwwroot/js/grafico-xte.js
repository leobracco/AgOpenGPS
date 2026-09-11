// ============================================================================
// grafico-xte.js
// Reemplazo HTML del WinForms FormGraphXTE. Grafica en vivo el error de rumbo
// (°) y el error de seguimiento XTE (cm) en un buffer rodante de 120 muestras,
// dibujado sobre <canvas>. Solo lectura: los botones de escala reescalan el
// eje Y del lado cliente (igual que hacían btnGainUp/Down/Auto), sin tocar
// nada en PilotX. Fuente: GET /api/aog/graph-xte (snake_case AgpJson:
// heading_error_deg, xte_cm). Muestrea a 5 Hz.
// ============================================================================

(function () {
  'use strict';

  var MAX_POINTS = 120;
  var POLL_MS = 200;

  var canvas   = document.getElementById('xteCanvas');
  var ctx      = canvas.getContext('2d');
  var valHe    = document.getElementById('valHe');
  var valXte   = document.getElementById('valXte');
  var axisMax  = document.getElementById('axisMax');
  var axisMin  = document.getElementById('axisMin');
  var statusEl = document.getElementById('statusText');

  // Buffers rodantes.
  var heErr = [];
  var xte   = [];

  // Escala del eje Y. yMax = ±yScale (cm/°); null = modo Auto (ajusta a datos).
  var yScale = 80;
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
    // Auto: mayor magnitud presente, con un piso de 10 para que no colapse.
    var m = 10;
    for (var i = 0; i < heErr.length; i++) m = Math.max(m, Math.abs(heErr[i]));
    for (var j = 0; j < xte.length; j++)   m = Math.max(m, Math.abs(xte[j]));
    return Math.ceil(m / 10) * 10;
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

    drawSeries(heErr, scale, css('--agp-accent', '#4aba3e'), w, h);
    drawSeries(xte,   scale, '#3D87C6', w, h);

    axisMax.textContent = scale;
    axisMin.textContent = '-' + scale;
  }

  var lastOk = false;
  async function poll() {
    try {
      var r = await fetch('/api/aog/graph-xte', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();

      var he = Number(d.heading_error_deg) || 0;
      var xt = Number(d.xte_cm) || 0;

      // El motor devuelve el XTE crudo del guiado (mismo valor que graficaba
      // la ventana nativa). Cuando el tractor no está sobre ninguna guía, ese
      // valor no es un error de guiado sino una distancia enorme (sentinel):
      // se acota para que no destruya la escala del gráfico, igual que la
      // ventana vieja, que tenía escala fija y simplemente lo recortaba.
      var XTE_MAX_CM = 5120;
      if (xt > XTE_MAX_CM) xt = XTE_MAX_CM;
      else if (xt < -XTE_MAX_CM) xt = -XTE_MAX_CM;

      heErr.push(he); if (heErr.length > MAX_POINTS) heErr.shift();
      xte.push(xt);   if (xte.length > MAX_POINTS)   xte.shift();

      valHe.textContent  = he.toFixed(1);
      valXte.textContent = Math.round(xt);

      if (!lastOk) { statusEl.textContent = 'en vivo'; lastOk = true; }
      draw();
    } catch (e) {
      if (lastOk) { statusEl.textContent = 'sin conexión'; lastOk = false; }
    }
  }

  // ── Controles de escala (solo cliente) ────────────────────────────────────
  document.getElementById('btnGainUp').addEventListener('click', function () {
    autoScale = false;
    yScale = Math.min(5120, yScale * 2);
    draw();
  });
  document.getElementById('btnGainDown').addEventListener('click', function () {
    autoScale = false;
    yScale = Math.max(10, Math.round(yScale / 2));
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
