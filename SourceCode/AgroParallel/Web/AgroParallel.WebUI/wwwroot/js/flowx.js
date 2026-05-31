// ============================================================================
// flowx.js — UI de FlowX (corte + dosis para pulverizadoras).
// Conecta con /api/flowx/config (CRUD) y /api/flowx/nodos (descubrimiento LAN).
// El estado live del aguilón (velocidad + secciones abiertas) viene de
// /api/aog/state — la lógica de escalado de caudal por secciones la hace
// el FlowXBridge en C#, acá sólo mostramos.
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
  var nodos = [];
  var live = null;

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function fmtNum(v, decimals) {
    if (v == null || isNaN(v)) return '—';
    return Number(v).toFixed(decimals || 0);
  }

  // El "primer" nodo habilitado del config es el de referencia para los KPIs.
  // FlowX típicamente tiene UN nodo por aguilón (una bomba central), así que
  // mostrar el primero alcanza para el preview.
  function activeNodo() {
    if (!cfg || !cfg.nodos) return null;
    for (var i = 0; i < cfg.nodos.length; i++) {
      if (cfg.nodos[i].habilitado) return cfg.nodos[i];
    }
    return cfg.nodos[0] || null;
  }

  // Telemetría live de la barra. Para FlowX la dosis efectiva depende de
  // cuántas secciones PilotX están abiertas — escalado proporcional, igual que
  // hace el FlowXBridge en C#.
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

    // Ancho activo = anchoBarra × (on / num). Asume picos equiespaciados.
    // Si el aguilón tiene anchos por sección distintos (caso raro en
    // pulverización), conviene usar snap.sectionPositions, pero hoy
    // alcanzamos con esta aproximación para el preview.
    var anchoTotal = nodo ? (nodo.ancho_barra_m || 0) : 0;
    var anchoActivo = (num > 0 && anchoTotal > 0) ? (anchoTotal * on / num) : anchoTotal;
    if (kpiAncho) kpiAncho.textContent = fmtNum(anchoTotal, 1) + ' m';

    // Dosis objetivo del primer producto del nodo activo.
    var dosis = 0;
    if (nodo && nodo.productos && nodo.productos.length > 0) {
      dosis = nodo.productos[0].dosis_lha || 0;
    }
    if (kpiDose) kpiDose.textContent = fmtNum(dosis, 0);

    // Target L/min = dosis × vel × ancho_activo / 600
    var targetLmin = (dosis > 0 && vel > 0 && anchoActivo > 0)
      ? (dosis * vel * anchoActivo / 600) : 0;
    if (kpiTarget) kpiTarget.textContent = fmtNum(targetLmin, 2) + ' L/min';

    // Caudal real + PWM + estado PID: vienen del FlowXLiveService.
    // Buscamos el nodo activo en el snapshot live por uid.
    var liveNodo = null;
    if (live && live.nodos && nodo) {
      for (var j = 0; j < live.nodos.length; j++) {
        if (live.nodos[j].uid === nodo.uid) { liveNodo = live.nodos[j]; break; }
      }
    }
    if (liveNodo && liveNodo.online) {
      if (kpiFlow) kpiFlow.textContent = fmtNum(liveNodo.caudal_lmin, 2);
      if (kpiPwm)  kpiPwm.textContent  = fmtNum(liveNodo.pwm, 0);
      if (kpiPid)  kpiPid.textContent  = liveNodo.pid_estado || 'ok';
    } else {
      if (kpiFlow) kpiFlow.textContent = '—';
      if (kpiPwm)  kpiPwm.textContent  = '—';
      if (kpiPid)  kpiPid.textContent  = nodo ? 'Esperando telemetría' : 'Sin config';
    }

    // Insumo ahorrado por corte automático.
    // PilotX provee área trabajada total (con solapamiento) y área neta cubierta.
    // El "repintado" = total - neta. Si no hubiera corte automático, esa área
    // se hubiera vuelto a pulverizar → litros ahorrados = repintado_ha × dosis.
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
      return '<div class="row" style="padding: var(--agp-sp-2) 0">' +
              '<span style="color:' + color + '">' + dot + '</span> ' +
              '<strong style="color:var(--agp-text); margin-left:8px">' + escapeHtml(n.uid || '—') + '</strong>' +
              '<span class="subtitle" style="margin-left:8px">' + escapeHtml(n.ip || '') + ' · fw ' + escapeHtml(n.firmware || '?') + '</span>' +
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
    // Pedimos estado PilotX (velocidad/secciones/áreas) y telemetría FlowX
    // en paralelo. Si una falla, igual renderizamos con lo que haya.
    try {
      var results = await Promise.all([
        fetch('/api/aog/state', { cache: 'no-store' }).then(function (r) { return r.json(); }).catch(function () { return null; }),
        fetch('/api/flowx/live', { cache: 'no-store' }).then(function (r) { return r.json(); }).catch(function () { return null; })
      ]);
      var snap = results[0];
      live = results[1];
      if (snap) renderLive(snap);
    } catch (e) { /* offline */ }
  }

  // Pausar polling cuando la pestaña no está visible (igual patrón que camaras.js).
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
  var tblProductos  = document.getElementById('tblProductos');
  var tblCables     = document.getElementById('tblCables');
  var btnAddProducto = document.getElementById('btnAddProducto');
  var btnAddCable   = document.getElementById('btnAddCable');
  var btnSaveCfg    = document.getElementById('btnSaveCfg');
  var btnReload     = document.getElementById('btnReload');
  var saveStatus    = document.getElementById('saveStatus');

  var currentUid = null;  // uid del nodo seleccionado en el editor

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
    renderProductos(n);
    renderCables(n);
  }

  function renderProductos(n) {
    if (!tblProductos) return;
    var tbody = tblProductos.querySelector('tbody');
    tbody.innerHTML = '';
    (n.productos || []).forEach(function (p, idx) {
      var tr = document.createElement('tr');
      tr.innerHTML =
        '<td><input type="number" min="0" step="1" data-k="id"        value="' + (p.id || 0) + '" style="width:60px"></td>' +
        '<td><input type="text"                   data-k="nombre"    value="' + escapeHtml(p.nombre || '') + '" style="width:140px"></td>' +
        '<td><input type="number" step="0.1"      data-k="meter_cal" value="' + (p.meter_cal || 0) + '" style="width:100px"></td>' +
        // dosis_lha y Kp/Ki/Kd usan paso adaptativo — el atributo step se
        // recalcula sobre cada input vía AGPSteps.attachAdaptive() abajo.
        '<td><input type="number" data-adaptive="dose" data-k="dosis_lha" value="' + (p.dosis_lha || 0) + '" style="width:90px"></td>' +
        '<td><input type="number" step="1" min="0" max="100" data-k="pwm_min"   value="' + (p.pwm_min || 0) + '" style="width:70px"></td>' +
        '<td><input type="number" data-adaptive="pid"  data-k="kp"        value="' + (p.kp || 0) + '" style="width:70px"></td>' +
        '<td><input type="number" data-adaptive="pid"  data-k="ki"        value="' + (p.ki || 0) + '" style="width:70px"></td>' +
        '<td><input type="number" data-adaptive="pid"  data-k="kd"        value="' + (p.kd || 0) + '" style="width:70px"></td>' +
        '<td style="white-space:nowrap;">' +
          '<button type="button" class="btn" data-act="calibrar" data-idx="' + idx + '" title="Calibrar caudalímetro">Calibrar</button> ' +
          '<button type="button" class="btn" data-act="autotune" data-idx="' + idx + '" title="Auto-tune PID">Auto-tune</button> ' +
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

  function renderCables(n) {
    if (!tblCables) return;
    var tbody = tblCables.querySelector('tbody');
    tbody.innerHTML = '';
    (n.cables || []).forEach(function (c, idx) {
      var tr = document.createElement('tr');
      tr.innerHTML =
        '<td><input type="number" min="0" step="1" data-k="cable"        value="' + (c.cable || 0) + '" style="width:90px"></td>' +
        '<td><input type="number" min="0" step="1" data-k="seccion_aog"  value="' + (c.seccion_aog || 0) + '" style="width:90px"></td>' +
        '<td><button type="button" class="btn" data-act="delete-cable" data-idx="' + idx + '" style="color:var(--agp-state-bad)">×</button></td>';
      tbody.appendChild(tr);
    });
  }

  // Lee el editor de vuelta al objeto cfg (sin guardar).
  function commitEditorToCfg() {
    var n = findNodo(currentUid);
    if (!n) return;
    n.nombre        = nodoNombre ? nodoNombre.value : n.nombre;
    n.ancho_barra_m = nodoAncho  ? parseFloat(nodoAncho.value) || 0 : n.ancho_barra_m;
    n.habilitado    = !!(nodoHab && nodoHab.checked);
    n.is_3wire      = !!(nodo3wire && nodo3wire.checked);
    n.invert_relay  = !!(nodoInv && nodoInv.checked);

    // Productos
    var prods = [];
    if (tblProductos) {
      var rows = tblProductos.querySelectorAll('tbody tr');
      rows.forEach(function (tr) {
        var inputs = tr.querySelectorAll('input[data-k]');
        var p = { id: 0, nombre: '', meter_cal: 0, dosis_lha: 0, pwm_min: 0, kp: 0, ki: 0, kd: 0 };
        inputs.forEach(function (inp) {
          var k = inp.getAttribute('data-k');
          if (k === 'nombre') p[k] = inp.value;
          else if (k === 'id' || k === 'pwm_min') p[k] = parseInt(inp.value, 10) || 0;
          else p[k] = parseFloat(inp.value) || 0;
        });
        prods.push(p);
      });
    }
    n.productos = prods;

    // Cables
    var cables = [];
    if (tblCables) {
      var rows2 = tblCables.querySelectorAll('tbody tr');
      rows2.forEach(function (tr) {
        var inputs = tr.querySelectorAll('input[data-k]');
        var c = { cable: 0, seccion_aog: 0 };
        inputs.forEach(function (inp) {
          var k = inp.getAttribute('data-k');
          c[k] = parseInt(inp.value, 10) || 0;
        });
        cables.push(c);
      });
    }
    n.cables = cables;

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
  // Calibración y Auto-tune (vía MQTT cmd → resultado pooled del live svc).
  // Endpoints C#:
  //   POST /api/flowx/{uid}/cmd?verb={calibrar_start|calibrar_stop|autotune_start|autotune_stop}
  //   GET  /api/flowx/{uid}/calibrar   → { ok, hasResult, result: {pulsos, ok, error...} }
  //   GET  /api/flowx/{uid}/autotune   → { ok, hasResult, result: {kp, ki, kd, ok, error...} }
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

  function pollResult(uid, kind, timeoutMs) {
    // Pollea hasta que el firmware reporte resultado o se agote el timeout.
    // kind: 'calibrar' | 'autotune'
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
    var volStr = window.prompt(
      'Calibración del caudalímetro\n\n' +
      'Vamos a contar pulsos del flowmeter mientras pasa un volumen conocido.\n' +
      'Ingrese el volumen exacto que va a verter (litros):',
      '1'
    );
    if (!volStr) return;
    var vol = parseFloat(volStr);
    if (!isFinite(vol) || vol <= 0) { window.alert('Volumen inválido.'); return; }

    try {
      var startRes = await sendCmd(n.uid, 'calibrar_start', { producto_id: p.id, vol_l: vol });
      if (!startRes || !startRes.ok) {
        window.alert('No se pudo iniciar la calibración: ' + ((startRes && startRes.error) || 'fallo MQTT'));
        return;
      }
      var ok = window.confirm(
        'Calibración iniciada en producto "' + (p.nombre || p.id) + '".\n\n' +
        'Vierta exactamente ' + vol + ' L por el caudalímetro.\n\n' +
        'Cuando termine, pulse Aceptar para detener y leer pulsos.\n' +
        'Cancelar = abortar.'
      );
      if (!ok) {
        await sendCmd(n.uid, 'calibrar_stop', { producto_id: p.id, abort: true });
        return;
      }
      await sendCmd(n.uid, 'calibrar_stop', { producto_id: p.id });
      var r = await pollResult(n.uid, 'calibrar', 8000);
      if (!r.ok) {
        window.alert('El nodo devolvió error: ' + (r.error || 'desconocido'));
        return;
      }
      var pulsos = Number(r.pulsos) || 0;
      if (pulsos <= 0) {
        window.alert('No se detectaron pulsos. Revise cableado y reintente.');
        return;
      }
      var meterCal = pulsos / vol;
      var apply = window.confirm(
        'Resultado:\n' +
        '  · Pulsos: ' + pulsos + '\n' +
        '  · Volumen: ' + vol + ' L\n' +
        '  · meter_cal calculado: ' + meterCal.toFixed(2) + ' pulsos/L\n\n' +
        '¿Aplicar al producto "' + (p.nombre || p.id) + '"?'
      );
      if (apply) {
        p.meter_cal = Math.round(meterCal * 100) / 100;
        renderProductos(n);
      }
    } catch (e) {
      window.alert('Calibración fallida: ' + (e.message || e));
    }
  }

  async function runAutotune(n, prodIdx) {
    var p = n.productos[prodIdx];
    var ok = window.confirm(
      'Auto-tune PID\n\n' +
      'El firmware aplicará escalones al motor de la bomba durante ~30 s.\n' +
      'Asegúrese de que el sistema esté presurizado y la barra cerrada.\n\n' +
      'Producto: ' + (p.nombre || p.id) + '\n' +
      '¿Continuar?'
    );
    if (!ok) return;
    try {
      var startRes = await sendCmd(n.uid, 'autotune_start', { producto_id: p.id });
      if (!startRes || !startRes.ok) {
        window.alert('No se pudo iniciar auto-tune: ' + ((startRes && startRes.error) || 'fallo MQTT'));
        return;
      }
      var r = await pollResult(n.uid, 'autotune', 45000);
      if (!r.ok) {
        window.alert('Auto-tune falló: ' + (r.error || 'sin oscilación'));
        return;
      }
      var apply = window.confirm(
        'Auto-tune OK\n\n' +
        '  · Kp = ' + (Number(r.kp) || 0).toFixed(3) + '\n' +
        '  · Ki = ' + (Number(r.ki) || 0).toFixed(3) + '\n' +
        '  · Kd = ' + (Number(r.kd) || 0).toFixed(3) + '\n' +
        (r.ku ? '  · Ku = ' + (Number(r.ku) || 0).toFixed(3) + '\n' : '') +
        (r.tu_ms ? '  · Tu = ' + (Number(r.tu_ms) || 0).toFixed(0) + ' ms\n' : '') +
        '\n¿Aplicar al producto "' + (p.nombre || p.id) + '"?'
      );
      if (apply) {
        p.kp = Number(r.kp) || 0;
        p.ki = Number(r.ki) || 0;
        p.kd = Number(r.kd) || 0;
        renderProductos(n);
      }
    } catch (e) {
      window.alert('Auto-tune fallido: ' + (e.message || e));
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
      cfg.nodos.push({
        uid: uid,
        nombre: (lanInfo && lanInfo.nombre) || 'Nodo FlowX',
        habilitado: true,
        ancho_barra_m: 0,
        is_3wire: false,
        invert_relay: false,
        productos: [{ id: 0, nombre: 'Producto', meter_cal: 100, dosis_lha: 100, pwm_min: 40, kp: 1, ki: 0.1, kd: 0 }],
        cables: []
      });
      currentUid = uid;
      renderNodoSelect();
      renderLanSelect();
    });
  }

  // btnAddNodoManual eliminado del HTML — los nodos solo entran por
  // auto-descubrimiento MQTT (`agp/flowx/+/announcement` → registry LAN),
  // y desde acá se importan con btnImportNodo. Pedir UID a mano causaba typos.

  if (btnDeleteNodo) {
    btnDeleteNodo.addEventListener('click', function () {
      if (!cfg || !currentUid) return;
      if (!window.confirm('¿Eliminar nodo ' + currentUid + ' de la configuración?')) return;
      cfg.nodos = (cfg.nodos || []).filter(function (n) { return n.uid !== currentUid; });
      currentUid = null;
      renderNodoSelect();
      renderLanSelect();
    });
  }

  if (btnAddProducto) {
    btnAddProducto.addEventListener('click', function () {
      var n = findNodo(currentUid);
      if (!n) return;
      commitEditorToCfg();
      n.productos = n.productos || [];
      var nextId = 0;
      n.productos.forEach(function (p) { if (p.id >= nextId) nextId = p.id + 1; });
      n.productos.push({ id: nextId, nombre: 'Producto ' + (n.productos.length + 1), meter_cal: 100, dosis_lha: 100, pwm_min: 40, kp: 1, ki: 0.1, kd: 0 });
      renderProductos(n);
    });
  }

  if (btnAddCable) {
    btnAddCable.addEventListener('click', function () {
      var n = findNodo(currentUid);
      if (!n) return;
      commitEditorToCfg();
      n.cables = n.cables || [];
      n.cables.push({ cable: n.cables.length, seccion_aog: n.cables.length + 1 });
      renderCables(n);
    });
  }

  // Acciones por fila (eliminar / calibrar / autotune)
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
      }
    });
  }

  if (tblCables) {
    tblCables.addEventListener('click', function (e) {
      var btn = e.target.closest && e.target.closest('button[data-act="delete-cable"]');
      if (!btn) return;
      var n = findNodo(currentUid);
      if (!n) return;
      var idx = parseInt(btn.getAttribute('data-idx'), 10);
      if (isNaN(idx)) return;
      commitEditorToCfg();
      n.cables.splice(idx, 1);
      renderCables(n);
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

  // Cuando lleguen nodos LAN nuevos, refrescamos el selector de import.
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
