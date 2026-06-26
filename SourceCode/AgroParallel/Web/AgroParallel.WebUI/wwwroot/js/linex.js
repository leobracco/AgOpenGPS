// ============================================================================
// linex.js — editor de config + monitor live de LineX (corte surco por surco).
//
// Los nodos se descubren solos vía agp/linex/{uid}/announcement (GET
// /api/linex/nodos). Por cada nodo, lineX.json guarda: tipo de placa
// (servo|embrague), parámetros del módulo y la lista de surcos con su mapeo a
// secciones de PilotX. El estado live (qué surco está abierto) viene de
// /api/linex/live.
//
// Selector servo/embrague: es PC-side. El firmware recibe el mismo bitmask de
// secciones en ambos casos; lo único que cambia acá es qué columnas de servo
// (ángulos/pulsos/rampa) se muestran. Para embrague el corte es on/off binario.
// ============================================================================

(function () {
  'use strict';

  var nodoSel    = document.getElementById('nodoSel');
  var enabledChk = document.getElementById('enabledChk');
  var statusChip = document.getElementById('lxStatusChip');
  var liveEl     = document.getElementById('liveSurcos');
  var boardSeg   = document.getElementById('boardSeg');
  var surcoBody  = document.getElementById('surcoBody');
  var jsonOut    = document.getElementById('jsonOut');
  var noNodosEl  = document.getElementById('lxNoNodos');
  var hintEl     = document.getElementById('hint');
  var toastEl    = document.getElementById('lxToast');
  var toastTimer = null;

  var cfgNombre       = document.getElementById('cfgNombre');
  var cfgSectionCount = document.getElementById('cfgSectionCount');
  var cfgPwmFreq      = document.getElementById('cfgPwmFreq');
  var cfgOePin        = document.getElementById('cfgOePin');
  var cfgCommTimeout  = document.getElementById('cfgCommTimeout');

  var btnSave      = document.getElementById('btnSave');
  var btnPush      = document.getElementById('btnPush');
  var btnTestOpen  = document.getElementById('btnTestOpen');
  var btnTestClose = document.getElementById('btnTestClose');

  var cfg = null;          // lineX.json en memoria
  var nodos = [];          // nodos LineX descubiertos
  var selectedUid = null;
  var liveTimer = null;

  function toast(msg, kind, ms) {
    if (!toastEl) return;
    toastEl.textContent = msg;
    toastEl.className = 'show ' + (kind || '');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.className = ''; }, ms || 2400);
  }

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function api(path, opts) {
    return fetch('/api' + path, opts).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  }

  // -------- config en memoria --------
  function currentNode() {
    if (!cfg || !selectedUid) return null;
    if (!cfg.nodos) cfg.nodos = [];
    for (var i = 0; i < cfg.nodos.length; i++) {
      if ((cfg.nodos[i].uid || '').toLowerCase() === selectedUid.toLowerCase()) return cfg.nodos[i];
    }
    // Stub nuevo para un nodo recién descubierto.
    var stub = {
      uid: selectedUid,
      nombre: 'Nodo LineX',
      habilitado: true,
      board_type: 'servo',
      section_count: 7,
      pwm_freq: 50,
      output_enable_pin: 27,
      comm_timeout_ms: 3000,
      surcos: []
    };
    cfg.nodos.push(stub);
    return stub;
  }

  // Garantiza que el nodo tenga `section_count` surcos (rellena con defaults).
  function ensureSurcos(node) {
    if (!node.surcos) node.surcos = [];
    var n = Math.max(1, Math.min(16, parseInt(node.section_count, 10) || 7));
    for (var i = 0; i < n; i++) {
      if (!node.surcos[i]) {
        node.surcos[i] = {
          idx: i, backend: 0, channel: i,
          open_angle: 0, close_angle: 90,
          min_us: 500, max_us: 2500, travel_ms: 400,
          failsafe_open: false, invert: false, seccion_aog: 0
        };
      } else {
        node.surcos[i].idx = i;
      }
    }
    node.surcos.length = n; // recorta si bajó el count
  }

  function isServo(node) { return (node.board_type || 'servo') !== 'embrague'; }

  // -------- render --------
  function applyBoardVisibility(node) {
    var servo = isServo(node);
    document.querySelectorAll('.servo-only').forEach(function (el) {
      el.classList.toggle('hidden', !servo);
    });
    if (boardSeg) {
      boardSeg.querySelectorAll('button').forEach(function (b) {
        b.classList.toggle('active', (b.getAttribute('data-board') === (servo ? 'servo' : 'embrague')));
      });
    }
  }

  function renderForm() {
    var node = currentNode();
    if (!node) return;
    ensureSurcos(node);

    cfgNombre.value       = node.nombre || '';
    cfgSectionCount.value = node.section_count || 7;
    cfgPwmFreq.value      = node.pwm_freq || 50;
    cfgOePin.value        = node.output_enable_pin != null ? node.output_enable_pin : 27;
    cfgCommTimeout.value  = node.comm_timeout_ms || 3000;
    enabledChk.checked    = !!cfg.enabled;

    applyBoardVisibility(node);
    renderSurcoRows(node);
    renderJson();
  }

  function renderSurcoRows(node) {
    var servo = isServo(node);
    var html = [];
    for (var i = 0; i < node.surcos.length; i++) {
      var s = node.surcos[i];
      html.push(
        '<tr data-i="' + i + '">' +
          '<td class="num">' + (i + 1) + '</td>' +
          '<td><input type="number" data-k="channel" min="0" max="39" value="' + (s.channel | 0) + '"></td>' +
          '<td class="servo-only' + (servo ? '' : ' hidden') + '"><input type="number" data-k="open_angle" min="0" max="180" value="' + (s.open_angle | 0) + '"></td>' +
          '<td class="servo-only' + (servo ? '' : ' hidden') + '"><input type="number" data-k="close_angle" min="0" max="180" value="' + (s.close_angle | 0) + '"></td>' +
          '<td class="servo-only' + (servo ? '' : ' hidden') + '"><input type="number" data-k="min_us" min="200" max="3000" value="' + (s.min_us | 0) + '"></td>' +
          '<td class="servo-only' + (servo ? '' : ' hidden') + '"><input type="number" data-k="max_us" min="200" max="3000" value="' + (s.max_us | 0) + '"></td>' +
          '<td class="servo-only' + (servo ? '' : ' hidden') + '"><input type="number" data-k="travel_ms" min="0" max="3000" value="' + (s.travel_ms | 0) + '"></td>' +
          '<td><input type="checkbox" data-k="invert"' + (s.invert ? ' checked' : '') + '></td>' +
          '<td><input type="checkbox" data-k="failsafe_open"' + (s.failsafe_open ? ' checked' : '') + '></td>' +
          '<td><input type="number" data-k="seccion_aog" min="0" max="16" value="' + (s.seccion_aog | 0) + '"></td>' +
          '<td>' +
            '<button class="btn" type="button" data-test="open" title="Abrir surco">▲</button> ' +
            '<button class="btn" type="button" data-test="close" title="Cerrar surco">▼</button>' +
          '</td>' +
        '</tr>'
      );
    }
    surcoBody.innerHTML = html.join('');
  }

  function renderJson() {
    try { jsonOut.value = JSON.stringify(cfg, null, 2); } catch (e) { jsonOut.value = '(error)'; }
  }

  function renderNodoSel() {
    var opts = [];
    if (!nodos.length) {
      opts.push('<option value="">(sin nodos)</option>');
    } else {
      for (var i = 0; i < nodos.length; i++) {
        var u = nodos[i].uid || '';
        var label = (nodos[i].nombre || 'LineX') + ' · ' + u + (nodos[i].online ? '' : ' (offline)');
        opts.push('<option value="' + escapeHtml(u) + '">' + escapeHtml(label) + '</option>');
      }
    }
    nodoSel.innerHTML = opts.join('');
    if (selectedUid) nodoSel.value = selectedUid;
    else if (nodos.length) { selectedUid = nodos[0].uid; nodoSel.value = selectedUid; }
    if (noNodosEl) noNodosEl.style.display = nodos.length ? 'none' : '';
  }

  // -------- pull edits desde el DOM al modelo --------
  function syncFromForm() {
    var node = currentNode();
    if (!node) return;
    node.nombre = cfgNombre.value.trim() || 'Nodo LineX';
    node.section_count = Math.max(1, Math.min(16, parseInt(cfgSectionCount.value, 10) || 7));
    node.pwm_freq = parseInt(cfgPwmFreq.value, 10) || 50;
    node.output_enable_pin = parseInt(cfgOePin.value, 10);
    if (isNaN(node.output_enable_pin)) node.output_enable_pin = 27;
    node.comm_timeout_ms = parseInt(cfgCommTimeout.value, 10) || 3000;
    cfg.enabled = !!enabledChk.checked;
    ensureSurcos(node);

    var rows = surcoBody.querySelectorAll('tr');
    rows.forEach(function (tr) {
      var i = parseInt(tr.getAttribute('data-i'), 10);
      var s = node.surcos[i];
      if (!s) return;
      tr.querySelectorAll('[data-k]').forEach(function (inp) {
        var k = inp.getAttribute('data-k');
        if (inp.type === 'checkbox') s[k] = inp.checked;
        else s[k] = parseInt(inp.value, 10) || 0;
      });
      s.idx = i;
    });
  }

  // -------- payload canónico que entiende el firmware (config) --------
  function buildFirmwareConfig(node) {
    var sections = [];
    for (var i = 0; i < node.surcos.length; i++) {
      var s = node.surcos[i];
      sections.push({
        idx: i, backend: s.backend | 0, channel: s.channel | 0,
        open_angle: s.open_angle | 0, close_angle: s.close_angle | 0,
        min_us: s.min_us | 0, max_us: s.max_us | 0, travel_ms: s.travel_ms | 0,
        failsafe_open: !!s.failsafe_open, invert: !!s.invert
      });
    }
    return {
      mdl: {
        section_count: node.section_count | 0,
        pwm_freq: node.pwm_freq | 0,
        output_enable_pin: node.output_enable_pin | 0,
        comm_timeout_ms: node.comm_timeout_ms | 0
      },
      sections: sections
    };
  }

  // -------- live --------
  function renderLive(snap) {
    if (statusChip) {
      var on = snap && snap.monitoreo_activo;
      statusChip.className = 'lx-status ' + (on ? 'green' : 'gray');
      statusChip.querySelector('.lbl').textContent = on ? 'monitoreando' : 'inactivo';
    }
    if (!selectedUid) { liveEl.innerHTML = '<div class="subtitle">Seleccioná un nodo…</div>'; return; }
    var nodo = null;
    if (snap && snap.nodos) {
      for (var i = 0; i < snap.nodos.length; i++) {
        if ((snap.nodos[i].uid || '').toLowerCase() === selectedUid.toLowerCase()) { nodo = snap.nodos[i]; break; }
      }
    }
    if (!nodo) { liveEl.innerHTML = '<div class="subtitle">Sin telemetría del nodo todavía.</div>'; return; }

    var head = '<div style="display:flex; gap:var(--agp-sp-3); align-items:center; margin-bottom: var(--agp-sp-2)">' +
      '<strong>' + (nodo.online ? '● en línea' : '○ offline') + '</strong>' +
      '<span class="subtitle" style="margin:0">RSSI ' + (nodo.rssi || 0) + ' dBm · uptime ' + (nodo.uptime || 0) + ' s</span>' +
      '</div>';
    var cells = [];
    var surcos = nodo.surcos || [];
    if (!surcos.length) {
      cells.push('<div class="subtitle">El nodo no reportó surcos.</div>');
    } else {
      for (var j = 0; j < surcos.length; j++) {
        var su = surcos[j];
        var open = !!su.abierto;
        cells.push(
          '<div class="surco-cell' + (open ? ' open' : '') + '">' +
            '<span class="num">' + ((su.id | 0) + 1) + '</span>' +
            '<span class="st">' + (open ? 'ABIERTO' : 'cerrado') + '</span>' +
            (isServoLive(nodo) ? '<span class="st">' + (su.angle | 0) + '°</span>' : '') +
          '</div>'
        );
      }
    }
    liveEl.innerHTML = head + '<div class="surco-grid">' + cells.join('') + '</div>';
  }

  function isServoLive(nodo) {
    return (nodo.board_type || 'servo') !== 'embrague';
  }

  function pollLive() {
    api('/linex/live').then(renderLive).catch(function () {
      if (statusChip) {
        statusChip.className = 'lx-status gray';
        statusChip.querySelector('.lbl').textContent = 'sin conexión';
      }
    });
  }

  // -------- acciones --------
  function doSave() {
    syncFromForm();
    renderJson();
    api('/linex/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cfg)
    }).then(function (r) {
      if (r && r.ok) toast('Configuración guardada', 'ok');
      else toast('No se pudo guardar', 'bad');
    }).catch(function () { toast('Error al guardar', 'bad'); });
  }

  function doPush() {
    syncFromForm();
    var node = currentNode();
    if (!node || !selectedUid) { toast('Seleccioná un nodo', 'warn'); return; }
    var payload = buildFirmwareConfig(node);
    api('/linex/' + encodeURIComponent(selectedUid) + '/config-push', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).then(function (r) {
      if (r && r.ok) toast('Configuración enviada al nodo', 'ok');
      else toast('No se pudo enviar (¿nodo offline?)', 'bad');
    }).catch(function () { toast('Error al enviar', 'bad'); });
  }

  // Abre/cierra todos los surcos del nodo (prueba). Itera el topic /test por canal.
  function doTestAll(state) {
    var node = currentNode();
    if (!node || !selectedUid) { toast('Seleccioná un nodo', 'warn'); return; }
    syncFromForm();
    var chain = Promise.resolve();
    node.surcos.forEach(function (s) {
      chain = chain.then(function () {
        return api('/linex/' + encodeURIComponent(selectedUid) + '/test', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ch: s.channel | 0, state: state })
        }).catch(function () {});
      });
    });
    chain.then(function () {
      toast(state === 'open' ? 'Surcos abiertos (prueba)' : 'Surcos cerrados (prueba)', 'ok');
    });
  }

  function doTestRow(i, state) {
    var node = currentNode();
    if (!node || !selectedUid) return;
    syncFromForm();
    var s = node.surcos[i];
    if (!s) return;
    api('/linex/' + encodeURIComponent(selectedUid) + '/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ch: s.channel | 0, state: state })
    }).then(function () {
      toast('Surco ' + (i + 1) + ' → ' + (state === 'open' ? 'abierto' : 'cerrado'), 'ok', 1400);
    }).catch(function () { toast('Error en prueba', 'bad'); });
  }

  // -------- wiring --------
  function onBoardClick(e) {
    var b = e.target.closest('button[data-board]');
    if (!b) return;
    var node = currentNode();
    if (!node) return;
    node.board_type = b.getAttribute('data-board');
    applyBoardVisibility(node);
    renderSurcoRows(node);
    renderJson();
  }

  function onSurcoBodyClick(e) {
    var tb = e.target.closest('button[data-test]');
    if (!tb) return;
    var tr = tb.closest('tr');
    if (!tr) return;
    doTestRow(parseInt(tr.getAttribute('data-i'), 10), tb.getAttribute('data-test'));
  }

  function onSectionCountChange() {
    var node = currentNode();
    if (!node) return;
    node.section_count = Math.max(1, Math.min(16, parseInt(cfgSectionCount.value, 10) || 7));
    ensureSurcos(node);
    renderSurcoRows(node);
    renderJson();
  }

  function init() {
    boardSeg.addEventListener('click', onBoardClick);
    surcoBody.addEventListener('click', onSurcoBodyClick);
    cfgSectionCount.addEventListener('change', onSectionCountChange);
    nodoSel.addEventListener('change', function () { selectedUid = nodoSel.value; renderForm(); });
    btnSave.addEventListener('click', doSave);
    btnPush.addEventListener('click', doPush);
    btnTestOpen.addEventListener('click', function () { doTestAll('open'); });
    btnTestClose.addEventListener('click', function () { doTestAll('close'); });
    enabledChk.addEventListener('change', function () { cfg.enabled = !!enabledChk.checked; renderJson(); });

    Promise.all([
      api('/linex/config').catch(function () { return new_default(); }),
      api('/linex/nodos').catch(function () { return { ok: true, nodos: [] }; })
    ]).then(function (res) {
      cfg = res[0] || new_default();
      if (!cfg.nodos) cfg.nodos = [];
      nodos = (res[1] && res[1].nodos) ? res[1].nodos : [];
      renderNodoSel();
      if (selectedUid) renderForm();
      else { renderJson(); }
      pollLive();
      liveTimer = setInterval(pollLive, 1000);
    });
  }

  function new_default() { return { enabled: true, nodos: [], ignorados: [] }; }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
