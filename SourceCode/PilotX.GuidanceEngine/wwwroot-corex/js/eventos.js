// ============================================================================
// eventos.js — Visor de eventos de CoreX (port de FormEventViewer).
//
// GET /api/corex/eventos trae el log persistido en disco (histórico) y el
// buffer de la sesión actual. Sin polling: carga inicial + botón Actualizar
// (igual que el form viejo). El log de sesión usa \r como salto de línea
// (Log.EventWriter), se normaliza acá.
// ============================================================================

(function () {
  'use strict';

  var btn = document.getElementById('btnRefreshLog');
  var box = document.getElementById('logBox');
  var hint = document.getElementById('logHint');

  function normalize(s) {
    return (s || '').replace(/\r\n?/g, '\n').trim();
  }

  function load() {
    btn.disabled = true;
    hint.textContent = 'Cargando…';

    fetch('/api/corex/eventos', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.ok) throw new Error('respuesta inválida');

        var historico = normalize(d.historico);
        var sesion = normalize(d.sesion);

        var partes = [];
        if (historico) partes.push(historico);
        partes.push('──── Sesión actual ────');
        partes.push(sesion || '(sin eventos en esta sesión)');

        box.textContent = partes.join('\n\n');
        hint.textContent = d.archivo || '';

        // Lo último es lo que importa: scroll al final.
        box.scrollTop = box.scrollHeight;
      })
      .catch(function () {
        hint.textContent = 'No se pudo cargar el registro.';
        AgpModal.alert('Error de red', 'No se pudo cargar el registro de eventos.');
      })
      .finally(function () {
        btn.disabled = false;
      });
  }

  btn.addEventListener('click', load);
  load();
}());
