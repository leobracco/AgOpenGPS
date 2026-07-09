// ============================================================================
// vehiculo.js — GET/PUT /api/vehicle. Lee Properties.Settings.Default
// (setVehicle_*) del piloto, edita y persiste. Al guardar el piloto recarga CVehicle
// en el hilo de UI (sin reiniciar el GPS).
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

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
    // La antena se edita en cm (como la pantalla WinForms original);
    // el API habla en metros. Signo del offset: >0 izquierda, <0 derecha.
    $('antennaHeight').value  = Math.round(num(v.antennaHeight, 3) * 100);
    $('antennaPivot').value   = Math.round(num(v.antennaPivot, 0.1) * 100);
    var off = num(v.antennaOffset, 0);
    $('antennaOffset').value  = Math.round(Math.abs(off) * 100);
    setSide(off > 0 ? 'left' : off < 0 ? 'right' : 'center');
  }

  function num(v, def) {
    return (typeof v === 'number' && isFinite(v)) ? v : def;
  }

  function readForm() {
    var picked = document.querySelector('.veh-card.on');
    var vt = picked ? parseInt(picked.dataset.vt, 10) : 0;
    if (picked) localStorage.setItem('agp.vehVariant', picked.dataset.variant || '');
    // Offset: input en cm absoluto + lado elegido → metros con signo
    // (misma convención que WinForms: izquierda +, derecha −, centro 0).
    var offCm = Math.abs(parseFloat($('antennaOffset').value) || 0);
    var offM = side === 'left' ? offCm / 100 : side === 'right' ? -offCm / 100 : 0;
    return {
      vehicleType: isFinite(vt) ? vt : 0,
      wheelbase: parseFloat($('wheelbase').value),
      trackWidth: parseFloat($('trackWidth').value),
      maxSteerAngle: parseFloat($('maxSteerAngle').value),
      slowSpeedCutoff: parseFloat($('slowSpeedCutoff').value),
      antennaHeight: (parseFloat($('antennaHeight').value) || 0) / 100,
      antennaPivot: (parseFloat($('antennaPivot').value) || 0) / 100,
      antennaOffset: offM
    };
  }

  // ---- Selector de lado del desfase de antena ----
  var side = 'center';

  function setSide(s) {
    side = s;
    document.querySelectorAll('.ant-side').forEach(function (b) {
      b.classList.toggle('on', b.dataset.side === s);
    });
    var inp = $('antennaOffset');
    if (s === 'center') {
      inp.value = 0;
      inp.disabled = true;
    } else {
      inp.disabled = false;
    }
    updateAntSvg();
  }

  // Mueve el punto de la antena y la cota en el SVG de vista superior.
  function updateAntSvg() {
    var dot = document.getElementById('svgAntDot');
    var line = document.getElementById('svgAntLine');
    if (!dot || !line) return;
    var cx = side === 'left' ? 62 : side === 'right' ? 108 : 85;
    dot.setAttribute('cx', cx);
    line.setAttribute('x1', 85);
    line.setAttribute('x2', cx);
  }

  async function load() {
    pill('', 'Cargando…');
    try {
      var res = await fetch('/api/vehicle', { cache: 'no-store' });
      var data = await res.json();
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
      var data = await res.json();
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

  document.querySelectorAll('.ant-side').forEach(function (b) {
    b.addEventListener('click', function () { setSide(b.dataset.side); });
  });

  $('btnSaveVeh').addEventListener('click', save);
  $('btnReloadVeh').addEventListener('click', function () { msg('', ''); load(); });

  load();
})();
