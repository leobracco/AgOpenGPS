// ============================================================================
// direccion.js — UI de configuración de AutoSteer (clon de FormSteer) para el
// Hub de PilotX. Paso 1 visual: maneja los tabs, el teclado virtual, y los
// controles (sliders / nud / checkboxes / segmentados).
//
// Persistencia best-effort contra el backend:
//   · GET  /api/steer/config    → popula los controles (si 404, quedan defaults)
//   · POST /api/steer/config    → guarda el objeto serializado
//   · POST /api/steer/zero-was  → pone el WAS en cero
// Todos los fetch van envueltos en try/catch: si el endpoint todavía no existe,
// la página sigue funcionando con los valores por defecto.
//
// Convención de nombres: snake_case en el wire (AgpJson del Hub), camelCase en
// los data-key del markup. serialize()/apply() traducen entre ambos.
// ============================================================================
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  // --------------------------------------------------------------------------
  // Tabs (mismo mecanismo que corex-ecu.js)
  // --------------------------------------------------------------------------
  function initTabs() {
    document.querySelectorAll('.tab-btn').forEach(function (b) {
      b.addEventListener('click', function () {
        var tab = b.getAttribute('data-tab');
        document.querySelectorAll('.tab-btn').forEach(function (x) {
          x.classList.toggle('active', x === b);
        });
        document.querySelectorAll('.tab-pane').forEach(function (p) {
          p.classList.toggle('active', p.getAttribute('data-tab') === tab);
        });
      });
    });
  }

  // Nota: el teclado virtual (keyboard.js) se autoengancha por focusin a todos
  // los <input type=number> de la página — no hace falta invocarlo a mano.

  // --------------------------------------------------------------------------
  // Sliders — display del valor + botones de nudge
  // --------------------------------------------------------------------------
  function fmtSlider(range) {
    var raw = parseFloat(range.value);
    var scale = parseFloat(range.getAttribute('data-scale') || '1');
    var dec = parseInt(range.getAttribute('data-dec') || '0', 10);
    var display = range.getAttribute('data-display');
    var v;
    if (display === 'minus10') {
      v = raw - 10;              // U-turn: la UI del original muestra value-10
      return v.toString();
    }
    v = raw * scale;
    return v.toFixed(dec);
  }

  function updateSliderLabel(range) {
    var key = range.getAttribute('data-key');
    var out = document.querySelector('.slider-val[data-for="' + key + '"]');
    if (out) out.textContent = fmtSlider(range);
    // Recalcular el ángulo de cero del WAS cuando cambia offset o cuentas/grado.
    if (key === 'wasOffset' || key === 'countsPerDegree') updateWasZeroAngle();
  }

  function updateWasZeroAngle() {
    var offR = document.querySelector('input[data-key="wasOffset"]');
    var cpdR = document.querySelector('input[data-key="countsPerDegree"]');
    var el = $('wasZeroAngle');
    if (!offR || !cpdR || !el) return;
    var offset = parseFloat(offR.value);
    var cpd = parseFloat(cpdR.value) || 1;
    el.textContent = (offset / cpd).toFixed(2);
  }

  function initSliders() {
    document.querySelectorAll('input[type=range][data-key]').forEach(function (range) {
      range.addEventListener('input', function () { updateSliderLabel(range); });
      updateSliderLabel(range);
    });
    document.querySelectorAll('.nudge[data-nudge]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var key = btn.getAttribute('data-nudge');
        var dir = parseInt(btn.getAttribute('data-dir'), 10) || 1;
        var range = document.querySelector('input[type=range][data-key="' + key + '"]');
        if (!range) return;
        var step = parseFloat(range.step || '1') || 1;
        var next = parseFloat(range.value) + dir * step;
        var min = parseFloat(range.min), max = parseFloat(range.max);
        if (next < min) next = min;
        if (next > max) next = max;
        range.value = next;
        updateSliderLabel(range);
      });
    });
  }

  // --------------------------------------------------------------------------
  // Checkboxes (chk-row) — mutuamente exclusivos los sensores
  // --------------------------------------------------------------------------
  var EXCLUSIVE_SENSORS = ['encoder', 'pressureSensor', 'currentSensor'];

  function setChk(key, on) {
    var row = document.querySelector('.chk-row[data-key="' + key + '"]');
    if (row) row.classList.toggle('on', !!on);
  }
  function getChk(key) {
    var row = document.querySelector('.chk-row[data-key="' + key + '"]');
    return row ? row.classList.contains('on') : false;
  }

  function initCheckboxes() {
    document.querySelectorAll('.chk-row[data-key]').forEach(function (row) {
      row.addEventListener('click', function () {
        var key = row.getAttribute('data-key');
        var willBeOn = !row.classList.contains('on');
        // Los 3 sensores de giro son excluyentes (como en el original).
        if (willBeOn && EXCLUSIVE_SENSORS.indexOf(key) >= 0) {
          EXCLUSIVE_SENSORS.forEach(function (k) { if (k !== key) setChk(k, false); });
        }
        row.classList.toggle('on', willBeOn);
      });
    });
  }

  // --------------------------------------------------------------------------
  // Segmentados (combos del original)
  // --------------------------------------------------------------------------
  function setSeg(name, val) {
    var grp = document.querySelector('.seg[data-seg="' + name + '"]');
    if (!grp) return;
    grp.querySelectorAll('.seg-btn').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-val') === val);
    });
  }
  function getSeg(name) {
    var grp = document.querySelector('.seg[data-seg="' + name + '"]');
    if (!grp) return null;
    var on = grp.querySelector('.seg-btn.on');
    return on ? on.getAttribute('data-val') : null;
  }
  function initSegments() {
    document.querySelectorAll('.seg[data-seg]').forEach(function (grp) {
      grp.querySelectorAll('.seg-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
          grp.querySelectorAll('.seg-btn').forEach(function (b) { b.classList.remove('on'); });
          btn.classList.add('on');
        });
      });
    });
  }

  // --------------------------------------------------------------------------
  // Serialización de todos los controles → objeto de config (camelCase)
  // --------------------------------------------------------------------------
  function collectConfig() {
    var cfg = {};
    document.querySelectorAll('input[type=range][data-key]').forEach(function (r) {
      cfg[r.getAttribute('data-key')] = parseFloat(r.value);
    });
    document.querySelectorAll('input[type=number][data-key]').forEach(function (n) {
      cfg[n.getAttribute('data-key')] = parseFloat(n.value);
    });
    document.querySelectorAll('.chk-row[data-key]').forEach(function (row) {
      cfg[row.getAttribute('data-key')] = row.classList.contains('on');
    });
    document.querySelectorAll('.seg[data-seg]').forEach(function (grp) {
      cfg[grp.getAttribute('data-seg')] = getSeg(grp.getAttribute('data-seg'));
    });
    return cfg;
  }

  // Aplica un objeto de config (parcial) a los controles. Ignora claves ausentes.
  function applyConfig(cfg) {
    if (!cfg || typeof cfg !== 'object') return;
    document.querySelectorAll('input[type=range][data-key]').forEach(function (r) {
      var k = r.getAttribute('data-key');
      if (cfg[k] !== undefined && cfg[k] !== null && !isNaN(cfg[k])) {
        r.value = cfg[k];
        updateSliderLabel(r);
      }
    });
    document.querySelectorAll('input[type=number][data-key]').forEach(function (n) {
      var k = n.getAttribute('data-key');
      if (cfg[k] !== undefined && cfg[k] !== null && !isNaN(cfg[k])) n.value = cfg[k];
    });
    document.querySelectorAll('.chk-row[data-key]').forEach(function (row) {
      var k = row.getAttribute('data-key');
      if (cfg[k] !== undefined) setChk(k, !!cfg[k]);
    });
    document.querySelectorAll('.seg[data-seg]').forEach(function (grp) {
      var name = grp.getAttribute('data-seg');
      if (cfg[name]) setSeg(name, cfg[name]);
    });
    updateWasZeroAngle();
  }

  // --------------------------------------------------------------------------
  // Carga / guardado contra el backend (best-effort)
  // --------------------------------------------------------------------------
  function loadConfig() {
    fetch('/api/steer/config', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (j) {
          applyConfig(j);
          setPill(true, 'Configuración cargada');
        } else {
          setPill(false, 'Sin config guardada (defaults)');
        }
      })
      .catch(function () {
        // Endpoint aún inexistente → la UI arranca con los valores por defecto.
        setPill(false, 'Sin conexión (defaults)');
      });
  }

  function saveConfig() {
    var cfg = collectConfig();
    $('saveMsg').textContent = 'Guardando…';
    fetch('/api/steer/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cfg)
    })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) {
        if (j && j.ok === false) {
          $('saveMsg').textContent = (j.error_code || 'AGP-NET-201') + ' · ' + (j.error || 'No se pudo guardar.');
          return;
        }
        $('saveMsg').textContent = 'Guardado y enviado al módulo de dirección.';
      })
      .catch(function (e) {
        $('saveMsg').textContent = 'No se pudo guardar (el módulo puede estar offline). [' + e + ']';
      });
  }

  function zeroWas() {
    $('saveMsg').textContent = 'Poniendo el WAS en cero…';
    fetch('/api/steer/zero-was', { method: 'POST' })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) {
        if (j && typeof j.was_offset !== 'undefined') {
          var r = document.querySelector('input[data-key="wasOffset"]');
          if (r) { r.value = j.was_offset; updateSliderLabel(r); }
        }
        $('saveMsg').textContent = 'WAS puesto en cero.';
      })
      .catch(function (e) {
        $('saveMsg').textContent = 'No se pudo poner el WAS en cero (módulo offline). [' + e + ']';
      });
  }

  // --------------------------------------------------------------------------
  // Pill de estado
  // --------------------------------------------------------------------------
  function setPill(ok, text) {
    var pill = $('okPill');
    if (!pill) return;
    pill.className = 'pill ' + (ok ? 'ok' : 'idle');
    pill.innerHTML = '<span class="dot"></span> ' + text;
  }

  // --------------------------------------------------------------------------
  // Botones auxiliares (free drive / arco) — solo estado visual en paso 1.
  // --------------------------------------------------------------------------
  function initAuxButtons() {
    var freeOn = false;
    var btnFree = $('btnFreeDrive'), up = $('btnFreeUp'), down = $('btnFreeDown');
    if (btnFree) {
      btnFree.addEventListener('click', function () {
        freeOn = !freeOn;
        btnFree.textContent = 'Libre: ' + (freeOn ? 'ON' : 'OFF');
        btnFree.classList.toggle('btn-primary', freeOn);
        btnFree.classList.toggle('btn-ghost', !freeOn);
        if (up) up.disabled = !freeOn;
        if (down) down.disabled = !freeOn;
      });
    }
    var arcOn = false;
    var btnArc = $('btnArc');
    if (btnArc) {
      btnArc.addEventListener('click', function () {
        arcOn = !arcOn;
        btnArc.textContent = arcOn ? 'Detener arco' : 'Iniciar arco';
      });
    }
    var btnZero = $('btnZeroWas');
    if (btnZero) btnZero.addEventListener('click', zeroWas);

    var btnReset = $('btnReset');
    if (btnReset) {
      btnReset.addEventListener('click', function () {
        var doReset = function () { location.reload(); };
        if (window.AgpModal && typeof window.AgpModal.confirm === 'function') {
          window.AgpModal.confirm('Restablecer', '¿Volver a los valores por defecto de esta pantalla?')
            .then(function (ok) { if (ok) doReset(); });
        } else {
          doReset();
        }
      });
    }
  }

  // --------------------------------------------------------------------------
  // Wiring
  // --------------------------------------------------------------------------
  document.addEventListener('DOMContentLoaded', function () {
    initTabs();
    initSliders();
    initCheckboxes();
    initSegments();
    initAuxButtons();
    var btnSave = $('btnSave');
    if (btnSave) btnSave.addEventListener('click', saveConfig);
    loadConfig();
  });
})();
