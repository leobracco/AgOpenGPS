// ============================================================================
// quantix.js — Monitor en vivo de nodos QuantiX (per motor).
// Trae /api/quantix/live (que filtra los nodos QuantiX del registry MQTT) y
// renderiza una card por motor con PPS objetivo/real, PWM, RPM y estado.
// ============================================================================

(function () {
  'use strict';

  var listEl   = document.getElementById('qxList');
  var emptyEl  = document.getElementById('qxEmpty');
  var statusEl = document.getElementById('qxStatus');

  // Edad máxima de un sample de status_live para considerarlo "fresco".
  var FRESH_MS = 3000;

  function pick(o, a, b) { return o[a] != null ? o[a] : o[b]; }

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

    return '' +
      '<div class="card motor-card">' +
        '<div class="node-head">' +
          '<h3 style="margin:0">QuantiX node · Motor ' + (m.id != null ? m.id + 1 : m.Id + 1) + '</h3>' +
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

  function renderNodo(n) {
    var uid = pick(n, 'uid', 'Uid') || '';
    var ip  = pick(n, 'ip',  'Ip')  || '—';
    var fw  = pick(n, 'firmware', 'Firmware') || '—';
    var online = !!pick(n, 'online', 'Online');
    var motors = pick(n, 'motorsLive', 'MotorsLive') || [];
    if (!motors.length) {
      // Mostrar igual el nodo aunque no haya telemetría todavía
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

  async function poll() {
    try {
      var res = await fetch('/api/quantix/live', { cache: 'no-store' });
      var data = await res.json();
      var nodos = pick(data, 'nodos', 'Nodos') || [];
      if (statusEl) {
        statusEl.className = 'pill ' + (nodos.length > 0 ? 'ok' : 'warn');
        statusEl.innerHTML = '<span class="dot"></span> ' + nodos.length + ' nodo' + (nodos.length !== 1 ? 's' : '') + ' QuantiX';
      }
      if (nodos.length === 0) {
        if (listEl)  listEl.innerHTML  = '';
        if (emptyEl) emptyEl.style.display = '';
        return;
      }
      if (emptyEl) emptyEl.style.display = 'none';
      if (listEl)  listEl.innerHTML = nodos.map(renderNodo).join('');
    } catch (e) {
      if (statusEl) {
        statusEl.className = 'pill err';
        statusEl.innerHTML = '<span class="dot"></span> Sin conexión';
      }
    }
  }

  poll();
  setInterval(poll, 500); // 2 Hz, suficiente para humano viendo
})();
