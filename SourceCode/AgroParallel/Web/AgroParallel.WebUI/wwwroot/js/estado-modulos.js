// ============================================================================
// estado-modulos.js — tira de estado de módulos (CoreX/Motor/GPS/IMU/Machine)
// para pages/estado-modulos.html, hosteada como overlay WinForms SIEMPRE
// VISIBLE sobre el mapa (EstadoModulosOverlayControl.cs, pedido de usuario:
// "muy pequeña arriba... en la pantalla principal", sin depender de abrir el
// Hub). Misma lógica y mismo endpoint que refreshModulos() en hub.js —
// duplicado a propósito: esta página vive "desnuda" (sin sidebar/chrome)
// dentro de un WebView2 embebido aparte, no dentro del shell del Hub.
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  function notify(obj) {
    try {
      var wv = window.chrome && window.chrome.webview;
      if (wv) wv.postMessage(obj);
    } catch (e) { /* fuera de WebView2 (dev en browser normal): no-op */ }
  }

  // status: 'ok' (verde) | 'bad' (rojo) | 'idle' (negro = desconectado).
  // Solo el color del ícono cambia — nada de texto, nada de dot aparte.
  function setStatus(el, status, label, detail) {
    if (!el) return;
    el.className = 'mitem ' + status;
    el.title = label + (detail ? ': ' + detail : '');
  }

  function setModuleStatus(el, label, configured, hello) {
    if (!configured) { setStatus(el, 'idle', label, 'no conectado'); return; }
    setStatus(el, hello ? 'ok' : 'bad', label, hello ? 'OK' : 'sin responder');
  }

  function allOffline() {
    setStatus($('pillCorex'), 'bad', 'CoreX', 'offline');
    setStatus($('pillMotor'), 'idle', 'Motor', 'sin datos');
    setStatus($('pillGps'), 'idle', 'GPS', 'sin datos');
    setStatus($('pillImu'), 'idle', 'IMU', 'sin datos');
    setStatus($('pillMachine'), 'idle', 'Machine', 'sin datos');
  }

  async function refresh() {
    try {
      var res = await fetch('/api/corex-bridge/status', { cache: 'no-store' });
      var d = await res.json();
      if (!d || !d.ok) { allOffline(); return; }
      setStatus($('pillCorex'), 'ok', 'CoreX', 'OK');
      setStatus($('pillGps'), d.gps_alive ? 'ok' : 'bad', 'GPS', d.gps_alive ? 'OK' : 'sin señal');
      setModuleStatus($('pillMotor'), 'Motor', d.steer_configured, d.steer_hello);
      setModuleStatus($('pillImu'), 'IMU', d.imu_configured, d.imu_hello);
      setModuleStatus($('pillMachine'), 'Machine', d.machine_configured, d.machine_hello);
    } catch (e) {
      allOffline();
    }
  }

  refresh();
  setInterval(refresh, 2000);

  var bar = $('bar');

  // ---- ancho/alto SIEMPRE ajustado al contenido real ----------------------
  // Medimos #bar (inline-flex, se achica solo a lo que ocupan las pills) —
  // NO document.body: body llenaría siempre el viewport actual del WebView2,
  // así que su scrollWidth nunca "encoge" por debajo del tamaño que ya tenía
  // el control (circular). Le pedimos al host que redimensione el control
  // nativo 1:1 a lo medido — mismo mecanismo que 'resize:WxH' de
  // guia-rapida.js, acá con JSON. ResizeObserver re-mide solo: si un estado
  // cambia de texto ("Motor" → "Motor sin responder") el ancho se reajusta.
  var lastW = 0, lastH = 0;
  function reportSize() {
    var r = bar.getBoundingClientRect();
    var w = Math.ceil(r.width);
    var h = Math.ceil(r.height);
    if (!w || !h || (w === lastW && h === lastH)) return;
    lastW = w; lastH = h;
    notify({ type: 'resize', w: w, h: h });
  }
  if (window.ResizeObserver) {
    new ResizeObserver(reportSize).observe(bar);
  }
  reportSize();

  // ---- arrastre: TODA #bar es agarrable (no hay controles interactivos
  // adentro, son pills de solo lectura). El WebView2 SÍ recibe pointer
  // events con normalidad (a diferencia del native drag de WinForms sobre un
  // control hermano) — mandamos los deltas al host nativo por postMessage y
  // ahí se aplica el movimiento real + se persiste al soltar. Ver
  // EstadoModulosOverlayControl.OnWebMessageReceived (type: drag_start /
  // drag / drag_end). ----
  var dragging = false;
  var startX = 0, startY = 0;

  bar.addEventListener('pointerdown', function (e) {
    dragging = true;
    startX = e.clientX; startY = e.clientY;
    try { bar.setPointerCapture(e.pointerId); } catch (_) { /* no-op */ }
    notify({ type: 'drag_start' });
  });
  bar.addEventListener('pointermove', function (e) {
    if (!dragging) return;
    notify({ type: 'drag', dx: e.clientX - startX, dy: e.clientY - startY });
  });
  function endDrag() {
    if (!dragging) return;
    dragging = false;
    notify({ type: 'drag_end' });
  }
  bar.addEventListener('pointerup', endDrag);
  bar.addEventListener('pointercancel', endDrag);
})();
