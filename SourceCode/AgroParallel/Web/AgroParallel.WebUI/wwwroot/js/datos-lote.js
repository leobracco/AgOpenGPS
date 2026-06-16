// ============================================================================
// datos-lote.js
// Reemplazo HTML/táctil de la ventana FormFieldData de PilotX.
// Muestra KPIs del lote + calcula INSUMO AHORRADO por corte automático.
//
// Datos:
//   /api/aog/state    → estado live (velocidad, secciones, áreas trabajadas,
//                       ancho herramienta, vehículo, lote en curso, etc.)
//   /api/quantix/motores → nodos[].motores[].dosis_fija (precarga modo kg).
//   /api/flowx/config   → dosis_lha del primer nodo/producto (precarga modo L).
//
// Cálculo central:
//   overlap_m2  = max(0, workedAreaTotalM2 - actualAreaCoveredM2)
//   overlap_ha  = overlap_m2 / 10000
//   insumoAhorrado = overlap_ha × dosis_aplicada
//   dineroAhorrado = insumoAhorrado × precio (opcional, vacío si no se carga)
// ============================================================================

(function () {
  'use strict';

  // ---- refs UI ----
  var pillEl       = document.getElementById('estadoPill');
  var pillText     = document.getElementById('estadoText');
  var kpiAreaLindero = document.getElementById('kpiAreaLindero');
  var kpiAreaNeta  = document.getElementById('kpiAreaNeta');
  var kpiAreaTotal = document.getElementById('kpiAreaTotal');
  var kpiAreaOver  = document.getElementById('kpiAreaOverlap');
  var kpiOverlapPct= document.getElementById('kpiOverlapPct');
  var kpiVel       = document.getElementById('kpiVel');
  var kpiSec       = document.getElementById('kpiSec');

  var doseToggle   = document.getElementById('doseToggle');
  var doseValue    = document.getElementById('doseValue');
  var doseUnit     = document.getElementById('doseUnit');
  var doseSource   = document.getElementById('doseSource');
  var priceValue   = document.getElementById('priceValue');
  var priceUnit    = document.getElementById('priceUnit');

  var kpiSavedInsumo   = document.getElementById('kpiSavedInsumo');
  var kpiSavedUnit     = document.getElementById('kpiSavedUnit');
  var kpiSavedMoney    = document.getElementById('kpiSavedMoney');
  var kpiSavedCurrency = document.getElementById('kpiSavedCurrency');
  var kpiSavedHa       = document.getElementById('kpiSavedHa');

  var detLote     = document.getElementById('detLote');
  var detAncho    = document.getElementById('detAncho');
  var detVehiculo = document.getElementById('detVehiculo');
  var detTrack    = document.getElementById('detTrack');
  var detShape    = document.getElementById('detShape');

  // ---- estado local ----
  // mode: 'kg' (semilla/fertilizante) | 'l' (líquido)
  // dosePrefill: lo que vino de QuantiX/FlowX; sirve para repoblar al togglear
  //   sin pisar lo que el operario haya tocado manualmente.
  var mode = 'kg';
  var userTouchedDose = false;
  var userTouchedPrice = false;
  var prefill = { kg: null, l: null };
  var lastSnap = null;

  function fmtNum(v, dec) {
    if (v == null || isNaN(v)) return '—';
    return Number(v).toFixed(dec || 0);
  }

  // /api/aog/state serializa con los nombres PascalCase de las props C#
  // (sin CamelCasePolicy). Normalizamos acá a camelCase para que renderSnap()
  // /recompute() no tengan que conocer la convención del backend. Sin esto los
  // KPIs leían undefined y quedaban todos en cero. Mismo patrón que flowx.js.
  function normalizeSnap(raw) {
    if (!raw) return null;
    function pick(o, p, c) { return (o[p] != null) ? o[p] : o[c]; }
    var track = pick(raw, 'ActiveTrack', 'activeTrack');
    return {
      isJobStarted:        !!pick(raw, 'IsJobStarted', 'isJobStarted'),
      avgSpeed:            Number(pick(raw, 'AvgSpeed', 'avgSpeed') || 0),
      numSections:         Number(pick(raw, 'NumSections', 'numSections') || 0),
      sectionOnRequest:    pick(raw, 'SectionOnRequest', 'sectionOnRequest') || [],
      workedAreaTotalM2:   Number(pick(raw, 'WorkedAreaTotalM2', 'workedAreaTotalM2') || 0),
      actualAreaCoveredM2: Number(pick(raw, 'ActualAreaCoveredM2', 'actualAreaCoveredM2') || 0),
      boundaryAreaM2:      Number(pick(raw, 'BoundaryAreaM2', 'boundaryAreaM2') || 0),
      toolWidth:           Number(pick(raw, 'ToolWidth', 'toolWidth') || 0),
      currentFieldDirectory: pick(raw, 'CurrentFieldDirectory', 'currentFieldDirectory'),
      vehicleBrand:        pick(raw, 'VehicleBrand', 'vehicleBrand'),
      vehicleType:         pick(raw, 'VehicleType', 'vehicleType'),
      shapeIsInside:       !!pick(raw, 'ShapeIsInside', 'shapeIsInside'),
      shapeCurrentDose:    Number(pick(raw, 'ShapeCurrentDose', 'shapeCurrentDose') || 0),
      activeTrack: track ? {
        name: pick(track, 'Name', 'name'),
        mode: pick(track, 'Mode', 'mode')
      } : null
    };
  }

  function setDoseUnits() {
    if (mode === 'kg') {
      doseUnit.textContent  = 'kg/ha';
      priceUnit.textContent = 'USD/kg';
      kpiSavedUnit.textContent = 'kg';
    } else {
      doseUnit.textContent  = 'L/ha';
      priceUnit.textContent = 'USD/L';
      kpiSavedUnit.textContent = 'L';
    }
    // si el operario no tocó el campo, poblar con el prefill del modo activo
    if (!userTouchedDose) {
      var pre = prefill[mode];
      doseValue.value = (pre != null && !isNaN(pre)) ? pre : 0;
      if (pre != null) {
        doseSource.textContent = mode === 'kg'
          ? 'Tomado de QuantiX (' + fmtNum(pre, 1) + ' kg/ha). Editá si querés.'
          : 'Tomado de FlowX (' + fmtNum(pre, 1) + ' L/ha). Editá si querés.';
      } else {
        doseSource.textContent = 'Sin config previa — ingresá la dosis.';
      }
    }
    recompute();
  }

  function setMode(m) {
    if (m !== 'kg' && m !== 'l') return;
    mode = m;
    var btns = doseToggle.querySelectorAll('button');
    for (var i = 0; i < btns.length; i++) {
      var act = btns[i].getAttribute('data-mode') === m;
      btns[i].classList.toggle('active', act);
    }
    setDoseUnits();
  }

  // ---- carga inicial de configs (dosis precargada) ----
  async function loadConfigs() {
    // QuantiX: la dosis por motor vive en motores[].dosis_fija (snake_case
    // explícito vía [JsonPropertyName]; ver QuantiXMotoresConfig.cs).
    // Si hay varios motores y el operario configuró distintos, tomamos el
    // primero > 0 — es solo la precarga, después puede editarla a mano.
    try {
      var r = await fetch('/api/quantix/motores', { cache: 'no-store' });
      if (r.ok) {
        var qx = await r.json();
        // MotoresConfig: { nodos: [{ motores: [{ dosis_fija }] }] }
        var dose = null;
        var lista = (qx && qx.nodos) ? qx.nodos : [];
        for (var n = 0; n < lista.length && dose == null; n++) {
          var ms = lista[n].motores || [];
          for (var i = 0; i < ms.length; i++) {
            var d = ms[i].dosis_fija;
            if (typeof d === 'number' && d > 0) { dose = d; break; }
          }
        }
        prefill.kg = dose;
      }
    } catch (e) { /* offline */ }

    // FlowX: nodos[].productos[].dosis_lha. Primer producto del primer nodo
    // habilitado — alcanza para precarga (pulverización es 1 producto por aguilón).
    try {
      var r2 = await fetch('/api/flowx/config', { cache: 'no-store' });
      if (r2.ok) {
        var fx = await r2.json();
        var l = null;
        if (fx && fx.nodos && fx.nodos.length > 0) {
          for (var k = 0; k < fx.nodos.length; k++) {
            var n = fx.nodos[k];
            if (n && n.habilitado && n.productos && n.productos.length > 0) {
              if (typeof n.productos[0].dosis_lha === 'number' && n.productos[0].dosis_lha > 0) {
                l = n.productos[0].dosis_lha;
                break;
              }
            }
          }
        }
        prefill.l = l;
      }
    } catch (e) { /* offline */ }

    setDoseUnits();
  }

  // ---- recalcula ahorro con la última snap + lo que esté en los inputs ----
  function recompute() {
    var snap = lastSnap;
    if (!snap) {
      kpiSavedInsumo.textContent = '—';
      kpiSavedMoney.textContent  = '—';
      kpiSavedHa.textContent     = '—';
      return;
    }
    var worked = snap.workedAreaTotalM2 || 0;
    var actual = snap.actualAreaCoveredM2 || 0;
    var overlap_m2 = Math.max(0, worked - actual);
    var overlap_ha = overlap_m2 / 10000;

    var dose = parseFloat(doseValue.value);
    if (isNaN(dose) || dose < 0) dose = 0;

    var price = parseFloat(priceValue.value);
    var hasPrice = !isNaN(price) && price >= 0;

    var insumo = overlap_ha * dose;
    kpiSavedInsumo.textContent = fmtNum(insumo, 1);
    kpiSavedHa.textContent     = fmtNum(overlap_ha, 2);

    if (hasPrice) {
      kpiSavedMoney.textContent    = fmtNum(insumo * price, 2);
      kpiSavedCurrency.textContent = 'USD';
    } else {
      kpiSavedMoney.textContent    = '—';
      kpiSavedCurrency.textContent = 'USD';
    }
  }

  // ---- render del snapshot PilotX ----
  function renderSnap(snap) {
    lastSnap = snap;
    if (!snap) return;

    // KPIs área
    var worked = snap.workedAreaTotalM2 || 0;
    var actual = snap.actualAreaCoveredM2 || 0;
    var overlap = Math.max(0, worked - actual);
    var overlapPct = worked > 0 ? (overlap / worked * 100) : 0;

    var lindero = snap.boundaryAreaM2 || 0;
    kpiAreaLindero.textContent = lindero > 0 ? fmtNum(lindero / 10000, 2) : '—';
    kpiAreaNeta.textContent  = fmtNum(actual / 10000, 2);
    kpiAreaTotal.textContent = fmtNum(worked / 10000, 2);
    kpiAreaOver.textContent  = fmtNum(overlap / 10000, 2);
    kpiOverlapPct.textContent = fmtNum(overlapPct, 1) + ' %';

    // Velocidad + secciones
    kpiVel.textContent = fmtNum(snap.avgSpeed || 0, 1);
    var num = snap.numSections || 0;
    var on = 0;
    var arr = snap.sectionOnRequest || [];
    for (var i = 0; i < num; i++) if (arr[i]) on++;
    kpiSec.textContent = on + ' de ' + num;

    // Estado del trabajo (pill arriba)
    if (snap.isJobStarted) {
      pillEl.className = 'pill ok';
      pillText.textContent = 'Trabajo en curso';
    } else {
      pillEl.className = 'pill idle';
      pillText.textContent = 'Sin trabajo';
    }

    // Detalles
    detLote.textContent = snap.currentFieldDirectory || '—';
    detAncho.textContent = (snap.toolWidth ? fmtNum(snap.toolWidth, 2) + ' m' : '— m');
    detVehiculo.textContent = (snap.vehicleBrand ? (snap.vehicleBrand + ' · ') : '')
                            + (snap.vehicleType || '—');
    if (snap.activeTrack && snap.activeTrack.name) {
      detTrack.textContent = snap.activeTrack.name +
        (snap.activeTrack.mode ? ' (' + snap.activeTrack.mode + ')' : '');
    } else {
      detTrack.textContent = 'Ninguna';
    }
    if (snap.shapeIsInside) {
      detShape.textContent = fmtNum(snap.shapeCurrentDose, 1) + ' (shapefile)';
    } else {
      detShape.textContent = 'Fuera de polígono';
    }

    recompute();
  }

  async function pollState() {
    try {
      var r = await fetch('/api/aog/state', { cache: 'no-store' });
      if (!r.ok) return;
      var raw = await r.json();
      renderSnap(normalizeSnap(raw));
    } catch (e) { /* offline */ }
  }

  // ---- pausar polling cuando la pestaña no es visible (patrón flowx.js) ----
  var pollHandle = null;
  function startPolling() {
    if (!pollHandle) pollHandle = setInterval(pollState, 1000);
  }
  function stopPolling() {
    if (pollHandle) { clearInterval(pollHandle); pollHandle = null; }
  }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stopPolling();
    else { pollState(); startPolling(); }
  });

  // ---- eventos UI ----
  doseToggle.addEventListener('click', function (e) {
    var t = e.target;
    if (!t || t.tagName !== 'BUTTON') return;
    var m = t.getAttribute('data-mode');
    if (!m) return;
    // Al togglear modo, reseteamos el "user touched" porque el operario
    // típicamente cambia de modo justamente para pasar de kg a L o viceversa;
    // queremos que la nueva precarga aparezca.
    userTouchedDose = false;
    userTouchedPrice = false;
    priceValue.value = '';
    setMode(m);
  });

  // Marcar "tocado por el operario" solo si el evento es por input real,
  // no por nuestro setDoseUnits().
  doseValue.addEventListener('input', function () { userTouchedDose = true; recompute(); });
  priceValue.addEventListener('input', function () { userTouchedPrice = true; recompute(); });

  // ---- exportar VistaX ZIP ----
  // El endpoint /api/lotes/vistax-zip zippea <Field>/VistaX/* y lo devuelve
  // como application/zip con Content-Disposition. WebView2 lo dispara como
  // descarga normal del SO; el operario ve el archivo en su carpeta de
  // descargas y puede pasarlo al agrónomo por USB/mail.
  var btnExp = document.getElementById('btnExportVistaXZip');
  var statusExp = document.getElementById('exportVxStatus');
  if (btnExp) {
    btnExp.addEventListener('click', async function () {
      btnExp.disabled = true;
      if (statusExp) statusExp.textContent = 'Preparando ZIP...';
      try {
        var resp = await fetch('/api/lotes/vistax-zip');
        if (!resp.ok) {
          var msg = 'sin sesiones VistaX en este lote';
          try { var j = await resp.json(); if (j && j.error) msg = j.error; } catch (_) {}
          if (statusExp) statusExp.textContent = '✕ ' + msg;
          return;
        }
        var blob = await resp.blob();
        var cd = resp.headers.get('Content-Disposition') || '';
        var m = cd.match(/filename="?([^";]+)"?/);
        var fname = (m && m[1]) || ('vistax_' + Date.now() + '.zip');
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url; a.download = fname;
        document.body.appendChild(a); a.click();
        setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 500);
        if (statusExp) statusExp.textContent = '✓ Descargado ' + fname;
      } catch (e) {
        if (statusExp) statusExp.textContent = '✕ Error: ' + (e.message || e);
      } finally {
        btnExp.disabled = false;
      }
    });
  }

  // ---- arranque ----
  (async function init() {
    setMode('kg');     // arranca en kg (la mayoría son siembra/fertilización)
    await loadConfigs();
    await pollState();
    startPolling();
  })();
})();
