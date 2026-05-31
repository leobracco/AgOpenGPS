// ============================================================================
// vistax-stats.js — ventana superior de VistaX sobre PilotX.
// KPIs grandes (sem/m, sem/ha, sem/min, vel) + barra objetivo/desvío +
// lista de auxiliares (turbina, eje, tolva, presión, herramienta, ...).
// Polleo /api/vistax/live a 2 Hz + /api/vistax/implemento a 0.1 Hz (10 s)
// para enterarnos cuando cambia el insumo / la separación entre surcos.
// ============================================================================
(function () {
  'use strict';

  var POLL_MS = 500;
  var IMP_POLL_MS = 10000;

  var PRIMARIOS = { 'semilla': 1, 'fertilizante': 1 };

  // ico/label/unit/mode + prio (orden de aparición: menor primero).
  // Prioridad operativa: alertas críticas (tolvas) → estado de implemento
  // → finales de carrera → presión → rates (turbina/eje).
  var AUX_META = {
    'tolva_vacia':        { ico: 'V',  label: 'Tolva vacía',  unit: '',    mode: 'state', onTxt: 'VACÍA', offTxt: 'OK', alarmWhenOn: true, prio: 10 },
    'tolva_llena':        { ico: 'L',  label: 'Tolva llena',  unit: '',    mode: 'state', onTxt: 'LLENA', offTxt: 'OK', prio: 20 },
    'bajada_herramienta': { ico: 'H',  label: 'Herramienta',  unit: '',    mode: 'state', onTxt: 'BAJO',  offTxt: 'ARRIBA', prio: 30 },
    'final_carrera':      { ico: 'F',  label: 'Fin carrera',  unit: '',    mode: 'state', onTxt: 'ACTIVO', offTxt: 'LIBRE', prio: 40 },
    'presion':            { ico: 'P',  label: 'Presión',      unit: 'bar', mode: 'rate',  prio: 50 },
    'turbina':            { ico: 'T',  label: 'Turbina',      unit: 'rpm', mode: 'rate',  prio: 60 },
    'rotacion_eje':       { ico: 'E',  label: 'Eje',          unit: 'rpm', mode: 'rate',  prio: 70 }
  };

  function prioOf(tipo) {
    var m = AUX_META[tipo];
    return m && m.prio != null ? m.prio : 999;
  }

  function $(id) { return document.getElementById(id); }
  function pick(o, a, b) { return (o && o[a] != null) ? o[a] : (o ? o[b] : undefined); }
  function fmt(n, d) {
    if (n == null || isNaN(n)) return '—';
    return Number(n).toFixed(d == null ? 1 : d);
  }
  function fmtMiles(n) {
    if (n == null || isNaN(n)) return '—';
    var x = Math.round(Number(n));
    return x.toLocaleString('es-AR');
  }
  function ageStr(iso) {
    if (!iso) return '—';
    var t = Date.parse(iso); if (isNaN(t)) return '—';
    var s = Math.max(0, Math.round((Date.now() - t) / 1000));
    if (s < 60) return s + 's';
    if (s < 3600) return Math.round(s / 60) + 'min';
    return Math.round(s / 3600) + 'h';
  }

  var state = {
    paused: false,
    timer: null,
    impTimer: null,
    lastIso: '',
    // Datos del implemento (cacheados, refrescados cada IMP_POLL_MS).
    distanciaEntreSurcos: 0,    // metros
    densidadObjetivo: 0,        // sem/m
    toleranciaPct: 20
  };

  function tipoOf(s) { return (pick(s, 'Tipo', 'tipo') || '').toLowerCase(); }
  function classFromEstado(st) {
    st = (st || 'no-data').toLowerCase();
    if (st === 'ok')     return 's-ok';
    if (st === 'bajo')   return 's-bajo';
    if (st === 'tapado') return 's-tapado';
    if (st === 'exceso') return 's-exceso';
    if (st === 'muted')  return 's-muted';
    return 's-no-data';
  }
  function isStateOn(val) {
    if (val == null) return false;
    if (typeof val === 'boolean') return val;
    var n = Number(val);
    if (!isNaN(n)) return n > 0.5;
    var s = String(val).toLowerCase();
    return s === 'true' || s === 'on' || s === '1';
  }

  function avgValor(arr) {
    var s = 0, n = 0;
    for (var i = 0; i < arr.length; i++) {
      var muted = pick(arr[i], 'Muted', 'muted');
      if (muted) continue;
      var v = pick(arr[i], 'Valor', 'valor');
      if (v == null || isNaN(v)) continue;
      s += Number(v); n++;
    }
    return n > 0 ? (s / n) : null;
  }

  function renderAuxRow(s) {
    var tipo   = tipoOf(s);
    var meta   = AUX_META[tipo] || { ico: '?', label: tipo || 'sensor', unit: '', mode: 'rate' };
    var bajada = pick(s, 'Bajada', 'bajada') || 0;
    var estado = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
    var muted  = pick(s, 'Muted', 'muted');
    var val    = pick(s, 'Valor', 'valor');
    var spm    = pick(s, 'Spm', 'spm');

    var cls = muted ? 's-muted' : classFromEstado(estado);
    var vTxt = '—';
    var uTxt = meta.unit || '';

    if (meta.mode === 'state') {
      if (val == null && spm == null) { vTxt = '—'; uTxt = ''; }
      else {
        var on = isStateOn(val != null ? val : spm);
        vTxt = on ? (meta.onTxt || 'ON') : (meta.offTxt || 'OFF');
        uTxt = '';
        if (meta.alarmWhenOn && on && !muted && estado === 'no-data') cls = 's-tapado';
      }
    } else if (meta.mode === 'count') {
      vTxt = (val != null && !isNaN(val)) ? fmt(val, 0) : '—';
    } else {
      if (spm != null && !isNaN(spm) && spm > 0) vTxt = fmt(spm, 0);
      else if (val != null && !isNaN(val))       vTxt = fmt(val, val < 10 ? 1 : 0);
    }

    var sub = 'bajada ' + bajada + ' · ' + (muted ? 'silenciado' : estado);
    return '' +
      '<div class="vx-aux-row ' + cls + '" title="' + meta.label + ' · ' + sub + '">' +
        '<div class="ico">' + meta.ico + '</div>' +
        '<div class="meta">' +
          '<div class="lbl">' + meta.label + '</div>' +
          '<div class="sub">' + sub + '</div>' +
        '</div>' +
        '<div class="v">' + vTxt + (uTxt ? '<span class="u">' + uTxt + '</span>' : '') + '</div>' +
      '</div>';
  }

  function countFallas(arr) {
    var n = 0;
    for (var i = 0; i < arr.length; i++) {
      var muted = pick(arr[i], 'Muted', 'muted');
      var estado = (pick(arr[i], 'Estado', 'estado') || 'no-data').toLowerCase();
      if (!muted && (estado === 'tapado' || estado === 'bajo' || estado === 'exceso')) n++;
    }
    return n;
  }

  function setCount(elId, total, fallas) {
    var el = $(elId);
    if (!el) return;
    el.textContent = fallas > 0 ? (fallas + ' / ' + total) : total;
    el.classList.toggle('bad', fallas > 0);
  }

  function updateTargetBar(semM) {
    var obj = state.densidadObjetivo;
    var tol = state.toleranciaPct || 20;
    var elObj = $('vxObj');
    var bar = $('vxBar');
    var fill = $('vxBarFill');
    var desv = $('vxDesvio');

    elObj.textContent = obj > 0 ? (fmt(obj, 1) + ' sem/m') : '—';
    bar.classList.remove('bajo', 'exceso');

    if (obj <= 0 || semM == null) {
      fill.style.width = '0%';
      fill.style.left = '50%';
      desv.textContent = '—';
      return;
    }

    // Desvío en %: positivo = exceso, negativo = bajo
    var deltaPct = ((semM - obj) / obj) * 100;
    var clamped = Math.max(-100, Math.min(100, deltaPct));
    // La barra crece desde el centro hacia la derecha (exceso) o izquierda (bajo).
    var half = Math.abs(clamped) / 2; // 0..50
    if (clamped >= 0) {
      fill.style.left = '50%';
      fill.style.width = half + '%';
    } else {
      fill.style.left = (50 - half) + '%';
      fill.style.width = half + '%';
    }

    if (deltaPct < -tol) bar.classList.add('bajo');
    else if (deltaPct > tol) bar.classList.add('exceso');

    var sign = deltaPct >= 0 ? '+' : '';
    desv.textContent = sign + fmt(deltaPct, 0) + '%';
  }

  function render(live) {
    if (!live) return;
    var trenes   = pick(live, 'Trenes', 'trenes') || [];
    var spm      = pick(live, 'SpmPromedio', 'spmPromedio');
    var fallas   = pick(live, 'FallasActivas', 'fallasActivas') || 0;
    var vel      = pick(live, 'Velocidad', 'velocidad');
    var hasAlarm = pick(live, 'HasAlarm', 'hasAlarm');
    var monAct   = pick(live, 'MonitoreoActivo', 'monitoreoActivo');
    var impNom   = pick(live, 'NombreImplemento', 'nombreImplemento') || '—';
    var tolFromLive = pick(live, 'ToleranciaDesvio', 'toleranciaDesvio');
    if (tolFromLive != null && !isNaN(tolFromLive) && tolFromLive > 0) {
      state.toleranciaPct = tolFromLive;
    }

    // Pill arriba.
    var pill = $('vxPill'), txt = $('vxPillTxt');
    pill.classList.remove('warn', 'bad');
    if (hasAlarm) { pill.classList.add('bad'); txt.textContent = 'alarma'; }
    else if (!monAct) { pill.classList.add('warn'); txt.textContent = 'detenido'; }
    else if (fallas > 0) { pill.classList.add('warn'); txt.textContent = fallas + ' falla' + (fallas === 1 ? '' : 's'); }
    else { txt.textContent = 'ok'; }

    // Aplanar todos los surcos.
    var todos = [];
    trenes.forEach(function (t) {
      (pick(t, 'Surcos', 'surcos') || []).forEach(function (s) { todos.push(s); });
    });

    var primarios  = todos.filter(function (s) { return PRIMARIOS[tipoOf(s)]; });
    var auxiliares = todos.filter(function (s) { return !PRIMARIOS[tipoOf(s)]; });

    // Foco: sem/m → tomamos sólo SEMILLA como referencia de densidad real
    // (fert es importante pero no representa "densidad de siembra").
    var semilla = primarios.filter(function (s) { return tipoOf(s) === 'semilla'; });
    var semMRef = semilla.length > 0 ? semilla : primarios;
    var semMAvg = avgValor(semMRef);

    // KPI sem/m
    $('vxSemM').textContent = (semMAvg == null) ? '—' : fmt(semMAvg, 1);
    $('vxSemMSub').textContent = semilla.length > 0
      ? (semilla.length + ' surcos sem')
      : (primarios.length + ' primarios');

    // KPI sem/ha = sem/m × (10000 / distanciaEntreSurcos)
    // (líneas continuas: cada metro de surco × ancho lineal por ha)
    var semHa = null;
    if (semMAvg != null && state.distanciaEntreSurcos > 0) {
      semHa = semMAvg * (10000 / state.distanciaEntreSurcos);
    }
    $('vxSemHa').textContent = fmtMiles(semHa);
    $('vxSemHaSub').textContent = state.distanciaEntreSurcos > 0
      ? ('sep ' + fmt(state.distanciaEntreSurcos * 100, 0) + ' cm')
      : 'sep —';

    // KPI sem/min y velocidad
    $('vxSpm').textContent = (spm == null) ? '—' : fmt(spm, 0);
    $('vxVel').textContent = (vel == null) ? '—' : fmt(vel, 1);
    $('vxVelSub').textContent = monAct ? 'sembrando' : 'detenido';

    // Barra objetivo / desvío.
    updateTargetBar(semMAvg);

    // Auxiliares.
    var auxBody = $('vxAuxBody');
    if (auxiliares.length === 0) {
      auxBody.innerHTML = '<div class="vx-empty">sin sensores auxiliares</div>';
    } else {
      // Ordenar por prioridad operativa (alertas críticas primero, rates al final),
      // y dentro del mismo tipo por número de bajada.
      auxiliares.sort(function (a, b) {
        var ta = tipoOf(a), tb = tipoOf(b);
        var pa = prioOf(ta), pb = prioOf(tb);
        if (pa !== pb) return pa - pb;
        if (ta !== tb) return ta < tb ? -1 : 1;
        return (pick(a, 'Bajada', 'bajada') || 0) - (pick(b, 'Bajada', 'bajada') || 0);
      });
      auxBody.innerHTML = auxiliares.map(renderAuxRow).join('');
    }
    setCount('vxAuxCount', auxiliares.length, countFallas(auxiliares));

    $('vxImp').textContent = impNom;
    state.lastIso = new Date().toISOString();
    $('vxAge').textContent = ageStr(state.lastIso);
  }

  function tickAge() {
    if (state.lastIso) $('vxAge').textContent = ageStr(state.lastIso);
  }

  async function poll() {
    if (state.paused) return;
    try {
      var r = await fetch('/api/vistax/live', { cache: 'no-store' });
      if (r.ok) render(await r.json());
    } catch (e) { /* silencioso */ }
  }

  async function pollImp() {
    try {
      var r = await fetch('/api/vistax/implemento', { cache: 'no-store' });
      if (!r.ok) return;
      var j = await r.json();
      var setup = pick(j, 'Setup', 'setup') || j;
      var d = pick(setup, 'DistanciaEntreSurcos', 'distanciaEntreSurcos');
      if (d == null) d = pick(setup, 'distancia_entre_surcos', 'distancia_entre_surcos');
      var dens = pick(setup, 'DensidadObjetivo', 'densidadObjetivo');
      if (dens == null) dens = pick(setup, 'densidad_objetivo', 'densidad_objetivo');
      var tol = pick(setup, 'ToleranciaDesvio', 'toleranciaDesvio');
      if (tol == null) tol = pick(setup, 'tolerancia_desvio', 'tolerancia_desvio');
      if (d != null && !isNaN(d)) state.distanciaEntreSurcos = Number(d);
      if (dens != null && !isNaN(dens)) state.densidadObjetivo = Number(dens);
      if (tol != null && !isNaN(tol) && tol > 0) state.toleranciaPct = Number(tol);
    } catch (e) { /* silencioso */ }
  }

  function start() {
    if (state.timer) return;
    pollImp();
    poll();
    state.timer = setInterval(poll, POLL_MS);
    state.impTimer = setInterval(pollImp, IMP_POLL_MS);
    setInterval(tickAge, 1000);
  }

  document.addEventListener('visibilitychange', function () {
    state.paused = document.hidden;
    if (!state.paused) poll();
  });

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
