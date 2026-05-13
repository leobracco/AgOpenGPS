// ============================================================================
// camaras.js — Monitor + Config de cámaras IP (Hikvision ISAPI).
// Monitor: grid adaptativa, refrescá <img> apuntando a /api/camaras/{idx}/snapshot
// (el WebHost actúa de proxy con auth Digest, así el browser no necesita creds).
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

  var state = { config: null, timers: [], activeTab: 'monitor', errors: {} };

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

  function gridColsFor(n) {
    if (n <= 1) return 1;
    if (n <= 4) return 2;
    if (n <= 9) return 3;
    return 4;
  }

  function buildMonitor() {
    clearTimers();
    state.errors = {};
    if (!gridEl) return;
    var cams = (state.config && state.config.camaras) || [];
    var activas = [];
    for (var i = 0; i < cams.length; i++) if (cams[i] && cams[i].activa) activas.push({ idx: i, cam: cams[i] });

    subtitleEl.textContent = cams.length + ' configurada' + (cams.length !== 1 ? 's' : '') +
      ' · ' + activas.length + ' activa' + (activas.length !== 1 ? 's' : '');

    if (activas.length === 0) {
      gridEl.innerHTML = ''; gridEl.style.display = 'none';
      emptyEl.style.display = '';
      setStatus('warn', 'sin cámaras activas');
      return;
    }
    emptyEl.style.display = 'none'; gridEl.style.display = '';
    var cols = gridColsFor(activas.length);
    gridEl.className = 'cam-grid cols-' + cols;

    var html = '';
    for (var k = 0; k < activas.length; k++) {
      var a = activas[k];
      html += '' +
        '<div class="cam-tile" data-idx="' + a.idx + '">' +
          '<img class="frame" alt="" />' +
          '<span class="label"><span class="ok-dot"></span>' + escapeHtml(a.cam.nombre || ('Cámara ' + (a.idx + 1))) + '</span>' +
          '<div class="err-msg"></div>' +
        '</div>';
    }
    gridEl.innerHTML = html;
    setStatus('ok', activas.length + ' cámara' + (activas.length !== 1 ? 's' : '') + ' activa' + (activas.length !== 1 ? 's' : ''));

    var refresco = Math.max(300, (state.config && state.config.refrescoMs) || 1000);
    var tiles = gridEl.querySelectorAll('.cam-tile');
    tiles.forEach(function (tile) {
      var idx = parseInt(tile.getAttribute('data-idx'), 10);
      scheduleSnapshot(tile, idx, refresco);
    });
  }

  function scheduleSnapshot(tile, idx, period) {
    var img = tile.querySelector('img.frame');
    var label = tile.querySelector('.label');
    var errEl = tile.querySelector('.err-msg');

    function tick() {
      var url = '/api/camaras/' + idx + '/snapshot?t=' + Date.now();
      var probe = new Image();
      probe.onload = function () {
        img.src = url;
        if (errEl) errEl.textContent = '';
        tile.classList.remove('errored');
        label.innerHTML = '<span class="ok-dot"></span>' + label.textContent.trim();
      };
      probe.onerror = function () {
        tile.classList.add('errored');
        if (label) label.innerHTML = '<span class="err-dot"></span>' + label.textContent.trim();
        if (errEl) errEl.textContent = '✕ sin imagen (verificá IP / credenciales)';
      };
      probe.src = url;
    }
    tick();
    var t = setInterval(tick, period);
    state.timers.push(t);
  }

  function clearTimers() {
    for (var i = 0; i < state.timers.length; i++) clearInterval(state.timers[i]);
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
