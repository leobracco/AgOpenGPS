// ============================================================================
// hdr.js — pills de versión y perfil en las subpáginas de CoreX.
// El dashboard usa corex.js (poll @1Hz); acá alcanza con un refresh lento.
// ============================================================================

(function () {
  'use strict';

  function setText(id, v) {
    var el = document.getElementById(id);
    if (el) el.textContent = v;
  }

  async function refresh() {
    try {
      var res = await fetch('/api/corex/status', { cache: 'no-store' });
      var d = await res.json();
      if (!d || !d.ok) return;
      setText('hdrVersion', 'v' + (d.version || '—'));
      setText('hdrProfile', d.profile || '—');
    } catch (_) { /* sin backend no hay pills, no molesta */ }
  }

  refresh();
  setInterval(refresh, 5000);
})();
