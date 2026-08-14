// ============================================================================
// modal.js — Diálogos HTML compartidos del Hub (AgpModal).
//
// WebView2 desactiva window.prompt() y bloquea window.confirm()/alert(),
// así que cualquier página que los use tiene botones muertos en el tractor.
// Este módulo inyecta un modal propio (mismo look que flowx/herramienta) y
// expone:
//   AgpModal.confirm(titulo, mensaje)        → Promise<boolean>
//   AgpModal.text(titulo, mensaje, default)  → Promise<string|null>
//   AgpModal.alert(titulo, mensaje)          → Promise<void>
//
// Estilos en theme.css (.agp-modal-backdrop / .agp-modal). Compatible con el
// teclado virtual (keyboard.js se engancha por focusin al input).
// ============================================================================
(function () {
  'use strict';

  var backdrop, title, msg, input, btnOk, btnCancel;
  var resolver = null;

  function build() {
    if (backdrop) return;
    backdrop = document.createElement('div');
    backdrop.className = 'agp-modal-backdrop';
    backdrop.innerHTML =
      '<div class="agp-modal" role="dialog" aria-modal="true">' +
        '<h2></h2>' +
        '<p class="msg"></p>' +
        '<input type="text" hidden />' +
        '<div class="actions">' +
          '<button type="button" class="btn agp-modal-cancel">Cancelar</button>' +
          '<button type="button" class="btn primary agp-modal-ok">Aceptar</button>' +
        '</div>' +
      '</div>';
    document.body.appendChild(backdrop);
    title = backdrop.querySelector('h2');
    msg = backdrop.querySelector('.msg');
    input = backdrop.querySelector('input');
    btnOk = backdrop.querySelector('.agp-modal-ok');
    btnCancel = backdrop.querySelector('.agp-modal-cancel');

    btnOk.addEventListener('click', function () {
      close(input.hidden ? true : input.value);
    });
    btnCancel.addEventListener('click', function () { close(null); });
    backdrop.addEventListener('click', function (e) {
      if (e.target === backdrop) close(null);
    });
  }

  function close(result) {
    backdrop.classList.remove('show');
    input.hidden = true;
    btnCancel.style.display = '';
    var r = resolver;
    resolver = null;
    if (r) r(result);
  }

  function open(t, m) {
    build();
    // Si había uno abierto, resolverlo como cancelado antes de pisar.
    if (resolver) close(null);
    title.textContent = t || '';
    msg.textContent = m || '';
    backdrop.classList.add('show');
  }

  window.AgpModal = {
    confirm: function (t, m) {
      open(t, m);
      return new Promise(function (res) { resolver = function (v) { res(v === true); }; });
    },
    text: function (t, m, defaultVal) {
      open(t, m);
      input.hidden = false;
      input.value = defaultVal || '';
      setTimeout(function () { try { input.focus(); input.select(); } catch (e) {} }, 50);
      return new Promise(function (res) {
        resolver = function (v) { res(v === null ? null : String(v)); };
      });
    },
    alert: function (t, m) {
      open(t, m);
      btnCancel.style.display = 'none';
      return new Promise(function (res) { resolver = function () { res(); }; });
    }
  };
})();
