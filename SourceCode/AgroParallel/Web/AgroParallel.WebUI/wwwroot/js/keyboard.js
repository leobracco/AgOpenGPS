// ============================================================================
// keyboard.js — Teclado virtual en pantalla para AgroParallel.
//
// Se autoengancha por focusin a inputs/textarea de la página. No requiere
// inicialización manual: incluir este script + keyboard.css en el <head> y
// el teclado aparece al tocar cualquier campo editable.
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
    [KEY('0'), KEY('.'), { k: 'enter', l: '⏎', c: 'mod enter accent', w: 2 }]
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
    suppressNextFocus: false
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
      '  <button type="button" class="agp-kbd-close" data-action="close" aria-label="Cerrar teclado">▼</button>',
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
  function applyBodyPadding() {
    if (!state.root) return;
    var prev = document.body.getAttribute('data-agp-prev-pb');
    if (prev == null) {
      document.body.setAttribute('data-agp-prev-pb', document.body.style.paddingBottom || '');
    }
    var h = state.root.offsetHeight || 320;
    document.body.style.paddingBottom = (h + 16) + 'px';
  }
  function restoreBodyPadding() {
    var prev = document.body.getAttribute('data-agp-prev-pb');
    if (prev != null) {
      document.body.style.paddingBottom = prev;
      document.body.removeAttribute('data-agp-prev-pb');
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
    // Reservar espacio en el body para que el input pueda quedar centrado
    // realmente, no detrás del teclado.
    applyBodyPadding();
    setTimeout(() => {
      // El render del teclado puede tardar un frame; recalculamos por las dudas.
      applyBodyPadding();
      if (target && target.scrollIntoView) {
        try { target.scrollIntoView({ block: 'center', behavior: 'smooth' }); } catch (_) {}
      }
    }, 50);
  }

  function hide() {
    if (!state.visible) return;
    state.visible = false;
    if (state.root) state.root.classList.remove('open');
    restoreBodyPadding();
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

  // ---------- Init ------------------------------------------------------------
  function init() {
    if (!enabled()) return;
    ensureRoot();
    bindRoot();
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
    isEnabled: enabled
  };
})();
