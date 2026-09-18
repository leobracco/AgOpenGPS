// ============================================================================
// barra-superior.js — barra superior estilo mockup principal.png:
// logo + tabs (TRABAJO/GPS/LOTE/SISTEMA) + badge de línea ‹ n › + telemetría
// (km/h, señal, hora, ha). Comandos = contrato con
// FormGPS.ExecuteGuidanceCommand (mismo canal que menu-izquierda.js).
// Estado en vivo sale de GET /api/aog/state — mismo endpoint que hub.js.
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

  document.querySelectorAll('[data-cmd]').forEach(function (b) {
    if (b.closest('#menuPanel')) return; // el menú tiene su propio handler
    b.addEventListener('click', function () { send(b.dataset.cmd, b); });
  });

  // ---- desplegable SISTEMA (espejo HTML del menuStrip1) ----
  // Abrir expande el widget dockeado (resize:WxH → ResizeFloatingWidget);
  // cerrar vuelve al alto de barra sola. Mismo mecanismo que menu-izquierda.
  var BAR = { w: 1020, h: 64 };
  // abierto: panel compacto centrado (la barra se esconde mientras tanto)
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

  // ---- telemetría en vivo ----
  var speedVal = document.getElementById('speedVal');
  var btnLote = document.getElementById('btnLote');
  var dotGps = document.getElementById('dotGps');
  var senalVal = document.getElementById('senalVal');
  var fechaVal = document.getElementById('fechaVal');
  var fechaLbl = document.getElementById('fechaLbl');
  var haHechasVal = document.getElementById('haHechasVal');
  var lineBadge = document.getElementById('lineBadge');
  var lineVal = document.getElementById('lineVal');

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

  // nombre del tipo de señal (calidad GGA), corto como el mockup ("RTK FIJO")
  function fixName(fixQuality) {
    switch (fixQuality) {
      case 4: return 'RTK FIJO';
      case 5: return 'RTK FLOAT';
      case 2: return 'DGPS';
      case 1: return 'GPS';
      case 8: return 'SIMULADOR';
      default: return 'SIN FIX';
    }
  }

  // coma decimal como el mockup (0,0 km/h · 3,80 ha)
  function coma(n, dec) { return n.toFixed(dec).replace('.', ','); }

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
      speedVal.textContent = coma(speed, 1);

      var fix = Number(snap.fix_quality || 0);
      // estado del simulador en el menú (fix 8 = GGA simulation)
      var simState = document.getElementById('simState');
      if (simState) {
        simState.textContent = fix === 8 ? 'ON' : 'OFF';
        simState.classList.toggle('on', fix === 8);
      }
      setStat(dotGps, fixClass(fix));
      senalVal.textContent = fixName(fix);
      setStat(senalVal, fixClass(fix));

      // ha hechas: área trabajada del lote (m² → ha)
      var ha = Number(snap.worked_area_total_m2 || 0) * 0.0001;
      haHechasVal.textContent = coma(ha, 2);

      btnLote.classList.toggle('disabled', !snap.is_job_started);

      // badge de línea: nº actual (1-based) + total, como "11L" del mockup
      var total = Number(snap.tracks_total || 0);
      var idx = Number(typeof snap.track_idx === 'number' ? snap.track_idx : -1);
      if (total > 0 && idx >= 0) {
        lineBadge.classList.remove('off');
        lineVal.innerHTML = (idx + 1) + '<small>/' + total + '</small>';
      } else {
        lineBadge.classList.add('off');
        lineVal.textContent = '—';
      }
    } catch (e) {
      // sin conexión: deja el último estado conocido en pantalla
    }
  }

  poll();
  setInterval(poll, 1000);
})();
