// ============================================================================
// barra-superior.js — espejo HTML de la barra superior nativa (panelControlBox):
// Lote/Carga/GPS/Velocidad/Minimizar/Maximizar/Cerrar. Comandos = contrato
// con FormGPS.ExecuteGuidanceCommand (mismo canal que menu-izquierda.js).
// Estado en vivo (velocidad, calidad de fix, alimentación, lote abierto) sale
// de GET /api/aog/state — mismo endpoint que ya usan hub.js/flowx.js.
// ============================================================================
(function () {
  'use strict';

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
      if (!data.ok) console.warn('[barra-superior] comando rechazado:', cmd, data.error);
    } catch (e) {
      console.warn('[barra-superior] sin conexión:', e.message);
    }
  }

  document.querySelectorAll('.tbtn[data-cmd]').forEach(function (b) {
    b.addEventListener('click', function () { send(b.dataset.cmd, b); });
  });

  // ---- desplegable de la hamburguesa (espejo HTML del menuStrip1) ----
  // Abrir expande el widget dockeado (resize:WxH → ResizeFloatingWidget);
  // cerrar vuelve al alto de barra sola. Mismo mecanismo que menu-izquierda.
  var BAR = { w: 1020, h: 64 };
  //abierto: panel compacto centrado (la barra se esconde mientras tanto)
  var OPEN = { w: 560, h: 560 };

  function resizeWidget(w, h) {
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv) wv.postMessage('resize:' + w + 'x' + h);
    } catch (e) { /* fuera de WebView2: no-op */ }
  }

  var btnMenu = document.getElementById('btnMenu');
  var menuPanel = document.getElementById('menuPanel');
  var LISTAS = { root: 'mlistRoot', idioma: 'mlistIdioma' };

  function mostrarLista(nombre) {
    Object.keys(LISTAS).forEach(function (k) {
      document.getElementById(LISTAS[k]).hidden = (k !== nombre);
    });
  }

  function abrirMenu() {
    mostrarLista('root');
    menuPanel.hidden = false;
    document.body.classList.add('menu-open');
    resizeWidget(OPEN.w, OPEN.h);
  }

  function cerrarMenu() {
    menuPanel.hidden = true;
    document.body.classList.remove('menu-open');
    resizeWidget(BAR.w, BAR.h);
  }

  btnMenu.addEventListener('click', function () {
    if (menuPanel.hidden) abrirMenu(); else cerrarMenu();
  });
  document.getElementById('btnCerrarMenu').addEventListener('click', cerrarMenu);

  menuPanel.querySelectorAll('.mitem').forEach(function (b) {
    b.addEventListener('click', function () {
      if (b.dataset.sub) { mostrarLista(b.dataset.sub); return; }
      if (b.dataset.back) { mostrarLista('root'); return; }
      if (b.dataset.cmd) {
        send(b.dataset.cmd, b);
        if (!b.dataset.keep) cerrarMenu();
      }
    });
  });

  var speedVal = document.getElementById('speedVal');
  var btnLote = document.getElementById('btnLote');
  var btnGps = document.getElementById('btnGps');
  var btnCarga = document.getElementById('btnCarga');
  var fechaVal = document.getElementById('fechaVal');
  var fechaLbl = document.getElementById('fechaLbl');
  var latVal = document.getElementById('latVal');
  var lonVal = document.getElementById('lonVal');
  var chipSenal = document.getElementById('chipSenal');
  var senalVal = document.getElementById('senalVal');
  var haHoraVal = document.getElementById('haHoraVal');
  var haHechasVal = document.getElementById('haHechasVal');

  var STAT_CLASSES = ['stat-ok', 'stat-mid', 'stat-low', 'stat-bad'];
  function setStat(el, cls) {
    STAT_CLASSES.forEach(function (c) { el.classList.remove(c); });
    if (cls) el.classList.add(cls);
  }

  // igual mapeo que GUI.Designer.cs (switch pn.fixQuality) para btnGPSData.BackColor
  function fixClass(fixQuality) {
    switch (fixQuality) {
      case 4: return 'stat-ok';   // RTK fijo
      case 5: return 'stat-mid';  // RTK float
      case 2: return 'stat-low';  // DGPS
      default: return 'stat-bad'; // sin fix
    }
  }

  // nombre del tipo de señal (calidad GGA), igual criterio que datos-gps
  function fixName(fixQuality) {
    switch (fixQuality) {
      case 4: return 'RTK';
      case 5: return 'RTK Float';
      case 2: return 'DGPS';
      case 1: return 'GPS';
      case 8: return 'Simulador';
      default: return 'Sin fix';
    }
  }

  var DIAS = ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'];
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function actualizarFecha() {
    var d = new Date();
    fechaVal.textContent = pad(d.getHours()) + ':' + pad(d.getMinutes());
    fechaLbl.textContent = DIAS[d.getDay()] + ' ' + pad(d.getDate()) + '/' + pad(d.getMonth() + 1);
  }
  actualizarFecha();
  setInterval(actualizarFecha, 15000);

  async function poll() {
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      var snap = await res.json();

      var speed = Number(snap.avg_speed || 0);
      speedVal.textContent = speed.toFixed(1);

      var fix = Number(snap.fix_quality || 0);
      // estado del simulador en el menú (fix 8 = GGA simulation)
      var simState = document.getElementById('simState');
      if (simState) {
        simState.textContent = fix === 8 ? 'ON' : 'OFF';
        simState.classList.toggle('on', fix === 8);
      }
      setStat(btnGps, fixClass(fix));
      setStat(btnCarga, snap.power_online ? 'stat-ok' : 'stat-bad');
      senalVal.textContent = fixName(fix);
      setStat(chipSenal, fixClass(fix));

      var lat = Number(snap.latitude || 0), lon = Number(snap.longitude || 0);
      latVal.textContent = lat ? lat.toFixed(6) : '—';
      lonVal.textContent = lon ? lon.toFixed(6) : '—';

      // ha/h: misma fórmula que el nativo (CFieldData.WorkRateHectares):
      // ancho de labor [m] * velocidad [km/h] * 0.1
      var rate = Number(snap.tool_width || 0) * speed * 0.1;
      haHoraVal.textContent = rate.toFixed(1);

      // ha hechas: área trabajada del lote (m² → ha)
      var ha = Number(snap.worked_area_total_m2 || 0) * 0.0001;
      haHechasVal.textContent = ha.toFixed(2);

      btnLote.classList.toggle('disabled', !snap.is_job_started);
    } catch (e) {
      // sin conexión: deja el último estado conocido en pantalla
    }
  }

  poll();
  setInterval(poll, 1000);
})();
