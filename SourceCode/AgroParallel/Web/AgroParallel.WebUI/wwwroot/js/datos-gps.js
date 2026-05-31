// ============================================================================
// datos-gps.js
// Reemplazo HTML/táctil de la ventana FormGPSData de PilotX.
// Solo lectura de /api/aog/state. Cadencia 1 Hz (no necesitamos 10 Hz para
// un panel informativo; el piloto sigue trabajando a 10 Hz en su propio canvas).
// ============================================================================

(function () {
  'use strict';

  var pillEl     = document.getElementById('estadoPill');
  var pillText   = document.getElementById('estadoText');
  var kpiVel     = document.getElementById('kpiVel');
  var kpiHeading = document.getElementById('kpiHeading');
  var kpiCard    = document.getElementById('kpiCardinal');
  var kpiLat     = document.getElementById('kpiLat');
  var kpiLon     = document.getElementById('kpiLon');
  var kpiEast    = document.getElementById('kpiEast');
  var kpiNorth   = document.getElementById('kpiNorth');
  var detVeh     = document.getElementById('detVehiculo');
  var detAncho   = document.getElementById('detAncho');
  var detSec     = document.getElementById('detSec');
  var detJob     = document.getElementById('detJob');

  function fmt(v, dec) {
    if (v == null || isNaN(v)) return '—';
    return Number(v).toFixed(dec || 0);
  }

  // Convierte heading (radianes) a cardinal estilo "NE", "S", etc.
  // El operario lee mejor "NE" que "47°", pero le mostramos ambos.
  function radToCardinal(rad) {
    if (rad == null || isNaN(rad)) return '—';
    var deg = (rad * 180 / Math.PI) % 360;
    if (deg < 0) deg += 360;
    var dirs = ['N','NE','E','SE','S','SO','O','NO'];
    return dirs[Math.round(deg / 45) % 8];
  }
  function radToDeg(rad) {
    if (rad == null || isNaN(rad)) return null;
    var d = (rad * 180 / Math.PI) % 360;
    if (d < 0) d += 360;
    return d;
  }

  function render(snap) {
    if (!snap) return;

    // Pill: usamos isJobStarted como proxy débil de "señal en uso". Si no hay
    // lat/lon de fix válido (ambos 0), mostramos "Sin fix".
    var hasLat = (snap.latitude && snap.latitude !== 0);
    if (hasLat) {
      pillEl.className = 'pill ok';
      pillText.textContent = snap.isJobStarted ? 'En trabajo' : 'Fix OK';
    } else {
      pillEl.className = 'pill idle';
      pillText.textContent = 'Sin fix';
    }

    // Velocidad + heading
    kpiVel.textContent = fmt(snap.avgSpeed, 1);
    var deg = radToDeg(snap.heading);
    kpiHeading.textContent = deg != null ? fmt(deg, 0) : '—';
    kpiCard.textContent = radToCardinal(snap.heading);

    // Coords: 7 decimales (~11 mm) para lat/lon — suficiente y legible.
    kpiLat.textContent = hasLat ? fmt(snap.latitude, 7) + ' °' : '—';
    kpiLon.textContent = hasLat ? fmt(snap.longitude, 7) + ' °' : '—';
    kpiEast.textContent  = fmt(snap.pivotEasting, 2)  + ' m';
    kpiNorth.textContent = fmt(snap.pivotNorthing, 2) + ' m';

    // Equipo
    detVeh.textContent = (snap.vehicleBrand ? (snap.vehicleBrand + ' · ') : '')
                       + (snap.vehicleType || '—');
    detAncho.textContent = (snap.toolWidth ? fmt(snap.toolWidth, 2) + ' m' : '— m');
    var num = snap.numSections || 0;
    var on = 0, arr = snap.sectionOnRequest || [];
    for (var i = 0; i < num; i++) if (arr[i]) on++;
    detSec.textContent = num > 0 ? (on + ' activas de ' + num) : '—';
    detJob.textContent = snap.isJobStarted
      ? (snap.currentFieldDirectory || 'Lote sin nombre')
      : 'No';
  }

  async function pollState() {
    try {
      var r = await fetch('/api/aog/state', { cache: 'no-store' });
      if (!r.ok) return;
      var snap = await r.json();
      render(snap);
    } catch (e) { /* offline */ }
  }

  var handle = null;
  function startPolling() { if (!handle) handle = setInterval(pollState, 1000); }
  function stopPolling()  { if (handle) { clearInterval(handle); handle = null; } }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stopPolling();
    else { pollState(); startPolling(); }
  });

  (async function init() {
    await pollState();
    startPolling();
  })();
})();
