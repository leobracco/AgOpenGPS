// ============================================================================
// grafico-correccion.js
// Reemplazo HTML del WinForms FormCorrection. Grafica en vivo tres series en un
// buffer rodante de 120 muestras, centrado en cero (las tres son simétricas ±):
//   · Corrección por roll del IMU (m)
//   · Este (easting) crudo del fix GPS (m)
//   · Este (easting) sin corregir por roll (m)
// Solo lectura: los botones de escala reescalan el eje Y del lado cliente, igual
// que los toggles Congelar (freeze del scroll) y Poste/Movimiento (cambia cómo se
// grafica la serie de corrección, replicando isPole del FormCorrection original).
// Fuente: GET /api/aog/graph-correction (snake_case AgpJson:
// correction_distance, easting, uncorrected_easting, roll_degrees, roll_present).
// Muestrea a 5 Hz.
// ============================================================================

(function () {
  'use strict';

  var MAX_POINTS = 120;
  var POLL_MS = 200;

  var canvas    = document.getElementById('corrCanvas');
  var ctx       = canvas.getContext('2d');
  var valCorr   = document.getElementById('valCorr');
  var valEast   = document.getElementById('valEast');
  var valUncorr = document.getElementById('valUncorr');
  var valRoll   = document.getElementById('valRoll');
  var axisMax   = document.getElementById('axisMax');
  var axisMin   = document.getElementById('axisMin');
  var statusEl  = document.getElementById('statusText');

  // Buffers rodantes.
  var corrSeries   = [];
  var eastSeries   = [];
  var uncorrSeries = [];

  // Escala del eje Y. yMax = ±yScale (m); autoScale ajusta a datos.
  var yScale = 0.1;
  var autoScale = true;

  // Toggles cliente (igual que FormCorrection).
  var isFrozen = false;   // btnScroll: congela el avance del buffer.
  var isPole = true;      // btnPoleOrMoving: corrección = corr (Poste) o corr+uncorr (Movimiento).

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
    // Auto: mayor magnitud presente, con un piso de 0.05 m para que no colapse.
    var m = 0.05;
    for (var i = 0; i < corrSeries.length; i++)   m = Math.max(m, Math.abs(corrSeries[i]));
    for (var j = 0; j < eastSeries.length; j++)   m = Math.max(m, Math.abs(eastSeries[j]));
    for (var k = 0; k < uncorrSeries.length; k++) m = Math.max(m, Math.abs(uncorrSeries[k]));
    // Redondeo a 2 decimales hacia arriba.
    return Math.ceil(m * 100) / 100;
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

    drawSeries(uncorrSeries, scale, '#E0A030', w, h);
    drawSeries(eastSeries,   scale, '#3D87C6', w, h);
    drawSeries(corrSeries,   scale, css('--agp-accent', '#4aba3e'), w, h);

    axisMax.textContent = scale.toFixed(2);
    axisMin.textContent = '-' + scale.toFixed(2);
  }

  var lastOk = false;
  async function poll() {
    try {
      var r = await fetch('/api/aog/graph-correction', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();

      var corr   = Number(d.correction_distance) || 0;
      var east   = Number(d.easting) || 0;
      var uncorr = Number(d.uncorrected_easting) || 0;

      // Poste: la corrección es tal cual. Movimiento: corr + sin-corregir
      // (mismo criterio que isPole en FormCorrection).
      var corrPlot = isPole ? corr : (corr + uncorr);

      if (!isFrozen) {
        corrSeries.push(corrPlot);   if (corrSeries.length > MAX_POINTS)   corrSeries.shift();
        eastSeries.push(east);       if (eastSeries.length > MAX_POINTS)   eastSeries.shift();
        uncorrSeries.push(uncorr);   if (uncorrSeries.length > MAX_POINTS) uncorrSeries.shift();
      }

      valCorr.textContent   = corr.toFixed(2);
      valEast.textContent   = east.toFixed(2);
      valUncorr.textContent = uncorr.toFixed(2);
      valRoll.textContent   = d.roll_present ? ((Number(d.roll_degrees) || 0).toFixed(1) + '°') : '—';

      if (!lastOk) { statusEl.textContent = 'en vivo'; lastOk = true; }
      draw();
    } catch (e) {
      if (lastOk) { statusEl.textContent = 'sin conexión'; lastOk = false; }
    }
  }

  // ── Controles de escala (solo cliente) ────────────────────────────────────
  document.getElementById('btnGainUp').addEventListener('click', function () {
    autoScale = false;
    yScale = Math.min(10, +(yScale * 2).toFixed(2));
    draw();
  });
  document.getElementById('btnGainDown').addEventListener('click', function () {
    autoScale = false;
    yScale = Math.max(0.02, +(yScale / 2).toFixed(2));
    draw();
  });
  document.getElementById('btnGainAuto').addEventListener('click', function () {
    autoScale = !autoScale;
    draw();
  });

  // ── Toggles (solo cliente) ─────────────────────────────────────────────────
  document.getElementById('btnFreeze').addEventListener('click', function () {
    isFrozen = !isFrozen;
    this.classList.toggle('on', isFrozen);
    this.textContent = isFrozen ? 'Reanudar' : 'Congelar';
  });
  document.getElementById('btnMode').addEventListener('click', function () {
    isPole = !isPole;
    this.classList.toggle('on', isPole);
    this.textContent = isPole ? 'Poste' : 'Movimiento';
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
