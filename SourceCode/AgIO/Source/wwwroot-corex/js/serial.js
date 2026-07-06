// ============================================================================
// serial.js — Configuración de puertos serie CoreX.
// Espejo web de FormCommSetGPS / FormCommSetModule (WinForms viejos).
//
// El wire viene en snake_case (AgpJson server-side). El body de las requests
// POST también va en snake_case para que la deserialización del controller
// (con [JsonPropertyName] explícito) funcione sin ambigüedad.
// ============================================================================

(function () {
  'use strict';

  // Canales que tienen selector de baud rate. IMU/Steer/Machine tienen baud
  // fijo en el firmware (38400) y el backend ignora el parámetro baud.
  var HAS_BAUD = { gps: true, gps2: true, rtcm: true, imu: false, steer: false, machine: false };
  var CHANNELS  = ['gps', 'gps2', 'rtcm', 'imu', 'steer', 'machine'];

  function $(id) { return document.getElementById(id); }

  // Popula el select de puertos con las opciones disponibles y selecciona
  // el puerto actualmente configurado. No usa innerHTML para evitar XSS.
  function fillPortSelect(sel, ports, current) {
    while (sel.firstChild) sel.removeChild(sel.firstChild);

    // Opción vacía si no hay nada configurado todavía.
    if (!ports || ports.length === 0) {
      var empty = document.createElement('option');
      empty.textContent = '(ningún puerto)';
      empty.value = '';
      sel.appendChild(empty);
      return;
    }

    for (var i = 0; i < ports.length; i++) {
      var opt = document.createElement('option');
      opt.value       = ports[i];
      opt.textContent = ports[i];
      if (ports[i] === current) opt.selected = true;
      sel.appendChild(opt);
    }
  }

  // Actualiza la UI de un canal con el estado devuelto por el API.
  function applyChannelState(ch, channelData, portsAvailable) {
    var open   = !!channelData.is_open;
    var dot    = $('dot-' + ch);
    var sel    = $('port-' + ch);
    var btn    = $('btn-' + ch);
    var baudSel = HAS_BAUD[ch] ? $('baud-' + ch) : null;

    fillPortSelect(sel, portsAvailable, channelData.port);

    if (baudSel) baudSel.value = String(channelData.baud || 4800);

    // Dot: verde = abierto, gris = cerrado (no es "mal" estado, es neutro).
    if (dot) {
      dot.className = 'dot' + (open ? ' on' : '');
    }

    // Botón y selects: bloqueados mientras está abierto.
    btn.textContent = open ? 'Cerrar' : 'Abrir';
    btn.className   = open ? 'btn-canal cerrar' : 'btn-canal';
    sel.disabled    = open;
    if (baudSel) baudSel.disabled = open;
  }

  // Carga el estado completo desde el API y actualiza todos los canales.
  async function load() {
    try {
      var res = await fetch('/api/corex/config/serial', { cache: 'no-store' });
      if (!res.ok) return;
      var cfg = await res.json();
      var ports = cfg.ports_available || [];
      var channels = cfg.channels || {};

      for (var i = 0; i < CHANNELS.length; i++) {
        var ch = CHANNELS[i];
        var chData = channels[ch] || {};
        applyChannelState(ch, chData, ports);
      }
    } catch (e) {
      // Red caída o CoreX reiniciando: silencioso, el usuario vuelve a intentar.
    }
  }

  // Alterna el estado del canal: abre si estaba cerrado, cierra si estaba abierto.
  async function toggle(ch) {
    var btn     = $('btn-' + ch);
    var isOpen  = btn.className.indexOf('cerrar') !== -1;

    if (isOpen) {
      try {
        await fetch('/api/corex/serial/close', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ channel: ch })
        });
      } catch (e) {
        AgpModal.alert('Error de red', 'No se pudo contactar a CoreX para cerrar el puerto.');
        return;
      }
    } else {
      var portSel = $('port-' + ch);
      var port    = portSel ? portSel.value : '';
      var baud    = HAS_BAUD[ch] ? parseInt($('baud-' + ch).value, 10) : 0;

      if (!port) {
        AgpModal.alert('Sin puerto', 'Seleccioná un puerto COM antes de abrir.');
        return;
      }

      var body = { channel: ch, port: port, baud: baud };
      var r, result;
      try {
        r      = await fetch('/api/corex/serial/open', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body)
        });
        result = await r.json();
      } catch (e) {
        AgpModal.alert('Error de red', 'No se pudo contactar a CoreX para abrir el puerto.');
        return;
      }

      // ok:false significa que el puerto no se pudo abrir (en uso o no existe).
      if (!result.ok) {
        AgpModal.alert('Puerto no disponible',
          port + ' no se pudo abrir — ¿está en uso o desconectado?');
      }
    }

    // Recarga siempre para reflejar el estado real (no solo el esperado).
    await load();
  }

  // Engancha cada botón con su canal al cargar.
  for (var i = 0; i < CHANNELS.length; i++) {
    (function (ch) {
      var btn = $('btn-' + ch);
      if (btn) btn.addEventListener('click', function () { toggle(ch); });
    })(CHANNELS[i]);
  }

  load();
})();
