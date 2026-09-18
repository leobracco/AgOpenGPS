// ============================================================================
// i18n.js — traducción al vuelo de las páginas del Hub.
//
// Mismo criterio que la pantalla nativa (ver PilotX.Cockpit.Bars/Traductor.cs):
// las páginas están ESCRITAS en castellano y se traducen recorriendo el DOM
// contra wwwroot/idiomas.json, cuya clave es el propio texto castellano.
// No hay que marcar nada con data-i18n ni tocar el markup existente.
//
// Se guarda el texto ORIGINAL de cada nodo la primera vez que se toca, y
// siempre se traduce DESDE el castellano. Sin eso, pasar de inglés a
// portugués buscaría "Headland" en un diccionario que solo conoce "Cabecera".
//
// El MutationObserver es lo que cubre el contenido que arma el JS después de
// cargar (tablas de nodos, listas de surcos, mensajes de estado): se traduce
// solo, sin que cada página tenga que acordarse de llamar a nadie.
//
// Para excluir algo de la traducción: data-no-i18n en el elemento.
// ============================================================================

(function () {
  'use strict';

  var dic = null;             // { "Cabecera": { en: "...", pt: "..." } }
  var idioma = 'es';
  var seqVisto = -1;
  var origen = new WeakMap(); // nodo -> texto castellano original
  var observer = null;

  // Nunca traducir acá adentro: es código, o lo escribió el operario.
  var SALTEAR = { SCRIPT: 1, STYLE: 1, CODE: 1, PRE: 1, TEXTAREA: 1, SVG: 1 };

  function traducible(nodo) {
    for (var p = nodo.parentNode; p; p = p.parentNode) {
      if (p.nodeType !== 1) continue;
      if (SALTEAR[p.tagName]) return false;
      if (p.hasAttribute && p.hasAttribute('data-no-i18n')) return false;
    }
    return true;
  }

  function t(txt) {
    if (!txt || idioma === 'es' || !dic) return txt;
    var clave = txt.trim();
    if (!clave) return txt;
    var e = dic[clave];
    if (!e || !e[idioma]) return txt;
    // Se conservan los espacios/saltos de línea del original: en el markup
    // suelen ser los que separan un texto de un icono al lado.
    var izq = txt.slice(0, txt.length - txt.trimStart().length);
    var der = txt.slice(txt.trimEnd().length);
    return izq + e[idioma] + der;
  }

  // Texto original de un nodo, recordándolo la primera vez.
  function orig(nodo, actual) {
    if (!origen.has(nodo)) origen.set(nodo, actual);
    return origen.get(nodo);
  }

  function traducirNodoTexto(nodo) {
    if (!nodo.nodeValue || !nodo.nodeValue.trim()) return;
    if (!traducible(nodo)) return;
    var o = orig(nodo, nodo.nodeValue);
    var n = t(o);
    if (n !== nodo.nodeValue) nodo.nodeValue = n;
  }

  // Atributos que el operario ve aunque no sean texto del documento.
  var ATRIBUTOS = ['placeholder', 'title', 'aria-label', 'value'];

  function traducirAtributos(el) {
    if (el.hasAttribute && el.hasAttribute('data-no-i18n')) return;
    for (var i = 0; i < ATRIBUTOS.length; i++) {
      var a = ATRIBUTOS[i];
      // value solo en botones: en un input es el dato que cargó el operario,
      // traducirlo sería pisarle lo que escribió.
      if (a === 'value' && !(el.tagName === 'BUTTON' ||
          (el.tagName === 'INPUT' && /^(button|submit|reset)$/i.test(el.type || '')))) continue;
      if (!el.hasAttribute(a)) continue;
      var clave = el.tagName + '::' + a;
      if (!origen.has(el)) origen.set(el, {});
      var guardados = origen.get(el);
      if (typeof guardados !== 'object' || guardados === null) guardados = {};
      if (!(clave in guardados)) { guardados[clave] = el.getAttribute(a); origen.set(el, guardados); }
      var n = t(guardados[clave]);
      if (n !== el.getAttribute(a)) el.setAttribute(a, n);
    }
  }

  function aplicar(raiz) {
    if (!dic) return;
    raiz = raiz || document.body;
    if (!raiz) return;

    try {
      if (raiz.nodeType === 3) { traducirNodoTexto(raiz); return; }

      var w = document.createTreeWalker(raiz, NodeFilter.SHOW_TEXT, null);
      var pendientes = [];
      while (w.nextNode()) pendientes.push(w.currentNode);
      for (var i = 0; i < pendientes.length; i++) traducirNodoTexto(pendientes[i]);

      if (raiz.querySelectorAll) {
        if (raiz.nodeType === 1) traducirAtributos(raiz);
        var els = raiz.querySelectorAll('[placeholder],[title],[aria-label],button[value],input[value]');
        for (var j = 0; j < els.length; j++) traducirAtributos(els[j]);
      }

      // El <title> de la ventana también se ve (barra del diálogo).
      if (raiz === document.body && document.title) {
        if (!origen.has(document)) origen.set(document, document.title);
        document.title = t(origen.get(document));
      }
    } catch (e) { /* traducir nunca puede romper una pantalla */ }
  }

  function observar() {
    if (observer || !window.MutationObserver || !document.body) return;
    observer = new MutationObserver(function (muts) {
      if (!dic || idioma === 'es') return;
      for (var i = 0; i < muts.length; i++) {
        var m = muts[i];
        if (m.type === 'characterData') { traducirNodoTexto(m.target); continue; }
        for (var j = 0; j < m.addedNodes.length; j++) {
          var n = m.addedNodes[j];
          if (n.nodeType === 3) traducirNodoTexto(n);
          else if (n.nodeType === 1) aplicar(n);
        }
      }
    });
    observer.observe(document.body, {
      childList: true, subtree: true, characterData: true
    });
  }

  function cambiar(codigo) {
    if (codigo === idioma) return;
    idioma = codigo;
    // Se retraduce TODO desde los originales guardados: volver a castellano
    // es simplemente t() devolviendo el original.
    if (idioma === 'es') {
      restaurar(document.body);
    } else {
      aplicar(document.body);
    }
  }

  // Volver a castellano = escribir de nuevo los originales guardados.
  function restaurar(raiz) {
    try {
      var w = document.createTreeWalker(raiz, NodeFilter.SHOW_TEXT, null);
      var nodos = [];
      while (w.nextNode()) nodos.push(w.currentNode);
      for (var i = 0; i < nodos.length; i++) {
        var n = nodos[i];
        if (origen.has(n)) n.nodeValue = origen.get(n);
      }
      var els = raiz.querySelectorAll('[placeholder],[title],[aria-label],button[value],input[value]');
      for (var j = 0; j < els.length; j++) {
        var el = els[j], g = origen.get(el);
        if (!g || typeof g !== 'object') continue;
        for (var k in g) {
          var attr = k.split('::')[1];
          if (attr) el.setAttribute(attr, g[k]);
        }
      }
      if (origen.has(document)) document.title = origen.get(document);
    } catch (e) { }
  }

  // ------------------------------------------------------------ arranque

  async function init() {
    try {
      var r = await fetch('/api/idioma', { cache: 'no-store' });
      var j = await r.json();
      idioma = j.idioma || 'es';
      seqVisto = j.seq || 0;
    } catch (e) { idioma = 'es'; }

    if (idioma !== 'es') {
      try { dic = await (await fetch('/idiomas.json', { cache: 'force-cache' })).json(); }
      catch (e) { dic = null; }
    }

    if (document.body) { aplicar(document.body); observar(); }
    else document.addEventListener('DOMContentLoaded', function () { aplicar(document.body); observar(); });

    // Un cambio hecho en OTRA ventana (el menú SISTEMA de la pantalla) tiene
    // que llegar acá: si no, quedaba media app en cada idioma.
    setInterval(async function () {
      try {
        var r = await fetch('/api/idioma', { cache: 'no-store' });
        var j = await r.json();
        if ((j.seq || 0) === seqVisto) return;
        seqVisto = j.seq || 0;
        if (!dic && (j.idioma || 'es') !== 'es') {
          try { dic = await (await fetch('/idiomas.json', { cache: 'force-cache' })).json(); }
          catch (e) { }
        }
        cambiar(j.idioma || 'es');
      } catch (e) { }
    }, 2000);
  }

  window.PilotXI18N = {
    t: t,
    aplicar: aplicar,
    get idioma() { return idioma; },
    // Para el selector: guarda y aplica en el acto, sin esperar el sondeo.
    elegir: async function (codigo) {
      try {
        await fetch('/api/idioma', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ idioma: codigo })
        });
      } catch (e) { }
      if (!dic && codigo !== 'es') {
        try { dic = await (await fetch('/idiomas.json', { cache: 'force-cache' })).json(); }
        catch (e) { }
      }
      cambiar(codigo);
    }
  };

  init();
})();
