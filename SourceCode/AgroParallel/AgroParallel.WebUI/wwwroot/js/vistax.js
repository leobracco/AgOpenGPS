// ============================================================================
// vistax.js — UI completa del módulo VistaX.
// Tabs:
//   Monitor    → polling /api/vistax/live (2 Hz) — cards por tren con surcos
//   Implemento → /api/vistax/implemento GET/PUT (setup + trenes + mapeo_sensores)
//   Nodos      → toma /api/vistax/live.Nodos
//   Config     → /api/vistax/config GET/PUT (broker, topics, timeouts)
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
    livePollHandle: null
  };

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
    var okCount = 0, warnCount = 0, badCount = 0, ndCount = 0;
    trenes.forEach(function (t) {
      var surcos = pick(t, 'Surcos', 'surcos') || [];
      surcos.forEach(function (s) {
        var st = pick(s, 'Estado', 'estado');
        if (st === 'ok') okCount++;
        else if (st === 'warn') warnCount++;
        else if (st === 'bad') badCount++;
        else ndCount++;
      });
    });
    if (okCount > 0)   badges += '<span class="pill ok"><span class="dot"></span> ' + okCount + ' OK</span>';
    if (warnCount > 0) badges += '<span class="pill warn"><span class="dot"></span> ' + warnCount + ' marginal</span>';
    if (badCount > 0)  badges += '<span class="pill bad"><span class="dot"></span> ' + badCount + ' falla</span>';
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
        html += '<div class="sensors">';
        surcos.sort(function (a, b) {
          return (pick(a, 'Bajada', 'bajada') || 0) - (pick(b, 'Bajada', 'bajada') || 0);
        });
        surcos.forEach(function (s) {
          var st = pick(s, 'Estado', 'estado') || 'no-data';
          var b = pick(s, 'Bajada', 'bajada');
          var sp = pick(s, 'Spm', 'spm') || 0;
          var label = (st === 'no-data') ? 'sin señal'
                    : (st === 'bad')     ? 'falla'
                    : (st === 'warn')    ? 'marginal'
                    : 'sem/min';
          html += '<div class="sensor ' + escapeHtml(st) + '">' +
                  '<div class="id">B' + escapeHtml(b == null ? '–' : b) + '</div>' +
                  '<div class="pps">' + fmtNum(sp, sp >= 100 ? 0 : 1) + '</div>' +
                  '<div class="row2">' + label + '</div>' +
                  '</div>';
        });
        html += '</div>';
      });
    }
    $('vxTrenes').innerHTML = html;

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

  // ---------- Implemento ----------

  async function loadImplemento() {
    try {
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
    $('impNombre').value = pick(i, 'Nombre', 'nombre') || '';
    $('impId').value = pick(i, 'Id', 'id') || '';
    $('impDensidad').value = pick(setup, 'densidad_objetivo', 'DensidadObjetivo') ?? 0;
    $('impTol').value = pick(setup, 'tolerancia_desvio', 'ToleranciaDesvio') ?? 0;
    $('impDistSurcos').value = pick(setup, 'distancia_entre_surcos', 'DistanciaEntreSurcos') ?? 0;
    $('impFactorK').value = pick(setup, 'factor_k_default', 'FactorK') ?? 0;
    $('impTotalSurcos').value = pick(setup, 'total_surcos', 'TotalSurcos') ?? 0;
    $('impSeccAOG').value = pick(setup, 'secciones_aog', 'SeccionesAOG') ?? 0;
    $('impAncho').value = pick(setup, 'ancho_implemento', 'AnchoImplemento') ?? 0;

    var trenes = pick(i, 'Trenes', 'trenes') || [];
    var objetivos = pick(setup, 'objetivos_tren', 'ObjetivosTren') || {};

    var trBody = document.querySelector('#tblTrenes tbody');
    var trHtml = '';
    trenes.forEach(function (t, idx) {
      var id = pick(t, 'id', 'Id');
      var obj = (objetivos && objetivos[String(id)] != null) ? objetivos[String(id)]
              : (pick(setup, 'densidad_objetivo', 'DensidadObjetivo') ?? 0);
      trHtml += '<tr data-idx="' + idx + '">' +
                '<td><input type="number" data-f="id" value="' + (id ?? 0) + '" /></td>' +
                '<td><input type="text" data-f="nombre" value="' + escapeHtml(pick(t, 'nombre', 'Nombre') || '') + '" /></td>' +
                '<td><input type="number" data-f="surcos" value="' + (pick(t, 'surcos', 'Surcos') ?? 0) + '" /></td>' +
                '<td><input type="number" data-f="objetivo" step="0.1" value="' + obj + '" /></td>' +
                '<td class="actions"><button class="btn small btn-del-tren">×</button></td>' +
                '</tr>';
    });
    trBody.innerHTML = trHtml;
    trBody.querySelectorAll('.btn-del-tren').forEach(function (b) {
      b.addEventListener('click', function (e) {
        var tr = e.target.closest('tr'); tr.parentNode.removeChild(tr);
      });
    });

    var sensores = pick(i, 'MapeoSensores', 'mapeo_sensores') || [];
    var sBody = document.querySelector('#tblSensores tbody');
    var sHtml = '';
    sensores.forEach(function (s, idx) {
      sHtml += '<tr data-idx="' + idx + '">' +
               '<td><input type="text" data-f="uid" value="' + escapeHtml(pick(s, 'uid', 'Uid') || '') + '" /></td>' +
               '<td><input type="number" data-f="cable" value="' + (pick(s, 'cable', 'Cable') ?? 0) + '" /></td>' +
               '<td><input type="number" data-f="pin" value="' + (pick(s, 'pin', 'Pin') ?? 0) + '" /></td>' +
               '<td><input type="number" data-f="bajada" value="' + (pick(s, 'bajada', 'Bajada') ?? 0) + '" /></td>' +
               '<td><input type="number" data-f="tren" value="' + (pick(s, 'tren', 'Tren') ?? 1) + '" /></td>' +
               '<td><input type="text" data-f="tipo" value="' + escapeHtml(pick(s, 'tipo', 'Tipo') || 'semilla') + '" /></td>' +
               '<td><input type="text" data-f="nombre" value="' + escapeHtml(pick(s, 'nombre', 'Nombre') || '') + '" /></td>' +
               '<td><input type="checkbox" data-f="is_active" ' + (pick(s, 'is_active', 'IsActive') !== false ? 'checked' : '') + ' /></td>' +
               '<td class="actions"><button class="btn small btn-del-sensor">×</button></td>' +
               '</tr>';
    });
    sBody.innerHTML = sHtml;
    sBody.querySelectorAll('.btn-del-sensor').forEach(function (b) {
      b.addEventListener('click', function (e) {
        var tr = e.target.closest('tr'); tr.parentNode.removeChild(tr);
      });
    });

    var path = state.impPath ? (' · ' + state.impPath) : '';
    $('impStatus').textContent = 'Cargado' + path;
  }

  function readImplementoFromForm() {
    var rowsTrenes = Array.from(document.querySelectorAll('#tblTrenes tbody tr'));
    var trenes = rowsTrenes.map(function (tr) {
      var inputs = tr.querySelectorAll('input');
      return {
        id: parseInt(inputs[0].value || '0', 10) || 0,
        nombre: inputs[1].value || '',
        surcos: parseInt(inputs[2].value || '0', 10) || 0
      };
    });

    var objetivosTren = {};
    rowsTrenes.forEach(function (tr) {
      var inputs = tr.querySelectorAll('input');
      var id = parseInt(inputs[0].value || '0', 10) || 0;
      var obj = parseFloat(inputs[3].value || '0') || 0;
      if (id) objetivosTren[String(id)] = obj;
    });

    var rowsSens = Array.from(document.querySelectorAll('#tblSensores tbody tr'));
    var sensores = rowsSens.map(function (tr) {
      var ins = tr.querySelectorAll('input');
      return {
        uid: ins[0].value || '',
        cable: parseInt(ins[1].value || '0', 10) || 0,
        pin: parseInt(ins[2].value || '0', 10) || 0,
        bajada: parseInt(ins[3].value || '0', 10) || 0,
        tren: parseInt(ins[4].value || '1', 10) || 1,
        tipo: ins[5].value || 'semilla',
        nombre: ins[6].value || '',
        is_active: ins[7].checked,
        surco_desde: 0,
        surco_hasta: 0,
        seccion_aog: 0
      };
    });

    return {
      id: $('impId').value || '',
      nombre: $('impNombre').value || '',
      setup: {
        densidad_objetivo: parseFloat($('impDensidad').value || '0') || 0,
        tolerancia_desvio: parseFloat($('impTol').value || '0') || 0,
        distancia_entre_surcos: parseFloat($('impDistSurcos').value || '0') || 0,
        factor_k_default: parseFloat($('impFactorK').value || '0') || 0,
        objetivos_tren: objetivosTren,
        total_surcos: parseInt($('impTotalSurcos').value || '0', 10) || 0,
        secciones_aog: parseInt($('impSeccAOG').value || '0', 10) || 0,
        ancho_implemento: parseFloat($('impAncho').value || '0') || 0
      },
      trenes: trenes,
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

  $('btnAddTren').addEventListener('click', function () {
    var body = document.querySelector('#tblTrenes tbody');
    var idx = body.children.length;
    var nextId = idx + 1;
    var tr = document.createElement('tr');
    tr.innerHTML =
      '<td><input type="number" data-f="id" value="' + nextId + '" /></td>' +
      '<td><input type="text" data-f="nombre" value="Tren ' + nextId + '" /></td>' +
      '<td><input type="number" data-f="surcos" value="0" /></td>' +
      '<td><input type="number" data-f="objetivo" step="0.1" value="0" /></td>' +
      '<td class="actions"><button class="btn small btn-del-tren">×</button></td>';
    body.appendChild(tr);
    tr.querySelector('.btn-del-tren').addEventListener('click', function (e) {
      var row = e.target.closest('tr'); row.parentNode.removeChild(row);
    });
  });

  $('btnAddSensor').addEventListener('click', function () {
    var body = document.querySelector('#tblSensores tbody');
    var tr = document.createElement('tr');
    tr.innerHTML =
      '<td><input type="text" data-f="uid" value="" /></td>' +
      '<td><input type="number" data-f="cable" value="0" /></td>' +
      '<td><input type="number" data-f="pin" value="0" /></td>' +
      '<td><input type="number" data-f="bajada" value="0" /></td>' +
      '<td><input type="number" data-f="tren" value="1" /></td>' +
      '<td><input type="text" data-f="tipo" value="semilla" /></td>' +
      '<td><input type="text" data-f="nombre" value="" /></td>' +
      '<td><input type="checkbox" data-f="is_active" checked /></td>' +
      '<td class="actions"><button class="btn small btn-del-sensor">×</button></td>';
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
    $('cfgEnabled').checked = !!pick(c, 'Enabled', 'enabled');
    $('cfgBroker').value = pick(c, 'BrokerAddress', 'brokerAddress') || '';
    $('cfgPort').value = pick(c, 'BrokerPort', 'brokerPort') || 1883;
    $('cfgClient').value = pick(c, 'ClientId', 'clientId') || '';
    $('cfgUser').value = pick(c, 'Username', 'username') || '';
    $('cfgPass').value = pick(c, 'Password', 'password') || '';
    $('cfgTls').checked = !!pick(c, 'UseTls', 'useTls');
    $('cfgTopicTel').value = pick(c, 'TelemetriaTopic', 'telemetriaTopic') || '';
    $('cfgTopicSpeed').value = pick(c, 'SpeedTopic', 'speedTopic') || '';
    $('cfgTopicSec').value = pick(c, 'SectionsTopic', 'sectionsTopic') || '';
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

  function readConfigFromForm() {
    return {
      Enabled: $('cfgEnabled').checked,
      BrokerAddress: $('cfgBroker').value || '127.0.0.1',
      BrokerPort: parseInt($('cfgPort').value || '1883', 10) || 1883,
      ClientId: $('cfgClient').value || 'AgOpenGPS_VistaX',
      Username: $('cfgUser').value || '',
      Password: $('cfgPass').value || '',
      UseTls: $('cfgTls').checked,
      TelemetriaTopic: $('cfgTopicTel').value || 'vistax/nodos/telemetria',
      SpeedTopic: $('cfgTopicSpeed').value || 'aog/machine/speed',
      SectionsTopic: $('cfgTopicSec').value || 'sections/state',
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
