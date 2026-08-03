// ============================================================================
// keyboard.js — Teclado virtual en pantalla para AgroParallel.
//
// Se autoengancha por focusin a inputs/textarea de la página. No requiere
// inicialización manual: incluir este script + keyboard.css en el <head> y
// el teclado aparece al tocar cualquier campo editable.
//
// BONUS anti page-zoom (2026-07-31): este archivo lo cargan ~47 páginas del
// Hub, así que también mata acá el ZOOM DE PÁGINA del WebView (ctrl+rueda y
// pinch del navegador). En cabina el pinch tiene que zoomear EL MAPA de la
// página (canvas con su propio handler), no agrandar toda la pantalla — el
// operario quedaba con la página gigante sin forma obvia de volver.
(function () {
  'use strict';
  // ctrl+rueda = zoom del navegador → bloqueado (la rueda sola sigue siendo
  // scroll/zoom-de-canvas según la página).
  document.addEventListener('wheel', function (ev) {
    if (ev.ctrlKey) ev.preventDefault();
  }, { passive: false });
  // ctrl +/- y ctrl+0 (por si hay teclado físico conectado en el taller).
  document.addEventListener('keydown', function (ev) {
    if (ev.ctrlKey && (ev.key === '+' || ev.key === '-' || ev.key === '=' || ev.key === '0'))
      ev.preventDefault();
  });
  // Viewport: pinch de página deshabilitado. Se inyecta acá para no tocar
  // los 47 <meta> uno por uno; si la página ya trae maximum-scale, se pisa
  // con el mismo valor y no cambia nada.
  var meta = document.querySelector('meta[name="viewport"]');
  if (!meta) {
    meta = document.createElement('meta');
    meta.name = 'viewport';
    document.head.appendChild(meta);
  }
  meta.content = 'width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no';
})();
//
// Layouts: qwerty-es (con ñ y acentos por long-press), numeric, symbols.
// Tipos de input → layout sugerido:
//   type="number" / inputmode="numeric"   → numeric
//   type="tel"                            → numeric
//   type="email"                          → qwerty
//   default                               → qwerty
//
// Opt-out:
//   <input data-no-keyboard>              → este input nunca abre el teclado
//   localStorage['agp_keyboard_enabled']='0' → desactiva el teclado global
//
// Eventos disparados al input:
//   - "input" después de cada cambio de valor
//   - "change" al cerrar el teclado (Enter o ▼)
// ============================================================================
(function () {
  'use strict';

  if (window.AgpKeyboard) return; // ya cargado

  // ---------- Layouts ---------------------------------------------------------
  // Cada tecla es { k: texto a insertar, c: clase extra, l: label visible,
  //                 w: ancho relativo (1=normal), s: shift-variant, a: acentos }
  const KEY = (k, opts) => Object.assign({ k: k, l: k }, opts || {});

  const ROWS_QWERTY = [
    [
      KEY('1'), KEY('2'), KEY('3'), KEY('4'), KEY('5'),
      KEY('6'), KEY('7'), KEY('8'), KEY('9'), KEY('0')
    ],
    [
      KEY('q'), KEY('w'), KEY('e', { a: 'éè€' }), KEY('r'), KEY('t'),
      KEY('y'), KEY('u', { a: 'üú' }), KEY('i', { a: 'í' }), KEY('o', { a: 'óö' }), KEY('p')
    ],
    [
      KEY('a', { a: 'áà@' }), KEY('s'), KEY('d'), KEY('f'), KEY('g'),
      KEY('h'), KEY('j'), KEY('k'), KEY('l'), KEY('ñ')
    ],
    [
      { k: 'shift', l: '⇧', c: 'mod shift', w: 1.5 },
      KEY('z'), KEY('x'), KEY('c'), KEY('v'), KEY('b'), KEY('n', { a: 'ñ' }),
      KEY('m'), KEY(',', { a: ';' }), KEY('.', { a: '?¿' }),
      { k: 'back', l: '⌫', c: 'mod back', w: 1.5 }
    ],
    [
      { k: 'layout', l: '123', c: 'mod layout-toggle', w: 1.4 },
      KEY('-'), KEY('_'),
      { k: ' ', l: 'espacio', c: 'space', w: 5 },
      KEY('@'), KEY('.'),
      { k: 'enter', l: '⏎', c: 'mod enter accent', w: 1.6 }
    ]
  ];

  const ROWS_NUMERIC = [
    [KEY('7'), KEY('8'), KEY('9'), { k: 'back', l: '⌫', c: 'mod back' }],
    [KEY('4'), KEY('5'), KEY('6'), KEY('-')],
    [KEY('1'), KEY('2'), KEY('3'), KEY(',')],
    [{ k: 'layout', l: 'ABC', c: 'mod layout-toggle' }, KEY('0'), KEY('.'),
     { k: 'enter', l: '⏎', c: 'mod enter accent' }]
  ];

  const ROWS_SYMBOLS = [
    [
      KEY('1'), KEY('2'), KEY('3'), KEY('4'), KEY('5'),
      KEY('6'), KEY('7'), KEY('8'), KEY('9'), KEY('0')
    ],
    [
      KEY('!'), KEY('@'), KEY('#'), KEY('$'), KEY('%'),
      KEY('&'), KEY('*'), KEY('('), KEY(')'), KEY('=')
    ],
    [
      KEY('-'), KEY('_'), KEY('+'), KEY('/'), KEY('\\'),
      KEY(':'), KEY(';'), KEY('"'), KEY('\''), KEY('?')
    ],
    [
      { k: 'layout', l: 'ABC', c: 'mod layout-toggle', w: 1.4 },
      KEY('¿'), KEY('¡'), KEY('<'), KEY('>'), KEY('['), KEY(']'), KEY('{'), KEY('}'),
      { k: 'back', l: '⌫', c: 'mod back', w: 1.4 }
    ],
    [
      KEY(','), KEY('.'),
      { k: ' ', l: 'espacio', c: 'space', w: 6 },
      KEY('|'), KEY('~'),
      { k: 'enter', l: '⏎', c: 'mod enter accent', w: 1.6 }
    ]
  ];

  // ---------- Estado ----------------------------------------------------------
  const state = {
    root: null,
    visible: false,
    target: null,        // input/textarea con foco
    layout: 'qwerty',    // 'qwerty' | 'numeric' | 'symbols'
    shift: false,
    capsLock: false,
    lastShiftAt: 0,      // para detectar doble tap → caps lock
    longPressTimer: null,
    longPressKey: null,
    suppressNextFocus: false,
    // Contenedor al que se le agregó espacio abajo para que el teclado no tape
    // el campo, y su padding original para devolvérselo al cerrar.
    padEl: null,
    padPrev: ''
  };

  function enabled() {
    try {
      return localStorage.getItem('agp_keyboard_enabled') !== '0';
    } catch (_) {
      return true;
    }
  }

  // ---------- Render ----------------------------------------------------------
  function ensureRoot() {
    if (state.root) return state.root;
    const r = document.createElement('div');
    r.className = 'agp-kbd';
    r.setAttribute('role', 'dialog');
    r.setAttribute('aria-label', 'Teclado en pantalla');
    document.body.appendChild(r);
    // Evita que el foco se vaya al tocar las teclas. En desktop el mousedown
    // fira directo; en touch el mousedown que disparamos acá es el SINTÉTICO
    // que el browser genera después del touchstart, y su preventDefault sigue
    // siendo válido para impedir el focus-shift.
    //
    // NO preventeás touchstart acá — si lo hacés, el browser no genera el
    // click sintético en touch y el handler de teclas (que escucha 'click'
    // más abajo) nunca corre. Bug del teclado táctil observado 2026-05-19:
    // las teclas no insertaban nada en la pantalla del tractor. El control
    // de scroll/zoom lo hace touch-action: manipulation en keyboard.css.
    r.addEventListener('mousedown', (e) => { e.preventDefault(); });
    state.root = r;
    return r;
  }

  function pickRows() {
    if (state.layout === 'numeric') return ROWS_NUMERIC;
    if (state.layout === 'symbols') return ROWS_SYMBOLS;
    return ROWS_QWERTY;
  }

  function render() {
    const r = ensureRoot();
    const rows = pickRows();
    const html = [
      '<div class="agp-kbd-bar">',
      '  <span class="agp-kbd-hint">' + (state.target && state.target.placeholder ? esc(state.target.placeholder) : 'Teclado AgroParallel') + '</span>',
      '  <span class="agp-kbd-acciones">',
      '    <button type="button" class="agp-kbd-reset" data-action="reset-pos" title="Volver el teclado abajo" aria-label="Volver el teclado abajo">⇩</button>',
      '    <button type="button" class="agp-kbd-close" data-action="close" aria-label="Cerrar teclado">▼</button>',
      '  </span>',
      '</div>',
      '<div class="agp-kbd-grid agp-kbd-' + state.layout + (state.shift ? ' agp-kbd-shift' : '') + (state.capsLock ? ' agp-kbd-caps' : '') + '">'
    ];
    for (let i = 0; i < rows.length; i++) {
      html.push('<div class="agp-kbd-row">');
      for (const key of rows[i]) {
        const cls = 'agp-key' + (key.c ? ' ' + key.c : '');
        const style = key.w ? ' style="flex:' + key.w + '"' : '';
        const lbl = renderKeyLabel(key);
        const data = key.k === ' ' ? ' ' : (key.k || '');
        const acc = key.a ? ' data-accents="' + esc(key.a) + '"' : '';
        html.push('<button type="button" class="' + cls + '"' + style +
          ' data-key="' + esc(data) + '"' + acc + '>' + lbl + '</button>');
      }
      html.push('</div>');
    }
    html.push('</div>');
    r.innerHTML = html.join('');
  }

  function renderKeyLabel(key) {
    if (!key.k || key.k.length !== 1) return esc(key.l || '');
    if (state.layout === 'qwerty') {
      const shifted = state.shift || state.capsLock;
      return esc(shifted ? key.k.toUpperCase() : key.k);
    }
    return esc(key.l || key.k);
  }

  function esc(s) {
    return String(s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // ---------- Mostrar/ocultar ------------------------------------------------
  // Padding inyectado en el <body> mientras el teclado está abierto.
  // Sin esto, el teclado tapa ~330px de la mitad inferior de cualquier
  // página del Hub y los inputs no se pueden centrar realmente con
  // scrollIntoView porque la página no tiene espacio para scrollear.
  // ---------- Hacerle lugar al teclado ----------------------------------------
  // El teclado vive en la misma ventana, así que SIEMPRE va a estar encima de
  // algo: moverlo sólo cambia qué tapa. Lo que lo arregla de verdad es que el
  // contenido le haga lugar, como en el celular — se le agrega abajo el alto
  // que el teclado le come, y con eso el campo que se está editando siempre
  // puede quedar a la vista.
  //
  // Antes esto se hacía sobre <body>, y en las pantallas del Hub no servía de
  // nada: ahí el que scrollea es #main (overflow-y:auto), no el body, así que
  // el padding no lo veía nadie y el campo seguía tapado.
  function contenedorScrollable(el) {
    var n = el && el.parentElement;
    while (n && n !== document.body && n !== document.documentElement) {
      var s = window.getComputedStyle(n);
      // Alcanza con que PUEDA scrollear por CSS: no se le exige tener scroll
      // ya, porque justamente el espacio que le agregamos es lo que se lo da.
      // (Exigirlo mandaba el padding al <body>, que en el Hub no scrollea y
      // por eso el campo seguía tapado.)
      if (/(auto|scroll)/.test(s.overflowY)) return n;
      n = n.parentElement;
    }
    return null;   // no hay: scrollea el documento
  }

  // Dónde está (o va a estar) el teclado, SIN preguntarle al rect: mientras
  // corre la animación de apertura el transform devuelve una posición
  // intermedia, y con eso el cálculo del espacio daba cualquier cosa (llegó a
  // reservar el alto entero de la ventana y a mandar el campo fuera de vista).
  function cajaTeclado() {
    var r = state.root;
    if (!r) return null;
    var h = r.offsetHeight || 320;
    if (r.classList.contains('flotante')) {
      var y = parseFloat(r.style.getPropertyValue('--kbd-y')) || 0;
      return { top: y, bottom: y + h, height: h };
    }
    return { top: window.innerHeight - h, bottom: window.innerHeight, height: h };
  }
  function aplicarEspacio() {
    if (!state.root) return;
    var cont = contenedorScrollable(state.target) || document.body;
    var kb = cajaTeclado();
    if (!kb) return;
    // Cuánto del contenedor queda debajo del teclado: eso es lo que hay que
    // compensar. Con el teclado movido a un costado o arriba, el solape es
    // menor (o cero) y no se reserva de más.
    var abajo = (cont === document.body)
      ? window.innerHeight
      : cont.getBoundingClientRect().bottom;
    var solape = Math.max(0, abajo - kb.top);
    if (solape <= 0) { quitarEspacio(); return; }

    if (state.padEl && state.padEl !== cont) quitarEspacio();
    if (state.padEl !== cont) {
      state.padEl = cont;
      state.padPrev = cont.style.paddingBottom || '';
    }
    cont.style.paddingBottom = (solape + 16) + 'px';
  }

  function quitarEspacio() {
    if (!state.padEl) return;
    state.padEl.style.paddingBottom = state.padPrev || '';
    state.padEl = null;
    state.padPrev = '';
  }

  // Deja el campo en la franja que el teclado NO tapa. scrollIntoView('center')
  // no alcanza: el centro de la ventana puede caer justo detrás del teclado.
  function traerAlaVista(target) {
    if (!target || !state.root) return;
    var kb = cajaTeclado();
    if (!kb) return;
    var r = target.getBoundingClientRect();
    var margen = 12;
    // Franja util: de arriba de todo hasta donde empieza el teclado (o desde
    // donde termina, si el teclado quedó pegado arriba).
    var libreTop = (kb.top <= 4) ? kb.bottom + margen : 0;
    var libreBot = (kb.top <= 4) ? window.innerHeight : kb.top - margen;
    if (r.top >= libreTop && r.bottom <= libreBot) return;   // ya se ve

    var cont = contenedorScrollable(target);
    var delta = (r.top < libreTop) ? (r.top - libreTop) : (r.bottom - libreBot);
    try {
      if (cont) cont.scrollBy({ top: delta, behavior: 'smooth' });
      else window.scrollBy({ top: delta, behavior: 'smooth' });
    } catch (_) {
      if (cont) cont.scrollTop += delta; else window.scrollBy(0, delta);
    }
  }

  function show(target) {
    state.target = target;
    state.layout = layoutFor(target);
    state.shift = autoCapital(target);
    state.capsLock = false;
    render();
    state.root.classList.add('open');
    state.visible = true;
    // El contenido le hace lugar al teclado y el campo se trae a la franja
    // que queda libre: si no, el operario escribe a ciegas.
    aplicarEspacio();
    setTimeout(() => {
      // El render del teclado puede tardar un frame: recalcular con el alto real.
      aplicarEspacio();
      traerAlaVista(target);
    }, 50);
  }

  function hide() {
    if (!state.visible) return;
    state.visible = false;
    if (state.root) state.root.classList.remove('open');
    quitarEspacio();
    if (state.target) {
      try { state.target.dispatchEvent(new Event('change', { bubbles: true })); } catch (_) {}
      state.target = null;
    }
  }

  function layoutFor(el) {
    if (!el) return 'qwerty';
    const t = (el.getAttribute('type') || '').toLowerCase();
    const im = (el.getAttribute('inputmode') || '').toLowerCase();
    if (t === 'number' || t === 'tel' || im === 'numeric' || im === 'decimal' || im === 'tel') return 'numeric';
    return 'qwerty';
  }

  function autoCapital(el) {
    if (!el || el.tagName !== 'TEXTAREA') return false;
    const v = el.value || '';
    return v.length === 0 || /[\.!\?]\s*$/.test(v);
  }

  // ---------- Inserción de texto ---------------------------------------------
  function insertText(text) {
    const el = state.target;
    if (!el) return;
    if (el.disabled || el.readOnly) return;

    let start, end;
    try {
      start = el.selectionStart != null ? el.selectionStart : el.value.length;
      end = el.selectionEnd != null ? el.selectionEnd : el.value.length;
    } catch (_) {
      // Inputs tipo "number" no soportan selectionStart → append al final
      start = end = (el.value || '').length;
    }
    const before = (el.value || '').slice(0, start);
    const after = (el.value || '').slice(end);
    const next = before + text + after;
    try { setNativeValue(el, next); } catch (_) { el.value = next; }
    try {
      const pos = start + text.length;
      el.setSelectionRange(pos, pos);
    } catch (_) {}
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }

  function backspace() {
    const el = state.target;
    if (!el) return;
    if (el.disabled || el.readOnly) return;
    let start, end;
    try {
      start = el.selectionStart != null ? el.selectionStart : el.value.length;
      end = el.selectionEnd != null ? el.selectionEnd : el.value.length;
    } catch (_) {
      start = end = (el.value || '').length;
    }
    if (start === end) {
      if (start === 0) return;
      start = start - 1;
    }
    const next = (el.value || '').slice(0, start) + (el.value || '').slice(end);
    try { setNativeValue(el, next); } catch (_) { el.value = next; }
    try { el.setSelectionRange(start, start); } catch (_) {}
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }

  // Trampolín para frameworks tipo React que sobreescriben el setter de value.
  function setNativeValue(el, value) {
    const proto = el.tagName === 'TEXTAREA'
      ? window.HTMLTextAreaElement && window.HTMLTextAreaElement.prototype
      : window.HTMLInputElement && window.HTMLInputElement.prototype;
    const desc = proto && Object.getOwnPropertyDescriptor(proto, 'value');
    if (desc && desc.set) desc.set.call(el, value);
    else el.value = value;
  }

  // ---------- Handling de teclas ---------------------------------------------
  function onKeyTap(btn) {
    // El botón ▼ de cerrar usa data-action="close"; el resto de teclas usan
    // data-key. Aceptamos ambos acá para que el tap del ▼ realmente dispare
    // hide() (antes salía null en data-key y nunca llegaba al case 'close').
    const k = btn.getAttribute('data-key') || btn.getAttribute('data-action');
    if (k == null) return;
    switch (k) {
      case 'shift': toggleShift(); return;
      case 'back': backspace(); return;
      case 'enter': return enterKey();
      case 'layout': return cycleLayout();
      case 'close': return hide();
      default:
        const ch = (state.layout === 'qwerty' && (state.shift || state.capsLock) && k.length === 1)
          ? k.toUpperCase() : k;
        insertText(ch);
        if (state.shift && !state.capsLock) {
          state.shift = false;
          render();
        }
    }
  }

  function toggleShift() {
    const now = Date.now();
    if (now - state.lastShiftAt < 400) {
      state.capsLock = !state.capsLock;
      state.shift = state.capsLock;
    } else {
      state.shift = !state.shift;
      if (!state.shift) state.capsLock = false;
    }
    state.lastShiftAt = now;
    render();
  }

  function cycleLayout() {
    if (state.layout === 'qwerty') state.layout = 'symbols';
    else if (state.layout === 'symbols') state.layout = 'numeric';
    else state.layout = 'qwerty';
    state.shift = false;
    state.capsLock = false;
    render();
  }

  function enterKey() {
    const el = state.target;
    if (!el) return;
    if (el.tagName === 'TEXTAREA') {
      insertText('\n');
      return;
    }
    // Form submit si está dentro de un <form>
    if (el.form) {
      try {
        const evt = new Event('submit', { bubbles: true, cancelable: true });
        el.form.dispatchEvent(evt);
      } catch (_) {}
    }
    hide();
  }

  // ---------- Long-press para acentos ----------------------------------------
  function startLongPress(btn) {
    const accents = btn.getAttribute('data-accents');
    if (!accents) return;
    cancelLongPress();
    state.longPressKey = btn;
    state.longPressTimer = setTimeout(() => {
      showAccents(btn, accents);
    }, 350);
  }

  function cancelLongPress() {
    if (state.longPressTimer) {
      clearTimeout(state.longPressTimer);
      state.longPressTimer = null;
    }
    state.longPressKey = null;
  }

  function showAccents(btn, accents) {
    closeAccents();
    const base = btn.getAttribute('data-key') || '';
    const variants = (state.shift || state.capsLock)
      ? accents.toUpperCase().split('')
      : accents.split('');
    if (!variants.length) return;
    const pop = document.createElement('div');
    pop.className = 'agp-kbd-accents';
    for (const v of variants) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'agp-key agp-key-accent';
      b.setAttribute('data-key', v);
      b.textContent = v;
      pop.appendChild(b);
    }
    // Mismo razonamiento que en ensureRoot(): touchstart.preventDefault mata
    // el click sintético en touch. Solo preventeamos el mousedown sintético
    // para que no se vaya el foco del input.
    pop.addEventListener('mousedown', (e) => e.preventDefault());
    pop.addEventListener('click', (e) => {
      const t = e.target.closest('.agp-key-accent');
      if (!t) return;
      insertText(t.getAttribute('data-key') || '');
      closeAccents();
    });
    const rect = btn.getBoundingClientRect();
    pop.style.left = Math.max(8, rect.left) + 'px';
    pop.style.top = (rect.top - 8 - 56) + 'px';
    document.body.appendChild(pop);
    state.accentsEl = pop;
    // No insertes la tecla base mientras esté el popup abierto
    state.longPressActivated = true;
  }

  function closeAccents() {
    if (state.accentsEl) {
      try { state.accentsEl.remove(); } catch (_) {}
      state.accentsEl = null;
    }
    state.longPressActivated = false;
  }

  // ---------- Eventos globales ------------------------------------------------
  function isEditable(el) {
    if (!el || el.disabled || el.readOnly) return false;
    if (el.hasAttribute && el.hasAttribute('data-no-keyboard')) return false;
    const tag = el.tagName;
    if (tag === 'TEXTAREA') return true;
    if (tag !== 'INPUT') return false;
    const t = (el.getAttribute('type') || 'text').toLowerCase();
    return ['text', 'number', 'tel', 'email', 'password', 'search', 'url'].indexOf(t) >= 0;
  }

  function onFocusIn(e) {
    if (!enabled()) return;
    const el = e.target;
    if (isEditable(el)) show(el);
    else if (state.visible && !el.closest('.agp-kbd') && !el.closest('.agp-kbd-accents')) hide();
  }

  function onPointerDown(e) {
    if (!state.visible) return;
    const t = e.target;
    if (t.closest('.agp-kbd') || t.closest('.agp-kbd-accents')) return;
    if (isEditable(t)) return; // se va a manejar por focusin
    hide();
  }

  function bindRoot() {
    const r = ensureRoot();
    r.addEventListener('click', (e) => {
      if (state.longPressActivated) {
        // El long-press abrió un popup; el click base no inserta
        return;
      }
      // El botón de reposición no escribe nada: mueve el teclado y sale.
      if (e.target.closest('.agp-kbd-reset')) { resetPos(); return; }
      const btn = e.target.closest('.agp-key, .agp-kbd-close');
      if (!btn) return;
      const k = btn.getAttribute('data-key') || btn.getAttribute('data-action');
      if (k == null) return;
      onKeyTap(btn);
    });
    // Long-press en .agp-key
    r.addEventListener('pointerdown', (e) => {
      const btn = e.target.closest('.agp-key');
      if (btn) startLongPress(btn);
    });
    r.addEventListener('pointerup', cancelLongPress);
    r.addEventListener('pointerleave', cancelLongPress);
    r.addEventListener('pointercancel', cancelLongPress);
  }


  // ---------- Arrastre ---------------------------------------------------------
  // El teclado nace pegado abajo y de ancho completo, y ahí SIEMPRE tapa algo:
  // en unas pantallas los botones de guardar, en otras el campo que se está
  // editando. Agarrándolo de la barra de arriba se lo puede correr a donde
  // moleste menos, y la posición queda guardada para la próxima.
  var POS_KEY = 'agp_kbd_pos';
  var drag = null;

  function guardarPos(x, y, w) {
    try { localStorage.setItem(POS_KEY, JSON.stringify({ x: x, y: y, w: w })); } catch (_) {}
  }
  function leerPos() {
    try { return JSON.parse(localStorage.getItem(POS_KEY) || 'null'); } catch (_) { return null; }
  }

  // Nunca dejar el teclado fuera de la pantalla: si queda un borde afuera no
  // hay forma de volver a agarrarlo. Se deja siempre visible la barra de arriba.
  function acotar(x, y, w, h) {
    var maxX = Math.max(0, window.innerWidth - w);
    var maxY = Math.max(0, window.innerHeight - Math.min(h, 60));
    return { x: Math.min(Math.max(0, x), maxX), y: Math.min(Math.max(0, y), maxY) };
  }

  function aplicarPos(x, y, w) {
    var r = state.root;
    if (!r) return;
    r.classList.add('flotante');
    r.style.setProperty('--kbd-x', x + 'px');
    r.style.setProperty('--kbd-y', y + 'px');
    r.style.setProperty('--kbd-w', w + 'px');
  }

  // Vuelve al lugar de fábrica: abajo, ancho completo.
  function resetPos() {
    var r = state.root;
    if (!r) return;
    r.classList.remove('flotante');
    r.style.removeProperty('--kbd-x');
    r.style.removeProperty('--kbd-y');
    r.style.removeProperty('--kbd-w');
    try { localStorage.removeItem(POS_KEY); } catch (_) {}
  }

  // Restaura la posición elegida por el operario, acotada a la ventana de ahora
  // (la guardada puede ser de una resolución distinta).
  function restaurarPos() {
    var p = leerPos();
    var r = state.root;
    if (!p || !r) return;
    var w = Math.min(p.w || r.offsetWidth, window.innerWidth);
    var c = acotar(p.x, p.y, w, r.offsetHeight);
    aplicarPos(c.x, c.y, w);
  }

  function onDragStart(e) {
    var r = state.root;
    if (!r) return;
    // El asa es la barra de arriba, pero no sus botones.
    var bar = e.target.closest ? e.target.closest('.agp-kbd-bar') : null;
    if (!bar || e.target.closest('button')) return;
    var caja = r.getBoundingClientRect();
    // Al despegarse se achica: de ancho completo no se lo puede correr a un
    // costado y seguiría tapando toda la franja. Con 720 px (o el 85% de la
    // pantalla si es más chica) queda lugar para ver lo que está al lado.
    var wNueva = Math.min(caja.width, 720, Math.round(window.innerWidth * 0.85));
    // El punto de agarre se reescala para que el teclado no salte bajo el dedo.
    var dx = (e.clientX - caja.left) * (wNueva / caja.width);
    drag = { dx: dx, dy: e.clientY - caja.top, w: wNueva };
    aplicarPos(e.clientX - dx, caja.top, wNueva);
    r.classList.add('arrastrando');
    try { bar.setPointerCapture(e.pointerId); } catch (_) {}
    e.preventDefault();
  }

  function onDragMove(e) {
    if (!drag || !state.root) return;
    var r = state.root;
    var c = acotar(e.clientX - drag.dx, e.clientY - drag.dy, drag.w, r.offsetHeight);
    aplicarPos(c.x, c.y, drag.w);
    e.preventDefault();
  }

  function onDragEnd() {
    if (!drag || !state.root) return;
    var caja = state.root.getBoundingClientRect();
    guardarPos(caja.left, caja.top, caja.width);
    state.root.classList.remove('arrastrando');
    drag = null;
  }

  function bindDrag() {
    var r = ensureRoot();
    r.addEventListener('pointerdown', onDragStart);
    r.addEventListener('pointermove', onDragMove);
    r.addEventListener('pointerup', onDragEnd);
    r.addEventListener('pointercancel', onDragEnd);
    // Si cambia el tamaño de la ventana, volver a acotar (si no, el teclado
    // puede quedar fuera de la pantalla y sin forma de recuperarlo).
    window.addEventListener('resize', function () {
      if (state.root && state.root.classList.contains('flotante')) restaurarPos();
    });
  }

  // ---------- Init ------------------------------------------------------------
  function init() {
    if (!enabled()) return;
    ensureRoot();
    bindRoot();
    bindDrag();
    restaurarPos();
    document.addEventListener('focusin', onFocusIn, true);
    document.addEventListener('mousedown', onPointerDown, true);
    document.addEventListener('touchstart', onPointerDown, { capture: true, passive: true });
    document.addEventListener('keydown', (e) => {
      // Si llega un evento de teclado físico, asumimos PC con teclado y ocultamos.
      if (e.key === 'Escape' && state.visible) hide();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  // API pública mínima.
  window.AgpKeyboard = {
    show: show,
    hide: hide,
    enable: function () { try { localStorage.setItem('agp_keyboard_enabled', '1'); } catch (_) {} init(); },
    disable: function () { try { localStorage.setItem('agp_keyboard_enabled', '0'); } catch (_) {} hide(); },
    isEnabled: enabled,
    // Devuelve el teclado a su lugar de fábrica (abajo, ancho completo).
    resetPosicion: resetPos
  };
})();
