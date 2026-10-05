// ============================================================================
// menu-izquierda.js — espejo HTML del menú izquierdo nativo (panelLeft) con
// SUBMENÚS: tocar un ítem con data-sub expande la ventana (resize:WxH al
// host) y muestra sus opciones; tocar una opción manda el comando y colapsa.
// data-cmd = acción directa (Dirección, CoreX). Comandos = contrato con
// FormGPS.ExecuteGuidanceCommand.
// ============================================================================
(function () {
  'use strict';

  var COLLAPSED = { w: 94, h: 560 };
  var EXPANDED = { w: 384, h: 560 };

  // submenús: espejo EXACTO de los desplegables nativos de AOG 6.8.5, con sus
  // iconos verdaderos (img = PNG real en ../img/menu/) y el mismo orden/etiquetas.
  var SUBMENUS = {
    // panelNavigation de AOG (cámara/vista). mantener:true = queda abierto.
    navegacion: {
      titulo: 'Navegación',
      mantener: true,
      items: [
        { img: 'Camera2D64.png', label: '2D', cmd: 'v2d' },
        { img: 'Camera3D64.png', label: '3D', cmd: 'v3d' },
        { img: 'CameraNorth2D.png', label: 'Norte 2D', cmd: 'norte2d' },
        { img: 'TiltUp.png', label: 'Inclinar +', cmd: 'tilt_up' },
        { img: 'TiltDown.png', label: 'Inclinar −', cmd: 'tilt_dn' },
        { img: 'GridRotate.png', label: 'Grilla', cmd: 'grilla' },
        { img: 'WindowNightMode.png', label: 'Día / Noche', cmd: 'dia_noche' },
        { img: 'BrightnessUp.png', label: 'Brillo +', cmd: 'brillo_up' },
        { img: 'BrightnessDn.png', label: 'Brillo −', cmd: 'brillo_dn' }
      ]
    },
    // dropdown "Settings" (toolStripDropDownButton1) de AOG
    config: {
      titulo: 'Configuración',
      items: [
        { img: 'Settings48.png', label: 'Configuración', cmd: 'config_form' },
        { img: 'AutoSteerOff.png', label: 'Auto Steer', cmd: 'direccion' },
        { img: 'ScreenShot.png', label: 'Ver todos los ajustes', cmd: 'todos_ajustes' },
        { img: 'FileOpen.png', label: 'Directorios', cmd: 'directorios' },
        { img: 'GPSQuality.png', label: 'Datos GPS', cmd: 'datos_gps' },
        { img: 'ColourPick.png', label: 'Colores', cmd: 'colores' },
        { img: 'SectionMapping.png', label: 'Colores secciones', cmd: 'colores_sec' },
        { img: 'ConD_KeyBoard.png', label: 'Atajos', cmd: 'hotkeys' }
      ]
    },
    // dropdown "Tools" (toolStripDropDownButton4) de AOG (Wizards/Charts aplanados)
    herramientas: {
      titulo: 'Herramientas',
      items: [
        { img: 'AutoSteerOn.png', label: 'Asist. dirección', cmd: 'asistente_direccion' },
        { img: 'AutoSteerOn.png', label: 'Gráfico dirección', cmd: 'grafico_direccion' },
        { img: 'ConS_SourcesHeading.png', label: 'Gráfico rumbo', cmd: 'grafico_rumbo' },
        { img: 'AutoManualIsAuto.png', label: 'Gráfico XTE', cmd: 'grafico_xte' },
        { img: 'ConS_SourcesRoll.png', label: 'Corrección roll', cmd: 'chequeo_roll' },
        { img: 'Boundary.png', label: 'Herram. límites', cmd: 'herr_limites' },
        { img: 'ABTracks.png', label: 'Visor eventos', cmd: 'visor_eventos' },
        { img: 'ABSmooth.png', label: 'Suavizar AB', cmd: 'suavizar_ab' },
        { img: 'TrashContourRef.png', label: 'Ocultar contornos', cmd: 'borrar_contornos' },
        { img: 'Webcam.png', label: 'Webcam', cmd: 'webcam' },
        { img: 'YouTurnReverse.png', label: 'Corregir posición', cmd: 'corregir_pos' },
        // Apagar / Reiniciar la PC — acción de SISTEMA, no guidance command:
        // van por /api/sistema/power con confirmación táctil dentro del widget.
        { ico: '⏻', label: 'Apagar PC',   power: 'shutdown', confirm: '¿Apagar la PC?' },
        { ico: '⟳', label: 'Reiniciar PC', power: 'restart',  confirm: '¿Reiniciar la PC?' }
      ]
    },
    // btnJobMenu (JobActive) de AOG: abrir/crear/continuar/cerrar lote
    lote: {
      titulo: 'Lote',
      items: [
        { img: 'FileOpen.png', label: 'Continuar', cmd: 'lote_continuar' },
        { img: 'FileOpen.png', label: 'Abrir', cmd: 'lote_abrir' },
        { img: 'FileNew.png', label: 'Nuevo', cmd: 'lote_nuevo' },
        { img: 'FileNew.png', label: 'Nuevo desde KML', cmd: 'lote_kml' },
        { img: 'SwitchOff.png', label: 'Cerrar', cmd: 'lote_cerrar' }
      ]
    },
    // dropdown "Field Tools" (toolStripBtnFieldTools) de AOG
    herrlote: {
      titulo: 'Herr. lote',
      items: [
        { img: 'Boundary.png', label: 'Lindero', cmd: 'lindero' },
        { img: 'HeadlandBuild.png', label: 'Cabecera', cmd: 'cabecera' },
        { img: 'Headache.png', label: 'Cabecera (Build)', cmd: 'cabecera_avanzada' },
        { img: 'TramAll.png', label: 'Tramlines', cmd: 'tram_crear' },
        { img: 'TramMulti.png', label: 'Tramlines multi', cmd: 'tram_multi' },
        { img: 'TrashApplied.png', label: 'Borrar aplicado', cmd: 'borrar_aplicado' },
        { img: 'FlagRed.png', label: 'Bandera lat/lon', cmd: 'bandera_latlon' },
        { img: 'RecPath.png', label: 'Ruta grabada', cmd: 'ruta_grabada' },
        { img: 'FileNew.png', label: 'Importar guías', cmd: 'importar_guias' }
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

  // Confirmación táctil que llena el widget (el confirm() del SO sale chico e
  // inservible con guantes). El overlay es position:fixed inset:0 → cubre el
  // widget expandido. Devuelve Promise<bool>.
  function confirmarTactil(msg) {
    return new Promise(function (resolve) {
      var ov = document.createElement('div');
      ov.style.cssText = 'position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.72);' +
        'display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px;padding:16px';
      var p = document.createElement('div');
      p.textContent = msg || '¿Confirmar?';
      p.style.cssText = 'font-size:20px;color:#fff;text-align:center';
      var bOk = document.createElement('button');
      bOk.type = 'button';
      bOk.textContent = 'Confirmar';
      bOk.style.cssText = 'width:100%;padding:18px;font-size:20px;border-radius:10px;border:0;background:#C84141;color:#fff';
      var bCancel = document.createElement('button');
      bCancel.type = 'button';
      bCancel.textContent = 'Cancelar';
      bCancel.style.cssText = 'width:100%;padding:18px;font-size:20px;border-radius:10px;border:1px solid #666;background:#2a2a2e;color:#fff';
      function cerrar(v) { try { document.body.removeChild(ov); } catch (e) { } resolve(v); }
      bOk.addEventListener('click', function () { cerrar(true); });
      bCancel.addEventListener('click', function () { cerrar(false); });
      ov.appendChild(p); ov.appendChild(bOk); ov.appendChild(bCancel);
      document.body.appendChild(ov);
    });
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
      var icoHtml = it.img
        ? '<img src="../img/menu/' + it.img + '" alt="">'
        : '<span class="ico">' + (it.ico || '') + '</span>';
      d.innerHTML = icoHtml + '<span>' + it.label + '</span>';
      d.addEventListener('click', function () {
        // Acciones de sistema (Apagar/Reiniciar): confirmación antes de mandar,
        // y por /api/sistema/power (no por el canal de guidance commands).
        if (it.power) {
          confirmarTactil(it.confirm || '¿Confirmar?').then(function (ok) {
            if (!ok) return;
            fetch('/api/sistema/power?action=' + encodeURIComponent(it.power), { method: 'POST' })
              .catch(function () { /* la PC se está apagando/reiniciando */ });
          });
          return;
        }
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

  // Auto-ocultado: cualquier interacción sobre la barra (hover/touch/click)
  // reinicia el contador de 15 s en PilotX vía "paneles_keepalive".
  // Throttle 2 s para no inundar el canal de comandos con el pointermove.
  var kaLast = 0;
  function keepalive() {
    var now = Date.now();
    if (now - kaLast < 2000) return;
    kaLast = now;
    fetch('/api/aog/guidance/command', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cmd: 'paneles_keepalive' })
    }).catch(function () {});
  }
  ['pointermove', 'pointerdown', 'touchstart'].forEach(function (ev) {
    document.addEventListener(ev, keepalive, { passive: true });
  });
})();
