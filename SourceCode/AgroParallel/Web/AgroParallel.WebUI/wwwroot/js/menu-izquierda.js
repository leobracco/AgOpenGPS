// ============================================================================
// menu-izquierda.js — espejo HTML del menú izquierdo nativo (panelLeft) con
// SUBMENÚS: tocar un ítem con data-sub expande la ventana (resize:WxH al
// host) y muestra sus opciones; tocar una opción manda el comando y colapsa.
// data-cmd = acción directa (Dirección, CoreX). Comandos = contrato con
// FormGPS.ExecuteGuidanceCommand.
// ============================================================================
(function () {
  'use strict';

  var COLLAPSED = { w: 116, h: 560 };
  var EXPANDED = { w: 400, h: 560 };

  // submenús: espejo de los desplegables/paneles nativos de cada ítem
  var SUBMENUS = {
    navegacion: {
      titulo: 'Navegación',
      // como el panel nativo: queda abierto para tocar varias veces
      // (2D↔3D, brillo +/-, inclinar) — se cierra tocando el ítem de nuevo
      mantener: true,
      items: [
        { ico: '▱', label: '2D', cmd: 'v2d' },
        { ico: '⛰', label: '3D', cmd: 'v3d' },
        { ico: '🧭', label: 'Norte 2D', cmd: 'norte2d' },
        { ico: '▦', label: 'Grilla', cmd: 'grilla' },
        { ico: '🌗', label: 'Día / Noche', cmd: 'dia_noche' },
        { ico: '🔆', label: 'Brillo +', cmd: 'brillo_up' },
        { ico: '🔅', label: 'Brillo −', cmd: 'brillo_dn' },
        { ico: '⬢', label: 'Hub', cmd: 'hub' },
        { ico: '⚙', label: 'Panel nativo', cmd: 'navegacion' }
      ]
    },
    config: {
      titulo: 'Config',
      items: [
        { ico: '🚜', label: 'Configuración', cmd: 'config_form' },
        { ico: '🎯', label: 'Dirección', cmd: 'direccion' },
        { ico: '🗒', label: 'Todos los ajustes', cmd: 'todos_ajustes' },
        { ico: '🎨', label: 'Colores', cmd: 'colores' },
        { ico: '🟩', label: 'Colores secciones', cmd: 'colores_sec' },
        { ico: '📡', label: 'Datos GPS', cmd: 'datos_gps' },
        { ico: '➕', label: 'Perfil nuevo', cmd: 'perfil_nuevo' },
        { ico: '📂', label: 'Cargar perfil', cmd: 'perfil_cargar' },
        { ico: '🗂', label: 'Directorios', cmd: 'directorios' },
        { ico: '❓', label: 'Ayuda', cmd: 'ayuda' }
      ]
    },
    herramientas: {
      titulo: 'Herramientas',
      items: [
        { ico: '🪄', label: 'Asistente dirección', cmd: 'asistente_direccion' },
        { ico: '📈', label: 'Gráfico dirección', cmd: 'grafico_direccion' },
        { ico: '🧭', label: 'Gráfico rumbo', cmd: 'grafico_rumbo' },
        { ico: '📉', label: 'Gráfico XTE', cmd: 'grafico_xte' },
        { ico: '⚖', label: 'Chequeo roll', cmd: 'chequeo_roll' },
        { ico: '⬠', label: 'Herram. límites', cmd: 'herr_limites' },
        { ico: '〰', label: 'Suavizar AB', cmd: 'suavizar_ab' },
        { ico: '🗑', label: 'Borrar contornos', cmd: 'borrar_contornos' },
        { ico: '📍', label: 'Corregir posición', cmd: 'corregir_pos' },
        { ico: '📜', label: 'Visor eventos', cmd: 'visor_eventos' },
        { ico: '📷', label: 'Webcam', cmd: 'webcam' }
      ]
    },
    lote: {
      titulo: 'Lote',
      items: [
        { ico: '🗂', label: 'Lote nuevo / abrir', cmd: 'lote_menu' },
        { ico: '📊', label: 'Datos lote', cmd: 'lote_datos' },
        { ico: '🚩', label: 'Bandera', cmd: 'bandera' },
        { ico: '📌', label: 'Bandera lat/lon', cmd: 'bandera_latlon' },
        { ico: '🗑', label: 'Borrar aplicado', cmd: 'borrar_aplicado' },
        { ico: '🎨', label: 'Color mapeo', cmd: 'mapeo_color' }
      ]
    },
    herrlote: {
      titulo: 'Herr. lote',
      items: [
        { ico: '⬠', label: 'Lindero', cmd: 'lindero' },
        { ico: '⛶', label: 'Cabecera', cmd: 'cabecera' },
        { ico: '⛶', label: 'Cabecera avanzada', cmd: 'cabecera_avanzada' },
        { ico: '🔛', label: 'Cabecera SÍ/NO', cmd: 'cabecera_onoff' },
        { ico: '📥', label: 'Importar guías', cmd: 'importar_guias' },
        { ico: '🛤', label: 'Tram crear', cmd: 'tram_crear' },
        { ico: '👁', label: 'Tram vista', cmd: 'tram_vista' }
      ]
    }
  };

  function resizeWidget(w, h) {
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv) wv.postMessage('resize:' + w + 'x' + h);
    } catch (e) { /* fuera de WebView2: no-op */ }
  }

  async function send(cmd, btn) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (btn) {
        btn.classList.add('flash');
        setTimeout(function () { btn.classList.remove('flash'); }, 250);
      }
      if (!data.ok) console.warn('[menu-izq] comando rechazado:', cmd, data.error);
    } catch (e) {
      console.warn('[menu-izq] sin conexión:', e.message);
    }
  }

  var panel = document.getElementById('subPanel');
  var subTitle = document.getElementById('subTitle');
  var subItems = document.getElementById('subItems');
  var abierto = null; // id del submenú abierto

  function cerrarSub() {
    abierto = null;
    panel.hidden = true;
    document.querySelectorAll('#mainCol .mbtn').forEach(function (b) { b.classList.remove('on'); });
    resizeWidget(COLLAPSED.w, COLLAPSED.h);
  }

  function abrirSub(id, btn) {
    var def = SUBMENUS[id];
    if (!def) return;
    abierto = id;
    document.querySelectorAll('#mainCol .mbtn').forEach(function (b) { b.classList.remove('on'); });
    btn.classList.add('on');
    subTitle.textContent = def.titulo;
    subItems.innerHTML = '';
    def.items.forEach(function (it) {
      var d = document.createElement('button');
      d.type = 'button';
      d.className = 'sbtn';
      d.innerHTML = '<span class="ico">' + it.ico + '</span><span>' + it.label + '</span>';
      d.addEventListener('click', function () {
        send(it.cmd, d);
        // submenús "mantener" (Navegación) quedan abiertos para tocar varias
        // veces; el resto colapsa tras el flash (lo nativo ya se abrió)
        if (!def.mantener) setTimeout(cerrarSub, 250);
      });
      subItems.appendChild(d);
    });
    panel.hidden = false;
    resizeWidget(EXPANDED.w, EXPANDED.h);
  }

  document.querySelectorAll('#mainCol .mbtn').forEach(function (b) {
    b.addEventListener('click', function () {
      if (b.dataset.sub) {
        // toggle: tocar el mismo ítem cierra su submenú
        if (abierto === b.dataset.sub) cerrarSub();
        else abrirSub(b.dataset.sub, b);
      } else if (b.dataset.cmd) {
        send(b.dataset.cmd, b);
        if (abierto) cerrarSub();
      }
    });
  });
})();
