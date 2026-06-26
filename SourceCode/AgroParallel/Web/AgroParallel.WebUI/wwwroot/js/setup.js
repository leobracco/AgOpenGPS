// ============================================================================
// setup.js — wizard de primera vez del Hub PilotX.
// Lee /api/setup/estado para checks live + marca pasos completados via
// /api/setup/paso. Aceptación de nodos delega a /api/nodos/aceptar.
// ============================================================================

(function () {
  'use strict';

  var stepper = document.getElementById('stepper');
  var panels = [0, 1, 2, 3, 4, 5, 6].map(function (i) { return document.getElementById('panel' + i); });
  var current = 0;

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function setStep(i) {
    if (i < 0 || i >= panels.length) return;
    current = i;
    panels.forEach(function (p, idx) { if (p) p.hidden = idx !== i; });
    if (stepper) {
      stepper.querySelectorAll('.step').forEach(function (s) {
        var n = parseInt(s.getAttribute('data-step'), 10);
        s.classList.toggle('active', n === i);
        s.classList.toggle('done', n < i);
      });
    }
    // Cada vez que entramos a un paso lo marcamos en backend (audit/state).
    var pasoNombre = ['', 'pc_ok', 'orbitx', 'encender', 'aceptar', 'configurar', ''][i];
    if (pasoNombre) {
      postJson('/api/setup/paso', { paso: pasoNombre, valor: true }).catch(function () {});
    }
  }

  function postJson(url, body) {
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    }).then(function (r) { return r.json(); });
  }

  // Botones de navegación "data-go" para todos los paneles.
  document.querySelectorAll('button[data-go]').forEach(function (b) {
    b.addEventListener('click', function () { setStep(parseInt(b.getAttribute('data-go'), 10)); });
  });
  var btnNext0 = document.getElementById('btnNext0');
  if (btnNext0) btnNext0.addEventListener('click', function () { setStep(1); });

  var btnFinish = document.getElementById('btnFinish');
  if (btnFinish) {
    btnFinish.addEventListener('click', async function () {
      try { await postJson('/api/setup/completar', { valor: true }); } catch (e) {}
      setStep(6);
    });
  }

  var btnDismiss = document.getElementById('btnDismiss');
  if (btnDismiss) {
    btnDismiss.addEventListener('click', async function () {
      try { await postJson('/api/setup/dismiss', { valor: true }); } catch (e) {}
      window.location.href = 'hub.html';
    });
  }

  // ---------- Chequeos live ----------
  function setCheck(id, state, sub) {
    var el = document.getElementById(id);
    if (!el) return;
    el.classList.remove('ok', 'bad', 'idle');
    el.classList.add(state);
    var ico = el.querySelector('.ico');
    if (ico) ico.textContent = state === 'ok' ? '✓' : state === 'bad' ? '✗' : '⋯';
    var subEl = document.getElementById(id + 'Sub');
    if (subEl && sub != null) subEl.textContent = sub;
  }

  async function pollEstado() {
    try {
      var res = await fetch('/api/setup/estado', { cache: 'no-store' });
      var data = await res.json();
      if (!data || !data.ok) return;
      var live = data.live || {};

      // Paso 1 — chequeos PC
      setCheck('chkWeb', 'ok', 'sirviendo en 127.0.0.1:5180');
      setCheck('chkBroker', live.broker_connected ? 'ok' : 'bad',
        live.broker_connected
          ? 'conectado'
          : 'sin conexión — verificá que CoreX esté corriendo');
      // chkAog: lo verificamos por separado contra /api/aog/state
      try {
        var aog = await fetch('/api/aog/state', { cache: 'no-store' }).then(function (r) { return r.json(); });
        if (aog) setCheck('chkAog', 'ok', 'snapshot OK');
        else setCheck('chkAog', 'bad', 'sin respuesta');
      } catch (e) {
        setCheck('chkAog', 'bad', 'sin respuesta');
      }

      // Paso 2 — OrbitX
      var orbStat = document.getElementById('orbitxStat');
      if (orbStat) {
        if (live.orbitx_vinculado) {
          orbStat.innerHTML = '<span class="dot-on" style="color:var(--agp-state-ok)">●</span> <span><strong style="color:var(--agp-text)">Vinculado</strong> · device-id presente</span>';
        } else {
          orbStat.innerHTML = '<span class="dot-off" style="color:var(--agp-text-muted)">○</span> <span>No vinculado — podés saltar este paso</span>';
        }
      }

      // Paso 3/4 — broker + nodos
      var lbr = document.getElementById('liveBroker');
      if (lbr) lbr.textContent = live.broker_connected ? 'conectado' : 'desconectado';
      var ldesc = document.getElementById('liveDescubiertos');
      if (ldesc) ldesc.textContent = live.nodos_descubiertos != null ? live.nodos_descubiertos : 0;
      var lace = document.getElementById('liveAceptados');
      if (lace) lace.textContent = live.nodos_aceptados != null ? live.nodos_aceptados : 0;
      var lpen = document.getElementById('livePendientes');
      if (lpen) lpen.textContent = live.nodos_pendientes != null ? live.nodos_pendientes : 0;
    } catch (e) { /* offline */ }
  }

  // ---------- Tabla de pendientes (paso 4) ----------
  function pageForTipo(tipo) {
    if (!tipo) return null;
    var t = tipo.toLowerCase();
    if (t.indexOf('quantix') >= 0) return 'quantix.html';
    if (t.indexOf('vistax')  >= 0) return 'vistax.html';
    if (t.indexOf('sectionx') >= 0) return 'sectionx.html';
    if (t.indexOf('flowx')   >= 0 || t.indexOf('flow')  >= 0) return 'flowx.html';
    if (t.indexOf('stormx')  >= 0 || t.indexOf('storm') >= 0) return 'stormx.html';
    return null;
  }

  async function pollUnified() {
    try {
      var res = await fetch('/api/nodos/unified', { cache: 'no-store' });
      var data = await res.json();
      if (!data || !data.ok) return;
      var nodos = data.nodos || [];

      // Tabla de pendientes
      var tbody = document.querySelector('#tblPendientes tbody');
      var empPend = document.getElementById('empPend');
      if (tbody) {
        tbody.innerHTML = '';
        var pendientes = nodos.filter(function (n) { return n.estado === 'pendiente'; });
        if (pendientes.length === 0) {
          if (empPend) empPend.hidden = false;
        } else {
          if (empPend) empPend.hidden = true;
          pendientes.forEach(function (n) {
            var tr = document.createElement('tr');
            tr.innerHTML =
              '<td style="font-family: var(--agp-font-mono); font-size: 0.9em;">' + escapeHtml(n.uid) + '</td>' +
              '<td>' + escapeHtml(n.tipo || '—') + '</td>' +
              '<td>' + escapeHtml(n.ip || '—') + '</td>' +
              '<td>' + escapeHtml(n.firmware || '—') + '</td>' +
              '<td style="text-align:right; white-space:nowrap;">' +
                '<button class="btn" data-act="aceptar" data-uid="' + escapeHtml(n.uid) + '" data-tipo="' + escapeHtml(n.tipo || '') + '">Aceptar</button> ' +
                '<button class="btn" data-act="ignorar" data-uid="' + escapeHtml(n.uid) + '">Ignorar</button>' +
              '</td>';
            tbody.appendChild(tr);
          });
        }
      }

      // Lista de aceptados (paso 5)
      var list = document.getElementById('listConfig');
      if (list) {
        var aceptados = nodos.filter(function (n) { return n.estado === 'aceptado' || n.estado === 'offline'; });
        if (aceptados.length === 0) {
          list.innerHTML = '<div class="subtitle">No hay nodos aceptados todavía. Volvé al paso 4 para aceptar los pendientes.</div>';
        } else {
          list.innerHTML = aceptados.map(function (n) {
            var page = pageForTipo(n.tipo);
            var pageBtn = page
              ? '<a class="btn primary" href="' + page + '">Configurar</a>'
              : '<button class="btn" disabled>Sin página</button>';
            return '<div class="led-card">' +
                     '<span class="pictogram">⚙</span>' +
                     '<div style="flex:1">' +
                       '<strong>' + escapeHtml(n.alias || n.uid) + '</strong> ' +
                       '<span class="tipo-pill" style="margin-left:6px;">' + escapeHtml(n.tipo || '—') + '</span>' +
                       '<div class="subtitle uid-mono">' + escapeHtml(n.uid) + '</div>' +
                     '</div>' +
                     pageBtn +
                   '</div>';
          }).join('');
        }
      }
    } catch (e) { /* offline */ }
  }

  // Delegación de eventos para el paso 4
  document.addEventListener('click', async function (e) {
    var btn = e.target.closest && e.target.closest('button[data-act]');
    if (!btn) return;
    var act = btn.getAttribute('data-act');
    var uid = btn.getAttribute('data-uid');
    var tipo = btn.getAttribute('data-tipo') || '';
    if (!uid) return;
    if (act === 'aceptar') {
      var alias = window.prompt('Alias humano (ej: "Motor izquierdo"):', 'Nodo ' + uid);
      if (alias == null) return;
      alias = alias.trim();
      if (!alias) return;
      try { await postJson('/api/nodos/aceptar', { uid: uid, tipo: tipo, alias: alias }); } catch (e) {}
      pollUnified();
    } else if (act === 'ignorar') {
      if (!window.confirm('Ignorar este nodo? No volverá a aparecer.')) return;
      try { await postJson('/api/nodos/ignorar', { uid: uid }); } catch (e) {}
      pollUnified();
    }
  });

  // Polling — pausamos si la tab está oculta
  var pHandle = null, uHandle = null;
  function startPoll() {
    if (!pHandle) pHandle = setInterval(pollEstado, 2000);
    if (!uHandle) uHandle = setInterval(pollUnified, 2000);
  }
  function stopPoll() {
    if (pHandle) { clearInterval(pHandle); pHandle = null; }
    if (uHandle) { clearInterval(uHandle); uHandle = null; }
  }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stopPoll();
    else { pollEstado(); pollUnified(); startPoll(); }
  });

  pollEstado();
  pollUnified();
  startPoll();
})();
