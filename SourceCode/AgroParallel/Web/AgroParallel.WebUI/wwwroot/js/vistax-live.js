// ============================================================================
// vistax-live.js — franja MONITOR de VistaX para la barra inferior de PilotX.
// Una sola fila de chips: un chip por surco primario (semilla/fertilizante).
// El operario lo usa como vista de "todo el ancho de la sembradora" para
// detectar surcos tapados al toque sin entrar al Hub.
// Polleo /api/vistax/live a 2 Hz. Pausa con visibilitychange.
// ============================================================================
(function () {
  'use strict';

  var POLL_MS = 500;
  var PRIMARIOS = { 'semilla': 1, 'fertilizante': 1 };

  function $(id) { return document.getElementById(id); }
  function pick(o, a, b) { return (o && o[a] != null) ? o[a] : (o ? o[b] : undefined); }
  function fmt(n, d) {
    if (n == null || isNaN(n)) return '—';
    return Number(n).toFixed(d == null ? 1 : d);
  }
  function tipoOf(s) {
    var t = pick(s, 'Tipo', 'tipo');
    return (t || '').toLowerCase();
  }
  function classFromEstado(st) {
    st = (st || 'no-data').toLowerCase();
    if (st === 'ok')     return 's-ok';
    if (st === 'bajo')   return 's-bajo';
    if (st === 'tapado') return 's-tapado';
    if (st === 'exceso') return 's-exceso';
    if (st === 'muted')  return 's-muted';
    return 's-no-data';
  }

  var state = { paused: false, timer: null };

  function renderChip(s) {
    var bajada = pick(s, 'Bajada', 'bajada') || 0;
    var estado = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
    var muted  = pick(s, 'Muted', 'muted');
    var cls    = muted ? 's-muted' : classFromEstado(estado);
    var tipo   = tipoOf(s);
    var tag    = '';
    if (tipo === 'semilla')      tag = '<span class="tipo">S</span>';
    else if (tipo === 'fertilizante') tag = '<span class="tipo">F</span>';
    var title = 'surco ' + bajada + ' · ' + (tipo || '?') + ' · ' + estado;
    return '<div class="vx-chip ' + cls + '" title="' + title + '">' +
             '<span class="num">' + bajada + '</span>' + tag +
           '</div>';
  }

  function render(live) {
    if (!live) return;
    var trenes   = pick(live, 'Trenes', 'trenes') || [];
    var spm      = pick(live, 'SpmPromedio', 'spmPromedio');
    var fallas   = pick(live, 'FallasActivas', 'fallasActivas') || 0;
    var vel      = pick(live, 'Velocidad', 'velocidad');
    var hasAlarm = pick(live, 'HasAlarm', 'hasAlarm');
    var monAct   = pick(live, 'MonitoreoActivo', 'monitoreoActivo');

    $('vxSpm').textContent    = (spm == null) ? '—' : fmt(spm, 0);
    $('vxFallas').textContent = fallas;
    $('vxVel').textContent    = (vel == null) ? '—' : fmt(vel, 1);

    var pill = $('vxPill'), txt = $('vxPillTxt');
    pill.classList.remove('warn', 'bad');
    if (hasAlarm) { pill.classList.add('bad'); txt.textContent = 'alarma'; }
    else if (!monAct) { pill.classList.add('warn'); txt.textContent = 'detenido'; }
    else if (fallas > 0) { pill.classList.add('warn'); txt.textContent = fallas + ' falla' + (fallas === 1 ? '' : 's'); }
    else { txt.textContent = 'ok'; }

    var fbox = $('vxFallasBox');
    if (fbox) fbox.classList.toggle('bad', fallas > 0);

    // Aplanar surcos primarios en orden (tren, bajada).
    var todos = [];
    trenes.forEach(function (t) {
      var surcos = pick(t, 'Surcos', 'surcos') || [];
      surcos.forEach(function (s) { todos.push(s); });
    });
    todos.sort(function (a, b) {
      var ta = pick(a, 'Tren', 'tren') || 0;
      var tb = pick(b, 'Tren', 'tren') || 0;
      if (ta !== tb) return ta - tb;
      return (pick(a, 'Bajada', 'bajada') || 0) - (pick(b, 'Bajada', 'bajada') || 0);
    });

    // Separar primarios en dos sub-listas: semilla / fertilizante.
    var semilla = [], fert = [];
    todos.forEach(function (s) {
      var t = tipoOf(s);
      if (t === 'semilla') semilla.push(s);
      else if (t === 'fertilizante') fert.push(s);
    });

    var strip = $('vxStrip');
    if (semilla.length === 0 && fert.length === 0) {
      strip.innerHTML = '<div class="vx-empty">sin sensores de siembra/fertilización mapeados</div>';
      return;
    }

    var html = semilla.map(renderChip).join('');
    if (semilla.length > 0 && fert.length > 0) {
      html += '<span class="sep"></span>';
    }
    html += fert.map(renderChip).join('');
    strip.innerHTML = html;
  }

  async function poll() {
    if (state.paused) return;
    try {
      var r = await fetch('/api/vistax/live', { cache: 'no-store' });
      if (r.ok) render(await r.json());
    } catch (e) { /* silencioso */ }
  }

  function start() {
    if (state.timer) return;
    poll();
    state.timer = setInterval(poll, POLL_MS);
  }

  document.addEventListener('visibilitychange', function () {
    state.paused = document.hidden;
    if (!state.paused) poll();
  });

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
