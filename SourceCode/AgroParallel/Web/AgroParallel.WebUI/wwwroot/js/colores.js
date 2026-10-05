// ============================================================================
// colores.js
// Reemplazo HTML del WinForms FormColor. Colores de marco, campo y texto para
// modo día y noche, suavizado de cámara y selección de modo. Lee el estado inicial
// de GET /api/aog/display-colors (AgpJson: frame_day, frame_night, field_day,
// field_night, text_day, text_night hex "#RRGGBB"; cam_smooth 0..100; is_day) y
// aplica por POST /api/aog/guidance/command con
// cmd = display_colors_<frameDay>_<frameNight>_<fieldDay>_<fieldNight>_<textDay>
//       _<textNight>_<camSmooth>_<0|1> (hex sin '#', minúsculas).
// ============================================================================

(function () {
  'use strict';

  var frameDay   = document.getElementById('frameDay');
  var frameNight = document.getElementById('frameNight');
  var fieldDay   = document.getElementById('fieldDay');
  var fieldNight = document.getElementById('fieldNight');
  var textDay    = document.getElementById('textDay');
  var textNight  = document.getElementById('textNight');
  var camSmooth  = document.getElementById('camSmooth');
  var camVal     = document.getElementById('camVal');
  var btnDay     = document.getElementById('btnDay');
  var btnNight   = document.getElementById('btnNight');
  var btnApply   = document.getElementById('btnApply');
  var btnReset   = document.getElementById('btnReset');
  var statusEl   = document.getElementById('statusText');

  // Valores por defecto (mismos que el "Restablecer" del FormColor original).
  var DEFAULTS = {
    frame_day: '#D2D2E6', frame_night: '#323241',
    field_day: '#64647D', field_night: '#3C3C3C',
    text_day:  '#0A0A14', text_night:  '#E6E6E6',
    cam_smooth: 25, is_day: true
  };

  // Estado guardado (para Restablecer) e input de modo día/noche.
  var saved = null;
  var isDay = true;

  function hexBody(v) { return String(v || '#000000').replace('#', '').toLowerCase(); }

  function setMode(day) {
    isDay = !!day;
    btnDay.classList.toggle('active', isDay);
    btnNight.classList.toggle('active', !isDay);
  }

  function fillFrom(d) {
    frameDay.value   = d.frame_day   || '#000000';
    frameNight.value = d.frame_night || '#000000';
    fieldDay.value   = d.field_day   || '#000000';
    fieldNight.value = d.field_night || '#000000';
    textDay.value    = d.text_day    || '#000000';
    textNight.value  = d.text_night  || '#000000';
    camSmooth.value  = (d.cam_smooth != null ? d.cam_smooth : 0);
    camVal.textContent = camSmooth.value + ' %';
    setMode(!!d.is_day);
  }

  // ── Estado inicial ────────────────────────────────────────────────────────
  async function loadState() {
    try {
      var r = await fetch('/api/aog/display-colors', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();
      saved = d;
      fillFrom(d);
      statusEl.textContent = 'en vivo';
      btnApply.disabled = false;
    } catch (e) {
      statusEl.textContent = 'sin conexión';
      btnApply.disabled = true;
    }
  }

  async function apply() {
    var tokens = [
      hexBody(frameDay.value), hexBody(frameNight.value),
      hexBody(fieldDay.value), hexBody(fieldNight.value),
      hexBody(textDay.value),  hexBody(textNight.value),
      String(parseInt(camSmooth.value, 10) || 0),
      isDay ? '1' : '0'
    ];
    var cmd = 'display_colors_' + tokens.join('_');
    btnApply.disabled = true;
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (data.ok) {
        statusEl.textContent = 'aplicado';
        saved = {
          frame_day: frameDay.value, frame_night: frameNight.value,
          field_day: fieldDay.value, field_night: fieldNight.value,
          text_day: textDay.value, text_night: textNight.value,
          cam_smooth: parseInt(camSmooth.value, 10) || 0, is_day: isDay
        };
      } else {
        statusEl.textContent = 'rechazado';
        console.warn('[colores] comando rechazado:', data.error);
      }
    } catch (e) {
      statusEl.textContent = 'sin conexión';
      console.warn('[colores] sin conexión:', e.message);
    } finally {
      btnApply.disabled = false;
    }
  }

  // ── Controles ─────────────────────────────────────────────────────────────
  camSmooth.addEventListener('input', function () { camVal.textContent = camSmooth.value + ' %'; });
  btnDay.addEventListener('click', function () { setMode(true); });
  btnNight.addEventListener('click', function () { setMode(false); });
  btnApply.addEventListener('click', apply);
  btnReset.addEventListener('click', function () { fillFrom(DEFAULTS); });

  loadState();
})();
