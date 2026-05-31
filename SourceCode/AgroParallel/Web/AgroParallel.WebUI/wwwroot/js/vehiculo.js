// ============================================================================
// vehiculo.js — GET/PUT /api/vehicle. Lee Properties.Settings.Default
// (setVehicle_*) del piloto, edita y persiste. Al guardar el piloto recarga CVehicle
// en el hilo de UI (sin reiniciar el GPS).
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  // Normalizador PascalCase → camelCase. EmbedIO/Swan serializa anónimos en
  // lowercase pero los POCOs llegan PascalCase desde la red en algunos pipes.
  function lkeys(v) {
    if (Array.isArray(v)) return v.map(lkeys);
    if (v && typeof v === 'object') {
      var out = {};
      Object.keys(v).forEach(function (k) {
        var nk = k.charAt(0).toLowerCase() + k.slice(1);
        out[nk] = lkeys(v[k]);
      });
      return out;
    }
    return v;
  }

  function pill(state, text) {
    var el = $('vehStatus');
    el.className = 'pill ' + (state === 'ok' ? 'ok' : state === 'err' ? 'bad' : '');
    el.innerHTML = '<span class="dot"></span> ' + text;
  }

  function msg(state, text) {
    var el = $('msgVeh');
    el.className = 'msg ' + (state || '');
    el.textContent = text || '';
  }

  function setActiveCard(vt, variant) {
    // Marca como .on la tarjeta cuyo data-vt coincide. Si hay 2 con el mismo
    // vt (Tractor vs Pulverizadora), prioriza la guardada en localStorage.
    var cards = document.querySelectorAll('.veh-card');
    cards.forEach(function (c) { c.classList.remove('on'); c.querySelector('input').checked = false; });
    var pref = variant || localStorage.getItem('agp.vehVariant') || '';
    var match = null, fallback = null;
    cards.forEach(function (c) {
      if (parseInt(c.dataset.vt, 10) !== vt) return;
      if (!fallback) fallback = c;
      if (pref && c.dataset.variant === pref) match = c;
    });
    var picked = match || fallback;
    if (picked) {
      picked.classList.add('on');
      picked.querySelector('input').checked = true;
    }
  }

  function fillForm(v) {
    if (!v) return;
    setActiveCard(v.vehicleType | 0, null);
    $('wheelbase').value      = num(v.wheelbase, 3.3);
    $('trackWidth').value     = num(v.trackWidth, 1.9);
    $('maxSteerAngle').value  = num(v.maxSteerAngle, 30);
    $('slowSpeedCutoff').value = num(v.slowSpeedCutoff, 0.5);
    $('antennaHeight').value  = num(v.antennaHeight, 3);
    $('antennaPivot').value   = num(v.antennaPivot, 0.1);
    $('antennaOffset').value  = num(v.antennaOffset, 0);
  }

  function num(v, def) {
    return (typeof v === 'number' && isFinite(v)) ? v : def;
  }

  function readForm() {
    var picked = document.querySelector('.veh-card.on');
    var vt = picked ? parseInt(picked.dataset.vt, 10) : 0;
    if (picked) localStorage.setItem('agp.vehVariant', picked.dataset.variant || '');
    return {
      vehicleType: isFinite(vt) ? vt : 0,
      wheelbase: parseFloat($('wheelbase').value),
      trackWidth: parseFloat($('trackWidth').value),
      maxSteerAngle: parseFloat($('maxSteerAngle').value),
      slowSpeedCutoff: parseFloat($('slowSpeedCutoff').value),
      antennaHeight: parseFloat($('antennaHeight').value),
      antennaPivot: parseFloat($('antennaPivot').value),
      antennaOffset: parseFloat($('antennaOffset').value)
    };
  }

  async function load() {
    pill('', 'Cargando…');
    try {
      var res = await fetch('/api/vehicle', { cache: 'no-store' });
      var data = lkeys(await res.json());
      if (!data.ok) throw new Error(data.error || 'GET falló');
      fillForm(data.vehicle);
      pill('ok', 'OK');
    } catch (e) {
      pill('err', 'Error');
      msg('err', '✕ ' + e.message);
    }
  }

  async function save() {
    msg('', 'Guardando…');
    try {
      var cfg = readForm();
      var res = await fetch('/api/vehicle', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var data = lkeys(await res.json());
      if (data.ok) {
        msg('ok', '✓ Guardado y aplicado.');
      } else {
        msg('err', '✕ ' + (data.error || 'no se pudo guardar'));
      }
    } catch (e) {
      msg('err', '✕ ' + e.message);
    }
  }

  // ---- Tabs ----
  document.querySelectorAll('.tabs .tab').forEach(function (t) {
    t.addEventListener('click', function () {
      document.querySelectorAll('.tabs .tab').forEach(function (x) { x.classList.remove('on'); });
      document.querySelectorAll('.panel').forEach(function (p) { p.classList.remove('on'); });
      t.classList.add('on');
      var p = document.getElementById('panel-' + t.dataset.panel);
      if (p) p.classList.add('on');
    });
  });

  // ---- Cards de tipo: click marca activa + radio interno ----
  document.querySelectorAll('.veh-card').forEach(function (c) {
    c.addEventListener('click', function (e) {
      // Si el click vino del propio <input>, el navegador ya lo maneja
      document.querySelectorAll('.veh-card').forEach(function (x) { x.classList.remove('on'); });
      c.classList.add('on');
      var r = c.querySelector('input');
      if (r) r.checked = true;
    });
  });

  $('btnSaveVeh').addEventListener('click', save);
  $('btnReloadVeh').addEventListener('click', function () { msg('', ''); load(); });

  load();
})();
