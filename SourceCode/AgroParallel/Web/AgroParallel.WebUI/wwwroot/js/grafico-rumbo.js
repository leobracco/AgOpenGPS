// ============================================================================
// grafico-rumbo.js
// Reemplazo HTML del WinForms FormGraphHeading. Grafica en vivo el rumbo GPS
// (°) y el rumbo IMU corregido (°) en un buffer rodante de 120 muestras, sobre
// <canvas>. A diferencia del gráfico XTE/dirección, los rumbos son valores
// absolutos (0-360°): el eje Y se auto-encuadra al min/max presente en los
// datos (con un padding), no se centra en cero. La diferencia GPS−IMU se
// muestra como lectura (era la gráfica inferior del WinForms). Solo lectura.
// Fuente: GET /api/aog/graph-heading (snake_case AgpJson:
// gps_heading_deg, imu_heading_deg). Muestrea a 5 Hz.
// ============================================================================

(function () {
  'use strict';

  var MAX_POINTS = 120;
  var POLL_MS = 200;

  var canvas   = document.getElementById('headingCanvas');
  var ctx      = canvas.getContext('2d');
  var valGps   = document.getElementById('valGps');
  var valImu   = document.getElementById('valImu');
  var valDiff  = document.getElementById('valDiff');
  var axisMax  = document.getElementById('axisMax');
  var axisMin  = document.getElementById('axisMin');
  var statusEl = document.getElementById('statusText');

  // Buffers rodantes.
  var gps = [];
  var imu = [];

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

  // Encuadre automático: rango [min, max] de ambas series, con padding y un
  // ancho mínimo de 2° para que una señal casi plana no colapse la escala.
  function currentRange() {
    var lo = Infinity, hi = -Infinity, i;
    for (i = 0; i < gps.length; i++) { lo = Math.min(lo, gps[i]); hi = Math.max(hi, gps[i]); }
    for (i = 0; i < imu.length; i++) { lo = Math.min(lo, imu[i]); hi = Math.max(hi, imu[i]); }
    if (!isFinite(lo) || !isFinite(hi)) { lo = 0; hi = 360; }
    var span = hi - lo;
    if (span < 2) { var mid = (hi + lo) / 2; lo = mid - 1; hi = mid + 1; span = 2; }
    var pad = span * 0.1;
    return { lo: lo - pad, hi: hi + pad };
  }

  function drawSeries(data, rng, color, w, h) {
    if (data.length < 2) return;
    var span = rng.hi - rng.lo;
    ctx.beginPath();
    ctx.lineWidth = 2;
    ctx.strokeStyle = color;
    for (var i = 0; i < data.length; i++) {
      var x = (i / (MAX_POINTS - 1)) * w;
      var y = h - ((data[i] - rng.lo) / span) * h;
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
  }

  function draw() {
    var w = canvas.clientWidth, h = canvas.clientHeight;
    var rng = currentRange();

    ctx.clearRect(0, 0, w, h);

    // Grilla suave (tercios).
    ctx.strokeStyle = css('--agp-border', '#c5cfc5');
    ctx.lineWidth = 1;
    ctx.globalAlpha = 0.5;
    [0.25, 0.5, 0.75].forEach(function (f) {
      ctx.beginPath(); ctx.moveTo(0, h * f); ctx.lineTo(w, h * f); ctx.stroke();
    });
    ctx.globalAlpha = 1;

    drawSeries(gps, rng, css('--agp-accent', '#4aba3e'), w, h);
    drawSeries(imu, rng, '#3D87C6', w, h);

    axisMax.textContent = rng.hi.toFixed(0);
    axisMin.textContent = rng.lo.toFixed(0);
  }

  var lastOk = false;
  async function poll() {
    try {
      var r = await fetch('/api/aog/graph-heading', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();

      var g = Number(d.gps_heading_deg) || 0;
      var m = Number(d.imu_heading_deg) || 0;

      gps.push(g); if (gps.length > MAX_POINTS) gps.shift();
      imu.push(m); if (imu.length > MAX_POINTS) imu.shift();

      valGps.textContent  = g.toFixed(1);
      valImu.textContent  = m.toFixed(1);
      valDiff.textContent = (g - m).toFixed(1);

      if (!lastOk) { statusEl.textContent = 'en vivo'; lastOk = true; }
      draw();
    } catch (e) {
      if (lastOk) { statusEl.textContent = 'sin conexión'; lastOk = false; }
    }
  }

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
