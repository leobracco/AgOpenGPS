// ============================================================================
// sidebar.js — render del sidebar común en todas las páginas del Hub.
// Uso: <aside class="sidebar" data-active="sistema"></aside><script src="...sidebar.js"></script>
// ============================================================================
(function () {
  'use strict';

  // Cierra el Hub. Canal principal: WebView2.postMessage('close-hub'), que el
  // FormAgroParallelHubWebView2 escucha via WebMessageReceived y mapea a
  // _host.Close(). postMessage SIEMPRE está disponible apenas la página corre
  // dentro de WebView2 (no hay timing race como con hostObjects).
  // Fallback: ShellBridge.Close() vía hostObjects.agp (por si se cambia el
  // wiring del WebMessageReceived).
  function closeHubViaBridge() {
    var w = (window.chrome && window.chrome.webview) || null;
    if (!w) {
      console.warn('[sidebar] No estamos en WebView2 — fallback window.close()');
      try { window.close(); } catch (_) {}
      return;
    }

    // 1) postMessage (canal estable).
    try {
      w.postMessage('close-hub');
    } catch (e) {
      console.warn('[sidebar] postMessage close-hub falló:', e);
    }

    // 2) Fallback hostObjects (best-effort, ignorar errores).
    try {
      if (w.hostObjects && w.hostObjects.agp && typeof w.hostObjects.agp.Close === 'function') {
        var p = w.hostObjects.agp.Close();
        if (p && typeof p.catch === 'function') {
          p.catch(function () { /* swallow — ya mandamos postMessage */ });
        }
      }
    } catch (_) { /* swallow */ }
  }

  // El Hub WebView solo muestra productos AgroParallel (X-*) + cloud + utilidades
  // del Hub. La config dura del tractor (Piloto, Vehículo, Herramienta, brillo
  // /energía del PC) vive en la UI nativa de Agro Parallel / AgValoniaGPS.
  // El FAB "✕ Cerrar Hub" devuelve al piloto nativo.
  const ITEMS = [
    { id: 'hub',      ico: '▤',  label: 'Hub',      href: 'hub.html' },
    { id: 'herramienta', ico: '⚙', label: 'Implemento', href: 'herramienta.html' },
    { id: 'quantix',  ico: '⟁',  label: 'QuantiX',  href: 'quantix.html' },
    { id: 'vistax',   ico: '◉',  label: 'VistaX',   href: 'vistax.html' },
    { id: 'sectionx', ico: '▦',  label: 'SectionX', href: 'sectionx.html' },
    { id: 'flowx',    ico: '◊',  label: 'FlowX',    href: 'flowx.html' },
    { id: 'stormx',   ico: '☴',  label: 'StormX',   href: 'stormx.html' },
    { id: 'corex-ecu', ico: '⌬', label: 'CoreX-ECU', href: 'corex-ecu.html' },
    { id: 'insumos',  ico: '🌱', label: 'Insumos',  href: 'insumos.html' },
    { id: 'mapas',    ico: '🗺',  label: 'Mapas',    href: 'mapas.html' },
    { id: 'prescripciones', ico: '⛗', label: 'Prescripciones', href: 'prescripciones.html' },
    { id: 'orbitx',   ico: '☁',  label: 'OrbitX',   href: 'orbitx.html' },
    { id: 'firmwares', ico: '⬇', label: 'Firmwares', href: 'firmwares.html' },
    { id: 'camaras',  ico: '⌘',  label: 'Cámaras',  href: 'camaras.html' },
    { id: 'nodos',    ico: '📡', label: 'Nodos',    href: 'nodos.html' },
    { id: 'setup',    ico: '🧭', label: 'Asistente', href: 'setup.html' },
    { id: 'debug',    ico: '🐞', label: 'Debug',    href: 'debug.html' },
    { id: 'pwa-qr',   ico: '▣',  label: 'Conectar celular', href: 'pwa-qr.html' },
    { id: 'actualizar', ico: '⤓', label: 'Actualizar', href: 'actualizar.html' }
  ];

  function render(aside) {
    const active = aside.getAttribute('data-active') || '';
    const html = [
      '<div class="brand">',
      '  <div class="brand-mark">AP</div>',
      '  <div>',
      '    <div class="brand-name">Agro Parallel</div>',
      '    <div class="brand-sub">PilotX · preview</div>',
      '  </div>',
      '</div>',
      '<ul class="nav">'
    ];
    for (const it of ITEMS) {
      const cls = it.id === active ? ' class="active"' : '';
      html.push(
        '<li><a' + cls + ' href="' + it.href + '">' +
          '<span class="ico">' + it.ico + '</span>' +
          '<span class="label">' + it.label + '</span>' +
        '</a></li>'
      );
    }
    html.push('</ul>');
    html.push(
      '<div class="sidebar-foot">',
      '  <div class="foot-meta">Sesión: <strong style="color:var(--agp-text)">Demo</strong></div>',
      '  <div class="foot-meta" style="margin-top:4px">Cloud: <span style="color:var(--agp-state-ok)">●</span> conectado</div>',
      '  <button id="agpOpenWifi" type="button" style="margin-top:12px; width:100%; min-height:44px; padding:8px 12px;',
      '    background:rgba(74,186,62,0.10); color:var(--agp-text); border:1px solid var(--agp-border);',
      '    border-radius: var(--agp-radius-md); cursor:pointer; font-weight:var(--agp-fw-medium)" title="WiFi de Windows">',
      '    <span class="foot-ico">📶</span><span class="foot-label"> WiFi de Windows</span>',
      '  </button>',
      '  <button id="agpCloseHub" type="button" style="margin-top:8px; width:100%; min-height:44px; padding:8px 12px;',
      '    background:rgba(201,45,45,0.12); color:#E15A5A; border:1px solid rgba(201,45,45,0.35);',
      '    border-radius: var(--agp-radius-md); cursor:pointer; font-weight:var(--agp-fw-medium)" title="Cerrar Hub">',
      '    <span class="foot-ico">✕</span><span class="foot-label"> Cerrar Hub</span>',
      '  </button>',
      '</div>'
    );
    aside.innerHTML = html.join('');

    var btnWifi = aside.querySelector('#agpOpenWifi');
    if (btnWifi) {
      if (!isInWebView2()) {
        // Fuera del Hub no hay ShellBridge — no podemos abrir el applet nativo.
        btnWifi.style.display = 'none';
      } else {
        btnWifi.addEventListener('click', function () {
          // Canal: hostObjects.agp.OpenWifiSettings(). Si el bridge no resolvió
          // por timing race (poco probable acá, el sidebar render es tardío),
          // mandamos un postMessage de fallback que el host puede mapear.
          try {
            var w = window.chrome && window.chrome.webview;
            if (w && w.hostObjects && w.hostObjects.agp
                && typeof w.hostObjects.agp.OpenWifiSettings === 'function') {
              var p = w.hostObjects.agp.OpenWifiSettings();
              if (p && typeof p.catch === 'function') p.catch(function () {});
              return;
            }
            if (w) w.postMessage('open-wifi-settings');
          } catch (e) {
            console.warn('[sidebar] OpenWifiSettings error:', e);
          }
        });
      }
    }

    var btn = aside.querySelector('#agpCloseHub');
    if (btn) {
      // Mostramos SIEMPRE el botón si estamos dentro de WebView2. El check
      // viejo de hostObjects.agp era frágil (proxy asíncrono que se evalúa
      // antes de que AddHostObjectToScript haya inyectado el objeto) y dejaba
      // el botón invisible. Ahora el canal principal es postMessage, que está
      // disponible apenas la página corre.
      if (!isInWebView2()) {
        // Página servida fuera del Hub (ej: browser normal o widget Avalonia
        // con título nativo). El operario cierra con la X de la ventana.
        btn.style.display = 'none';
      } else {
        btn.addEventListener('click', function () {
          // El operario ya pulsó deliberadamente la X roja. NO usar confirm()
          // nativo: en kiosko WebView2 borderless ese diálogo no se ve bien
          // (queda detrás, sin teclado, o se autodescarta) y daba la sensación
          // de que el botón no hacía nada.
          closeHubViaBridge();
        });
      }
    }
  }

  document.querySelectorAll('aside.sidebar').forEach(render);

  // True si la página corre dentro de WebView2 (Hub o widget Avalonia con
  // WebView2). Suficiente para mostrar el botón — el cierre se enruta por
  // postMessage que el host decide cómo manejar (o ignorar).
  function isInWebView2() {
    try {
      var w = window.chrome && window.chrome.webview;
      return !!(w && typeof w.postMessage === 'function');
    } catch (e) { return false; }
  }

  // El FAB flotante de "✕ Cerrar Hub" redondo arriba a la derecha se sacó:
  // el sidebar ya trae su propio botón rectangular "✕ Cerrar Hub" en el pie,
  // y el operario lo percibía como "cruz extra redonda" frente a la X nativa
  // del Form (cuando el Hub corre con chrome) o frente al botón del sidebar.
  // Si en el futuro hace falta volver a tener cierre flotante, restaurar el
  // injectFab() de la historia git.

  // Si quedó algún FAB inyectado por una versión previa cacheada del JS,
  // lo removemos defensivamente al cargar.
  (function purgeLegacyFab() {
    function tryPurge() {
      var old = document.getElementById('agpFabClose');
      if (old && old.parentNode) old.parentNode.removeChild(old);
    }
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', tryPurge);
    } else {
      tryPurge();
    }
  })();
})();
