// ============================================================================
// suavizar-ab.js
// Reemplazo HTML del WinForms FormSmoothAB. Suaviza la curva AB activa. La vista
// previa la dibuja PilotX sobre el mapa principal (curve.isSmoothWindowOpen +
// curve.smooList); acá solo mandamos comandos por POST /api/aog/guidance/command:
//   smooth_ab_open        → abre la ventana de suavizado (habilita la preview)
//   smooth_ab_set_<n>     → recalcula la preview con nivel n (2..100)
//   smooth_ab_apply       → aplica en memoria ("Por ahora")
//   smooth_ab_save        → aplica y guarda a archivo ("A archivo")
//   smooth_ab_cancel      → descarta la preview ("Cancelar")
// No hay estado inicial que leer: el nivel arranca en 20 igual que el form
// original. Si el operario sale sin aplicar, mandamos cancel para no dejar la
// preview colgada en el mapa.
// ============================================================================

(function () {
  'use strict';

  var MIN = 2, MAX = 100;

  var valLevel = document.getElementById('valLevel');
  var btnLess  = document.getElementById('btnLess');
  var btnMore  = document.getElementById('btnMore');
  var btnOk    = document.getElementById('btnOk');
  var btnSave  = document.getElementById('btnSave');
  var btnCancel= document.getElementById('btnCancel');
  var statusEl = document.getElementById('statusText');

  var level = 20;
  var settled = false; // true tras aplicar/guardar/cancelar (no re-mandar cancel al salir)
  var lastOk = true;

  function clamp(v) { return Math.max(MIN, Math.min(MAX, v)); }

  function render() { valLevel.textContent = level; }

  async function send(cmd) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (!lastOk) { statusEl.textContent = 'en vivo'; lastOk = true; }
      if (!data.ok) console.warn('[suavizar-ab] comando rechazado:', cmd, data.error);
      return data.ok;
    } catch (e) {
      if (lastOk) { statusEl.textContent = 'sin conexión'; lastOk = false; }
      console.warn('[suavizar-ab] sin conexión:', e.message);
      return false;
    }
  }

  function setLevel(v) {
    level = clamp(v);
    render();
    send('smooth_ab_set_' + level);
  }

  // ── Controles ─────────────────────────────────────────────────────────────
  btnLess.addEventListener('click', function () { setLevel(level - 1); });
  btnMore.addEventListener('click', function () { setLevel(level + 1); });

  btnOk.addEventListener('click', async function () {
    if (await send('smooth_ab_apply')) { settled = true; statusEl.textContent = 'aplicado'; }
  });
  btnSave.addEventListener('click', async function () {
    if (await send('smooth_ab_save')) { settled = true; statusEl.textContent = 'guardado'; }
  });
  btnCancel.addEventListener('click', async function () {
    if (await send('smooth_ab_cancel')) { settled = true; statusEl.textContent = 'cancelado'; }
  });

  // Al salir sin aplicar: descartar la preview para no dejarla colgada en el mapa.
  window.addEventListener('pagehide', function () {
    if (settled) return;
    try {
      var body = JSON.stringify({ cmd: 'smooth_ab_cancel' });
      if (navigator.sendBeacon) {
        navigator.sendBeacon('/api/aog/guidance/command',
          new Blob([body], { type: 'application/json' }));
      } else {
        fetch('/api/aog/guidance/command', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: body, keepalive: true
        });
      }
    } catch (e) { /* best-effort */ }
  });

  // ── Arranque ──────────────────────────────────────────────────────────────
  render();
  send('smooth_ab_open');
})();
