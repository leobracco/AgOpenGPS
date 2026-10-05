// ============================================================================
// guia-rapida.js — widget "Elegir guía" (vive solo con lote abierto).
// Colapsado: un botón. Al tocarlo se despliegan los 3 modos de creación
// (A · A/B · A/B curvo) — cada uno POST /api/aog/guidance/command {cmd} y el
// backend abre el FormBuildTracks nativo ya posicionado en ese flujo.
// Resize de la ventanita nativa vía postMessage('resize:WxH')
// (FormAgroParallelHubWebView2.ResizeFloatingWidget); fuera de WebView2 el
// panel simplemente aparece dentro del alto disponible.
// ============================================================================
(function () {
  'use strict';

  // Material compacto: FAB solo / FAB + card de 3 modos
  var COLLAPSED = { w: 200, h: 80 };
  var EXPANDED = { w: 400, h: 186 };

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
      if (!data.ok) console.warn('[guia-rapida] comando rechazado:', cmd, data.error);
    } catch (e) {
      console.warn('[guia-rapida] sin conexión:', e.message);
    }
  }

  function resizeWidget(w, h) {
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv) wv.postMessage('resize:' + w + 'x' + h);
    } catch (e) { /* fuera de WebView2: no-op */ }
  }

  var gTrack = document.getElementById('gTrack');
  var panel = document.getElementById('trackPanel');

  function togglePanel() {
    var open = panel.hidden;
    panel.hidden = !open;
    gTrack.classList.toggle('on', open);
    resizeWidget(open ? EXPANDED.w : COLLAPSED.w, open ? EXPANDED.h : COLLAPSED.h);
  }
  gTrack.addEventListener('click', togglePanel);

  // Los 3 modos de creación: mandan el comando y colapsan el panel (el
  // operario sigue en el form nativo que se abrió).
  document.querySelectorAll('[data-cmd]').forEach(function (b) {
    b.addEventListener('click', function () {
      send(b.dataset.cmd, b);
      setTimeout(function () { if (!panel.hidden) togglePanel(); }, 300);
    });
  });
})();
