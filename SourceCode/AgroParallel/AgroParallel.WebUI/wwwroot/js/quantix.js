// ============================================================================
// quantix.js — UI completa del módulo QuantiX.
// Tabs:
//   Monitor    → /api/quantix/live (2 Hz)
//   Motores    → /api/quantix/motores GET/PUT + POST /{uid}/send
//   PID live   → POST /{uid}/cmd?verb=config con {configs:[...]} para tune en vivo
//   Calibrar   → POST /{uid}/cmd?verb=calibrar para start/stop, lee pulsos de live
//   Config UDP → /api/quantix/udp GET/PUT
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

  // ---------- State ----------

  var state = {
    activeTab: 'monitor',
    motoresCfg: { nodos: [], ignorados: [] },
    liveByUid: {}
  };

  // ---------- Tabs ----------

  function showTab(name) {
    state.activeTab = name;
    document.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.getAttribute('data-tab') === name);
    });
    ['Monitor', 'Motores', 'Pid', 'Calibrar', 'Udp'].forEach(function (k) {
      var el = $('tab' + k);
      if (el) el.style.display = (k.toLowerCase() === name) ? '' : 'none';
    });
    if (name === 'motores')  renderMotores();
    if (name === 'pid')      renderPid();
    if (name === 'calibrar') renderCalibrar();
    if (name === 'udp')      loadUdp();
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
    return '' +
      '<div class="card motor-card">' +
        '<div class="node-head">' +
          '<h3 style="margin:0">QuantiX node · Motor ' + ((mid|0) + 1) + '</h3>' +
          '<span class="uid">' + escapeHtml(uid) + '</span>' +
        '</div>' +
        '<div class="pps ' + cls + '">' + real.toFixed(1) + '</div>' +
        '<div class="target">PPS real · objetivo <strong>' + target.toFixed(1) + '</strong> ' +
          '(<span class="' + cls + '">Δ ' + deltaStr + '</span>)</div>' +
        '<div class="pid-bar" title="PWM ' + pct + '%">' +
          '<div class="fill" style="width:' + pct + '%"></div>' +
        '</div>' +
        '<div class="kv">' +
          '<div class="k">PWM</div><div class="v">' + pwm + ' / 4095</div>' +
          '<div class="k">RPM</div><div class="v">' + rpm + '</div>' +
          '<div class="k">Pulsos</div><div class="v">' + pulsos.toLocaleString() + '</div>' +
          '<div class="k">Visto</div><div class="v">' + (stale ? '⚠ ' : '') + (Math.round(ageMs(seen)/100)/10) + ' s</div>' +
        '</div>' +
        '<div class="row" style="margin-top: var(--agp-sp-3)">' +
          '<span></span>' + pidLabel +
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
  }

  function defaultMotor(nombre) {
    return {
      nombre: nombre || 'Motor', dosis_fija: 0, campo_dosis: '',
      kp: 80, ki: 30, kd: 0, pwm_min: 600, pwm_max: 4095, meter_cal: 50,
      max_integral: 1200, deadband: 2, slew_rate: 40, dientes_engranaje: 20,
      motor_type: 0, max_hz: 40, ff_gain: 1.0, alpha: 0.4,
      slew_rate_per_sec: 5000, pid_time: 50, cortes: [], tren: 0
    };
  }

  function renderMotorCfg(nodoIdx, motorIdx) {
    var m = state.motoresCfg.nodos[nodoIdx].motores[motorIdx] || defaultMotor();
    var cortesStr = (m.cortes || []).join(',');
    var mClass = motorIdx === 0 ? '' : 'm1';
    return '' +
      '<div class="motor-cfg ' + mClass + '" data-mi="' + motorIdx + '">' +
        '<h4>M' + motorIdx + ' — <input type="text" data-f="nombre" value="' + escapeHtml(m.nombre || '') + '" style="font-weight:bold; width:auto; min-width:160px"></h4>' +
        '<div class="fld-grid">' +
          fld('Dosis fija (0=mapa)', 'dosis_fija', m.dosis_fija, 'number', '0', '0.1') +
          fld('Campo shapefile',     'campo_dosis', m.campo_dosis, 'text') +
          fld('MeterCal',            'meter_cal',   m.meter_cal,   'number', '0.1', '0.5') +
          fld('Kp',                  'kp',          m.kp,          'number', '0',   '0.1') +
          fld('Ki',                  'ki',          m.ki,          'number', '0',   '0.1') +
          fld('Kd',                  'kd',          m.kd,          'number', '0',   '0.01') +
          fld('PWM min',             'pwm_min',     m.pwm_min,     'number', '0',   '10') +
          fld('PWM max',             'pwm_max',     m.pwm_max,     'number', '0',   '10') +
          fld('Max Hz (FF)',         'max_hz',      m.max_hz,      'number', '0',   '1') +
          fld('FF gain',             'ff_gain',     m.ff_gain,     'number', '0',   '0.05') +
          fld('Alpha',               'alpha',       m.alpha,       'number', '0',   '0.05') +
          fld('PID time (ms)',       'pid_time',    m.pid_time,    'number', '10',  '5') +
          fld('Slew/s',              'slew_rate_per_sec', m.slew_rate_per_sec, 'number', '0', '100') +
          fld('Dientes',             'dientes_engranaje', m.dientes_engranaje, 'number', '1', '1') +
          fld('Tipo motor',          'motor_type',  m.motor_type,  'number', '0', '1') +
          fld('Tren (0=del/1=tras)', 'tren',        m.tren,        'number', '0', '1') +
        '</div>' +
        '<div class="cortes-row">' +
          '<label style="color: var(--agp-text-muted); font-size: var(--agp-fs-xs)">Cortes AOG (ej: 1,2,3):</label>' +
          '<input type="text" data-f="cortes" value="' + escapeHtml(cortesStr) + '">' +
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

  function renderMotores() {
    var listEl = $('mtList');
    var cfg = state.motoresCfg;
    if (!cfg.nodos || cfg.nodos.length === 0) {
      listEl.innerHTML = '<div class="card subtitle">No hay nodos QuantiX configurados. ' +
        'Cuando un nodo nuevo aparezca por MQTT (<code>agp/quantix/+/announcement</code>) se auto-registra al primer arranque.</div>';
      return;
    }
    var html = '';
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
  }

  function readMotoresFromUI() {
    var cfg = state.motoresCfg;
    var cards = document.querySelectorAll('#mtList > .card');
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
        mc.querySelectorAll('input[data-f]').forEach(function (inp) {
          var f = inp.getAttribute('data-f');
          if (f === 'cortes') {
            m.cortes = (inp.value || '').split(',').map(function (s) { return parseInt(s.trim(), 10); })
              .filter(function (v) { return !isNaN(v) && v > 0; });
          } else if (inp.type === 'number') {
            m[f] = parseFloat(inp.value) || 0;
          } else {
            m[f] = inp.value || '';
          }
        });
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

  function addNodo() {
    readMotoresFromUI();
    var n = state.motoresCfg.nodos.length + 1;
    var baseCorte = (n - 1) * 7 + 1;
    var cortes = []; for (var i = 0; i < 7; i++) cortes.push(baseCorte + i);
    state.motoresCfg.nodos.push({
      uid: 'QX-' + Math.random().toString(16).substr(2, 8).toUpperCase(),
      nombre: 'Nodo ' + n, habilitado: true, distancia_entre_trenes: 0,
      motores: [
        Object.assign(defaultMotor('Producto 1'), { cortes: cortes }),
        defaultMotor('Producto 2')
      ]
    });
    renderMotores();
  }

  function delNodo(idx) {
    if (!confirm('¿Quitar nodo ' + (state.motoresCfg.nodos[idx].uid || '?') + '?')) return;
    var uid = state.motoresCfg.nodos[idx].uid;
    if (uid && state.motoresCfg.ignorados.indexOf(uid) < 0) state.motoresCfg.ignorados.push(uid);
    state.motoresCfg.nodos.splice(idx, 1);
    saveMotores();
    renderMotores();
  }

  $('mtList').addEventListener('click', function (ev) {
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
  $('btnAddNodo').addEventListener('click', addNodo);
  $('btnSaveMotores').addEventListener('click', saveMotores);
  $('btnSendAll').addEventListener('click', sendAllNodos);

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
  }

  function pidTuneCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +
      '<div class="kv" style="margin-top:0">' +
        '<div class="k">PPS real</div><div class="v" data-live="pps_real">—</div>' +
        '<div class="k">PPS target</div><div class="v" data-live="pps_target">—</div>' +
        '<div class="k">PWM</div><div class="v" data-live="pwm">—</div>' +
      '</div>' +
      '<div class="fld-grid" style="margin-top: var(--agp-sp-3)">' +
        '<div class="field"><label>Kp <span data-show="kp">' + m.kp + '</span></label>' +
          '<input type="range" class="slider" data-tune="kp" min="0" max="300" step="1" value="' + m.kp + '"></div>' +
        '<div class="field"><label>Ki <span data-show="ki">' + m.ki + '</span></label>' +
          '<input type="range" class="slider" data-tune="ki" min="0" max="200" step="1" value="' + m.ki + '"></div>' +
        '<div class="field"><label>Kd <span data-show="kd">' + m.kd + '</span></label>' +
          '<input type="range" class="slider" data-tune="kd" min="0" max="50" step="0.1" value="' + m.kd + '"></div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn" data-tune-act="push" data-mi="' + mi + '">Aplicar</button>' +
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
    var sh = s.parentElement.querySelector('span[data-show]');
    if (sh) sh.textContent = s.value;
  });

  document.getElementById('tabPid').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-tune-act="push"]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(mc.getAttribute('data-mi'), 10);
    var kp = parseFloat(mc.querySelector('input[data-tune="kp"]').value);
    var ki = parseFloat(mc.querySelector('input[data-tune="ki"]').value);
    var kd = parseFloat(mc.querySelector('input[data-tune="kd"]').value);
    var msgEl = mc.querySelector('span[data-tune-msg="' + mi + '"]');
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
  });

  // ============================================================================
  // CALIBRACIÓN — pulse counting
  // ============================================================================

  var calState = {};  // uid -> { mi: { startPulsos, target, done } }

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
          'Conectá un recipiente para recolectar el producto. Apretá <strong>Iniciar</strong>, dejá que el motor gire ' +
          'hasta sacar una cantidad conocida (ej: 1 kg). Apretá <strong>Detener</strong>. El sistema calcula ' +
          '<code>MeterCal = pulsos / cantidad</code>.' +
        '</p>' +
        '<div class="live-tune-grid">' +
          calCard(n, 0) +
          calCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
  }

  function calCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +
      '<div class="kv" style="margin-top:0">' +
        '<div class="k">Pulsos actuales</div><div class="v" data-cal="pulsos">—</div>' +
        '<div class="k">Δ pulsos</div><div class="v" data-cal="delta">—</div>' +
        '<div class="k">MeterCal actual</div><div class="v">' + (m.meter_cal || 0) + '</div>' +
      '</div>' +
      '<div class="fld-grid" style="margin-top: var(--agp-sp-3)">' +
        '<div class="field"><label>Cantidad obtenida (kg o L)</label>' +
          '<input type="number" data-cal-f="qty" min="0.01" step="0.01" value="1"></div>' +
        '<div class="field"><label>Nuevo MeterCal</label>' +
          '<input type="number" data-cal-f="newcal" step="0.01" value="—" readonly></div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-cal-act="start" data-mi="' + mi + '">▶ Iniciar</button>' +
        '<button class="btn" data-cal-act="stop" data-mi="' + mi + '">■ Detener</button>' +
        '<button class="btn" data-cal-act="apply" data-mi="' + mi + '">✓ Guardar MeterCal</button>' +
        '<span class="send-msg" data-cal-msg="' + mi + '"></span>' +
      '</div>' +
    '</div>';
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
        var pul = pick(m, 'pulsos', 'Pulsos') || 0;
        var pEl = mc.querySelector('[data-cal="pulsos"]');
        if (pEl) pEl.textContent = pul.toLocaleString();

        var st = calState[uid] && calState[uid][mi];
        var dEl = mc.querySelector('[data-cal="delta"]');
        if (dEl) dEl.textContent = st && st.startPulsos != null ? (pul - st.startPulsos).toLocaleString() : '—';

        if (st && st.startPulsos != null && st.endPulsos != null) {
          var qty = parseFloat(mc.querySelector('input[data-cal-f="qty"]').value) || 0;
          if (qty > 0) {
            var newCal = (st.endPulsos - st.startPulsos) / qty;
            var ncEl = mc.querySelector('input[data-cal-f="newcal"]');
            if (ncEl) ncEl.value = newCal.toFixed(2);
          }
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
    msgEl.className = 'send-msg';

    if (!calState[uid]) calState[uid] = {};
    if (!calState[uid][mi]) calState[uid][mi] = {};

    if (act === 'start') {
      var live = state.liveByUid[uid];
      var pulNow = 0;
      if (live && live.motors) {
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id || live.motors[k].Id || 0) === mi)
            pulNow = pick(live.motors[k], 'pulsos', 'Pulsos') || 0;
      }
      calState[uid][mi].startPulsos = pulNow;
      calState[uid][mi].endPulsos = null;
      msgEl.textContent = '… girá el motor manualmente y luego apretá Detener'; msgEl.className = 'send-msg';

      // Opcional: pedirle al firmware que arranque el motor en PWM medio
      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ idx: mi, action: 'start' })
        });
      } catch (e) { /* no fatal: el operador puede mover el motor a mano */ }
    } else if (act === 'stop') {
      var live2 = state.liveByUid[uid];
      var pulEnd = 0;
      if (live2 && live2.motors) {
        for (var j = 0; j < live2.motors.length; j++)
          if ((live2.motors[j].id || live2.motors[j].Id || 0) === mi)
            pulEnd = pick(live2.motors[j], 'pulsos', 'Pulsos') || 0;
      }
      calState[uid][mi].endPulsos = pulEnd;
      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ idx: mi, action: 'stop' })
        });
      } catch (e) {}
      msgEl.textContent = '✓ Detenido. Ajustá la cantidad y guardá MeterCal.'; msgEl.className = 'send-msg ok';
      updateCalibrarPulses();
    } else if (act === 'apply') {
      var ncEl = mc.querySelector('input[data-cal-f="newcal"]');
      var newCal = parseFloat(ncEl.value);
      if (isNaN(newCal) || newCal <= 0) { msgEl.textContent = '✕ valor inválido'; msgEl.className = 'send-msg err'; return; }
      var nIdx = -1;
      for (var i = 0; i < state.motoresCfg.nodos.length; i++)
        if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
      if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
        state.motoresCfg.nodos[nIdx].motores[mi].meter_cal = newCal;
        await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        var res = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
        var data = await res.json();
        msgEl.textContent = data.ok ? '✓ MeterCal=' + newCal + ' guardado y enviado' : '✕ guardado pero MQTT falló';
        msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
      }
    }
  });

  // ============================================================================
  // CONFIG UDP — quantiX.json
  // ============================================================================

  async function loadUdp() {
    try {
      var res = await fetch('/api/quantix/udp', { cache: 'no-store' });
      var data = await res.json();
      var c = (data && data.config) || {};
      $('udpEnabled').checked = !!c.enabled;
      $('udpHost').value = c.udpHost || '127.0.0.1';
      $('udpPort').value = c.udpPort || 17770;
      $('udpRate').value = c.sampleRateHz || 5;
      $('udpOutside').value = c.outsideValue || 0;
      $('udpUnit').value = c.doseUnit || '';
      $('udpOnlyChange').checked = !!c.sendOnlyOnChange;
      $('udpIncludePos').checked = c.includePosition !== false;
    } catch (e) { /* ignore */ }
  }

  $('btnSaveUdp').addEventListener('click', async function () {
    var cfg = {
      enabled: $('udpEnabled').checked,
      udpHost: $('udpHost').value,
      udpPort: parseInt($('udpPort').value, 10) || 17770,
      sampleRateHz: parseFloat($('udpRate').value) || 5,
      outsideValue: parseFloat($('udpOutside').value) || 0,
      sendOnlyOnChange: $('udpOnlyChange').checked,
      includePosition: $('udpIncludePos').checked,
      doseUnit: $('udpUnit').value || ''
    };
    var msg = $('udpMsg'); msg.textContent = 'Guardando…'; msg.className = 'send-msg';
    try {
      var res = await fetch('/api/quantix/udp', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var data = await res.json();
      msg.textContent = data.ok ? '✓ Guardado.' : '✕ ' + (data.error || 'error');
      msg.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
    } catch (e) { msg.textContent = '✕ ' + e.message; msg.className = 'send-msg err'; }
  });

  // ============================================================================
  // Init
  // ============================================================================

  loadMotores();
  pollLive();
  setInterval(pollLive, 500);
})();
