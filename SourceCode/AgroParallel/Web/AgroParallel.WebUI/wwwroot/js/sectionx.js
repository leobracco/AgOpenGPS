// ============================================================================
// sectionx.js — editor de mapeo de surcos → secciones del piloto.
// El dropdown de nodos se arma desde /api/quantix/motores (los nodos físicos
// son los QuantiX). Para cada nodo seleccionado, sectionX.json guarda el
// mapeo surco→sección. Si el operario elige un QuantiX que aún no tiene
// entrada en sectionX.json, se crea una stub al vuelo (7 surcos por nodo).
// El estado live (qué sección está abierta) viene de /api/aog/state.
// ============================================================================

(function () {
  'use strict';

  var SURCOS_POR_NODO = 7;

  var liveEl    = document.getElementById('liveSections');
  var nodoSel   = document.getElementById('nodoSel');
  var nodoForm  = document.getElementById('nodoForm');
  var cablesEl  = document.getElementById('cablesGrid');
  var jsonOut   = document.getElementById('jsonOut');
  var btnSave   = document.getElementById('btnSave');
  var btnTestRelays = document.getElementById('btnTestRelays');
  var hintEl    = document.getElementById('hint');
  var enabledChk = document.getElementById('enabledChk');
  var statusChip = document.getElementById('sxStatusChip');
  var debugPanel = document.getElementById('sxDebugPanel');
  var debugPubEl = document.getElementById('sxDebugPublish');
  var debugLogEl = document.getElementById('sxDebugLog');
  var toastEl    = document.getElementById('sxToast');
  var toastTimer = null;

  // ---------------------------------------------------------------------------
  // Toast no-bloqueante. Reemplaza al `hintEl.textContent = '...'` viejo —
  // ese feedback era casi invisible (texto chico, mismo color que el subtítulo).
  // Tres "kinds": ok=verde, bad=rojo, warn=amarillo. Sin kind = neutro (oscuro).
  // ---------------------------------------------------------------------------
  function toast(msg, kind, ms) {
    if (!toastEl) return;
    toastEl.textContent = msg;
    toastEl.className = 'show ' + (kind || '');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.className = ''; }, ms || 2400);
  }

  var cfg = null;             // sectionX.json en memoria
  var quantixNodos = [];      // lista de QuantiX disponibles (uid/nombre)
  var selectedUid = null;     // uid actualmente seleccionado en el dropdown
  // Implemento central (Herramienta) — fuente única de verdad de trenes/surcos.
  // SectionX sólo lo lee para mostrar el resumen y poblar el dropdown "Tren".
  var implCentral = null;

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // Devuelve la entrada SxNodo de sectionX.json que matchea el uid seleccionado.
  // Si todavía no existe, la crea al vuelo con campos por defecto + 0 cables.
  function currentNode() {
    if (!cfg || !selectedUid) return null;
    if (!cfg.nodos) cfg.nodos = [];
    for (var i = 0; i < cfg.nodos.length; i++) {
      if ((cfg.nodos[i].uid || '').toLowerCase() === selectedUid.toLowerCase()) return cfg.nodos[i];
    }
    // Stub nuevo: copia nombre/distancia del QuantiX si existe
    var qx = findQuantixByUid(selectedUid);
    var stub = {
      uid: selectedUid,
      nombre: qx ? (qx.nombre || 'Nodo QuantiX') : 'Nodo QuantiX',
      habilitado: true,
      distanciaEntreTrenes: qx ? (qx.distancia_entre_trenes ?? qx.distanciaEntreTrenes ?? 0) : 0,
      cables: []
    };
    cfg.nodos.push(stub);
    return stub;
  }

  function findQuantixByUid(uid) {
    if (!uid) return null;
    for (var i = 0; i < quantixNodos.length; i++) {
      if ((quantixNodos[i].uid || '').toLowerCase() === uid.toLowerCase()) return quantixNodos[i];
    }
    return null;
  }

  function renderNodoSel() {
    if (!nodoSel) return;
    var opts = quantixNodos.map(function (n) {
      var label = (n.nombre || 'Nodo QuantiX') + ' · ' + (n.uid || '—');
      var sel = (selectedUid && (n.uid || '').toLowerCase() === selectedUid.toLowerCase()) ? ' selected' : '';
      return '<option value="' + escapeHtml(n.uid || '') + '"' + sel + '>' + escapeHtml(label) + '</option>';
    }).join('');
    if (!opts) opts = '<option value="">(no hay nodos QuantiX — configurá primero en /quantix)</option>';
    nodoSel.innerHTML = opts;
    if (enabledChk && cfg) enabledChk.checked = !!cfg.enabled;
  }

  function renderForm() {
    var n = currentNode();
    if (!nodoForm) return;
    if (!n) {
      nodoForm.innerHTML = '<p class="subtitle">Elegí un nodo QuantiX en el desplegable de arriba para mapearlo. Si la lista está vacía, primero hay que dar de alta el nodo desde la pantalla QuantiX.</p>';
      cablesEl.innerHTML = '';
      jsonOut.value = '';
      return;
    }
    var qx = findQuantixByUid(n.uid);
    var qxLabel = qx ? (qx.nombre || 'Nodo QuantiX') : '(QuantiX no encontrado — el uid existe en sectionX.json pero no en quantiX_motores.json)';
    nodoForm.innerHTML = '' +
      '<div style="display:grid; grid-template-columns: 1fr 1fr; gap: var(--agp-sp-4)">' +
        '<div class="field"><label>Nodo QuantiX</label><input type="text" value="' + escapeHtml(qxLabel) + '" disabled></div>' +
        '<div class="field"><label>UID</label><input type="text" value="' + escapeHtml(n.uid || '') + '" disabled></div>' +
        '<div class="field"><label>Alias para SectionX (opcional)</label><input data-name="nombre" type="text" value="' + escapeHtml(n.nombre || '') + '"></div>' +
        '<div class="field"><label>Distancia entre trenes (m)</label><input data-name="distancia_entre_trenes" type="number" step="0.05" value="' + (n.distanciaEntreTrenes ?? n.distancia_entre_trenes ?? 0) + '"></div>' +
        '<label class="switch" style="align-self:center"><input data-name="habilitado" type="checkbox" ' + (n.habilitado ? 'checked' : '') + '><span class="track"></span> Habilitado en SectionX</label>' +
      '</div>';

    renderCables();
    renderJson();
  }

  // Opciones del dropdown "Tren" para cada cable. Vienen del implemento central
  // (Herramienta). El value es el LOCAL del nodo QuantiX (0 = primer motor del
  // nodo, 1 = segundo) — el bridge espera 0/1. El label muestra el nombre del
  // tren del central para que el operario identifique de qué tren físico está
  // hablando. Si el central no se cargó, caemos a "Tren 1 / Tren 2".
  function trenOpts(currentLocal) {
    var labels;
    if (implCentral && Array.isArray(implCentral.trenes) && implCentral.trenes.length > 0) {
      labels = implCentral.trenes.slice(0, 2).map(function (t) {
        return t.nombre || ('Tren ' + t.id);
      });
    } else {
      labels = ['Tren 1', 'Tren 2'];
    }
    // Asegurar dos opciones aunque el central tenga 1 solo tren.
    while (labels.length < 2) labels.push('Tren ' + (labels.length + 1));
    var cur = currentLocal | 0;
    return '<option value="0"' + (cur === 0 ? ' selected' : '') + '>' + escapeHtml(labels[0]) + '</option>' +
           '<option value="1"' + (cur === 1 ? ' selected' : '') + '>' + escapeHtml(labels[1]) + '</option>';
  }

  function renderCables() {
    var n = currentNode();
    if (!cablesEl || !n) return;

    // Hasta 7 surcos por nodo QuantiX (1..SURCOS_POR_NODO).
    var byCable = {};
    (n.cables || []).forEach(function (c) { byCable[c.cable] = c; });

    var html = '<div class="cable-grid">';
    for (var i = 1; i <= SURCOS_POR_NODO; i++) {
      var c = byCable[i] || { cable: i, seccionAOG: 0, seccion_aog: 0, tren: 0 };
      var sec = c.seccionAOG ?? c.seccion_aog ?? 0;
      var on = sec > 0;
      html += '<div class="cable-cell' + (on ? ' on' : '') + '" data-cable="' + i + '">' +
                '<div class="cable-num">Surco ' + i + '</div>' +
                '<input type="number" min="0" max="16" data-cable-sec="' + i + '" value="' + sec + '" title="Sección PilotX (0 = no asignado)">' +
                '<select data-cable-tren="' + i + '" title="Tren">' + trenOpts(c.tren ?? 0) + '</select>' +
              '</div>';
    }
    html += '</div>';

    cablesEl.innerHTML = html;
  }

  function paintImplSummary() {
    var el = document.getElementById('sxImplSummary');
    if (!el) return;
    var c = implCentral || {};
    var nT = Array.isArray(c.trenes) ? c.trenes.length : 0;
    var nS = (c.numero_surcos | 0) || (Array.isArray(c.surcos) ? c.surcos.length : 0);
    var nSec = Array.isArray(c.secciones) ? c.secciones.length : 0;
    el.textContent = 'Trenes: ' + (nT || '–') + ' · Surcos: ' + (nS || '–') +
                     ' · Secciones PilotX: ' + (nSec || '–');
  }

  async function loadImplCentral() {
    try {
      var r = await fetch('/api/implemento', { cache: 'no-store' });
      var d = await r.json();
      implCentral = (d && d.ok) ? d.implemento : null;
    } catch (e) { implCentral = null; }
    paintImplSummary();
  }

  function collectFromUi() {
    var n = currentNode();
    if (!n) return;
    nodoForm.querySelectorAll('input[data-name],select[data-name]').forEach(function (el) {
      var k = el.getAttribute('data-name');
      if (el.type === 'checkbox') n[k === 'habilitado' ? 'habilitado' : k] = el.checked;
      else if (el.type === 'number') {
        var v = parseFloat(el.value);
        if (k === 'distancia_entre_trenes') n.distanciaEntreTrenes = isNaN(v) ? 0 : v;
        else n[k] = isNaN(v) ? 0 : v;
      } else {
        n[k] = el.value;
      }
    });

    // Surcos: reconstruir desde la grilla (1..SURCOS_POR_NODO)
    var cables = [];
    for (var i = 1; i <= SURCOS_POR_NODO; i++) {
      var secEl  = cablesEl.querySelector('[data-cable-sec="' + i + '"]');
      var trenEl = cablesEl.querySelector('[data-cable-tren="' + i + '"]');
      var sec = parseInt(secEl ? secEl.value : '0', 10) || 0;
      var tren = parseInt(trenEl ? trenEl.value : '0', 10) || 0;
      if (sec > 0) cables.push({ cable: i, seccionAOG: sec, tren: tren });
    }
    n.cables = cables;
    // Asegurar que el uid del entry siga apuntando al QuantiX seleccionado
    n.uid = selectedUid;
  }

  function renderJson() {
    if (!jsonOut) return;
    jsonOut.value = JSON.stringify(currentNode() || {}, null, 2);
  }

  function renderLive(snapshot) {
    if (!liveEl) return;
    var num = snapshot.numSections || 0;
    var on = snapshot.sectionOnRequest || [];
    if (num <= 0) {
      liveEl.innerHTML = '<div class="subtitle">No hay job activo.</div>';
      return;
    }
    var rows = '';
    for (var i = 0; i < num; i++) {
      var open = !!on[i];
      rows += '<div class="seg-row' + (open ? ' on' : '') + '">' +
                '<div class="name">Sec ' + (i + 1) + '</div>' +
                '<div class="state">' + (open ? 'ABIERTA' : 'CERRADA') + '</div>' +
              '</div>';
    }
    liveEl.innerHTML = rows;
  }

  async function loadQuantixNodos() {
    try {
      var res = await fetch('/api/quantix/motores', { cache: 'no-store' });
      var body = await res.json();
      // El endpoint serializa con System.Text.Json honrando JsonPropertyName → snake_case.
      // Shape esperada: { ok:true, config:{ nodos:[{uid,nombre,distancia_entre_trenes,...}], ... } }
      var nodos = (body && body.config && body.config.nodos) ? body.config.nodos : [];
      quantixNodos = nodos.filter(function (n) { return n && n.uid; });
    } catch (e) {
      quantixNodos = [];
    }
  }

  async function loadCfg() {
    try {
      await Promise.all([loadQuantixNodos(), loadImplCentral()]);
      var res = await fetch('/api/sectionx/config', { cache: 'no-store' });
      cfg = await res.json();
      if (!cfg.nodos) cfg.nodos = [];
      // Normalizar keys snake_case → camelCase para edición en JS
      cfg.nodos.forEach(function (n) {
        if (n.distancia_entre_trenes != null && n.distanciaEntreTrenes == null)
          n.distanciaEntreTrenes = n.distancia_entre_trenes;
        (n.cables || []).forEach(function (c) {
          if (c.seccion_aog != null && c.seccionAOG == null) c.seccionAOG = c.seccion_aog;
        });
      });
      // Selección inicial: primero QuantiX disponible; si no hay, queda null
      if (quantixNodos.length > 0) selectedUid = quantixNodos[0].uid;
      renderNodoSel();
      renderForm();
      if (quantixNodos.length === 0 && hintEl) {
        hintEl.textContent = 'No hay nodos QuantiX dados de alta. Configurá al menos uno en la pantalla QuantiX.';
      }
    } catch (e) {
      if (hintEl) hintEl.textContent = 'No se pudo cargar la config: ' + e.message;
    }
  }

  async function pollLive() {
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      var snap = await res.json();
      renderLive(snap);
    } catch (e) { /* offline */ }
  }

  // ----- eventos -----
  if (nodoSel) {
    nodoSel.addEventListener('change', function () {
      // Antes de cambiar, persistir en memoria los valores del nodo actual
      // para no perderlos al cambiar de selección sin guardar.
      try { collectFromUi(); } catch (_) {}
      selectedUid = nodoSel.value || null;
      renderForm();
    });
  }

  if (cablesEl) {
    cablesEl.addEventListener('input', function () {
      renderJson();
    });
  }
  if (nodoForm) {
    nodoForm.addEventListener('input', function () {
      renderJson();
    });
  }
  if (enabledChk) {
    enabledChk.addEventListener('change', function () {
      if (cfg) cfg.enabled = enabledChk.checked;
    });
  }

  // ---------------------------------------------------------------------------
  // Validación pre-guardado. Devuelve { blocking:[], warnings:[] }:
  //   * blocking: errores que impiden guardar (ej. cable duplicado — provocaría
  //     dos surcos manejando la misma sección, comportamiento ambiguo en bridge).
  //   * warnings: cosas raras pero válidas que requieren confirmación del operario
  //     (guardar sin nodos, nodo habilitado sin cables, etc.). Bug 2026-05-19:
  //     guardábamos vacío y nadie avisaba → operario creía que andaba.
  // ---------------------------------------------------------------------------
  function validateBeforeSave() {
    var blocking = [], warnings = [];
    var nodos = (cfg && cfg.nodos) || [];

    if (nodos.length === 0) {
      warnings.push('No hay nodos configurados. SectionX no controlará nada.');
    }

    nodos.forEach(function (n) {
      var label = n.nombre || n.uid || '(sin nombre)';
      var cables = n.cables || [];
      if (n.habilitado && cables.length === 0) {
        warnings.push('Nodo "' + label + '" está habilitado pero no tiene surcos asignados.');
      }
      // Duplicados de sección dentro del mismo nodo (ambiguo en bridge)
      var seenSec = {};
      cables.forEach(function (c) {
        var sec = c.seccionAOG ?? c.seccion_aog ?? 0;
        if (sec > 0) {
          if (seenSec[sec]) {
            blocking.push('Nodo "' + label + '": sección ' + sec + ' asignada a más de un surco.');
          }
          seenSec[sec] = true;
        }
      });
    });

    return { blocking: blocking, warnings: warnings };
  }

  // Convierte el cfg en memoria (camelCase) al wire-format snake_case esperado
  // por las DTOs C# (JsonPropertyName). Idéntico al payload viejo — extraído
  // para reutilizar desde test-relays y futuras llamadas.
  function buildPayload() {
    var payload = JSON.parse(JSON.stringify(cfg));
    (payload.nodos || []).forEach(function (n) {
      if (n.distanciaEntreTrenes != null) {
        n.distancia_entre_trenes = n.distanciaEntreTrenes;
        delete n.distanciaEntreTrenes;
      }
      (n.cables || []).forEach(function (c) {
        if (c.seccionAOG != null) {
          c.seccion_aog = c.seccionAOG;
          delete c.seccionAOG;
        }
      });
    });
    return payload;
  }

  if (btnSave) {
    btnSave.addEventListener('click', async function () {
      try {
        collectFromUi();
        // Validación previa al POST — bloqueantes nunca, warnings con confirm.
        var v = validateBeforeSave();
        if (v.blocking.length > 0) {
          toast('No se puede guardar: ' + v.blocking[0], 'bad', 4000);
          return;
        }
        if (v.warnings.length > 0) {
          var msg = '⚠️ ' + v.warnings.join('\n⚠️ ') + '\n\n¿Guardar igualmente?';
          if (!window.confirm(msg)) {
            toast('Guardado cancelado', 'warn');
            return;
          }
        }
        btnSave.disabled = true;
        hintEl.textContent = '';
        var payload = buildPayload();
        var res = await fetch('/api/sectionx/config', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
        var ok = (await res.json()).ok;
        if (ok) {
          toast('Config guardada · bridge actualizado', 'ok');
          // Refresh inmediato del chip para reflejar el nuevo estado del bridge.
          pollStatus();
        } else {
          toast('Error al guardar', 'bad');
        }
      } catch (e) {
        toast('Error: ' + e.message, 'bad', 4000);
      }
      btnSave.disabled = false;
    });
  }

  // ---------------------------------------------------------------------------
  // Test de relés: secuencia los cables del nodo seleccionado (1s c/u) para
  // que el operario los escuche clickear desde la cabina sin tener que mover
  // el tractor a velocidad >0.5 km/h. El bridge entra en "modo test" para ese
  // UID y deja de publicar /sections automático durante la secuencia.
  // ---------------------------------------------------------------------------
  if (btnTestRelays) {
    btnTestRelays.addEventListener('click', async function () {
      var n = currentNode();
      if (!n || !n.uid) {
        toast('Seleccioná un nodo primero', 'warn');
        return;
      }
      var cables = (n.cables || []).map(function (c) { return c.cable; }).filter(function (x) { return x > 0; });
      if (cables.length === 0) {
        toast('Este nodo no tiene surcos asignados', 'warn');
        return;
      }
      try {
        btnTestRelays.disabled = true;
        toast('🔧 Probando ' + cables.length + ' relé(s) en ' + (n.nombre || n.uid) + '…', null, 4000);
        var res = await fetch('/api/sectionx/test/' + encodeURIComponent(n.uid), {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cables: cables, stepMs: 1000 })
        });
        var body = await res.json();
        if (body && body.ok) {
          // El endpoint es fire-and-forget: el bridge corre la secuencia en
          // background. Damos tiempo y confirmamos al operario.
          setTimeout(function () {
            toast('Test completo · ¿escuchaste los relés clickear?', 'ok', 3500);
          }, cables.length * 1000 + 300);
        } else {
          toast('Error en test: ' + (body && body.error || 'desconocido'), 'bad', 4000);
        }
      } catch (e) {
        toast('Error en test: ' + e.message, 'bad', 4000);
      }
      btnTestRelays.disabled = false;
    });
  }

  // ---------------------------------------------------------------------------
  // Status chip: polling 1 Hz al endpoint /api/sectionx/status. Tres estados
  // visuales: 🟢 publicando · 🟡 conectado pero sin nodos / stale · 🔴 broker
  // caído. La idea es que el operario sepa con un vistazo si SectionX está
  // listo, sin tener que abrir el debug panel.
  // ---------------------------------------------------------------------------
  function setStatusChip(klass, label, title) {
    if (!statusChip) return;
    statusChip.className = 'sx-status ' + klass;
    var lblEl = statusChip.querySelector('.lbl');
    if (lblEl) lblEl.textContent = label;
    statusChip.title = title || label;
  }

  async function pollStatus() {
    if (!statusChip) return;
    try {
      var r = await fetch('/api/sectionx/status', { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      var s = await r.json();
      if (!s.connected) {
        setStatusChip('red', 'broker caído', 'No hay conexión con el broker MQTT. Revisar CoreX/AgIO.');
      } else if (!s.running || (s.nodoCount | 0) === 0) {
        setStatusChip('yellow', 'sin nodos', 'Bridge conectado pero no hay nodos configurados todavía.');
      } else if (s.lastPublishMsAgo != null && s.lastPublishMsAgo < 3000) {
        setStatusChip('green', 'publicando', 'Bridge publicando OK. Último mensaje hace ' +
          Math.round(s.lastPublishMsAgo / 100) / 10 + 's.');
      } else {
        setStatusChip('yellow', 'inactivo',
          'Bridge conectado y con nodos, pero no publica desde hace rato. ' +
          'Normal si el tractor está quieto (velocidad < 0.5 km/h) o no hay job activo.');
      }
    } catch (e) {
      setStatusChip('gray', 'sin datos', 'No se pudo consultar el estado del bridge: ' + e.message);
    }
  }

  // ---------------------------------------------------------------------------
  // Debug panel: muestra el último payload publicado por UID + tail del log.
  // Polling solo cuando el <details> está open (evita carga innecesaria).
  // ---------------------------------------------------------------------------
  function renderDebug(snap) {
    if (!debugPubEl || !debugLogEl) return;
    var last = (snap && snap.lastByNodo) || {};
    var keys = Object.keys(last);
    if (keys.length === 0) {
      debugPubEl.innerHTML = '<div class="subtitle">Bridge sin publicar todavía. ' +
        'Guardá una config válida o esperá a tener velocidad >0.5 km/h.</div>';
    } else {
      debugPubEl.innerHTML = keys.map(function (uid) {
        var e = last[uid] || {};
        var bits = Array.isArray(e.bits) ? '[' + e.bits.join(',') + ']' : '(sin datos)';
        var ago = (e.msAgo != null) ? (Math.round(e.msAgo / 100) / 10) + 's' : '?';
        return '<div class="debug-row">' +
                 '<div><strong>' + escapeHtml(uid) + '</strong> · hace ' + ago + '</div>' +
                 '<div style="color: var(--agp-text-muted)">→ ' + escapeHtml(e.topic || '') + '</div>' +
                 '<div>' + escapeHtml(bits) + '</div>' +
               '</div>';
      }).join('');
    }
    var lines = (snap && snap.logTail) || [];
    debugLogEl.textContent = lines.length === 0 ? '(log vacío)' : lines.join('\n');
  }

  async function pollDebug() {
    if (!debugPanel || !debugPanel.open) return;
    try {
      var r = await fetch('/api/sectionx/debug', { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      renderDebug(await r.json());
    } catch (e) {
      if (debugLogEl) debugLogEl.textContent = '(error: ' + e.message + ')';
    }
  }

  // Refresh al abrir el panel + arranque del polling si está abierto en boot.
  if (debugPanel) {
    debugPanel.addEventListener('toggle', function () { if (debugPanel.open) pollDebug(); });
  }

  loadCfg();
  pollLive();
  pollStatus();
  setInterval(pollLive, 1000);
  setInterval(pollStatus, 1000);
  setInterval(pollDebug, 500);  // 2 Hz; sale temprano si el panel está cerrado
})();
