// ============================================================================
// camaras.js — Monitor + Config de cámaras IP (Hikvision ISAPI).
// Monitor: layout seleccionable (1x1, 2x1, 2x2), doble click para enfocar.
//   El WebHost actúa de proxy con auth Digest, así el browser no necesita creds.
// Config: edita y guarda camaras.json vía /api/camaras/config.
// ============================================================================

(function () {
  'use strict';

  var statusEl   = document.getElementById('camStatus');
  var subtitleEl = document.getElementById('camSubtitle');
  var gridEl     = document.getElementById('camGrid');
  var emptyEl    = document.getElementById('camEmpty');
  var tabMonEl   = document.getElementById('tabMonitor');
  var tabCfgEl   = document.getElementById('tabConfig');
  var listEl     = document.getElementById('cfgList');
  var refrescoEl = document.getElementById('inpRefresco');
  var msgEl      = document.getElementById('cfgMsg');
  var layoutGroup= document.getElementById('layoutGroup');

  var LAYOUT_KEY = 'agp.cam.layout';
  var FOCUS_KEY  = 'agp.cam.focusIdx';

  var state = {
    config: null,
    activeTab: 'monitor',
    layout: localStorage.getItem(LAYOUT_KEY) || '2x2',
    focusIdx: parseInt(localStorage.getItem(FOCUS_KEY), 10),
    timers: [],
    activas: []
  };
  if (isNaN(state.focusIdx)) state.focusIdx = -1;

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function setStatus(kind, text) {
    if (!statusEl) return;
    statusEl.className = 'pill ' + (kind || '');
    statusEl.innerHTML = '<span class="dot"></span> ' + escapeHtml(text);
  }

  // ---------- Layout selector ----------

  function visibleCountForLayout() {
    switch (state.layout) {
      case '1x1': return 1;
      case '2x1': return 2;
      default:    return 4;
    }
  }

  function applyLayout() {
    gridEl.className = 'cam-grid lay-' + state.layout;
    // Marca toolbar
    layoutGroup.querySelectorAll('button[data-layout]').forEach(function (b) {
      b.classList.toggle('active', b.getAttribute('data-layout') === state.layout);
    });
    // Decide qué tiles se ven
    var n = visibleCountForLayout();
    var tiles = gridEl.querySelectorAll('.cam-tile');
    if (state.layout === '1x1') {
      // Mostrar solo el enfocado (o el primero si no hay).
      var focusIdx = state.focusIdx;
      if (focusIdx < 0 || !state.activas.some(function (a) { return a.idx === focusIdx; })) {
        focusIdx = state.activas.length > 0 ? state.activas[0].idx : -1;
      }
      tiles.forEach(function (t) {
        var i = parseInt(t.getAttribute('data-idx'), 10);
        t.classList.toggle('focused', i === focusIdx);
        t.classList.remove('hidden');
      });
    } else {
      // 2x1 → primeros 2; 2x2 → primeros 4.
      tiles.forEach(function (t, idx) {
        t.classList.remove('focused');
        t.classList.toggle('hidden', idx >= n);
      });
    }
  }

  function setLayout(l) {
    state.layout = l;
    localStorage.setItem(LAYOUT_KEY, l);
    applyLayout();
  }

  if (layoutGroup) {
    layoutGroup.addEventListener('click', function (ev) {
      var b = ev.target.closest('button[data-layout]');
      if (!b) return;
      setLayout(b.getAttribute('data-layout'));
    });
  }

  // ---------- Monitor grid ----------

  function buildMonitor() {
    clearTimers();
    if (!gridEl) return;
    var cams = (state.config && state.config.camaras) || [];
    var activas = [];
    for (var i = 0; i < cams.length; i++) if (cams[i] && cams[i].activa) activas.push({ idx: i, cam: cams[i] });
    state.activas = activas;

    subtitleEl.textContent = cams.length + ' configurada' + (cams.length !== 1 ? 's' : '') +
      ' · ' + activas.length + ' activa' + (activas.length !== 1 ? 's' : '');

    if (activas.length === 0) {
      gridEl.innerHTML = ''; gridEl.style.display = 'none';
      emptyEl.style.display = '';
      setStatus('warn', 'sin cámaras activas');
      return;
    }
    emptyEl.style.display = 'none'; gridEl.style.display = '';

    var html = '';
    for (var k = 0; k < activas.length; k++) {
      var a = activas[k];
      html += '' +
        '<div class="cam-tile" data-idx="' + a.idx + '" title="Doble click para enfocar">' +
          '<img class="frame" alt="" />' +
          '<span class="label"><span class="ok-dot"></span>' + escapeHtml(a.cam.nombre || ('Cámara ' + (a.idx + 1))) + '</span>' +
          '<div class="err-msg"></div>' +
        '</div>';
    }
    gridEl.innerHTML = html;
    setStatus('ok', activas.length + ' cámara' + (activas.length !== 1 ? 's' : '') + ' activa' + (activas.length !== 1 ? 's' : ''));

    // Wire double-click → focus toggle.
    gridEl.querySelectorAll('.cam-tile').forEach(function (tile) {
      tile.addEventListener('dblclick', function () {
        var i = parseInt(tile.getAttribute('data-idx'), 10);
        if (state.layout === '1x1') {
          // Salir de focus → volver al layout previo (2x2 si hay >=3, sino 2x1).
          var prev = state.activas.length > 2 ? '2x2' : '2x1';
          setLayout(prev);
        } else {
          state.focusIdx = i;
          localStorage.setItem(FOCUS_KEY, String(i));
          setLayout('1x1');
        }
      });
    });

    applyLayout();
    startTicking();
  }

  function startTicking() {
    // Default 1500ms para tiles en mosaico 2x1/2x2 (varios pidiendo a la vez
    // al WebHost). En 1x1 (focused) el tick mismo usa periodo más corto vía
    // el override de abajo. Configurable desde la pestaña Config.
    var refresco = Math.max(300, (state.config && state.config.refrescoMs) || 1500);
    var tiles = gridEl.querySelectorAll('.cam-tile');
    // Stagger inicial: distribuye el primer tick para no pegarle a todas a la vez.
    var stagger = Math.floor(refresco / Math.max(1, tiles.length));
    tiles.forEach(function (tile, k) {
      var idx = parseInt(tile.getAttribute('data-idx'), 10);
      scheduleSnapshot(tile, idx, refresco, k * stagger);
    });
  }

  function scheduleSnapshot(tile, idx, period, initialDelay) {
    var img   = tile.querySelector('img.frame');
    var label = tile.querySelector('.label');
    var errEl = tile.querySelector('.err-msg');
    var labelText = (label.textContent || '').trim();
    var inFlight = false;
    var hidden = false;

    img.addEventListener('load', function () {
      inFlight = false;
      tile.classList.remove('errored');
      label.innerHTML = '<span class="ok-dot"></span>' + escapeHtml(labelText);
      if (errEl) errEl.textContent = '';
    });
    img.addEventListener('error', function () {
      inFlight = false;
      tile.classList.add('errored');
      label.innerHTML = '<span class="err-dot"></span>' + escapeHtml(labelText);
      if (errEl) errEl.textContent = '✕ sin imagen (verificá IP / credenciales)';
    });

    function tick() {
      // Si el tile no se ve (hidden por layout) no pidas snapshot — ahorra ancho de banda.
      hidden = tile.classList.contains('hidden') ||
               (state.layout === '1x1' && !tile.classList.contains('focused'));
      if (hidden) return;
      // Si el último request todavía no volvió (red lenta), no encolés otro encima.
      if (inFlight) return;
      inFlight = true;
      img.src = '/api/camaras/' + idx + '/snapshot?t=' + Date.now();
    }

    // Re-scheduling con período dinámico: en 1x1 el tile focused tira más
    // rápido (1000ms, "live" feel); en mosaico todas pollean a `period`
    // (default 1500ms) para no saturar el WebHost.
    function loop() {
      tick();
      var p = (state.layout === '1x1' && tile.classList.contains('focused'))
        ? Math.min(period, 1000)
        : period;
      var t = setTimeout(loop, p);
      state.timers.push(t);
    }

    var to = setTimeout(loop, initialDelay || 0);
    state.timers.push(to);
  }

  function clearTimers() {
    for (var i = 0; i < state.timers.length; i++) {
      clearInterval(state.timers[i]);
      clearTimeout(state.timers[i]);
    }
    state.timers = [];
  }

  // ---------- Config tab ----------

  function buildConfig() {
    var cams = (state.config && state.config.camaras) || [];
    var html = '';
    for (var i = 0; i < cams.length; i++) {
      var c = cams[i] || {};
      html += '' +
        '<div class="cfg-row" data-i="' + i + '">' +
          '<div style="color: var(--agp-text-muted)">' + (i + 1) + '</div>' +
          '<input type="text" data-f="nombre"  value="' + escapeHtml(c.nombre || '') + '">' +
          '<input type="text" data-f="ip"      value="' + escapeHtml(c.ip || '') + '">' +
          '<input type="number" data-f="puerto" value="' + (c.puerto || 80) + '" min="1" max="65535">' +
          '<input type="number" data-f="canal"  value="' + (c.canal || 1) + '" min="1" max="32">' +
          '<input type="text" data-f="usuario" value="' + escapeHtml(c.usuario || '') + '">' +
          '<input type="password" data-f="clave" value="' + escapeHtml(c.clave || '') + '">' +
          '<div style="display:flex; align-items:center; justify-content:center; gap:8px">' +
            '<input type="checkbox" data-f="activa"' + (c.activa ? ' checked' : '') + '>' +
            '<button class="btn" data-act="del" title="Quitar">✕</button>' +
          '</div>' +
        '</div>';
    }
    listEl.innerHTML = html;
    refrescoEl.value = (state.config && state.config.refrescoMs) || 1000;
  }

  function readConfigFromUI() {
    var cams = [];
    var rows = listEl.querySelectorAll('.cfg-row');
    rows.forEach(function (r) {
      cams.push({
        nombre:  r.querySelector('input[data-f="nombre"]').value || '',
        ip:      r.querySelector('input[data-f="ip"]').value || '',
        puerto:  parseInt(r.querySelector('input[data-f="puerto"]').value, 10) || 80,
        canal:   parseInt(r.querySelector('input[data-f="canal"]').value, 10) || 1,
        usuario: r.querySelector('input[data-f="usuario"]').value || '',
        clave:   r.querySelector('input[data-f="clave"]').value || '',
        activa:  r.querySelector('input[data-f="activa"]').checked
      });
    });
    return {
      camaras: cams,
      refrescoMs: Math.max(200, parseInt(refrescoEl.value, 10) || 1000)
    };
  }

  function onListClick(ev) {
    var btn = ev.target.closest('button[data-act="del"]');
    if (!btn) return;
    var row = btn.closest('.cfg-row');
    if (row) row.remove();
  }

  function onAddCam() {
    state.config = readConfigFromUI();
    state.config.camaras.push({
      nombre: 'Cámara ' + (state.config.camaras.length + 1),
      ip: '192.168.1.64', puerto: 80, canal: state.config.camaras.length + 1,
      usuario: 'admin', clave: '', activa: true
    });
    buildConfig();
  }

  async function onSave() {
    msgEl.textContent = 'Guardando…';
    var cfg = readConfigFromUI();
    try {
      var res = await fetch('/api/camaras/config', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var data = await res.json();
      if (data.ok) {
        state.config = cfg;
        msgEl.textContent = '✓ Guardado.';
        buildMonitor();
      } else {
        msgEl.textContent = '✕ ' + (data.error || 'error al guardar');
      }
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message;
    }
  }

  // ---------- Tabs ----------

  function showTab(name) {
    state.activeTab = name;
    document.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.getAttribute('data-tab') === name);
    });
    tabMonEl.style.display = name === 'monitor' ? '' : 'none';
    tabCfgEl.style.display = name === 'config'  ? '' : 'none';
    if (name === 'monitor') buildMonitor();
    else { clearTimers(); buildConfig(); }
  }

  document.querySelectorAll('.tab').forEach(function (t) {
    t.addEventListener('click', function () { showTab(t.getAttribute('data-tab')); });
  });
  listEl.addEventListener('click', onListClick);
  document.getElementById('btnAddCam').addEventListener('click', onAddCam);
  document.getElementById('btnSave').addEventListener('click', onSave);

  // Pausa al ocultar la pestaña/ventana (Page Visibility API).
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) clearTimers();
    else if (state.activeTab === 'monitor') startTicking();
  });

  // ---------- Init ----------

  async function loadConfig() {
    setStatus('', 'cargando…');
    try {
      var res = await fetch('/api/camaras/config', { cache: 'no-store' });
      var data = await res.json();
      if (data.ok) {
        state.config = data.config || { camaras: [], refrescoMs: 1000 };
      } else {
        state.config = { camaras: [], refrescoMs: 1000 };
        setStatus('err', data.error || 'sin servicio');
        return;
      }
      buildMonitor();
    } catch (e) {
      setStatus('err', 'sin conexión');
    }
  }

  loadConfig();
})();
