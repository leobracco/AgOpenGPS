// ============================================================================
// widget-mode.js — marca la pagina como "abierta desde PilotX", no desde el Hub.
//
// PilotX abre estas mismas paginas como ventana chica sobre el mapa. Ahi la
// navegacion del Hub (la barra lateral) no sirve para nada: el operario no vino
// a navegar, vino a hacer UNA cosa y volver al lote — y encima esa columna se
// come la mitad del ancho de una ventana que queremos lo mas chica posible,
// porque el mapa tiene que seguir viendose.
//
// Va en el <head> y ANTES de theme.css hace efecto: la clase tiene que estar en
// <html> antes del primer pintado, si no se ve la barra un instante y salta.
// sidebar.js despues lee esta misma clase y ni siquiera renderiza la barra.
//
// Se carga como archivo compartido y no como <script> inline copiado en cada
// pagina: ya estaba duplicado en tres (actualizar, camaras, datos-lote) y cada
// copia es un lugar donde la regla se puede desincronizar.
// ============================================================================
// El modo TIENE QUE VIAJAR con la navegacion. El Hub tambien embebe estas
// paginas (config.html las carga en un iframe con ?widget=1), y si desde
// adentro se navega a otra pagina SIN el parametro, la destino se cree
// abierta a pantalla completa y dibuja el sidebar del Hub ADENTRO del iframe:
// se ve el menu dentro del menu, y desde ahi se puede volver a entrar al Hub
// anidado sin fondo. Por eso ademas de marcar la pagina, este archivo propaga
// el parametro a los links internos y ofrece AgpWidget.url() para las
// navegaciones por codigo.
(function () {
  'use strict';
  var activo = location.search.indexOf('widget=1') !== -1;
  if (activo) document.documentElement.classList.add('widget-mode');

  // Fuera del modo widget devuelve la URL intacta, asi el que llama no tiene
  // que preguntar si el modo esta activo.
  function conModo(url) {
    if (!activo || !url) return url;
    if (/^([a-z]+:|\/\/|#)/i.test(url)) return url;   // absoluta, protocolo o ancla
    if (url.indexOf('widget=1') !== -1) return url;
    return url + (url.indexOf('?') < 0 ? '?widget=1' : '&widget=1');
  }
  window.AgpWidget = { activo: activo, url: conModo };
  if (!activo) return;

  // Links internos: se reescriben al vuelo (captura, antes de que navegue).
  document.addEventListener('click', function (ev) {
    var a = ev.target && ev.target.closest ? ev.target.closest('a[href]') : null;
    if (!a || a.target === '_blank') return;
    var href = a.getAttribute('href');
    var nuevo = conModo(href);
    if (nuevo !== href) a.setAttribute('href', nuevo);
  }, true);
})();
