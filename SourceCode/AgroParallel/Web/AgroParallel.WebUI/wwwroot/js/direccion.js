// ============================================================================
// direccion.js — Configuración de AutoSteer (FormSteer) para el Hub de PilotX.
// El markup replica el LAYOUT del FormSteer WinForms original (pedido
// 2026-07-31): dos columnas con dos grupos de tabs independientes (#menu =
// guiado, #menu2 = módulo), panel Set/Actual/Error en vivo (graph-steer) y el
// manejo libre abajo a la izquierda. Teclado virtual, botón Guardar con
// estados (dirty / ok) y deep-link ?tab= / ?tab2=.
//
// A diferencia de config.js (que guarda por sección al salir de cada tab), acá
// toda la config de dirección se maneja como UN solo objeto y se persiste
// best-effort contra el módulo de dirección:
//   · GET  /api/steer/config    → popula los controles (si 404, quedan defaults)
//   · POST /api/steer/config    → guarda el objeto serializado
//   · POST /api/steer/zero-was  → pone el WAS en cero
//   · /api/steer/freedrive[/angle|/zero] → manejo libre (mueve el volante sin
//     guía; el motor lo rechaza en movimiento y lo apaga solo si el tractor
//     arranca, así que la pantalla relee el estado en vez de suponerlo)
// Todos los fetch van en try/catch: si el endpoint no existe, la página sigue
// andando con los valores por defecto.
//
// Convención: los data-key del markup son camelCase; el wire usa esos mismos
// nombres (best-effort, el backend/AgpJson tolera el snake vs camel si aplica).
// ============================================================================
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  var estado = $('estado');

  function setEstado(msg, cls) {
    if (!estado) return;
    estado.textContent = msg || '';
    estado.className = cls || '';
  }

  // --------------------------------------------------------------------------
  // Sliders — display del valor grande + botones de nudge
  // --------------------------------------------------------------------------
  function fmtSlider(range) {
    var raw = parseFloat(range.value);
    var scale = parseFloat(range.getAttribute('data-scale') || '1');
    var dec = parseInt(range.getAttribute('data-dec') || '0', 10);
    var display = range.getAttribute('data-display');
    if (display === 'minus10') return (raw - 10).toString();   // U-Turn: muestra value−10
    return (raw * scale).toFixed(dec);
  }

  function updateSliderLabel(range) {
    var key = range.getAttribute('data-key');
    var out = document.querySelector('.sval[data-for="' + key + '"]');
    if (out) {
      var unit = out.querySelector('.u');   // preservar el sufijo de unidad
      out.textContent = fmtSlider(range);
      if (unit) out.appendChild(unit);
    }
    // Sensor: la lectura se muestra como porcentaje 0..255 → 0..100 %.
    if (key === 'sensorLimit') {
      var pct = $('lblSensorPct');
      if (pct) {
        var u = pct.querySelector('.u');
        pct.textContent = Math.round(parseFloat(range.value) / 255 * 100);
        if (u) pct.appendChild(u);
      }
    }
    // Ángulo de cero del WAS y barra de ángulo dependen de offset y cuentas/grado.
    if (key === 'wasOffset' || key === 'countsPerDegree' || key === 'maxSteerAngle') {
      updateWasZeroAngle();
    }
  }

  function updateWasZeroAngle() {
    var offR = document.querySelector('input[data-key="wasOffset"]');
    var cpdR = document.querySelector('input[data-key="countsPerDegree"]');
    var el = $('wasZeroAngle');
    if (offR && cpdR && el) {
      var cpd = parseFloat(cpdR.value) || 1;
      el.textContent = (parseFloat(offR.value) / cpd).toFixed(2);
    }
  }

  function initSliders() {
    document.querySelectorAll('input[type=range][data-key]').forEach(function (range) {
      range.addEventListener('input', function () { updateSliderLabel(range); marcarSucio(); });
      updateSliderLabel(range);
    });
    document.querySelectorAll('.sbtn[data-nudge]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var key = btn.getAttribute('data-nudge');
        var dir = parseInt(btn.getAttribute('data-dir'), 10) || 1;
        var range = document.querySelector('input[type=range][data-key="' + key + '"]');
        if (!range) return;
        var step = parseFloat(range.step || '1') || 1;
        var next = parseFloat(range.value) + dir * step;
        var min = parseFloat(range.min), max = parseFloat(range.max);
        if (next < min) next = min;
        if (next > max) next = max;
        range.value = next;
        updateSliderLabel(range);
        marcarSucio();
      });
    });
  }

  // --------------------------------------------------------------------------
  // Checkboxes (chkimg) — los 3 sensores de giro son excluyentes
  // --------------------------------------------------------------------------
  var EXCLUSIVE_SENSORS = ['encoder', 'pressureSensor', 'currentSensor'];

  function setChk(key, on) {
    var row = document.querySelector('.chkimg[data-key="' + key + '"]');
    if (row) row.classList.toggle('sel', !!on);
  }

  function initCheckboxes() {
    document.querySelectorAll('.chkimg[data-key]').forEach(function (row) {
      row.addEventListener('click', function () {
        var key = row.getAttribute('data-key');
        var willBeOn = !row.classList.contains('sel');
        if (willBeOn && EXCLUSIVE_SENSORS.indexOf(key) >= 0) {
          EXCLUSIVE_SENSORS.forEach(function (k) { if (k !== key) setChk(k, false); });
        }
        row.classList.toggle('sel', willBeOn);
        marcarSucio();
      });
    });
  }

  // --------------------------------------------------------------------------
  // Segmentados (combos del original)
  // --------------------------------------------------------------------------
  function setSeg(name, val) {
    var grp = document.querySelector('.seg[data-seg="' + name + '"]');
    if (!grp) return;
    grp.querySelectorAll('.seg-btn').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-val') === val);
    });
  }
  function getSeg(name) {
    var grp = document.querySelector('.seg[data-seg="' + name + '"]');
    if (!grp) return null;
    var on = grp.querySelector('.seg-btn.on');
    return on ? on.getAttribute('data-val') : null;
  }
  function initSegments() {
    document.querySelectorAll('.seg[data-seg]').forEach(function (grp) {
      grp.querySelectorAll('.seg-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
          grp.querySelectorAll('.seg-btn').forEach(function (b) { b.classList.remove('on'); });
          btn.classList.add('on');
          marcarSucio();
        });
      });
    });
  }

  // --------------------------------------------------------------------------
  // Serialización de todos los controles → objeto de config (camelCase)
  // --------------------------------------------------------------------------
  // El markup usa data-key camelCase; el wire del backend es snake_case
  // (convención AgpJson/AgpControllerBase). Conversión mecánica en los dos
  // sentidos, sin tabla: proportionalGain ⇄ proportional_gain.
  // Las siglas (PWM, PP) no sobreviven la conversión mecánica de vuelta:
  // min_pwm → "minPwm" ≠ data-key "minPWM". Se listan a mano las 3 que hay.
  var KEY_ALIAS = { min_pwm: 'minPWM', high_steer_pwm: 'highSteerPWM', integral_pp: 'integralPP' };

  function toSnake(k) {
    return k
      .replace(/([a-z0-9])([A-Z])/g, '$1_$2')     // minSteerSpeed → min_Steer_Speed
      .replace(/([A-Z]+)([A-Z][a-z])/g, '$1_$2')  // PWMValue → PWM_Value
      .toLowerCase();                             // minPWM → min_pwm
  }

  function snakeToCamelKeys(obj) {
    var out = {};
    Object.keys(obj || {}).forEach(function (k) {
      var camel = KEY_ALIAS[k] ||
        k.replace(/_([a-z])/g, function (m, c) { return c.toUpperCase(); });
      out[camel] = obj[k];
    });
    return out;
  }

  function collectConfig() {
    var cfg = {};
    document.querySelectorAll('input[type=range][data-key]').forEach(function (r) {
      cfg[r.getAttribute('data-key')] = parseFloat(r.value);
    });
    document.querySelectorAll('input[type=number][data-key]').forEach(function (n) {
      cfg[n.getAttribute('data-key')] = parseFloat(n.value);
    });
    document.querySelectorAll('.chkimg[data-key]').forEach(function (row) {
      cfg[row.getAttribute('data-key')] = row.classList.contains('sel');
    });
    document.querySelectorAll('.seg[data-seg]').forEach(function (grp) {
      cfg[grp.getAttribute('data-seg')] = getSeg(grp.getAttribute('data-seg'));
    });
    // camelCase (markup) → snake_case (wire)
    var wire = {};
    Object.keys(cfg).forEach(function (k) { wire[toSnake(k)] = cfg[k]; });
    return wire;
  }

  // Aplica un objeto de config (parcial) a los controles. Ignora claves ausentes.
  // Acepta el wire snake_case del backend (lo pasa a camelCase de los data-key).
  function applyConfig(raw) {
    if (!raw || typeof raw !== 'object') return;
    var cfg = snakeToCamelKeys(raw);
    document.querySelectorAll('input[type=range][data-key]').forEach(function (r) {
      var k = r.getAttribute('data-key');
      if (cfg[k] !== undefined && cfg[k] !== null && !isNaN(cfg[k])) {
        r.value = cfg[k];
        updateSliderLabel(r);
      }
    });
    document.querySelectorAll('input[type=number][data-key]').forEach(function (n) {
      var k = n.getAttribute('data-key');
      if (cfg[k] !== undefined && cfg[k] !== null && !isNaN(cfg[k])) n.value = cfg[k];
    });
    document.querySelectorAll('.chkimg[data-key]').forEach(function (row) {
      var k = row.getAttribute('data-key');
      if (cfg[k] !== undefined) setChk(k, !!cfg[k]);
    });
    document.querySelectorAll('.seg[data-seg]').forEach(function (grp) {
      var name = grp.getAttribute('data-seg');
      if (cfg[name]) setSeg(name, cfg[name]);
    });
    updateWasZeroAngle();
  }

  // --------------------------------------------------------------------------
  // Carga / guardado contra el backend (best-effort)
  // --------------------------------------------------------------------------
  function loadConfig() {
    fetch('/api/steer/config', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (j) {
          applyConfig(j);
          $('ftModuloVal').textContent = 'Conectado';
          setEstado('Configuración cargada', 'ok');
        } else {
          $('ftModuloVal').textContent = 'Sin config';
          setEstado('Sin config guardada — valores por defecto', '');
        }
      })
      .catch(function () {
        // Endpoint aún inexistente → la UI arranca con los valores por defecto.
        $('ftModuloVal').textContent = 'Offline';
        setEstado('Sin conexión con el módulo — valores por defecto', '');
      });
  }

  function saveConfig() {
    var cfg = collectConfig();
    setEstado('Guardando…', '');
    return fetch('/api/steer/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cfg)
    })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) {
        if (j && j.ok === false) {
          setEstado((j.error_code || 'AGP-NET-201') + ' · ' + (j.error || 'No se pudo guardar.'), 'err');
          return false;
        }
        setEstado('Guardado y enviado al módulo de dirección ✔', 'ok');
        limpiarSucio(true);
        return true;
      })
      .catch(function (e) {
        setEstado('No se pudo guardar (módulo offline) [' + e + ']', 'err');
        return false;
      });
  }

  function zeroWas() {
    setEstado('Poniendo el WAS en cero…', '');
    fetch('/api/steer/zero-was', { method: 'POST' })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) {
        if (j && j.ok === false) {
          setEstado(j.error === 'fuera-de-rango'
            ? 'No se puede poner en cero: el ángulo actual se va de rango (revisá el montaje del sensor).'
            : 'No se pudo poner el WAS en cero (' + (j.error || 'sin módulo') + ')', 'err');
          return;
        }
        if (j && typeof j.was_offset !== 'undefined') {
          var r = document.querySelector('input[data-key="wasOffset"]');
          if (r) { r.value = j.was_offset; updateSliderLabel(r); }
        }
        setEstado('WAS puesto en cero ✔', 'ok');
      })
      .catch(function (e) {
        setEstado('No se pudo poner el WAS en cero (módulo offline) [' + e + ']', 'err');
      });
  }

  // --------------------------------------------------------------------------
  // Manejo libre (free drive)
  //
  // Prendido, el módulo mueve el volante con el ángulo que fija el operario, sin
  // guía. El motor no lo deja prender con el tractor andando y lo APAGA SOLO si
  // arranca, así que la pantalla no puede quedarse con su idea del estado: se
  // relee del backend después de cada acción y en un latido mientras está
  // prendido. Lo que se muestra es siempre lo que contestó el motor.
  //   GET  /api/steer/freedrive
  //   POST /api/steer/freedrive        {on}
  //   POST /api/steer/freedrive/angle  {dir:-1|1}
  //   POST /api/steer/freedrive/zero
  // --------------------------------------------------------------------------
  var fdEstadoPrevio = false;
  var fdTimer = null;

  function fdMotivo(j) {
    var vel = (j && typeof j.speed === 'number') ? j.speed.toFixed(1) : '?';
    var lim = (j && typeof j.speed_limit === 'number') ? j.speed_limit.toFixed(1) : '?';
    switch (j && j.error) {
      case 'velocidad':
        return 'No se puede prender: el tractor va a ' + vel + ' km/h (límite ' + lim + ' km/h).';
      case 'sin-velocidad':
        return 'No se puede prender: PilotX no está informando velocidad.';
      case 'apagado':
        return 'Prendé el manejo libre antes de mover el ángulo.';
      case 'service-unavailable':
        return 'Sin módulo de dirección conectado.';
      case 'bad-json':
      case 'error-interno':
        return 'No se pudo completar la acción (AGP-SYS-009).';
      default:
        return null;
    }
  }

  function fdRender(j) {
    var btn = $('fdBtn');
    if (!btn) return;

    var on = !!(j && j.on);
    var ang = (j && typeof j.angle === 'number') ? j.angle : 0;

    btn.classList.toggle('on', on);
    var img = $('fdBtnImg');
    if (img) img.src = '../img/steer/SteerDrive' + (on ? 'On' : 'Off') + '.png';
    var cap = $('fdBtnCap');
    if (cap) cap.textContent = on ? 'Prendido' : 'Apagado';

    var out = $('fdAngle');
    if (out) {
      var u = out.querySelector('.u');
      out.textContent = (ang > 0 ? '+' : '') + ang.toFixed(0);
      if (u) out.appendChild(u);
    }

    ['fdLeft', 'fdRight', 'fdDot'].forEach(function (id) {
      var b = $(id);
      if (b) b.disabled = !on;
    });

    // Botón "Prueba dirección" de la barra fija (layout cabina): ámbar
    // mientras el motor está bajo control manual — se ve aunque el drawer
    // esté cerrado.
    var pill = $('btnFreeDrivePanel');
    if (pill) pill.classList.toggle('running', on);

    var nota = $('fdNota');
    if (nota) {
      var motivo = fdMotivo(j);
      // Se apagó solo entre dos latidos: el motivo es el watchdog de velocidad.
      if (!motivo && fdEstadoPrevio && !on) {
        motivo = 'Se apagó solo: el tractor superó el límite de velocidad de guiado.';
      }
      nota.textContent = motivo ||
        'Con el tractor parado. Se apaga solo por encima del límite de velocidad de guiado.';
      nota.className = motivo ? 'nota err' : 'nota';
    }

    fdEstadoPrevio = on;
    fdLatido(on);
  }

  // Latido: rápido mientras está prendido (para ver el apagado automático),
  // parado cuando no lo está — no tiene sentido machacar el motor apagado.
  function fdLatido(on) {
    if (on && !fdTimer) {
      fdTimer = setInterval(fdPoll, 700);
    } else if (!on && fdTimer) {
      clearInterval(fdTimer);
      fdTimer = null;
    }
  }

  function fdPoll() {
    fetch('/api/steer/freedrive', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) { if (j) fdRender(j); })
      .catch(function () { /* motor caído: se deja lo último mostrado */ });
  }

  function fdPost(url, body) {
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (j) { fdRender(j); return j; })
      .catch(function (e) {
        var nota = $('fdNota');
        if (nota) {
          nota.textContent = 'Sin conexión con PilotX [' + e + '] — el manejo libre no responde.';
          nota.className = 'nota err';
        }
        return null;
      });
  }

  function initFreeDrive() {
    var btn = $('fdBtn');
    if (!btn) return;

    btn.addEventListener('click', function () {
      fdPost('/api/steer/freedrive', { on: !btn.classList.contains('on') });
    });
    var l = $('fdLeft');
    if (l) l.addEventListener('click', function () { fdPost('/api/steer/freedrive/angle', { dir: -1 }); });
    var r = $('fdRight');
    if (r) r.addEventListener('click', function () { fdPost('/api/steer/freedrive/angle', { dir: 1 }); });
    var d = $('fdDot');
    if (d) d.addEventListener('click', function () { fdPost('/api/steer/freedrive/zero', {}); });

    // Salir de la pantalla con el volante bajo control manual sería dejar una
    // función viva sin nadie mirándola: se apaga al ocultarse o cerrarse.
    function apagar() {
      if (!fdEstadoPrevio) return;
      try {
        var body = JSON.stringify({ on: false });
        if (navigator.sendBeacon) {
          navigator.sendBeacon('/api/steer/freedrive', new Blob([body], { type: 'application/json' }));
        } else {
          fdPost('/api/steer/freedrive', { on: false });
        }
      } catch (e) { /* último recurso: el watchdog del motor */ }
    }
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'hidden') apagar();
    });
    window.addEventListener('pagehide', apagar);

    initFreeDrawer();
    fdPoll();
  }

  // --------------------------------------------------------------------------
  // Drawer del manejo libre (layout cabina, direccion_mejorado 2026-08-08):
  // #fsFree ya no ocupa una franja fija — se abre desde "Prueba dirección" en
  // la barra de abajo, con scrim. Cerrarlo con el motor prendido primero lo
  // APAGA (nunca queda el volante bajo control manual sin nadie mirándolo).
  // Con el markup viejo (sin drawer) los ids no existen y esto es un no-op.
  // --------------------------------------------------------------------------
  function initFreeDrawer() {
    var pill = $('btnFreeDrivePanel');
    var panel = $('fsFree');
    var scrim = $('freeScrim');
    var cerrarBtn = $('btnCloseFreeDrive');
    if (!pill || !panel || !scrim) return;

    function setAbierto(abierto) {
      panel.classList.toggle('open', abierto);
      scrim.classList.toggle('open', abierto);
      panel.setAttribute('aria-hidden', abierto ? 'false' : 'true');
      pill.setAttribute('aria-expanded', abierto ? 'true' : 'false');
    }

    function cerrar() {
      if (fdEstadoPrevio) {
        // Motor bajo control manual: apagar PRIMERO, cerrar cuando confirme.
        fdPost('/api/steer/freedrive', { on: false })
          .then(function () { setAbierto(false); });
        return;
      }
      setAbierto(false);
    }

    pill.addEventListener('click', function () {
      if (panel.classList.contains('open')) cerrar();
      else { setAbierto(true); fdPoll(); }
    });
    if (cerrarBtn) cerrarBtn.addEventListener('click', cerrar);
    scrim.addEventListener('click', cerrar);
  }

  // --------------------------------------------------------------------------
  // Tabs en DOS grupos independientes, como los dos TabControl del FormSteer
  // original: #menu (guiado, izquierda) y #menu2 (módulo, derecha). Cada grupo
  // tiene su tab activo propio; los dos paneles se ven a la vez.
  // Deep-link: ?tab= para la izquierda, ?tab2= para la derecha.
  // --------------------------------------------------------------------------
  var GRUPOS = {
    izq: { menu: 'menu',  def: 'pp',      param: 'tab'  },
    der: { menu: 'menu2', def: 'sensors', param: 'tab2' }
  };

  function irATab(grupo, id) {
    var g = GRUPOS[grupo];
    if (!g) return;
    var sec = document.querySelector('section[data-group="' + grupo + '"][data-tab="' + id + '"]');
    if (!sec) { id = g.def; }
    // Layout unificado (2026-08-08): UN solo panel visible en toda la
    // pantalla — elegir una tab apaga la del otro grupo también. Antes las
    // dos columnas mostraban un panel cada una y la ventana ocupaba el doble.
    document.querySelectorAll('#menu button, #menu2 button').forEach(function (b) {
      b.classList.remove('sel');
    });
    document.querySelectorAll('section[data-group]').forEach(function (s) {
      s.classList.remove('activa');
    });
    document.querySelectorAll('#' + g.menu + ' button').forEach(function (b) {
      b.classList.toggle('sel', b.dataset.tab === id);
    });
    var act = document.querySelector('section[data-group="' + grupo + '"][data-tab="' + id + '"]');
    if (act) act.classList.add('activa');
    // Layout cabina (direccion_mejorado): el .tabpanel se muestra con la
    // clase .activo — solo maqueta el grupo que contiene la sección elegida.
    document.querySelectorAll('.tabpanel').forEach(function (p) {
      p.classList.toggle('activo', !!p.querySelector('section.activa'));
    });
    try {
      var url = new URL(window.location.href);
      url.searchParams.set(g.param, id);
      // un solo panel activo → un solo deep-link: se borra el del otro grupo
      var otro = grupo === 'izq' ? GRUPOS.der.param : GRUPOS.izq.param;
      url.searchParams.delete(otro);
      history.replaceState(null, '', url.toString());
    } catch (e) { /* file:// etc. */ }
  }

  function initTabs() {
    Object.keys(GRUPOS).forEach(function (grupo) {
      var g = GRUPOS[grupo];
      document.querySelectorAll('#' + g.menu + ' button').forEach(function (b) {
        b.addEventListener('click', function () { irATab(grupo, b.dataset.tab); });
      });
    });
  }

  // --------------------------------------------------------------------------
  // Ángulo en vivo (panel Set/Actual/Error del FormSteer + barra del tab
  // Dirección). Misma fuente que el gráfico: GET /api/aog/graph-steer a 5 Hz.
  // Si el motor no contesta, los tres quedan en "—" — nunca números viejos.
  // --------------------------------------------------------------------------
  function initLiveAngle() {
    var elSet = $('liveSet'), elAct = $('liveAct'), elErr = $('liveErr');
    if (!elSet && !elAct) return;

    function pinta(set, act) {
      var ok = typeof set === 'number' && typeof act === 'number';
      if (elSet) elSet.textContent = ok ? set.toFixed(1) : '—';
      if (elAct) elAct.textContent = ok ? act.toFixed(1) : '—';
      if (elErr) elErr.textContent = ok ? (set - act).toFixed(1) : '—';

      // Barra de ángulo real del tab Dirección (pbar del original): el fondo
      // de escala es el ángulo máximo configurado en su slider.
      var l = $('angleFillLeft'), r = $('angleFillRight');
      if (l && r) {
        var maxR = document.querySelector('input[data-key="maxSteerAngle"]');
        var max = maxR ? (parseFloat(maxR.value) || 40) : 40;
        var pct = ok ? Math.min(100, Math.abs(act) / max * 100) : 0;
        l.style.width = (ok && act < 0) ? pct + '%' : '0';
        r.style.width = (ok && act >= 0) ? pct + '%' : '0';
      }
    }

    setInterval(function () {
      if (document.visibilityState === 'hidden') return;
      fetch('/api/aog/graph-steer', { cache: 'no-store' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (j) {
          if (j && typeof j.actual_steer_deg === 'number') pinta(j.set_steer_deg, j.actual_steer_deg);
          else pinta(null, null);
        })
        .catch(function () { pinta(null, null); });
    }, 200);
  }

  // --------------------------------------------------------------------------
  // Botón Guardar flotante con estados (idéntico a config.js)
  // --------------------------------------------------------------------------
  var btnG = $('btnGuardarFloat');
  var btnGImg = $('btnGuardarImg');
  var btnGCap = $('btnGuardarCap');
  var btnGTimer = null;

  function marcarSucio() {
    if (btnGTimer) { clearTimeout(btnGTimer); btnGTimer = null; }
    btnG.classList.remove('ok');
    btnGImg.src = '../img/config/FileSave.png';
    btnG.classList.add('dirty');
    btnGCap.textContent = 'Guardar';
  }

  function limpiarSucio(guardado) {
    btnG.classList.remove('dirty');
    if (guardado) {
      btnG.classList.add('ok');
      btnGImg.src = '../img/config/OK64.png';
      btnGCap.textContent = 'Guardado';
      if (btnGTimer) clearTimeout(btnGTimer);
      btnGTimer = setTimeout(function () {
        btnG.classList.remove('ok');
        btnGImg.src = '../img/config/FileSave.png';
        btnGCap.textContent = 'Sin cambios';
      }, 1500);
    } else {
      btnG.classList.remove('ok');
      btnGImg.src = '../img/config/FileSave.png';
      btnGCap.textContent = 'Sin cambios';
    }
  }

  function initSaveButton() {
    // Los inputs numéricos también marcan sucio (los sliders/toggles/segmentados
    // llaman a marcarSucio() en sus propios handlers). Se escucha en #layout,
    // que envuelve las dos columnas del layout FormSteer.
    var raiz = $('layout') || document.body;
    raiz.addEventListener('input', function (ev) {
      if (ev.target && ev.target.matches('input[type=number]')) marcarSucio();
    });
    btnG.addEventListener('click', function () { saveConfig(); });
    // guardar la tab activa si la página se oculta (cierre del widget del Hub)
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'hidden' && btnG.classList.contains('dirty')) saveConfig();
    });
  }

  // --------------------------------------------------------------------------
  // Botones auxiliares (WAS cero, Smart WAS, Restablecer)
  // --------------------------------------------------------------------------
  function initAuxButtons() {
    var bz = $('btnZeroWas');
    if (bz) bz.addEventListener('click', zeroWas);
    var bs = $('btnSmartZeroWas');
    if (bs) bs.addEventListener('click', zeroWas);
    var br = $('btnReset');
    if (br) br.addEventListener('click', function () {
      var doReset = function () { location.reload(); };
      if (window.AgpModal && typeof window.AgpModal.confirm === 'function') {
        window.AgpModal.confirm('Restablecer', '¿Volver a los valores por defecto de esta pantalla?')
          .then(function (ok) { if (ok) doReset(); });
      } else {
        doReset();
      }
    });
  }

  // --------------------------------------------------------------------------
  // Arranque
  // --------------------------------------------------------------------------
  document.addEventListener('DOMContentLoaded', function () {
    initTabs();
    initSliders();
    initCheckboxes();
    initSegments();
    initSaveButton();
    initAuxButtons();
    initFreeDrive();
    initLiveAngle();
    var q = new URLSearchParams(window.location.search);
    // Un solo panel activo: ?tab2 (módulo) tiene prioridad si viene explícito,
    // sino se abre el grupo de guiado (?tab o su default).
    if (q.get('tab2')) irATab('der', q.get('tab2'));
    else irATab('izq', q.get('tab') || GRUPOS.izq.def);
    loadConfig();
  });
})();
