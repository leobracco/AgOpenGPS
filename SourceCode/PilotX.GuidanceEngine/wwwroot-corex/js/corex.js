// ============================================================================
// corex.js — dashboard live de CoreX. Polling de /api/corex/status @1Hz,
// mismo refresco que tenía el timer WinForms. Toggles vía POST.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }
  function setText(id, v) { var el = $(id); if (el) el.textContent = v; }
  function setDot(id, state) {
    var el = $(id); if (!el) return;
    el.classList.remove('on', 'bad');
    if (state === true) el.classList.add('on');
    else if (state === false) el.classList.add('bad');
    // null/undefined → gris neutro (no configurado)
  }
  function fmtUptime(sec) {
    if (!sec) return '—';
    var h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
    return h > 0 ? h + ' h ' + m + ' min' : m + ' min';
  }

  async function poll() {
    try {
      var res = await fetch('/api/corex/status', { cache: 'no-store' });
      var d = await res.json();
      if (!d || !d.ok) return;

      setText('hdrVersion', 'v' + (d.version || '—'));
      setText('hdrProfile', d.profile || '—');

      var g = d.gps || {};
      setDot('dotGps', !!g.alive);
      setText('gpsLat', g.latitude != null ? g.latitude.toFixed(7) : '—');
      setText('gpsLon', g.longitude != null ? g.longitude.toFixed(7) : '—');

      var n = d.ntrip || {};
      setDot('dotNtrip', n.connected ? true : (n.required_on ? false : null));
      setText('ntripEstado', n.connected ? 'conectado'
        : n.connecting ? 'conectando…'
        : n.required_on ? 'esperando' : 'apagado');
      setText('ntripKb', (n.kb_total != null ? n.kb_total : 0) + ' kB');
      setText('ntripCaster', n.caster_ip || '—');
      // Tipos RTCM del caster ("1074×123"), los 6 más frecuentes.
      setText('ntripRtcm', (n.rtcm_types && n.rtcm_types.length)
        ? n.rtcm_types.slice(0, 6).join('  ') : '—');

      var m = d.mqtt || {};
      setDot('dotMqtt', !!m.running);
      setText('mqttPort', m.port != null ? m.port : '—');
      setText('mqttClients', m.clients != null ? m.clients : '—');
      setText('mqttMsgs', m.messages != null ? m.messages : '—');
      setText('mqttUptime', m.running ? fmtUptime(m.uptime_sec) : '—');
      var tp = $('mqttTopics');
      if (tp) tp.textContent = (m.recent_topics || []).join('\n');

      var mods = d.modules || {};
      setDot('dotSteer', mods.steer_configured ? !!mods.steer_hello : null);
      setText('modSteer', mods.steer_configured
        ? (mods.steer_hello ? 'online' : 'sin hello') : 'no configurado');
      setDot('dotMachine', mods.machine_configured ? !!mods.machine_hello : null);
      setText('modMachine', mods.machine_configured
        ? (mods.machine_hello ? 'online' : 'sin hello') : 'no configurado');
      setDot('dotImu', mods.imu_configured ? !!mods.imu_hello : null);
      setText('modImu', mods.imu_configured
        ? (mods.imu_hello ? 'online' : 'sin hello') : 'no configurado');
    } catch (e) { /* offline: el próximo tick reintenta */ }
  }

  function post(url) {
    return fetch(url, { method: 'POST' }).then(function (r) { return r.json(); });
  }

  var btnMqtt = $('btnMqtt');
  if (btnMqtt) btnMqtt.addEventListener('click', function () {
    AgpModal.confirm('Broker MQTT', '¿Cambiar el estado del broker MQTT? Los nodos se desconectan si lo apagás.')
      .then(function (ok) { if (ok) return post('/api/corex/mqtt/toggle'); })
      .then(poll);
  });

  var btnNtrip = $('btnNtrip');
  if (btnNtrip) btnNtrip.addEventListener('click', function () {
    post('/api/corex/ntrip/toggle').then(poll);
  });

  // Polling — pausa con la tab oculta (mismo patrón que setup.js del Hub).
  var handle = null;
  function start() { if (!handle) handle = setInterval(poll, 1000); }
  function stop() { if (handle) { clearInterval(handle); handle = null; } }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stop(); else { poll(); start(); }
  });

  poll();
  start();
})();
