// ============================================================================
// red.js — Configuración de red UDP de CoreX.
// Espejo web de FormUDP (WinForms):
//   · UDP on/off: guarda y SIEMPRE reinicia CoreX.
//   · Subnet: broadcast del PGN de subnet a todos los nodos + guarda settings.
//     NO reinicia CoreX.
//
// Wire: snake_case en ambas direcciones (AgpJson + [JsonPropertyName] en DTOs).
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  // Estado real del toggle (para revertir si el usuario cancela el confirm).
  var udpOnReal = false;

  // ── Carga inicial: GET /api/corex/config/red ────────────────────────────

  function load() {
    fetch('/api/corex/config/red')
      .then(function (r) { return r.json(); })
      .then(function (d) {
        udpOnReal = !!d.udp_is_on;
        $('udp-on').checked = udpOnReal;
        $('ip-actual').textContent = d.ip_actual || '—';

        var sub = d.subnet || [192, 168, 5];
        $('o1').value = sub[0] != null ? sub[0] : 192;
        $('o2').value = sub[1] != null ? sub[1] : 168;
        $('o3').value = sub[2] != null ? sub[2] : 5;

        var pilotx = d.pilotx_ip || [127, 0, 0, 1];
        $('p1').value = pilotx[0] != null ? pilotx[0] : 127;
        $('p2').value = pilotx[1] != null ? pilotx[1] : 0;
        $('p3').value = pilotx[2] != null ? pilotx[2] : 0;
        $('p4').value = pilotx[3] != null ? pilotx[3] : 1;

        actualizarHintSubnet(d.ip_actual || '');
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo cargar la configuración de red.');
      });
  }

  // Muestra la IP actual como referencia en el hint de subnet.
  // Construye los nodos a mano: nunca innerHTML con datos del server.
  function actualizarHintSubnet(ipActual) {
    var hint = $('subnet-hint');
    if (!ipActual || ipActual === 'Off' || ipActual === '—') return;

    while (hint.firstChild) hint.removeChild(hint.firstChild);

    hint.appendChild(document.createTextNode('IP actual del host: '));
    var ipStrong = document.createElement('strong');
    ipStrong.textContent = ipActual;
    hint.appendChild(ipStrong);
    hint.appendChild(document.createTextNode(
      '. Ingresá los tres primeros octetos de la subnet de la LAN. ' +
      'Al confirmar, CoreX envía el comando a '));
    var todosStrong = document.createElement('strong');
    todosStrong.textContent = 'todos';
    hint.appendChild(todosStrong);
    hint.appendChild(document.createTextNode(' los nodos presentes en la red.'));
  }

  // ── Toggle UDP on/off ───────────────────────────────────────────────────

  function onToggleUdp() {
    var nuevoEstado = $('udp-on').checked;

    // Revertir inmediatamente y pedir confirmación antes de hacer nada.
    $('udp-on').checked = udpOnReal;

    var titulo = nuevoEstado ? 'Habilitar UDP' : 'Deshabilitar UDP';
    AgpModal.confirm(
      'Reiniciar CoreX',
      'CoreX se reinicia para aplicar el cambio. ¿Continuar?'
    ).then(function (confirmado) {
      if (!confirmado) return;

      $('udp-on').disabled = true;

      fetch('/api/corex/config/red/udp', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ on: nuevoEstado })
      })
        .then(function (r) {
          return r.json().then(function (d) { return { ok: r.ok, data: d }; });
        })
        .then(function (res) {
          $('udp-on').disabled = false;
          if (!res.ok) {
            AgpModal.alert('Error', (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
            return;
          }
          // restart=true siempre en este endpoint; esperamos que CoreX vuelva.
          if (res.data.restart) {
            udpOnReal = nuevoEstado;
            $('udp-on').checked = nuevoEstado;
            waitForRestart();
          }
        })
        .catch(function () {
          $('udp-on').disabled = false;
          AgpModal.alert('Error de red', 'No se pudo cambiar el estado UDP.');
        });
    });
  }

  // ── Enviar subnet a módulos ─────────────────────────────────────────────

  function onEnviarSubnet() {
    var o1 = parseInt($('o1').value, 10);
    var o2 = parseInt($('o2').value, 10);
    var o3 = parseInt($('o3').value, 10);

    // Validar octetos 0-255.
    if (!esOcteto(o1) || !esOcteto(o2) || !esOcteto(o3)) {
      AgpModal.alert('Valor inválido',
        'Cada octeto debe ser un número entre 0 y 255.');
      return;
    }

    AgpModal.confirm(
      'Cambiar subnet',
      'Esto cambia la subnet de TODOS los nodos de la LAN. ¿Continuar?'
    ).then(function (confirmado) {
      if (!confirmado) return;

      var btn = $('btn-subnet');
      btn.disabled = true;

      fetch('/api/corex/config/red/subnet', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ o1: o1, o2: o2, o3: o3 })
      })
        .then(function (r) {
          return r.json().then(function (d) { return { ok: r.ok, data: d }; });
        })
        .then(function (res) {
          btn.disabled = false;
          if (!res.ok) {
            AgpModal.alert('Error', (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
            return;
          }
          // Releer el estado actualizado y confirmar al operario.
          load();
          AgpModal.alert('Enviado',
            'Subnet ' + o1 + '.' + o2 + '.' + o3 + '.x enviada a los módulos.');
        })
        .catch(function () {
          btn.disabled = false;
          AgpModal.alert('Error de red', 'No se pudo enviar la subnet.');
        });
    });
  }

  function esOcteto(v) {
    return Number.isInteger(v) && v >= 0 && v <= 255;
  }

  // ── Esperar reinicio: poll /api/corex/status hasta que responda ──────────
  // Copiado del patrón de ntrip.js: 30 intentos × 2 s = 60 s de timeout.

  function waitForRestart() {
    AgpModal.alert('Reiniciando', 'CoreX se está reiniciando… esperá unos segundos.');
    var attempts = 0;
    var interval = setInterval(function () {
      attempts++;
      if (attempts > 30) {
        clearInterval(interval);
        AgpModal.alert('CoreX no responde',
          'El reinicio está tardando más de lo esperado. Cerrá y volvé a abrir CoreX.');
        return;
      }
      fetch('/api/corex/status')
        .then(function (r) {
          if (r.ok) {
            clearInterval(interval);
            location.reload();
          }
        })
        .catch(function () {
          // Sigue esperando; CoreX todavía está reiniciando.
        });
    }, 2000);
  }

  // ── IP de PilotX (eth_loop, port de FormEthernet) ────────────────────────
  // Guardar SIEMPRE reinicia CoreX (igual que el form viejo).

  function onGuardarPilotx() {
    var p1 = parseInt($('p1').value, 10);
    var p2 = parseInt($('p2').value, 10);
    var p3 = parseInt($('p3').value, 10);
    var p4 = parseInt($('p4').value, 10);

    if (!esOcteto(p1) || !esOcteto(p2) || !esOcteto(p3) || !esOcteto(p4)) {
      AgpModal.alert('Valor inválido', 'Cada octeto debe ser un número entre 0 y 255.');
      return;
    }

    AgpModal.confirm('IP de PilotX',
      'Guardar ' + p1 + '.' + p2 + '.' + p3 + '.' + p4 +
      ' reinicia CoreX. ¿Continuar?')
      .then(function (si) {
        if (!si) return;
        var btn = $('btn-pilotx');
        btn.disabled = true;
        fetch('/api/corex/config/red/pilotx', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ o1: p1, o2: p2, o3: p3, o4: p4 }),
        })
          .then(function (r) {
            return r.json().then(function (d) { return { ok: r.ok, data: d }; });
          })
          .then(function (res) {
            if (!res.ok) {
              btn.disabled = false;
              AgpModal.alert('Error',
                (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
              return;
            }
            waitForRestart();
          })
          .catch(function () {
            btn.disabled = false;
            AgpModal.alert('Error de red', 'No se pudo guardar la IP de PilotX.');
          });
      });
  }

  // ── Inicialización ───────────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', function () {
    load();
    $('udp-on').addEventListener('change', onToggleUdp);
    $('btn-subnet').addEventListener('click', onEnviarSubnet);
    $('btn-pilotx').addEventListener('click', onGuardarPilotx);
  });

})();
