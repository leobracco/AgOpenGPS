// ============================================================================
// calculadora-siembra.js — Cuentas de siembra de gruesa.
//
// Sigue el método del documento "Cálculos distanciamiento entre semillas de
// gruesa" (evaluación de siembra de precisión a campo, INTA):
//
//   metros lineales de surco por ha = 10.000 m² / e
//   densidad (sem/ha)               = N (sem por metro lineal) × 10.000 / e
//   distanciamiento Dref (cm)       = 100 / N
//   falla        → separación > 1,5 Dref   (doble: entre 2,5 y 3,5 Dref)
//   duplicación  → separación < 0,5 Dref
//   eventos corregidos = totales + fallas + fallas dobles × 2 − duplicaciones
//   densidad corregida = eventos corregidos × (metros por ha / metros medidos)
//   población en tolerancia = eventos entre 0,75 y 1,25 Dref sobre las bien
//                             sembradas (las que caen entre 0,5 y 1,5 Dref)
//
// Los valores de entrada se precargan del implemento activo y de los motores
// QuantiX configurados, así la cuenta arranca con la máquina real y no con
// números de ejemplo.
// ============================================================================
(function () {
  'use strict';

  var el = function (id) { return document.getElementById(id); };
  var num = function (id, def) {
    var v = parseFloat((el(id) || {}).value);
    return isFinite(v) ? v : (def === undefined ? 0 : def);
  };
  var fmt = function (v, dec) {
    if (!isFinite(v) || v <= 0) return '—';
    return v.toFixed(dec === undefined ? 1 : dec);
  };
  var miles = function (v) {
    if (!isFinite(v) || v <= 0) return '—';
    return Math.round(v).toLocaleString('es-AR');
  };

  var state = { motores: [], impl: null };

  // ── Tabs ──────────────────────────────────────────────────────────────────

  var SECCIONES = { densidad: 'csDensidad', campo: 'csCampo', pms: 'csPms', motor: 'csMotor' };

  document.querySelectorAll('[data-cs-tab]').forEach(function (b) {
    b.addEventListener('click', function () {
      var t = this.getAttribute('data-cs-tab');
      document.querySelectorAll('[data-cs-tab]').forEach(function (o) {
        o.className = 'btn' + (o.getAttribute('data-cs-tab') === t ? ' primary' : '');
      });
      Object.keys(SECCIONES).forEach(function (k) {
        var s = el(SECCIONES[k]);
        if (s) s.style.display = (k === t) ? '' : 'none';
      });
    });
  });

  // ── 1. Densidad ↔ sem/m ↔ distanciamiento ────────────────────────────────
  //
  // Tres formas de decir lo mismo. Guardamos cuál fue el último campo tocado y
  // ese manda; los otros dos se recalculan. Sin esto, escribir en uno pisaría
  // lo que el operario acaba de escribir en otro.

  var mandaDensidad = 'dens';

  function calcDensidad() {
    var e = num('dEsp', 0);
    if (!(e > 0)) return;
    var metrosHa = 10000 / e;
    var semM;

    if (mandaDensidad === 'dens') {
      semM = num('dDens', 0) / metrosHa;
    } else if (mandaDensidad === 'semm') {
      semM = num('dSemM', 0);
    } else {
      var dist = num('dDist', 0);
      semM = dist > 0 ? (100 / dist) : 0;
    }

    var dens = semM * metrosHa;
    var dref = semM > 0 ? (100 / semM) : 0;

    if (mandaDensidad !== 'dens') el('dDens').value = dens > 0 ? Math.round(dens) : '';
    if (mandaDensidad !== 'semm') el('dSemM').value = semM > 0 ? semM.toFixed(2) : '';
    if (mandaDensidad !== 'dist') el('dDist').value = dref > 0 ? dref.toFixed(1) : '';

    el('oSemM').innerHTML = fmt(semM, 2) + ' <small>sem/m</small>';
    el('oDens').innerHTML = miles(dens) + ' <small>sem/ha</small>';
    el('oDist').innerHTML = fmt(dref, 1) + ' <small>cm</small>';
    el('oMetros').innerHTML = miles(metrosHa) + ' <small>m/ha</small>';
    el('oFalla').innerHTML = fmt(dref * 1.5, 1) + ' <small>cm</small>';
    el('oDup').innerHTML = fmt(dref * 0.5, 1) + ' <small>cm</small>';
    el('oTol').innerHTML = dref > 0
      ? (fmt(dref * 0.75, 1) + '–' + fmt(dref * 1.25, 1) + ' <small>cm</small>') : '—';
  }

  ['dEsp', 'dDens', 'dSemM', 'dDist'].forEach(function (id) {
    var input = el(id);
    if (!input) return;
    input.addEventListener('input', function () {
      if (id === 'dDens') mandaDensidad = 'dens';
      else if (id === 'dSemM') mandaDensidad = 'semm';
      else if (id === 'dDist') mandaDensidad = 'dist';
      calcDensidad();
    });
  });

  // ── 2. Evaluación a campo ────────────────────────────────────────────────

  function parseEventos(txt) {
    return (txt || '').split(/[\s,;]+/)
      .map(function (s) { return parseFloat(s.replace(',', '.')); })
      .filter(function (v) { return isFinite(v) && v > 0; });
  }

  function calcCampo() {
    var ev = parseEventos(el('cEventos').value);
    var msg = el('cMsg');
    if (ev.length < 2) {
      msg.textContent = '✕ cargá al menos 2 separaciones';
      msg.className = 'send-msg err';
      return;
    }
    var dref = num('cDref', 0);
    var metros = num('cMetros', 0);
    var e = num('cEsp', 0);
    if (!(dref > 0)) {
      msg.textContent = '✕ falta el distanciamiento de referencia';
      msg.className = 'send-msg err';
      return;
    }

    var fallas = 0, fallasDobles = 0, dups = 0, correctos = [], enTol = 0;
    ev.forEach(function (x) {
      if (x < 0.5 * dref) { dups++; return; }
      if (x > 1.5 * dref) {
        // Entre 2,5 y 3,5 Dref el hueco equivale a dos semillas faltantes.
        if (x > 2.5 * dref) fallasDobles++; else fallas++;
        return;
      }
      correctos.push(x);
      if (x >= 0.75 * dref && x <= 1.25 * dref) enTol++;
    });

    var total = ev.length;
    var corregidos = total + fallas + fallasDobles * 2 - dups;
    var metrosHa = e > 0 ? (10000 / e) : 0;
    var factor = (metros > 0 && metrosHa > 0) ? (metrosHa / metros) : 0;
    var dReal = total * factor;
    var dCorr = corregidos * factor;
    var densRef = dref > 0 && metrosHa > 0 ? (100 / dref) * metrosHa : 0;

    var media = correctos.reduce(function (a, b) { return a + b; }, 0) / (correctos.length || 1);
    var varianza = correctos.reduce(function (a, b) { return a + (b - media) * (b - media); }, 0) /
                   (correctos.length || 1);
    var desvio = Math.sqrt(varianza);

    el('eTot').textContent = total;
    el('eOk').textContent = correctos.length;
    el('eFallas').innerHTML = (fallas + fallasDobles) +
      (fallasDobles ? ' <small>(' + fallasDobles + ' dobles)</small>' : '');
    el('eDup').textContent = dups;
    el('eCorr').textContent = corregidos;
    el('eDreal').innerHTML = miles(dReal) + ' <small>sem/ha</small>';
    el('eDcorr').innerHTML = miles(dCorr) + ' <small>sem/ha</small>';

    // El doc pide que la corregida no difiera más de ±5% de la de referencia.
    var box = el('eEnRango').parentNode;
    if (densRef > 0 && dCorr > 0) {
      var desvPct = ((dCorr - densRef) / densRef) * 100;
      el('eEnRango').innerHTML = (desvPct >= 0 ? '+' : '') + desvPct.toFixed(1) + ' <small>%</small>';
      box.className = 'cs-kpi ' + (Math.abs(desvPct) <= 5 ? 'ok' : 'bad');
    } else {
      el('eEnRango').textContent = '—';
      box.className = 'cs-kpi';
    }

    var pobl = correctos.length > 0 ? (enTol * 100 / correctos.length) : 0;
    el('ePobl').innerHTML = fmt(pobl, 1) + ' <small>%</small>';
    el('ePobl').parentNode.className = 'cs-kpi ' + (pobl >= 70 ? 'ok' : pobl >= 50 ? 'warn' : 'bad');
    el('eDesv').innerHTML = correctos.length ? (fmt(desvio, 2) + ' <small>cm</small>') : '—';
    el('ePctF').innerHTML = corregidos > 0
      ? fmt((fallas + fallasDobles * 2) * 100 / corregidos, 1) + ' <small>%</small>' : '—';
    el('ePctD').innerHTML = corregidos > 0 ? fmt(dups * 100 / corregidos, 1) + ' <small>%</small>' : '—';

    msg.textContent = '✓ ' + total + ' separaciones evaluadas';
    msg.className = 'send-msg ok';
  }

  if (el('cCalc')) el('cCalc').addEventListener('click', calcCampo);
  if (el('cLimpiar')) el('cLimpiar').addEventListener('click', function () {
    el('cEventos').value = '';
    el('cMsg').textContent = '';
    ['eTot', 'eOk', 'eFallas', 'eDup', 'eCorr', 'eDreal', 'eDcorr', 'eEnRango',
     'ePobl', 'eDesv', 'ePctF', 'ePctD'].forEach(function (id) {
      el(id).textContent = '—';
      el(id).parentNode.className = 'cs-kpi';
    });
  });

  // ── 3. kg/ha ↔ sem/ha ────────────────────────────────────────────────────

  var mandaPms = 'kg';

  function calcPms() {
    var pms = num('pPms', 0);
    if (!(pms > 0)) return;
    var kg, sem;
    if (mandaPms === 'kg') {
      kg = num('pKg', 0);
      sem = kg * 1000000 / pms;
      el('pSem').value = sem > 0 ? Math.round(sem) : '';
    } else {
      sem = num('pSem', 0);
      kg = sem * pms / 1000000;
      el('pKg').value = kg > 0 ? kg.toFixed(2) : '';
    }
    var pg = num('pPg', 100) / 100;
    el('oPSem').innerHTML = miles(sem) + ' <small>sem/ha</small>';
    el('oPKg').innerHTML = fmt(kg, 2) + ' <small>kg/ha</small>';
    el('oPLog').innerHTML = miles(sem * pg) + ' <small>pl/ha</small>';
  }

  ['pPms', 'pKg', 'pSem', 'pPg'].forEach(function (id) {
    var input = el(id);
    if (!input) return;
    input.addEventListener('input', function () {
      if (id === 'pKg') mandaPms = 'kg';
      else if (id === 'pSem') mandaPms = 'sem';
      calcPms();
    });
  });

  // ── 4. Motor: sem/vuelta y rpm ───────────────────────────────────────────

  function calcMotor() {
    var semM = num('mSemM', 0);
    var vel = num('mVel', 0);
    var surcos = Math.max(1, num('mSurcos', 1));
    var semVuelta = num('mSemVuelta', 0);
    var ppr = num('mPpr', 0);
    var maxHz = num('mMaxHz', 0);

    var vMs = vel / 3.6;
    var semS = semM * vMs * surcos;                 // semillas por segundo
    var rpm = semVuelta > 0 ? (semS / semVuelta) * 60 : 0;
    var hz = ppr > 0 ? (rpm * ppr / 60) : 0;

    el('oMSemS').innerHTML = fmt(semS, 1) + ' <small>sem/s</small>';
    el('oMRpm').innerHTML = fmt(rpm, 1) + ' <small>rpm</small>';
    el('oMHz').innerHTML = fmt(hz, 0) + ' <small>Hz</small>';

    var uso = maxHz > 0 ? (hz * 100 / maxHz) : 0;
    el('oMUso').innerHTML = uso > 0 ? (fmt(uso, 0) + ' <small>% del tope</small>') : '—';
    el('oMUso').parentNode.className = 'cs-kpi ' +
      (uso <= 0 ? '' : uso <= 85 ? 'ok' : uso <= 100 ? 'warn' : 'bad');

    // A qué velocidad el motor se queda sin vueltas con esta dosis.
    var velMax = 0;
    if (maxHz > 0 && ppr > 0 && semVuelta > 0 && semM > 0) {
      var rpmMax = maxHz * 60 / ppr;
      var semSMax = rpmMax * semVuelta / 60;
      velMax = (semSMax / (semM * surcos)) * 3.6;
    }
    el('oMVelMax').innerHTML = velMax > 0 ? (fmt(velMax, 1) + ' <small>km/h</small>') : '—';
    el('oMVelMax').parentNode.className = 'cs-kpi ' +
      (velMax <= 0 ? '' : velMax >= vel ? 'ok' : 'bad');
  }

  ['mSemM', 'mVel', 'mSurcos', 'mSemVuelta', 'mPpr', 'mMaxHz'].forEach(function (id) {
    var input = el(id);
    if (input) input.addEventListener('input', calcMotor);
  });

  if (el('mMotor')) el('mMotor').addEventListener('change', function () {
    var m = state.motores[parseInt(this.value, 10)];
    if (!m) return;
    el('mSemVuelta').value = m.semillas_vuelta || 24;
    el('mPpr').value = m.dientes_engranaje || 600;
    el('mMaxHz').value = m.max_hz || 0;
    el('mSurcos').value = (m.cortes && m.cortes.length) ? m.cortes.length : 1;
    if (m.unidad_dosis === 'sem_m' && m.dosis_fija > 0) el('mSemM').value = m.dosis_fija;
    calcMotor();
  });

  // ── Precarga desde la máquina real ───────────────────────────────────────

  async function cargarContexto() {
    var espaciamiento = 0;
    try {
      var ri = await fetch('/api/implemento', { cache: 'no-store' });
      var di = await ri.json();
      state.impl = (di && di.ok) ? di.implemento : null;
    } catch (e) { state.impl = null; }

    // Toda la geometría sale de la configuración de secciones de PilotX:
    // ancho de labor, cantidad de surcos (= secciones) y distancia entre
    // hileras (= ancho / secciones).
    var ancho = 0, surcos = 0;
    try {
      var rt = await fetch('/api/tool', { cache: 'no-store' });
      var dt = await rt.json();
      var tool = (dt && dt.tool) || {};
      ancho = Number(tool.width) || 0;
      surcos = tool.numSections | 0;
    } catch (e) {}
    if (ancho > 0 && surcos > 0) espaciamiento = ancho / surcos;

    var pill = el('csImpl'), txt = el('csImplTxt');
    if (espaciamiento > 0) {
      pill.className = 'pill ok';
      txt.textContent = surcos + ' surcos a ' + espaciamiento.toFixed(2) +
        ' m · ancho ' + ancho.toFixed(2) + ' m (de PilotX)';
      el('dEsp').value = espaciamiento.toFixed(3);
      el('cEsp').value = espaciamiento.toFixed(3);
    } else {
      pill.className = 'pill warn';
      txt.textContent = 'PilotX sin secciones configuradas';
    }

    // Motores QuantiX configurados.
    try {
      var rm = await fetch('/api/quantix/motores', { cache: 'no-store' });
      var dm = await rm.json();
      var cfg = (dm && dm.config) || dm;
      var sel = el('mMotor');
      state.motores = [];
      (cfg.nodos || []).forEach(function (n) {
        (n.motores || []).forEach(function (m, i) {
          state.motores.push(m);
          var o = document.createElement('option');
          o.value = state.motores.length - 1;
          o.textContent = (n.nombre || n.uid) + ' · M' + i + ' ' + (m.nombre || '');
          sel.appendChild(o);
        });
      });
      if (state.motores.length) sel.dispatchEvent(new Event('change'));
    } catch (e) {}

    calcDensidad();
    calcPms();
    calcMotor();
  }

  document.addEventListener('DOMContentLoaded', cargarContexto);
})();
