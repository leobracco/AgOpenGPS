// ============================================================================
// cx-shell.js — botonera de shell para cuando las páginas de CoreX corren
// embebidas en el WebView del Hub de PilotX (overlay sin marco sobre el mapa,
// sin X nativa). Agrega, fijos abajo a la derecha:
//   ◂ Hub    → vuelve al Hub de PilotX (127.0.0.1:5180)
//   ✕ Cerrar → cierra la ventana (postMessage 'close-hub', el mismo canal que
//              usa sidebar.js del Hub; FormAgroParallelHubWebView2 no filtra
//              por origen, así que funciona también desde :5181)
// En un browser normal (sin WebView2) no se muestra nada: la pestaña tiene
// su propia X y el Hub puede no estar corriendo.
// ============================================================================
(function () {
  'use strict';

  var w = window.chrome && window.chrome.webview;
  if (!w || typeof w.postMessage !== 'function') return;

  function build() {
    if (document.getElementById('cxShellBar')) return;

    var bar = document.createElement('div');
    bar.id = 'cxShellBar';
    bar.style.cssText =
      'position:fixed; right:14px; bottom:14px; z-index:9999;' +
      'display:flex; gap:10px;';

    function mkBtn(text, css) {
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = text;
      b.style.cssText =
        'min-height:48px; padding:10px 18px; font:600 15px/1 "Segoe UI",sans-serif;' +
        'border-radius:10px; cursor:pointer; box-shadow:0 2px 10px rgba(0,0,0,0.18);' +
        css;
      return b;
    }

    var back = mkBtn('◂ Hub',
      'background:#F5F7F4; color:#101612; border:1px solid #C5CFC5;');
    back.title = 'Volver al Hub de PilotX';
    back.addEventListener('click', function () {
      window.location.href = 'http://127.0.0.1:5180/pages/hub.html';
    });

    var close = mkBtn('✕ Cerrar',
      'background:rgba(201,45,45,0.12); color:#E15A5A; border:1px solid rgba(201,45,45,0.4);');
    close.title = 'Cerrar esta ventana';
    close.addEventListener('click', function () {
      try { w.postMessage('close-hub'); } catch (e) { /* sin canal, nada */ }
    });

    bar.appendChild(back);
    bar.appendChild(close);
    document.body.appendChild(bar);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', build);
  } else {
    build();
  }
})();
