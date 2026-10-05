// ============================================================================
// corregir-posicion.js
// Reemplazo HTML del WinForms FormShiftPos. Ajusta el corrimiento de deriva GPS
// (drift compensation) norte/este en cm y el toggle "mantener corrimiento".
// Aplica en vivo (igual que el modal): cada botón computa el nuevo valor
// absoluto y lo manda por POST /api/aog/guidance/command con
// shift_north_<cm> / shift_east_<cm> / shift_zero / offsets_on / offsets_off.
// El estado inicial viene de GET /api/aog/shift-pos (snake_case AgpJson:
// north_cm, east_cm, offsets_on). Rango ±9999 cm.
// ============================================================================

(function () {
  'use strict';

  var LIMIT = 9999;

  var valNorth = document.getElementById('valNorth');
  var valEast  = document.getElementById('valEast');
  var btnZero  = document.getElementById('btnZero');
  var btnOff   = document.getElementById('btnOffsets');
  var statusEl = document.getElementById('statusText');

  // Estado local (espejo del backend).
  var north = 0, east = 0, offsets = false;

  function clamp(v) { return Math.max(-LIMIT, Math.min(LIMIT, v)); }

  function render() {
    valNorth.textContent = north;
    valEast.textContent = east;
    btnOff.textContent = offsets ? 'On' : 'Off';
    btnOff.classList.toggle('on', offsets);
  }

  var lastOk = true;
  async function send(cmd) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (!lastOk) { statusEl.textContent = 'en vivo'; lastOk = true; }
      if (!data.ok) console.warn('[corregir-posicion] comando rechazado:', cmd, data.error);
      return data.ok;
    } catch (e) {
      if (lastOk) { statusEl.textContent = 'sin conexión'; lastOk = false; }
      console.warn('[corregir-posicion] sin conexión:', e.message);
      return false;
    }
  }

  // ── Estado inicial ────────────────────────────────────────────────────────
  async function loadState() {
    try {
      var r = await fetch('/api/aog/shift-pos', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();
      north = clamp(Math.round(Number(d.north_cm) || 0));
      east  = clamp(Math.round(Number(d.east_cm) || 0));
      offsets = !!d.offsets_on;
      statusEl.textContent = 'en vivo'; lastOk = true;
      render();
    } catch (e) {
      statusEl.textContent = 'sin conexión'; lastOk = false;
    }
  }

  // ── Controles ─────────────────────────────────────────────────────────────
  Array.prototype.forEach.call(document.querySelectorAll('[data-step]'), function (b) {
    b.addEventListener('click', function () {
      var axis = b.getAttribute('data-axis');
      var step = parseInt(b.getAttribute('data-step'), 10);
      if (axis === 'north') {
        north = clamp(north + step);
        render();
        send('shift_north_' + north);
      } else {
        east = clamp(east + step);
        render();
        send('shift_east_' + east);
      }
    });
  });

  btnZero.addEventListener('click', function () {
    north = 0; east = 0;
    render();
    send('shift_zero');
  });

  btnOff.addEventListener('click', function () {
    offsets = !offsets;
    render();
    send(offsets ? 'offsets_on' : 'offsets_off');
  });

  render();
  loadState();
})();
