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

  // EmbedIO serializa respuestas en PascalCase; el archivo on-disk también es
  // PascalCase. Leer ambos casings para ser tolerantes a cualquier cambio de
  // policy futuro del WebHost.
  function pick(o, camel, pascal) {
    if (!o) return undefined;
    var v = o[camel];
    if (v === undefined || v === null) v = o[pascal];
    return v;
  }

  function renderStatus(s) {
    if (!statusEl) return;
    var enabled        = pick(s, 'enabled', 'Enabled');
    var lastSync       = pick(s, 'lastSync', 'LastSync');
    var filesSynced    = pick(s, 'filesSynced', 'FilesSynced');
    var estabSlug      = pick(s, 'estabSlug', 'EstabSlug');
    var deviceId       = pick(s, 'deviceId', 'DeviceId');
    var cloudConnected = pick(s, 'cloudConnected', 'CloudConnected');
    var lastError      = pick(s, 'lastError', 'LastError');
    statusEl.innerHTML = '' +
      '<div class="card"><h3>Heartbeat</h3>' +
        '<div class="metric" style="font-size:var(--agp-fs-xl)">' + (enabled ? 'on' : 'off') + '</div>' +
        '<div class="row"><span>Último sync</span><strong style="color:var(--agp-text)">' + fmtTs(lastSync) + '</strong></div>' +
      '</div>' +
      '<div class="card"><h3>Cola sync</h3>' +
        '<div class="metric">' + (filesSynced || 0) + '<span class="unit">arch</span></div>' +
        '<div class="row"><span>Total subidos</span><strong style="color:var(--agp-text)">acumulado</strong></div>' +
      '</div>' +
      '<div class="card"><h3>Establecimiento</h3>' +
        '<div class="metric" style="font-size:var(--agp-fs-xl)">' + escapeHtml(estabSlug || '—') + '</div>' +
        '<div class="row"><span>Device ID</span><strong style="color:var(--agp-text); font-family:var(--agp-font-mono); font-size:var(--agp-fs-sm)">' + escapeHtml(deviceId || '—') + '</strong></div>' +
      '</div>' +
      '<div class="card"><h3>Estado conexión</h3>' +
        '<div class="metric" style="font-size:var(--agp-fs-xl)">' + (cloudConnected ? 'OK' : '—') + '</div>' +
        '<div class="row"><span>Último error</span><strong style="color:var(--agp-text); font-size:var(--agp-fs-sm)">' + escapeHtml(lastError || 'ninguno') + '</strong></div>' +
      '</div>';
  }

  function renderForm(cfg) {
    if (!formEl) return;
    // Server URL queda como label informativa (no editable) — la URL del
    // cloud la fija el firmware del Hub (OrbitXConfigService.FixedServerUrl)
    // y NO se persiste al operario; pedido explícito 2026-05-27.
    // Estab slug / Device Token siguen editables para debug/claim manual.
    // Device ID se autogenera del MAC → readonly.
    var serverUrl       = pick(cfg, 'serverUrl', 'ServerUrl');
    var estabSlug       = pick(cfg, 'estabSlug', 'EstabSlug');
    var deviceId        = pick(cfg, 'deviceId', 'DeviceId');
    var deviceToken     = pick(cfg, 'deviceToken', 'DeviceToken');
    var syncIntervalSec = pick(cfg, 'syncIntervalSec', 'SyncIntervalSec');
    var enabled         = pick(cfg, 'enabled', 'Enabled');
    var syncAOG         = pick(cfg, 'syncAOG', 'SyncAOG');
    var syncVistaX      = pick(cfg, 'syncVistaX', 'SyncVistaX');
    var syncQuantiX     = pick(cfg, 'syncQuantiX', 'SyncQuantiX');
    var syncSectionX    = pick(cfg, 'syncSectionX', 'SyncSectionX');
    formEl.innerHTML = '' +
      '<div class="field" style="margin-bottom: var(--agp-sp-3)">' +
        '<label>Server URL</label>' +
        '<div style="font-family:var(--agp-font-mono); color:var(--agp-text); padding: var(--agp-sp-2) var(--agp-sp-3); background:var(--agp-bg-2,#0e1612); border:1px solid var(--agp-border,#2a3a31); border-radius:var(--agp-radius); user-select:text">' +
          escapeHtml(serverUrl || 'https://orbitx.agroparallel.com') +
        '</div>' +
        '<div style="margin-top:4px; font-size:11px; color:var(--agp-text-muted)">Fija por sistema — no se edita desde la UI.</div>' +
      '</div>' +
      '<div style="display:grid; grid-template-columns: 1fr 1fr; gap: var(--agp-sp-4)">' +
        field('Establecimiento slug',  'estabSlug',    estabSlug, false) +
        field('Device ID',             'deviceId',     deviceId, true) +
        field('Device Token',          'deviceToken',  deviceToken, false) +
        field('Sync interval (s)',     'syncIntervalSec', syncIntervalSec, false, 'number') +
      '</div>' +
      '<div style="display:flex; flex-wrap:wrap; gap:var(--agp-sp-4); margin-top: var(--agp-sp-3)">' +
        toggle('Enabled',     'enabled',     enabled) +
        toggle('Sync Piloto', 'syncAOG',     syncAOG) +
        toggle('Sync VistaX', 'syncVistaX',  syncVistaX) +
        toggle('Sync QuantiX','syncQuantiX', syncQuantiX) +
        toggle('Sync SectionX','syncSectionX',syncSectionX) +
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
        var cc = pick(status, 'cloudConnected', 'CloudConnected');
        connectedPill.className = 'pill ' + (cc ? 'ok' : 'idle');
        connectedPill.innerHTML = '<span class="dot"></span> ' + (cc ? 'Cloud conectado' : 'Sin verificar');
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

  // ── Pair flow ───────────────────────────────────────────────────
  // Pollea /api/orbitx/pair-info cada 4s. Muestra el código en la
  // pantalla del tractor mientras el operario lo tipea en OrbitX cloud.
  var pairCardEl  = document.getElementById('pairCard');
  var pairTitleEl = document.getElementById('pairTitle');
  var pairPillEl  = document.getElementById('pairStatePill');
  var pairBodyEl  = document.getElementById('pairBody');
  var lastShownCode = null;

  function renderPairUnpaired(info) {
    pairCardEl.className = 'pair-card ' + (info.status === 'offline' ? 'offline' : 'unpaired');
    pairTitleEl.textContent = 'Vinculá este tractor a OrbitX Cloud';
    if (info.status === 'offline') {
      pairPillEl.className = 'pair-state-pill offline';
      pairPillEl.textContent = 'sin conexión al cloud';
    } else if (info.status === 'expired') {
      pairPillEl.className = 'pair-state-pill pending';
      pairPillEl.textContent = 'código vencido, generando otro…';
    } else {
      pairPillEl.className = 'pair-state-pill pending';
      pairPillEl.textContent = 'esperando confirmación';
    }
    var code = info.code || '——————';
    var expires = info.expiresInSec || 0;
    var mm = Math.floor(expires / 60), ss = expires % 60;
    var clock = expires > 0 ? ('vence en ' + mm + ':' + (ss < 10 ? '0' : '') + ss) : '';
    var serverUrl = info.serverUrl || '(no configurado)';
    pairBodyEl.innerHTML = '' +
      '<div class="pair-code" id="pairCode">' + escapeHtml(code) + '</div>' +
      '<div class="pair-steps">' +
        '<div class="pair-step"><span class="num">1</span><strong>Abrí OrbitX Cloud</strong>' +
          '<div class="body">Desde tu PC o teléfono, logueate en <strong>' + escapeHtml(serverUrl.replace(/^https?:\/\//,'')) + '</strong> con tu usuario de la organización.</div></div>' +
        '<div class="pair-step"><span class="num">2</span><strong>Dispositivos → Vincular por código</strong>' +
          '<div class="body">En el panel cloud, andá a la sección "Dispositivos" y tocá <em>⚡ Vincular por código</em>.</div></div>' +
        '<div class="pair-step"><span class="num">3</span><strong>Ingresá el código</strong>' +
          '<div class="body">Tipeá los 6 caracteres de arriba, dale un nombre al tractor y confirmá. En unos segundos esta pantalla va a decir "Vinculado".</div></div>' +
      '</div>' +
      '<div class="pair-meta">' +
        '<div>Device ID local: <strong style="font-family:var(--agp-font-mono)">' + escapeHtml(info.deviceId || '—') + '</strong></div>' +
        '<div>Server: <strong>' + escapeHtml(serverUrl) + '</strong></div>' +
        (clock ? '<div>' + clock + '</div>' : '') +
        '<div style="flex:1"></div>' +
      '</div>' +
      // Cuando hay errorCode (status="offline"), pintamos badge AGP-* +
      // mensaje amigable + <details> técnico para soporte, igual que la
      // página /nodos. Si no hay errorCode (estado pending/expired), solo
      // el hint en italic como antes.
      (info.errorCode
        ? '<div style="margin-top: var(--agp-sp-3); padding: var(--agp-sp-2) var(--agp-sp-3); background: rgba(201,45,45,0.08); border: 1px solid rgba(201,45,45,0.35); border-radius: var(--agp-radius); font-size: var(--agp-fs-sm)">' +
            '<div style="display:flex; align-items:center; gap: var(--agp-sp-2); flex-wrap:wrap">' +
              '<span style="background:#C92D2D; color:#fff; padding:2px 6px; border-radius:4px; font-family:var(--agp-font-mono); font-weight:700; font-size:11px">' + escapeHtml(info.errorCode) + '</span>' +
              (info.hint ? '<span>' + escapeHtml(info.hint) + '</span>' : '') +
            '</div>' +
            '<div style="margin-top: var(--agp-sp-2); color: var(--agp-text-muted); font-size: 11px">Para soporte: dictá el código <strong>' + escapeHtml(info.errorCode) + '</strong> al asistente por WhatsApp o teléfono.</div>' +
            (info.hintTechnical
              ? '<details style="margin-top: var(--agp-sp-2)"><summary style="cursor:pointer; font-size:11px; color:var(--agp-text-muted)">Detalle técnico (soporte)</summary>' +
                '<pre style="font-family:var(--agp-font-mono); font-size:11px; background:var(--agp-bg-2,#0e1612); color:var(--agp-text); padding:6px 8px; border-radius:4px; margin:6px 0 0 0; white-space:pre-wrap; word-break:break-word">' + escapeHtml(info.hintTechnical) + '</pre>' +
                '</details>'
              : '') +
          '</div>'
        : (info.hint ? '<div style="margin-top: var(--agp-sp-2)"><em>' + escapeHtml(info.hint) + '</em></div>' : ''));
    // Re-mostrar feedback al cambiar de código (regeneración).
    if (info.code && info.code !== lastShownCode) {
      lastShownCode = info.code;
    }
  }

  function renderPairPaired(info) {
    pairCardEl.className = 'pair-card paired';
    pairTitleEl.textContent = 'Tractor vinculado';
    pairPillEl.className = 'pair-state-pill ok';
    pairPillEl.textContent = '✓ activo';
    pairBodyEl.innerHTML = '' +
      '<div class="pair-meta" style="margin-bottom: var(--agp-sp-3)">' +
        '<div>Establecimiento: <strong>' + escapeHtml(info.estabSlug || '—') + '</strong></div>' +
        '<div>Device ID: <strong style="font-family:var(--agp-font-mono)">' + escapeHtml(info.deviceId || '—') + '</strong></div>' +
        '<div>Server: <strong>' + escapeHtml(info.serverUrl || '—') + '</strong></div>' +
      '</div>' +
      '<div class="pair-actions">' +
        '<button class="btn" id="btnUnpair">Desvincular</button>' +
      '</div>';
    var btn = document.getElementById('btnUnpair');
    if (btn) btn.addEventListener('click', async function () {
      if (!confirm('¿Desvincular este tractor de OrbitX? El token actual se borra y vas a tener que volver a vincularlo desde el panel cloud.')) return;
      btn.disabled = true;
      try {
        await fetch('/api/orbitx/pair-reset', { method: 'POST' });
        lastShownCode = null;
        await pollPair();
      } catch (e) {
        alert('Error: ' + e.message);
        btn.disabled = false;
      }
    });
  }

  async function pollPair() {
    try {
      var r = await fetch('/api/orbitx/pair-info', { cache: 'no-store' });
      var info = await r.json();
      if (!info || (info.error && !info.deviceId)) {
        pairCardEl.style.display = 'block';
        pairCardEl.className = 'pair-card offline';
        pairBodyEl.innerHTML = '<div class="subtitle">' + escapeHtml(info && info.error || 'Sin respuesta del servicio.') + '</div>';
        return;
      }
      pairCardEl.style.display = 'block';
      if (info.paired) {
        renderPairPaired(info);
        if (info.justClaimed) {
          // Refrescamos el form/estado para reflejar token nuevo.
          load();
        }
      } else {
        renderPairUnpaired(info);
      }
    } catch (e) {
      pairCardEl.style.display = 'block';
      pairCardEl.className = 'pair-card offline';
      pairBodyEl.innerHTML = '<div class="subtitle">Sin conexión local al servicio: ' + escapeHtml(e.message) + '</div>';
    }
  }

  pollPair();
  setInterval(pollPair, 4000);
})();
