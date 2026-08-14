// ============================================================================
// monitor.js — Monitor de tráfico de CoreX (port de FormUDPMonitor +
// FormSerialMonitor "log monitor").
//
// Dos monitores independientes con el mismo mecanismo:
//   · Polling @800ms que drena lo acumulado en el backend desde el último
//     request. Cada GET renueva el keep-alive; al pausar (o salir de la
//     página) el backend apaga la captura solo a los 5 s.
//   · El texto se acumula en el cliente con tope de líneas (recorta arriba).
//   · Guardar descarga lo visible como .txt (reemplazo del write a
//     zAgIO_*_log.txt del form viejo).
//
// Filtros del monitor UDP (NMEA / NTRIP) → POST inmediato al cambiar.
// ============================================================================

(function () {
  'use strict';

  var MAX_LINES = 3000;
  var POLL_MS = 800;

  function $(id) { return document.getElementById(id); }

  // ── Un monitor genérico: caja + pausa + limpiar + guardar ──────────────────
  function makeMonitor(opts) {
    var box = $(opts.boxId);
    var btnPause = $(opts.pauseId);
    var btnClear = $(opts.clearId);
    var btnSave = $(opts.saveId);
    var paused = false;
    var inFlight = false;

    function append(text) {
      if (!text) return;
      // Autoscroll solo si ya estaba mirando el final (±40px).
      var follow = box.scrollTop + box.clientHeight >= box.scrollHeight - 40;

      box.textContent += text;

      var lines = box.textContent.split('\n');
      if (lines.length > MAX_LINES) {
        box.textContent = lines.slice(lines.length - MAX_LINES).join('\n');
      }
      if (follow) box.scrollTop = box.scrollHeight;
    }

    function poll() {
      if (paused || inFlight) return;
      inFlight = true;
      fetch(opts.url, { cache: 'no-store' })
        .then(function (r) { return r.json(); })
        .then(function (d) {
          if (!d || !d.ok) return;
          append(d.data || '');
          if (opts.onData) opts.onData(d);
        })
        .catch(function () { /* offline: el próximo tick reintenta */ })
        .finally(function () { inFlight = false; });
    }

    btnPause.addEventListener('click', function () {
      paused = !paused;
      btnPause.textContent = paused ? 'Reanudar' : 'Pausar';
      btnPause.classList.toggle('green', paused);
    });

    btnClear.addEventListener('click', function () {
      box.textContent = '';
    });

    btnSave.addEventListener('click', function () {
      var blob = new Blob([box.textContent], { type: 'text/plain' });
      var a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = opts.fileName;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(a.href);
    });

    var interval = setInterval(poll, POLL_MS);
    window.addEventListener('pagehide', function () { clearInterval(interval); });
    poll();
  }

  // ── Monitor UDP/PGN con filtros NMEA/NTRIP ─────────────────────────────────
  var chkNmea = $('chkUdpNmea');
  var chkNtrip = $('chkUdpNtrip');
  var flagsSynced = false;

  function pushFlags() {
    fetch('/api/corex/monitor/udp/flags', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        log_nmea: chkNmea.checked,
        log_ntrip: chkNtrip.checked,
      }),
    }).catch(function () {
      AgpModal.alert('Error de red', 'No se pudo aplicar el filtro del monitor.');
    });
  }

  chkNmea.addEventListener('change', pushFlags);
  chkNtrip.addEventListener('change', pushFlags);

  makeMonitor({
    boxId: 'udpBox',
    pauseId: 'btnUdpPause',
    clearId: 'btnUdpClear',
    saveId: 'btnUdpSave',
    url: '/api/corex/monitor/udp',
    fileName: 'corex_trafico_udp.txt',
    onData: function (d) {
      // Sincronizar los checks con el estado real UNA vez (primer poll);
      // después mandan los clicks del usuario.
      if (!flagsSynced) {
        flagsSynced = true;
        chkNmea.checked = !!d.log_nmea;
        chkNtrip.checked = !!d.log_ntrip;
      }
    },
  });

  // ── Monitor GPS crudo ──────────────────────────────────────────────────────
  makeMonitor({
    boxId: 'rawBox',
    pauseId: 'btnRawPause',
    clearId: 'btnRawClear',
    saveId: 'btnRawSave',
    url: '/api/corex/monitor/gps',
    fileName: 'corex_gps_crudo.txt',
  });
}());
