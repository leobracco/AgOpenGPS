// ============================================================================
// vistax-live.js — franja MONITOR de VistaX para la barra inferior de PilotX.
// Una fila de chips POR TREN: un chip por surco primario (semilla/fertilizante).
// El operario lo usa como vista de "todo el ancho de la sembradora" para
// detectar surcos tapados al toque sin entrar al Hub.
// Polleo /api/vistax/live a 2 Hz. Pausa con visibilitychange.
// ============================================================================
(function () {
  'use strict';

  var POLL_MS = 500;
  var PRIMARIOS = { 'semilla': 1, 'fertilizante': 1 };

  function $(id) { return document.getElementById(id); }
  function fmt(n, d) {
    if (n == null || isNaN(n)) return '—';
    return Number(n).toFixed(d == null ? 1 : d);
  }
  // sem/min (wire) → sem/m (lo único que ve el operario). Con el tractor
  // parado (< 0,5 km/h) no existe "semillas por metro": se muestra —.
  function fmtSemM(spm, velKmh) {
    if (spm == null || isNaN(spm)) return '—';
    var vms = (velKmh || 0) / 3.6;
    if (vms < 0.14) return '—';
    return fmt(spm / 60.0 / vms, 1);
  }
  function tipoOf(s) {
    return (s.tipo || '').toLowerCase();
  }
  function classFromEstado(st) {
    st = (st || 'no-data').toLowerCase();
    if (st === 'ok')          return 's-ok';
    if (st === 'bajo')        return 's-bajo';
    if (st === 'tapado')      return 's-tapado';
    if (st === 'exceso')      return 's-exceso';
    if (st === 'muted')       return 's-muted';
    if (st === 'seccion-off') return 's-seccion-off';
    return 's-no-data';
  }
  function labelEstado(st) {
    st = (st || 'no-data').toLowerCase();
    if (st === 'ok')          return 'OK';
    if (st === 'bajo')        return 'Bajo';
    if (st === 'tapado')      return 'Tapado';
    if (st === 'exceso')      return 'Exceso';
    if (st === 'muted')       return 'Muteado';
    if (st === 'seccion-off') return 'Sección OFF';
    return 'Sin datos';
  }
  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;')
      .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  // state.lastLive: último snapshot completo para popup reactivo.
  // state.detailFocus: { uid, cable } del surco abierto en el popup.
  var state = { paused: false, timer: null, lastLive: null, detailFocus: null, lastCfg: null };

  function renderChip(s, monActivo) {
    var bajada = s.bajada || 0;
    var estado = (s.estado || 'no-data').toLowerCase();
    var muted  = s.muted;
    // Reglas de prioridad visual:
    //   1. Si el MONITOREO no está activo → todos los chips IDLE (gris)
    //      independientemente de lo que reporten los sensores. El usuario
    //      no debe ver "verde OK" antes de que el sistema empiece a sembrar
    //      (caída de semilla / sección pintando, según método configurado).
    //   2. Si la sección de PilotX está cerrada para este surco → gris oscuro punteado.
    //   3. Si está muteado por config → s-muted (gris claro punteado).
    //   4. Sino, el estado real reportado por el backend.
    var cls;
    if (!monActivo)                     cls = 's-no-data';
    else if (estado === 'seccion-off')  cls = 's-seccion-off';
    else if (muted)                     cls = 's-muted';
    // no-data CON alerta = monitoreo activo y el sensor de siembra no
    // reporta telemetría (nodo caído / no cae semilla) → falla en rojo.
    else if (estado === 'no-data' && s.alerta) cls = 's-tapado';
    else                                cls = classFromEstado(estado);
    var tipo   = tipoOf(s);
    var uid    = s.uid || '';
    var cable  = s.cable;
    var tag    = '';
    if (tipo === 'semilla')      tag = '<span class="tipo">S</span>';
    else if (tipo === 'fertilizante') tag = '<span class="tipo">F</span>';
    var title  = 'surco ' + bajada + ' · ' + (tipo || '?') + ' · ' + estado;
    // Barra de NIVEL estilo monitor de siembra clásico (referencia de los
    // competidores): el relleno de abajo hacia arriba es el ratio real vs
    // objetivo, saturado en 100%. El color del chip sigue diciendo el estado.
    var ratio = s.ratio_objetivo;
    var fillPct = 0;
    if (cls === 's-ok' || cls === 's-bajo' || cls === 's-exceso') {
      fillPct = ratio == null ? 0 : Math.max(4, Math.min(100, Math.round(ratio * 100)));
    }
    return '<div class="vx-chip ' + cls + '" title="' + esc(title) +
             '" data-uid="' + esc(uid) + '" data-cable="' + esc(cable) +
             '" data-bajada="' + esc(bajada) + '">' +
             (fillPct > 0 ? '<i class="fill" style="height:' + fillPct + '%"></i>' : '') +
             '<span class="num">' + bajada + '</span>' + tag +
           '</div>';
  }

  // Busca el surco vivo en el último snapshot por (uid, cable).
  function findSurcoVivo(uid, cable) {
    var live = state.lastLive;
    if (!live) return null;
    var trenes = live.trenes || [];
    for (var i = 0; i < trenes.length; i++) {
      var surcos = trenes[i].surcos || [];
      for (var j = 0; j < surcos.length; j++) {
        var s = surcos[j];
        var u = s.uid || '';
        var c = s.cable;
        if (u === uid && String(c) === String(cable)) return s;
      }
    }
    return null;
  }

  function openDetail(uid, cable) {
    state.detailFocus = { uid: uid, cable: String(cable) };
    refreshDetail();
    $('vxPopBack').classList.add('open');
  }
  function closeDetail() {
    state.detailFocus = null;
    $('vxPopBack').classList.remove('open');
  }

  function refreshDetail() {
    if (!state.detailFocus) return;
    // "cable" lo usa la fila Tren · Cable de abajo: viene del foco, no del
    // surco (bug histórico: era una ReferenceError y el popup nunca abría).
    var cable = state.detailFocus.cable;
    var s = findSurcoVivo(state.detailFocus.uid, cable);
    var ttl = $('vxPopTtl'), body = $('vxPopBody');
    if (!s) {
      ttl.textContent = 'Surco';
      body.innerHTML = '<div class="warn">Sensor no encontrado en el snapshot actual.</div>';
      return;
    }
    var bajada = s.bajada || 0;
    var tipo   = tipoOf(s);
    var estado = (s.estado || 'no-data').toLowerCase();
    var spm    = s.spm;
    var obj    = s.objetivo;
    var pct    = (spm != null && obj != null && obj > 0) ? Math.round((spm / obj) * 100) : null;
    var muted  = s.muted;
    var secOff = s.seccion_cortada;
    var tren   = s.tren;

    ttl.textContent = 'Surco ' + bajada + (tipo ? ' · ' + tipo : '');

    var velLive = (state.lastLive || {}).velocidad;

    var html = '';
    html += '<div class="row"><span class="lbl">Estado</span><span>' + esc(labelEstado(estado)) + '</span></div>';
    html += '<div class="row"><span class="lbl">sem/m</span><span>' + fmtSemM(spm, velLive) +
            (obj == null || obj <= 0 ? '' : ' / ' + fmtSemM(obj, velLive)) + '</span></div>';
    // sem/ha desde sem/m y la distancia entre surcos del implemento central.
    var dist = (state.lastLive || {}).distancia_entre_surcos || 0;
    var vms = (velLive || 0) / 3.6;
    if (dist > 0 && vms >= 0.14 && spm != null) {
      var semHa = (spm / 60.0 / vms) * 10000.0 / dist;
      html += '<div class="row"><span class="lbl">sem/ha</span><span>' + fmt(semHa, 0) + '</span></div>';
    }
    // Dosis estimada por REGLA DE TRES: la máquina fue calibrada para
    // dosificar dosis_ref (valor + UNIDAD del insumo activo); el flujo
    // promedio de la primera pasada estable quedó como spm_ref ≡ esa dosis.
    // Proporcional y agnóstico de unidad: el estimado sale en la unidad
    // configurada en el insumo (la comparten QuantiX y VistaX).
    var spmRef   = (state.lastLive || {}).spm_ref || 0;
    var dosisRef = (state.lastLive || {}).dosis_ref_kg_ha || 0;
    var UNIDADES = { kg_ha: 'kg/ha', sem_ha: 'sem/ha', sem_m: 'sem/m' };
    var uniRef   = UNIDADES[(state.lastLive || {}).dosis_ref_unidad] || 'kg/ha';
    if (spmRef > 0 && dosisRef > 0 && spm != null) {
      var dosisEst = spm / spmRef * dosisRef;
      var dec = dosisRef >= 1000 ? 0 : 1;
      html += '<div class="row"><span class="lbl">Densidad est.</span><span>' +
              fmt(dosisEst, dec) + ' / ' + fmt(dosisRef, dec) + ' ' + uniRef + '</span></div>';
    }
    if (pct != null) {
      var pctClamp = Math.max(0, Math.min(150, pct));
      html += '<div class="row"><span class="lbl">% objetivo</span><span>' + pct + '%</span></div>';
      html += '<div class="bar"><i style="width:' + Math.min(100, pctClamp) + '%"></i></div>';
    }
    html += '<div class="row"><span class="lbl">Tren · Cable</span><span>' + esc(tren) + ' · ' + esc(cable) + '</span></div>';
    html += '<div class="row"><span class="lbl">Sección PilotX</span><span>' +
            (secOff ? 'cerrada' : 'abierta') + '</span></div>';
    if (muted)  html += '<div class="warn">Sensor muteado — alarmas desactivadas.</div>';
    if (secOff) html += '<div class="warn">Sección PilotX cerrada — este surco no sensa.</div>';
    body.innerHTML = html;
  }

  // Bind a nivel documento con closest(): sobrevive a cualquier rerender del
  // strip y no depende de bubbling intermedio. Una sola vez.
  var _chipsBound = false;
  function bindChipClicks() {
    if (_chipsBound) return;
    _chipsBound = true;
    document.body.addEventListener('click', function (ev) {
      var chip = ev.target && ev.target.closest && ev.target.closest('.vx-chip');
      if (!chip) return;
      var uid = chip.getAttribute('data-uid') || '';
      var cable = chip.getAttribute('data-cable') || '';
      openDetail(uid, cable);
    });
  }

  function render(live) {
    if (!live) return;
    state.lastLive = live;
    var trenes   = live.trenes || [];
    var spm      = live.spm_promedio;
    var fallas   = live.fallas_activas || 0;
    var vel      = live.velocidad;
    var hasAlarm = live.has_alarm;
    var monAct   = live.monitoreo_activo;

    // Al operario SIEMPRE sem/m (regla de unidades): el wire trae sem/min,
    // se convierte con la velocidad viva. Parado no hay sem/m que valga: —.
    $('vxSpm').textContent    = fmtSemM(spm, vel);
    $('vxFallas').textContent = fallas;
    $('vxVel').textContent    = (vel == null) ? '—' : fmt(vel, 1);

    var pill = $('vxPill'), txt = $('vxPillTxt');
    pill.classList.remove('warn', 'bad');
    pill.style.cursor = 'pointer';
    if (hasAlarm) { pill.classList.add('bad'); txt.textContent = 'alarma'; }
    else if (!monAct) { pill.classList.add('warn'); txt.textContent = 'detenido'; }
    else if (fallas > 0) { pill.classList.add('warn'); txt.textContent = fallas + ' falla' + (fallas === 1 ? '' : 's'); }
    else { txt.textContent = 'ok'; }

    // Tooltip nativo con el diagnóstico — siempre disponible al pasar el mouse.
    var motivo = live.motivo_detenido || '';
    pill.title = motivo || (monAct ? 'Monitoreando' : '');

    var fbox = $('vxFallasBox');
    if (fbox) fbox.classList.toggle('bad', fallas > 0);

    // Aplanar surcos primarios en orden (tren, bajada).
    var todos = [];
    trenes.forEach(function (t) {
      var surcos = t.surcos || [];
      surcos.forEach(function (s) { todos.push(s); });
    });
    todos.sort(function (a, b) {
      var ta = a.tren || 0;
      var tb = b.tren || 0;
      if (ta !== tb) return ta - tb;
      return (a.bajada || 0) - (b.bajada || 0);
    });

    // Solo tipos primarios (semilla/fertilizante) van al strip.
    var primarios = todos.filter(function (s) { return PRIMARIOS[tipoOf(s)]; });

    var strip = $('vxStrip');
    if (primarios.length === 0) {
      strip.innerHTML = '<div class="vx-empty">sin sensores de siembra/fertilización mapeados</div>';
      return;
    }

    // Una FILA de chips por tren: la cantidad de filas del strip refleja la
    // cantidad de trenes del implemento (ej. tren 1 semilla / tren 2 ferti).
    var porTren = {};
    primarios.forEach(function (s) {
      var t = s.tren || 1;
      (porTren[t] = porTren[t] || []).push(s);
    });
    var trenKeys = Object.keys(porTren)
      .map(Number).sort(function (a, b) { return a - b; });
    // Ancho de chip uniforme entre filas: sin esto, un tren con pocos surcos
    // (ej. tren 2 con 2 fertilizantes) estira sus chips hasta max-width y
    // queda desalineado con el tren de 40+ surcos de arriba.
    var maxN = 1;
    trenKeys.forEach(function (t) { if (porTren[t].length > maxN) maxN = porTren[t].length; });
    strip.style.setProperty('--vx-chip-w',
      'calc((100% - ' + (maxN - 1) + ' * 3px) / ' + maxN + ')');
    var html = trenKeys
      .map(function (t) {
        return '<div class="vx-row">' +
          porTren[t].map(function (s) { return renderChip(s, !!monAct); }).join('') +
        '</div>';
      }).join('');
    strip.innerHTML = html;
    bindChipClicks();
    refreshDetail(); // si hay popup abierto, actualizar reactivamente.
    if ($('vxDiagBack') && $('vxDiagBack').classList.contains('open')) refreshDiag();
  }

  async function poll() {
    if (state.paused) return;
    try {
      var r = await fetch('/api/vistax/live', { cache: 'no-store' });
      if (r.ok) render(await r.json());
    } catch (e) { /* silencioso */ }
  }

  // ----- Config método de inicio -----
  // GET /api/vistax/config → snapshot completo (lo guardamos para no perder
  // campos al hacer PUT). Mutamos MetodoInicio y reenviamos el mismo objeto.
  async function openCfg() {
    var back = $('vxCfgBack');
    var msg = $('vxCfgMsg');
    msg.textContent = 'Cargando…';
    paintCfgRadio('sensores'); // fallback visual antes del GET
    back.classList.add('open');
    try {
      var r = await fetch('/api/vistax/config', { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      var cfg = await r.json();
      state.lastCfg = cfg;
      var cur = cfg.metodo_inicio || 'sensores';
      paintCfgRadio(cur);
      msg.textContent = '';
    } catch (e) {
      msg.textContent = 'No se pudo cargar la config';
    }
  }
  function closeCfg() { $('vxCfgBack').classList.remove('open'); }
  function paintCfgRadio(metodo) {
    var opts = document.querySelectorAll('#vxCfgBack .opt');
    for (var i = 0; i < opts.length; i++) {
      var sel = opts[i].getAttribute('data-m') === metodo;
      opts[i].classList.toggle('sel', sel);
      var input = opts[i].querySelector('input');
      if (input) input.checked = sel;
    }
  }
  function currentCfgRadio() {
    var checked = document.querySelector('#vxCfgBack input[name="vxMet"]:checked');
    return checked ? checked.value : 'sensores';
  }
  async function saveCfg() {
    var msg = $('vxCfgMsg');
    var nuevo = currentCfgRadio();
    if (!state.lastCfg) { msg.textContent = 'Sin config base'; return; }
    var dto = Object.assign({}, state.lastCfg);
    // snake_case — el binder server-side usa PropertyNameCaseInsensitive=true.
    dto.metodo_inicio = nuevo;
    msg.textContent = 'Guardando…';
    try {
      var r = await fetch('/api/vistax/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dto)
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      msg.textContent = 'Guardado ✓';
      state.lastCfg = dto;
      setTimeout(closeCfg, 600);
    } catch (e) {
      msg.textContent = 'Error al guardar';
    }
  }

  // ----- Diagnóstico (click en pill) -----
  function openDiag() {
    refreshDiag();
    $('vxDiagBack').classList.add('open');
  }
  function closeDiag() { $('vxDiagBack').classList.remove('open'); }
  function refreshDiag() {
    var live = state.lastLive || {};
    var body = $('vxDiagBody');
    if (!body) return;
    var monAct  = live.monitoreo_activo;
    var metodo  = live.metodo_inicio || '—';
    var motivo  = live.motivo_detenido || '';
    var vel     = live.velocidad;
    var velMin  = live.vel_minima;
    var secPint = live.secciones_pintando;
    var sensArr = live.sensores_arriba;
    var umbral  = live.umbral_sensores;

    var html = '';
    html += '<div class="row"><span class="lbl">Estado</span><span>' +
            (monAct ? 'Monitoreando ✓' : 'Detenido') + '</span></div>';
    html += '<div class="row"><span class="lbl">Método</span><span>' + esc(metodo) + '</span></div>';
    html += '<div class="row"><span class="lbl">Velocidad</span><span>' +
            (vel == null ? '—' : fmt(vel, 1)) + ' / ' +
            (velMin == null ? '—' : fmt(velMin, 1)) + ' km/h</span></div>';
    if (metodo === 'pintando') {
      html += '<div class="row"><span class="lbl">Secciones pintando</span><span>' +
              (secPint == null ? '—' : secPint) + '</span></div>';
    } else {
      html += '<div class="row"><span class="lbl">Sensores con caída</span><span>' +
              (sensArr == null ? '—' : sensArr) + ' / ' +
              (umbral  == null ? '—' : umbral) + '</span></div>';
    }
    if (!monAct && motivo) {
      html += '<div class="warn">' + esc(motivo) + '</div>';
    }

    // Referencia de la regla de tres de densidad: hoy se captura sola en la
    // primera pasada estable, pero el operario puede pisarla a mano — "el
    // flujo de AHORA equivale a la densidad configurada" — o volver al auto.
    var spmRef   = live.spm_ref || 0;
    var dosisRef = live.dosis_ref_kg_ha || 0;
    var UNI = { kg_ha: 'kg/ha', sem_ha: 'sem/ha', sem_m: 'sem/m' };
    var uniRef = UNI[live.dosis_ref_unidad] || 'kg/ha';
    html += '<div class="row"><span class="lbl">Referencia densidad</span><span>' +
            (dosisRef <= 0 ? 'sin insumo con densidad'
              : (spmRef > 0 ? ('capturada ✓ (' + fmt(dosisRef, 1) + ' ' + uniRef + ')')
                            : 'pendiente de captura…')) + '</span></div>';
    if (dosisRef > 0) {
      html += '<div class="vx-ref-btns">' +
              '<button type="button" class="vx-diag-btn" data-refaccion="fijar">Fijar ahora (flujo actual = densidad)</button>' +
              '<button type="button" class="vx-diag-btn" data-refaccion="auto">Re-capturar auto</button>' +
              '</div>';
    }
    body.innerHTML = html;
  }

  async function ajustarReferencia(accion) {
    try {
      var r = await fetch('/api/vistax/referencia', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ accion: accion })
      });
      await r.json();
      poll(); // refresca el snapshot (y con él, el modal) al toque
    } catch (e) { /* sin conexión: el próximo poll lo muestra */ }
  }

  function start() {
    if (state.timer) return;
    poll();
    state.timer = setInterval(poll, POLL_MS);

    // Wiring popups una sola vez.
    $('vxPopX').addEventListener('click', closeDetail);
    $('vxPopBack').addEventListener('click', function (ev) {
      if (ev.target === $('vxPopBack')) closeDetail();
    });
    // Click en pill abre diagnóstico (por qué está detenido / activo).
    $('vxPill').addEventListener('click', openDiag);
    $('vxDiagX').addEventListener('click', closeDiag);
    // Delegación: el body del modal se re-renderiza en cada poll, el listener
    // vive en el contenedor que persiste.
    $('vxDiagBody').addEventListener('click', function (ev) {
      var a = ev.target.getAttribute && ev.target.getAttribute('data-refaccion');
      if (a) ajustarReferencia(a);
    });
    $('vxDiagBack').addEventListener('click', function (ev) {
      if (ev.target === $('vxDiagBack')) closeDiag();
    });
    $('vxCfgBtn').addEventListener('click', openCfg);
    $('vxCfgX').addEventListener('click', closeCfg);
    $('vxCfgCancel').addEventListener('click', closeCfg);
    $('vxCfgSave').addEventListener('click', saveCfg);
    $('vxCfgBack').addEventListener('click', function (ev) {
      if (ev.target === $('vxCfgBack')) closeCfg();
    });
    // Click sobre una opción del modal config marca el radio.
    document.querySelectorAll('#vxCfgBack .opt').forEach(function (opt) {
      opt.addEventListener('click', function () {
        paintCfgRadio(opt.getAttribute('data-m'));
      });
    });
  }

  document.addEventListener('visibilitychange', function () {
    state.paused = document.hidden;
    if (!state.paused) poll();
  });

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
