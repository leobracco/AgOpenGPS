// ============================================================================
// vistax-live.js — franja MONITOR de VistaX para la barra inferior de PilotX.
// Una sola fila de chips: un chip por surco primario (semilla/fertilizante).
// El operario lo usa como vista de "todo el ancho de la sembradora" para
// detectar surcos tapados al toque sin entrar al Hub.
// Polleo /api/vistax/live a 2 Hz. Pausa con visibilitychange.
// ============================================================================
(function () {
  'use strict';

  var POLL_MS = 500;
  var PRIMARIOS = { 'semilla': 1, 'fertilizante': 1 };

  function $(id) { return document.getElementById(id); }
  function pick(o, a, b) { return (o && o[a] != null) ? o[a] : (o ? o[b] : undefined); }
  function fmt(n, d) {
    if (n == null || isNaN(n)) return '—';
    return Number(n).toFixed(d == null ? 1 : d);
  }
  function tipoOf(s) {
    var t = pick(s, 'Tipo', 'tipo');
    return (t || '').toLowerCase();
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
    var bajada = pick(s, 'Bajada', 'bajada') || 0;
    var estado = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
    var muted  = pick(s, 'Muted', 'muted');
    // Reglas de prioridad visual:
    //   1. Si el MONITOREO no está activo → todos los chips IDLE (gris)
    //      independientemente de lo que reporten los sensores. El usuario
    //      no debe ver "verde OK" antes de que el sistema empiece a sembrar
    //      (caída de semilla / sección pintando, según método configurado).
    //   2. Si la sección de AOG está cerrada para este surco → gris oscuro punteado.
    //   3. Si está muteado por config → s-muted (gris claro punteado).
    //   4. Sino, el estado real reportado por el backend.
    var cls;
    if (!monActivo)                     cls = 's-no-data';
    else if (estado === 'seccion-off')  cls = 's-seccion-off';
    else if (muted)                     cls = 's-muted';
    else                                cls = classFromEstado(estado);
    var tipo   = tipoOf(s);
    var uid    = pick(s, 'Uid', 'uid') || '';
    var cable  = pick(s, 'Cable', 'cable');
    var tag    = '';
    if (tipo === 'semilla')      tag = '<span class="tipo">S</span>';
    else if (tipo === 'fertilizante') tag = '<span class="tipo">F</span>';
    var title  = 'surco ' + bajada + ' · ' + (tipo || '?') + ' · ' + estado;
    return '<div class="vx-chip ' + cls + '" title="' + esc(title) +
             '" data-uid="' + esc(uid) + '" data-cable="' + esc(cable) +
             '" data-bajada="' + esc(bajada) + '">' +
             '<span class="num">' + bajada + '</span>' + tag +
           '</div>';
  }

  // Busca el surco vivo en el último snapshot por (uid, cable).
  function findSurcoVivo(uid, cable) {
    var live = state.lastLive;
    if (!live) return null;
    var trenes = pick(live, 'Trenes', 'trenes') || [];
    for (var i = 0; i < trenes.length; i++) {
      var surcos = pick(trenes[i], 'Surcos', 'surcos') || [];
      for (var j = 0; j < surcos.length; j++) {
        var s = surcos[j];
        var u = pick(s, 'Uid', 'uid') || '';
        var c = pick(s, 'Cable', 'cable');
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
    var s = findSurcoVivo(state.detailFocus.uid, state.detailFocus.cable);
    var ttl = $('vxPopTtl'), body = $('vxPopBody');
    if (!s) {
      ttl.textContent = 'Surco';
      body.innerHTML = '<div class="warn">Sensor no encontrado en el snapshot actual.</div>';
      return;
    }
    var bajada = pick(s, 'Bajada', 'bajada') || 0;
    var tipo   = tipoOf(s);
    var estado = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
    var spm    = pick(s, 'Spm', 'spm');
    var obj    = pick(s, 'Objetivo', 'objetivo');
    var pct    = (spm != null && obj != null && obj > 0) ? Math.round((spm / obj) * 100) : null;
    var muted  = pick(s, 'Muted', 'muted');
    var secOff = pick(s, 'SeccionCortada', 'seccionCortada');
    var tren   = pick(s, 'Tren', 'tren');

    ttl.textContent = 'Surco ' + bajada + (tipo ? ' · ' + tipo : '');

    var html = '';
    html += '<div class="row"><span class="lbl">Estado</span><span>' + esc(labelEstado(estado)) + '</span></div>';
    html += '<div class="row"><span class="lbl">SPM</span><span>' + (spm == null ? '—' : fmt(spm, 0)) +
            (obj == null ? '' : ' / ' + fmt(obj, 0)) + '</span></div>';
    if (pct != null) {
      var pctClamp = Math.max(0, Math.min(150, pct));
      html += '<div class="row"><span class="lbl">% objetivo</span><span>' + pct + '%</span></div>';
      html += '<div class="bar"><i style="width:' + Math.min(100, pctClamp) + '%"></i></div>';
    }
    html += '<div class="row"><span class="lbl">Tren · Cable</span><span>' + esc(tren) + ' · ' + esc(cable) + '</span></div>';
    html += '<div class="row"><span class="lbl">Sección AOG</span><span>' +
            (secOff ? 'cerrada' : 'abierta') + '</span></div>';
    if (muted)  html += '<div class="warn">Sensor muteado — alarmas desactivadas.</div>';
    if (secOff) html += '<div class="warn">Sección AOG cerrada — este surco no sensa.</div>';
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
    var trenes   = pick(live, 'Trenes', 'trenes') || [];
    var spm      = pick(live, 'SpmPromedio', 'spmPromedio');
    var fallas   = pick(live, 'FallasActivas', 'fallasActivas') || 0;
    var vel      = pick(live, 'Velocidad', 'velocidad');
    var hasAlarm = pick(live, 'HasAlarm', 'hasAlarm');
    var monAct   = pick(live, 'MonitoreoActivo', 'monitoreoActivo');

    $('vxSpm').textContent    = (spm == null) ? '—' : fmt(spm, 0);
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
    var motivo = pick(live, 'MotivoDetenido', 'motivoDetenido') || '';
    pill.title = motivo || (monAct ? 'Monitoreando' : '');

    var fbox = $('vxFallasBox');
    if (fbox) fbox.classList.toggle('bad', fallas > 0);

    // Aplanar surcos primarios en orden (tren, bajada).
    var todos = [];
    trenes.forEach(function (t) {
      var surcos = pick(t, 'Surcos', 'surcos') || [];
      surcos.forEach(function (s) { todos.push(s); });
    });
    todos.sort(function (a, b) {
      var ta = pick(a, 'Tren', 'tren') || 0;
      var tb = pick(b, 'Tren', 'tren') || 0;
      if (ta !== tb) return ta - tb;
      return (pick(a, 'Bajada', 'bajada') || 0) - (pick(b, 'Bajada', 'bajada') || 0);
    });

    // Separar primarios en dos sub-listas: semilla / fertilizante.
    var semilla = [], fert = [];
    todos.forEach(function (s) {
      var t = tipoOf(s);
      if (t === 'semilla') semilla.push(s);
      else if (t === 'fertilizante') fert.push(s);
    });

    var strip = $('vxStrip');
    if (semilla.length === 0 && fert.length === 0) {
      strip.innerHTML = '<div class="vx-empty">sin sensores de siembra/fertilización mapeados</div>';
      return;
    }

    var html = semilla.map(function (s) { return renderChip(s, !!monAct); }).join('');
    if (semilla.length > 0 && fert.length > 0) {
      html += '<span class="sep"></span>';
    }
    html += fert.map(function (s) { return renderChip(s, !!monAct); }).join('');
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
      var cur = pick(cfg, 'MetodoInicio', 'metodoInicio') || 'sensores';
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
    // Setear ambas grafías por las dudas del binder server-side.
    dto.MetodoInicio = nuevo;
    dto.metodoInicio = nuevo;
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
    var monAct  = pick(live, 'MonitoreoActivo', 'monitoreoActivo');
    var metodo  = pick(live, 'MetodoInicio', 'metodoInicio') || '—';
    var motivo  = pick(live, 'MotivoDetenido', 'motivoDetenido') || '';
    var vel     = pick(live, 'Velocidad', 'velocidad');
    var velMin  = pick(live, 'VelMinima', 'velMinima');
    var secPint = pick(live, 'SeccionesPintando', 'seccionesPintando');
    var sensArr = pick(live, 'SensoresArriba', 'sensoresArriba');
    var umbral  = pick(live, 'UmbralSensores', 'umbralSensores');

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
    body.innerHTML = html;
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
