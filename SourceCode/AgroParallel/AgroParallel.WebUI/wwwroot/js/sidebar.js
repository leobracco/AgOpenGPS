// ============================================================================
// sidebar.js — render del sidebar común en todas las páginas del Hub.
// Uso: <aside class="sidebar" data-active="sistema"></aside><script src="...sidebar.js"></script>
// ============================================================================
(function () {
  'use strict';

  const ITEMS = [
    { id: 'hub',      ico: '▤',  label: 'Hub',      href: 'hub.html' },
    { id: 'piloto',   ico: '◎',  label: 'Piloto',   href: 'piloto.html' },
    { id: 'quantix',  ico: '⟁',  label: 'QuantiX',  href: 'quantix.html' },
    { id: 'vistax',   ico: '◉',  label: 'VistaX',   href: 'vistax.html' },
    { id: 'sectionx', ico: '▦',  label: 'SectionX', href: 'sectionx.html' },
    { id: 'orbitx',   ico: '☁',  label: 'OrbitX',   href: 'orbitx.html' },
    { id: 'camaras',  ico: '⌘',  label: 'Cámaras',  href: 'camaras.html' },
    { id: 'sistema',  ico: '⚙',  label: 'Sistema',  href: 'sistema.html' },
    { id: 'nodos',    ico: '📡', label: 'Nodos',    href: 'nodos.html' },
    { id: 'debug',    ico: '🐞', label: 'Debug',    href: 'debug.html' }
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
      '  <div>Sesión: <strong style="color:var(--agp-text)">Demo</strong></div>',
      '  <div style="margin-top:4px">Cloud: <span style="color:var(--agp-state-ok)">●</span> conectado</div>',
      '</div>'
    );
    aside.innerHTML = html.join('');
  }

  document.querySelectorAll('aside.sidebar').forEach(render);
})();
