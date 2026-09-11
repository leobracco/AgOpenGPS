// ============================================================================
// ntrip.js — Configuración NTRIP de CoreX.
// Espejo web de FormNtrip (WinForms). Guardar puede reiniciar CoreX si cambia
// is_on o el destino serial/UDP (mismo criterio que el form: ntripStatusChanged).
//
// Wire: snake_case en ambas direcciones (AgpJson + [JsonPropertyName] en el DTO).
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  // ── Carga inicial: GET /api/corex/config/ntrip ──────────────────────────

  function load() {
    fetch('/api/corex/config/ntrip')
      .then(function (r) { return r.json(); })
      .then(function (d) {
        $('is_on').checked              = !!d.is_on;
        $('caster_url').value           = d.caster_url       || '';
        $('caster_ip').value            = d.caster_ip        || '';
        $('caster_port').value          = d.caster_port      || 2101;
        $('mount').value                = d.mount            || '';
        $('user_name').value            = d.user_name        || '';
        $('user_password').value        = d.user_password    || '';
        $('send_gga_interval').value    = d.send_gga_interval != null ? d.send_gga_interval : 0;
        $('is_gga_manual').value        = d.is_gga_manual ? 'manual' : 'gps';
        $('manual_lat').value           = d.manual_lat       || 0;
        $('manual_lon').value           = d.manual_lon       || 0;
        $('is_tcp').checked             = !!d.is_tcp;
        $('http_ver').value             = d.is_http10 ? '1.0' : '1.1';
        $('packet_size').value          = d.packet_size      || 512;

        // Destino: serial tiene prioridad si ambos estuviesen activos (no debería)
        if (d.send_to_serial) {
          $('dest_serial').checked = true;
        } else {
          $('dest_udp').checked = true;
        }

        $('send_to_udp_port').value     = d.send_to_udp_port || 2233;
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo cargar la configuración NTRIP.');
      });
  }

  // ── Guardar: POST /api/corex/config/ntrip ───────────────────────────────

  function save() {
    var btn = $('btn-save');
    btn.disabled = true;

    var destVal  = document.querySelector('input[name="dest"]:checked');
    var destSerial = destVal ? destVal.value === 'serial' : true;
    var destUdp    = destVal ? destVal.value === 'udp'    : false;

    var body = {
      is_on:              $('is_on').checked,
      caster_url:         $('caster_url').value.trim(),
      caster_ip:          $('caster_ip').value.trim(),
      caster_port:        parseInt($('caster_port').value, 10)    || 2101,
      mount:              $('mount').value.trim(),
      user_name:          $('user_name').value.trim(),
      user_password:      $('user_password').value,
      send_gga_interval:  parseInt($('send_gga_interval').value, 10) || 0,
      is_gga_manual:      $('is_gga_manual').value === 'manual',
      manual_lat:         parseFloat($('manual_lat').value)  || 0,
      manual_lon:         parseFloat($('manual_lon').value)  || 0,
      is_tcp:             $('is_tcp').checked,
      is_http10:          $('http_ver').value === '1.0',
      packet_size:        parseInt($('packet_size').value, 10) || 512,
      send_to_serial:     destSerial,
      send_to_udp:        destUdp,
      send_to_udp_port:   parseInt($('send_to_udp_port').value, 10) || 2233
    };

    fetch('/api/corex/config/ntrip', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
      .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, data: d }; }); })
      .then(function (res) {
        btn.disabled = false;
        if (!res.ok) {
          AgpModal.alert('Error al guardar', (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
          return;
        }
        if (res.data.restart) {
          waitForRestart();
        } else {
          AgpModal.alert('Guardado', 'Aplicado en caliente.');
        }
      })
      .catch(function () {
        btn.disabled = false;
        AgpModal.alert('Error de red', 'No se pudo guardar la configuración NTRIP.');
      });
  }

  // ── Esperar reinicio: poll /api/corex/status hasta que responda ──────────

  function waitForRestart() {
    AgpModal.alert('Reiniciando', 'CoreX se está reiniciando… esperá unos segundos.');
    // Timeout: 30 intentos × 2 s = 60 s. Si CoreX no vuelve, avisamos en vez
    // de dejar al operario mirando un modal colgado para siempre.
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

  // ── Mountpoints del caster (port de FormSource) ──────────────────────────
  // GET /api/corex/ntrip/mounts?ip=..&port=.. baja la sourcetable y devuelve
  // los mountpoints ya ordenados por distancia. Tocar una fila la elige como
  // mountpoint (falta Guardar, igual que el resto del formulario).

  function fmtDistKm(km) {
    if (typeof km !== 'number' || km < 0) return '—';
    return km < 100 ? km.toFixed(1) + ' km' : Math.round(km) + ' km';
  }

  function renderMounts(mounts) {
    var body = $('mnt-body');
    while (body.firstChild) body.removeChild(body.firstChild);

    var actual = $('mount').value.trim();

    mounts.forEach(function (m) {
      var tr = document.createElement('tr');
      if (m.mount === actual) tr.className = 'sel';

      [m.mount, m.format, m.nav_system].forEach(function (t) {
        var td = document.createElement('td');
        td.textContent = t || '—';
        tr.appendChild(td);
      });
      var tdDist = document.createElement('td');
      tdDist.className = 'num';
      tdDist.textContent = fmtDistKm(m.distance_km);
      tr.appendChild(tdDist);

      tr.addEventListener('click', function () {
        $('mount').value = m.mount;
        var sel = body.querySelector('tr.sel');
        if (sel) sel.className = '';
        tr.className = 'sel';
        $('mnt-hint').textContent = 'Elegido "' + m.mount + '"'
          + (m.distance_km >= 0 ? ' (' + fmtDistKm(m.distance_km) + ')' : '')
          + '. Acordate de Guardar.';
      });

      body.appendChild(tr);
    });

    $('mnt-wrap').style.display = mounts.length ? '' : 'none';
  }

  function buscarMounts() {
    var ip = $('caster_ip').value.trim() || $('caster_url').value.trim();
    var port = parseInt($('caster_port').value, 10);

    if (!ip || !port) {
      AgpModal.alert('Faltan datos', 'Completá la IP (o URL) y el puerto del caster.');
      return;
    }

    var btn = $('btn-mounts');
    btn.disabled = true;
    $('mnt-hint').textContent = 'Consultando el caster…';

    fetch('/api/corex/ntrip/mounts?ip=' + encodeURIComponent(ip)
        + '&port=' + encodeURIComponent(port), { cache: 'no-store' })
      .then(function (r) { return r.json().then(function (d) { return { st: r.status, d: d }; }); })
      .then(function (x) {
        if (x.st !== 200) {
          $('mnt-hint').textContent = (x.d && x.d.mensaje) || 'No se pudo consultar el caster.';
          renderMounts([]);
          return;
        }
        var mounts = x.d.mounts || [];
        $('mnt-hint').textContent = mounts.length
          ? mounts.length + ' mountpoints. Tocá uno para elegirlo (después Guardar).'
          : 'El caster no devolvió mountpoints.';
        renderMounts(mounts);
      })
      .catch(function () {
        $('mnt-hint').textContent = 'Error de red consultando el caster.';
      })
      .finally(function () { btn.disabled = false; });
  }

  // ── Inicialización ───────────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', function () {
    load();
    $('btn-save').addEventListener('click', save);
    $('btn-mounts').addEventListener('click', buscarMounts);
  });

})();
