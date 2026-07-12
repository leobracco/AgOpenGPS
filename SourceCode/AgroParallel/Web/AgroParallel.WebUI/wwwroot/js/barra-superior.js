// ============================================================================
// barra-superior.js — espejo HTML de la barra superior nativa (panelControlBox):
// Lote/Carga/GPS/Velocidad/Minimizar/Maximizar/Cerrar. Comandos = contrato
// con FormGPS.ExecuteGuidanceCommand (mismo canal que menu-izquierda.js).
// Estado en vivo (velocidad, calidad de fix, alimentación, lote abierto) sale
// de GET /api/aog/state — mismo endpoint que ya usan hub.js/flowx.js.
// ============================================================================
(function () {
  'use strict';

  async function send(cmd, btn) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (btn) {
        btn.classList.add('flash');
        setTimeout(function () { btn.classList.remove('flash'); }, 250);
      }
      if (!data.ok) console.warn('[barra-superior] comando rechazado:', cmd, data.error);
    } catch (e) {
      console.warn('[barra-superior] sin conexión:', e.message);
    }
  }

  document.querySelectorAll('.tbtn[data-cmd]').forEach(function (b) {
    b.addEventListener('click', function () { send(b.dataset.cmd, b); });
  });

  var speedVal = document.getElementById('speedVal');
  var btnLote = document.getElementById('btnLote');
  var btnGps = document.getElementById('btnGps');
  var btnCarga = document.getElementById('btnCarga');

  var STAT_CLASSES = ['stat-ok', 'stat-mid', 'stat-low', 'stat-bad'];
  function setStat(el, cls) {
    STAT_CLASSES.forEach(function (c) { el.classList.remove(c); });
    if (cls) el.classList.add(cls);
  }

  // igual mapeo que GUI.Designer.cs (switch pn.fixQuality) para btnGPSData.BackColor
  function fixClass(fixQuality) {
    switch (fixQuality) {
      case 4: return 'stat-ok';   // RTK fijo
      case 5: return 'stat-mid';  // RTK float
      case 2: return 'stat-low';  // DGPS
      default: return 'stat-bad'; // sin fix
    }
  }

  async function poll() {
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      var snap = await res.json();

      var speed = Number(snap.avg_speed || 0);
      speedVal.textContent = speed.toFixed(1);

      setStat(btnGps, fixClass(Number(snap.fix_quality || 0)));
      setStat(btnCarga, snap.power_online ? 'stat-ok' : 'stat-bad');

      btnLote.classList.toggle('disabled', !snap.is_job_started);
    } catch (e) {
      // sin conexión: deja el último estado conocido en pantalla
    }
  }

  poll();
  setInterval(poll, 1000);
})();
