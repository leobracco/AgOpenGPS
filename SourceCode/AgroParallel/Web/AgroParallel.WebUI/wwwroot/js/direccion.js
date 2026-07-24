// ============================================================================
// direccion.js — Configuración de AutoSteer (FormSteer) para el Hub de PilotX.
// Mismo sistema de diseño e interacción que config.js: menú lateral de tabs,
// teclado virtual, botón Guardar flotante con estados (dirty / ok) y deep-link.
//
// A diferencia de config.js (que guarda por sección al salir de cada tab), acá
// toda la config de dirección se maneja como UN solo objeto y se persiste
// best-effort contra el módulo de dirección:
//   · GET  /api/steer/config    → popula los controles (si 404, quedan defaults)
//   · POST /api/steer/config    → guarda el objeto serializado
//   · POST /api/steer/zero-was  → pone el WAS en cero
// Todos los fetch van en try/catch: si el endpoint no existe, la página sigue
// andando con los valores por defecto.
//
// Convención: los data-key del markup son camelCase; el wire usa esos mismos
// nombres (best-effort, el backend/AgpJson tolera el snake vs camel si aplica).
// ============================================================================
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  var estado = $('estado');
  var tabActual = null;

  function setEstado(msg, cls) {
    if (!estado) return;
    estado.textContent = msg || '';
    estado.className = cls || '';
  }

  // --------------------------------------------------------------------------
  // Sliders — display del valor grande + botones de nudge
  // --------------------------------------------------------------------------
  function fmtSlider(range) {
    var raw = parseFloat(range.value);
    var scale = parseFloat(range.getAttribute('data-scale') || '1');
    var dec = parseInt(range.getAttribute('data-dec') || '0', 10);
    var display = range.getAttribute('data-display');
    if (display === 'minus10') return (raw - 10).toString();   // U-Turn: muestra value−10
    return (raw * scale).toFixed(dec);
  }

  function updateSliderLabel(range) {
    var key = range.getAttribute('data-key');
    var out = document.querySelector('.sval[data-for="' + key + '"]');
    if (out) {
      var unit = out.querySelector('.u');   // preservar el sufijo de unidad
      out.textContent = fmtSlider(range);
      if (unit) out.appendChild(unit);
    }
    // Sensor: la lectura se muestra como porcentaje 0..255 → 0..100 %.
    if (key === 'sensorLimit') {
      var pct = $('lblSensorPct');
      if (pct) {
        var u = pct.querySelector('.u');
        pct.textContent = Math.round(parseFloat(range.value) / 255 * 100);
        if (u) pct.appendChild(u);
      }
    }
    // Ángulo de cero del WAS y barra de ángulo dependen de offset y cuentas/grado.
    if (key === 'wasOffset' || key === 'countsPerDegree' || key === 'maxSteerAngle') {
      updateWasZeroAngle();
    }
  }

  function updateWasZeroAngle() {
    var offR = document.querySelector('input[data-key="wasOffset"]');
    var cpdR = document.querySelector('input[data-key="countsPerDegree"]');
    var el = $('wasZeroAngle');
    if (offR && cpdR && el) {
      var cpd = parseFloat(cpdR.value) || 1;
      el.textContent = (parseFloat(offR.value) / cpd).toFixed(2);
    }
  }

  function initSliders() {
    document.querySelectorAll('input[type=range][data-key]').forEach(function (range) {
      range.addEventListener('input', function () { updateSliderLabel(range); marcarSucio(); });
      updateSliderLabel(range);
    });
    document.querySelectorAll('.sbtn[data-nudge]').forEach(function (btn) {
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
        marcarSucio();
      });
    });
  }

  // --------------------------------------------------------------------------
  // Checkboxes (chkimg) — los 3 sensores de giro son excluyentes
  // --------------------------------------------------------------------------
  var EXCLUSIVE_SENSORS = ['encoder', 'pressureSensor', 'currentSensor'];

  function setChk(key, on) {
    var row = document.querySelector('.chkimg[data-key="' + key + '"]');
    if (row) row.classList.toggle('sel', !!on);
  }

  function initCheckboxes() {
    document.querySelectorAll('.chkimg[data-key]').forEach(function (row) {
      row.addEventListener('click', function () {
        var key = row.getAttribute('data-key');
        var willBeOn = !row.classList.contains('sel');
        if (willBeOn && EXCLUSIVE_SENSORS.indexOf(key) >= 0) {
          EXCLUSIVE_SENSORS.forEach(function (k) { if (k !== key) setChk(k, false); });
        }
        row.classList.toggle('sel', willBeOn);
        marcarSucio();
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
          marcarSucio();
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
    document.querySelectorAll('.chkimg[data-key]').forEach(function (row) {
      cfg[row.getAttribute('data-key')] = row.classList.contains('sel');
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
    document.querySelectorAll('.chkimg[data-key]').forEach(function (row) {
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
          $('ftModuloVal').textContent = 'Conectado';
          setEstado('Configuración cargada', 'ok');
        } else {
          $('ftModuloVal').textContent = 'Sin config';
          setEstado('Sin config guardada — valores por defecto', '');
        }
      })
      .catch(function () {
        // Endpoint aún inexistente → la UI arranca con los valores por defecto.
        $('ftModuloVal').textContent = 'Offline';
        setEstado('Sin conexión con el módulo — valores por defecto', '');
      });
  }

  function saveConfig() {
    var cfg = collectConfig();
    setEstado('Guardando…', '');
    return fetch('/api/steer/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cfg)
    })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) {
        if (j && j.ok === false) {
          setEstado((j.error_code || 'AGP-NET-201') + ' · ' + (j.error || 'No se pudo guardar.'), 'err');
          return false;
        }
        setEstado('Guardado y enviado al módulo de dirección ✔', 'ok');
        limpiarSucio(true);
        return true;
      })
      .catch(function (e) {
        setEstado('No se pudo guardar (módulo offline) [' + e + ']', 'err');
        return false;
      });
  }

  function zeroWas() {
    setEstado('Poniendo el WAS en cero…', '');
    fetch('/api/steer/zero-was', { method: 'POST' })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) {
        if (j && typeof j.was_offset !== 'undefined') {
          var r = document.querySelector('input[data-key="wasOffset"]');
          if (r) { r.value = j.was_offset; updateSliderLabel(r); }
        }
        setEstado('WAS puesto en cero ✔', 'ok');
      })
      .catch(function (e) {
        setEstado('No se pudo poner el WAS en cero (módulo offline) [' + e + ']', 'err');
      });
  }

  // --------------------------------------------------------------------------
  // Navegación por menú lateral (réplica del patrón de config.js)
  // --------------------------------------------------------------------------
  function irATab(id) {
    var sec = document.querySelector('section[data-tab="' + id + '"]');
    if (!sec) { id = 'gain'; }
    if (tabActual === id) return;
    tabActual = id;
    document.querySelectorAll('#menu button').forEach(function (b) {
      b.classList.toggle('sel', b.dataset.tab === id);
    });
    document.querySelectorAll('section[data-tab]').forEach(function (s) {
      s.classList.toggle('activa', s.dataset.tab === id);
    });
    try {
      var url = new URL(window.location.href);
      url.searchParams.set('tab', id);
      history.replaceState(null, '', url.toString());
    } catch (e) { /* file:// etc. */ }
    $('main').scrollTop = 0;
  }

  function initTabs() {
    document.querySelectorAll('#menu button').forEach(function (b) {
      b.addEventListener('click', function () { irATab(b.dataset.tab); });
    });
  }

  // --------------------------------------------------------------------------
  // Botón Guardar flotante con estados (idéntico a config.js)
  // --------------------------------------------------------------------------
  var btnG = $('btnGuardarFloat');
  var btnGImg = $('btnGuardarImg');
  var btnGCap = $('btnGuardarCap');
  var btnGTimer = null;

  function marcarSucio() {
    if (btnGTimer) { clearTimeout(btnGTimer); btnGTimer = null; }
    btnG.classList.remove('ok');
    btnGImg.src = '../img/config/FileSave.png';
    btnG.classList.add('dirty');
    btnGCap.textContent = 'Guardar';
  }

  function limpiarSucio(guardado) {
    btnG.classList.remove('dirty');
    if (guardado) {
      btnG.classList.add('ok');
      btnGImg.src = '../img/config/OK64.png';
      btnGCap.textContent = 'Guardado';
      if (btnGTimer) clearTimeout(btnGTimer);
      btnGTimer = setTimeout(function () {
        btnG.classList.remove('ok');
        btnGImg.src = '../img/config/FileSave.png';
        btnGCap.textContent = 'Sin cambios';
      }, 1500);
    } else {
      btnG.classList.remove('ok');
      btnGImg.src = '../img/config/FileSave.png';
      btnGCap.textContent = 'Sin cambios';
    }
  }

  function initSaveButton() {
    // Los inputs numéricos también marcan sucio (los sliders/toggles/segmentados
    // llaman a marcarSucio() en sus propios handlers).
    var main = $('main');
    main.addEventListener('input', function (ev) {
      if (ev.target && ev.target.matches('input[type=number]')) marcarSucio();
    });
    btnG.addEventListener('click', function () { saveConfig(); });
    // guardar la tab activa si la página se oculta (cierre del widget del Hub)
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'hidden' && btnG.classList.contains('dirty')) saveConfig();
    });
  }

  // --------------------------------------------------------------------------
  // Botones auxiliares (WAS cero, Smart WAS, Restablecer)
  // --------------------------------------------------------------------------
  function initAuxButtons() {
    var bz = $('btnZeroWas');
    if (bz) bz.addEventListener('click', zeroWas);
    var bs = $('btnSmartZeroWas');
    if (bs) bs.addEventListener('click', zeroWas);
    var br = $('btnReset');
    if (br) br.addEventListener('click', function () {
      var doReset = function () { location.reload(); };
      if (window.AgpModal && typeof window.AgpModal.confirm === 'function') {
        window.AgpModal.confirm('Restablecer', '¿Volver a los valores por defecto de esta pantalla?')
          .then(function (ok) { if (ok) doReset(); });
      } else {
        doReset();
      }
    });
  }

  // --------------------------------------------------------------------------
  // Arranque
  // --------------------------------------------------------------------------
  document.addEventListener('DOMContentLoaded', function () {
    initTabs();
    initSliders();
    initCheckboxes();
    initSegments();
    initSaveButton();
    initAuxButtons();
    var tab = new URLSearchParams(window.location.search).get('tab') || 'gain';
    irATab(tab);
    loadConfig();
  });
})();
