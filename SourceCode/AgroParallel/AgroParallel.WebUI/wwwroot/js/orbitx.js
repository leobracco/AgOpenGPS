// ============================================================================
// orbitx.js — Config + Estado del módulo OrbitX cloud.
// Tabs (Estado / Config / OTA / Prescripciones). Estado y Config son
// funcionales; OTA y Prescripciones aún son mock visual (legacy todavía
// activo en el shell WinForms).
// ============================================================================

(function () {
  'use strict';

  var statusEl     = document.getElementById('orbitxStatus');
  var formEl       = document.getElementById('orbitxForm');
  var btnSave      = document.getElementById('btnSave');
  var btnTest      = document.getElementById('btnTest');
  var hintEl       = document.getElementById('formHint');
  var connectedPill = document.getElementById('connectedPill');

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function fmtTs(iso) {
    if (!iso) return '—';
    try {
      var d = new Date(iso);
      if (isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch (e) { return iso; }
  }

  function renderStatus(s) {
    if (!statusEl) return;
    statusEl.innerHTML = '' +
      '<div class="card"><h3>Heartbeat</h3>' +
        '<div class="metric" style="font-size:var(--agp-fs-xl)">' + (s.enabled ? 'on' : 'off') + '</div>' +
        '<div class="row"><span>Último sync</span><strong style="color:var(--agp-text)">' + fmtTs(s.lastSync) + '</strong></div>' +
      '</div>' +
      '<div class="card"><h3>Cola sync</h3>' +
        '<div class="metric">' + (s.filesSynced || 0) + '<span class="unit">arch</span></div>' +
        '<div class="row"><span>Total subidos</span><strong style="color:var(--agp-text)">acumulado</strong></div>' +
      '</div>' +
      '<div class="card"><h3>Establecimiento</h3>' +
        '<div class="metric" style="font-size:var(--agp-fs-xl)">' + escapeHtml(s.estabSlug || '—') + '</div>' +
        '<div class="row"><span>Device ID</span><strong style="color:var(--agp-text); font-family:var(--agp-font-mono); font-size:var(--agp-fs-sm)">' + escapeHtml(s.deviceId || '—') + '</strong></div>' +
      '</div>' +
      '<div class="card"><h3>Estado conexión</h3>' +
        '<div class="metric" style="font-size:var(--agp-fs-xl)">' + (s.cloudConnected ? 'OK' : '—') + '</div>' +
        '<div class="row"><span>Último error</span><strong style="color:var(--agp-text); font-size:var(--agp-fs-sm)">' + escapeHtml(s.lastError || 'ninguno') + '</strong></div>' +
      '</div>';
  }

  function renderForm(cfg) {
    if (!formEl) return;
    formEl.innerHTML = '' +
      '<div style="display:grid; grid-template-columns: 1fr 1fr; gap: var(--agp-sp-4)">' +
        field('Server URL',            'serverUrl',    cfg.serverUrl) +
        field('Establecimiento slug',  'estabSlug',    cfg.estabSlug) +
        field('Device ID',             'deviceId',     cfg.deviceId, true) +
        field('Device Token',          'deviceToken',  cfg.deviceToken) +
        field('Sync interval (s)',     'syncIntervalSec', cfg.syncIntervalSec, false, 'number') +
        field('Master Token',          'masterToken',  cfg.masterToken) +
      '</div>' +
      '<div style="display:flex; flex-wrap:wrap; gap:var(--agp-sp-4); margin-top: var(--agp-sp-3)">' +
        toggle('Enabled',     'enabled',     cfg.enabled) +
        toggle('Sync AOG',    'syncAOG',     cfg.syncAOG) +
        toggle('Sync VistaX', 'syncVistaX',  cfg.syncVistaX) +
        toggle('Sync QuantiX','syncQuantiX', cfg.syncQuantiX) +
        toggle('Sync SectionX','syncSectionX',cfg.syncSectionX) +
      '</div>';
    formEl._dto = cfg;
  }

  function field(label, name, value, readonly, type) {
    var t = type || 'text';
    var ro = readonly ? ' readonly' : '';
    return '<div class="field"><label>' + escapeHtml(label) + '</label>' +
      '<input type="' + t + '" data-name="' + name + '" value="' + escapeHtml(value == null ? '' : value) + '"' + ro + '></div>';
  }

  function toggle(label, name, value) {
    var checked = value ? 'checked' : '';
    return '<label class="switch"><input type="checkbox" data-name="' + name + '" ' + checked + '><span class="track"></span> ' + escapeHtml(label) + '</label>';
  }

  function collectDto() {
    var dto = Object.assign({}, formEl._dto || {});
    formEl.querySelectorAll('input[data-name]').forEach(function (el) {
      var name = el.getAttribute('data-name');
      if (el.type === 'checkbox') dto[name] = el.checked;
      else if (el.type === 'number') dto[name] = parseInt(el.value, 10) || 0;
      else dto[name] = el.value;
    });
    return dto;
  }

  async function load() {
    try {
      var [cfgRes, statusRes] = await Promise.all([
        fetch('/api/orbitx/config', { cache: 'no-store' }),
        fetch('/api/orbitx/status', { cache: 'no-store' })
      ]);
      var cfg = await cfgRes.json();
      var status = await statusRes.json();
      renderForm(cfg);
      renderStatus(status);
      if (connectedPill) {
        connectedPill.className = 'pill ' + (status.cloudConnected ? 'ok' : 'idle');
        connectedPill.innerHTML = '<span class="dot"></span> ' + (status.cloudConnected ? 'Cloud conectado' : 'Sin verificar');
      }
    } catch (e) {
      if (hintEl) hintEl.textContent = 'No se pudo cargar la config: ' + e.message;
    }
  }

  if (btnSave) {
    btnSave.addEventListener('click', async function () {
      if (!formEl) return;
      btnSave.disabled = true;
      hintEl.textContent = 'Guardando…';
      try {
        var dto = collectDto();
        var res = await fetch('/api/orbitx/config', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(dto)
        });
        var ok = (await res.json()).ok;
        hintEl.textContent = ok ? 'Guardado.' : 'Error al guardar.';
      } catch (e) {
        hintEl.textContent = 'Error: ' + e.message;
      }
      btnSave.disabled = false;
    });
  }

  if (btnTest) {
    btnTest.addEventListener('click', async function () {
      btnTest.disabled = true;
      hintEl.textContent = 'Probando conexión…';
      try {
        var res = await fetch('/api/orbitx/test', { method: 'POST' });
        var data = await res.json();
        if (data.ok) {
          hintEl.textContent = '✓ Conexión OK';
          if (connectedPill) {
            connectedPill.className = 'pill ok';
            connectedPill.innerHTML = '<span class="dot"></span> Cloud conectado';
          }
        } else {
          hintEl.textContent = '✗ ' + (data.error || 'sin respuesta');
        }
      } catch (e) {
        hintEl.textContent = 'Error: ' + e.message;
      }
      btnTest.disabled = false;
    });
  }

  load();
  setInterval(load, 10000);
})();
