// ============================================================================
// sidebar.js — render del sidebar común en todas las páginas del Hub.
// Uso: <aside class="sidebar" data-active="sistema"></aside><script src="...sidebar.js"></script>
// ============================================================================
(function () {
  'use strict';

  // Modo widget flotante (PilotX abre páginas como datos-lote.html?widget=1 como
  // ventana chica independiente, sin la navegación del Hub). En ese modo no
  // renderizamos el sidebar: la página muestra solo su contenido y se cierra con
  // la X nativa de la ventana. Sale temprano para no hacer trabajo de más.
  if (document.documentElement.classList.contains('widget-mode')) return;

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
  // del Hub. El FAB "✕ Cerrar Hub" devuelve al piloto nativo.
  //
  // Navegación agrupada: con 20+ páginas la lista plana no entraba en la
  // pantalla de 10". Grupos colapsables (acordeón táctil); el grupo de la
  // página activa arranca abierto y el estado de cada grupo persiste en
  // localStorage (agp.nav.g.<id>). group id '' = ítems sueltos arriba de todo.
  const GROUPS = [
    { id: '', label: '', items: [
      { id: 'hub',      ico: '▤',  label: 'Hub',      href: 'hub.html' }
    ]},
    { id: 'modulos', label: 'Módulos', items: [
      { id: 'quantix',  ico: '⟁',  label: 'QuantiX',  href: 'quantix.html' },
      { id: 'flowx',    ico: '◊',  label: 'FlowX',    href: 'flowx.html' },
      { id: 'sectionx', ico: '▦',  label: 'SectionX', href: 'sectionx.html' },
      { id: 'linex',    ico: '⊞',  label: 'LineX',    href: 'linex.html' },
      { id: 'vistax',   ico: '◉',  label: 'VistaX',   href: 'vistax.html' },
      { id: 'stormx',   ico: '☴',  label: 'StormX',   href: 'stormx.html' },
      { id: 'corex-ecu', ico: '⌬', label: 'CoreX-ECU', href: 'corex-ecu.html' },
      { id: 'nodos',    ico: '📡', label: 'Nodos',    href: 'nodos.html' },
      { id: 'camaras',  ico: '⌘',  label: 'Cámaras',  href: 'camaras.html' }
    ]},
    { id: 'campo', label: 'Campo', items: [
      { id: 'insumos',  ico: '🌱', label: 'Insumos',  href: 'insumos.html' },
      { id: 'mapas',    ico: '🗺',  label: 'Mapas',    href: 'mapas.html' },
      { id: 'prescripciones', ico: '⛗', label: 'Prescripciones', href: 'prescripciones.html' }
    ]},
    { id: 'config', label: 'Configuración', items: [
      { id: 'vehiculo', ico: '🚜', label: 'Vehículo', href: 'vehiculo.html' },
      { id: 'config-implemento', ico: '⚙', label: 'Implemento PilotX', href: 'config-implemento.html' },
      { id: 'herramienta', ico: '⚙', label: 'Implemento', href: 'herramienta.html' },
      { id: 'calibracion-imu', ico: '⟲', label: 'Calibración IMU', href: 'calibracion-imu.html' },
      { id: 'setup',    ico: '🧭', label: 'Asistente', href: 'setup.html' },
      { id: 'sistema',  ico: '🖥', label: 'Sistema',  href: 'sistema.html' },
      // CoreX corre como servicio sin ventana: su config vive en :5181
      // (con link de vuelta "PilotX" en su propio sidebar).
      { id: 'corex',    ico: '⌬',  label: 'CoreX',    href: 'http://127.0.0.1:5181/' }
    ]},
    { id: 'cloud', label: 'Cloud', items: [
      { id: 'orbitx',   ico: '☁',  label: 'OrbitX',   href: 'orbitx.html' },
      { id: 'firmwares', ico: '⬇', label: 'Firmwares', href: 'firmwares.html' },
      { id: 'actualizar', ico: '⤓', label: 'Actualizar', href: 'actualizar.html' },
      { id: 'pwa-qr',   ico: '▣',  label: 'Conectar celular', href: 'pwa-qr.html' }
    ]},
    { id: 'mant', label: 'Mantenimiento', items: [
      { id: 'eventos',  ico: '📜', label: 'Eventos',  href: 'eventos.html' },
      { id: 'debug',    ico: '🐞', label: 'Debug',    href: 'debug.html' }
    ]}
  ];

  // CSS de los grupos inyectado desde acá (capa JS) para no tocar layout.css
  // de Codex. Tokens de theme.css como siempre. En modo icono (<=900px) los
  // headers se ocultan y quedan todos los ítems visibles como antes.
  function injectGroupCss() {
    if (document.getElementById('agpNavGroupsCss')) return;
    var st = document.createElement('style');
    st.id = 'agpNavGroupsCss';
    st.textContent = [
      '.nav-group-head { display:flex; align-items:center; width:100%; min-height:44px;',
      '  padding: var(--agp-sp-1) var(--agp-sp-4); background:transparent; border:0; cursor:pointer;',
      '  color: var(--agp-text-muted); font: inherit; font-size: var(--agp-fs-xs);',
      '  text-transform: uppercase; letter-spacing: 1.2px; font-weight: 700; }',
      '.nav-group-head:hover { color: var(--agp-text); }',
      '.nav-group-head .chev { margin-left:auto; transition: transform 0.15s; font-size: 13px; }',
      '.nav-group.open > .nav-group-head .chev { transform: rotate(90deg); }',
      '.nav-group-head .gdot { margin-left:6px; color: var(--agp-accent); font-size: 10px; display:none; }',
      '.nav-group.has-active > .nav-group-head .gdot { display:inline; }',
      '.nav-sub { list-style:none; margin:0; padding:0; display:none; }',
      '.nav-group.open > .nav-sub { display:block; }',
      '@media (max-width: 900px) {',
      '  .nav-group-head { display:none; }',
      '  .nav-sub { display:block !important; }',
      '}'
    ].join('\n');
    document.head.appendChild(st);
  }

  function iconHtml(id) {
    var src = '../img/icons/existing/agp-' + id + '.png';
    return '<span class="ico"><img src="' + src + '" alt="" aria-hidden="true" loading="lazy" onerror="this.style.display=&quot;none&quot;"></span>';
  }

  function itemHtml(it, active) {
    const isActive = it.id === active;
    const cls = isActive ? ' class="active"' : '';
    const aria = isActive ? ' aria-current="page"' : '';
    return '<li><a' + cls + aria + ' href="' + it.href + '" title="' + it.label + '">' +
      iconHtml(it.id) +
      '<span class="label">' + it.label + '</span>' +
    '</a></li>';
  }

  function groupOpen(g, hasActive) {
    // El grupo de la página activa SIEMPRE arranca abierto (que el operario
    // se vea a sí mismo); el resto respeta lo último que dejó en localStorage.
    if (hasActive) return true;
    try { return localStorage.getItem('agp.nav.g.' + g.id) === '1'; }
    catch (_) { return false; }
  }

  function render(aside) {
    injectGroupCss();
    const active = aside.getAttribute('data-active') || '';
    const html = [
      '<div class="brand">',
      '  <div class="brand-mark">AP</div>',
      '  <div>',
      '    <div class="brand-name">Agro Parallel</div>',
      '    <div class="brand-sub">PilotX · preview</div>',
      '  </div>',
      '</div>',
      '<ul class="nav" aria-label="Navegacion principal">'
    ];
    for (const g of GROUPS) {
      if (!g.id) {
        for (const it of g.items) html.push(itemHtml(it, active));
        continue;
      }
      const hasActive = g.items.some(function (it) { return it.id === active; });
      const open = groupOpen(g, hasActive);
      html.push(
        '<li class="nav-group' + (open ? ' open' : '') + (hasActive ? ' has-active' : '') +
          '" data-group="' + g.id + '">' +
          '<button type="button" class="nav-group-head" aria-expanded="' + open + '">' +
            '<span>' + g.label + '</span><span class="gdot">●</span><span class="chev">▸</span>' +
          '</button>' +
          '<ul class="nav-sub">' + g.items.map(function (it) { return itemHtml(it, active); }).join('') + '</ul>' +
        '</li>'
      );
    }
    html.push('</ul>');
    html.push(
      '<div class="sidebar-foot">',
      '  <div class="foot-meta">Equipo: <strong id="agpFootEquipo" style="color:var(--agp-text)">—</strong></div>',
      '  <div class="foot-meta" id="agpFootCloud" style="margin-top:4px">Cloud: <span style="color:var(--agp-text-muted)">●</span> …</div>',
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

    // Acordeón: tap en el header abre/cierra y persiste la elección.
    aside.querySelectorAll('.nav-group-head').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var li = btn.parentNode;
        var open = !li.classList.contains('open');
        li.classList.toggle('open', open);
        btn.setAttribute('aria-expanded', String(open));
        try { localStorage.setItem('agp.nav.g.' + li.dataset.group, open ? '1' : '0'); }
        catch (_) { /* storage puede no estar (webview restrictivo) */ }
      });
    });

    // Pie con datos reales de OrbitX: establecimiento vinculado + estado cloud
    (function () {
      var elEquipo = aside.querySelector('#agpFootEquipo');
      var elCloud = aside.querySelector('#agpFootCloud');
      function refresh() {
        fetch('/api/orbitx/status').then(function (r) { return r.json(); }).then(function (st) {
          if (elEquipo) {
            elEquipo.textContent = st.estab_slug || st.device_id || 'Sin vincular';
            elEquipo.title = st.device_id || '';
          }
          if (elCloud) {
            elCloud.innerHTML = st.cloud_connected
              ? 'Cloud: <span style="color:var(--agp-state-ok)">●</span> conectado'
              : 'Cloud: <span style="color:var(--agp-text-muted)">●</span> sin conexión';
          }
        }).catch(function () {
          if (elCloud) elCloud.innerHTML = 'Cloud: <span style="color:var(--agp-text-muted)">●</span> sin conexión';
        });
      }
      refresh();
      setInterval(refresh, 15000);
    })();

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
