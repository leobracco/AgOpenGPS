// ============================================================================
// radio.js — Radio RTCM de CoreX (port de FormRadio + FormRadioChannel).
//
// Estado local: la lista de canales se edita en el cliente (agregar/editar/
// borrar con AgpModal.text) y se persiste completa con Guardar, junto con
// puerto/baud/on-off/canal seleccionado. Sintonizar y el comando avanzado
// hablan con la radio ya mismo (POST /api/corex/radio/comando).
//
// Dot de estado: verde si la radio es la fuente RTCM activa y está conectada
// (status.ntrip.connected cubre radio: comparten pipeline), rojo si está
// encendida sin conexión, gris si está apagada.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  var radioOn = $('radioOn');
  var selPort = $('radioPort');
  var selBaud = $('radioBaud');
  var tbody = $('chBody');
  var chEmpty = $('chEmpty');
  var msg = $('radioMsg');
  var dot = $('dotRadio');

  var canales = [];      // [{id, name, frequency, location, distance_km}]
  var selFreq = '';      // frecuencia del canal seleccionado (setPort_radioChannel)
  var isOnCfg = false;   // último estado guardado (para el dot)

  // ── Render de la tabla de canales ─────────────────────────────────────────
  function fmtDist(km) {
    return (typeof km === 'number' && km >= 0) ? km.toFixed(1) + ' km' : '—';
  }

  function renderCanales() {
    tbody.innerHTML = '';
    canales.forEach(function (c) {
      var tr = document.createElement('tr');
      if (c.frequency === selFreq) tr.className = 'sel';

      [c.name, c.frequency, fmtDist(c.distance_km)].forEach(function (t) {
        var td = document.createElement('td');
        td.textContent = t || '—';
        tr.appendChild(td);
      });

      tr.addEventListener('click', function () {
        selFreq = c.frequency;
        renderCanales();
      });
      tbody.appendChild(tr);
    });
    chEmpty.style.display = canales.length ? 'none' : '';
  }

  function selCanal() {
    for (var i = 0; i < canales.length; i++) {
      if (canales[i].frequency === selFreq) return canales[i];
    }
    return null;
  }

  // ── Cargar config ──────────────────────────────────────────────────────────
  function fillPorts(ports, current) {
    selPort.innerHTML = '';
    var list = ports.slice();
    if (current && list.indexOf(current) < 0) list.unshift(current);
    if (!list.length) list.push('');
    list.forEach(function (p) {
      var o = document.createElement('option');
      o.value = o.textContent = p || '—';
      selPort.appendChild(o);
    });
    if (current) selPort.value = current;
  }

  function load() {
    fetch('/api/corex/config/radio', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.ok) return;
        radioOn.checked = isOnCfg = !!d.is_on;
        fillPorts(d.available_ports || [], d.port || '');
        if (d.baud) selBaud.value = d.baud;
        selFreq = d.channel || '';
        canales = d.channels || [];
        renderCanales();
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo cargar la configuración de radio.');
      });
  }

  // ── Guardar ────────────────────────────────────────────────────────────────
  function save() {
    var payload = {
      is_on: radioOn.checked,
      port: selPort.value === '—' ? '' : selPort.value,
      baud: selBaud.value,
      channel: selFreq,
      channels: canales.map(function (c) {
        return { id: c.id, name: c.name, frequency: c.frequency, location: c.location || '' };
      }),
    };

    $('btnRadioSave').disabled = true;
    fetch('/api/corex/config/radio', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
      .then(function (r) { return r.json().then(function (d) { return { st: r.status, d: d }; }); })
      .then(function (x) {
        if (x.st !== 200) {
          AgpModal.alert('No se guardó', (x.d && x.d.mensaje) || 'Error de validación.');
          return;
        }
        msg.textContent = radioOn.checked
          ? 'Guardado. La radio es ahora la fuente de corrección (NTRIP apagado).'
          : 'Guardado.';
        load();
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo guardar la configuración de radio.');
      })
      .finally(function () { $('btnRadioSave').disabled = false; });
  }

  // ── Alta / edición de canal (secuencia de diálogos táctiles) ──────────────
  function pedirCanal(base) {
    var out = { id: base.id, name: base.name, frequency: base.frequency, location: base.location };
    return AgpModal.text('Canal de radio', 'Nombre del canal', base.name || '')
      .then(function (nombre) {
        if (nombre === null || !nombre.trim()) return null;
        out.name = nombre.trim();
        return AgpModal.text('Canal de radio', 'Frecuencia (MHz, ej. 439.000)', base.frequency || '')
          .then(function (freq) {
            if (freq === null || !freq.trim()) return null;
            out.frequency = freq.trim();
            return AgpModal.text('Canal de radio',
              'Ubicación de la base "lat lon" (opcional, para la distancia)',
              base.location || '')
              .then(function (loc) {
                if (loc === null) return null;
                out.location = loc.trim();
                return out;
              });
          });
      });
  }

  $('btnChAdd').addEventListener('click', function () {
    var maxId = 0;
    canales.forEach(function (c) { if (c.id > maxId) maxId = c.id; });
    pedirCanal({ id: maxId + 1, name: '', frequency: '', location: '' })
      .then(function (c) {
        if (!c) return;
        canales.push(c);
        if (!selFreq) selFreq = c.frequency;
        renderCanales();
        msg.textContent = 'Canal agregado. Acordate de Guardar.';
      });
  });

  $('btnChEdit').addEventListener('click', function () {
    var c = selCanal();
    if (!c) { AgpModal.alert('Sin selección', 'Tocá primero el canal a editar.'); return; }
    pedirCanal(c).then(function (nuevo) {
      if (!nuevo) return;
      c.name = nuevo.name;
      c.frequency = nuevo.frequency;
      c.location = nuevo.location;
      selFreq = c.frequency;
      renderCanales();
      msg.textContent = 'Canal editado. Acordate de Guardar.';
    });
  });

  $('btnChDel').addEventListener('click', function () {
    var c = selCanal();
    if (!c) { AgpModal.alert('Sin selección', 'Tocá primero el canal a borrar.'); return; }
    AgpModal.confirm('Borrar canal', '¿Borrar "' + c.name + '" (' + c.frequency + ')?')
      .then(function (si) {
        if (!si) return;
        canales = canales.filter(function (x) { return x !== c; });
        if (selFreq === c.frequency) selFreq = '';
        renderCanales();
        msg.textContent = 'Canal borrado. Acordate de Guardar.';
      });
  });

  // ── Sintonizar y comando avanzado ──────────────────────────────────────────
  function mandarComando(texto, cb) {
    fetch('/api/corex/radio/comando', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ texto: texto }),
    })
      .then(function (r) { return r.json().then(function (d) { return { st: r.status, d: d }; }); })
      .then(function (x) {
        if (x.st !== 200) {
          AgpModal.alert('Radio', (x.d && x.d.mensaje) || 'No se pudo hablar con la radio.');
          cb(null);
          return;
        }
        cb(x.d.respuesta || '');
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo hablar con la radio.');
        cb(null);
      });
  }

  $('btnChTune').addEventListener('click', function () {
    var c = selCanal();
    if (!c) { AgpModal.alert('Sin selección', 'Tocá primero el canal a sintonizar.'); return; }
    var btn = $('btnChTune');
    btn.disabled = true;
    mandarComando('SL&F=' + c.frequency, function (resp) {
      btn.disabled = false;
      if (resp !== null) {
        msg.textContent = 'Frecuencia ' + c.frequency + ' enviada a la radio.';
      }
    });
  });

  $('btnCmdSend').addEventListener('click', function () {
    var t = $('cmdTexto').value.trim();
    if (!t) return;
    var btn = $('btnCmdSend');
    btn.disabled = true;
    mandarComando(t, function (resp) {
      btn.disabled = false;
      if (resp !== null) $('cmdResp').textContent = resp || '(sin respuesta)';
    });
  });

  $('btnRadioRescan').addEventListener('click', load);
  $('btnRadioSave').addEventListener('click', save);

  // ── Paso serial RTCM (port de FormSerialPass) ──────────────────────────────
  // Comparte puerto/baud con el panel Conexión (mismos settings de radio).
  function loadPass() {
    fetch('/api/corex/config/pass', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.ok) return;
        $('passOn').checked = !!d.is_on;
        $('passToSerial').checked = !!d.send_to_serial;
        $('passToUdp').checked = !!d.send_to_udp;
        $('passUdpPort').value = d.send_to_udp_port || 2233;
      })
      .catch(function () { /* la carga principal ya avisó si no hay backend */ });
  }

  $('btnPassSave').addEventListener('click', function () {
    AgpModal.confirm('Paso serial',
      'Guardar reinicia CoreX para aplicar el cambio. ¿Continuar?')
      .then(function (si) {
        if (!si) return;
        var btn = $('btnPassSave');
        btn.disabled = true;
        fetch('/api/corex/config/pass', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            is_on: $('passOn').checked,
            port: selPort.value === '—' ? '' : selPort.value,
            baud: selBaud.value,
            send_to_serial: $('passToSerial').checked,
            send_to_udp: $('passToUdp').checked,
            send_to_udp_port: parseInt($('passUdpPort').value, 10) || 2233,
          }),
        })
          .then(function (r) { return r.json().then(function (d) { return { st: r.status, d: d }; }); })
          .then(function (x) {
            if (x.st !== 200) {
              AgpModal.alert('No se guardó', (x.d && x.d.mensaje) || 'Error de validación.');
              btn.disabled = false;
              return;
            }
            msg.textContent = 'Guardado. CoreX se está reiniciando…';
            // CoreX reinicia en ~1 s; recargamos cuando vuelva.
            setTimeout(function () { location.reload(); }, 4000);
          })
          .catch(function () {
            AgpModal.alert('Error de red', 'No se pudo guardar el paso serial.');
            btn.disabled = false;
          });
      });
  });

  // ── Dot de estado @2s ──────────────────────────────────────────────────────
  function pollStatus() {
    fetch('/api/corex/status', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.ok) return;
        dot.classList.remove('on', 'bad');
        if (!isOnCfg) return; // radio apagada → gris
        dot.classList.add(d.ntrip && d.ntrip.connected ? 'on' : 'bad');
      })
      .catch(function () { /* offline */ });
  }

  load();
  loadPass();
  pollStatus();
  var interval = setInterval(pollStatus, 2000);
  window.addEventListener('pagehide', function () { clearInterval(interval); });
}());
