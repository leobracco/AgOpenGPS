// ============================================================================
// vistax.js — UI completa del módulo VistaX.
// Tabs:
//   Monitor    → polling /api/vistax/live (2 Hz) — cards por tren con surcos
//   Implemento → /api/vistax/implemento GET/PUT (setup + trenes + mapeo_sensores)
//   Nodos      → toma /api/vistax/live.Nodos
//   Config     → /api/vistax/config GET/PUT (archivos, timeouts, alarmas)
//                Los campos MQTT (broker/topics) quedan ocultos: los maneja CoreX.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }
  function pick(o, a, b) { return (o && o[a] != null) ? o[a] : (o ? o[b] : undefined); }

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // Catálogo de tipos de sensor — se llena async desde /api/vistax/sensor/tipos.
  // Hasta que llegue, usamos un fallback estático para no romper el render inicial.
  var TIPOS_SENSOR = [
    { id: 'semilla',            etiqueta: 'Semilla' },
    { id: 'fertilizante',       etiqueta: 'Fertilizante' },
    { id: 'rotacion_eje',       etiqueta: 'Rotación de eje' },
    { id: 'turbina',            etiqueta: 'Turbina' },
    { id: 'bajada_herramienta', etiqueta: 'Bajada de herramienta' },
    { id: 'tolva_vacia',        etiqueta: 'Tolva vacía' },
    { id: 'tolva_llena',        etiqueta: 'Tolva llena' },
    { id: 'presion',            etiqueta: 'Presión' },
    { id: 'final_carrera',      etiqueta: 'Final de carrera' }
  ];
  (async function loadTiposSensor() {
    try {
      var r = await fetch('/api/vistax/sensor/tipos');
      var arr = await r.json();
      if (Array.isArray(arr) && arr.length) TIPOS_SENSOR = arr;
    } catch (e) { /* fallback estático ya está */ }
  })();

  function renderTipoSelect(actual) {
    var opts = TIPOS_SENSOR.map(function (t) {
      var sel = (t.id === actual) ? ' selected' : '';
      return '<option value="' + escapeHtml(t.id) + '"' + sel + '>' + escapeHtml(t.etiqueta || t.id) + '</option>';
    }).join('');
    return '<select data-f="tipo">' + opts + '</select>';
  }

  function fmtNum(n, d) {
    if (n == null || isNaN(n)) return '–';
    return Number(n).toFixed(d == null ? 1 : d);
  }

  function ageStr(iso) {
    if (!iso) return 'nunca';
    var t = Date.parse(iso);
    if (isNaN(t)) return 'nunca';
    var s = Math.max(0, Math.round((Date.now() - t) / 1000));
    if (s < 60) return s + 's';
    if (s < 3600) return Math.round(s / 60) + 'min';
    return Math.round(s / 3600) + 'h';
  }

  var state = {
    activeTab: 'monitor',
    cfg: null,
    imp: null,
    impPath: '',
    live: null,
    livePollHandle: null,
    // UIDs descubiertos por MQTT — alimenta el <select> del mapeo de sensores.
    // Se actualiza en cada poll de /api/vistax/live a partir de live.Nodos.
    nodos: []
  };

  // Devuelve la lista de UIDs únicos discovered por MQTT. Si state.imp ya
  // referencia un UID que no apareció todavía, igual lo agregamos para que
  // el <select> pueda re-seleccionarlo al cargar la config.
  function knownUids() {
    var out = [], seen = {};
    (state.nodos || []).forEach(function (n) {
      var uid = pick(n, 'Uid', 'uid');
      if (uid && !seen[uid]) { seen[uid] = true; out.push({ uid: uid, online: !!pick(n, 'Online', 'online') }); }
    });
    var sensores = state.imp ? (pick(state.imp, 'MapeoSensores', 'mapeo_sensores') || []) : [];
    sensores.forEach(function (s) {
      var uid = pick(s, 'uid', 'Uid');
      if (uid && !seen[uid]) { seen[uid] = true; out.push({ uid: uid, online: false }); }
    });
    return out;
  }

  // ---------- Tabs ----------

  function showTab(name) {
    state.activeTab = name;
    document.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.getAttribute('data-tab') === name);
    });
    ['monitor', 'implemento', 'nodos', 'config'].forEach(function (k) {
      var el = $('pane' + k.charAt(0).toUpperCase() + k.slice(1));
      if (el) el.classList.toggle('active', k === name);
    });

    if (name === 'implemento' && state.imp == null) loadImplemento();
    if (name === 'config' && state.cfg == null) loadConfig();
  }

  document.querySelectorAll('.tab').forEach(function (t) {
    t.addEventListener('click', function () { showTab(t.getAttribute('data-tab')); });
  });

  // ---------- Monitor ----------

  function renderMonitor(live) {
    state.live = live;
    if (!live) return;

    var trenes = pick(live, 'Trenes', 'trenes') || [];
    var spm = pick(live, 'SpmPromedio', 'spmPromedio');
    var activos = pick(live, 'SurcosActivos', 'surcosActivos') || 0;
    var fallas = pick(live, 'FallasActivas', 'fallasActivas') || 0;
    var hasAlarm = pick(live, 'HasAlarm', 'hasAlarm');
    var alarmMsg = pick(live, 'AlarmMessage', 'alarmMessage') || '';
    var impNombre = pick(live, 'NombreImplemento', 'nombreImplemento') || '–';
    var tol = pick(live, 'ToleranciaDesvio', 'toleranciaDesvio');
    var monActivo = pick(live, 'MonitoreoActivo', 'monitoreoActivo');

    $('vxSubtitle').textContent =
      'Monitoreo de siembra · ' + (impNombre || '–') +
      (tol ? (' · tol ±' + tol + '%') : '');

    var badges = '';
    var okCount = 0, bajoCount = 0, tapCount = 0, excCount = 0, ndCount = 0, muCount = 0;
    trenes.forEach(function (t) {
      var surcos = pick(t, 'Surcos', 'surcos') || [];
      surcos.forEach(function (s) {
        var st = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
        if (st === 'ok') okCount++;
        else if (st === 'bajo' || st === 'bad') bajoCount++;
        else if (st === 'tapado') tapCount++;
        else if (st === 'exceso' || st === 'warn') excCount++;
        else if (st === 'muted') muCount++;
        else ndCount++;
      });
    });
    if (okCount > 0)   badges += '<span class="pill ok"><span class="dot"></span> ' + okCount + ' OK</span>';
    if (bajoCount > 0) badges += '<span class="pill bad"><span class="dot"></span> ' + bajoCount + ' bajo</span>';
    if (tapCount > 0)  badges += '<span class="pill bad"><span class="dot"></span> ' + tapCount + ' tapado</span>';
    if (excCount > 0)  badges += '<span class="pill"><span class="dot" style="background:var(--vx-exceso)"></span> ' + excCount + ' exceso</span>';
    if (muCount > 0)   badges += '<span class="pill"><span class="dot"></span> ' + muCount + ' silenciado</span>';
    if (ndCount > 0)   badges += '<span class="pill"><span class="dot"></span> ' + ndCount + ' sin datos</span>';
    if (!badges)       badges += '<span class="pill"><span class="dot"></span> sin sensores</span>';
    $('vxBadges').innerHTML = badges;

    var html = '';
    if (trenes.length === 0) {
      html = '<div class="empty-hint">Sin trenes mapeados. Configurá el implemento en la pestaña Implemento.</div>';
    } else {
      trenes.forEach(function (t) {
        var tren = pick(t, 'Tren', 'tren');
        var nombre = pick(t, 'Nombre', 'nombre') || ('Tren ' + tren);
        var obj = pick(t, 'Objetivo', 'objetivo');
        var surcos = pick(t, 'Surcos', 'surcos') || [];
        html += '<h2 style="margin-top:var(--agp-sp-4)">' + escapeHtml(nombre);
        if (obj) html += ' <span class="subtitle">· objetivo ' + fmtNum(obj, 0) + ' sem/min</span>';
        html += '</h2>';

        // Split: semilla + cualquier variante de ferti → tubitos
        //        resto (turbina, tolva*, bajada_herramienta, rotacion_eje…) → barras
        var tubitos = [], barras = [];
        surcos.forEach(function (s) {
          var tipo = String(pick(s, 'Tipo', 'tipo') || 'semilla').toLowerCase();
          if (tipo === 'semilla' || tipo.indexOf('ferti') === 0 || tipo === 'fertilizante') {
            tubitos.push(s);
          } else {
            barras.push(s);
          }
        });

        if (tubitos.length > 0) {
          tubitos.sort(function (a, b) {
            return (pick(a, 'Bajada', 'bajada') || 0) - (pick(b, 'Bajada', 'bajada') || 0);
          });
          html += '<div class="tren-sub">Semilla / Fertilizante · ' + tubitos.length + '</div>';
          html += '<div class="sensors">';
          tubitos.forEach(function (s) { html += renderSensorCell(s, obj); });
          html += '</div>';
        }

        if (barras.length > 0) {
          barras.sort(function (a, b) {
            var ta = String(pick(a, 'Tipo', 'tipo') || '');
            var tb = String(pick(b, 'Tipo', 'tipo') || '');
            if (ta !== tb) return ta.localeCompare(tb);
            return (pick(a, 'Bajada', 'bajada') || 0) - (pick(b, 'Bajada', 'bajada') || 0);
          });
          html += '<div class="tren-sub">Otros sensores · ' + barras.length + '</div>';
          html += '<div class="sensors-bars">';
          barras.forEach(function (s) { html += renderSensorBar(s, obj); });
          html += '</div>';
        }
      });
    }
    $('vxTrenes').innerHTML = html;
    bindSensorClicks();
    bindBarObjetivoInputs();

    $('vxSpm').textContent = (spm == null) ? '–' : fmtNum(spm, 1);
    var objMax = 0;
    trenes.forEach(function (t) {
      var o = pick(t, 'Objetivo', 'objetivo') || 0;
      if (o > objMax) objMax = o;
    });
    $('vxObj').textContent = objMax ? (fmtNum(objMax, 0) + ' sem/min') : '–';
    $('vxActivos').textContent = activos;
    $('vxFallas').textContent = fallas;
    $('vxImp').textContent = impNombre;

    var alarm = $('vxAlarm');
    if (hasAlarm) {
      alarm.className = 'pill bad';
      alarm.innerHTML = '<span class="dot"></span> ' + escapeHtml(alarmMsg || 'alarma');
    } else if (!monActivo) {
      alarm.className = 'pill';
      alarm.innerHTML = '<span class="dot"></span> monitor detenido';
    } else {
      alarm.className = 'pill ok';
      alarm.innerHTML = '<span class="dot"></span> sin alarma';
    }

    // Repintar también la grilla de nodos a partir del mismo snapshot.
    renderNodos(live);
  }

  // ---------- Helpers de pintado por sensor ----------
  //
  // Mapeo de estado a estilo. "bajo" se pinta con un degradé negro→verde según
  // ratio real/objetivo (0 = negro, 1 = verde). El resto son colores planos.
  function colorForSurco(surco) {
    var st = (pick(surco, 'Estado', 'estado') || 'no-data').toLowerCase();
    if (st === 'ok')      return 'var(--vx-ok)';
    if (st === 'tapado')  return 'var(--vx-tapado)';
    if (st === 'exceso' || st === 'warn')
      return 'linear-gradient(180deg, var(--vx-exceso), var(--vx-exceso-dark))';
    if (st === 'muted')   return 'var(--vx-muted)';
    if (st === 'no-data') return 'var(--vx-no-data)';
    if (st === 'bajo' || st === 'bad') {
      // Ratio en [0..1]. Usamos linear-gradient con stop intermedio para que
      // el verde aparezca recién cerca del objetivo.
      var r = Math.max(0, Math.min(1, pick(surco, 'RatioObjetivo', 'ratioObjetivo') || 0));
      // tono interpolado manual: black → ok
      var g = Math.round(0x4B * r);
      var rr = Math.round(0x05 + (0x40 - 0x05) * r);
      var bb = Math.round(0x05 + (0x18 - 0x05) * r);
      return 'rgb(' + rr + ',' + (g + 0x0A) + ',' + bb + ')';
    }
    return 'var(--vx-no-data)';
  }

  function renderSensorCell(s, objTren) {
    var st = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
    var b = pick(s, 'Bajada', 'bajada');
    var sp = pick(s, 'Spm', 'spm') || 0;
    var obj = pick(s, 'Objetivo', 'objetivo') || objTren || 0;
    var uid = pick(s, 'Uid', 'uid') || '';
    var cable = pick(s, 'Cable', 'cable');
    var muted = !!pick(s, 'Muted', 'muted');
    var label = (st === 'no-data') ? 'sin señal'
              : (st === 'tapado')  ? 'TAPADO'
              : (st === 'bajo' || st === 'bad') ? 'bajo objetivo'
              : (st === 'exceso' || st === 'warn') ? 'exceso'
              : (st === 'muted') ? 'silenciado'
              : 'sem/min';
    var cls = 's-' + st;
    var bg = colorForSurco(s);
    var spmTxt = fmtNum(sp, sp >= 100 ? 0 : 1);
    var objTxt = obj ? fmtNum(obj, 0) : '–';
    var badge = muted ? '<span class="badge muted">MUTE</span>' :
                (st === 'tapado' ? '<span class="badge">!</span>' : '');
    return '' +
      '<div class="sensor ' + cls + '" ' +
      'data-uid="' + escapeHtml(uid) + '" data-cable="' + (cable == null ? '' : cable) + '" ' +
      'data-muted="' + (muted ? '1' : '0') + '" ' +
      'data-bajada="' + escapeHtml(b == null ? '' : b) + '">' +
        '<div class="tube" style="background:' + bg + '"></div>' +
        badge +
        '<div class="id">B' + escapeHtml(b == null ? '–' : b) + '</div>' +
        '<div class="pps">' + spmTxt + '</div>' +
        '<div class="obj">real <span class="sep">·</span> obj ' + objTxt + '</div>' +
        '<div class="row2">' + label + '</div>' +
      '</div>';
  }

  // --- Barras horizontales (sensores no semilla/ferti) ----------------
  // Render: nombre + tipo + valor + barra horizontal (fill % por ratio)
  // + input editable de objetivo (POST a /api/vistax/sensor/config).
  // El input lleva data-* con el sensor identity para que el handler
  // pueda armar el upsert con todos los campos requeridos.
  function renderSensorBar(s, objTren) {
    var st = (pick(s, 'Estado', 'estado') || 'no-data').toLowerCase();
    var b = pick(s, 'Bajada', 'bajada');
    var sp = pick(s, 'Spm', 'spm') || 0;
    var obj = pick(s, 'Objetivo', 'objetivo') || 0; // 0 = hereda del tren
    var ratio = (obj > 0) ? Math.max(0, Math.min(1.3, sp / obj)) : 0;
    var fillPct = Math.round(ratio * 100);
    if (st === 'no-data') fillPct = 0;
    if (st === 'tapado') fillPct = Math.max(fillPct, 6); // hint visual
    var uid = pick(s, 'Uid', 'uid') || '';
    var cable = pick(s, 'Cable', 'cable');
    var muted = !!pick(s, 'Muted', 'muted');
    var tipo = pick(s, 'Tipo', 'tipo') || '';
    var tren = pick(s, 'Tren', 'tren') || 0;

    var nombreTipo = tipo
      .replace('bajada_herramienta', 'Bajada herr.')
      .replace('rotacion_eje', 'Rotación eje')
      .replace('tolva_vacia', 'Tolva vacía')
      .replace('tolva_llena', 'Tolva llena')
      .replace('final_carrera', 'Final carrera')
      .replace(/^./, function (c) { return c.toUpperCase(); });

    var estadoTxt = (st === 'no-data') ? 'sin señal'
                  : (st === 'tapado')  ? 'TAPADO'
                  : (st === 'bajo' || st === 'bad') ? 'BAJO'
                  : (st === 'exceso' || st === 'warn') ? 'EXCESO'
                  : (st === 'muted') ? 'silenciado'
                  : 'OK';

    var valTxt = fmtNum(sp, sp >= 100 ? 0 : 1);
    var objTxt = obj > 0 ? fmtNum(obj, 0) : '–';

    // Label: tipo + (Bajada/cable si corresponde)
    var label = escapeHtml(nombreTipo || 'Sensor');
    var idTag = (b != null && b !== 0)
      ? (' B' + escapeHtml(String(b)))
      : (cable ? (' cable ' + escapeHtml(String(cable))) : '');

    return '' +
      '<div class="bar-sensor s-' + st + '" ' +
        'data-uid="' + escapeHtml(uid) + '" ' +
        'data-cable="' + (cable == null ? '' : cable) + '" ' +
        'data-muted="' + (muted ? '1' : '0') + '" ' +
        'data-bajada="' + escapeHtml(b == null ? '' : String(b)) + '" ' +
        'data-tren="' + escapeHtml(String(tren)) + '" ' +
        'data-tipo="' + escapeHtml(tipo) + '">' +
        '<div class="bs-head">' +
          '<div class="bs-title">' + label + '<span class="tipo">' + escapeHtml(idTag) + '</span></div>' +
          '<div class="bs-value">' + valTxt + '</div>' +
        '</div>' +
        '<div class="bar-track"><div class="bar-fill" style="width:' + fillPct + '%"></div></div>' +
        '<div class="bs-foot">' +
          '<span class="estado">' + estadoTxt + '</span>' +
          '<span class="obj-edit" onclick="event.stopPropagation()">' +
            'obj <input type="number" min="0" step="1" class="bar-obj-input" ' +
                  'value="' + (obj > 0 ? obj : '') + '" ' +
                  'placeholder="' + (objTren > 0 ? fmtNum(objTren, 0) : '—') + '" />' +
            '<span class="unit">/min</span>' +
          '</span>' +
        '</div>' +
      '</div>';
  }

  // Wire de los inputs de objetivo en cada barra. El operario edita el valor
  // y al perder foco (blur) o presionar Enter se postea al endpoint upsert.
  function bindBarObjetivoInputs() {
    var inputs = document.querySelectorAll('#vxTrenes .bar-obj-input');
    inputs.forEach(function (inp) {
      // Anti-reentrancia: no atrapar el click en la celda padre.
      inp.addEventListener('click', function (e) { e.stopPropagation(); });
      inp.addEventListener('focus', function () { inp.dataset.dirty = '0'; });
      inp.addEventListener('input', function () { inp.dataset.dirty = '1'; });
      inp.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); inp.blur(); }
        if (e.key === 'Escape') { e.preventDefault(); inp.value = inp.defaultValue; inp.dataset.dirty = '0'; inp.blur(); }
      });
      inp.addEventListener('blur', function () {
        if (inp.dataset.dirty !== '1') return;
        var cell = inp.closest('.bar-sensor');
        if (!cell) return;
        var nuevo = parseFloat(inp.value || '0');
        if (isNaN(nuevo) || nuevo < 0) nuevo = 0;
        saveSensorObjetivo(cell, nuevo, inp);
      });
    });
  }

  async function saveSensorObjetivo(cell, nuevoObjetivo, inp) {
    var uid = cell.getAttribute('data-uid') || '';
    var cable = parseInt(cell.getAttribute('data-cable') || '0', 10) || 0;
    var bajada = parseInt(cell.getAttribute('data-bajada') || '0', 10) || 0;
    var tren = parseInt(cell.getAttribute('data-tren') || '0', 10) || 0;
    var tipo = cell.getAttribute('data-tipo') || 'semilla';
    if (!uid) return;
    try {
      var res = await fetch('/api/vistax/sensor/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          uid: uid,
          cable: cable,
          pin: 0,
          bajada: bajada,
          tren: tren,
          tipo: tipo,
          nombre: '',
          is_active: true,
          surco_desde: 0,
          surco_hasta: 0,
          seccion_aog: 0,
          objetivo: nuevoObjetivo
        })
      });
      if (res.ok) {
        inp.dataset.dirty = '0';
        // Flash visual breve.
        inp.classList.add('ok-flash');
        setTimeout(function () { inp.classList.remove('ok-flash'); }, 800);
        // Forzar refresh del snapshot para que la próxima poll ya use el nuevo obj.
        pollLive();
      }
    } catch (_) { /* silencioso — UI sigue funcionando */ }
  }

  // Cierra el menú contextual abierto (si hay).
  function closeMenu() {
    var m = document.getElementById('vxMenu');
    if (m && m.parentNode) m.parentNode.removeChild(m);
  }
  document.addEventListener('click', function (e) {
    var m = document.getElementById('vxMenu');
    if (m && !m.contains(e.target)) closeMenu();
  });

  function showMenu(x, y, cellData) {
    closeMenu();
    var menu = document.createElement('div');
    menu.id = 'vxMenu';
    menu.className = 'vx-menu';
    var muted = cellData.muted;
    menu.innerHTML =
      '<div class="head">Sensor ' + escapeHtml(cellData.uid || '—') +
        ' · cable ' + escapeHtml(cellData.cable) +
        (cellData.bajada ? ' · B' + escapeHtml(cellData.bajada) : '') + '</div>' +
      '<div class="item" data-act="mute">' + (muted ? '🔔 Reactivar sensor' : '🔕 Silenciar sensor') + '</div>';
    document.body.appendChild(menu);
    // Posicionar dentro del viewport
    var w = menu.offsetWidth, h = menu.offsetHeight;
    var px = Math.min(window.innerWidth - w - 8, Math.max(8, x));
    var py = Math.min(window.innerHeight - h - 8, Math.max(8, y));
    menu.style.left = px + 'px';
    menu.style.top = py + 'px';
    menu.querySelector('[data-act=mute]').addEventListener('click', function () {
      toggleMute(cellData.uid, cellData.cable, !muted);
      closeMenu();
    });
  }

  function bindSensorClicks() {
    var cells = document.querySelectorAll('#vxTrenes .sensor');
    cells.forEach(function (cell) {
      cell.addEventListener('click', function (ev) {
        ev.stopPropagation();
        showMenu(ev.clientX, ev.clientY, {
          uid: cell.getAttribute('data-uid') || '',
          cable: parseInt(cell.getAttribute('data-cable') || '0', 10),
          bajada: cell.getAttribute('data-bajada') || '',
          muted: cell.getAttribute('data-muted') === '1'
        });
      });
    });
  }

  async function toggleMute(uid, cable, muted) {
    if (!uid) return;
    try {
      var res = await fetch('/api/vistax/sensor/mute', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ uid: uid, cable: cable, muted: !!muted })
      });
      if (res.ok) pollLive(); // refrescar grilla inmediatamente
    } catch (e) {
      // silencioso — el siguiente tick reflejará el estado real
    }
  }

  async function pollLive() {
    try {
      var live = await window.agpApi.get('vistax/live');
      renderMonitor(live);
    } catch (e) {
      // El servicio puede no estar disponible — degradación silenciosa.
    }
  }

  function startLivePolling() {
    if (state.livePollHandle) return;
    pollLive();
    state.livePollHandle = setInterval(pollLive, 500);
  }

  // ---------- Nodos ----------

  function renderNodos(live) {
    var nodos = (live && (pick(live, 'Nodos', 'nodos'))) || [];
    // Mantener un datalist global con todos los UIDs vistos para autocompletar
    // el mapeo de sensores. Se actualiza en cada poll. Si más adelante el nodo
    // se desconecta, el último UID conocido queda en el datalist hasta el
    // próximo cambio de snapshot — está bien, el operario sigue pudiendo
    // mapear sensores aunque el nodo esté momentáneamente offline.
    updateNodosDatalist(nodos);
    if (nodos.length === 0) {
      $('vxNodos').innerHTML = '<div class="empty-hint">Sin nodos VistaX vistos todavía.</div>';
      return;
    }
    var html = '';
    nodos.forEach(function (n) {
      var uid = pick(n, 'Uid', 'uid') || '–';
      var online = pick(n, 'Online', 'online');
      var sensors = pick(n, 'SensorsReporting', 'sensorsReporting') || 0;
      var last = pick(n, 'LastSeenIso', 'lastSeenIso');
      html += '<div class="nodo-card">' +
              '<div class="uid">' + escapeHtml(uid) + '</div>' +
              '<div style="margin-top:var(--agp-sp-2)">' +
              (online ? '<span class="pill ok"><span class="dot"></span> online</span>'
                      : '<span class="pill bad"><span class="dot"></span> offline</span>') +
              ' <span class="subtitle">' + sensors + ' sensores</span></div>' +
              '<div class="last">hace ' + ageStr(last) + '</div>' +
              '</div>';
    });
    $('vxNodos').innerHTML = html;
  }

  // Guarda los nodos vistos por MQTT en state.nodos y refresca los <select>
  // de UID en las filas de mapeo (si el tab Implemento está visible). Sin
  // datalist: el operario elige de un dropdown, no tipea.
  function updateNodosDatalist(nodos) {
    state.nodos = nodos || [];
    // Si la tabla de mapeo está renderizada, refrescar opciones de cada fila
    // sin perder la selección actual (se preserva el value).
    var rows = document.querySelectorAll('#tblSensores tbody tr');
    if (!rows.length) return;
    rows.forEach(function (tr) {
      var sel = tr.querySelector('select[data-f="uid"]');
      if (!sel) return;
      var current = sel.value;
      sel.innerHTML = buildUidOptions(current);
    });
  }

  // Genera <option>s para el <select> de UID en una fila de mapeo.
  // Si `current` no aparece en la lista discovered, se incluye igual al final
  // para no perderlo al recargar el implemento.
  function buildUidOptions(current) {
    var html = '<option value="">— elegir nodo —</option>';
    var uids = knownUids();
    var hasCurrent = !current || uids.some(function (u) { return u.uid === current; });
    uids.forEach(function (u) {
      var sel = (u.uid === current) ? ' selected' : '';
      var tag = u.online ? ' (online)' : ' (offline)';
      html += '<option value="' + escapeHtml(u.uid) + '"' + sel + '>' +
              escapeHtml(u.uid) + tag + '</option>';
    });
    if (!hasCurrent && current) {
      html += '<option value="' + escapeHtml(current) + '" selected>' +
              escapeHtml(current) + ' (no visto)</option>';
    }
    return html;
  }

  // ---------- Implemento ----------

  async function loadImplemento() {
    try {
      // Geometría central — fuente única de verdad (Herramienta).
      try {
        var r = await fetch('/api/implemento', { cache: 'no-store' });
        var dCentral = await r.json();
        state.implCentral = (dCentral && dCentral.ok) ? dCentral.implemento : null;
      } catch (_) { state.implCentral = null; }

      // DTO VistaX: tolerancia + mapeo de sensores (lo VistaX-específico).
      var res = await window.agpApi.get('vistax/implemento');
      state.imp = pick(res, 'Implemento', 'implemento') || res.implemento || res;
      state.impPath = pick(res, 'Path', 'path') || '';
      paintImplemento();
    } catch (e) {
      $('impStatus').textContent = 'No se pudo cargar el implemento';
    }
  }

  function paintImplemento() {
    var i = state.imp || {};
    var setup = pick(i, 'Setup', 'setup') || {};
    var central = state.implCentral || {};

    // Banner: resumen read-only desde el implemento central.
    var anchoC = central.ancho_total_m || 0;
    var nSurcosC = central.numero_surcos || (Array.isArray(central.surcos) ? central.surcos.length : 0);
    var distC = central.distancia_entre_surcos_m || 0;
    var nTrenesC = Array.isArray(central.trenes) ? central.trenes.length : 0;
    var el;
    if ((el = $('vxImplAncho')))     el.textContent = anchoC ? anchoC.toFixed(2) : '–';
    if ((el = $('vxImplNumSurcos'))) el.textContent = nSurcosC || '–';
    if ((el = $('vxImplDistSurcos')))el.textContent = distC ? distC.toFixed(3) : '–';
    if ((el = $('vxImplNumTrenes'))) el.textContent = nTrenesC || '–';

    // Único parámetro editable del VistaX a este nivel: tolerancia.
    $('impTol').value = pick(setup, 'tolerancia_desvio', 'ToleranciaDesvio') ?? 0;

    // Torres (agrupado opcional). 0 = sin agrupar; vista_modo_default decide
    // qué muestra el overlay live por defecto. El operario puede cambiar
    // en runtime (la preferencia runtime vive en localStorage).
    var torres = pick(setup, 'torres', 'Torres') ?? 0;
    var spt    = pick(setup, 'surcos_por_torre', 'SurcosPorTorre') ?? 0;
    var modo   = pick(setup, 'vista_modo_default', 'VistaModoDefault') || 'surcos';
    if ($('impTorres'))      $('impTorres').value = torres | 0;
    if ($('impSurcosTorre')) $('impSurcosTorre').value = spt | 0;
    if ($('impVistaModo'))   $('impVistaModo').value = (modo === 'torres' ? 'torres' : 'surcos');

    // Trenes para el dropdown del mapeo de sensores: vienen del central.
    var trenesCentral = Array.isArray(central.trenes) && central.trenes.length
      ? central.trenes
      : [{ id: 0, nombre: 'Tren 0' }];

    var sensores = pick(i, 'MapeoSensores', 'mapeo_sensores') || [];
    var sBody = document.querySelector('#tblSensores tbody');
    sBody.innerHTML = sensores.map(function (s, idx) {
      return renderSensorRow(s, idx, trenesCentral);
    }).join('');
    sBody.querySelectorAll('.btn-del-sensor').forEach(function (b) {
      b.addEventListener('click', function (e) {
        var tr = e.target.closest('tr'); tr.parentNode.removeChild(tr);
      });
    });

    var path = state.impPath ? (' · ' + state.impPath) : '';
    $('impStatus').textContent = 'Cargado' + path;
  }

  function renderSensorRow(s, idx, trenes) {
    var trenSel = (pick(s, 'tren', 'Tren') ?? 0) | 0;
    var trenOpts = trenes.map(function (t) {
      var sel = ((t.id | 0) === trenSel) ? ' selected' : '';
      return '<option value="' + (t.id | 0) + '"' + sel + '>' + escapeHtml(t.nombre || ('Tren ' + t.id)) + '</option>';
    }).join('');
    var uidVal = pick(s, 'uid', 'Uid') || '';
    return '<tr data-idx="' + idx + '">' +
           '<td><select data-f="uid">' + buildUidOptions(uidVal) + '</select></td>' +
           '<td><input type="number" data-f="cable" value="' + (pick(s, 'cable', 'Cable') ?? 0) + '" /></td>' +
           '<td><input type="number" data-f="bajada" value="' + (pick(s, 'bajada', 'Bajada') ?? 0) + '" /></td>' +
           '<td><select data-f="tren">' + trenOpts + '</select></td>' +
           '<td>' + renderTipoSelect(pick(s, 'tipo', 'Tipo') || 'semilla') + '</td>' +
           '<td><input type="checkbox" data-f="is_active" ' + (pick(s, 'is_active', 'IsActive') !== false ? 'checked' : '') + ' /></td>' +
           '<td class="actions"><button class="btn small btn-del-sensor">×</button></td>' +
           '</tr>';
  }

  function readImplementoFromForm() {
    var central = state.implCentral || {};
    var prevImp = state.imp || {};
    var prevSetup = pick(prevImp, 'Setup', 'setup') || {};

    // Geometría y trenes vienen del IMPLEMENTO CENTRAL (no se editan acá).
    var anchoC = central.ancho_total_m || 0;
    var nSurcosC = central.numero_surcos || (Array.isArray(central.surcos) ? central.surcos.length : 0);
    var distC = central.distancia_entre_surcos_m || 0;
    var nSecC = Array.isArray(central.secciones) ? central.secciones.length : 0;
    var trenesC = (Array.isArray(central.trenes) ? central.trenes : []).map(function (t) {
      // Mantener compat con VistaXTrenConfigDto: id/nombre/surcos.
      // "surcos" = cantidad de surcos asignados a ese tren en el central.
      var nSur = 0;
      if (Array.isArray(central.surcos)) {
        central.surcos.forEach(function (s) { if ((s.tren_id | 0) === (t.id | 0)) nSur++; });
      }
      return { id: t.id | 0, nombre: t.nombre || ('Tren ' + t.id), surcos: nSur };
    });

    var rowsSens = Array.from(document.querySelectorAll('#tblSensores tbody tr'));
    var sensores = rowsSens.map(function (tr) {
      function f(name) { return tr.querySelector('[data-f="' + name + '"]'); }
      // Pin y nombre ya no se editan desde la UI:
      //  · pin: el mapeo a hardware del nodo es interno (se deriva del cable).
      //  · nombre: redundante con UID + bajada — etiquetado por surco no aporta.
      // Mantenemos los campos en el DTO con valores neutros para no romper
      // VistaXSensorConfigDto ni la persistencia.
      return {
        uid: (f('uid').value || ''),
        cable: parseInt(f('cable').value || '0', 10) || 0,
        pin: 0,
        bajada: parseInt(f('bajada').value || '0', 10) || 0,
        tren: parseInt(f('tren').value || '0', 10) || 0,
        tipo: (f('tipo').value || 'semilla'),
        nombre: '',
        is_active: f('is_active').checked,
        surco_desde: 0,
        surco_hasta: 0,
        seccion_aog: 0
      };
    });

    return {
      // id/nombre del implemento: heredan del previo (no editable acá).
      id: pick(prevImp, 'Id', 'id') || '',
      nombre: pick(prevImp, 'Nombre', 'nombre') || central.nombre || '',
      setup: {
        // Densidad / factor K viven en el catálogo de insumos. Preservamos
        // lo que ya estaba en el DTO previo para no pisar nada.
        densidad_objetivo: pick(prevSetup, 'densidad_objetivo', 'DensidadObjetivo') ?? 0,
        tolerancia_desvio: parseFloat($('impTol').value || '0') || 0,
        distancia_entre_surcos: distC,
        factor_k_default: pick(prevSetup, 'factor_k_default', 'FactorK') ?? 0,
        objetivos_tren: pick(prevSetup, 'objetivos_tren', 'ObjetivosTren') || {},
        total_surcos: nSurcosC,
        secciones_aog: nSecC,
        ancho_implemento: anchoC,
        // Torres: agrupado opcional para la vista live.
        torres: parseInt(($('impTorres') && $('impTorres').value) || '0', 10) || 0,
        surcos_por_torre: parseInt(($('impSurcosTorre') && $('impSurcosTorre').value) || '0', 10) || 0,
        vista_modo_default: (($('impVistaModo') && $('impVistaModo').value) === 'torres' ? 'torres' : 'surcos'),
        // Preservamos resto del setup que no editamos acá.
        max_densidad_sensor: pick(prevSetup, 'max_densidad_sensor', 'MaxDensidadSensor') ?? 20,
        insumo_activo_id: pick(prevSetup, 'insumo_activo_id', 'InsumoActivoId') || ''
      },
      trenes: trenesC,
      mapeo_sensores: sensores
    };
  }

  async function saveImplemento() {
    var dto = readImplementoFromForm();
    $('impStatus').textContent = 'Guardando…';
    try {
      var r = await fetch('/api/vistax/implemento', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dto)
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      $('impStatus').textContent = 'Guardado ✓';
    } catch (e) {
      $('impStatus').textContent = 'Error al guardar: ' + e.message;
    }
  }

  $('btnAddSensor').addEventListener('click', function () {
    var body = document.querySelector('#tblSensores tbody');
    var trenes = (state.implCentral && Array.isArray(state.implCentral.trenes) && state.implCentral.trenes.length)
      ? state.implCentral.trenes
      : [{ id: 0, nombre: 'Tren 0' }];
    // Pre-rellenar UID con el primer nodo online discovered. Si hay varios,
    // el operario usa el datalist para elegir. Esto evita el caso "tengo
    // un solo nodo arriba y aun así me pide tipearlo".
    var seed = {};
    try {
      var nodos = (state.live && (state.live.Nodos || state.live.nodos)) || [];
      var defaultUid = '';
      for (var i = 0; i < nodos.length; i++) {
        var n = nodos[i];
        if (pick(n, 'Online', 'online')) { defaultUid = pick(n, 'Uid', 'uid') || ''; break; }
      }
      if (!defaultUid && nodos.length > 0) defaultUid = pick(nodos[0], 'Uid', 'uid') || '';
      if (defaultUid) seed.uid = defaultUid;
    } catch (_) { /* sin live snapshot aún */ }
    var tr = document.createElement('tr');
    tr.innerHTML = renderSensorRow(seed, body.children.length, trenes).replace(/^<tr[^>]*>|<\/tr>$/g, '');
    body.appendChild(tr);
    tr.querySelector('.btn-del-sensor').addEventListener('click', function (e) {
      var row = e.target.closest('tr'); row.parentNode.removeChild(row);
    });
  });

  $('btnSaveImplemento').addEventListener('click', saveImplemento);
  $('btnReloadImplemento').addEventListener('click', loadImplemento);

  // ---------- Config ----------

  async function loadConfig() {
    try {
      var cfg = await window.agpApi.get('vistax/config');
      state.cfg = cfg;
      paintConfig();
    } catch (e) {
      $('cfgStatus').textContent = 'No se pudo cargar la configuración';
    }
  }

  function paintConfig() {
    var c = state.cfg || {};
    $('cfgImpPath').value = pick(c, 'ImplementoJsonPath', 'implementoJsonPath') || '';
    $('cfgUiMs').value = pick(c, 'UiUpdateIntervalMs', 'uiUpdateIntervalMs') || 500;
    $('cfgTimeoutMs').value = pick(c, 'SensorTimeoutMs', 'sensorTimeoutMs') || 3000;
    $('cfgLogField').checked = !!pick(c, 'LogToFieldRecord', 'logToFieldRecord');
    $('cfgMetodo').value = pick(c, 'MetodoInicio', 'metodoInicio') || 'sensores';
    $('cfgUmbral').value = pick(c, 'UmbralSensoresActivos', 'umbralSensoresActivos') || 3;
    $('cfgTConf').value = pick(c, 'TiempoConfirmacionMs', 'tiempoConfirmacionMs') || 500;
    $('cfgMuted').checked = !!pick(c, 'AlarmMuted', 'alarmMuted');
    $('cfgLogDrive').value = pick(c, 'LogOutputDrive', 'logOutputDrive') || '';
    $('cfgStatus').textContent = 'Cargada';
  }

  // Mezcla los campos editados con state.cfg para preservar BrokerAddress/topics
  // etc. que ya no se muestran en la UI (el broker MQTT lo maneja CoreX).
  function readConfigFromForm() {
    var c = state.cfg || {};
    var get = function (a, b, def) {
      var v = pick(c, a, b);
      return v == null ? def : v;
    };
    return {
      Enabled: get('Enabled', 'enabled', true),
      BrokerAddress: get('BrokerAddress', 'brokerAddress', '127.0.0.1'),
      BrokerPort: get('BrokerPort', 'brokerPort', 1883),
      ClientId: get('ClientId', 'clientId', 'PilotX_VistaX'),
      Username: get('Username', 'username', ''),
      Password: get('Password', 'password', ''),
      UseTls: get('UseTls', 'useTls', false),
      TelemetriaTopic: get('TelemetriaTopic', 'telemetriaTopic', 'vistax/nodos/telemetria'),
      SpeedTopic: get('SpeedTopic', 'speedTopic', 'aog/machine/speed'),
      SectionsTopic: get('SectionsTopic', 'sectionsTopic', 'sections/state'),
      ImplementoJsonPath: $('cfgImpPath').value || '',
      UiUpdateIntervalMs: parseInt($('cfgUiMs').value || '500', 10) || 500,
      SensorTimeoutMs: parseInt($('cfgTimeoutMs').value || '3000', 10) || 3000,
      LogToFieldRecord: $('cfgLogField').checked,
      MetodoInicio: $('cfgMetodo').value || 'sensores',
      UmbralSensoresActivos: parseInt($('cfgUmbral').value || '3', 10) || 3,
      TiempoConfirmacionMs: parseInt($('cfgTConf').value || '500', 10) || 500,
      AlarmMuted: $('cfgMuted').checked,
      LogOutputDrive: $('cfgLogDrive').value || ''
    };
  }

  async function saveConfig() {
    var dto = readConfigFromForm();
    $('cfgStatus').textContent = 'Guardando…';
    try {
      var r = await fetch('/api/vistax/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dto)
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      $('cfgStatus').textContent = 'Guardada ✓';
    } catch (e) {
      $('cfgStatus').textContent = 'Error al guardar: ' + e.message;
    }
  }

  $('btnSaveConfig').addEventListener('click', saveConfig);
  $('btnReloadConfig').addEventListener('click', loadConfig);

  // ---------- Boot ----------

  startLivePolling();
})();
