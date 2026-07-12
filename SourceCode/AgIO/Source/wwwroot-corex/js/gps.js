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

  refresh();
  var interval = setInterval(refresh, 1000);
  window.addEventListener('pagehide', function () { clearInterval(interval); });
}());
