// ============================================================================
// eventos.js
// Reemplazo HTML de la ventana WinForms FormEventViewer ("Event Viewer").
// GET /api/aog/eventos trae la cola del log persistido en disco (history) y
// el buffer de la sesión actual (session). Sin polling: carga inicial + botón
// Actualizar (igual que el form viejo). El log de sesión usa \r como salto de
// línea (Log.EventWriter), se normaliza acá. El endpoint serializa en
// snake_case (AgpJson): history, session, file.
// ============================================================================

(function () {
  'use strict';

  var btn  = document.getElementById('btnRefreshLog');
  var box  = document.getElementById('logBox');
  var hint = document.getElementById('logHint');

  function normalize(s) {
    return (s || '').replace(/\r\n?/g, '\n').replace(/\s+$/, '');
  }

  async function load() {
    btn.disabled = true;
    hint.textContent = 'Cargando…';
    try {
      var r = await fetch('/api/aog/eventos', { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      var d = await r.json();

      var history = normalize(d.history);
      var session = normalize(d.session);

      var partes = [];
      if (history) partes.push(history);
      partes.push('──── Sesión actual ────');
      partes.push(session || '(sin eventos en esta sesión)');

      box.textContent = partes.join('\n\n');
      hint.textContent = d.file || '';

      // Lo último es lo que importa: scroll al final.
      box.scrollTop = box.scrollHeight;
    } catch (e) {
      hint.textContent = 'No se pudo cargar el registro de eventos.';
    } finally {
      btn.disabled = false;
    }
  }

  btn.addEventListener('click', load);
  load();
})();
