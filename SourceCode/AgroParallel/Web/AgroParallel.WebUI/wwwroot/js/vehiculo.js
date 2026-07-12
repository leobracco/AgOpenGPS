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

  // Sprites reales del FormConfig por tipo de vehículo (0=Tractor incl.
  // pulverizadora, 1=Cosechadora, 2=Articulada). Los diagramas de Geometría
  // y Antena referencian SIEMPRE al tipo elegido en la pestaña Tipo.
  var VEH_IMG = {
    0: { geo: 'RadiusWheelBase.png',            ant: 'AntennaTractor.png' },
    1: { geo: 'RadiusWheelBaseHarvester.png',   ant: 'AntennaHarvester.png' },
    2: { geo: 'RadiusWheelBaseArticulated.png', ant: 'AntennaArticulated.png' }
  };

  function updateVehImages(vt) {
    var m = VEH_IMG[vt] || VEH_IMG[0];
    var geo = $('geoDiagram');
    var ant = $('antDiagram');
    if (geo) geo.src = '../img/vehicle/' + m.geo;
    if (ant) ant.src = '../img/vehicle/' + m.ant;
  }

  function setActiveCard(vt, variant) {
    // Marca como .on la tarjeta de TIPO cuyo data-vt coincide (scopeado a
    // #tipoCards: el grid de modelos también usa .veh-card). Si hay 2 con el
    // mismo vt (Tractor vs Pulverizadora), prioriza la de localStorage.
    var cards = document.querySelectorAll('#tipoCards .veh-card');
    cards.forEach(function (c) {
      c.classList.remove('on');
      var r = c.querySelector('input');
      if (r) r.checked = false;
    });
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
      var r2 = picked.querySelector('input');
      if (r2) r2.checked = true;
    }
    updateVehImages(vt);
    renderSprites();
  }

  // Último vehículo cargado del API: fallback para inputs vacíos al guardar
  // (un campo en blanco parseFloat→NaN→null en el JSON y el backend lo
  // rechazaba con "could not be converted to System.Double").
  var lastVeh = null;

  function fillForm(v) {
    if (!v) return;
    lastVeh = v;
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

  // número del input o fallback (nunca NaN → nunca null en el JSON)
  function fnum(id, def) {
    var v = parseFloat($(id).value);
    return isFinite(v) ? v : def;
  }

  function readForm() {
    var picked = document.querySelector('#tipoCards .veh-card.on');
    var vt = picked ? parseInt(picked.dataset.vt, 10) : 0;
    if (picked) localStorage.setItem('agp.vehVariant', picked.dataset.variant || '');
    var lv = lastVeh || {};
    // Offset: input en cm absoluto + lado elegido → metros con signo
    // (misma convención que WinForms: izquierda +, derecha −, centro 0).
    var offCm = Math.abs(fnum('antennaOffset', 0));
    var offM = side === 'left' ? offCm / 100 : side === 'right' ? -offCm / 100 : 0;
    return {
      vehicleType: isFinite(vt) ? vt : 0,
      wheelbase: fnum('wheelbase', num(lv.wheelbase, 3.3)),
      trackWidth: fnum('trackWidth', num(lv.trackWidth, 1.9)),
      maxSteerAngle: fnum('maxSteerAngle', num(lv.maxSteerAngle, 30)),
      slowSpeedCutoff: fnum('slowSpeedCutoff', num(lv.slowSpeedCutoff, 0.5)),
      antennaHeight: fnum('antennaHeight', num(lv.antennaHeight, 3) * 100) / 100,
      antennaPivot: fnum('antennaPivot', num(lv.antennaPivot, 0.1) * 100) / 100,
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

  // ---- Cards de tipo: click marca activa + despliega los modelos del tipo ----
  document.querySelectorAll('#tipoCards .veh-card').forEach(function (c) {
    c.addEventListener('click', function (e) {
      document.querySelectorAll('#tipoCards .veh-card').forEach(function (x) { x.classList.remove('on'); });
      c.classList.add('on');
      var r = c.querySelector('input');
      if (r) r.checked = true;
      // Los diagramas de Geometría y Antena siguen al tipo elegido
      updateVehImages(parseInt(c.dataset.vt, 10) || 0);
      // ...y abajo se despliegan los vehículos disponibles de ese tipo
      renderSprites();
    });
  });

  document.querySelectorAll('.ant-side').forEach(function (b) {
    b.addEventListener('click', function () { setSide(b.dataset.side); });
  });

  $('btnSaveVeh').addEventListener('click', save);
  $('btnReloadVeh').addEventListener('click', function () { msg('', ''); load(); });

  // ---- Vehículos unificados: el tipo elegido arriba despliega abajo los
  // modelos disponibles (sprites de img/vehiculos/, convención
  // tipo_marca_modelo.png). data-tiposlug de la tarjeta = tipo del archivo.
  var spriteData = { activo: '', sprites: [] };

  function tipoSlugActivo() {
    var picked = document.querySelector('#tipoCards .veh-card.on');
    return picked ? (picked.dataset.tiposlug || '') : '';
  }

  function renderSprites() {
    var box = $('misVehiculos');
    if (!box) return;
    var slug = tipoSlugActivo();
    var activo = spriteData.activo || '';
    var lista = spriteData.sprites.filter(function (s) {
      return !slug || String(s.tipo || '').toLowerCase() === slug;
    });
    var titulo = $('modelosTitulo');
    if (titulo) {
      var pickedName = document.querySelector('#tipoCards .veh-card.on .veh-name');
      titulo.textContent = 'Elegí tu vehículo' + (pickedName ? ' — ' + pickedName.textContent : '');
    }

    var parts = [];
    // tarjeta "Estándar" = marca embebida de PilotX (archivo vacío)
    parts.push(
      '<div class="veh-card' + (activo === '' ? ' on' : '') + '" data-sprite="">' +
        '<img class="veh-img" src="../img/tractors/TractorAoG.png" alt="" loading="lazy">' +
        '<div class="veh-name">Estándar</div>' +
        '<div class="veh-hint">Marca embebida de PilotX</div>' +
      '</div>');
    lista.forEach(function (s) {
      var det = [s.marca, s.modelo].filter(Boolean).join(' ') || s.tipo;
      parts.push(
        '<div class="veh-card' + (activo === s.archivo ? ' on' : '') + '" data-sprite="' + s.archivo + '">' +
          '<img class="veh-img" src="..' + s.url + '" alt="" loading="lazy">' +
          '<div class="veh-name">' + s.nombre + '</div>' +
          '<div class="veh-hint">' + det + '</div>' +
        '</div>');
    });
    if (lista.length === 0) {
      parts.push('<div class="help" style="align-self:center">No hay vehículos de este tipo ' +
        'cargados todavía — tirá el PNG en img\\vehiculos\\ y aparece acá.</div>');
    }
    box.innerHTML = parts.join('');
    box.querySelectorAll('.veh-card').forEach(function (c) {
      c.addEventListener('click', async function () {
        try {
          var res = await fetch('/api/vehicle/sprite', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ archivo: c.dataset.sprite })
          });
          var d = await res.json();
          if (d.ok) {
            spriteData.activo = c.dataset.sprite;
            box.querySelectorAll('.veh-card').forEach(function (x) { x.classList.remove('on'); });
            c.classList.add('on');
            msg('ok', '✓ Vehículo aplicado al mapa.');
          } else {
            msg('err', '✕ No se pudo aplicar el vehículo.');
          }
        } catch (e) {
          msg('err', '✕ ' + e.message);
        }
      });
    });
  }

  async function loadSprites() {
    try {
      var res = await fetch('/api/vehicle/sprites', { cache: 'no-store' });
      var data = await res.json();
      spriteData.activo = (data && data.activo) || '';
      spriteData.sprites = (data && data.sprites) || [];
      renderSprites();
    } catch (e) {
      var box = $('misVehiculos');
      if (box) box.innerHTML = '<div class="help">Sin conexión con PilotX</div>';
    }
  }

  load();
  loadSprites();
})();
