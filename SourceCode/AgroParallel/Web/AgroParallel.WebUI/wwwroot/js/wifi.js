// ============================================================================
// wifi.js — lógica de la página Red WiFi (pages/wifi.html).
//
// Wire (snake_case, AgpJson):
//   GET  /api/red/wifi             → { ok, estado:{conectado,ssid,ip},
//                                      redes:[{ssid,senal_pct,segura,conectada}] }
//   POST /api/red/wifi/conectar    ← { ssid, clave }  → { ok, error?, estado }
//   POST /api/red/wifi/desconectar → { ok, error? }
//
// La clave se teclea con el teclado de PilotX (keyboard.js / teclado nativo
// del host) — nunca el del sistema operativo. El refresco automático se
// PAUSA mientras hay una red elegida para que la lista no se re-arme abajo
// del dedo del operario.
// ============================================================================
(function () {
  'use strict';

  var lista = document.getElementById('wfLista');
  var pillEstado = document.getElementById('wfEstado');
  var btnActualizar = document.getElementById('wfActualizar');

  var redes = [];
  var seleccionada = null;   // ssid elegido (panel de clave abierto)
  var conectando = false;
  var ultimoError = null;

  function nivelSenal(pct) {
    if (pct >= 75) return 4;
    if (pct >= 50) return 3;
    if (pct >= 25) return 2;
    return 1;
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function pintarEstado(estado) {
    if (estado && estado.conectado) {
      pillEstado.textContent = (estado.ssid || '') + (estado.ip ? ' · ' + estado.ip : '');
      pillEstado.classList.add('ok');
    } else {
      pillEstado.textContent = 'Sin conexión';
      pillEstado.classList.remove('ok');
    }
  }

  function pintar() {
    if (!redes.length) {
      lista.innerHTML = '<div class="wf-vacio">No se ven redes WiFi. ' +
        'Revisá que el equipo tenga WiFi y tocá Actualizar.</div>';
      return;
    }
    var html = '';
    redes.forEach(function (r) {
      var sel = seleccionada === r.ssid;
      html += '<div class="wf-red' + (sel ? ' sel' : '') + '" data-ssid="' + esc(r.ssid) + '">';
      html += '<div class="wf-red-fila">';
      html += '<span class="wf-barras" data-nivel="' + nivelSenal(r.senal_pct) + '">' +
              '<span></span><span></span><span></span><span></span></span>';
      html += '<span class="wf-ssid">' + esc(r.ssid) + '</span>';
      if (r.segura) html += '<span class="wf-lock">🔒</span>';
      html += '<span class="wf-senal">' + (r.senal_pct || 0) + '%</span>';
      if (r.conectada) html += '<span class="wf-badge">Conectada</span>';
      html += '</div>';

      if (sel) {
        html += '<div class="wf-conectar">';
        if (ultimoError) html += '<div class="wf-error">' + esc(ultimoError) + '</div>';
        if (r.conectada) {
          html += '<button type="button" class="btn" data-act="desconectar">Desconectar</button>';
        } else {
          if (r.segura) {
            html += '<input type="password" id="wfClave" placeholder="Clave de la red" autocomplete="off" />';
          }
          html += '<button type="button" class="btn acento" data-act="conectar"' +
                  (conectando ? ' disabled' : '') + '>' +
                  (conectando ? 'Conectando…' : 'Conectar') + '</button>';
        }
        html += '</div>';
      }
      html += '</div>';
    });
    lista.innerHTML = html;

    // Enfocar la clave recién pintada: dispara el teclado propio del host.
    var clave = document.getElementById('wfClave');
    if (clave && !conectando) clave.focus();
  }

  function cargar(mostrarCargando) {
    if (mostrarCargando) {
      lista.innerHTML = '<div class="wf-vacio">Buscando redes…</div>';
    }
    fetch('/api/red/wifi')
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d.ok) {
          lista.innerHTML = '<div class="wf-vacio">WiFi no disponible en este equipo.</div>';
          pintarEstado(null);
          return;
        }
        redes = d.redes || [];
        pintarEstado(d.estado);
        pintar();
      })
      .catch(function () {
        lista.innerHTML = '<div class="wf-vacio">No se pudo consultar el WiFi.</div>';
      });
  }

  function conectar(ssid) {
    var claveEl = document.getElementById('wfClave');
    var clave = claveEl ? claveEl.value : '';
    conectando = true;
    ultimoError = null;
    pintar();
    fetch('/api/red/wifi/conectar', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ssid: ssid, clave: clave }),
    })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        conectando = false;
        if (d.ok) {
          seleccionada = null;
          ultimoError = null;
          cargar(false);
        } else {
          ultimoError = d.error || 'No se pudo conectar.';
          pintar();
        }
      })
      .catch(function () {
        conectando = false;
        ultimoError = 'No se pudo conectar.';
        pintar();
      });
  }

  function desconectar() {
    fetch('/api/red/wifi/desconectar', { method: 'POST' })
      .then(function () { seleccionada = null; cargar(true); })
      .catch(function () { cargar(true); });
  }

  lista.addEventListener('click', function (e) {
    var act = e.target.closest ? e.target.closest('[data-act]') : null;
    if (act) {
      var red = act.closest('.wf-red');
      if (act.dataset.act === 'conectar' && red) conectar(red.dataset.ssid);
      if (act.dataset.act === 'desconectar') desconectar();
      return;
    }
    // Toque en el campo de clave: no colapsar el panel.
    if (e.target.tagName === 'INPUT') return;
    var fila = e.target.closest ? e.target.closest('.wf-red') : null;
    if (!fila) return;
    var ssid = fila.dataset.ssid;
    seleccionada = (seleccionada === ssid) ? null : ssid;
    ultimoError = null;
    pintar();
  });

  btnActualizar.addEventListener('click', function () {
    seleccionada = null;
    ultimoError = null;
    cargar(true);
  });

  // Refresco de fondo cada 20 s — pausado con el panel de clave abierto para
  // no re-armar la lista abajo del dedo, y durante una conexión en curso.
  setInterval(function () {
    if (seleccionada == null && !conectando) cargar(false);
  }, 20000);

  cargar(true);
})();
