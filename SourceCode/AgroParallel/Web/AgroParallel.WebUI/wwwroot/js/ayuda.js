// ============================================================================
// ayuda.js
// Reemplazo HTML del WinForms FormHelp. La navegación son links estáticos a las
// utilidades internas del Hub; este script solo completa la tarjeta "Acerca de"
// con la versión instalada (GET /api/pilotx/update/status → current_version) y
// el equipo vinculado (GET /api/orbitx/status → estab_slug / device_id).
// ============================================================================

(function () {
  'use strict';

  var elVersion = document.getElementById('aboutVersion');
  var elEquipo  = document.getElementById('aboutEquipo');

  async function loadVersion() {
    try {
      var r = await agpApi.get('pilotx/update/status');
      var st = r && r.status;
      elVersion.textContent = (st && st.current_version) ? st.current_version : '—';
    } catch (e) {
      elVersion.textContent = '—';
    }
  }

  async function loadEquipo() {
    try {
      var st = await agpApi.get('orbitx/status');
      var name = (st && (st.estab_slug || st.device_id)) || 'Sin vincular';
      elEquipo.textContent = name;
    } catch (e) {
      elEquipo.textContent = '—';
    }
  }

  loadVersion();
  loadEquipo();
})();
