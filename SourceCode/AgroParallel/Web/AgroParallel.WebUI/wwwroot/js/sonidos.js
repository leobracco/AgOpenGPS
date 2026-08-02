// ============================================================================
// sonidos.js — pantalla de alarmas sonoras: qué avisa, con qué sonido, cuándo.
// Config en /api/sonidos/config; los wav viven en /sounds y se prueban acá
// mismo con <audio>. El que suena en cabina es el Desktop (poller nativo) —
// esta pantalla configura y muestra las alarmas activas.
// ============================================================================
(function () {
  'use strict';

  var el = function (id) { return document.getElementById(id); };
  var cfg = null;
  var archivos = [];

  async function jget(u) {
    var r = await fetch(u, { cache: 'no-store' });
    return JSON.parse((await r.text()).replace(/^﻿/, ''));
  }

  function fila(ev) {
    var esDosis = ev.id === 'dosis_baja' || ev.id === 'dosis_alta';
    var conSostenido = ev.id !== 'piloto_on' && ev.id !== 'piloto_off';
    var opts = archivos.map(function (a) {
      return '<option value="' + a + '"' + (a === ev.sonido ? ' selected' : '') + '>' + a + '</option>';
    }).join('');
    return '<div class="sn-fila' + (ev.habilitado ? '' : ' off') + '" data-id="' + ev.id + '">' +
      '<label style="display:flex;align-items:center;gap:8px;cursor:pointer">' +
        '<input type="checkbox" data-f="habilitado"' + (ev.habilitado ? ' checked' : '') + ' style="width:22px;height:22px">' +
      '</label>' +
      '<div class="sn-nombre">' + ev.nombre + '<small>' + ev.id + '</small></div>' +
      '<select data-f="sonido">' + opts + '</select>' +
      '<button type="button" class="btn" data-play="' + ev.id + '">▶</button>' +
      (esDosis
        ? '<span class="sn-mini">umbral ±</span><input class="sn-num" type="number" data-f="umbral_pct" min="1" max="50" step="1" value="' + (ev.umbral_pct || 10) + '"><span class="sn-mini">%</span>'
        : '') +
      (conSostenido
        ? '<span class="sn-mini">sostenido</span><input class="sn-num" type="number" data-f="sostenido_seg" min="0" max="60" step="1" value="' + (ev.sostenido_seg || 0) + '"><span class="sn-mini">s</span>' +
          '<span class="sn-mini">repetir</span><input class="sn-num" type="number" data-f="repetir_seg" min="0" max="120" step="1" value="' + (ev.repetir_seg || 0) + '"><span class="sn-mini">s</span>'
        : '') +
      '</div>';
  }

  function pintar() {
    el('snLista').innerHTML = cfg.eventos.map(fila).join('');
    var mute = el('btnMute');
    mute.classList.toggle('on', !!cfg.mute);
    mute.textContent = cfg.mute ? '🔇 SILENCIADO — tocar para activar' : '🔇 Silenciar todo';
  }

  function leerPantalla() {
    document.querySelectorAll('.sn-fila').forEach(function (f) {
      var ev = cfg.eventos.filter(function (e) { return e.id === f.getAttribute('data-id'); })[0];
      if (!ev) return;
      f.querySelectorAll('[data-f]').forEach(function (inp) {
        var k = inp.getAttribute('data-f');
        if (k === 'habilitado') ev.habilitado = inp.checked;
        else if (k === 'sonido') ev.sonido = inp.value;
        else ev[k] = parseFloat(inp.value) || 0;
      });
    });
  }

  el('snLista').addEventListener('click', function (evn) {
    var play = evn.target.getAttribute && evn.target.getAttribute('data-play');
    if (play) {
      var fl = evn.target.closest('.sn-fila');
      var snd = fl.querySelector('select[data-f="sonido"]').value;
      if (snd) new Audio('../sounds/' + snd).play().catch(function () {});
      return;
    }
    // toggle visual al habilitar/deshabilitar
    if (evn.target.getAttribute && evn.target.getAttribute('data-f') === 'habilitado') {
      evn.target.closest('.sn-fila').classList.toggle('off', !evn.target.checked);
    }
  });

  el('btnMute').addEventListener('click', async function () {
    cfg.mute = !cfg.mute;
    leerPantalla();
    await guardar(false);
    pintar();
  });

  el('btnGuardar').addEventListener('click', function () { leerPantalla(); guardar(true); });

  async function guardar(avisar) {
    try {
      var r = await fetch('/api/sonidos/config', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var d = JSON.parse((await r.text()).replace(/^﻿/, ''));
      if (avisar) {
        el('snMsg').textContent = d.ok ? '✓ guardado' : '✕ no se pudo guardar';
        el('snMsg').className = 'send-msg ' + (d.ok ? 'ok' : 'err');
      }
    } catch (e) {
      if (avisar) { el('snMsg').textContent = '✕ ' + e.message; el('snMsg').className = 'send-msg err'; }
    }
  }

  // subir un wav propio
  el('btnSubir').addEventListener('click', function () { el('snFile').click(); });
  el('snFile').addEventListener('change', function () {
    var f = this.files && this.files[0];
    this.value = '';
    if (!f) return;
    var reader = new FileReader();
    reader.onload = async function () {
      try {
        var r = await fetch('/api/sonidos/archivos?nombre=' + encodeURIComponent(f.name), {
          method: 'POST', body: reader.result
        });
        var d = JSON.parse((await r.text()).replace(/^﻿/, ''));
        if (d.ok) {
          el('snMsg').textContent = '✓ ' + d.archivo + ' subido';
          el('snMsg').className = 'send-msg ok';
          archivos = (await jget('/api/sonidos/archivos')).archivos || [];
          leerPantalla(); pintar();
        } else {
          el('snMsg').textContent = '✕ ' + (d.error || 'no se pudo subir');
          el('snMsg').className = 'send-msg err';
        }
      } catch (e) {
        el('snMsg').textContent = '✕ ' + e.message; el('snMsg').className = 'send-msg err';
      }
    };
    reader.readAsArrayBuffer(f);
  });

  // alarmas activas (solo mostrar — el sonido lo pone el Desktop)
  async function refrescarActivas() {
    try {
      var d = await jget('/api/sonidos/estado?desde=999999999');
      var box = el('snActivas');
      if (!d.activas || !d.activas.length) {
        box.innerHTML = '<p class="sn-mini">Sin alarmas activas.</p>';
      } else {
        box.innerHTML = d.activas.map(function (a) {
          return '<div class="sn-activa"><b>' + a.evento + '</b> — ' + (a.detalle || '') + '</div>';
        }).join('');
      }
    } catch (e) {}
  }

  document.addEventListener('DOMContentLoaded', async function () {
    try {
      archivos = (await jget('/api/sonidos/archivos')).archivos || [];
      cfg = await jget('/api/sonidos/config');
      pintar();
    } catch (e) {
      el('snLista').innerHTML = '<p class="sn-mini">Sin conexión con PilotX.</p>';
    }
    refrescarActivas();
    setInterval(refrescarActivas, 2000);
  });
})();
