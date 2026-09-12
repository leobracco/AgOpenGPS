// ============================================================================
// librax.js — pantalla de diagnóstico de LibraX (monitor de rendimiento).
//
// Consume GET /api/librax/live a 4 Hz y grafica el ratio de los últimos 60 s.
// Es una pantalla para JUZGAR EL MONTAJE DEL SENSOR, no el monitor final: no
// muestra qq/ha ni humedad en % porque en fase 1 nada de eso está calibrado.
//
// Si hay más de un nodo, se muestra el primero por uid (en la práctica hay uno
// solo: una cosechadora, una noria).
// ============================================================================

(function () {
  'use strict';

  var POLL_MS = 250;
  var VENTANA_SEG = 60;
  var MAX_POINTS = Math.round((VENTANA_SEG * 1000) / POLL_MS);   // 240

  var kpiRatio  = document.getElementById('kpiRatio');
  var kpiSensor = document.getElementById('kpiSensor');
  var kpiPaddle = document.getElementById('kpiPaddle');
  var kpiRpm    = document.getElementById('kpiRpm');
  var kpiMoist  = document.getElementById('kpiMoist');
  var kpiUptime = document.getElementById('kpiUptime');
  var kpiNoise  = document.getElementById('kpiNoise');
  var noiseHint = document.getElementById('noiseHint');
  var ratioBar  = document.getElementById('ratioBar');
  var okPill    = document.getElementById('okPill');
  var nodosList = document.getElementById('nodosList');
  var canvas    = document.getElementById('ratioCanvas');
  var ctx       = canvas.getContext('2d');

  var serie = [];            // ratio en % (0..100), buffer rodante
  var noiseAnterior = null;  // para detectar ruido CRECIENDO, no acumulado

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function css(varName, fallback) {
    var v = getComputedStyle(document.documentElement).getPropertyValue(varName);
    return (v && v.trim()) || fallback;
  }

  function fmtUptime(s) {
    // Comparación explícita contra null/undefined: un nodo recién booteado
    // manda uptime_s = 0, que es un dato real ("0 s"), no un dato ausente.
    if (s === null || s === undefined) return '—';
    if (s < 60) return s + ' s';
    if (s < 3600) return Math.floor(s / 60) + ' min';
    return Math.floor(s / 3600) + ' h ' + Math.floor((s % 3600) / 60) + ' min';
  }

  // ── Canvas ────────────────────────────────────────────────────────────────
  function fitCanvas() {
    var dpr = window.devicePixelRatio || 1;
    canvas.width  = Math.max(1, Math.round(canvas.clientWidth * dpr));
    canvas.height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  window.addEventListener('resize', function () { fitCanvas(); draw(); });

  function draw() {
    var w = canvas.clientWidth, h = canvas.clientHeight;
    if (!w || !h) return;

    ctx.clearRect(0, 0, w, h);

    var grid = css('--agp-border', '#2a312c');
    var texto = css('--agp-text-muted', '#8a938c');
    var linea = css('--agp-state-ok', '#4ABA3E');

    // Grilla cada 25 %.
    ctx.strokeStyle = grid;
    ctx.lineWidth = 1;
    ctx.fillStyle = texto;
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    for (var p = 0; p <= 100; p += 25) {
      var y = h - (p / 100) * h;
      ctx.beginPath();
      ctx.moveTo(28, y + 0.5);
      ctx.lineTo(w, y + 0.5);
      ctx.stroke();
      ctx.fillText(p + '%', 2, Math.min(h - 6, Math.max(6, y)));
    }

    if (serie.length < 2) return;

    // Serie del ratio. Los puntos null son "sin dato" y dejan un HUECO: el
    // trazo se corta y vuelve a arrancar con moveTo. Dibujarlos como 0 sería
    // indistinguible de un flujo real de 0 %, y esta pantalla existe
    // justamente para leer este gráfico.
    ctx.strokeStyle = linea;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.beginPath();
    var x0 = 28, ancho = w - 28;
    var trazando = false;
    for (var i = 0; i < serie.length; i++) {
      var v = serie[i];
      if (v === null) { trazando = false; continue; }
      var x = x0 + (i / (MAX_POINTS - 1)) * ancho;
      var yy = h - (Math.max(0, Math.min(100, v)) / 100) * h;
      if (trazando) { ctx.lineTo(x, yy); }
      else { ctx.moveTo(x, yy); trazando = true; }
    }
    ctx.stroke();
  }

  // ── Render ────────────────────────────────────────────────────────────────
  function setPill(estado, texto) {
    if (!okPill) return;
    var color = estado === 'ok'   ? css('--agp-state-ok', '#4ABA3E')
              : estado === 'warn' ? css('--agp-state-warn', '#E0A93C')
              :                     css('--agp-text-muted', '#8a938c');
    okPill.style.color = color;
    var dot = okPill.querySelector('.dot');
    if (dot) dot.style.background = color;
    var node = okPill.lastChild;
    if (node && node.nodeType === 3) node.nodeValue = ' ' + texto;
    else okPill.appendChild(document.createTextNode(' ' + texto));
  }

  function renderNodo(n) {
    var pct = n.ratio_pct != null ? n.ratio_pct : (n.ratio_permil || 0) / 10;

    if (kpiRatio)  kpiRatio.textContent  = n.sensor_ok ? pct.toFixed(1) : '—';
    if (ratioBar)  ratioBar.style.width  = (n.sensor_ok ? Math.max(0, Math.min(100, pct)) : 0) + '%';
    if (kpiPaddle) kpiPaddle.textContent = n.paddle_hz != null ? n.paddle_hz : '—';
    if (kpiRpm)    kpiRpm.textContent    = (n.rpm != null ? n.rpm : '—') + ' rpm';
    if (kpiMoist)  kpiMoist.textContent  = n.moist_mv != null ? n.moist_mv : '—';
    if (kpiUptime) kpiUptime.textContent = fmtUptime(n.uptime_s);
    if (kpiNoise)  kpiNoise.textContent  = n.noise != null ? n.noise : '—';

    if (kpiSensor) {
      if (!n.online) {
        kpiSensor.textContent = 'NODO OFFLINE';
        kpiSensor.style.color = css('--agp-text-muted', '#8a938c');
      } else if (!n.sensor_ok) {
        kpiSensor.textContent = 'Noria parada o sensor sin señal';
        kpiSensor.style.color = css('--agp-state-warn', '#E0A93C');
      } else {
        kpiSensor.textContent = 'Midiendo';
        kpiSensor.style.color = css('--agp-state-ok', '#4ABA3E');
      }
    }

    // El ruido acumulado no dice nada; lo que importa es que CREZCA.
    if (noiseHint) {
      var delta = (noiseAnterior == null) ? 0 : (n.noise - noiseAnterior);
      noiseAnterior = n.noise;
      if (delta > 0) {
        noiseHint.textContent = 'Subiendo (+' + delta + ') — revisar EMI o alineación del haz';
        noiseHint.className = 'lbx-warn';
      } else {
        noiseHint.textContent = 'Estable';
        noiseHint.className = 'subtitle';
      }
    }

    if (!n.online)          setPill('idle', 'Nodo offline');
    else if (!n.sensor_ok)  setPill('warn', 'Sin señal del sensor');
    else                    setPill('ok',   'Midiendo');

    // Serie: solo se grafica lo que el sensor realmente midió. Sin dato va
    // null, no 0 — el gráfico deja un hueco (ver draw()).
    serie.push(n.online && n.sensor_ok ? pct : null);
    if (serie.length > MAX_POINTS) serie.shift();
    draw();
  }

  function renderSinNodo() {
    setPill('idle', 'Sin nodo');
    if (kpiSensor) {
      kpiSensor.textContent = 'Ningún nodo publicando';
      kpiSensor.style.color = css('--agp-text-muted', '#8a938c');
    }
    serie.push(null);   // sin nodo = sin dato, no un flujo de 0 %
    if (serie.length > MAX_POINTS) serie.shift();
    draw();
  }

  // IP y versión de firmware por uid. No vienen en status_live (a 5 Hz cada
  // byte cuenta): salen del announcement, que el NodoRegistry ya colecciona y
  // expone en /api/librax/nodos. Se refresca lento, cada 5 s.
  var infoNodos = {};

  function renderLista(nodos) {
    if (!nodosList) return;
    if (!nodos || nodos.length === 0) {
      nodosList.innerHTML =
        '<div class="subtitle">Buscando nodos en LAN… El nodo publica en ' +
        '<code>agp/librax/{uid}/status_live</code> al broker MQTT del PC.</div>';
      return;
    }
    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      var extra = infoNodos[n.uid] || {};
      var color = n.online ? css('--agp-state-ok', '#4ABA3E') : css('--agp-text-muted', '#8a938c');
      html += '<div class="row">' +
                '<span><b>' + escapeHtml(n.nombre || n.uid) + '</b> ' +
                  '<span class="subtitle">' + escapeHtml(n.uid) +
                    (extra.ip ? ' · ' + escapeHtml(extra.ip) : '') +
                    (extra.firmware ? ' · v' + escapeHtml(extra.firmware) : '') +
                  '</span></span>' +
                '<strong style="color:' + color + '">' +
                  (n.online ? 'online' : 'offline') +
                '</strong>' +
              '</div>';
    }
    nodosList.innerHTML = html;
  }

  // ── Polling ───────────────────────────────────────────────────────────────
  function tick() {
    fetch('/api/librax/live', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (snap) {
        var nodos = (snap && snap.nodos) || [];
        renderLista(nodos);
        if (nodos.length === 0) renderSinNodo();
        else renderNodo(nodos[0]);
      })
      .catch(function () {
        // PilotX cerrado o WebHost reiniciando: no ensuciamos la consola en
        // un loop de 4 Hz, solo mostramos el estado.
        renderSinNodo();
      });
  }

  function tickInfo() {
    fetch('/api/librax/nodos', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (res) {
        var lista = (res && res.nodos) || [];
        for (var i = 0; i < lista.length; i++) {
          infoNodos[lista[i].uid] = { ip: lista[i].ip, firmware: lista[i].firmware };
        }
      })
      .catch(function () { /* el registry puede no estar listo todavía */ });
  }

  fitCanvas();
  draw();
  tick();
  tickInfo();
  setInterval(tick, POLL_MS);
  setInterval(tickInfo, 5000);
})();
