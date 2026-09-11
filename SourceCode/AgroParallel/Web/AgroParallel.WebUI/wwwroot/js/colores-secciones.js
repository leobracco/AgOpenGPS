// ============================================================================
// colores-secciones.js
// Reemplazo HTML del WinForms FormColorSection. Asigna un color a cada una de las
// 16 secciones y activa/desactiva el modo multicolor. Lee el estado inicial de
// GET /api/aog/section-colors (AgpJson: colors[16] hex "#RRGGBB", multi_color) y
// aplica por POST /api/aog/guidance/command con
// cmd = sec_colors_<hex1>_..._<hex16>_<0|1> (hex sin '#', minúsculas).
// Los selectores quedan deshabilitados cuando el modo multicolor está apagado
// (los colores por sección sólo se ven en el mapa con multicolor activo).
// ============================================================================

(function () {
  'use strict';

  var grid     = document.getElementById('grid');
  var chkMulti = document.getElementById('chkMulti');
  var btnApply = document.getElementById('btnApply');
  var btnReset = document.getElementById('btnReset');
  var statusEl = document.getElementById('statusText');

  // Estado guardado (para Restablecer) y los 16 inputs de color.
  var savedColors = [], savedMulti = false;
  var inputs = [];

  // Normaliza "#RRGGBB" → "rrggbb" (minúsculas, sin '#') para el comando.
  function hexBody(v) {
    return String(v || '#000000').replace('#', '').toLowerCase();
  }

  // Construye los 16 selectores una sola vez.
  (function buildGrid() {
    for (var i = 0; i < 16; i++) {
      var cell = document.createElement('div');
      cell.className = 'cs-cell';
      var num = document.createElement('div');
      num.className = 'cs-num';
      num.textContent = 'Sección ' + (i + 1);
      var inp = document.createElement('input');
      inp.type = 'color';
      inp.value = '#000000';
      cell.appendChild(num);
      cell.appendChild(inp);
      grid.appendChild(cell);
      inputs.push(inp);
    }
  })();

  // Habilita/deshabilita los selectores según el modo multicolor.
  function applyEnabled() {
    var on = chkMulti.checked;
    for (var i = 0; i < inputs.length; i++) inputs[i].disabled = !on;
  }

  // ── Estado inicial ────────────────────────────────────────────────────────
  async function loadState() {
    try {
      var r = await fetch('/api/aog/section-colors', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();
      var cols = Array.isArray(d.colors) ? d.colors : [];
      savedColors = [];
      for (var i = 0; i < 16; i++) {
        var hex = cols[i] || '#000000';
        savedColors.push(hex);
        inputs[i].value = hex;
      }
      savedMulti = !!d.multi_color;
      chkMulti.checked = savedMulti;
      applyEnabled();
      statusEl.textContent = 'en vivo';
      btnApply.disabled = false;
    } catch (e) {
      statusEl.textContent = 'sin conexión';
      btnApply.disabled = true;
    }
  }

  async function apply() {
    var tokens = [];
    for (var i = 0; i < 16; i++) tokens.push(hexBody(inputs[i].value));
    tokens.push(chkMulti.checked ? '1' : '0');
    var cmd = 'sec_colors_' + tokens.join('_');
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
        // Nuevo baseline para Restablecer.
        savedColors = [];
        for (var i = 0; i < 16; i++) savedColors.push(inputs[i].value);
        savedMulti = chkMulti.checked;
      } else {
        statusEl.textContent = 'rechazado';
        console.warn('[colores-secciones] comando rechazado:', data.error);
      }
    } catch (e) {
      statusEl.textContent = 'sin conexión';
      console.warn('[colores-secciones] sin conexión:', e.message);
    } finally {
      btnApply.disabled = false;
    }
  }

  // ── Controles ─────────────────────────────────────────────────────────────
  chkMulti.addEventListener('change', applyEnabled);
  btnApply.addEventListener('click', apply);
  btnReset.addEventListener('click', function () {
    for (var i = 0; i < 16; i++) inputs[i].value = savedColors[i] || '#000000';
    chkMulti.checked = savedMulti;
    applyEnabled();
  });

  loadState();
})();
