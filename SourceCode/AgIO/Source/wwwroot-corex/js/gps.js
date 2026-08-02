// ============================================================================
// gps.js — Página de datos GPS de CoreX (port de FormGPSData).
//
// Polling @1s de GET /api/corex/gps. Cada request además renueva el
// keep-alive de captura de sentencias NMEA en el backend (a los 5 s sin
// polling se apaga sola, no hace falta avisar al salir de la página).
//
// Unidades al operario: km/h, m, °, s. Los campos imu_* llegan crudos del
// PANDA (heading/rolido/cabeceo/yaw ×10) y acá se escalan a grados.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function setText(id, v) {
    var el = $(id);
    if (el) el.textContent = v;
  }

  function num(v, dec, unit) {
    if (typeof v !== 'number' || !isFinite(v)) return '—';
    return v.toFixed(dec) + (unit || '');
  }

  // imu_* crudos ×10 → grados con 1 decimal. 0 crudo = sin dato del IMU
  // cuando tampoco hay heading (el form viejo mostraba el crudo directo).
  function imu(v, unit) {
    if (typeof v !== 'number' || !isFinite(v)) return '—';
    return (v / 10).toFixed(1) + (unit || '°');
  }

  function setNmea(id, v) {
    setText(id, v ? v.trim() : '—');
  }

  function refresh() {
    fetch('/api/corex/gps', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.ok || !d.gps) return;
        var g = d.gps;

        var dot = $('dotGpsLive');
        if (dot) {
          dot.classList.remove('on', 'bad');
          dot.classList.add(g.alive ? 'on' : 'bad');
        }

        setText('gpsLat', num(g.latitude, 7));
        setText('gpsLon', num(g.longitude, 7));
        setText('gpsAlt', num(g.altitude_m, 1, ' m'));
        setText('gpsSpeed', num(g.speed_kmh, 1, ' km/h'));

        setText('gpsFix', g.fix_quality || '—');
        setText('gpsSats', typeof g.sats === 'number' ? String(g.sats) : '—');
        setText('gpsHdop', num(g.hdop, 2));
        setText('gpsAge', num(g.age_sec, 1, ' s'));

        setText('gpsHdg', num(g.heading_true, 2, '°'));
        setText('gpsHdgDual', num(g.heading_dual, 2, '°'));
        setText('gpsRoll', num(g.roll_deg, 2, '°'));

        setText('imuHdg', imu(g.imu_heading));
        setText('imuRoll', imu(g.imu_roll));
        setText('imuPitch', imu(g.imu_pitch));
        setText('imuYaw', imu(g.imu_yaw_rate, '°/s'));

        renderInventario(g.nmea && g.nmea.vistas);

        var n = g.nmea;
        setNmea('nmeaGga', n && n.gga);
        setNmea('nmeaVtg', n && n.vtg);
        setNmea('nmeaPanda', n && n.panda);
        setNmea('nmeaPaogi', n && n.paogi);
        setNmea('nmeaHdt', n && n.hdt);
        setNmea('nmeaAvr', n && n.avr);
        setNmea('nmeaHpd', n && n.hpd);
        setNmea('nmeaKsxt', n && n.ksxt);
      })
      .catch(function () { /* offline: el próximo tick reintenta */ });
  }

  // ---- Inventario NMEA: checklist de qué sentencias manda el receptor ----
  // Tipos que PilotX conoce/espera; lo que llegue fuera de esta lista (GSA,
  // GSV, ZDA, TXT, propietarias...) se agrega solo, abajo.
  var ESPERADAS = ['GPGGA', 'GPVTG', 'GPRMC', 'GPHDT', 'PANDA', 'PAOGI',
                   'PTNL', 'GPHPD', 'KSXT'];
  var FRESCA_S = 3;

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  function invRow(tipo, vista) {
    var cls, est;
    if (!vista) { cls = 'never'; est = '✖'; }
    else if (vista.edad_sec <= FRESCA_S) { cls = 'ok'; est = '✔'; }
    else { cls = 'stale'; est = '⚠'; }
    var edad = vista ? (vista.edad_sec <= FRESCA_S ? 'ahora' : vista.edad_sec + ' s') : '—';
    return '<div class="inv-row ' + cls + '">' +
           '<span class="inv-est ' + cls + '">' + est + '</span>' +
           '<span class="inv-tag">' + esc(tipo) + '</span>' +
           '<span class="inv-edad">' + esc(edad) + '</span>' +
           '<span class="inv-cruda">' + esc(vista ? vista.cruda : '') + '</span>' +
           '</div>';
  }

  function renderInventario(vistas) {
    var box = document.getElementById('nmeaInventario');
    if (!box) return;
    var porTipo = {};
    (vistas || []).forEach(function (v) {
      // Normalizar talker: GNGGA/GLGGA cuentan como GPGGA de la lista.
      porTipo[v.tipo] = v;
      var m = v.tipo.match(/^G[A-Z](GGA|VTG|RMC|HDT)$/);
      if (m && !porTipo['GP' + m[1]]) porTipo['GP' + m[1]] = v;
    });
    var html = '';
    ESPERADAS.forEach(function (t) { html += invRow(t, porTipo[t]); });
    (vistas || []).forEach(function (v) {
      if (ESPERADAS.indexOf(v.tipo) === -1 &&
          !/^G[A-Z](GGA|VTG|RMC|HDT)$/.test(v.tipo)) html += invRow(v.tipo, v);
    });
    box.innerHTML = html || 'esperando datos…';
  }

  refresh();
  var interval = setInterval(refresh, 1000);
  window.addEventListener('pagehide', function () { clearInterval(interval); });
}());
