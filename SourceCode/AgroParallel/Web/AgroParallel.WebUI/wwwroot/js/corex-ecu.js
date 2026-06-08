// ============================================================================
// corex-ecu.js — UI del módulo CoreX-ECU (firmware Teensy 4.1, spec v1.11+).
//
// Polleo el snapshot del Hub (/api/corex-ecu/status) cada 500 ms y pinto los
// cuatro tabs:
//   · Live        — telemetría runtime (IMU/WAS/GPS/CAN/Autosteer/Sistema)
//   · Estado      — checklist runtime derivado del último /status + acciones
//   · Parámetros  — GET/POST /api/corex-ecu/params (auto-zero, keya, IMU EMA)
//   · Conexión    — config persistida del Hub (IP, puerto, timeout)
//
// Nota sobre el JSON: EmbedIO (Swan.Lite) emite SIEMPRE camelCase a partir del
// PascalCase del DTO en C#, e ignora los `[JsonPropertyName]` de outbound. Por
// eso desde acá leemos `s.imu.yawDeg`, `s.was.zeroDone`, `s.errorCode`, etc.
// ============================================================================
(function () {
  'use strict';

  var POLL_MS = 500;
  var pollTimer = null;
  var lastSnapshot = null;

  // -------- Helpers --------------------------------------------------------
  function $(id) { return document.getElementById(id); }
  function fmt(n, d) {
    if (n === null || n === undefined || isNaN(n)) return '—';
    return Number(n).toFixed(d === undefined ? 1 : d);
  }
  function fmtInt(n) {
    if (n === null || n === undefined || isNaN(n)) return '—';
    return Math.trunc(n).toString();
  }
  function yesNo(b) { return b ? 'Sí' : 'No'; }
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, function (m) {
      return { '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[m];
    });
  }
  // Etiqueta humana para las fuentes WAS. El operario ve labels cortos en los
  // tres botones de la UI (Encoder motor / WAS / YAW) y este helper se usa
  // también en mensajes de estado.
  //   keya     → "Encoder motor"  (ticks del motor Keya por CAN)
  //   ads_se   → "WAS"            (analógico single-ended por ADS1115)
  //   ads_diff → "WAS diff"       (analógico differential, valor legacy aceptado)
  //   bno_was  → "YAW"            (BNO085 en modo RVC sobre Serial3 — firmware v1.14+)
  function prettySource(src) {
    switch ((src || '').toLowerCase()) {
      case 'keya':     return 'Encoder motor';
      case 'ads_se':   return 'WAS';
      case 'ads_diff': return 'WAS diff';
      case 'bno_was':  return 'YAW';
      // Legacy: el Hub viejo guardaba "yaw" antes de migrar al nombre canónico
      // del firmware v1.14. Lo renderizamos igual para que la UI no muestre "—".
      case 'yaw':      return 'YAW';
      default:         return src || '—';
    }
  }
  function fmtSeconds(s) {
    if (s === null || s === undefined || isNaN(s)) return '—';
    s = Math.trunc(s);
    var h = Math.trunc(s / 3600);
    var m = Math.trunc((s % 3600) / 60);
    var ss = s % 60;
    return (h > 0 ? h + 'h ' : '') + (m + 'm ' + ss + 's');
  }

  // -------- Tabs -----------------------------------------------------------
  function initTabs() {
    document.querySelectorAll('.tab-btn').forEach(function (b) {
      b.addEventListener('click', function () {
        var tab = b.getAttribute('data-tab');
        document.querySelectorAll('.tab-btn').forEach(function (x) {
          x.classList.toggle('active', x === b);
        });
        document.querySelectorAll('.tab-pane').forEach(function (p) {
          p.classList.toggle('active', p.getAttribute('data-tab') === tab);
        });
        if (tab === 'diag') {
          // El checklist se construye en cada render — actualizo con el último snap.
          renderChecklist(lastSnapshot);
        }
      });
    });
  }

  // -------- Pill estado conexión + banner error ---------------------------
  function setPill(ok, text) {
    var pill = $('okPill');
    pill.className = 'pill ' + (ok ? 'ok' : 'idle');
    pill.innerHTML = '<span class="dot"></span> ' + text;
  }
  function showError(code, msg, tech) {
    var b = $('errBanner');
    if (!code && !msg) { b.classList.remove('visible'); return; }
    b.classList.add('visible');
    $('errCode').textContent = code || 'AGP-SYS-009';
    $('errMsg').textContent = msg || 'Error desconocido.';
    $('errTech').textContent = tech || '(sin detalle)';
  }

  // -------- Polling /status ------------------------------------------------
  function pollStatus() {
    fetch('/api/corex-ecu/status', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        lastSnapshot = j;
        renderStatus(j);
        // Si el usuario está parado en Estado, refresco el checklist en vivo.
        var diagPane = document.querySelector('.tab-pane[data-tab="diag"]');
        if (diagPane && diagPane.classList.contains('active')) {
          renderChecklist(j);
        }
      })
      .catch(function (e) {
        setPill(false, 'Hub no responde');
        showError('AGP-NET-201', 'No se pudo contactar al Hub local.', String(e));
      });
  }

  function renderStatus(s) {
    if (!s || !s.ok) {
      setPill(false, 'ECU offline');
      showError(s && s.errorCode, (s && s.error) || 'Sin respuesta del CoreX-ECU.', s && s.errorTechnical);
      clearLive();
      return;
    }
    setPill(true, 'ECU conectado');
    showError(null, null, null);

    $('hdrFw').textContent = s.firmware ? (s.firmware + (s.version ? (' ' + s.version) : '')) : '—';

    var fwc = $('fwCurrent');
    if (fwc) fwc.textContent = s.version || '—';

    var imu = s.imu || {};
    $('imuMode').textContent = imu.mode || '—';
    $('imuYaw').textContent = fmt(imu.yawDeg) + ' °';
    $('imuRoll').textContent = fmt(imu.rollDeg) + ' °';
    $('imuPitch').textContent = fmt(imu.pitchDeg) + ' °';
    $('imuYawRate').textContent = fmt(imu.yawRateDps) + ' °/s';

    var w = s.was || {};
    $('wasSrc').textContent = w.source ? prettySource(w.source) : '—';
    $('wasAngle').textContent = fmt(w.angleDeg) + ' °';
    $('wasZero').textContent = w.zeroDone ? 'OK (centro capturado)' : 'Pendiente';
    $('wasRaw').textContent = fmtInt(w.encoderRaw);
    $('wasCenter').textContent = fmtInt(w.zeroTicks);
    $('wasTpd').textContent = fmt(w.ticksPerDeg, 2);
    // Firmware v1.11+: en modo ADS el firmware reporta probe + lectura cruda.
    // En modo Keya estos campos vienen vacíos / 0 — los mostramos igual para
    // que el operario pueda diagnosticar el chip antes de cambiar de fuente.
    var srcLow = (w.source || '').toLowerCase();
    var isAds = srcLow === 'ads_se' || srcLow === 'ads_diff';
    var elAdsP = $('wasAdsPresent');
    var elAdsR = $('wasAdsRaw');
    if (elAdsP) elAdsP.textContent = isAds ? (w.adsPresent ? 'Sí (chip detectado)' : 'No (sin respuesta I²C)')
                                           : (w.adsPresent ? 'Detectado (no activo)' : '—');
    if (elAdsR) elAdsR.textContent = isAds ? fmtInt(w.adsRaw) : (w.adsPresent ? fmtInt(w.adsRaw) : '—');

    var g = s.gps || {};
    $('gpsSpd').textContent = fmt(g.speedKmh) + ' km/h';
    $('gpsKnots').textContent = fmt(g.speedKnots, 2);
    $('gpsHdg').textContent = fmt(g.headingDeg) + ' °';
    $('gpsGga').textContent = yesNo(g.ggaSeen);

    var c = s.can || {};
    $('canEn').textContent = yesNo(c.keyaSteerEnabled);
    $('canCurr').textContent = fmt(c.keyaCurrentA, 2) + ' A';

    var a = s.autosteer || {};
    $('asRun').textContent = a.running ? 'Corriendo' : 'Detenido';
    $('asGuide').textContent = a.guidanceActive ? 'Activa' : 'Inactiva';
    $('asWd').textContent = fmtInt(a.watchdog) + (a.watchdog >= 100 ? ' (caído)' : '');
    $('asPwm').textContent = fmtInt(a.pwm);
    $('asSp').textContent = fmt(a.setpointDeg) + ' °';

    $('ip').textContent = s.ip || '—';
    $('eth').textContent = s.ethernet ? 'Link up' : 'Link down';
    $('fwVer').textContent = (s.firmware || '—') + (s.version ? (' ' + s.version) : '');
    $('upTime').textContent = fmtSeconds(s.uptimeSec);

    // Calibración tab: bloqueo del joystick + reflejo de motor.testActive.
    updateMotorLock(s);
  }

  function clearLive() {
    ['hdrFw',
     'imuMode','imuYaw','imuRoll','imuPitch','imuYawRate',
     'wasSrc','wasAngle','wasZero','wasRaw','wasCenter','wasTpd','wasAdsPresent','wasAdsRaw',
     'gpsSpd','gpsKnots','gpsHdg','gpsGga',
     'canEn','canCurr',
     'asRun','asGuide','asWd','asPwm','asSp',
     'ip','eth','fwVer','upTime'].forEach(function (id) {
      var el = $(id); if (el) el.textContent = '—';
    });
  }

  // -------- Checklist runtime ----------------------------------------------
  // No hay endpoint de boot en v1.08; el checklist se deriva del último /status.
  function renderChecklist(s) {
    var cl = $('checklist');
    if (!cl) return;
    if (!s) {
      cl.innerHTML = '<div class="subtitle">Esperando primer snapshot…</div>';
      return;
    }
    if (!s.ok) {
      cl.innerHTML = '<div class="subtitle">' + escapeHtml((s.errorCode || '') + ' · ' + (s.error || 'Sin respuesta del CoreX-ECU.')) + '</div>';
      return;
    }

    var checks = [];
    var imu = s.imu || {}, w = s.was || {}, g = s.gps || {}, c = s.can || {}, a = s.autosteer || {};

    checks.push({
      state: s.ethernet ? 'ok' : 'fail',
      label: 'Ethernet link',
      detail: s.ethernet ? 'Up · IP ' + (s.ip || '?') : 'Sin link'
    });
    checks.push({
      state: imu.present ? 'ok' : 'warn',
      label: 'IMU (BNO)',
      detail: imu.present ? ('Modo ' + (imu.mode || '?')) : 'No detectada'
    });
    checks.push({
      state: w.zeroDone ? 'ok' : 'warn',
      label: 'WAS auto-zero',
      detail: w.zeroDone ? ('Centro = ' + (w.zeroTicks || 0) + ' ticks · fuente ' + (w.source || '?')) : 'Pendiente · esperá que el tractor esté quieto y derecho'
    });
    checks.push({
      state: g.ggaSeen ? 'ok' : 'warn',
      label: 'GPS NMEA',
      detail: g.ggaSeen ? ('Spd ' + fmt(g.speedKmh) + ' km/h') : 'Sin GGA recibido'
    });
    checks.push({
      state: c.keyaSteerEnabled ? 'ok' : 'unknown',
      label: 'CAN Keya',
      detail: c.keyaSteerEnabled ? ('Motor habilitado · ' + fmt(c.keyaCurrentA, 2) + ' A') : 'Motor deshabilitado'
    });
    var wd = a.watchdog || 0;
    checks.push({
      state: a.running ? (wd < 100 ? 'ok' : 'warn') : 'fail',
      label: 'Autosteer loop',
      detail: a.running ? ('Watchdog PGN 254 = ' + wd + (wd >= 100 ? ' (AOG no manda guidance)' : '')) : 'No está corriendo'
    });
    checks.push({
      state: 'ok',
      label: 'Uptime',
      detail: fmtSeconds(s.uptimeSec)
    });

    var ico = { ok: '✓', warn: '!', fail: '✕', unknown: '?' };
    cl.innerHTML = checks.map(function (chk) {
      return '<div class="check-row">' +
        '<div class="check-state ' + chk.state + '">' + (ico[chk.state] || '?') + '</div>' +
        '<div class="check-label">' + escapeHtml(chk.label) + '</div>' +
        '<div class="check-detail">' + escapeHtml(chk.detail) + '</div>' +
        '</div>';
    }).join('');
  }

  // -------- Parámetros ----------------------------------------------------
  function loadParams() {
    $('paramsMsg').textContent = 'Cargando…';
    setUpdatedPill('updAz', null);
    setUpdatedPill('updKeya', null);
    setUpdatedPill('updImu', null);

    fetch('/api/corex-ecu/params', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (p) {
        if (!p || p.ok === false) {
          $('paramsMsg').textContent = ((p && p.errorCode) || 'AGP-NET-201') + ' · ' + ((p && p.error) || 'No se pudo leer /params.');
          return;
        }
        var az = p.autoZero || {}, ky = p.keya || {}, im = p.imu || {};
        setVal('p_useBno',     az.useBno);
        setVal('p_useGps',     az.useGps);
        setVal('p_beta',       az.beta);
        setVal('p_speedMin',   az.speedMin);
        setVal('p_yawRateMax', az.yawRateMax);
        setVal('p_gpsHdgMax',  az.gpsHdgMax);
        setVal('p_timeSlowMs', az.timeSlowMs);
        setVal('p_timeFastMs', az.timeFastMs);
        setVal('p_speedSlow',  az.speedSlow);
        setVal('p_speedFast',  az.speedFast);
        setVal('p_ticksPerDeg', ky.ticksPerDeg);
        setVal('p_emaYaw',   im.emaYaw);
        setVal('p_emaRoll',  im.emaRoll);
        setVal('p_emaPitch', im.emaPitch);
        setVal('p_emaStop',  im.emaStop);
        $('paramsMsg').textContent = 'Leído del Teensy.';
      })
      .catch(function (e) {
        $('paramsMsg').textContent = 'Error: ' + e;
      });
  }

  function setVal(id, v) {
    var el = $(id);
    if (!el) return;
    if (v === null || v === undefined || (typeof v === 'number' && isNaN(v))) {
      el.value = '';
    } else {
      el.value = String(v);
    }
  }

  function setUpdatedPill(id, ok) {
    var el = $(id);
    if (!el) return;
    if (ok === null) {
      el.className = 'updated-pill';
      el.textContent = 'sin guardar';
    } else if (ok) {
      el.className = 'updated-pill ok';
      el.textContent = '✓ persistido';
    } else {
      el.className = 'updated-pill';
      el.textContent = 'no aplicado';
    }
  }

  // Construye un patch FLAT solo con los campos que el usuario tocó (no NaN).
  function buildParamsPatch() {
    var patch = {};
    var entries = [
      // auto-zero — useBno/useGps son ints (0/1)
      ['p_useBno',     'useBno',     'int'],
      ['p_useGps',     'useGps',     'int'],
      ['p_beta',       'beta',       'num'],
      ['p_speedMin',   'speedMin',   'num'],
      ['p_yawRateMax', 'yawRateMax', 'num'],
      ['p_gpsHdgMax',  'gpsHdgMax',  'num'],
      ['p_timeSlowMs', 'timeSlowMs', 'int'],
      ['p_timeFastMs', 'timeFastMs', 'int'],
      ['p_speedSlow',  'speedSlow',  'num'],
      ['p_speedFast',  'speedFast',  'num'],
      // keya
      ['p_ticksPerDeg', 'ticksPerDeg', 'num'],
      // IMU EMA
      ['p_emaYaw',   'emaYaw',   'num'],
      ['p_emaRoll',  'emaRoll',  'num'],
      ['p_emaPitch', 'emaPitch', 'num'],
      ['p_emaStop',  'emaStop',  'num']
    ];
    entries.forEach(function (e) {
      var el = $(e[0]);
      if (!el) return;
      var raw = (el.value || '').trim();
      if (raw === '') return; // campo vacío → no mando nada (no pisa lo persistido)
      var v = e[2] === 'int' ? parseInt(raw, 10) : parseFloat(raw);
      if (isNaN(v)) return;
      patch[e[1]] = v;
    });
    return patch;
  }

  function saveParams() {
    var patch = buildParamsPatch();
    if (Object.keys(patch).length === 0) {
      $('paramsMsg').textContent = 'No hay cambios para guardar.';
      return;
    }
    $('paramsMsg').textContent = 'Guardando…';
    fetch('/api/corex-ecu/params', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch)
    })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        if (!j || !j.ok) {
          $('paramsMsg').textContent = ((j && j.errorCode) || 'AGP-NET-201') + ' · ' + ((j && j.error) || 'No se pudo guardar.');
          return;
        }
        var u = j.updated || {};
        setUpdatedPill('updAz',   !!u.autoZero);
        setUpdatedPill('updKeya', !!u.keya);
        setUpdatedPill('updImu',  !!u.imu);
        $('paramsMsg').textContent = 'Guardado. EEPROM actualizada.';
      })
      .catch(function (e) {
        $('paramsMsg').textContent = 'Error: ' + e;
      });
  }

  // -------- Acciones (zero + reboot) --------------------------------------
  function forceZero() {
    $('zeroMsg').textContent = 'Capturando centro…';
    fetch('/api/corex-ecu/zero', { method: 'POST' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        if (!j || !j.ok) {
          $('zeroMsg').textContent = ((j && j.errorCode) || 'AGP-NET-201') + ' · ' + ((j && j.error) || 'Falló.');
          return;
        }
        $('zeroMsg').textContent = 'OK · centro = ' + (j.zeroTicks || 0) + ' ticks.';
      })
      .catch(function (e) {
        $('zeroMsg').textContent = 'Error: ' + e;
      });
  }

  function rebootEcu() {
    if (!confirm('¿Reiniciar el CoreX-ECU? El autosteer se interrumpe ~3-5 segundos.')) return;
    fetch('/api/corex-ecu/reboot', { method: 'POST' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        alert(j && j.ok ? 'Reinicio solicitado.' : 'No se pudo reiniciar.');
      })
      .catch(function (e) { alert('Error: ' + e); });
  }

  // -------- Conexión / config persistida ----------------------------------
  function setToggle(btn, on) {
    if (!btn) return;
    btn.classList.toggle('on', !!on);
    btn.setAttribute('aria-pressed', on ? 'true' : 'false');
    var lbl = btn.querySelector('.toggle-label');
    if (lbl) lbl.textContent = on ? 'ON' : 'OFF';
  }

  // Valores aceptados por el firmware. keya/ads_se/ads_diff existen desde v1.11+;
  // bno_was se agregó en v1.14 (BNO085 en modo RVC sobre Serial3 como fuente de
  // ángulo de rueda) — la UI lo ofrece como tercer botón "YAW". Si el firmware
  // del tractor todavía está en una v1.11..v1.13 el POST /api/wassrc devolverá
  // 400 invalid_source y la banda de mensaje lo va a mostrar al operario.
  var WAS_SOURCES = ['keya', 'ads_se', 'ads_diff', 'bno_was'];

  function normalizeWasSource(src) {
    var s = (src || '').toLowerCase().trim();
    if (WAS_SOURCES.indexOf(s) >= 0) return s;
    // Compat con el schema viejo del Hub que guardaba "analog" — se mapea al
    // single-ended del ADS, que es lo equivalente físicamente.
    if (s === 'analog') return 'ads_se';
    // Compat con el Hub previo al rename v1.14: guardaba "yaw" para la fuente
    // IMU/BNO; ahora el firmware exige el nombre canónico "bno_was".
    if (s === 'yaw') return 'bno_was';
    return 'keya';
  }

  // Pinta el botón activo del segmented "Fuente preferida" y sincroniza el
  // <input hidden id="cfgWasSource"> que el resto del JS sigue leyendo.
  // El click en un botón cambia la selección al instante (no espera Guardar)
  // pero la persistencia recién va con el botón principal "Guardar configuración".
  function setWasSourceButtons(src) {
    var norm = normalizeWasSource(src);
    var hidden = document.getElementById('cfgWasSource');
    if (hidden) hidden.value = norm;
    var grp = document.getElementById('cfgWasSourceGroup');
    if (!grp) return;
    // ads_diff sigue siendo un valor válido del firmware, pero la UI lo agrupa
    // visualmente dentro del botón "WAS" (ads_se) para mantener 3 botones:
    // Encoder motor / WAS / YAW. Si la fuente persistida es ads_diff, marcamos
    // el botón WAS — el valor real ads_diff queda en el hidden y se respeta.
    var visualKey = (norm === 'ads_diff') ? 'ads_se' : norm;
    var btns = grp.querySelectorAll('.was-src-btn');
    for (var i = 0; i < btns.length; i++) {
      var on = btns[i].getAttribute('data-src') === visualKey;
      btns[i].classList.toggle('on', on);
      btns[i].setAttribute('aria-checked', on ? 'true' : 'false');
    }
  }

  function initWasSourceButtons() {
    var grp = document.getElementById('cfgWasSourceGroup');
    if (!grp || grp.dataset.bound === '1') return;
    grp.dataset.bound = '1';
    var btns = grp.querySelectorAll('.was-src-btn');
    for (var i = 0; i < btns.length; i++) {
      btns[i].addEventListener('click', function (ev) {
        var src = ev.currentTarget.getAttribute('data-src') || 'keya';
        setWasSourceButtons(src);
      });
    }
  }

  function loadCfg() {
    initWasSourceButtons();
    // 1) Config persistida del Hub (IP/puerto/timeout + preferencia WAS).
    fetch('/api/corex-ecu/config', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (c) {
        setToggle($('cfgEnabled'), !!c.enabled);
        $('cfgIp').value = c.ip || '';
        $('cfgPort').value = c.port || 80;
        $('cfgTimeout').value = c.timeoutMs || 3000;
        setWasSourceButtons(c.wasSource);
      })
      .catch(function () { /* swallow — la UI arranca con defaults */ });

    // 2) Fuente WAS efectiva en el firmware (puede diferir de la persistida si
    // alguien la cambió por curl). Si la lectura responde, sincronizamos los
    // botones con lo que realmente está activo en el Teensy.
    fetch('/api/corex-ecu/wassrc', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (w) {
        if (w && w.ok && w.source) setWasSourceButtons(w.source);
      })
      .catch(function () { /* swallow — el polleo de /status igual va a renderear was.source */ });
  }

  function saveCfg() {
    // El segmented de fuente WAS escribe en el <input hidden id="cfgWasSource">
    // al hacer click; acá lo leemos como antes.
    var srcEl = $('cfgWasSource');
    var wasSource = normalizeWasSource(srcEl ? srcEl.value : 'keya');

    var dto = {
      enabled: $('cfgEnabled').classList.contains('on'),
      ip: $('cfgIp').value.trim(),
      port: parseInt($('cfgPort').value, 10) || 80,
      timeoutMs: parseInt($('cfgTimeout').value, 10) || 3000,
      wasSource: wasSource
    };
    $('cfgMsg').textContent = 'Guardando…';
    fetch('/api/corex-ecu/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(dto)
    })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        if (!j || !j.ok) {
          $('cfgMsg').textContent = 'Falló al guardar.';
          return;
        }
        // Propagar la preferencia al firmware via endpoint canónico v1.11+.
        // El POST /api/wassrc cambia la fuente en caliente y persiste a EEPROM
        // del Teensy; si es ADS, además hace probe I²C y devuelve si el chip
        // contestó (ads_present + probed).
        return fetch('/api/corex-ecu/wassrc', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ source: wasSource })
        })
          .then(function (r) { return r.json(); })
          .then(function (wj) {
            if (wj && wj.ok) {
              var msg = 'OK · fuente WAS aplicada al firmware (' + prettySource(wj.source || wasSource) + ')';
              if (wj.probed) {
                msg += wj.adsPresent ? ' · ADS1115 detectado ✓' : ' · ADS1115 NO detectado ✗';
              }
              $('cfgMsg').textContent = msg + '.';
            } else {
              var code = (wj && (wj.errorCode || wj.error_code)) || 'AGP-NET-201';
              var err  = (wj && wj.error) || 'No se pudo aplicar la fuente WAS al firmware.';
              $('cfgMsg').textContent = 'OK config persistida · ' + code + ' · ' + err;
            }
          })
          .catch(function () {
            $('cfgMsg').textContent = 'OK (config persistida; no se pudo notificar al firmware).';
          });
      })
      .catch(function (e) { $('cfgMsg').textContent = 'Error: ' + e; });
  }

  // -------- Motor manual (v1.09+) -----------------------------------------
  // El motor manual usa dead-man: el firmware lo frena solo si dejamos de
  // postear /motor/test cada < duration_ms. Mientras el operario mantenga
  // el botón apretado, reenviamos cada HOLD_MS con duration_ms=1000. Si
  // suelta, mandamos UN /motor/stop explícito + dejamos que expire el dead-man.
  var HOLD_MS = 350;             // intervalo de reenvío mientras se mantiene
  var motorHoldTimer = null;
  var motorHoldPwm = 0;
  var motorLocked = false;
  // Guard anti-overlap: si un /motor/test sigue en vuelo cuando el tick del
  // intervalo dispara el siguiente, lo salteamos. Sin esto, una red lenta
  // genera N requests pendientes que llegan fuera de orden y pueden mantener
  // el dead-man activo aún después de stopHolding() — el motor seguiría
  // moviéndose 1s extra por cada request encolado.
  var motorTestInFlight = false;

  function updateMotorLock(s) {
    // Bloqueamos los botones si guidance está activa (cualquier intento da 409).
    var a = (s && s.autosteer) || {};
    var locked = !!a.guidanceActive;
    var warn = $('motorLockWarn');
    if (warn) warn.classList.toggle('visible', locked);
    var fwWarn = $('fwLockWarn');
    if (fwWarn) fwWarn.style.display = locked ? '' : 'none';
    if (locked !== motorLocked) {
      motorLocked = locked;
      document.querySelectorAll('.joystick .jbtn').forEach(function (b) {
        b.disabled = locked && b.id !== 'btnMotorStop';
      });
      // El PWM manual libre comparte el bloqueo por guidance externa.
      var customBtn = $('btnMotorHoldCustom');
      if (customBtn) customBtn.disabled = locked;
      var customInput = $('motorPwmCustom');
      if (customInput) customInput.disabled = locked;
      // El botón de actualizar firmware comparte el lock por guiado activo.
      var flashBtn = $('btnFlashFw');
      if (flashBtn) {
        if (locked) flashBtn.disabled = true;
        else flashBtn.disabled = flashing || ($('fwVersionSelect') && $('fwVersionSelect').options.length === 0);
      }
      if (locked) stopHolding();
    }
    // Reflejar telemetría test en el subtitle.
    var ms = $('motorStatus');
    var m = (s && s.motor) || {};
    if (!ms) return;
    if (m.testActive) {
      ms.innerHTML = '<span class="active">● Motor activo</span> · PWM ' + (m.testPwm || 0) +
                     ' · queda ' + (m.testRemainingMs || 0) + ' ms';
    } else {
      ms.textContent = 'Motor inactivo · sin comando manual.';
    }
  }

  async function motorTest(pwm) {
    // Skip si todavía hay un request en vuelo — evita pile-up cuando la red
    // está lenta y el HOLD_MS dispara antes de que vuelva el ack del previo.
    if (motorTestInFlight) return;
    motorTestInFlight = true;
    try {
      var r = await fetch('/api/corex-ecu/motor/test', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pwm: pwm, duration_ms: 1000 })
      });
      var j = await r.json();
      if (!j || !j.ok) {
        // Si saltó 409 mid-hold, frenamos el repeater para no spamear.
        stopHolding();
        var msg = (j && j.error) || 'No se pudo mover el motor.';
        $('motorStatus').textContent = ((j && (j.errorCode || j.error_code)) || 'AGP-NET-201') + ' · ' + msg;
      }
    } catch (e) {
      stopHolding();
      $('motorStatus').textContent = 'Error: ' + e;
    } finally {
      motorTestInFlight = false;
    }
  }

  function motorStopExplicit() {
    fetch('/api/corex-ecu/motor/stop', { method: 'POST' })
      .then(function (r) { return r.json(); })
      .catch(function () { /* swallow */ });
  }

  function startHolding(pwm) {
    if (motorLocked) return;
    motorHoldPwm = pwm;
    // Mandamos inmediatamente y después en intervalo.
    motorTest(pwm);
    if (motorHoldTimer) clearInterval(motorHoldTimer);
    motorHoldTimer = setInterval(function () {
      if (motorHoldPwm === 0) { stopHolding(); return; }
      motorTest(motorHoldPwm);
    }, HOLD_MS);
  }

  function stopHolding() {
    if (motorHoldTimer) { clearInterval(motorHoldTimer); motorHoldTimer = null; }
    if (motorHoldPwm !== 0) {
      motorHoldPwm = 0;
      // dead-man corta solo, pero acortamos la latencia con un stop explícito.
      motorStopExplicit();
    }
    // Cubre tanto el joystick como el botón "Mantener para mover" custom.
    document.querySelectorAll('.jbtn.holding').forEach(function (b) {
      b.classList.remove('holding');
    });
  }

  function initJoystick() {
    document.querySelectorAll('.joystick .jbtn[data-pwm]').forEach(function (btn) {
      var pwm = parseInt(btn.getAttribute('data-pwm'), 10) || 0;
      // Soporta mouse + touch + pointer events (kiosk con pantalla táctil).
      var down = function (e) {
        e.preventDefault();
        btn.classList.add('holding');
        startHolding(pwm);
      };
      var up = function () {
        btn.classList.remove('holding');
        stopHolding();
      };
      btn.addEventListener('pointerdown', down);
      btn.addEventListener('pointerup', up);
      btn.addEventListener('pointerleave', up);
      btn.addEventListener('pointercancel', up);
      // touch fallback (algunos webviews viejos no emiten pointer*)
      btn.addEventListener('touchstart', down, { passive: false });
      btn.addEventListener('touchend', up);
      btn.addEventListener('touchcancel', up);
    });
    var stopBtn = $('btnMotorStop');
    if (stopBtn) {
      stopBtn.addEventListener('click', function () {
        stopHolding();
        motorStopExplicit();
      });
    }
    // Dead-man de seguridad para la pestaña: si el operario tabea afuera,
    // minimiza, o el WebView pierde foco con un botón apretado, el evento
    // pointerup/touchend puede NO dispararse — el holding queda activo y el
    // motor sigue moviéndose hasta que vuelva el foco. Cortamos acá.
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState !== 'visible') stopHolding();
    });
    window.addEventListener('blur', stopHolding);
    window.addEventListener('pagehide', stopHolding);
  }

  // -------- Motor manual con PWM libre ------------------------------------
  // Igual que el joystick pero el PWM lo tipea el operario (−200..200, lo
  // clampeamos acá; el backend además re-clampea en CoreXEcuService). Reusa
  // startHolding/stopHolding → mismo dead-man, anti-overlap y bloqueo por
  // guidance externa.
  function readCustomPwm() {
    var el = $('motorPwmCustom');
    var v = parseInt(el && el.value, 10);
    if (isNaN(v)) return 0;
    if (v < -200) v = -200; else if (v > 200) v = 200;
    return v;
  }

  function initCustomMotor() {
    var btn = $('btnMotorHoldCustom');
    if (!btn) return;
    var down = function (e) {
      e.preventDefault();
      if (motorLocked) return;
      var pwm = readCustomPwm();
      if (pwm === 0) {
        $('motorStatus').textContent = 'Poné un PWM distinto de 0 para mover.';
        return;
      }
      btn.classList.add('holding');
      startHolding(pwm);
    };
    var up = function () {
      btn.classList.remove('holding');
      stopHolding();
    };
    btn.addEventListener('pointerdown', down);
    btn.addEventListener('pointerup', up);
    btn.addEventListener('pointerleave', up);
    btn.addEventListener('pointercancel', up);
    btn.addEventListener('touchstart', down, { passive: false });
    btn.addEventListener('touchend', up);
    btn.addEventListener('touchcancel', up);
  }

  // -------- Sweep PWM (v1.10+) --------------------------------------------
  // POST start → 202 con estimated_ms. Después polleamos GET cada 500 ms
  // hasta state=done. La tabla se va rellenando con las mediciones.
  var sweepPollTimer = null;

  // Lista canónica de PWMs del firmware (igual orden — la usamos para construir
  // la tabla apenas arranca, antes de que llegue measured=true por cada paso).
  var SWEEP_STEPS = [5,10,15,20,25,30,40,60,-5,-10,-15,-20,-25,-30,-40,-60];

  function renderSweepRows(results, currentStep) {
    var rows = [];
    var arr = Array.isArray(results) ? results : [];
    // Si el firmware no devolvió todavía los placeholders, usamos SWEEP_STEPS.
    var n = arr.length > 0 ? arr.length : SWEEP_STEPS.length;
    for (var i = 0; i < n; i++) {
      var r = arr[i] || { pwm: SWEEP_STEPS[i], measured: false };
      var cls = (!r.measured)
        ? (i === (currentStep | 0) - 1 ? 'current' : 'pending')
        : '';
      var dps = r.measured ? Number(r.degPerSec || r.deg_per_sec || 0) : null;
      var dPerPwm = r.measured && r.pwm ? (dps / Math.abs(r.pwm)).toFixed(3) : '—';
      rows.push(
        '<tr class="' + cls + '">' +
          '<td>' + (r.pwm > 0 ? '+' : '') + r.pwm + '</td>' +
          '<td>' + (r.measured ? (r.deltaTicks != null ? r.deltaTicks : r.delta_ticks) : '—') + '</td>' +
          '<td>' + (r.measured ? (r.durationMs != null ? r.durationMs : r.duration_ms) : '—') + '</td>' +
          '<td>' + (r.measured ? Number(dps).toFixed(3) : '—') + '</td>' +
          '<td>' + dPerPwm + '</td>' +
        '</tr>'
      );
    }
    var tb = document.querySelector('#sweepTable tbody');
    if (tb) tb.innerHTML = rows.join('');
  }

  async function startSweep() {
    var stepMs   = parseInt($('swStep').value, 10)   || 1500;
    var settleMs = parseInt($('swSettle').value, 10) || 400;
    $('sweepMsg').textContent = 'Iniciando barrido…';
    try {
      var r = await fetch('/api/corex-ecu/calibration/pwm-sweep', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ step_duration_ms: stepMs, settle_ms: settleMs })
      });
      var j = await r.json();
      if (!j || !j.ok) {
        $('sweepMsg').textContent = ((j && (j.errorCode || j.error_code)) || 'AGP-NET-201') + ' · ' +
                                    ((j && j.error) || 'No se pudo iniciar.');
        return;
      }
      var est = j.estimatedMs || j.estimated_ms || 0;
      $('sweepMsg').textContent = 'Barrido en curso — ' + (j.stepCount || j.step_count || 16) +
                                  ' pasos · ~' + Math.round(est / 1000) + ' s estimados.';
      $('sweepTable').style.display = 'table';
      $('sweepProgress').style.display = 'block';
      $('btnSweepStart').disabled = true;
      $('btnSweepCancel').disabled = false;
      renderSweepRows([], 1);
      startSweepPolling();
    } catch (e) {
      $('sweepMsg').textContent = 'Error: ' + e;
    }
  }

  function startSweepPolling() {
    if (sweepPollTimer) clearInterval(sweepPollTimer);
    pollSweepOnce();
    sweepPollTimer = setInterval(pollSweepOnce, 500);
  }

  async function pollSweepOnce() {
    try {
      var r = await fetch('/api/corex-ecu/calibration/pwm-sweep', { cache: 'no-store' });
      var j = await r.json();
      if (!j || !j.ok) return;
      var state = j.state || 'idle';
      var curr  = j.currentStep || j.current_step || 0;
      var total = j.totalSteps  || j.total_steps  || SWEEP_STEPS.length;
      $('sweepProgText').textContent = 'Estado: ' + state + ' · paso ' + curr + ' / ' + total +
                                        (j.ticksPerDeg ? (' · ticks/deg ' + Number(j.ticksPerDeg).toFixed(2)) : '');
      renderSweepRows(j.results || [], curr);

      if (state === 'done') {
        stopSweepPolling();
        $('sweepMsg').textContent = 'Barrido completo.';
        $('btnSweepStart').disabled = false;
        $('btnSweepCancel').disabled = true;
      } else if (state === 'idle' && curr === 0) {
        // El firmware volvió a idle sin que pidamos cancel — abortado por guidance.
        stopSweepPolling();
        $('sweepMsg').textContent = 'Barrido abortado (probable: guidance activa).';
        $('btnSweepStart').disabled = false;
        $('btnSweepCancel').disabled = true;
      }
    } catch (e) { /* swallow — siguiente tick re-intenta */ }
  }

  function stopSweepPolling() {
    if (sweepPollTimer) { clearInterval(sweepPollTimer); sweepPollTimer = null; }
  }

  async function cancelSweep() {
    if (!confirm('¿Cancelar el barrido en curso? El motor se frena de inmediato.')) return;
    try {
      var r = await fetch('/api/corex-ecu/calibration/pwm-sweep', { method: 'DELETE' });
      var j = await r.json();
      if (j && j.ok) {
        $('sweepMsg').textContent = 'Barrido cancelado.';
      } else {
        $('sweepMsg').textContent = ((j && (j.errorCode || j.error_code)) || 'AGP-NET-201') + ' · ' +
                                    ((j && j.error) || 'No se pudo cancelar.');
      }
      stopSweepPolling();
      $('btnSweepStart').disabled = false;
      $('btnSweepCancel').disabled = true;
    } catch (e) {
      $('sweepMsg').textContent = 'Error: ' + e;
    }
  }

  // -------- Actualizar firmware -------------------------------------------
  var flashing = false;

  function loadFirmwareVersions() {
    fetch('/api/firmwares', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        var sel = $('fwVersionSelect');
        var hint = $('fwEmptyHint');
        var btn = $('btnFlashFw');
        if (!sel || !btn) return;
        sel.innerHTML = '';
        var prod = null;
        if (j && j.productos) {
          prod = j.productos.filter(function (p) {
            return (p.producto || '').toLowerCase() === 'corex-ecu';
          })[0];
        }
        var vers = (prod && prod.versiones) || [];
        if (!vers.length) {
          if (hint) hint.style.display = '';
          btn.disabled = true;
          return;
        }
        if (hint) hint.style.display = 'none';
        vers.forEach(function (v) {
          var o = document.createElement('option');
          o.value = v.version;
          o.textContent = v.version + (v.local ? '' : ' (no descargado)');
          o.disabled = !v.local;   // sólo se puede flashear lo que está en disco
          sel.appendChild(o);
        });
        // Habilitar salvo que el guiado esté bloqueando o un flash en curso.
        btn.disabled = motorLocked || flashing;
      })
      .catch(function () {
        var hint = $('fwEmptyHint');
        if (hint) hint.style.display = '';
        var btn = $('btnFlashFw');
        if (btn) btn.disabled = true;
      });
  }

  function flashFirmware() {
    if (flashing) return;
    var sel = $('fwVersionSelect');
    var version = sel && sel.value;
    if (!version) return;
    if (motorLocked) {
      $('fwFlashMsg').textContent = 'No se puede actualizar con el guiado activo.';
      return;
    }
    if (!window.confirm('¿Actualizar el CoreX-ECU a la versión ' + version +
        '?\n\nLa unidad se va a reiniciar. No la apagues durante el proceso.')) {
      return;
    }
    flashing = true;
    $('btnFlashFw').disabled = true;
    sel.disabled = true;
    $('fwFlashMsg').textContent = 'Enviando firmware… no apagues la unidad (puede tardar ~30 s).';

    fetch('/api/corex-ecu/firmware/flash', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ version: version })
    })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        if (j && j.ok) {
          $('fwFlashMsg').textContent = 'Firmware enviado. La unidad se está reiniciando…';
          pollUntilRebooted(version);
        } else {
          flashing = false;
          $('btnFlashFw').disabled = false;
          sel.disabled = false;
          var code = (j && (j.errorCode || j.error_code)) || 'AGP-NET-201';
          $('fwFlashMsg').textContent = code + ' · ' + ((j && j.error) || 'No se pudo actualizar.');
        }
      })
      .catch(function (e) {
        flashing = false;
        $('btnFlashFw').disabled = false;
        sel.disabled = false;
        $('fwFlashMsg').textContent = 'Error: ' + e;
      });
  }

  // Tras el 200 del flash, la unidad rebootea (~3-5 s inalcanzable). El poll
  // normal de /status (cada 500 ms) actualiza lastSnapshot; acá esperamos a ver
  // la versión nueva o cortamos por timeout.
  function pollUntilRebooted(target) {
    var started = Date.now();
    var TIMEOUT_MS = 60000;
    var t = setInterval(function () {
      var s = lastSnapshot;
      var done = s && s.ok && s.version === target;
      if (done) {
        clearInterval(t);
        flashing = false;
        $('fwVersionSelect').disabled = false;
        $('fwFlashMsg').textContent = '✓ Actualizado a la versión ' + target + '.';
        loadFirmwareVersions();
        return;
      }
      if (Date.now() - started > TIMEOUT_MS) {
        clearInterval(t);
        flashing = false;
        $('btnFlashFw').disabled = motorLocked;
        $('fwVersionSelect').disabled = false;
        $('fwFlashMsg').textContent = 'No pude confirmar la versión nueva. Revisá el estado de la unidad manualmente.';
      }
    }, 2000);
  }

  // -------- Wiring --------------------------------------------------------
  document.addEventListener('DOMContentLoaded', function () {
    initTabs();

    $('btnSaveCfg').addEventListener('click', saveCfg);
    $('cfgEnabled').addEventListener('click', function () {
      setToggle(this, !this.classList.contains('on'));
    });

    initJoystick();
    initCustomMotor();
    var flashBtn = $('btnFlashFw');
    if (flashBtn) flashBtn.addEventListener('click', flashFirmware);
    loadFirmwareVersions();
    $('btnSweepStart').addEventListener('click', startSweep);
    $('btnSweepCancel').addEventListener('click', cancelSweep);

    loadCfg();
    pollStatus();
    pollTimer = setInterval(pollStatus, POLL_MS);
  });

  window.addEventListener('beforeunload', function () {
    if (pollTimer) clearInterval(pollTimer);
    stopHolding();
    stopSweepPolling();
  });
})();
