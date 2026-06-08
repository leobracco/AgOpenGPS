// ============================================================================
// flowx.js — UI de FlowX (corte + dosis para pulverizadoras).
// Conecta con /api/flowx/config (CRUD) y /api/flowx/nodos (descubrimiento LAN).
// El estado live del aguilón (velocidad + secciones abiertas) viene de
// /api/aog/state — la lógica de escalado de caudal por secciones la hace
// el FlowXBridge en C#, acá sólo mostramos.
//
// Modal HTML: WebView2 desactiva window.prompt() y bloquea window.confirm()
// salvo flag — usamos diálogo propio en index para no romper el flujo táctil.
// ============================================================================

(function () {
  'use strict';

  var kpiFlow   = document.getElementById('kpiFlow');
  var kpiTarget = document.getElementById('kpiTarget');
  var kpiDose   = document.getElementById('kpiDose');
  var kpiSpeed  = document.getElementById('kpiSpeed');
  var kpiPwm    = document.getElementById('kpiPwm');
  var kpiPid    = document.getElementById('kpiPid');
  var kpiSecAct = document.getElementById('kpiSecActive');
  var kpiSecTot = document.getElementById('kpiSecTotal');
  var kpiAncho  = document.getElementById('kpiAncho');
  var kpiAreaNeta    = document.getElementById('kpiAreaNeta');
  var kpiAreaOverlap = document.getElementById('kpiAreaOverlap');
  var kpiOverlapPct  = document.getElementById('kpiOverlapPct');
  var kpiSavedLitros = document.getElementById('kpiSavedLitros');
  var nodosList = document.getElementById('nodosList');

  var cfg = null;
  var nodos = [];     // nodos LAN (registry MQTT)
  var live = null;
  var aogSnap = null; // último snapshot AOG normalizado a camelCase

  // /api/aog/state serializa con los nombres PascalCase de las props C# (Newtonsoft
  // sin CamelCasePolicy). Normalizamos acá para que el resto del módulo no tenga
  // que conocer la convención del backend.
  var DEFAULT_CORTES_PER_NODE = 7;
  function normalizeAogSnap(raw) {
    if (!raw) return null;
    function pick(o, p, c) { return (o[p] != null) ? o[p] : o[c]; }
    return {
      numSections: Number(pick(raw, 'NumSections', 'numSections') || 0),
      toolWidth: Number(pick(raw, 'ToolWidth', 'toolWidth') || 0),
      avgSpeed: Number(pick(raw, 'AvgSpeed', 'avgSpeed') || 0),
      sectionOnRequest: pick(raw, 'SectionOnRequest', 'sectionOnRequest') || [],
      sectionPositions: pick(raw, 'SectionPositions', 'sectionPositions') || [],
      isJobStarted: !!pick(raw, 'IsJobStarted', 'isJobStarted'),
      workedAreaTotalM2: Number(pick(raw, 'WorkedAreaTotalM2', 'workedAreaTotalM2') || 0),
      actualAreaCoveredM2: Number(pick(raw, 'ActualAreaCoveredM2', 'actualAreaCoveredM2') || 0)
    };
  }

  // ==========================================================================
  // Modal HTML — sustituye window.prompt / window.confirm (rotos en WebView2)
  // ==========================================================================
  var modalBackdrop = document.getElementById('modalBackdrop');
  var modalTitle = document.getElementById('modalTitle');
  var modalMsg = document.getElementById('modalMsg');
  var modalInput = document.getElementById('modalInput');
  var modalOk = document.getElementById('modalOk');
  var modalCancel = document.getElementById('modalCancel');
  var modalResolver = null;

  function closeModal(result) {
    if (modalBackdrop) modalBackdrop.classList.remove('show');
    if (modalInput) modalInput.hidden = true;
    var r = modalResolver;
    modalResolver = null;
    if (r) r(result);
  }
  if (modalOk) modalOk.addEventListener('click', function () {
    var val = (modalInput && !modalInput.hidden) ? modalInput.value : true;
    closeModal(val);
  });
  if (modalCancel) modalCancel.addEventListener('click', function () { closeModal(null); });
  if (modalBackdrop) modalBackdrop.addEventListener('click', function (e) {
    if (e.target === modalBackdrop) closeModal(null);
  });

  // askConfirm(title, msg) → Promise<boolean>
  function askConfirm(title, msg) {
    if (!modalBackdrop) return Promise.resolve(window.confirm(title + '\n\n' + msg));
    modalTitle.textContent = title;
    modalMsg.textContent = msg;
    if (modalInput) { modalInput.hidden = true; modalInput.value = ''; }
    modalBackdrop.classList.add('show');
    return new Promise(function (res) { modalResolver = function (v) { res(v === true); }; });
  }
  // askText(title, msg, defaultVal) → Promise<string|null>
  function askText(title, msg, defaultVal) {
    if (!modalBackdrop) {
      var fallback = window.prompt(title + '\n\n' + msg, defaultVal || '');
      return Promise.resolve(fallback);
    }
    modalTitle.textContent = title;
    modalMsg.textContent = msg;
    if (modalInput) {
      modalInput.hidden = false;
      modalInput.type = 'text';
      modalInput.value = defaultVal || '';
      setTimeout(function () { try { modalInput.focus(); modalInput.select(); } catch (e) {} }, 50);
    }
    modalBackdrop.classList.add('show');
    return new Promise(function (res) {
      modalResolver = function (v) { res(v === null ? null : String(v)); };
    });
  }
  // showAlert(title, msg) → Promise<void> (Cancelar oculto, Aceptar solo)
  function showAlert(title, msg) {
    if (!modalBackdrop) { window.alert(title + '\n\n' + msg); return Promise.resolve(); }
    modalTitle.textContent = title;
    modalMsg.textContent = msg;
    if (modalInput) { modalInput.hidden = true; modalInput.value = ''; }
    if (modalCancel) modalCancel.style.display = 'none';
    modalBackdrop.classList.add('show');
    return new Promise(function (res) {
      modalResolver = function () { if (modalCancel) modalCancel.style.display = ''; res(); };
    });
  }

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function fmtNum(v, decimals) {
    if (v == null || isNaN(v)) return '—';
    return Number(v).toFixed(decimals || 0);
  }

  function fmtUptime(sec) {
    var s = Number(sec) || 0;
    if (s < 60) return s + ' s';
    if (s < 3600) return Math.floor(s / 60) + ' min';
    if (s < 86400) return Math.floor(s / 3600) + ' h ' + Math.floor((s % 3600) / 60) + ' min';
    return Math.floor(s / 86400) + ' d ' + Math.floor((s % 86400) / 3600) + ' h';
  }

  // El "primer" nodo habilitado del config es el de referencia para los KPIs.
  function activeNodo() {
    if (!cfg || !cfg.nodos) return null;
    for (var i = 0; i < cfg.nodos.length; i++) {
      if (cfg.nodos[i].habilitado) return cfg.nodos[i];
    }
    return cfg.nodos[0] || null;
  }

  function renderLive(snap) {
    if (!snap) return;

    var nodo = activeNodo();
    var sec = snap.sectionOnRequest || [];
    var num = snap.numSections || 0;
    var on = 0;
    for (var i = 0; i < num; i++) if (sec[i]) on++;

    if (kpiSecAct) kpiSecAct.textContent = on;
    if (kpiSecTot) kpiSecTot.textContent = num;

    var vel = snap.avgSpeed != null ? snap.avgSpeed : 0;
    if (kpiSpeed) kpiSpeed.textContent = fmtNum(vel, 1) + ' km/h';

    var anchoTotal = nodo ? (nodo.ancho_barra_m || 0) : 0;
    var anchoActivo = (num > 0 && anchoTotal > 0) ? (anchoTotal * on / num) : anchoTotal;
    if (kpiAncho) kpiAncho.textContent = fmtNum(anchoTotal, 1) + ' m';

    var prod0 = (nodo && nodo.productos && nodo.productos.length > 0) ? nodo.productos[0] : null;
    var dosis = prod0 ? (prod0.dosis_lha || 0) : 0;
    var modoManual = prod0 ? !!prod0.modo_manual : false;
    var manualLmin = prod0 ? (prod0.manual_lmin || 0) : 0;
    if (kpiDose) kpiDose.textContent = fmtNum(dosis, 0);

    // El objetivo se muestra en la unidad del modo: L/ha (dosis) en automático,
    // o L/min (caudal fijo) en manual, que es como el operario piensa cada modo.
    var flowUnit = document.getElementById('kpiFlowUnit');
    if (modoManual) {
      if (kpiTarget) kpiTarget.textContent = fmtNum(manualLmin, 1) + ' L/min';
      if (flowUnit) flowUnit.textContent = 'L/min';
    } else {
      if (kpiTarget) kpiTarget.textContent = fmtNum(dosis, 0) + ' L/ha';
      if (flowUnit) flowUnit.textContent = 'L/ha';
    }

    // Caudal real + PWM + estado PID (color-coded por estado).
    var liveNodo = null;
    if (live && live.nodos && nodo) {
      for (var j = 0; j < live.nodos.length; j++) {
        if (live.nodos[j].uid === nodo.uid) { liveNodo = live.nodos[j]; break; }
      }
    }
    if (liveNodo && liveNodo.online) {
      if (modoManual) {
        // En manual mostramos el caudal medido directo en L/min (no depende
        // de la velocidad: la válvula mantiene el caudal fijo).
        if (kpiFlow) kpiFlow.textContent = fmtNum(liveNodo.caudal_lmin || 0, 1);
      } else {
        // Caudal real en L/ha: reescalamos el L/min medido por vel y ancho
        // activo (L/ha = L/min · 600 / (vel · ancho)). Sin avance no es
        // computable, así que mostramos "—".
        var caudalLha = (vel > 0 && anchoActivo > 0)
          ? (liveNodo.caudal_lmin * 600 / (vel * anchoActivo)) : 0;
        if (kpiFlow) kpiFlow.textContent = (vel > 0 && anchoActivo > 0)
          ? fmtNum(caudalLha, 0) : '—';
      }
      if (kpiPwm)  kpiPwm.textContent  = fmtNum(liveNodo.pwm, 0);
      // Pulsos crudos del ISR — si la bomba gira y este número no sube,
      // el GPIO del caudalímetro no engancha (sensor / cable / nivel).
      var kpiPulsos = document.getElementById('kpiPulsos');
      if (kpiPulsos) kpiPulsos.textContent = (liveNodo.pulsos != null ? liveNodo.pulsos : '—');
      if (kpiPid) {
        var st = liveNodo.pid_estado || 'ok';
        kpiPid.textContent = st;
        kpiPid.className = 'pid-pill ' + st;
      }
    } else {
      if (kpiFlow) kpiFlow.textContent = '—';
      if (kpiPwm)  kpiPwm.textContent  = '—';
      if (kpiPid) {
        kpiPid.textContent = nodo ? 'Esperando telemetría' : 'Sin config';
        kpiPid.className = 'pid-pill off';
      }
    }

    var workedM2 = snap.workedAreaTotalM2 || 0;
    var actualM2 = snap.actualAreaCoveredM2 || 0;
    var overlapM2 = Math.max(0, workedM2 - actualM2);
    var actualHa = actualM2 * 0.0001;
    var overlapHa = overlapM2 * 0.0001;
    var overlapPct = workedM2 > 0 ? (overlapM2 / workedM2 * 100) : 0;
    var savedL = overlapHa * (dosis || 0);

    if (kpiAreaNeta)    kpiAreaNeta.textContent    = fmtNum(actualHa, 2);
    if (kpiAreaOverlap) kpiAreaOverlap.textContent = fmtNum(overlapHa, 2);
    if (kpiOverlapPct)  kpiOverlapPct.textContent  = fmtNum(overlapPct, 1);
    if (kpiSavedLitros) kpiSavedLitros.textContent = fmtNum(savedL, 1);
  }

  function renderNodos() {
    if (!nodosList) return;
    if (!nodos || nodos.length === 0) {
      nodosList.innerHTML = '<div class="subtitle">' +
        'No hay nodos FlowX en LAN. Los controladores se descubren al publicar ' +
        '<code>agp/flow/&lt;uid&gt;/announcement</code> en el broker MQTT del PC.' +
        '</div>';
      return;
    }
    var rows = nodos.map(function (n) {
      var dot = n.online ? '●' : '○';
      var color = n.online ? 'var(--agp-state-ok)' : 'var(--agp-text-muted)';
      var badges = [];
      if (n.boot_reason && n.boot_reason !== 'poweron') {
        badges.push('<span class="badge warn" title="Última razón de reset">' +
                    escapeHtml(n.boot_reason) + '</span>');
      }
      if (n.crash_count && Number(n.crash_count) > 0) {
        badges.push('<span class="badge warn" title="Crashes consecutivos en NVS">' +
                    n.crash_count + ' crashes</span>');
      }
      if (n.safe_mode) {
        badges.push('<span class="badge bad">SAFE-MODE</span>');
      }
      var safeBanner = n.safe_mode ? (
        '<div class="safe-mode-banner">' +
          '<strong>Nodo en safe-mode</strong>' +
          '<span>Solo acepta <code>ping</code> y <code>clear_safe_mode</code> hasta que lo resetees.</span>' +
          '<button type="button" class="btn" data-act="clear-safe" data-uid="' + escapeHtml(n.uid) + '">Limpiar safe-mode</button>' +
        '</div>'
      ) : '';
      return '<div class="nodo-row">' +
              '<span style="color:' + color + '">' + dot + '</span>' +
              '<span class="nodo-uid">' + escapeHtml(n.uid || '—') + '</span>' +
              '<span class="nodo-meta">' + escapeHtml(n.ip || '') + ' · fw ' + escapeHtml(n.firmware || '?') +
                (n.uptime ? ' · up ' + escapeHtml(fmtUptime(n.uptime)) : '') +
              '</span>' +
              badges.map(function (b) { return ' ' + b; }).join('') +
              '<span class="nodo-actions">' +
                '<button type="button" class="btn" data-act="ping" data-uid="' + escapeHtml(n.uid) + '">Ping</button>' +
              '</span>' +
              safeBanner +
            '</div>';
    }).join('');
    nodosList.innerHTML = rows;
  }

  async function loadCfg() {
    try {
      var res = await fetch('/api/flowx/config', { cache: 'no-store' });
      cfg = await res.json();
    } catch (e) { cfg = null; }
  }

  async function pollNodos() {
    try {
      var res = await fetch('/api/flowx/nodos', { cache: 'no-store' });
      var body = await res.json();
      nodos = (body && body.nodos) ? body.nodos : [];
      renderNodos();
    } catch (e) { /* offline */ }
  }

  async function pollLive() {
    try {
      var results = await Promise.all([
        fetch('/api/aog/state', { cache: 'no-store' }).then(function (r) { return r.json(); }).catch(function () { return null; }),
        fetch('/api/flowx/live', { cache: 'no-store' }).then(function (r) { return r.json(); }).catch(function () { return null; })
      ]);
      var snap = normalizeAogSnap(results[0]);
      live = results[1];
      if (snap) { aogSnap = snap; renderLive(snap); refreshAogHints(); }
    } catch (e) { /* offline */ }
  }

  var pollLiveHandle = null;
  var pollNodosHandle = null;
  function startPolling() {
    if (!pollLiveHandle) pollLiveHandle = setInterval(pollLive, 1000);
    if (!pollNodosHandle) pollNodosHandle = setInterval(pollNodos, 3000);
  }
  function stopPolling() {
    if (pollLiveHandle) { clearInterval(pollLiveHandle); pollLiveHandle = null; }
    if (pollNodosHandle) { clearInterval(pollNodosHandle); pollNodosHandle = null; }
  }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stopPolling();
    else { pollLive(); pollNodos(); startPolling(); }
  });

  // ==========================================================================
  // Editor de configuración
  // ==========================================================================
  var cfgEnabled    = document.getElementById('cfgEnabled');
  var nodoSelect    = document.getElementById('nodoSelect');
  var lanSelect     = document.getElementById('lanSelect');
  var btnImportNodo = document.getElementById('btnImportNodo');
  var btnDeleteNodo = document.getElementById('btnDeleteNodo');
  var nodoEditor    = document.getElementById('nodoEditor');
  var nodoUid       = document.getElementById('nodoUid');
  var nodoNombre    = document.getElementById('nodoNombre');
  var nodoAncho     = document.getElementById('nodoAncho');
  var nodoHab       = document.getElementById('nodoHab');
  var nodo3wire     = document.getElementById('nodo3wire');
  var nodoInv       = document.getElementById('nodoInv');
  var nodoInvMotor  = document.getElementById('nodoInvMotor');
  var tblProductos  = document.getElementById('tblProductos');
  var sec3wGrid     = document.getElementById('sec3wGrid');
  var btnAddProducto = document.getElementById('btnAddProducto');
  var nodoNumCortes = document.getElementById('nodoNumCortes');
  var nodoMaster    = document.getElementById('nodoMaster');
  var btnAutoAssign = document.getElementById('btnAutoAssign');
  var btnAnchoFromAog = document.getElementById('btnAnchoFromAog');
  var aogNumSec     = document.getElementById('aogNumSec');
  var cortesMap     = document.getElementById('cortesMap');
  var anchoHint     = document.getElementById('anchoHint');
  var btnSaveCfg    = document.getElementById('btnSaveCfg');
  var btnReload     = document.getElementById('btnReload');
  var btnPushConfig = document.getElementById('btnPushConfig');
  var btnPing       = document.getElementById('btnPing');
  var btnOta        = document.getElementById('btnOta');
  var otaUrl        = document.getElementById('otaUrl');
  var otaVersion    = document.getElementById('otaVersion');
  var otaSha        = document.getElementById('otaSha');
  var pushStatus    = document.getElementById('pushStatus');
  var saveStatus    = document.getElementById('saveStatus');

  var currentUid = null;

  function findNodo(uid) {
    if (!cfg || !cfg.nodos) return null;
    for (var i = 0; i < cfg.nodos.length; i++) {
      if (cfg.nodos[i].uid === uid) return cfg.nodos[i];
    }
    return null;
  }

  function renderNodoSelect() {
    if (!nodoSelect) return;
    nodoSelect.innerHTML = '';
    var list = (cfg && cfg.nodos) ? cfg.nodos : [];
    if (list.length === 0) {
      var opt = document.createElement('option');
      opt.value = '';
      opt.textContent = '(sin nodos en config)';
      nodoSelect.appendChild(opt);
      currentUid = null;
      renderEditor();
      return;
    }
    list.forEach(function (n) {
      var opt = document.createElement('option');
      opt.value = n.uid;
      opt.textContent = (n.nombre || n.uid) + ' — ' + n.uid;
      nodoSelect.appendChild(opt);
    });
    if (!currentUid || !findNodo(currentUid)) currentUid = list[0].uid;
    nodoSelect.value = currentUid;
    renderEditor();
  }

  function renderLanSelect() {
    if (!lanSelect) return;
    lanSelect.innerHTML = '';
    var inCfg = {};
    if (cfg && cfg.nodos) cfg.nodos.forEach(function (n) { inCfg[n.uid] = true; });
    var candidatos = (nodos || []).filter(function (n) { return n && n.uid && !inCfg[n.uid]; });
    if (candidatos.length === 0) {
      var opt = document.createElement('option');
      opt.value = '';
      opt.textContent = '(sin nodos LAN nuevos)';
      lanSelect.appendChild(opt);
      if (btnImportNodo) btnImportNodo.disabled = true;
      return;
    }
    if (btnImportNodo) btnImportNodo.disabled = false;
    candidatos.forEach(function (n) {
      var opt = document.createElement('option');
      opt.value = n.uid;
      opt.textContent = n.uid + ' · ' + (n.ip || '?');
      lanSelect.appendChild(opt);
    });
  }

  function renderEditor() {
    var n = findNodo(currentUid);
    if (!n) {
      if (nodoEditor) nodoEditor.hidden = true;
      return;
    }
    if (nodoEditor) nodoEditor.hidden = false;
    if (nodoUid)    nodoUid.value    = n.uid || '';
    if (nodoNombre) nodoNombre.value = n.nombre || '';
    if (nodoAncho)  nodoAncho.value  = (n.ancho_barra_m != null) ? n.ancho_barra_m : 0;
    if (nodoHab)    nodoHab.checked    = !!n.habilitado;
    if (nodo3wire)  nodo3wire.checked  = !!n.is_3wire;
    if (nodoInv)    nodoInv.checked    = !!n.invert_relay;
    if (nodoInvMotor) nodoInvMotor.checked = !!n.invert_motor;
    renderProductos(n);
    renderCortes(n);
    renderMaster(n);
    renderSec3w(n);
    refreshAogHints();
  }

  // Master: -1 = salida dedicada (firmware), 0 = sin master, 1..nCortes = corte.
  // Las opciones de corte se generan según la cantidad de cortes del nodo.
  function renderMaster(n) {
    if (!nodoMaster) return;
    var nCortes = inferNumCortes(n);
    var cur = (n && n.master_cable != null) ? parseInt(n.master_cable, 10) : -1;
    if (isNaN(cur)) cur = -1;
    var html =
      '<option value="-1"' + (cur === -1 ? ' selected' : '') + '>Salida dedicada (firmware)</option>' +
      '<option value="0"'  + (cur === 0  ? ' selected' : '') + '>Sin master</option>';
    for (var i = 1; i <= nCortes; i++) {
      html += '<option value="' + i + '"' + (cur === i ? ' selected' : '') + '>Corte ' + i + '</option>';
    }
    nodoMaster.innerHTML = html;
  }

  function renderProductos(n) {
    if (!tblProductos) return;
    var tbody = tblProductos.querySelector('tbody');
    tbody.innerHTML = '';
    (n.productos || []).forEach(function (p, idx) {
      var tr = document.createElement('tr');
      tr.innerHTML =
        '<td><input type="number" min="0" max="1" step="1" data-k="id" value="' + (p.id || 0) + '" style="width:60px" title="0=reg. 1 (principal), 1=reg. 2 (firmware MaxProductCount=2)"></td>' +
        '<td><input type="text"                   data-k="nombre"    value="' + escapeHtml(p.nombre || '') + '" style="width:140px"></td>' +
        '<td><select data-k="tipo" style="width:90px" title="Válvula motorizada o motor de bomba">' +
            '<option value="valvula"' + (p.tipo === 'motor' ? '' : ' selected') + '>Válvula</option>' +
            '<option value="motor"'   + (p.tipo === 'motor' ? ' selected' : '') + '>Motor</option>' +
          '</select></td>' +
        '<td><select data-k="flow_index" style="width:70px" title="Cuál de los 2 caudalímetros lee esta reguladora">' +
            '<option value="0"' + ((p.flow_index | 0) === 1 ? '' : ' selected') + '>1</option>' +
            '<option value="1"' + ((p.flow_index | 0) === 1 ? ' selected' : '') + '>2</option>' +
          '</select></td>' +
        '<td><input type="number" step="0.1"      data-k="meter_cal" value="' + (p.meter_cal || 0) + '" style="width:100px"></td>' +
        '<td><input type="number" data-adaptive="dose" data-k="dosis_lha" value="' + (p.dosis_lha || 0) + '" style="width:90px"></td>' +
        '<td style="text-align:center;"><input type="checkbox" data-k="modo_manual"' + (p.modo_manual ? ' checked' : '') + '></td>' +
        '<td><input type="number" step="0.1" min="0" data-k="manual_lmin" value="' + (p.manual_lmin || 0) + '" style="width:80px"></td>' +
        '<td><input type="number" step="0.1" min="0" data-k="paso_lha" value="' + (p.paso_lha != null ? p.paso_lha : 5) + '" style="width:80px"></td>' +
        '<td><input type="number" step="0.1" min="0" data-k="paso_lmin" value="' + (p.paso_lmin != null ? p.paso_lmin : 1) + '" style="width:80px"></td>' +
        '<td><input type="number" step="1" min="0" max="4095" data-k="pwm_min"   value="' + (p.pwm_min || 0) + '" style="width:80px" title="PWM al que arranca el PID (12-bit, 0..4095)"></td>' +
        '<td><input type="number" step="1" min="0" max="4095" data-k="pwm_max"   value="' + (p.pwm_max != null ? p.pwm_max : 4095) + '" style="width:80px" title="PWM máximo del PID (0 ó ≤min = sin techo)"></td>' +
        '<td><input type="number" data-adaptive="pid"  data-k="kp"        value="' + (p.kp || 0) + '" style="width:70px"></td>' +
        '<td><input type="number" data-adaptive="pid"  data-k="ki"        value="' + (p.ki || 0) + '" style="width:70px"></td>' +
        '<td><input type="number" data-adaptive="pid"  data-k="kd"        value="' + (p.kd || 0) + '" style="width:70px"></td>' +
        '<td style="text-align:center;"><input type="checkbox" data-k="invert_motor"' + (p.invert_motor ? ' checked' : '') + ' title="Invertir sentido de esta reguladora"></td>' +
        '<td style="white-space:nowrap;">' +
          '<button type="button" class="btn" data-act="calibrar" data-idx="' + idx + '" title="Calibrar caudalímetro">Calibrar</button> ' +
          '<button type="button" class="btn" data-act="autotune" data-idx="' + idx + '" title="Auto-tune PID">Auto-tune</button> ' +
          '<button type="button" class="btn" data-act="caracterizar" data-idx="' + idx + '" title="Barrido PWM para detectar pwm_min real">Detectar PWM</button> ' +
          '<button type="button" class="btn" data-act="delete"   data-idx="' + idx + '" style="color:var(--agp-state-bad)">×</button>' +
        '</td>';
      tbody.appendChild(tr);
    });
    if (window.AGPSteps) {
      tblProductos.querySelectorAll('input[data-adaptive]').forEach(function (inp) {
        window.AGPSteps.attachAdaptive(inp, inp.getAttribute('data-adaptive'));
      });
    }
  }

  // ============================================================================
  // Cortes (versión simplificada de la antigua tabla "cables").
  //
  // Modelo: el usuario dice cuántos cortes tiene la barra (= cantidad de válvulas
  // físicas = salidas usadas del PCA9685). PilotX asigna automáticamente sus
  // secciones a esos cortes, repartiéndolas en grupos consecutivos del mismo
  // tamaño (con el último grupo absorbiendo el resto si no divide exacto).
  //
  // Esto se persiste como `n.cables[]` (formato heredado: {cable, seccion_aog})
  // para no tocar el bridge: cuando un corte agrupa secciones N..M, generamos
  // M-N+1 entradas con el mismo `cable` y distintos `seccion_aog`. El bridge
  // hace OR sobre bits[cable-1].
  // ============================================================================
  function getCurrentNumSecAog() {
    if (aogSnap && Number(aogSnap.numSections) > 0) return Number(aogSnap.numSections);
    return 0;
  }
  function inferNumCortes(n) {
    if (!n) return DEFAULT_CORTES_PER_NODE;
    // Si ya hay cables[], num_cortes = cantidad de cables únicos.
    if (n.cables && n.cables.length > 0) {
      var uniq = {};
      n.cables.forEach(function (c) { if (c.cable > 0) uniq[c.cable] = true; });
      var k = Object.keys(uniq).length;
      if (k > 0) return k;
    }
    // Default fijo: cada nodo FlowX maneja 7 cortes (HW estándar).
    return DEFAULT_CORTES_PER_NODE;
  }
  // Asignación: nCortes cortes ↔ nSec secciones. Devuelve array de cables.
  // Distribución uniforme: corte i ↔ secciones [floor(nSec*i/nCortes)+1 .. floor(nSec*(i+1)/nCortes)].
  function autoAssignCortes(nCortes, nSec) {
    var out = [];
    if (nCortes <= 0 || nSec <= 0) return out;
    for (var i = 0; i < nCortes; i++) {
      var start = Math.floor(nSec * i / nCortes);
      var end = Math.floor(nSec * (i + 1) / nCortes);
      if (end <= start) end = start + 1;       // cada corte ≥ 1 sección si nCortes > nSec
      if (end > nSec) end = nSec;
      for (var s = start; s < end; s++) {
        out.push({ cable: i + 1, seccion_aog: s + 1 });
      }
    }
    return out;
  }
  function renderCortes(n) {
    if (nodoNumCortes) {
      var k = inferNumCortes(n);
      nodoNumCortes.value = k;
    }
    renderCortesMap(n);
  }
  function renderCortesMap(n) {
    if (!cortesMap) return;
    var cables = (n && n.cables) ? n.cables : [];
    if (cables.length === 0) {
      cortesMap.textContent = 'Sin asignación. Pulsá "Asignar automáticamente" después de cargar la cantidad de cortes.';
      return;
    }
    var byCable = {};
    cables.forEach(function (c) {
      if (!c || c.cable < 1 || c.seccion_aog < 1) return;
      if (!byCable[c.cable]) byCable[c.cable] = [];
      byCable[c.cable].push(c.seccion_aog);
    });
    var keys = Object.keys(byCable).map(Number).sort(function (a, b) { return a - b; });
    var parts = keys.map(function (k) {
      var secs = byCable[k].sort(function (a, b) { return a - b; });
      var label = secs.length === 1 ? ('sección ' + secs[0])
                : ('secciones ' + secs.join(', '));
      return '<strong>Corte ' + k + '</strong> → ' + label;
    });
    cortesMap.innerHTML = parts.join(' · ');
  }
  function refreshAogHints() {
    var num = getCurrentNumSecAog();
    if (aogNumSec) aogNumSec.textContent = num > 0 ? String(num) : '— (sin estado AOG)';
    if (anchoHint && aogSnap && Number(aogSnap.toolWidth) > 0) {
      anchoHint.textContent = 'PilotX reporta ancho de implemento: ' +
                              Number(aogSnap.toolWidth).toFixed(2) + ' m';
    } else if (anchoHint) {
      anchoHint.textContent = '';
    }
  }

  // sectionIs3Wire: array de 10 enteros. UI = 10 selectores con -1/0/1.
  // Si el array viene corto, se rellena con -1; si viene largo, se trunca.
  function normalizeSec3w(arr) {
    var out = [];
    for (var i = 0; i < 10; i++) {
      var v = (arr && arr[i] != null) ? parseInt(arr[i], 10) : -1;
      if (v !== 0 && v !== 1) v = -1;
      out.push(v);
    }
    return out;
  }
  function renderSec3w(n) {
    if (!sec3wGrid) return;
    var arr = normalizeSec3w(n.section_is_3wire);
    n.section_is_3wire = arr; // dejarlo persistido en cfg
    // El array del firmware es de 10 ítems fijo, pero acá mostramos solo los
    // cortes reales del nodo. Los extras se envían como -1 (Global) en el push.
    var nCortes = inferNumCortes(n);
    var shown = Math.min(10, Math.max(1, nCortes));
    // Ajustar grilla según cantidad real (CSS define repeat(10,1fr) — lo
    // recalculamos inline para que las celdas crezcan bien con 7 cortes).
    sec3wGrid.style.gridTemplateColumns = 'repeat(' + shown + ', 1fr)';
    sec3wGrid.innerHTML = '';
    for (var i = 0; i < shown; i++) {
      var cell = document.createElement('div');
      cell.className = 'sec3w-cell';
      var lbl = 'Corte ' + (i + 1);
      cell.innerHTML =
        '<label>' + lbl + '</label>' +
        '<select data-sec3w-idx="' + i + '">' +
          '<option value="-1"' + (arr[i] === -1 ? ' selected' : '') + '>Global</option>' +
          '<option value="0"'  + (arr[i] === 0  ? ' selected' : '') + '>2 cables</option>' +
          '<option value="1"'  + (arr[i] === 1  ? ' selected' : '') + '>3 cables</option>' +
        '</select>';
      sec3wGrid.appendChild(cell);
    }
  }

  function commitEditorToCfg() {
    var n = findNodo(currentUid);
    if (!n) return;
    n.nombre        = nodoNombre ? nodoNombre.value : n.nombre;
    n.ancho_barra_m = nodoAncho  ? parseFloat(nodoAncho.value) || 0 : n.ancho_barra_m;
    n.habilitado    = !!(nodoHab && nodoHab.checked);
    n.is_3wire      = !!(nodo3wire && nodo3wire.checked);
    n.invert_relay  = !!(nodoInv && nodoInv.checked);
    n.invert_motor  = !!(nodoInvMotor && nodoInvMotor.checked);
    if (nodoMaster) {
      var mc = parseInt(nodoMaster.value, 10);
      n.master_cable = isNaN(mc) ? -1 : mc;
    } else if (n.master_cable == null) {
      n.master_cable = -1;
    }

    var prods = [];
    if (tblProductos) {
      var rows = tblProductos.querySelectorAll('tbody tr');
      rows.forEach(function (tr) {
        // [data-k] captura inputs Y selects (tipo/flow_index son <select>).
        var inputs = tr.querySelectorAll('[data-k]');
        var p = { id: 0, nombre: '', tipo: 'valvula', flow_index: 0, invert_motor: false,
                  meter_cal: 0, dosis_lha: 0, pwm_min: 0, pwm_max: 4095, kp: 0, ki: 0, kd: 0,
                  modo_manual: false, manual_lmin: 0, paso_lha: 5, paso_lmin: 1 };
        inputs.forEach(function (inp) {
          var k = inp.getAttribute('data-k');
          if (k === 'nombre' || k === 'tipo') p[k] = inp.value;
          else if (k === 'modo_manual' || k === 'invert_motor') p[k] = !!inp.checked;
          else if (k === 'id' || k === 'pwm_min' || k === 'pwm_max' || k === 'flow_index') p[k] = parseInt(inp.value, 10) || 0;
          else p[k] = parseFloat(inp.value) || 0;
        });
        // Firmware acepta id ∈ {0,1} (MaxProductCount=2). Clamp acá para que
        // no se guarden valores que el ESP va a forzar a 0 sin avisar.
        if (p.id < 0) p.id = 0;
        if (p.id > 1) p.id = 1;
        prods.push(p);
      });
    }
    n.productos = prods;

    // Cables: si el usuario cambió `nodoNumCortes`, re-asignamos automático
    // contra las secciones AOG conocidas. Si la cantidad coincide con la actual,
    // dejamos `n.cables` como está (preserva asignaciones manuales tras un
    // reload del editor en la misma sesión).
    if (nodoNumCortes) {
      var nCortes = Math.max(1, parseInt(nodoNumCortes.value, 10) || 1);
      var existingCortes = 0;
      if (n.cables && n.cables.length > 0) {
        var uniq = {};
        n.cables.forEach(function (c) { if (c && c.cable > 0) uniq[c.cable] = true; });
        existingCortes = Object.keys(uniq).length;
      }
      var nSec = getCurrentNumSecAog();
      if (nCortes !== existingCortes && nSec > 0) {
        n.cables = autoAssignCortes(nCortes, nSec);
      } else if (!n.cables) {
        n.cables = [];
      }
    } else if (!n.cables) {
      n.cables = [];
    }

    // sectionIs3Wire: solo leemos los slots renderizados (1..nCortes).
    // El resto del array de 10 (slots no visibles) lo dejamos en -1 ("Global"),
    // que es lo que el firmware espera para entradas no usadas.
    if (sec3wGrid) {
      var sels = sec3wGrid.querySelectorAll('select[data-sec3w-idx]');
      var arr = new Array(10).fill(-1);
      sels.forEach(function (sel) {
        var i = parseInt(sel.getAttribute('data-sec3w-idx'), 10);
        var v = parseInt(sel.value, 10);
        if (i >= 0 && i < 10) arr[i] = (v === 0 || v === 1) ? v : -1;
      });
      n.section_is_3wire = arr;
    }

    if (cfg) cfg.enabled = !!(cfgEnabled && cfgEnabled.checked);
  }

  async function saveCfg() {
    if (!cfg) return;
    commitEditorToCfg();
    if (saveStatus) saveStatus.textContent = 'Guardando…';
    try {
      var res = await fetch('/api/flowx/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var body = await res.json();
      if (body && body.ok) {
        if (saveStatus) {
          saveStatus.textContent = 'Guardado ✓';
          saveStatus.style.color = 'var(--agp-state-ok)';
          setTimeout(function () { saveStatus.textContent = ''; saveStatus.style.color = ''; }, 2500);
        }
      } else {
        if (saveStatus) {
          saveStatus.textContent = 'Error: ' + ((body && body.error) || 'unknown');
          saveStatus.style.color = 'var(--agp-state-bad)';
        }
      }
    } catch (e) {
      if (saveStatus) {
        saveStatus.textContent = 'Error de red';
        saveStatus.style.color = 'var(--agp-state-bad)';
      }
    }
  }

  // ==========================================================================
  // Comandos al firmware: ping, clear_safe_mode, ota, config-push
  // ==========================================================================
  function sendCmd(uid, verb, body) {
    return fetch('/api/flowx/' + encodeURIComponent(uid) + '/cmd?verb=' + encodeURIComponent(verb),
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      })
      .then(function (r) { return r.json(); });
  }

  function pushConfigToFirmware(uid, payload) {
    return fetch('/api/flowx/' + encodeURIComponent(uid) + '/config-push',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      })
      .then(function (r) { return r.json(); });
  }

  function setPushStatus(text, ok) {
    if (!pushStatus) return;
    pushStatus.textContent = text;
    pushStatus.style.color = ok ? 'var(--agp-state-ok)' : 'var(--agp-state-bad)';
    if (text) setTimeout(function () { pushStatus.textContent = ''; pushStatus.style.color = ''; }, 3000);
  }

  function pollResult(uid, kind, timeoutMs) {
    var t0 = Date.now();
    return new Promise(function (resolve, reject) {
      (function loop() {
        if (Date.now() - t0 > timeoutMs) {
          reject(new Error('Timeout esperando respuesta del nodo.'));
          return;
        }
        fetch('/api/flowx/' + encodeURIComponent(uid) + '/' + kind, { cache: 'no-store' })
          .then(function (r) { return r.json(); })
          .then(function (body) {
            if (body && body.hasResult && body.result) resolve(body.result);
            else setTimeout(loop, 500);
          })
          .catch(function () { setTimeout(loop, 500); });
      })();
    });
  }

  async function runCalibrar(n, prodIdx) {
    var p = n.productos[prodIdx];
    var volStr = await askText(
      'Calibración del caudalímetro',
      'Vamos a contar pulsos del flowmeter mientras pasa un volumen conocido.\n' +
      'Ingresá el volumen exacto que vas a verter (litros):',
      '1'
    );
    if (volStr == null) return;
    var vol = parseFloat(volStr);
    if (!isFinite(vol) || vol <= 0) { await showAlert('Volumen inválido', 'El valor ingresado no es un número positivo.'); return; }

    try {
      var startRes = await sendCmd(n.uid, 'calibrar_start', { producto_id: p.id, vol_l: vol, pwm: 2048 });
      if (!startRes || !startRes.ok) {
        await showAlert('Error de inicio', 'No se pudo iniciar la calibración: ' + ((startRes && startRes.error) || 'fallo MQTT'));
        return;
      }
      var ok = await askConfirm(
        'Calibración en curso',
        'Calibración iniciada en producto "' + (p.nombre || p.id) + '".\n\n' +
        'Vertí exactamente ' + vol + ' L por el caudalímetro.\n\n' +
        'Cuando termines, pulsá Aceptar para detener y leer pulsos.\n' +
        'Cancelar = abortar.'
      );
      if (!ok) {
        await sendCmd(n.uid, 'calibrar_stop', { producto_id: p.id });
        return;
      }
      await sendCmd(n.uid, 'calibrar_stop', { producto_id: p.id });
      var r = await pollResult(n.uid, 'calibrar', 8000);
      if (!r.ok) {
        await showAlert('Error del nodo', 'El firmware devolvió error: ' + (r.error || 'desconocido'));
        return;
      }
      var pulsos = Number(r.pulsos) || 0;
      if (pulsos <= 0) {
        await showAlert('Sin pulsos', 'No se detectaron pulsos. Revisá cableado del caudalímetro y reintentá.');
        return;
      }
      var meterCal = pulsos / vol;
      var apply = await askConfirm(
        'Resultado de calibración',
        '· Pulsos: ' + pulsos + '\n' +
        '· Volumen: ' + vol + ' L\n' +
        '· meter_cal calculado: ' + meterCal.toFixed(2) + ' pulsos/L\n\n' +
        '¿Aplicar al producto "' + (p.nombre || p.id) + '"?'
      );
      if (apply) {
        p.meter_cal = Math.round(meterCal * 100) / 100;
        renderProductos(n);
      }
    } catch (e) {
      await showAlert('Calibración fallida', e.message || String(e));
    }
  }

  async function runAutotune(n, prodIdx) {
    var p = n.productos[prodIdx];
    var ok = await askConfirm(
      'Auto-tune PID',
      'El firmware aplicará escalones al motor de la bomba durante ~30 s.\n' +
      'Asegurate de que el sistema esté presurizado y la barra cerrada.\n\n' +
      'Producto: ' + (p.nombre || p.id) + '\n\n' +
      '¿Continuar?'
    );
    if (!ok) return;
    try {
      var startRes = await sendCmd(n.uid, 'autotune_start', {
        producto_id: p.id, setpoint_hz: 5.0, pwm_high: 4095, pwm_low: 200
      });
      if (!startRes || !startRes.ok) {
        await showAlert('Error de inicio', 'No se pudo iniciar auto-tune: ' + ((startRes && startRes.error) || 'fallo MQTT'));
        return;
      }
      var r = await pollResult(n.uid, 'autotune', 45000);
      if (!r.ok) {
        await showAlert('Auto-tune falló', r.error || 'Sin oscilación detectada.');
        return;
      }
      var apply = await askConfirm(
        'Auto-tune OK',
        '· Kp = ' + (Number(r.kp) || 0).toFixed(3) + '\n' +
        '· Ki = ' + (Number(r.ki) || 0).toFixed(3) + '\n' +
        '· Kd = ' + (Number(r.kd) || 0).toFixed(3) + '\n' +
        (r.ku ? '· Ku = ' + (Number(r.ku) || 0).toFixed(3) + '\n' : '') +
        (r.tu_ms ? '· Tu = ' + (Number(r.tu_ms) || 0).toFixed(0) + ' ms\n' : '') +
        '\n¿Aplicar al producto "' + (p.nombre || p.id) + '"?'
      );
      if (apply) {
        p.kp = Number(r.kp) || 0;
        p.ki = Number(r.ki) || 0;
        p.kd = Number(r.kd) || 0;
        renderProductos(n);
      }
    } catch (e) {
      await showAlert('Auto-tune fallido', e.message || String(e));
    }
  }

  // Barrido PWM 0..4095 que mide Hz por paso. Devuelve:
  //   pwm_min          -> primer PWM con flujo > 0.5 Hz (arranque mecánico)
  //   pwm_min_estable  -> primer PWM con flujo > 5 Hz (utilizable por el PID)
  //   hz_max, lmin_max -> tope físico de la bomba
  //   curva[] de {pwm, hz} para diagnóstico
  // Requiere bomba cebada + master + al menos una sección abierta (lo valida
  // el firmware y devuelve err si no se cumple).
  async function runCaracterizar(n, prodIdx) {
    var p = n.productos[prodIdx];
    var ok = await askConfirm(
      'Detectar PWM mínimo',
      'El nodo va a barrer el PWM desde 0 hasta 4095 midiendo el caudal.\n' +
      'Esto descubre el PWM real al que arranca la bomba y el caudal máximo.\n\n' +
      'Requisitos:\n' +
      '· Bomba cebada con agua circulando.\n' +
      '· Llave general (master) abierta y al menos una sección abierta.\n\n' +
      'Producto: ' + (p.nombre || p.id) + '\n\n' +
      '¿Empezar?'
    );
    if (!ok) return;
    try {
      // Limpiar resultado viejo para no leer una corrida previa.
      try {
        await fetch('/api/flowx/' + encodeURIComponent(n.uid) + '/caracterizar',
          { method: 'DELETE', cache: 'no-store' });
      } catch (_) { /* no bloquea */ }

      var startRes = await sendCmd(n.uid, 'caracterizar_start', { producto_id: p.id });
      if (!startRes || !startRes.ok) {
        await showAlert('Error de inicio',
          'No se pudo iniciar la caracterización: ' + ((startRes && startRes.error) || 'fallo MQTT'));
        return;
      }
      // 30 steps * 500ms = 15s mínimos en firmware; margen amplio por reintentos.
      var r = await pollResult(n.uid, 'caracterizar', 60000);
      if (!r.ok) {
        await showAlert('Caracterización falló', r.error || 'El firmware no completó el barrido.');
        return;
      }
      var pwmMin       = (r.pwm_min != null)         ? Number(r.pwm_min)         : null;
      var pwmMinEstab  = (r.pwm_min_estable != null) ? Number(r.pwm_min_estable) : null;
      var hzMax        = (r.hz_max != null)          ? Number(r.hz_max)          : null;
      var lminMax      = (r.lmin_max != null)        ? Number(r.lmin_max)        : null;
      var msg =
        '· PWM arranque (Hz > 0.5): ' + (pwmMin != null ? pwmMin : 'n/d') + '\n' +
        '· PWM mínimo estable (Hz > 5): ' + (pwmMinEstab != null ? pwmMinEstab : 'n/d') + '\n' +
        '· Hz máximo: ' + (hzMax != null ? hzMax.toFixed(1) : 'n/d') + '\n' +
        '· L/min máximo: ' + (lminMax != null ? lminMax.toFixed(2) : 'n/d') + '\n\n' +
        '¿Aplicar PWM mínimo estable como pwm_min del producto "' + (p.nombre || p.id) + '"?';
      var apply = await askConfirm('Resultado caracterización', msg);
      if (apply && pwmMinEstab != null && pwmMinEstab > 0) {
        p.pwm_min = pwmMinEstab;
        renderProductos(n);
      }
    } catch (e) {
      await showAlert('Caracterización fallida', e.message || String(e));
    }
  }

  // ----- Handlers -----
  if (nodoSelect) {
    nodoSelect.addEventListener('change', function () {
      commitEditorToCfg();
      currentUid = nodoSelect.value || null;
      renderEditor();
    });
  }

  if (btnImportNodo) {
    btnImportNodo.addEventListener('click', function () {
      if (!cfg) return;
      var uid = lanSelect ? lanSelect.value : '';
      if (!uid) return;
      if (findNodo(uid)) return;
      commitEditorToCfg();
      var lanInfo = (nodos || []).find(function (n) { return n.uid === uid; });
      cfg.nodos = cfg.nodos || [];
      // Cada nodo FlowX maneja 7 cortes (HW estándar). Si AOG ya reporta sus
      // secciones, los asignamos al toque; si no, dejamos cables[] vacío y el
      // usuario asigna después con el botón.
      var nSec = getCurrentNumSecAog();
      var defaultCables = nSec > 0
        ? autoAssignCortes(DEFAULT_CORTES_PER_NODE, nSec)
        : [];
      var defaultAncho = (aogSnap && Number(aogSnap.toolWidth) > 0) ? Number(aogSnap.toolWidth) : 0;
      cfg.nodos.push({
        uid: uid,
        nombre: (lanInfo && lanInfo.nombre) || 'Nodo FlowX',
        habilitado: true,
        ancho_barra_m: defaultAncho,
        is_3wire: false,
        invert_relay: false,
        invert_motor: false,
        master_cable: -1,
        section_is_3wire: new Array(10).fill(-1),
        productos: [{ id: 0, nombre: 'Producto', tipo: 'valvula', flow_index: 0, invert_motor: false, meter_cal: 100, dosis_lha: 100, pwm_min: 40, pwm_max: 4095, kp: 1, ki: 0.1, kd: 0, modo_manual: false, manual_lmin: 0, paso_lha: 5, paso_lmin: 1 }],
        cables: defaultCables
      });
      currentUid = uid;
      renderNodoSelect();
      renderLanSelect();
    });
  }

  if (btnDeleteNodo) {
    btnDeleteNodo.addEventListener('click', async function () {
      if (!cfg || !currentUid) return;
      var ok = await askConfirm('Eliminar nodo', '¿Eliminar nodo ' + currentUid + ' de la configuración?');
      if (!ok) return;
      cfg.nodos = (cfg.nodos || []).filter(function (n) { return n.uid !== currentUid; });
      currentUid = null;
      renderNodoSelect();
      renderLanSelect();
    });
  }

  if (btnAddProducto) {
    btnAddProducto.addEventListener('click', async function () {
      var n = findNodo(currentUid);
      if (!n) return;
      commitEditorToCfg();
      n.productos = n.productos || [];
      if (n.productos.length >= 2) {
        await showAlert('Límite del firmware',
          'El firmware FlowX soporta hasta 2 productos (canal 0 = semilla, canal 1 = fertilizante).\n' +
          'Productos extra serían ignorados por el nodo.');
        return;
      }
      var nextId = 0;
      n.productos.forEach(function (p) { if (p.id >= nextId) nextId = p.id + 1; });
      if (nextId > 1) nextId = 1;
      n.productos.push({ id: nextId, nombre: 'Producto ' + (n.productos.length + 1), tipo: 'valvula', flow_index: nextId, invert_motor: false, meter_cal: 100, dosis_lha: 100, pwm_min: 40, pwm_max: 4095, kp: 1, ki: 0.1, kd: 0, modo_manual: false, manual_lmin: 0, paso_lha: 5, paso_lmin: 1 });
      renderProductos(n);
    });
  }

  if (btnAutoAssign) {
    btnAutoAssign.addEventListener('click', async function () {
      var n = findNodo(currentUid);
      if (!n) return;
      var nCortes = Math.max(1, parseInt(nodoNumCortes && nodoNumCortes.value, 10) || 1);
      var nSec = getCurrentNumSecAog();
      if (nSec <= 0) {
        await showAlert('Falta estado de PilotX',
          'Todavía no hay info de cantidad de secciones desde PilotX.\n' +
          'Abrí el implemento y volvé a esta página.');
        return;
      }
      if (nCortes > 16) {
        await showAlert('Demasiados cortes',
          'El firmware FlowX soporta hasta 16 cortes (salidas del PCA9685).');
        return;
      }
      // commit del resto del editor antes (para no perder cambios paralelos)
      var saved = n.cables; n.cables = null; // forzamos re-asignación
      commitEditorToCfg();
      n.cables = autoAssignCortes(nCortes, nSec);
      renderCortesMap(n);
    });
  }
  if (btnAnchoFromAog) {
    btnAnchoFromAog.addEventListener('click', async function () {
      var n = findNodo(currentUid);
      if (!n) return;
      var w = aogSnap ? Number(aogSnap.toolWidth) : 0;
      if (!isFinite(w) || w <= 0) {
        await showAlert('Sin ancho disponible',
          'PilotX no reporta ancho de implemento todavía.\nConfigurá el implemento primero.');
        return;
      }
      if (nodoAncho) nodoAncho.value = w.toFixed(2);
      n.ancho_barra_m = w;
    });
  }
  // Re-asignar automático cuando cambian los cortes (sin esperar al botón).
  if (nodoNumCortes) {
    nodoNumCortes.addEventListener('change', function () {
      var n = findNodo(currentUid);
      if (!n) return;
      var nCortes = Math.max(1, parseInt(nodoNumCortes.value, 10) || 1);
      var nSec = getCurrentNumSecAog();
      if (nSec > 0) {
        n.cables = autoAssignCortes(nCortes, nSec);
        renderCortesMap(n);
      }
      // Preservar la master elegida antes de regenerar las opciones de corte.
      if (nodoMaster) {
        var mc = parseInt(nodoMaster.value, 10);
        n.master_cable = isNaN(mc) ? -1 : mc;
      }
      renderMaster(n);
      // El editor de override 2/3 cables se redibuja para coincidir con
      // los cortes reales del nodo (en vez de mostrar 10 slots vacíos).
      renderSec3w(n);
    });
  }

  // Push config persistente al firmware (agp/flow/{uid}/config).
  if (btnPushConfig) {
    btnPushConfig.addEventListener('click', async function () {
      var n = findNodo(currentUid);
      if (!n) return;
      commitEditorToCfg();
      // meterCal canónico = el del primer producto (canal 0). El firmware solo
      // tiene un Sensor[0] global, no per-producto.
      var meterCal = (n.productos && n.productos.length > 0) ? (n.productos[0].meter_cal || 0) : 0;
      var payload = {
        meterCal: meterCal,
        is3Wire: !!n.is_3wire,
        invertRelay: !!n.invert_relay,
        invertMotor: !!n.invert_motor,
        sectionIs3Wire: normalizeSec3w(n.section_is_3wire)
      };
      setPushStatus('Enviando…', true);
      try {
        var res = await pushConfigToFirmware(n.uid, payload);
        if (res && res.ok) setPushStatus('Enviado a ' + res.topic, true);
        else setPushStatus('Error: ' + ((res && res.error) || 'fallo MQTT'), false);
      } catch (e) {
        setPushStatus('Error de red', false);
      }
    });
  }

  // Ping del editor (botón inline al lado de Push config).
  if (btnPing) {
    btnPing.addEventListener('click', async function () {
      var n = findNodo(currentUid);
      if (!n) return;
      setPushStatus('Pingueando…', true);
      try {
        var res = await sendCmd(n.uid, 'ping', {});
        if (res && res.ok) setPushStatus('Ping enviado', true);
        else setPushStatus('Error: ' + ((res && res.error) || 'fallo MQTT'), false);
      } catch (e) { setPushStatus('Error de red', false); }
    });
  }

  // Push OTA (verbo cmd/ota, body con url/version/sha256).
  if (btnOta) {
    btnOta.addEventListener('click', async function () {
      var n = findNodo(currentUid);
      if (!n) return;
      var url = (otaUrl && otaUrl.value || '').trim();
      var version = (otaVersion && otaVersion.value || '').trim();
      var sha = (otaSha && otaSha.value || '').trim();
      if (!url || !version) {
        await showAlert('Faltan parámetros', 'URL y versión son obligatorios para el OTA.');
        return;
      }
      if (sha && !/^[0-9a-fA-F]{64}$/.test(sha)) {
        await showAlert('SHA-256 inválido', 'Debe ser 64 caracteres hexadecimales, o dejar vacío para no verificar.');
        return;
      }
      var ok = await askConfirm(
        'Confirmar OTA',
        'El nodo cerrará secciones, frenará la bomba y reflasheará a la versión ' + version + '.\n\n' +
        '¿Continuar?'
      );
      if (!ok) return;
      var payload = { url: url, version: version };
      if (sha) payload.sha256 = sha;
      setPushStatus('Lanzando OTA…', true);
      try {
        var res = await sendCmd(n.uid, 'ota', payload);
        if (res && res.ok) setPushStatus('OTA enviada — esperá reboot del nodo', true);
        else setPushStatus('Error: ' + ((res && res.error) || 'fallo MQTT'), false);
      } catch (e) { setPushStatus('Error de red', false); }
    });
  }

  // Acciones en la tarjeta de Nodos LAN (Ping + Clear safe-mode).
  if (nodosList) {
    nodosList.addEventListener('click', async function (e) {
      var btn = e.target.closest && e.target.closest('button[data-act]');
      if (!btn) return;
      var uid = btn.getAttribute('data-uid');
      var act = btn.getAttribute('data-act');
      if (!uid) return;
      try {
        if (act === 'ping') {
          await sendCmd(uid, 'ping', {});
        } else if (act === 'clear-safe') {
          var ok = await askConfirm('Limpiar safe-mode',
            'Reseteo el contador de crashes del nodo ' + uid + ' y vuelve a operación normal.\n\n¿Continuar?');
          if (!ok) return;
          await sendCmd(uid, 'clear_safe_mode', {});
          await pollNodos();
        }
      } catch (err) { /* silencioso */ }
    });
  }

  if (tblProductos) {
    tblProductos.addEventListener('click', function (e) {
      var btn = e.target.closest && e.target.closest('button[data-act]');
      if (!btn) return;
      var n = findNodo(currentUid);
      if (!n) return;
      var act = btn.getAttribute('data-act');
      var idx = parseInt(btn.getAttribute('data-idx'), 10);
      if (isNaN(idx)) return;
      commitEditorToCfg();
      if (act === 'delete') {
        n.productos.splice(idx, 1);
        renderProductos(n);
      } else if (act === 'calibrar') {
        runCalibrar(n, idx);
      } else if (act === 'autotune') {
        runAutotune(n, idx);
      } else if (act === 'caracterizar') {
        runCaracterizar(n, idx);
      }
    });
  }


  if (btnSaveCfg) btnSaveCfg.addEventListener('click', saveCfg);
  if (btnReload)  btnReload.addEventListener('click', async function () {
    await loadCfg();
    if (cfgEnabled) cfgEnabled.checked = !!(cfg && cfg.enabled);
    currentUid = null;
    renderNodoSelect();
    renderLanSelect();
  });

  var _origRenderNodos = renderNodos;
  renderNodos = function () { _origRenderNodos(); renderLanSelect(); };

  (async function init() {
    await loadCfg();
    await pollNodos();
    await pollLive();
    startPolling();

    if (cfgEnabled) cfgEnabled.checked = !!(cfg && cfg.enabled);
    renderNodoSelect();
    renderLanSelect();
  })();
})();
