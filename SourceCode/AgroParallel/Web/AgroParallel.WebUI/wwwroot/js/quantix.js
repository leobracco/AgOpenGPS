// ============================================================================
// quantix.js — UI completa del módulo QuantiX.
// Tabs:
//   Monitor    → /api/quantix/live (2 Hz)
//   Motores    → /api/quantix/motores GET/PUT + POST /{uid}/send
//   PID live   → POST /{uid}/cmd?verb=config con {configs:[...]} para tune en vivo
//   Calibrar   → POST /{uid}/cmd?verb=calibrar para start/stop, lee pulsos de live
//   Prueba     → POST /{uid}/cmd?verb=test para diagnóstico de motores
// ============================================================================

(function () {
  'use strict';

  // ---------- Shared helpers ----------

  function $(id) { return document.getElementById(id); }
  function pick(o, a, b) { return (o && o[a] != null) ? o[a] : (o ? o[b] : undefined); }

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function ageMs(iso) {
    if (!iso) return Infinity;
    var t = Date.parse(iso);
    if (isNaN(t)) return Infinity;
    return Date.now() - t;
  }

  // Formato corto para min..max en el dropdown de shape: 3 dígitos significativos,
  // sin colas de ceros (50000 → "50000", 0.005 → "0.005", 12.345678 → "12.3").
  function fmtNum(v) {
    if (v == null || !isFinite(v)) return '?';
    var a = Math.abs(v);
    if (a >= 1000) return v.toFixed(0);
    if (a >= 10)   return v.toFixed(1);
    if (a >= 1)    return v.toFixed(2);
    return v.toPrecision(2);
  }

  // ---------- State ----------

  var state = {
    activeTab: 'siembra',
    motoresCfg: { nodos: [], ignorados: [] },
    liveByUid: {},
    // NumSections viene del piloto (snapshot del tool actual). Se usa para
    // pintar el grid de selección de secciones en Motores. 0 = sin info aún.
    aogNumSections: 0,
    // Campos DBF del shapefile activo en el piloto. shapeSource cambia cuando se
    // abre otro .shp; usamos eso para refrescar sin pegarle a /aog/shape-fields
    // en cada render. shapeFields = [{name, numeric, min, max, count}].
    shapeSource: null,
    shapeFields: [],
    // Implemento central (Herramienta) — fuente única de verdad para trenes,
    // surcos y mapping surco→sección. QuantiX lee de acá; NO edita.
    implCentral: null
  };

  // --- Estado de la tira de surcos (Tarea 3-8) ---
  state.brushMotor = 0;          // índice del motor activo (pincel)
  state.siembraView = 'planter'; // 'planter' | 'tabla'

  var MOTOR_COLORS = ['#4ABA3E', '#7F6BE0', '#E0A33E', '#3E9BE0', '#E06B8B', '#46C5B0'];
  function motorColor(idx) { return MOTOR_COLORS[idx % MOTOR_COLORS.length]; }

  // Nodo activo (config plana: 1 nodo lógico de N motores). Si hay varios nodos
  // físicos, se opera sobre el primero habilitado; el resto conserva su config.
  function activeNodo() {
    var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
    return ns.find(function (n) { return n.habilitado; }) || ns[0] || null;
  }

  // Total de surcos = máx(secciones de PilotX, surcos cubiertos por motores, 1).
  // Usa state.aogNumSections (entero) que carga loadAogSections().
  function totalSurcos() {
    var nodo = activeNodo();
    var max = state.aogNumSections | 0;
    if (nodo) (nodo.motores || []).forEach(function (m) {
      (m.cortes || []).forEach(function (c) { if ((c | 0) > max) max = c | 0; });
    });
    return Math.max(max, 1);
  }

  // Índice surco (1-based) → motorIdx (o -1 si huérfano).
  function surcoOwner(nodo, surco) {
    var ms = nodo.motores || [];
    for (var i = 0; i < ms.length; i++) {
      if ((ms[i].cortes || []).indexOf(surco) >= 0) return i;
    }
    return -1;
  }

  // Renderiza la tira de celdas coloreadas por motor en #qxStrip.
  function renderStrip() {
    var el = document.getElementById('qxStrip');
    if (!el) return;
    var nodo = activeNodo();
    el.classList.toggle('cell-colors', !!nodo);
    if (!nodo) {
      el.innerHTML = '<span class="msg">No hay nodos QuantiX configurados</span>';
      return;
    }
    var total = totalSurcos();
    var html = '';
    for (var s = 1; s <= total; s++) {
      var owner = surcoOwner(nodo, s);
      if (owner < 0) {
        html += '<div class="cell orphan" data-surco="' + s + '">' + s + '</div>';
      } else {
        var brush = owner === state.brushMotor ? ' brush' : '';
        html += '<div class="cell' + brush + '" data-surco="' + s + '" '
              + 'style="background:' + motorColor(owner) + '">' + s + '</div>';
      }
    }
    el.innerHTML = html;
  }

  // Asigna 'surco' al motor del pincel, quitándolo de cualquier otro motor.
  function paintSurco(surco) {
    var nodo = activeNodo();
    var ms = (nodo && nodo.motores) || [];
    if (!nodo || state.brushMotor >= ms.length) return;
    ms.forEach(function (m, i) {
      m.cortes = (m.cortes || []).filter(function (c) { return c !== surco; });
      if (i === state.brushMotor && m.cortes.indexOf(surco) < 0) {
        m.cortes.push(surco); m.cortes.sort(function (a, b) { return a - b; });
      }
    });
    state.dirty = true;
    state.lastTouchedSurco = surco;
  }

  function fmtCortes(cortes) {
    if (!cortes || !cortes.length) return 'sin surcos';
    var sorted = cortes.slice().sort(function (a, b) { return a - b; });
    return 'surcos ' + sorted.join(',');
  }

  function renderMotorList() {
    var el = document.getElementById('qxMotorList');
    if (!el) return;
    var nodo = activeNodo();
    if (!nodo) { el.innerHTML = ''; return; }

    // Si estamos en vivo, delega al render live (se implementa en una tarea posterior).
    if (state.siembraEnMarcha && typeof renderMotorListLive === 'function') { renderMotorListLive(); return; }

    var ms = nodo.motores || [];
    var html = '';
    for (var i = 0; i < ms.length; i++) {
      var m = ms[i];
      var sel = (i === state.brushMotor) ? ' sel' : '';
      var efClass = m.campo_dosis ? 'mapa' : 'fija';
      var efTxt = m.campo_dosis ? ('mapa ' + escapeHtml(m.campo_dosis)) : 'fija';
      var nombre = escapeHtml(m.nombre || ('Motor ' + (i + 1)));
      var dosis = (typeof m.dosis_fija === 'number' ? m.dosis_fija : 0).toFixed(1);
      html += '<div class="mrow' + sel + '" data-mi="' + i + '">'
        + '<span class="sw" style="background:' + motorColor(i) + '"></span>'
        + '<span class="nm">' + nombre + '</span>'
        + '<span class="cnt">' + fmtCortes(m.cortes) + '</span>'
        + '<span class="dosebox"><input type="number" step="0.1" data-mi="' + i + '" '
        + 'class="qxDosisFija" value="' + dosis + '"> <span class="u">kg/ha</span></span>'
        + '<span class="eff ' + efClass + '">' + efTxt + '</span>'
        + '</div>';
    }
    el.innerHTML = html;

    // Tap a la fila = fijar pincel (sin robar foco al input de dosis).
    var rows = el.querySelectorAll('.mrow');
    for (var r = 0; r < rows.length; r++) {
      rows[r].addEventListener('click', function (e) {
        if (e.target && e.target.classList && e.target.classList.contains('qxDosisFija')) return;
        state.brushMotor = parseInt(this.getAttribute('data-mi'), 10);
        updateBrushChip(); renderStrip(); renderMotorList();
      });
    }
    // Editar dosis fija.
    var inputs = el.querySelectorAll('.qxDosisFija');
    for (var k = 0; k < inputs.length; k++) {
      inputs[k].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var n2 = activeNodo();
        if (n2 && n2.motores && n2.motores[idx]) {
          n2.motores[idx].dosis_fija = parseFloat(this.value) || 0;
          state.dirty = true;
          renderMotorList();
        }
      });
    }
  }

  function updateBrushChip() {
    var chip = document.getElementById('qxBrush');
    var nodo = activeNodo();
    if (!chip || !nodo) return;
    var ms = nodo.motores || [];
    var m = ms[state.brushMotor];
    var sw = chip.querySelector('.sw');
    if (sw) sw.style.background = motorColor(state.brushMotor);
    var label = m ? (m.nombre || ('Motor ' + (state.brushMotor + 1))) : '\u2014';
    if (chip.lastChild) chip.lastChild.textContent = 'Pincel: ' + label;
  }

  // Stub temporal — se reemplaza en la tarea del toggle Planter/Tabla.
  function renderTabla() {}
  // Stub temporal — se reemplaza en la tarea de huérfanos.
  function updateOrphanWarn() {}

  function renderSiembra() {
    renderStrip();
    renderMotorList();
    updateBrushChip();
    renderTabla();
    updateOrphanWarn();
  }

  function addMotor() {
    var nodo = activeNodo();
    if (!nodo) return;
    nodo.motores = nodo.motores || [];
    var m = defaultMotor('Motor ' + (nodo.motores.length + 1));
    m.cortes = [];
    nodo.motores.push(m);
    state.brushMotor = nodo.motores.length - 1;
    state.dirty = true;
    renderStrip(); renderMotorList();
  }

  // Quita el último surco tocado de cualquier motor (queda huérfano).
  function quitarSurco() {
    var s = state.lastTouchedSurco;
    if (!s) return;
    var nodo = activeNodo();
    if (!nodo) return;
    (nodo.motores || []).forEach(function (m) {
      m.cortes = (m.cortes || []).filter(function (c) { return c !== s; });
    });
    state.dirty = true;
    renderStrip(); renderMotorList();
  }

  // Auto-repartir: 'uno' = 1 motor por surco (crea motores si faltan);
  // 'grupos' = reparte los surcos en N grupos parejos entre los motores actuales.
  function autoReparto(modo) {
    var nodo = activeNodo();
    if (!nodo) return;
    nodo.motores = nodo.motores || [];
    var total = totalSurcos();
    if (modo === 'uno') {
      while (nodo.motores.length < total) {
        var nm = defaultMotor('Motor ' + (nodo.motores.length + 1));
        nm.cortes = [];
        nodo.motores.push(nm);
      }
      nodo.motores.forEach(function (m, i) { m.cortes = (i < total) ? [i + 1] : []; });
    } else {
      var n = nodo.motores.length;
      if (n === 0) return;
      nodo.motores.forEach(function (m) { m.cortes = []; });
      for (var s = 1; s <= total; s++) {
        var g = Math.floor((s - 1) * n / total);
        nodo.motores[g].cortes.push(s);
      }
    }
    state.dirty = true;
    renderStrip(); renderMotorList();
  }

  async function loadImplCentral() {
    try {
      var r = await fetch('/api/implemento', { cache: 'no-store' });
      var d = await r.json();
      state.implCentral = (d && d.ok) ? d.implemento : null;
    } catch (e) { state.implCentral = null; }
  }

  // Mapping surco_numero → seccion_pilotx_id desde el implemento central.
  // El motor guarda surcos_cubiertos[]; al guardar derivamos cortes[] = lista
  // de secciones PilotX únicas que cubren esos surcos. Si el central no tiene
  // surcos cargados, devuelve {} y el caller cae al fallback.
  function surcoToSeccionMap() {
    var c = state.implCentral;
    var map = {};
    if (!c || !Array.isArray(c.surcos)) return map;
    c.surcos.forEach(function (s) {
      var n = s.numero | 0;
      var sec = s.seccion_pilotx | 0;
      if (n > 0 && sec > 0) map[n] = sec;
    });
    return map;
  }
  function surcosToCortes(surcos) {
    var m = surcoToSeccionMap();
    var set = {};
    (surcos || []).forEach(function (n) {
      var sec = m[n | 0];
      if (sec) set[sec] = true;
    });
    return Object.keys(set).map(function (k) { return k | 0; }).sort(function (a, b) { return a - b; });
  }

  async function loadAogSections() {
    try {
      var r = await fetch('/api/aog/state', { cache: 'no-store' });
      var d = await r.json();
      var n = (d && (d.NumSections != null ? d.NumSections : d.numSections)) || 0;
      state.aogNumSections = n;
    } catch (e) { /* keep previous */ }
  }

  // Trae las columnas DBF del shapefile activo. Cachea por SourceToken para
  // que abrir y cerrar la tab no genere requests innecesarios.
  async function loadShapeFields() {
    try {
      var r = await fetch('/api/aog/shape-fields', { cache: 'no-store' });
      var d = await r.json();
      if (d && d.ok) {
        state.shapeSource = d.sourceToken || '';
        state.shapeFields = Array.isArray(d.fields) ? d.fields : [];
      } else {
        state.shapeSource = '';
        state.shapeFields = [];
      }
    } catch (e) {
      state.shapeSource = '';
      state.shapeFields = [];
    }
  }

  // ---------- Tabs ----------

  function showTab(name) {
    state.activeTab = name;
    document.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.getAttribute('data-tab') === name);
    });
    ['Siembra', 'Shape', 'Pid', 'Calibrar', 'Prueba'].forEach(function (k) {
      var el = $('tab' + k);
      if (el) el.style.display = (k.toLowerCase() === name) ? '' : 'none';
    });
    if (name === 'siembra')  renderSiembra();
    if (name === 'shape')    refreshShapeActive();
    if (name === 'pid')      renderPid();
    if (name === 'calibrar') renderCalibrar();
    if (name === 'prueba')   renderPrueba();
  }

  document.querySelectorAll('.tab').forEach(function (t) {
    t.addEventListener('click', function () { showTab(t.getAttribute('data-tab')); });
  });

  // ============================================================================
  // MONITOR
  // ============================================================================

  var FRESH_MS = 3000;
  var statusEl = $('qxStatus');
  var listEl   = $('qxList');
  var emptyEl  = $('qxEmpty');

  function deltaClass(target, real) {
    if (!target || target <= 0) return '';
    var pct = Math.abs((real - target) / target) * 100;
    if (pct <= 2) return 'delta-ok';
    if (pct <= 8) return 'delta-warn';
    return 'delta-err';
  }
  function deltaPct(target, real) {
    if (!target || target <= 0) return 0;
    return ((real - target) / target) * 100;
  }
  function pwmPct(pwm) {
    if (!pwm) return 0;
    return Math.max(0, Math.min(100, Math.round((pwm / 4095) * 100)));
  }

  function renderMotorCard(uid, nodoOnline, m) {
    var target = pick(m, 'ppsTarget', 'PpsTarget') || 0;
    var real   = pick(m, 'ppsReal',   'PpsReal')   || 0;
    var pwm    = pick(m, 'pwm',       'Pwm')       || 0;
    var rpm    = pick(m, 'rpm',       'Rpm')       || 0;
    var pulsos = pick(m, 'pulsos',    'Pulsos')    || 0;
    var seen   = pick(m, 'lastSeenUtc','LastSeenUtc');

    var stale = ageMs(seen) > FRESH_MS;
    var cls = nodoOnline && !stale ? deltaClass(target, real) : 'delta-err';
    var delta = deltaPct(target, real);
    var deltaStr = (delta >= 0 ? '+' : '') + delta.toFixed(1) + '%';

    var pct = pwmPct(pwm);
    var pidLabel = nodoOnline && !stale
      ? (Math.abs(delta) <= 2 ? '<span class="pill ok"><span class="dot"></span> en setpoint</span>'
         : Math.abs(delta) <= 8 ? '<span class="pill warn"><span class="dot"></span> ajustando</span>'
         : '<span class="pill err"><span class="dot"></span> fuera de setpoint</span>')
      : '<span class="pill err"><span class="dot"></span> sin telemetría</span>';

    var mid = m.id != null ? m.id : m.Id;

    // Gauge real/target: 0..150% del target. Si target=0, mostrar fill al 0.
    var ratioPct = target > 0 ? Math.min(150, Math.max(0, (real / target) * 100)) : 0;
    var markerPct = target > 0 ? (100 / 150) * 100 : 0; // marker visual del target dentro de 0..150
    var fillCls = (nodoOnline && !stale) ? deltaClass(target, real) : 'err';
    fillCls = fillCls.replace('delta-', '');

    return '' +
      '<div class="card motor-card">' +
        '<div class="mc-head">' +
          '<h3>Motor ' + ((mid|0) + 1) + '</h3>' +
          pidLabel +
        '</div>' +

        '<div class="mc-readouts">' +
          '<div>' +
            '<div class="lbl">PPS real</div>' +
            '<div class="big real ' + cls + '">' + real.toFixed(1) + '</div>' +
          '</div>' +
          '<div>' +
            '<div class="lbl" style="text-align:right">Objetivo</div>' +
            '<div class="big tgt">' + target.toFixed(1) + '</div>' +
          '</div>' +
        '</div>' +

        // Gauge: barra real/target con marca de target
        '<div class="gauge" title="Real ' + real.toFixed(1) + ' / Objetivo ' + target.toFixed(1) + '">' +
          '<div class="fill ' + fillCls + '" style="width:' + (ratioPct / 1.5) + '%"></div>' +
          (target > 0 ? '<div class="marker" style="left:' + markerPct + '%"></div>' : '') +
        '</div>' +
        '<div class="gauge-legend"><span>0</span><span class="' + cls + '">Δ ' + deltaStr + '</span><span>+50%</span></div>' +

        // PWM como slider visual
        '<div style="margin-top: var(--agp-sp-3); font-size: var(--agp-fs-xs); color: var(--agp-text-muted); text-transform: uppercase; letter-spacing: 0.5px">PWM</div>' +
        '<div class="pwm-slider" title="PWM ' + pwm + ' / 4095 (' + pct + '%)">' +
          '<div class="track"></div>' +
          '<div class="fill" style="width:' + pct + '%"></div>' +
          '<div class="thumb" style="left:' + pct + '%"></div>' +
        '</div>' +
        '<div class="gauge-legend"><span>0</span><span>' + pwm + ' / 4095 · ' + pct + '%</span><span>4095</span></div>' +

        '<div class="kv">' +
          '<div class="k">RPM</div><div class="v">' + rpm + '</div>' +
          '<div class="k">Pulsos</div><div class="v">' + pulsos.toLocaleString() + '</div>' +
          '<div class="k">Visto</div><div class="v">' + (stale ? '⚠ ' : '') + (Math.round(ageMs(seen)/100)/10) + ' s</div>' +
          '<div class="k">UID</div><div class="v" style="font-size:var(--agp-fs-xs)">' + escapeHtml(uid) + '</div>' +
        '</div>' +
      '</div>';
  }

  function renderNodoMonitor(n) {
    var uid = pick(n, 'uid', 'Uid') || '';
    var ip  = pick(n, 'ip',  'Ip')  || '—';
    var fw  = pick(n, 'firmware', 'Firmware') || '—';
    var online = !!pick(n, 'online', 'Online');
    var motors = pick(n, 'motorsLive', 'MotorsLive') || [];

    state.liveByUid[uid] = { online: online, motors: motors };

    if (!motors.length) {
      return '' +
        '<section style="margin-bottom: var(--agp-sp-5)">' +
          '<div class="node-head">' +
            '<h2 style="margin:0">QuantiX node <span style="font-family:var(--agp-font-mono); color:var(--agp-text-muted); font-size:var(--agp-fs-md); font-weight:normal">' + escapeHtml(uid) + '</span></h2>' +
            '<span class="pill ' + (online ? 'ok' : 'err') + '"><span class="dot"></span> ' + (online ? 'online' : 'offline') + ' · ' + escapeHtml(ip) + ' · fw ' + escapeHtml(fw) + '</span>' +
          '</div>' +
          '<div class="card subtitle">Sin telemetría todavía — esperando <code>status_live</code>…</div>' +
        '</section>';
    }
    motors = motors.slice().sort(function (a, b) { return (a.id || a.Id || 0) - (b.id || b.Id || 0); });
    var cards = motors.map(function (m) { return renderMotorCard(uid, online, m); }).join('');
    return '' +
      '<section style="margin-bottom: var(--agp-sp-5)">' +
        '<div class="node-head">' +
          '<h2 style="margin:0">QuantiX node <span style="font-family:var(--agp-font-mono); color:var(--agp-text-muted); font-size:var(--agp-fs-md); font-weight:normal">' + escapeHtml(uid) + '</span></h2>' +
          '<span class="pill ' + (online ? 'ok' : 'err') + '"><span class="dot"></span> ' + (online ? 'online' : 'offline') + ' · ' + escapeHtml(ip) + ' · fw ' + escapeHtml(fw) + '</span>' +
        '</div>' +
        '<div class="grid">' + cards + '</div>' +
      '</section>';
  }

  async function pollLive() {
    try {
      var res = await fetch('/api/quantix/live', { cache: 'no-store' });
      var data = await res.json();
      var nodos = pick(data, 'nodos', 'Nodos') || [];
      if (statusEl) {
        statusEl.className = 'pill ' + (nodos.length > 0 ? 'ok' : 'warn');
        statusEl.innerHTML = '<span class="dot"></span> ' + nodos.length + ' nodo' + (nodos.length !== 1 ? 's' : '') + ' QuantiX';
      }
      if (state.activeTab === 'monitor') {
        if (nodos.length === 0) {
          if (listEl) listEl.innerHTML = '';
          if (emptyEl) emptyEl.style.display = '';
          return;
        }
        if (emptyEl) emptyEl.style.display = 'none';
        if (listEl) listEl.innerHTML = nodos.map(renderNodoMonitor).join('');
      } else {
        // Mantener cache para Calibración (necesita pulsos)
        for (var i = 0; i < nodos.length; i++) {
          var n = nodos[i];
          var uid = pick(n, 'uid', 'Uid');
          var motors = pick(n, 'motorsLive', 'MotorsLive') || [];
          state.liveByUid[uid] = { online: !!pick(n, 'online', 'Online'), motors: motors };
        }
        // Si estoy en Calibrar, refrescá pulsos
        if (state.activeTab === 'calibrar') updateCalibrarPulses();
        if (state.activeTab === 'pid')      updatePidLive();
        if (state.activeTab === 'prueba')   updatePruebaLive();
      }
    } catch (e) {
      if (statusEl) {
        statusEl.className = 'pill err';
        statusEl.innerHTML = '<span class="dot"></span> Sin conexión';
      }
    }
  }

  // ============================================================================
  // MOTORES — CRUD persistente + send config MQTT
  // ============================================================================

  async function loadMotores() {
    try {
      var res = await fetch('/api/quantix/motores', { cache: 'no-store' });
      var data = await res.json();
      if (data && data.ok && data.config) {
        state.motoresCfg = data.config;
        if (!state.motoresCfg.nodos) state.motoresCfg.nodos = [];
      }
    } catch (e) { /* ignore */ }
    if (state.activeTab === 'siembra') renderSiembra();
  }

  function defaultMotor(nombre) {
    return {
      nombre: nombre || 'Motor', dosis_fija: 0, campo_dosis: '',
      kp: 80, ki: 30, kd: 0, pwm_min: 600, pwm_max: 4095, meter_cal: 50,
      max_integral: 1200, deadband: 2, slew_rate: 40, dientes_engranaje: 20,
      motor_type: 0, max_hz: 40, ff_gain: 1.0, alpha: 0.4,
      slew_rate_per_sec: 5000, pid_time: 50,
      // surcos_cubiertos[] es la fuente única de verdad de qué surcos físicos
      // alimenta este motor. cortes[] queda como cache derivado para el bridge
      // que aún habla en términos de secciones PilotX (se recalcula al guardar).
      surcos_cubiertos: [], cortes: [], tren: 0
    };
  }

  function renderMotorCfg(nodoIdx, motorIdx) {
    var m = state.motoresCfg.nodos[nodoIdx].motores[motorIdx] || defaultMotor();
    var mClass = motorIdx === 0 ? '' : 'm1';

    // Chips por SECCIÓN PilotX (las que vienen de AOG vía /api/aog/state).
    // El feedback del usuario fue claro: "deben ser las secciones que traemos
    // de AOG", no surcos físicos del implemento. cortes[] es la fuente de
    // verdad y se persiste tal cual. surcos_cubiertos[] queda como legacy
    // pasthrough (no editable acá; si en el futuro se reintroduce el modo
    // surco-físico se hará detrás de un toggle explícito).
    var nsAog = state.aogNumSections | 0;
    var savedSec = {};
    (m.cortes || []).forEach(function (c) { savedSec[c | 0] = true; });
    // Si el backend AOG aún no respondió, deducimos el rango del propio cortes[]
    // guardado para no perder la visualización en frío.
    var maxKnown = nsAog;
    if (!maxKnown) (m.cortes || []).forEach(function (c) { if ((c | 0) > maxKnown) maxKnown = c | 0; });
    var surcosHtml = '';
    if (maxKnown <= 0) {
      surcosHtml = '<span style="color: var(--agp-text-muted); font-size: var(--agp-fs-sm)">' +
        'No se detectaron secciones de PilotX. Configurá las secciones del implemento en la UI nativa de PilotX.</span>';
    } else {
      for (var s2 = 1; s2 <= maxKnown; s2++) {
        var chk2 = savedSec[s2] ? ' checked' : '';
        surcosHtml +=
          '<label class="sec-chip"><input type="checkbox" data-sec="' + s2 + '"' + chk2 + '> S' + s2 + '</label>';
      }
    }

    // Shapefile dropdown: columnas DBF del shape activo en el piloto. Preferimos
    // numéricas (las que sirven para dosis); las no numéricas quedan al final
    // marcadas. Si el shape no está cargado, mostramos el guardado como
    // "(sin shape)" para no perder la config al editar.
    var hasShape = !!(state.shapeSource);
    var shapeOpts = [{ v: '', t: hasShape ? '— sin asignar —' : '— sin shape cargado —' }];
    var numericNames = {};
    if (Array.isArray(state.shapeFields)) {
      state.shapeFields.forEach(function (f) {
        if (!f || !f.name) return;
        if (f.numeric) numericNames[f.name] = true;
      });
      // Numéricas primero
      state.shapeFields.forEach(function (f) {
        if (f && f.name && f.numeric) {
          var rng = (isFinite(f.min) && isFinite(f.max))
            ? ' (' + fmtNum(f.min) + '..' + fmtNum(f.max) + ')' : '';
          shapeOpts.push({ v: f.name, t: f.name + rng });
        }
      });
      // No-numéricas al final, deshabilitadas visualmente pero seleccionables
      state.shapeFields.forEach(function (f) {
        if (f && f.name && !f.numeric) {
          shapeOpts.push({ v: f.name, t: f.name + ' (no numérico)' });
        }
      });
    }
    // Preservar valor guardado aunque no esté en el shape actual
    if (m.campo_dosis && !shapeOpts.some(function (o) { return o.v === m.campo_dosis; })) {
      shapeOpts.push({ v: m.campo_dosis, t: m.campo_dosis + (hasShape ? ' (no existe en shape)' : ' (guardado)') });
    }

    return '' +
      '<div class="motor-cfg ' + mClass + '" data-mi="' + motorIdx + '">' +
        '<h4>M' + motorIdx + ' — <input type="text" data-f="nombre" value="' + escapeHtml(m.nombre || '') + '" style="font-weight:bold; width:auto; min-width:160px"></h4>' +
        '<div class="fld-grid">' +
          fld('Dosis fija (0=mapa)', 'dosis_fija', m.dosis_fija, 'number', '0', '0.1') +
          fldSelect('Campo shapefile', 'campo_dosis', m.campo_dosis, shapeOpts) +
          fld('MeterCal',            'meter_cal',   m.meter_cal,   'number', '0.1', '0.5') +
          fld('Dientes',             'dientes_engranaje', m.dientes_engranaje, 'number', '1', '1') +
          fldSelect('Tipo motor', 'motor_type', m.motor_type | 0, [
            { v: 0, t: 'Eléctrico' },
            { v: 1, t: 'Hidráulico' }
          ], true) +
          fldSelect('Tren', 'tren', m.tren | 0, trenOpts(m.tren | 0), true) +
          // step=10 (no 1) porque sin el drag del slider, ir de 0 a 4095 de a 1
          // con autorepeat tarda casi un minuto. Con 10, autorepeat 80ms ≈ 5s.
          // Para afinar más fino, el operario puede tocar tap-a-tap.
          fldRange('PWM min', 'pwm_min', m.pwm_min | 0, 0, 4095, 10) +
          fldRange('PWM max', 'pwm_max', m.pwm_max | 0, 0, 4095, 10) +
        '</div>' +
        '<div class="sec-row">' +
          '<label class="sec-row-label">Secciones PilotX cubiertas <span style="font-weight:normal; color: var(--agp-text-muted); font-size: var(--agp-fs-sm)">(' + (nsAog || maxKnown || 0) + ' detectadas — se configuran en <a href="herramienta.html#seccionesx" style="color: var(--agp-accent)">Implemento → Secciones</a>)</span></label>' +
          '<div class="sec-grid">' + surcosHtml + '</div>' +
        '</div>' +
      '</div>';
  }

  function fld(label, name, value, type, min, step) {
    var attrs = 'data-f="' + name + '" type="' + (type || 'text') + '"';
    if (min !== undefined) attrs += ' min="' + min + '"';
    if (step !== undefined) attrs += ' step="' + step + '"';
    var v = value == null ? '' : value;
    return '<div class="field"><label>' + escapeHtml(label) + '</label>' +
           '<input ' + attrs + ' value="' + escapeHtml(v) + '"></div>';
  }

  // Select con data-int="1" cuando el valor es entero (motor_type, tren).
  // readMotoresFromUI lo usa para parsear con parseInt en lugar de string.
  function fldSelect(label, name, value, options, isInt) {
    var v = (value == null ? '' : String(value));
    var opts = '';
    for (var i = 0; i < options.length; i++) {
      var o = options[i];
      var ov = String(o.v);
      opts += '<option value="' + escapeHtml(ov) + '"' +
              (ov === v ? ' selected' : '') + '>' + escapeHtml(o.t) + '</option>';
    }
    return '<div class="field"><label>' + escapeHtml(label) + '</label>' +
           '<select data-f="' + name + '"' + (isInt ? ' data-int="1"' : '') + '>' + opts + '</select></div>';
  }

  // Stepper PWM (antes era slider). El thumb del range era imposible de mover
  // con dedo grueso sobre la pantalla del tractor — un par de botones [−][+]
  // con autorepeat al mantener apretado es mucho más práctico. El <output>
  // queda como readout secundario en el label (se actualiza vía input event
  // delegado más abajo); el stepper trae su propio readout interno.
  function fldRange(label, name, value, min, max, step) {
    var v = value == null ? min : value;
    var stepperHtml = window.AGPSteps
      ? window.AGPSteps.stepperHTML({
          value: v, min: min, max: max, mode: 'int', step: step || 1,
          attrs: 'data-f="' + name + '" data-int="1"'
        })
      : ('<input data-f="' + name + '" data-int="1" type="number" min="' + min +
         '" max="' + max + '" step="' + (step || 1) + '" value="' + escapeHtml(v) + '">');
    return '<div class="field"><label>' + escapeHtml(label) +
           ' <output class="range-out">' + escapeHtml(v) + '</output></label>' +
           stepperHtml + '</div>';
  }

  // Opciones del dropdown de tren para cada motor. Se arman desde el implemento
  // CENTRAL (Herramienta), no desde la config local de QuantiX. Si el motor
  // tiene un tren guardado que ya no existe, lo mostramos al final como
  // "(eliminado)" para no perder la config silenciosamente.
  function trenOpts(currentId) {
    var trenes = (state.implCentral && Array.isArray(state.implCentral.trenes))
      ? state.implCentral.trenes : [];
    var opts = trenes.map(function (t) {
      var lbl = (t.nombre || ('Tren ' + t.id));
      if (t.distancia_m) lbl += ' (' + fmtNum(t.distancia_m) + ' m)';
      return { v: t.id | 0, t: lbl };
    });
    if (opts.length === 0) opts.push({ v: 0, t: '— definí trenes en Herramienta —' });
    var exists = opts.some(function (o) { return (o.v | 0) === (currentId | 0); });
    if (!exists) opts.push({ v: currentId | 0, t: 'Tren ' + currentId + ' (eliminado)' });
    return opts;
  }

  // Banner read-only: trenes/surcos/secciones se editan en Herramienta. QuantiX
  // sólo lee. Mostramos un resumen para que el operario sepa de dónde sale el
  // mapeo que está usando.
  function renderTrenesBar() {
    var c = state.implCentral || {};
    var nT = Array.isArray(c.trenes) ? c.trenes.length : 0;
    var nS = (c.numero_surcos | 0) || (Array.isArray(c.surcos) ? c.surcos.length : 0);
    var nSec = Array.isArray(c.secciones) ? c.secciones.length : 0;
    return '' +
      '<div class="card" style="margin-bottom: var(--agp-sp-4); border-left: 3px solid var(--agp-accent)">' +
        '<div class="node-head" style="margin-bottom: var(--agp-sp-2)">' +
          '<h3 style="margin:0">Implemento (read-only)</h3>' +
          '<a class="btn" href="herramienta.html">Editar en Herramienta</a>' +
        '</div>' +
        '<div class="subtitle" style="color: var(--agp-text-muted)">' +
          'Trenes: <strong>' + (nT || '—') + '</strong> · ' +
          'Surcos: <strong>' + (nS || '—') + '</strong> · ' +
          'Secciones PilotX: <strong>' + (nSec || '—') + '</strong>' +
        '</div>' +
        (nT === 0
          ? '<div class="subtitle" style="margin-top: var(--agp-sp-2); color: var(--agp-state-warn-tx)">Sin trenes definidos — los motores no tendrán dónde asignarse hasta que cargues Herramienta.</div>'
          : '') +
      '</div>';
  }

  function renderMotores() {
    var listEl = $('mtList');
    var cfg = state.motoresCfg;
    var html = renderTrenesBar();
    if (!cfg.nodos || cfg.nodos.length === 0) {
      listEl.innerHTML = html + '<div class="card subtitle">No hay nodos QuantiX configurados. ' +
        'Cuando un nodo nuevo aparezca por MQTT (<code>agp/quantix/+/announcement</code>) se auto-registra al primer arranque.</div>';
      return;
    }
    for (var i = 0; i < cfg.nodos.length; i++) {
      var n = cfg.nodos[i];
      if (!n.motores) n.motores = [defaultMotor('Producto 1'), defaultMotor('Producto 2')];
      html += '<div class="card" data-ni="' + i + '" style="margin-bottom: var(--agp-sp-4)">' +
        '<div class="node-head">' +
          '<div class="uid-row" style="flex:1">' +
            '<input type="text" data-nf="nombre" value="' + escapeHtml(n.nombre || '') + '" style="font-weight:bold; max-width:280px" placeholder="Nombre del nodo">' +
            '<span style="color: var(--agp-text-muted); font-size: var(--agp-fs-xs)">UID</span>' +
            '<input type="text" data-nf="uid" value="' + escapeHtml(n.uid || '') + '" style="font-family: var(--agp-font-mono); max-width:200px">' +
            '<label style="display:flex; gap:6px; align-items:center"><input type="checkbox" data-nf="habilitado"' + (n.habilitado ? ' checked' : '') + '> <span>Habilitado</span></label>' +
          '</div>' +
          '<span class="pill ' + (state.liveByUid[n.uid] && state.liveByUid[n.uid].online ? 'ok' : 'err') + '">' +
            '<span class="dot"></span> ' + (state.liveByUid[n.uid] && state.liveByUid[n.uid].online ? 'online' : 'offline') +
          '</span>' +
        '</div>' +
        renderMotorCfg(i, 0) +
        renderMotorCfg(i, 1) +
        '<div class="btn-row">' +
          '<button class="btn" data-act="send" data-ni="' + i + '">Enviar config por MQTT</button>' +
          '<button class="btn" data-act="del" data-ni="' + i + '" style="color:#C92D2D">Quitar nodo</button>' +
          '<span class="send-msg" data-msg="' + i + '"></span>' +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
    // Engancha los steppers PWM min/max de cada motor recién renderizado.
    // bindSteppers es idempotente sobre el mismo root (lo marca con un flag).
    if (window.AGPSteps) window.AGPSteps.bindSteppers(listEl);
  }

  function readMotoresFromUI() {
    var cfg = state.motoresCfg;

    // Trenes ya no se editan acá (vienen del implemento central). Mantenemos
    // cfg.trenes sincronizado al guardar para que el backend QuantiX siga
    // recibiendo la lista que espera (compat con QxTrenConfigDto).
    if (state.implCentral && Array.isArray(state.implCentral.trenes)) {
      cfg.trenes = state.implCentral.trenes.map(function (t) {
        return { id: t.id | 0, nombre: t.nombre || ('Tren ' + t.id), distancia_m: t.distancia_m || 0 };
      });
    }

    var cards = document.querySelectorAll('#mtList > .card[data-ni]');
    cards.forEach(function (card) {
      var ni = parseInt(card.getAttribute('data-ni'), 10);
      var n = cfg.nodos[ni];
      if (!n) return;
      card.querySelectorAll('input[data-nf]').forEach(function (inp) {
        var f = inp.getAttribute('data-nf');
        n[f] = (inp.type === 'checkbox') ? inp.checked : inp.value;
      });
      var motorCfgs = card.querySelectorAll('.motor-cfg');
      motorCfgs.forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = n.motores[mi]; if (!m) return;
        mc.querySelectorAll('[data-f]').forEach(function (inp) {
          var f = inp.getAttribute('data-f');
          var isInt = inp.getAttribute('data-int') === '1';
          if (inp.type === 'range' || isInt) {
            m[f] = parseInt(inp.value, 10) || 0;
          } else if (inp.type === 'number') {
            m[f] = parseFloat(inp.value) || 0;
          } else {
            m[f] = inp.value || '';
          }
        });
        // Secciones PilotX cubiertas (cortes[]) — fuente de verdad única en UI.
        // surcos_cubiertos[] queda intacto (legacy passthrough): si no había
        // chips data-sec en el DOM (caso "no hay secciones detectadas") no
        // tocamos cortes para no borrar lo que ya estaba guardado.
        var secInputs = mc.querySelectorAll('input[data-sec]');
        if (secInputs.length > 0) {
          var cortes = [];
          secInputs.forEach(function (s) {
            if (s.checked) cortes.push(parseInt(s.getAttribute('data-sec'), 10));
          });
          m.cortes = cortes;
        }
      });
    });
    return cfg;
  }

  async function saveMotores() {
    var msg = $('mtMsg');
    msg.textContent = 'Guardando…';
    var cfg = readMotoresFromUI();
    try {
      var res = await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var data = await res.json();
      msg.textContent = data.ok ? '✓ Guardado.' : '✕ ' + (data.error || 'error');
    } catch (e) { msg.textContent = '✕ ' + e.message; }
  }

  async function sendNodo(idx, msgEl) {
    msgEl.textContent = '… enviando';
    msgEl.className = 'send-msg';
    var cfg = readMotoresFromUI();
    var n = cfg.nodos[idx];
    if (!n || !n.uid) { msgEl.textContent = '✕ falta UID'; msgEl.className = 'send-msg err'; return; }
    try {
      // Persistir primero (para que el backend lea el config actualizado)
      await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var res = await fetch('/api/quantix/' + encodeURIComponent(n.uid) + '/send', { method: 'POST' });
      var data = await res.json();
      msgEl.textContent = data.ok ? '✓ ' + (data.topic || 'enviado') : '✕ ' + (data.error || 'sin conexión MQTT');
      msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message; msgEl.className = 'send-msg err';
    }
  }

  async function sendAllNodos() {
    var msg = $('mtMsg'); msg.textContent = 'Enviando todos…';
    var cfg = readMotoresFromUI();
    await fetch('/api/quantix/motores', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cfg)
    });
    var sent = 0, fail = 0;
    for (var i = 0; i < cfg.nodos.length; i++) {
      var n = cfg.nodos[i];
      if (!n.habilitado || !n.uid) continue;
      try {
        var res = await fetch('/api/quantix/' + encodeURIComponent(n.uid) + '/send', { method: 'POST' });
        var data = await res.json();
        if (data.ok) sent++; else fail++;
      } catch (e) { fail++; }
    }
    msg.textContent = 'Enviados: ' + sent + ' · Fallos: ' + fail;
  }

  // addNodo() (creaba un nodo con UID random "QX-XXXXXXXX") fue eliminado:
  // los nodos solo se incorporan vía importDiscovered() desde el registry
  // de announcements MQTT reales. Esto evita typos y desalineación con el
  // firmware del nodo físico.

  function delNodo(idx) {
    if (!confirm('¿Quitar nodo ' + (state.motoresCfg.nodos[idx].uid || '?') + '?')) return;
    var uid = state.motoresCfg.nodos[idx].uid;
    if (uid && state.motoresCfg.ignorados.indexOf(uid) < 0) state.motoresCfg.ignorados.push(uid);
    state.motoresCfg.nodos.splice(idx, 1);
    saveMotores();
    renderMotores();
  }

  // (Trenes ya no se crean/borran desde QuantiX — Herramienta es la fuente única.)

  var mtListEl = $('mtList');
  if (mtListEl) mtListEl.addEventListener('click', function (ev) {
    var btn = ev.target.closest('button[data-act]');
    if (!btn) return;
    var idx = parseInt(btn.getAttribute('data-ni'), 10);
    if (btn.getAttribute('data-act') === 'send') {
      var msgEl = document.querySelector('span[data-msg="' + idx + '"]');
      sendNodo(idx, msgEl);
    } else if (btn.getAttribute('data-act') === 'del') {
      delNodo(idx);
    }
  });
  // Readout secundario en el label cuando el operario toca [−]/[+] del stepper
  // (o, históricamente, arrastra el slider — ahora ya no hay slider pero el
  // handler sigue tolerando ambos). Buscamos .range-out subiendo a .field
  // porque el input vive adentro del .agp-stepper.
  if (mtListEl) mtListEl.addEventListener('input', function (ev) {
    var inp = ev.target;
    if (inp.tagName !== 'INPUT') return;
    if (inp.type !== 'range' && inp.type !== 'hidden' && inp.type !== 'number') return;
    var field = inp.closest('.field'); if (!field) return;
    var out = field.querySelector('.range-out');
    if (out) out.textContent = inp.value;
  });
  // btnAddNodo eliminado del HTML — ver comentario arriba (solo auto-descubrimiento).

  async function importDiscovered() {
    readMotoresFromUI();
    // Traemos QuantiX vivos del registry
    var found = [];
    try {
      var res = await fetch('/api/quantix/live', { cache: 'no-store' });
      var data = await res.json();
      found = pick(data, 'nodos', 'Nodos') || [];
    } catch (e) {}
    if (!found.length) {
      $('mtMsg').textContent = 'No hay nodos QuantiX descubiertos por MQTT en este momento.';
      return;
    }
    var existing = {};
    (state.motoresCfg.nodos || []).forEach(function (n) { if (n.uid) existing[n.uid.toUpperCase()] = true; });
    var ignored = {};
    (state.motoresCfg.ignorados || []).forEach(function (u) { if (u) ignored[u.toUpperCase()] = true; });
    var added = 0;
    for (var i = 0; i < found.length; i++) {
      var uid = pick(found[i], 'uid', 'Uid');
      if (!uid) continue;
      var k = uid.toUpperCase();
      if (existing[k] || ignored[k]) continue;
      var idx = state.motoresCfg.nodos.length + 1;
      var baseCorte = (idx - 1) * 7 + 1;
      var cortes = []; for (var c = 0; c < 7; c++) cortes.push(baseCorte + c);
      state.motoresCfg.nodos.push({
        uid: uid,
        nombre: 'QuantiX ' + idx,
        habilitado: true,
        distancia_entre_trenes: 0,
        motores: [
          Object.assign(defaultMotor('Producto 1'), { cortes: cortes }),
          defaultMotor('Producto 2')
        ]
      });
      existing[k] = true;
      added++;
    }
    if (added > 0) {
      await saveMotores();
      renderMotores();
      $('mtMsg').textContent = '✓ Importados ' + added + ' nodo' + (added > 1 ? 's' : '') + ' descubierto' + (added > 1 ? 's' : '') + '.';
    } else {
      $('mtMsg').textContent = 'Todos los nodos descubiertos ya están configurados.';
    }
  }
  var btnImp = $('btnImportDiscovered');
  if (btnImp) btnImp.addEventListener('click', importDiscovered);
  $('btnSaveMotores').addEventListener('click', saveMotores);
  $('btnSendAll').addEventListener('click', sendAllNodos);

  (function bindStripPaint() {
    var strip = document.getElementById('qxStrip');
    if (!strip || strip._painted) return;
    strip._painted = true;
    var painting = false;
    function cellSurco(t) {
      return t && t.classList && t.classList.contains('cell')
        ? parseInt(t.getAttribute('data-surco'), 10) : NaN;
    }
    function apply(t) {
      var s = cellSurco(t);
      if (!isNaN(s)) { state.lastTouchedSurco = s; paintSurco(s); renderStrip(); renderMotorList(); }
    }
    strip.addEventListener('pointerdown', function (e) {
      if (state.siembraView !== 'planter' || state.activeTab !== 'siembra') return;
      painting = true; apply(e.target);
      try { strip.setPointerCapture(e.pointerId); } catch (err) {}
    });
    strip.addEventListener('pointermove', function (e) {
      if (!painting) return;
      apply(document.elementFromPoint(e.clientX, e.clientY));
    });
    strip.addEventListener('pointerup', function () { painting = false; });
    strip.addEventListener('pointercancel', function () { painting = false; });
  })();

  var btnAdd = document.getElementById('btnAddMotor');
  if (btnAdd) btnAdd.addEventListener('click', addMotor);
  var btnQuit = document.getElementById('btnQuitarSurco');
  if (btnQuit) btnQuit.addEventListener('click', quitarSurco);
  var btnAuto = document.getElementById('btnAutoReparto');
  if (btnAuto) btnAuto.addEventListener('click', function () {
    var nodo = activeNodo();
    if (!nodo) return;
    autoReparto((nodo.motores || []).length < totalSurcos() ? 'uno' : 'grupos');
  });

  // ============================================================================
  // PID LIVE-TUNE
  // ============================================================================

  function renderPid() {
    var listEl = $('pidList');
    var emptyEl = $('pidEmpty');
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (nodos.length === 0) { listEl.innerHTML = ''; emptyEl.style.display = ''; return; }
    emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      var live = state.liveByUid[n.uid];
      html += '<div class="card" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<div class="node-head">' +
          '<h3 style="margin:0">' + escapeHtml(n.nombre || 'Nodo') + ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' + escapeHtml(n.uid) + '</span></h3>' +
          '<span class="pill ' + (live && live.online ? 'ok' : 'err') + '"><span class="dot"></span> ' + (live && live.online ? 'online' : 'offline') + '</span>' +
        '</div>' +
        '<div class="live-tune-grid">' +
          pidTuneCard(n, 0) +
          pidTuneCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;

    // Enganchar [−]/[+] de los steppers Kp/Ki/Kd. El paso se calcula sobre el
    // valor *actual* (adaptativo PID): v<1→0.01, v<10→0.1, v<100→1, v≥100→5.
    // bindSteppers es idempotente sobre el mismo root.
    if (window.AGPSteps) window.AGPSteps.bindSteppers(listEl);
  }

  function pidTuneCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    // Texto inicial con decimales acordes al paso adaptativo. El step queda
    // como hint; AGPSteps.attachAdaptive lo va a sobreescribir en runtime.
    var fmtPid = function (v) {
      return window.AGPSteps ? window.AGPSteps.fmt(v, window.AGPSteps.pid(v)) : String(v);
    };
    // PID con steppers adaptativos (sin slider). Paso = pid(value):
    //   v < 1   → 0.01    v < 10  → 0.1
    //   v < 100 → 1       v ≥ 100 → 5
    // El step acompaña al valor actual — fácil afinar Kd=0.5 y a la vez Kp=120.
    var stepperPid = function (name, val, mx) {
      return window.AGPSteps
        ? window.AGPSteps.stepperHTML({
            value: val, min: 0, max: mx, mode: 'pid',
            attrs: 'data-tune="' + name + '"'
          })
        : '<input type="number" data-tune="' + name + '" value="' + val + '" min="0" max="' + mx + '">';
    };
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +
      '<div class="kv" style="margin-top:0">' +
        '<div class="k">PPS real</div><div class="v" data-live="pps_real">—</div>' +
        '<div class="k">PPS target</div><div class="v" data-live="pps_target">—</div>' +
        '<div class="k">PWM</div><div class="v" data-live="pwm">—</div>' +
      '</div>' +
      '<div class="fld-grid" style="margin-top: var(--agp-sp-3)">' +
        '<div class="field"><label>Kp <span data-show="kp">' + fmtPid(m.kp) + '</span></label>' +
          stepperPid('kp', m.kp, 300) + '</div>' +
        '<div class="field"><label>Ki <span data-show="ki">' + fmtPid(m.ki) + '</span></label>' +
          stepperPid('ki', m.ki, 200) + '</div>' +
        '<div class="field"><label>Kd <span data-show="kd">' + fmtPid(m.kd) + '</span></label>' +
          stepperPid('kd', m.kd, 50) + '</div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-tune-act="push" data-mi="' + mi + '">Aplicar Kp/Ki/Kd</button>' +
        '<button class="btn" data-tune-act="maxhz" data-mi="' + mi + '" title="Mide Hz pico con PWM máximo durante 4s y guarda en max_hz">⏱ Medir Max Hz</button>' +
        '<button class="btn" data-tune-act="autotune" data-mi="' + mi + '" title="Auto-Tune PID (Ziegler-Nichols). Tarda ~30s">🎯 Auto-Tune</button>' +
        '<span class="send-msg" data-tune-msg="' + mi + '"></span>' +
      '</div>' +
    '</div>';
  }

  function updatePidLive() {
    document.querySelectorAll('#pidList .card[data-uid]').forEach(function (card) {
      var uid = card.getAttribute('data-uid');
      var live = state.liveByUid[uid];
      if (!live || !live.motors) return;
      card.querySelectorAll('.motor-cfg').forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = null;
        for (var k = 0; k < live.motors.length; k++) {
          if ((live.motors[k].id || live.motors[k].Id || 0) === mi) { m = live.motors[k]; break; }
        }
        if (!m) return;
        var t = pick(m, 'ppsTarget', 'PpsTarget') || 0;
        var r = pick(m, 'ppsReal',   'PpsReal')   || 0;
        var p = pick(m, 'pwm',       'Pwm')       || 0;
        var elT = mc.querySelector('[data-live="pps_target"]');
        var elR = mc.querySelector('[data-live="pps_real"]');
        var elP = mc.querySelector('[data-live="pwm"]');
        if (elT) elT.textContent = t.toFixed(1);
        if (elR) elR.textContent = r.toFixed(1);
        if (elP) elP.textContent = p;
      });
    });
  }

  document.getElementById('tabPid').addEventListener('input', function (ev) {
    var s = ev.target.closest('input[data-tune]');
    if (!s) return;
    // Antes el input era hijo directo del .field (al lado del label que tiene
    // el [data-show]). Con el stepper, el input vive dentro del .agp-stepper,
    // así que subimos hasta .field para encontrar el data-show del label.
    var field = s.closest('.field');
    var sh = field && field.querySelector('span[data-show]');
    if (sh) {
      // Decimales acordes al paso adaptativo (0.01 → "0.30", 1 → "80").
      sh.textContent = window.AGPSteps
        ? window.AGPSteps.fmt(s.value, window.AGPSteps.pid(s.value))
        : s.value;
    }
  });

  document.getElementById('tabPid').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-tune-act]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(mc.getAttribute('data-mi'), 10);
    var act = btn.getAttribute('data-tune-act');
    var msgEl = mc.querySelector('span[data-tune-msg="' + mi + '"]');

    if (act === 'push') return pidPushHandler(uid, mi, mc, msgEl);
    if (act === 'maxhz') return pidMaxHzHandler(uid, mi, mc, btn, msgEl);
    if (act === 'autotune') return pidAutoTuneHandler(uid, mi, mc, btn, msgEl);
  });

  async function pidPushHandler(uid, mi, mc, msgEl) {
    var kp = parseFloat(mc.querySelector('input[data-tune="kp"]').value);
    var ki = parseFloat(mc.querySelector('input[data-tune="ki"]').value);
    var kd = parseFloat(mc.querySelector('input[data-tune="kd"]').value);
    msgEl.textContent = '… enviando'; msgEl.className = 'send-msg';

    // Persistir en motores cfg local
    var nIdx = -1;
    for (var i = 0; i < state.motoresCfg.nodos.length; i++) {
      if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
    }
    if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
      var mref = state.motoresCfg.nodos[nIdx].motores[mi];
      mref.kp = kp; mref.ki = ki; mref.kd = kd;
    }

    // Payload "config" parcial: solo idx + config_pid (firmware lo mergea)
    var payload = JSON.stringify({ configs: [{ idx: mi, config_pid: { kp: kp, ki: ki, kd: kd } }] });
    try {
      var res = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=config&retain=true', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload
      });
      var data = await res.json();
      msgEl.textContent = data.ok ? '✓ aplicado' : '✕ ' + (data.error || 'fallo');
      msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');

      // Persistir motores.json
      await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.motoresCfg)
      });
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message; msgEl.className = 'send-msg err';
    }
  }

  // ── Medir Max Hz ──────────────────────────────────────────────────────────
  // Manda PWM=4095 (start), muestrea ppsReal cada 200ms durante 4s, manda
  // PWM=0 (stop) y guarda el máximo como motor.max_hz en motores.json + push
  // por MQTT /config.
  async function pidMaxHzHandler(uid, mi, mc, btn, msgEl) {
    if (btn.disabled) return;
    btn.disabled = true;
    var origText = btn.textContent;
    btn.textContent = '⏳ Midiendo…';
    msgEl.textContent = '… motor a PWM máximo 4s'; msgEl.className = 'send-msg';

    var peak = 0;
    try {
      // Arrancar motor a PWM=4095
      await sendTest(uid, mi, 4095);

      // Muestrear ppsReal 20 veces * 200ms = 4s
      for (var k = 0; k < 20; k++) {
        await new Promise(function (r) { setTimeout(r, 200); });
        var m = getLiveMotor(uid, mi);
        if (m) {
          var pps = pick(m, 'ppsReal', 'PpsReal') || 0;
          if (pps > peak) peak = pps;
        }
      }
    } finally {
      try { await sendTest(uid, mi, 0); } catch (e) {}
    }

    btn.disabled = false;
    btn.textContent = origText;

    if (peak < 1) {
      msgEl.textContent = '✕ no se detectaron pulsos — revisá el sensor';
      msgEl.className = 'send-msg err';
      return;
    }

    // Aplicar y guardar.
    var maxHz = Math.round(peak * 10) / 10;
    var nIdx = -1;
    for (var i = 0; i < state.motoresCfg.nodos.length; i++)
      if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
    if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
      state.motoresCfg.nodos[nIdx].motores[mi].max_hz = maxHz;
      try {
        await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
      } catch (e) {}
    }
    msgEl.textContent = '✓ Max Hz = ' + maxHz.toFixed(1) + ' (aplicado)';
    msgEl.className = 'send-msg ok';
  }

  // ── Auto-Tune PID ─────────────────────────────────────────────────────────
  // Manda {"cmd":"autotune_start","id":mi} al topic /cmd, después poolea el
  // endpoint /autotune cada 1s buscando un resultado posterior al inicio,
  // hasta 50s de timeout. Si llega ok=true, pide confirmación y aplica
  // Kp/Ki/Kd.
  async function pidAutoTuneHandler(uid, mi, mc, btn, msgEl) {
    if (btn.disabled) return;
    btn.disabled = true;
    var origText = btn.textContent;
    btn.textContent = '⏳ Tuning…';
    msgEl.textContent = '… autotune en curso (hasta 50s)'; msgEl.className = 'send-msg';

    var startedAt = Date.now();

    try {
      var startRes = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cmd&retain=false', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: 'autotune_start', id: mi })
      });
      var startData = await startRes.json();
      if (!startData.ok) {
        msgEl.textContent = '✕ no se pudo enviar start: ' + (startData.error || 'fallo');
        msgEl.className = 'send-msg err';
        btn.disabled = false; btn.textContent = origText;
        return;
      }
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message;
      msgEl.className = 'send-msg err';
      btn.disabled = false; btn.textContent = origText;
      return;
    }

    // Poll: cada 1s, buscar resultado con receivedUtc > startedAt
    var TIMEOUT_MS = 50000;
    var result = null;
    while (Date.now() - startedAt < TIMEOUT_MS) {
      await new Promise(function (r) { setTimeout(r, 1000); });
      try {
        var pr = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/autotune', { cache: 'no-store' });
        var pd = await pr.json();
        if (pd && pd.ok && pd.hasResult && pd.result) {
          var ts = Date.parse(pd.result.receivedUtc);
          if (!isNaN(ts) && ts >= startedAt - 1000 && (pd.result.motorId === mi || pd.result.motorId === 0)) {
            result = pd.result; break;
          }
        }
      } catch (e) {}
    }

    btn.disabled = false;
    btn.textContent = origText;

    if (!result) {
      msgEl.textContent = '✕ timeout — el firmware no respondió en 50s';
      msgEl.className = 'send-msg err';
      return;
    }

    if (!result.ok) {
      msgEl.textContent = '✕ autotune falló — el motor no oscilo lo suficiente';
      msgEl.className = 'send-msg err';
      return;
    }

    var kp = parseFloat(result.kp).toFixed(1);
    var ki = parseFloat(result.ki).toFixed(1);
    var kd = parseFloat(result.kd).toFixed(1);
    var apply = confirm('Auto-Tune completado:\n\nKp = ' + kp + '\nKi = ' + ki + '\nKd = ' + kd + '\n\n¿Aplicar estos valores?');
    if (!apply) {
      msgEl.textContent = 'Resultado descartado (Kp=' + kp + ' Ki=' + ki + ' Kd=' + kd + ')';
      msgEl.className = 'send-msg';
      return;
    }

    // Actualizar steppers (antes sliders) y persistir.
    // El input vive adentro del .agp-stepper; subimos a .field para encontrar
    // tanto el [data-show] del label como el [.agp-step-val] del propio stepper.
    var pidFmt = function (v) {
      return window.AGPSteps ? window.AGPSteps.fmt(v, window.AGPSteps.pid(v)) : v;
    };
    var setTune = function (name, val) {
      var inp = mc.querySelector('input[data-tune="' + name + '"]');
      if (!inp) return;
      inp.value = val;
      var field = inp.closest('.field');
      var sh = field && field.querySelector('span[data-show]');
      if (sh) sh.textContent = pidFmt(val);
      var stepVal = inp.parentElement && inp.parentElement.querySelector('.agp-step-val');
      if (stepVal) stepVal.textContent = pidFmt(val);
      inp.dispatchEvent(new Event('input', { bubbles: true }));
    };
    setTune('kp', kp); setTune('ki', ki); setTune('kd', kd);

    return pidPushHandler(uid, mi, mc, msgEl);
  }

  // ============================================================================
  // CALIBRACIÓN — el flujo viejo (FormQuantiXCalibrar) restaurado en HTML.
  //
  // Por qué este flujo y no "girá libre + ingresá lo que salió":
  //   · el operario quiere indicar de antemano CUÁNTO girar (10 vueltas,
  //     N pulsos) — el firmware para solo cuando llega a la meta y queda
  //     un Δ pulsos prolijo y repetible.
  //   · medir varios surcos y promediar es la única forma de bajar el ruido
  //     de las balanzas/conteos manuales.
  //   · "Calcular" debe ser un paso explícito — si lo hacemos implícito,
  //     el usuario no sabe cuándo el número de pantalla refleja lo que va
  //     a guardarse.
  //
  // Comando MQTT (lo espera firmware Quantix2Motors → MQTT_Custom.cpp:288):
  //   topic: agp/quantix/{uid}/cal
  //   start: {"cmd":"start","id":N,"pulsos":META,"pwm":P}   ← META = vueltas*ppr
  //   stop : {"cmd":"stop","id":N}
  // ============================================================================

  // uid -> mi -> { startPulsos, endPulsos, ppr, pwm, vueltas, surcoVals[] }
  var calState = {};

  function renderCalibrar() {
    var listEl = $('calList');
    var emptyEl = $('calEmpty');
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (nodos.length === 0) { listEl.innerHTML = ''; emptyEl.style.display = ''; return; }
    emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      html += '<div class="card" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<h3 style="margin-top:0">' + escapeHtml(n.nombre || 'Nodo') + ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' + escapeHtml(n.uid) + '</span></h3>' +
        '<p style="color: var(--agp-text-muted); margin-top: 0">' +
          '1) Configurá <strong>vueltas a girar</strong> y <strong>PWM</strong>. 2) Apretá <strong>Iniciar</strong>: ' +
          'el motor gira hasta llegar a la meta de pulsos y para solo. 3) Pesá/contá el producto recolectado por surco. ' +
          '4) Apretá <strong>Calcular</strong>: promedia los surcos y calcula <code>MeterCal = pulsos / unidades</code>.' +
        '</p>' +
        '<div class="live-tune-grid">' +
          calCard(n, 0) +
          calCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
    if (window.AGPSteps) window.AGPSteps.bindSteppers(listEl);
    // Render inicial de los inputs por surco (default 6) y suscripción a cambios.
    listEl.querySelectorAll('.motor-cfg').forEach(function (mc) { rebuildSurcoInputs(mc); });
    listEl.addEventListener('input', function (ev) {
      var inp = ev.target;
      if (!inp || !inp.getAttribute) return;
      // Cuando cambia "vueltas" o "ppr" recalculamos la meta total mostrada.
      var f = inp.getAttribute('data-cal-f');
      if (f === 'vueltas' || f === 'ppr') {
        var mc = inp.closest('.motor-cfg'); if (mc) updateMetaPulsos(mc);
      }
      // Cuando cambia "surcos" rearmamos la lista dinámica.
      if (f === 'surcos') {
        var mc2 = inp.closest('.motor-cfg'); if (mc2) rebuildSurcoInputs(mc2);
      }
    });
  }

  // Stepper helper para los campos numéricos enteros de calibración.
  // Reusa AGPSteps.stepperHTML con modo 'int' (paso fijo) para PWM/vueltas/ppr/surcos.
  function calIntStepper(field, value, opts) {
    opts = opts || {};
    var attrs = 'data-cal-f="' + field + '"';
    if (window.AGPSteps) {
      return window.AGPSteps.stepperHTML({
        value: value, min: opts.min, max: opts.max, mode: 'int',
        step: opts.step || 1, attrs: attrs
      });
    }
    return '<input type="number" ' + attrs +
      (opts.min != null ? ' min="' + opts.min + '"' : '') +
      (opts.max != null ? ' max="' + opts.max + '"' : '') +
      ' step="' + (opts.step || 1) + '" value="' + value + '">';
  }

  function calCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    var ppr = m.dientes_engranaje || 20;
    var pwmDef = Math.round(((m.pwm_min || 600) + (m.pwm_max || 4095)) / 2);
    var defaultVueltas = 10;
    var defaultSurcos = 6;
    var metaIni = defaultVueltas * ppr;
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +

      // ── Parámetros (lo que el operario configura ANTES de iniciar) ──────
      '<div class="fld-grid" style="margin-top:0">' +
        '<div class="field"><label>Vueltas a girar</label>' +
          calIntStepper('vueltas', defaultVueltas, { min: 1, max: 100, step: 1 }) + '</div>' +
        '<div class="field"><label>Pulsos por vuelta (PPR)</label>' +
          calIntStepper('ppr', ppr, { min: 1, max: 4000, step: 1 }) + '</div>' +
        '<div class="field"><label>PWM</label>' +
          calIntStepper('pwm', pwmDef, { min: 0, max: 4095, step: 10 }) + '</div>' +
        '<div class="field"><label>Cantidad de surcos</label>' +
          calIntStepper('surcos', defaultSurcos, { min: 1, max: 20, step: 1 }) + '</div>' +
      '</div>' +
      '<div class="kv" style="margin-top: var(--agp-sp-2)">' +
        '<div class="k">Meta total</div><div class="v"><span data-cal="meta">' + metaIni + '</span> pulsos</div>' +
        '<div class="k">MeterCal actual</div><div class="v">' + (m.meter_cal || 0) + '</div>' +
      '</div>' +

      // ── Botones de control del motor ────────────────────────────────────
      '<div class="btn-row" style="margin-top: var(--agp-sp-3)">' +
        '<button class="btn primary" data-cal-act="start" data-mi="' + mi + '">▶ Iniciar</button>' +
        '<button class="btn" data-cal-act="stop" data-mi="' + mi + '">■ Detener</button>' +
        '<button class="btn" data-cal-act="reset" data-mi="' + mi + '">⟲ Reset</button>' +
        '<span class="send-msg" data-cal-msg="' + mi + '"></span>' +
      '</div>' +

      // ── Estado en vivo (refrescado por updateCalibrarPulses) ────────────
      '<div class="kv" style="margin-top: var(--agp-sp-3)">' +
        '<div class="k">Pulsos contados</div><div class="v" data-cal="pulsos">—</div>' +
        '<div class="k">Δ pulsos (desde Iniciar)</div><div class="v" data-cal="delta">—</div>' +
        '<div class="k">Vueltas reales</div><div class="v" data-cal="vueltasReales">—</div>' +
        '<div class="k">PWM actual</div><div class="v" data-cal="pwmCur">—</div>' +
      '</div>' +

      // ── Surcos (lista dinámica) + Calcular ──────────────────────────────
      '<h4 style="margin-top: var(--agp-sp-4)">Resultado por surco</h4>' +
      '<p style="color: var(--agp-text-muted); margin-top:0">' +
        'Ingresá gramos o semillas medidos en cada surco. Promediar varios mejora la precisión.' +
      '</p>' +
      '<div class="fld-grid" data-cal="surcoList"></div>' +
      '<div class="btn-row" style="margin-top: var(--agp-sp-3)">' +
        '<button class="btn primary" data-cal-act="calc" data-mi="' + mi + '">✓ Calcular</button>' +
        '<button class="btn" data-cal-act="apply" data-mi="' + mi + '" disabled>💾 Guardar MeterCal</button>' +
        '<span class="send-msg" data-cal-result="' + mi + '"></span>' +
      '</div>' +
      '<div class="kv" style="margin-top: var(--agp-sp-2)" data-cal="resultBox" hidden>' +
        '<div class="k">Promedio por surco</div><div class="v" data-cal="prom">—</div>' +
        '<div class="k">Unidades / pulso</div><div class="v" data-cal="upp">—</div>' +
        '<div class="k">MeterCal calculado</div><div class="v" data-cal="newcal">—</div>' +
      '</div>' +
    '</div>';
  }

  // Vueltas × PPR = meta de pulsos que el ESP debe contar antes de parar.
  function updateMetaPulsos(mc) {
    var v = parseInt((mc.querySelector('input[data-cal-f="vueltas"]') || {}).value, 10) || 0;
    var p = parseInt((mc.querySelector('input[data-cal-f="ppr"]')     || {}).value, 10) || 0;
    var meta = v * p;
    var el = mc.querySelector('[data-cal="meta"]');
    if (el) el.textContent = meta.toLocaleString();
  }

  // Rebuild dinámico de los inputs por surco. Se llama en mount y cada vez que
  // cambia el stepper "surcos". Conservamos valores ya cargados si el nuevo
  // count es mayor o igual al anterior.
  function rebuildSurcoInputs(mc) {
    var box = mc.querySelector('[data-cal="surcoList"]');
    if (!box) return;
    var count = parseInt((mc.querySelector('input[data-cal-f="surcos"]') || {}).value, 10) || 6;
    // Capturamos valores actuales para no perder lo que el operario ya escribió.
    var prev = {};
    box.querySelectorAll('input[data-cal-surco]').forEach(function (inp) {
      prev[inp.getAttribute('data-cal-surco')] = inp.value;
    });
    var html = '';
    for (var i = 0; i < count; i++) {
      var val = prev[String(i)] != null ? prev[String(i)] : '';
      html += '<div class="field">' +
        '<label>Surco ' + (i + 1) + ' (g / semillas / L)</label>' +
        '<input type="number" data-cal-surco="' + i + '" min="0" step="0.1" value="' + val + '">' +
      '</div>';
    }
    box.innerHTML = html;
  }

  function updateCalibrarPulses() {
    document.querySelectorAll('#calList .card[data-uid]').forEach(function (card) {
      var uid = card.getAttribute('data-uid');
      var live = state.liveByUid[uid];
      if (!live || !live.motors) return;
      card.querySelectorAll('.motor-cfg').forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = null;
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id || live.motors[k].Id || 0) === mi) { m = live.motors[k]; break; }
        if (!m) return;

        var pul   = pick(m, 'pulsos', 'Pulsos') || 0;
        var pwmA  = pick(m, 'pwm', 'Pwm') || 0;
        var calOn = !!pick(m, 'calibrando', 'Calibrando');
        var ppr   = parseInt((mc.querySelector('input[data-cal-f="ppr"]') || {}).value, 10) || 1;

        var pEl  = mc.querySelector('[data-cal="pulsos"]');     if (pEl)  pEl.textContent  = pul.toLocaleString();
        var pwEl = mc.querySelector('[data-cal="pwmCur"]');     if (pwEl) pwEl.textContent = pwmA + ' / 4095';

        var st = calState[uid] && calState[uid][mi];
        var dEl  = mc.querySelector('[data-cal="delta"]');
        var vEl  = mc.querySelector('[data-cal="vueltasReales"]');
        if (st && st.startPulsos != null) {
          var delta = pul - st.startPulsos;
          if (dEl) dEl.textContent = delta.toLocaleString();
          if (vEl) vEl.textContent = (ppr > 0 ? (delta / ppr).toFixed(2) : '—');
          // El firmware bajó CalibActive cuando llegó a la meta → grabamos endPulsos.
          if (!calOn && st.endPulsos == null && delta > 0) {
            st.endPulsos = pul;
            var msgEl = mc.querySelector('span[data-cal-msg="' + mi + '"]');
            if (msgEl) { msgEl.textContent = '✓ Meta alcanzada — pesá los surcos y apretá Calcular'; msgEl.className = 'send-msg ok'; }
          }
        } else {
          if (dEl) dEl.textContent = '—';
          if (vEl) vEl.textContent = '—';
        }
      });
    });
  }

  document.getElementById('tabCalibrar').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-cal-act]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(btn.getAttribute('data-mi'), 10);
    var act = btn.getAttribute('data-cal-act');
    var msgEl = mc.querySelector('span[data-cal-msg="' + mi + '"]');
    var resEl = mc.querySelector('span[data-cal-result="' + mi + '"]');

    if (!calState[uid]) calState[uid] = {};
    if (!calState[uid][mi]) calState[uid][mi] = {};
    var st = calState[uid][mi];

    var readInt = function (f, dflt) {
      var v = parseInt((mc.querySelector('input[data-cal-f="' + f + '"]') || {}).value, 10);
      return isNaN(v) ? dflt : v;
    };

    if (act === 'start') {
      var vueltas = readInt('vueltas', 10);
      var ppr     = readInt('ppr', 20);
      var pwm     = readInt('pwm', 2000);
      var meta    = vueltas * ppr;
      if (meta <= 0) { msgEl.textContent = '✕ vueltas/PPR inválidos'; msgEl.className = 'send-msg err'; return; }

      // Snapshot del contador actual antes de arrancar — Δ se calcula contra esto.
      var live = state.liveByUid[uid]; var pulNow = 0;
      if (live && live.motors) {
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id || live.motors[k].Id || 0) === mi)
            pulNow = pick(live.motors[k], 'pulsos', 'Pulsos') || 0;
      }
      st.startPulsos = pulNow; st.endPulsos = null;
      st.vueltas = vueltas; st.ppr = ppr; st.pwm = pwm; st.meta = meta;

      msgEl.textContent = '… girando hasta ' + meta + ' pulsos (' + vueltas + ' vueltas)';
      msgEl.className = 'send-msg';

      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cmd: 'start', id: mi, pulsos: meta, pwm: pwm })
        });
      } catch (e) {
        msgEl.textContent = '✕ no se pudo enviar MQTT'; msgEl.className = 'send-msg err';
      }
    } else if (act === 'stop') {
      // Stop manual: cierre del Δ con el pulso actual.
      var live2 = state.liveByUid[uid]; var pulEnd = 0;
      if (live2 && live2.motors) {
        for (var j = 0; j < live2.motors.length; j++)
          if ((live2.motors[j].id || live2.motors[j].Id || 0) === mi)
            pulEnd = pick(live2.motors[j], 'pulsos', 'Pulsos') || 0;
      }
      st.endPulsos = pulEnd;
      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cmd: 'stop', id: mi })
        });
      } catch (e) {}
      msgEl.textContent = '✓ Detenido. Pesá los surcos y apretá Calcular.'; msgEl.className = 'send-msg ok';
      updateCalibrarPulses();
    } else if (act === 'reset') {
      // Limpia los inputs por surco y el estado — el operario reintenta desde cero.
      calState[uid][mi] = {};
      mc.querySelectorAll('input[data-cal-surco]').forEach(function (inp) { inp.value = ''; });
      var box = mc.querySelector('[data-cal="resultBox"]'); if (box) box.hidden = true;
      var applyBtn = mc.querySelector('button[data-cal-act="apply"]'); if (applyBtn) applyBtn.disabled = true;
      if (msgEl) { msgEl.textContent = ''; msgEl.className = 'send-msg'; }
      if (resEl) { resEl.textContent = ''; resEl.className = 'send-msg'; }
      var deltaEl  = mc.querySelector('[data-cal="delta"]');         if (deltaEl)  deltaEl.textContent  = '—';
      var vueltasEl= mc.querySelector('[data-cal="vueltasReales"]'); if (vueltasEl)vueltasEl.textContent= '—';
      // Stop por las dudas que el motor todavía esté girando.
      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cmd: 'stop', id: mi })
        });
      } catch (e) {}
    } else if (act === 'calc') {
      // Promediamos los surcos cargados (ignoramos los vacíos / 0).
      var suma = 0, count = 0;
      mc.querySelectorAll('input[data-cal-surco]').forEach(function (inp) {
        var v = parseFloat(inp.value);
        if (!isNaN(v) && v > 0) { suma += v; count++; }
      });
      if (count === 0) { resEl.textContent = '✕ ingresá al menos un surco con valor > 0'; resEl.className = 'send-msg err'; return; }

      // Pulsos totales = Δ entre start y end. Si endPulsos no llegó (operario no
      // detuvo o no esperó la meta), usamos el último pulso conocido.
      var pulsosTot = 0;
      if (st.startPulsos != null) {
        var endP = st.endPulsos;
        if (endP == null) {
          // Tomá el pulso actual del live.
          var liveC = state.liveByUid[uid];
          if (liveC && liveC.motors) {
            for (var kk = 0; kk < liveC.motors.length; kk++)
              if ((liveC.motors[kk].id || liveC.motors[kk].Id || 0) === mi)
                endP = pick(liveC.motors[kk], 'pulsos', 'Pulsos') || 0;
          }
        }
        if (endP != null) pulsosTot = endP - st.startPulsos;
      }
      if (pulsosTot <= 0) {
        resEl.textContent = '✕ no hay pulsos contados. Apretá Iniciar primero.';
        resEl.className = 'send-msg err'; return;
      }

      var promedio = suma / count;
      var unidadesPorPulso = promedio / pulsosTot;
      var meterCal = pulsosTot / promedio;  // pulsos por unidad → lo que el bridge multiplica

      var promEl   = mc.querySelector('[data-cal="prom"]');   if (promEl)   promEl.textContent   = promedio.toFixed(2) + ' (' + count + ' surcos)';
      var uppEl    = mc.querySelector('[data-cal="upp"]');    if (uppEl)    uppEl.textContent    = unidadesPorPulso.toFixed(4);
      var newcalEl = mc.querySelector('[data-cal="newcal"]'); if (newcalEl) newcalEl.textContent = meterCal.toFixed(4);
      var box2 = mc.querySelector('[data-cal="resultBox"]'); if (box2) box2.hidden = false;

      // Cacheamos para el botón Guardar.
      st.meterCalCalc = meterCal;
      var applyBtn2 = mc.querySelector('button[data-cal-act="apply"]'); if (applyBtn2) applyBtn2.disabled = false;
      resEl.textContent = '✓ MeterCal = ' + meterCal.toFixed(4); resEl.className = 'send-msg ok';
    } else if (act === 'apply') {
      var newCal = st && st.meterCalCalc;
      if (!newCal || newCal <= 0) { resEl.textContent = '✕ apretá Calcular primero'; resEl.className = 'send-msg err'; return; }
      var nIdx = -1;
      for (var i = 0; i < state.motoresCfg.nodos.length; i++)
        if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
      if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
        state.motoresCfg.nodos[nIdx].motores[mi].meter_cal = Math.round(newCal * 10000) / 10000;
        // PPR también lo persistimos por si el operario lo ajustó.
        var pprCfg = readInt('ppr', 0);
        if (pprCfg > 0) state.motoresCfg.nodos[nIdx].motores[mi].dientes_engranaje = pprCfg;
        await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        var res = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
        var data = await res.json();
        resEl.textContent = data.ok ? '✓ MeterCal=' + newCal.toFixed(4) + ' guardado y enviado' : '✕ guardado pero MQTT falló';
        resEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
      }
    }
  });

  // ============================================================================
  // PRUEBA — girar X pulsos + buscador de PWM mínimo (rampa)
  // ============================================================================
  //
  // 1) Girar X pulsos: publica agp/quantix/{uid}/cal con
  //    {cmd:"start", id, pulsos, pwm}. Firmware corre el motor a PWM fijo
  //    hasta acumular `pulsos`. Útil para probar dosificación o sensor.
  //
  // 2) PWM mínimo: rampa client-side. Empezamos en pwm=0, sumamos `paso`
  //    cada `interval` ms, leemos pps_real del feed live. El primer PWM
  //    que produce pps_real >= umbral_hz es el pwm_min. Se guarda en
  //    motores.json y se reenvía la config al ESP32.

  var pruebaState = {};   // uid -> { mi: { rampActive, rampPwm, rampPaso, rampHzMin, rampTimer } }

  function renderPrueba() {
    var listEl = $('prList');
    var emptyEl = $('prEmpty');
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (nodos.length === 0) { listEl.innerHTML = ''; emptyEl.style.display = ''; return; }
    emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      html += '<div class="card" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<h3 style="margin-top:0">' + escapeHtml(n.nombre || 'Nodo') +
        ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' + escapeHtml(n.uid) + '</span></h3>' +
        '<div class="live-tune-grid">' +
          pruebaCard(n, 0) +
          pruebaCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
  }

  function pruebaCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +

      '<div class="kv" style="margin-top:0">' +
        '<div class="k">PPS real</div><div class="v" data-pr="pps_real">—</div>' +
        '<div class="k">PWM actual</div><div class="v" data-pr="pwm">—</div>' +
        '<div class="k">Pulsos</div><div class="v" data-pr="pulsos">—</div>' +
        '<div class="k">PWM min cfg</div><div class="v" data-pr="pwm_min_cfg">' + (m.pwm_min || 0) + '</div>' +
      '</div>' +

      '<h4 style="margin-top: var(--agp-sp-3)">Girar X pulsos</h4>' +
      '<div class="fld-grid">' +
        '<div class="field"><label>Pulsos meta</label>' +
          '<input type="number" data-pr-f="pulsos" min="1" step="1" value="500"></div>' +
        '<div class="field"><label>PWM</label>' +
          '<input type="number" data-pr-f="pwm" min="0" max="4095" step="50" value="2000"></div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-pr-act="spin-start" data-mi="' + mi + '">▶ Girar</button>' +
        '<button class="btn" data-pr-act="spin-stop" data-mi="' + mi + '">■ Detener</button>' +
        '<span class="send-msg" data-pr-msg="spin-' + mi + '"></span>' +
      '</div>' +

      '<h4 style="margin-top: var(--agp-sp-4)">Buscar PWM mínimo (rampa)</h4>' +
      '<div class="fld-grid">' +
        '<div class="field"><label>Paso PWM</label>' +
          '<input type="number" data-pr-f="step" min="10" max="500" step="10" value="50"></div>' +
        '<div class="field"><label>Intervalo (ms)</label>' +
          '<input type="number" data-pr-f="interval" min="200" max="3000" step="100" value="600"></div>' +
        '<div class="field"><label>Umbral Hz</label>' +
          '<input type="number" data-pr-f="hzmin" min="0.5" step="0.5" value="2"></div>' +
        '<div class="field"><label>PWM max</label>' +
          '<input type="number" data-pr-f="pwmmax" min="500" max="4095" step="50" value="4095"></div>' +
      '</div>' +
      '<div class="kv">' +
        '<div class="k">Rampa estado</div><div class="v" data-pr="ramp-state">idle</div>' +
        '<div class="k">PWM rampa</div><div class="v" data-pr="ramp-pwm">—</div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-pr-act="ramp-start" data-mi="' + mi + '">▶ Buscar PWM min</button>' +
        '<button class="btn" data-pr-act="ramp-stop" data-mi="' + mi + '">■ Cancelar</button>' +
        '<span class="send-msg" data-pr-msg="ramp-' + mi + '"></span>' +
      '</div>' +

      // Calibración avanzada: lo que antes vivía en la tab Motores. Se edita
      // acá porque son parámetros que se ajustan a fuerza de prueba/ramp,
      // no en seteo inicial del motor.
      '<h4 style="margin-top: var(--agp-sp-4)">Calibración avanzada</h4>' +
      '<div class="fld-grid">' +
        '<div class="field"><label>PWM min</label>' +
          '<input type="number" data-cal-f="pwm_min" min="0" max="4095" step="10" value="' + (m.pwm_min || 0) + '"></div>' +
        '<div class="field"><label>PWM max</label>' +
          '<input type="number" data-cal-f="pwm_max" min="0" max="4095" step="10" value="' + (m.pwm_max || 4095) + '"></div>' +
        '<div class="field"><label>Max Hz (FF)</label>' +
          '<input type="number" data-cal-f="max_hz" min="0" step="1" value="' + (m.max_hz || 0) + '"></div>' +
        '<div class="field"><label>FF gain</label>' +
          '<input type="number" data-cal-f="ff_gain" min="0" step="0.05" value="' + (m.ff_gain != null ? m.ff_gain : 1.0) + '"></div>' +
        '<div class="field"><label>Alpha</label>' +
          '<input type="number" data-cal-f="alpha" min="0" max="1" step="0.05" value="' + (m.alpha != null ? m.alpha : 0.4) + '"></div>' +
        '<div class="field"><label>PID time (ms)</label>' +
          '<input type="number" data-cal-f="pid_time" min="10" step="5" value="' + (m.pid_time || 50) + '"></div>' +
        '<div class="field"><label>Slew/s</label>' +
          '<input type="number" data-cal-f="slew_rate_per_sec" min="0" step="100" value="' + (m.slew_rate_per_sec || 0) + '"></div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-pr-act="cal-apply" data-mi="' + mi + '">Aplicar calibración</button>' +
        '<span class="send-msg" data-pr-msg="cal-' + mi + '"></span>' +
      '</div>' +

    '</div>';
  }

  function updatePruebaLive() {
    document.querySelectorAll('#prList .card[data-uid]').forEach(function (card) {
      var uid = card.getAttribute('data-uid');
      var live = state.liveByUid[uid];
      if (!live || !live.motors) return;
      card.querySelectorAll('.motor-cfg').forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = null;
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id || live.motors[k].Id || 0) === mi) { m = live.motors[k]; break; }
        if (!m) return;
        var pps = pick(m, 'ppsReal', 'PpsReal') || 0;
        var pwm = pick(m, 'pwm', 'Pwm') || 0;
        var pul = pick(m, 'pulsos', 'Pulsos') || 0;
        var p1 = mc.querySelector('[data-pr="pps_real"]'); if (p1) p1.textContent = pps.toFixed(1);
        var p2 = mc.querySelector('[data-pr="pwm"]'); if (p2) p2.textContent = pwm;
        var p3 = mc.querySelector('[data-pr="pulsos"]'); if (p3) p3.textContent = pul.toLocaleString();
      });
    });
  }

  async function sendTest(uid, mi, pwm) {
    var payload = pwm > 0
      ? JSON.stringify({ cmd: 'start', id: mi, pwm: pwm })
      : JSON.stringify({ cmd: 'stop',  id: mi, pwm: 0 });
    return fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=test&retain=false', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload
    });
  }

  async function sendCal(uid, mi, pulsos, pwm) {
    var payload = pulsos > 0
      ? JSON.stringify({ cmd: 'start', id: mi, pulsos: pulsos, pwm: pwm })
      : JSON.stringify({ cmd: 'stop',  id: mi });
    return fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload
    });
  }

  function getLiveMotor(uid, mi) {
    var live = state.liveByUid[uid];
    if (!live || !live.motors) return null;
    for (var k = 0; k < live.motors.length; k++)
      if ((live.motors[k].id || live.motors[k].Id || 0) === mi) return live.motors[k];
    return null;
  }

  function stopRamp(uid, mi, reason) {
    var st = pruebaState[uid] && pruebaState[uid][mi];
    if (!st) return;
    if (st.rampTimer) { clearInterval(st.rampTimer); st.rampTimer = null; }
    st.rampActive = false;
    // Detener motor.
    sendTest(uid, mi, 0).catch(function () {});
    var card = document.querySelector('#prList .card[data-uid="' + uid + '"]');
    if (card) {
      var mc = card.querySelector('.motor-cfg[data-mi="' + mi + '"]');
      if (mc) {
        var stEl = mc.querySelector('[data-pr="ramp-state"]');
        if (stEl) stEl.textContent = reason || 'idle';
      }
    }
  }

  async function startRamp(uid, mi, params) {
    if (!pruebaState[uid]) pruebaState[uid] = {};
    if (!pruebaState[uid][mi]) pruebaState[uid][mi] = {};
    var st = pruebaState[uid][mi];
    if (st.rampActive) return;

    var card = document.querySelector('#prList .card[data-uid="' + uid + '"]');
    var mc = card.querySelector('.motor-cfg[data-mi="' + mi + '"]');
    var msgEl = mc.querySelector('span[data-pr-msg="ramp-' + mi + '"]');
    var stEl = mc.querySelector('[data-pr="ramp-state"]');
    var pwmEl = mc.querySelector('[data-pr="ramp-pwm"]');
    msgEl.className = 'send-msg'; msgEl.textContent = '… midiendo';

    st.rampActive = true;
    st.rampPwm = 0;
    st.rampPaso = params.step;
    st.rampHzMin = params.hzmin;
    st.rampPwmMax = params.pwmmax;
    if (stEl) stEl.textContent = 'rampando';

    // Settle delay antes de cada paso (sin parar el motor entre pasos —
    // queremos rampa contínua, no escalonada con paradas).
    st.rampTimer = setInterval(async function () {
      if (!st.rampActive) return;
      st.rampPwm += st.rampPaso;
      if (st.rampPwm > st.rampPwmMax) {
        msgEl.textContent = '✕ No detectó pulsos hasta PWM ' + st.rampPwmMax;
        msgEl.className = 'send-msg err';
        stopRamp(uid, mi, 'sin pulsos');
        return;
      }
      if (pwmEl) pwmEl.textContent = st.rampPwm;
      try { await sendTest(uid, mi, st.rampPwm); } catch (e) {}
      // Leer Hz tras settle.
      setTimeout(async function () {
        if (!st.rampActive) return;
        var m = getLiveMotor(uid, mi);
        var pps = m ? (pick(m, 'ppsReal', 'PpsReal') || 0) : 0;
        if (pps >= st.rampHzMin) {
          // Encontrado.
          var found = st.rampPwm;
          stopRamp(uid, mi, 'encontrado');
          msgEl.textContent = '✓ PWM mínimo = ' + found + ' (Hz=' + pps.toFixed(1) + ')';
          msgEl.className = 'send-msg ok';
          // Persistir.
          var nIdx = -1;
          for (var i = 0; i < state.motoresCfg.nodos.length; i++)
            if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
          if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
            state.motoresCfg.nodos[nIdx].motores[mi].pwm_min = found;
            try {
              await fetch('/api/quantix/motores', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(state.motoresCfg)
              });
              await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
            } catch (e) {}
            var cfgEl = mc.querySelector('[data-pr="pwm_min_cfg"]');
            if (cfgEl) cfgEl.textContent = found;
          }
        }
      }, Math.max(150, params.interval - 50));
    }, params.interval);
  }

  document.getElementById('tabPrueba').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-pr-act]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(btn.getAttribute('data-mi'), 10);
    var act = btn.getAttribute('data-pr-act');

    if (act === 'spin-start') {
      var pulsos = parseInt(mc.querySelector('input[data-pr-f="pulsos"]').value, 10);
      var pwm = parseInt(mc.querySelector('input[data-pr-f="pwm"]').value, 10);
      var msgEl = mc.querySelector('span[data-pr-msg="spin-' + mi + '"]');
      msgEl.className = 'send-msg';
      if (!(pulsos > 0) || !(pwm > 0)) { msgEl.textContent = '✕ valores inválidos'; msgEl.className = 'send-msg err'; return; }
      msgEl.textContent = '… girando ' + pulsos + ' pulsos a PWM ' + pwm;
      try {
        var res = await sendCal(uid, mi, pulsos, pwm);
        var data = await res.json();
        msgEl.textContent = data.ok ? '✓ enviado' : '✕ ' + (data.error || 'fallo');
        msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
      } catch (e) {
        msgEl.textContent = '✕ ' + e.message; msgEl.className = 'send-msg err';
      }
    } else if (act === 'spin-stop') {
      var msgEl2 = mc.querySelector('span[data-pr-msg="spin-' + mi + '"]');
      msgEl2.className = 'send-msg';
      try { await sendCal(uid, mi, 0, 0); msgEl2.textContent = '✓ detenido'; msgEl2.className = 'send-msg ok'; }
      catch (e) { msgEl2.textContent = '✕ ' + e.message; msgEl2.className = 'send-msg err'; }
    } else if (act === 'ramp-start') {
      var step = parseInt(mc.querySelector('input[data-pr-f="step"]').value, 10) || 50;
      var interval = parseInt(mc.querySelector('input[data-pr-f="interval"]').value, 10) || 600;
      var hzmin = parseFloat(mc.querySelector('input[data-pr-f="hzmin"]').value) || 2;
      var pwmmax = parseInt(mc.querySelector('input[data-pr-f="pwmmax"]').value, 10) || 4095;
      startRamp(uid, mi, { step: step, interval: interval, hzmin: hzmin, pwmmax: pwmmax });
    } else if (act === 'ramp-stop') {
      stopRamp(uid, mi, 'cancelado');
      var msgEl3 = mc.querySelector('span[data-pr-msg="ramp-' + mi + '"]');
      msgEl3.className = 'send-msg'; msgEl3.textContent = '■ cancelado';
    } else if (act === 'cal-apply') {
      // Persistir calibración avanzada (PWM min/max, max_hz, ff_gain, alpha,
      // pid_time, slew) y publicar la config completa por MQTT.
      var msgCal = mc.querySelector('span[data-pr-msg="cal-' + mi + '"]');
      msgCal.textContent = '… enviando'; msgCal.className = 'send-msg';
      var nIdx2 = -1;
      for (var j = 0; j < state.motoresCfg.nodos.length; j++) {
        if (state.motoresCfg.nodos[j].uid === uid) { nIdx2 = j; break; }
      }
      if (nIdx2 < 0 || !state.motoresCfg.nodos[nIdx2].motores[mi]) {
        msgCal.textContent = '✕ motor no encontrado'; msgCal.className = 'send-msg err'; return;
      }
      var mref2 = state.motoresCfg.nodos[nIdx2].motores[mi];
      mref2.pwm_min = parseInt(mc.querySelector('input[data-cal-f="pwm_min"]').value, 10) || 0;
      mref2.pwm_max = parseInt(mc.querySelector('input[data-cal-f="pwm_max"]').value, 10) || 4095;
      mref2.max_hz = parseFloat(mc.querySelector('input[data-cal-f="max_hz"]').value) || 0;
      mref2.ff_gain = parseFloat(mc.querySelector('input[data-cal-f="ff_gain"]').value);
      if (isNaN(mref2.ff_gain)) mref2.ff_gain = 1.0;
      mref2.alpha = parseFloat(mc.querySelector('input[data-cal-f="alpha"]').value);
      if (isNaN(mref2.alpha)) mref2.alpha = 0.4;
      mref2.pid_time = parseInt(mc.querySelector('input[data-cal-f="pid_time"]').value, 10) || 50;
      mref2.slew_rate_per_sec = parseInt(mc.querySelector('input[data-cal-f="slew_rate_per_sec"]').value, 10) || 0;
      try {
        var pr1 = await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        var d1 = await pr1.json();
        if (!d1.ok) { msgCal.textContent = '✕ ' + (d1.error || 'guardar falló'); msgCal.className = 'send-msg err'; return; }
        var pr2 = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
        var d2 = await pr2.json();
        msgCal.textContent = d2.ok ? '✓ aplicado' : '✕ ' + (d2.error || 'send falló');
        msgCal.className = 'send-msg ' + (d2.ok ? 'ok' : 'err');
      } catch (e) {
        msgCal.textContent = '✕ ' + e.message; msgCal.className = 'send-msg err';
      }
    }
  });

  // ============================================================================
  // SHAPE — upload de .shp/.shx/.dbf y carga en PilotX vía POST /api/aog/shape
  // ============================================================================

  var SHAPE_REQ = ['.shp', '.shx', '.dbf'];
  var SHAPE_OPT = ['.prj', '.cpg'];
  var shapeSelected = []; // [{name, ext, bytes (Uint8Array)}]

  function $sf(id) { return document.getElementById(id); }

  function shapeExtOf(name) {
    var i = name.lastIndexOf('.');
    return i < 0 ? '' : name.substring(i).toLowerCase();
  }

  function fmtBytes(n) {
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(2) + ' MB';
  }

  function shapeRenderFileList() {
    var box = $sf('shapeFileList');
    if (!box) return;
    if (shapeSelected.length === 0) { box.innerHTML = ''; updateShapeUploadEnabled(); return; }

    // Verificar requeridos
    var have = {};
    shapeSelected.forEach(function (f) { have[f.ext] = true; });

    var html = shapeSelected.map(function (f) {
      var isReq = SHAPE_REQ.indexOf(f.ext) >= 0;
      return '<div class="shape-file-row">' +
        '<span class="ext' + (isReq ? '' : ' opt') + '">' + f.ext + '</span>' +
        '<span class="nm">' + f.name + '</span>' +
        '<span class="sz">' + fmtBytes(f.bytes.length) + '</span>' +
        '</div>';
    }).join('');

    // Chips de estado de requeridos
    var chips = SHAPE_REQ.map(function (ext) {
      var cls = have[ext] ? 'ok' : 'missing';
      return '<span class="chip ' + cls + '">' + ext + (have[ext] ? ' ✓' : ' falta') + '</span>';
    }).join('') + SHAPE_OPT.map(function (ext) {
      var cls = have[ext] ? 'ok' : '';
      return '<span class="chip ' + cls + '">' + ext + (have[ext] ? ' ✓' : '') + '</span>';
    }).join('');

    box.innerHTML = html + '<div class="shape-required-grid">' + chips + '</div>';
    updateShapeUploadEnabled();
  }

  function updateShapeUploadEnabled() {
    var btn = $sf('btnShapeUpload');
    if (!btn) return;
    var have = {};
    shapeSelected.forEach(function (f) { have[f.ext] = true; });
    btn.disabled = !(have['.shp'] && have['.shx'] && have['.dbf']);
  }

  function shapeAddFiles(fileList) {
    var arr = Array.prototype.slice.call(fileList);
    var promises = arr.map(function (f) {
      return new Promise(function (resolve) {
        var ext = shapeExtOf(f.name);
        if (SHAPE_REQ.indexOf(ext) < 0 && SHAPE_OPT.indexOf(ext) < 0) {
          // Ignorar extensiones no aceptadas (no rompemos el flujo)
          resolve(null); return;
        }
        var rd = new FileReader();
        rd.onload = function () {
          resolve({ name: f.name, ext: ext, bytes: new Uint8Array(rd.result) });
        };
        rd.onerror = function () { resolve(null); };
        rd.readAsArrayBuffer(f);
      });
    });
    Promise.all(promises).then(function (results) {
      results.forEach(function (item) {
        if (!item) return;
        // Reemplazo por extensión: si ya hay .shp y se elige otra .shp nueva, gana la nueva.
        shapeSelected = shapeSelected.filter(function (x) { return x.ext !== item.ext; });
        shapeSelected.push(item);
      });
      shapeRenderFileList();
    });
  }

  function shapeBytesToBase64(uint8) {
    var CHUNK = 0x8000;
    var s = '';
    for (var i = 0; i < uint8.length; i += CHUNK) {
      s += String.fromCharCode.apply(null, uint8.subarray(i, i + CHUNK));
    }
    return btoa(s);
  }

  async function refreshShapeActive() {
    var box = $sf('shapeActive');
    if (!box) return;
    try {
      var r = await fetch('/api/aog/shape-fields', { cache: 'no-store' });
      var d = await r.json();
      if (!d || !d.ok || !d.sourceToken) {
        box.innerHTML = '<div class="subtitle">No hay shapefile activo en este lote.</div>';
        return;
      }
      var fields = Array.isArray(d.fields) ? d.fields : [];
      var fieldsHtml = fields.length === 0
        ? '<span style="color:var(--agp-text-muted)">— sin columnas DBF —</span>'
        : fields.map(function (f) {
            var nm = (typeof f === 'string') ? f : (f && f.name);
            return '<span class="chip ok" style="margin:2px">' + nm + '</span>';
          }).join('');
      box.innerHTML =
        '<div style="display:grid;grid-template-columns:auto 1fr;gap:var(--agp-sp-2) var(--agp-sp-3);align-items:baseline">' +
          '<div class="lbl" style="font-size:var(--agp-fs-xs);color:var(--agp-text-muted);text-transform:uppercase">Archivo</div>' +
          '<div style="font-family:var(--agp-font-mono)">' + d.sourceToken + '</div>' +
          '<div class="lbl" style="font-size:var(--agp-fs-xs);color:var(--agp-text-muted);text-transform:uppercase">Columnas DBF</div>' +
          '<div class="shape-required-grid" style="margin:0">' + fieldsHtml + '</div>' +
        '</div>';
    } catch (e) {
      box.innerHTML = '<div class="subtitle">Error consultando PilotX: ' + e.message + '</div>';
    }
  }

  function shapeSetMsg(state, text) {
    var el = $sf('shapeMsg');
    if (!el) return;
    el.className = 'send-msg ' + (state || '');
    el.textContent = text || '';
  }

  async function shapeUpload() {
    if (shapeSelected.length === 0) return;
    shapeSetMsg('', 'Subiendo y cargando en PilotX…');
    var btn = $sf('btnShapeUpload');
    if (btn) btn.disabled = true;
    try {
      var payload = {
        files: shapeSelected.map(function (f) {
          return { name: f.name, b64: shapeBytesToBase64(f.bytes) };
        })
      };
      var r = await fetch('/api/aog/shape', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      var d = await r.json();
      if (d && d.ok) {
        shapeSetMsg('ok', '✓ Cargado · ' + (d.polygonCount || 0) + ' polígonos');
        shapeSelected = [];
        shapeRenderFileList();
        var inp = $sf('shapeFiles'); if (inp) inp.value = '';
        // Invalidar cache de campos para que la tab Motores reciba las nuevas columnas
        state.shapeSource = '';
        await loadShapeFields();
        refreshShapeActive();
      } else {
        shapeSetMsg('err', '✕ ' + (d && d.error ? d.error : 'Error desconocido'));
      }
    } catch (e) {
      shapeSetMsg('err', '✕ ' + e.message);
    } finally {
      updateShapeUploadEnabled();
    }
  }

  async function shapeRemove() {
    if (!confirm('¿Quitar la capa de shapefile activa del lote?')) return;
    shapeSetMsg('', 'Quitando…');
    try {
      var r = await fetch('/api/aog/shape', { method: 'DELETE' });
      var d = await r.json();
      if (d && d.ok) {
        shapeSetMsg('ok', '✓ Capa quitada');
        state.shapeSource = '';
        await loadShapeFields();
        refreshShapeActive();
      } else {
        shapeSetMsg('err', '✕ ' + (d && d.error ? d.error : 'no se pudo quitar'));
      }
    } catch (e) {
      shapeSetMsg('err', '✕ ' + e.message);
    }
  }

  (function bindShapeUI() {
    var drop = $sf('shapeDrop');
    var inp  = $sf('shapeFiles');
    if (!drop || !inp) return;
    // Pantalla táctil: tap en la zona abre el file picker.
    // Filtramos el click que viene desde el propio <input> para no entrar en loop.
    drop.addEventListener('click', function (e) {
      if (e.target === inp) return;
      inp.click();
    });
    inp.addEventListener('change', function () { if (inp.files && inp.files.length) shapeAddFiles(inp.files); });
    ['dragenter', 'dragover'].forEach(function (ev) {
      drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.add('over'); });
    });
    ['dragleave', 'drop'].forEach(function (ev) {
      drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.remove('over'); });
    });
    drop.addEventListener('drop', function (e) {
      if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) {
        shapeAddFiles(e.dataTransfer.files);
      }
    });
    var bu = $sf('btnShapeUpload'); if (bu) bu.addEventListener('click', shapeUpload);
    var br = $sf('btnShapeRemove'); if (br) br.addEventListener('click', shapeRemove);
  })();

  // ============================================================================
  // Init
  // ============================================================================

  loadMotores();
  // Cargamos columnas DBF y secciones del tool en background — el primer
  // render de la tab Motores ya las encuentra en cache si el usuario clickea
  // rápido. Si no, showTab('motores') las refresca igual antes de pintar.
  loadShapeFields();
  loadAogSections();

  // Polling adaptativo:
  //   · tabs "live" (monitor/pid/calibrar/prueba) → 500ms (tiempo real)
  //   · tabs "config" (motores/shape) → 2000ms (solo refresca pill de "X nodos")
  //   · pestaña del WebView no visible → pausa total
  // Bajamos la presión sobre el WebHost cuando el operario está configurando
  // sin perder el feel real-time cuando mira telemetría.
  var LIVE_TABS = { siembra: 1, pid: 1, calibrar: 1, prueba: 1 };
  var pollTimer = null;
  function schedulePoll() {
    if (pollTimer) clearTimeout(pollTimer);
    if (document.hidden) { pollTimer = null; return; }
    var period = LIVE_TABS[state.activeTab] ? 500 : 2000;
    pollTimer = setTimeout(function () { pollLive().finally(schedulePoll); }, period);
  }
  // pollLive es async; envolvemos por compatibilidad con .finally().
  (async function bootPoll() {
    try { await pollLive(); } catch (_) {}
    schedulePoll();
  })();
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) {
      if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
    } else {
      schedulePoll();
    }
  });
})();
