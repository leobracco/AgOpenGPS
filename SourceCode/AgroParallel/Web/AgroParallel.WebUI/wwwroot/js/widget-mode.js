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
(function () {
  'use strict';
  if (location.search.indexOf('widget=1') !== -1) {
    document.documentElement.classList.add('widget-mode');
  }
})();
