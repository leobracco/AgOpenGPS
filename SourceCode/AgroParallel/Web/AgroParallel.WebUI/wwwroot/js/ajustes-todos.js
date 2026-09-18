// ============================================================================
// ajustes-todos.js
// Reemplazo HTML/táctil de la ventana WinForms FormAllSettings ("View All
// Settings"). Solo lectura de /api/aog/all-settings. Los grupos "En vivo"
// refrescan a 1 Hz; los "Ajustes" estáticos se piden una sola vez (no cambian
// mientras la página está abierta).
// El endpoint serializa en snake_case (AgpJson).
// ============================================================================

(function () {
  'use strict';

  var verText     = document.getElementById('verText');
  var liveGrid    = document.getElementById('liveGrid');
  var settingsGrid = document.getElementById('settingsGrid');
  var vehFileNote = document.getElementById('vehFileNote');

  // Construye una tarjeta a partir de un grupo { title, rows:[{label,value}] }.
  function buildCard(group) {
    var card = document.createElement('div');
    card.className = 'card';

    var h = document.createElement('h3');
    h.textContent = group.title || '';
    card.appendChild(h);

    var rows = group.rows || [];
    for (var i = 0; i < rows.length; i++) {
      var row = document.createElement('div');
      row.className = 'kv-row';
      var label = document.createElement('span');
      label.textContent = rows[i].label || '';
      var val = document.createElement('strong');
      val.textContent = (rows[i].value == null || rows[i].value === '') ? '—' : rows[i].value;
      row.appendChild(label);
      row.appendChild(val);
      card.appendChild(row);
    }
    return card;
  }

  function renderGroups(grid, groups) {
    grid.textContent = '';
    (groups || []).forEach(function (g) { grid.appendChild(buildCard(g)); });
  }

  var settingsLoaded = false;

  async function poll() {
    try {
      var r = await fetch('/api/aog/all-settings', { cache: 'no-store' });
      if (!r.ok) return;
      var d = await r.json();

      if (d.sem_ver) verText.textContent = 'v' + d.sem_ver;

      // "En vivo" siempre: cada segundo.
      renderGroups(liveGrid, d.live);

      // "Ajustes" estáticos: una sola vez (evita reflow innecesario).
      if (!settingsLoaded) {
        renderGroups(settingsGrid, d.groups);
        if (d.vehicle_file) {
          vehFileNote.textContent = 'Perfil de vehículo: ' + d.vehicle_file;
        }
        settingsLoaded = true;
      }
    } catch (e) { /* offline */ }
  }

  var handle = null;
  function start() { if (!handle) handle = setInterval(poll, 1000); }
  function stop()  { if (handle) { clearInterval(handle); handle = null; } }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stop();
    else { poll(); start(); }
  });

  (async function init() {
    await poll();
    start();
  })();
})();
