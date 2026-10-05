// ============================================================================
// calibracion-imu.js — GET/POST /api/aog/imu*. Puente al roll del IMU interno
// de PilotX (CAHRS/FormGPS.ahrs, ver ImuCalibracionController). Poll 1Hz para
// la lectura en vivo; los comandos (nivelar/invertir/reset) usan AgpModal para
// confirmar antes de pisar la calibración guardada, y un toast para avisar el
// resultado — mismo patrón que config-implemento.js.
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  function pill(state, text) {
    var el = $('imuStatus');
    el.className = 'pill ' + (state === 'ok' ? 'ok' : state === 'err' ? 'bad' : 'idle');
    el.innerHTML = '<span class="dot"></span> ' + text;
  }

  var toastTimer = null;
  function toast(text, kind, ms) {
    var el = $('imuToast');
    if (!el) return;
    el.textContent = text;
    el.className = 'show ' + (kind || '');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.className = ''; }, ms || 2600);
  }

  function fmt(v) {
    return (typeof v === 'number' && isFinite(v)) ? v.toFixed(1) : '—';
  }

  var lastSnap = null;
  var filterDirty = false; // true mientras el operario arrastra el slider — no lo pisamos con el poll

  async function refresh() {
    try {
      var res = await fetch('/api/aog/imu', { cache: 'no-store' });
      var data = await res.json();
      if (!data || !data.ok) throw new Error('GET falló');
      var s = data.snapshot || {};
      lastSnap = s;

      pill(s.present ? 'ok' : 'err', s.present ? 'IMU con datos' : 'Sin dato de IMU todavía');
      $('imuRollLive').textContent = s.present ? fmt(s.imu_roll) : '—';
      $('imuHeadingLive').textContent = s.present ? fmt(s.imu_heading) : '—';
      $('imuRollZeroVal').textContent = fmt(s.roll_zero);

      $('btnImuZero').disabled = !s.present;

      $('imuInvert').classList.toggle('on', !!s.is_roll_invert);

      if (!filterDirty) {
        var pct = Math.round((s.roll_filter || 0) * 100);
        $('imuRollFilter').value = pct;
        $('imuRollFilterVal').textContent = pct;
      }
    } catch (e) {
      pill('err', 'Error');
      toast('No se pudo leer el estado del IMU: ' + e.message, 'bad');
    }
  }

  async function sendCommand(cmd) {
    try {
      var res = await fetch('/api/aog/imu/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (!data || !data.ok) throw new Error((data && data.error) || 'no se pudo aplicar');
      return true;
    } catch (e) {
      toast('✕ ' + e.message, 'bad');
      return false;
    }
  }

  $('btnImuZero').addEventListener('click', async function () {
    if (!lastSnap || !lastSnap.present) return;
    var yes = window.AgpModal
      ? await AgpModal.confirm('Nivelar el IMU', 'Con el tractor parado y a nivel, esto toma el roll actual como referencia de "nivel" y pisa el offset guardado. ¿Confirmás?')
      : true;
    if (!yes) return;
    if (await sendCommand('zero_roll')) {
      toast('✓ IMU nivelado', 'ok');
      refresh();
    }
  });

  $('btnRollUp').addEventListener('click', async function () {
    if (await sendCommand('roll_offset_up')) refresh();
  });
  $('btnRollDown').addEventListener('click', async function () {
    if (await sendCommand('roll_offset_down')) refresh();
  });
  $('btnRollRemove').addEventListener('click', async function () {
    if (await sendCommand('remove_zero_offset')) { toast('✓ Offset en 0', 'ok'); refresh(); }
  });

  $('imuInvert').addEventListener('click', async function () {
    if (await sendCommand('toggle_invert')) refresh();
  });

  $('btnImuReset').addEventListener('click', async function () {
    var yes = window.AgpModal
      ? await AgpModal.confirm('Reiniciar IMU', 'PilotX va a esperar un dato nuevo del sensor (útil si lo desconectaste/reconectaste). ¿Confirmás?')
      : true;
    if (!yes) return;
    if (await sendCommand('reset_imu')) { toast('✓ IMU reiniciado, esperando dato nuevo', 'ok'); refresh(); }
  });

  var filterSlider = $('imuRollFilter');
  filterSlider.addEventListener('input', function () {
    filterDirty = true;
    $('imuRollFilterVal').textContent = filterSlider.value;
  });
  filterSlider.addEventListener('change', async function () {
    try {
      var res = await fetch('/api/aog/imu/roll-filter', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: parseFloat(filterSlider.value) || 0 })
      });
      var data = await res.json();
      if (!data || !data.ok) throw new Error('no se pudo guardar');
      toast('✓ Filtro guardado', 'ok');
    } catch (e) {
      toast('✕ ' + e.message, 'bad');
    } finally {
      filterDirty = false;
      refresh();
    }
  });

  refresh();
  setInterval(refresh, 1000);
})();
