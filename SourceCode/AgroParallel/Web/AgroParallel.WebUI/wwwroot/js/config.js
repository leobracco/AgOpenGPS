// ============================================================================
// config.js — Configuración vehículo/implemento (pages/config.html).
// Réplica de FormConfig: navegación por menú lateral + guardado on-leave.
// API:
//   GET  /api/aog/config                     snapshot completo (metros/km/h/s)
//   POST /api/aog/config/{seccion}          body con los campos de la sección
//   POST /api/aog/config/rolido/accion      { accion }  (tanda 4)
//   POST /api/aog/config/secciones/preparar (tanda 3)
// Convenciones de unidades (igual que el original):
//   settings SIEMPRE en metros — la UI muestra cm (métrico) o in (imperial)
//   enteros. Signos: antenna_offset + = izquierda; hitch_length − = atrás.
// ============================================================================
(function () {
  'use strict';

  var estado = document.getElementById('estado');
  var snap = null;            // snapshot del backend
  var tabActual = null;

  // ---- unidades --------------------------------------------------------------
  function m2disp(m) { return snap.is_metric ? m * 100.0 : m * 39.3701; }   // m → cm|in
  function disp2m(v) { return snap.is_metric ? v * 0.01 : v * 0.0254; }     // cm|in → m
  function unidad() { return snap.is_metric ? 'cm' : 'in'; }
  // Formatos del summary (réplica Units.cs)
  function fmtSmall(m) { return snap.is_metric ? Math.round(m * 100) + ' cm' : Math.round(m * 39.37) + ' in'; }
  function fmtMedium(m) {
    if (snap.is_metric) return (Math.round(m * 100) / 100).toFixed(2) + ' m';
    var ft = m * 3.28084, f = Math.floor(ft), inch = Math.round((ft - f) * 12);
    if (inch === 12) { f++; inch = 0; }
    return f + "' " + inch + '"';
  }

  function setEstado(msg, cls) {
    estado.textContent = msg || '';
    estado.className = cls || '';
  }

  async function api(path, body) {
    var res = await fetch('/api/aog/config' + path, body ? {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    } : { cache: 'no-store' });
    return await res.json();
  }

  // ---- registro de tabs --------------------------------------------------------
  // Cada tab: { enter(), leave() → Promise<bool> } (leave = guardar si hay cambios).
  var tabs = {};


  async function guardar(seccion, body, exitoMsg) {
    try {
      setEstado('Guardando…', '');
      var r = await api('/' + seccion, body);
      if (!r.ok) { setEstado('Error: ' + (r.error || 'desconocido'), 'err'); return false; }
      setEstado(exitoMsg || 'Guardado ✔', 'ok');
      try { limpiarSucio(true); } catch (e) { /* antes del init del botón */ }
      // refrescar snapshot silencioso (los saves recalculan runtime)
      try {
        var s = await api('');
        if (s.ok) { snap = s; pintarFooter(); }
      } catch (e) { /* footer viejo, no crítico */ }
      return true;
    } catch (e) {
      setEstado('Sin conexión: ' + e.message, 'err');
      return false;
    }
  }

  // ---- helpers de input numérico -------------------------------------------------
  function leerNud(input, min, max, conSigno) {
    var v = parseFloat(String(input.value).replace(',', '.'));
    if (isNaN(v)) { input.classList.add('invalido'); return null; }
    if (!conSigno) v = Math.abs(v);
    if (v < min) v = min;
    if (v > max) v = max;
    v = Math.round(v);
    input.classList.remove('invalido');
    input.value = v;
    return v;
  }

  // variante con 1 decimal (timing en segundos)
  function leerNudDec(input, min, max) {
    var v = parseFloat(String(input.value).replace(',', '.'));
    if (isNaN(v)) { input.classList.add('invalido'); return null; }
    if (v < min) v = min;
    if (v > max) v = max;
    v = Math.round(v * 10) / 10;
    input.classList.remove('invalido');
    input.value = v;
    return v;
  }

  // =============================================================================
  // Resumen (solo lectura — réplica ConfigSummaryControl.UpdateSummary)
  // =============================================================================
  tabs.summary = {
    enter: function () {
      document.getElementById('sumPerfil').textContent = 'Perfil: ' + (snap.perfil_activo || '—');
      document.getElementById('sumUnidades').textContent = snap.is_metric ? 'Métrico' : 'Imperial';
      document.getElementById('sumAncho').textContent = fmtMedium(snap.secciones.tool_width);
      document.getElementById('sumSecciones').textContent =
        snap.secciones.is_sections_not_zones ? snap.secciones.num_sections : snap.secciones.num_sections_multi;
      document.getElementById('sumOffset').textContent = fmtSmall(snap.offset.tool_offset);
      document.getElementById('sumOverlap').textContent = fmtSmall(snap.offset.tool_overlap);
      document.getElementById('sumLookahead').textContent = snap.timing.look_ahead_on + ' s';
      document.getElementById('sumTram').textContent = fmtMedium(snap.tram.tram_width);
      document.getElementById('sumWheelbase').textContent = fmtSmall(snap.dimensiones.wheelbase);
    },
    leave: function () { return Promise.resolve(true); }
  };

  // =============================================================================
  // Vehículo: tipo (tabVConfig) — reorganizado 2026-08-10.
  // Cuatro opciones: Rígido / Articulado / Cosechadora / Pulverizadora.
  // La pulverizadora ES un rígido para el motor (vehicle_type 0): la elección
  // se recuerda en localStorage solo para que la UI la resalte. Sin marca ni
  // imagen: el vehículo del mapa es SIEMPRE el triángulo verde
  // (is_vehicle_image=false se fuerza en cada guardado).
  // =============================================================================
  var SUB_KEY = 'pilotx_vehiculo_sub';
  var v = { tipo: 0, sub: 'rigido', dirty: false };

  function vSubDesdeTipo(tipo) {
    if (tipo === 1) return 'cosechadora';
    if (tipo === 2) return 'articulado';
    var guardado = null;
    try { guardado = localStorage.getItem(SUB_KEY); } catch (e) { }
    return guardado === 'pulverizadora' ? 'pulverizadora' : 'rigido';
  }

  function vPintar() {
    document.querySelectorAll('#vTipos .radioimg').forEach(function (el) {
      el.classList.toggle('sel', el.dataset.sub === v.sub);
    });
    document.getElementById('vNotaHarvester').hidden = v.sub !== 'cosechadora';
    var np = document.getElementById('vNotaPulv');
    if (np) np.hidden = v.sub !== 'pulverizadora';
  }

  tabs.vconfig = {
    enter: function () {
      var s = snap.vehiculo;
      v.tipo = s.vehicle_type;
      v.sub = vSubDesdeTipo(v.tipo);
      v.dirty = false;
      vPintar();
    },
    leave: function () {
      if (!v.dirty) return Promise.resolve(true);
      v.dirty = false;
      try { localStorage.setItem(SUB_KEY, v.sub); } catch (e) { }
      // body parcial: marcas y opacidad quedan como estén; sin imagen SIEMPRE
      return guardar('vehiculo', {
        vehicle_type: v.tipo,
        is_vehicle_image: false
      });
    }
  };

  document.querySelectorAll('#vTipos .radioimg').forEach(function (el) {
    el.addEventListener('click', function () {
      v.tipo = +el.dataset.tipo;
      v.sub = el.dataset.sub;
      v.dirty = true;
      vPintar();
    });
  });

  // =============================================================================
  // Vehículo: dimensiones (tabVDimensions)
  // =============================================================================
  var DIM_IMG = { 0: 'RadiusWheelBase.png', 1: 'RadiusWheelBaseHarvester.png', 2: 'RadiusWheelBaseArticulated.png' };
  var dim = { dirty: false };
  // límites del original (cm / in tras FixMinMaxSpinners)
  function limWheelbase() { return snap.is_metric ? [50, 1999] : [20, 787]; }
  function limTrack() { return snap.is_metric ? [20, 2000] : [8, 787]; }
  function limHitch() { return snap.is_metric ? [0, 4000] : [0, 1575]; }

  tabs.vdimensions = {
    enter: function () {
      document.getElementById('dimImg').src = '../img/config/' + DIM_IMG[snap.vehiculo.vehicle_type];
      document.getElementById('nudWheelbase').value = Math.round(m2disp(Math.abs(snap.dimensiones.wheelbase)));
      document.getElementById('nudTrack').value = Math.round(m2disp(Math.abs(snap.dimensiones.track_width)));
      document.getElementById('nudHitch').value = Math.round(m2disp(Math.abs(snap.dimensiones.hitch_length)));
      // hitch visible solo con implemento TBT o de arrastre (réplica del original)
      var estilo = snap.enganche.estilo;
      var conHitch = estilo === 'tbt' || estilo === 'trailing';
      document.getElementById('filaHitch').style.display = conHitch ? '' : 'none';
      document.getElementById('notaHitchOculto').hidden = conHitch;
      dim.dirty = false;
    },
    leave: function () {
      if (!dim.dirty) return Promise.resolve(true);
      var wb = leerNud(document.getElementById('nudWheelbase'), limWheelbase()[0], limWheelbase()[1]);
      var tr = leerNud(document.getElementById('nudTrack'), limTrack()[0], limTrack()[1]);
      var hi = leerNud(document.getElementById('nudHitch'), limHitch()[0], limHitch()[1]);
      if (wb === null || tr === null || hi === null) {
        setEstado('Revisá los valores marcados en rojo', 'err');
        return Promise.resolve(false);
      }
      dim.dirty = false;
      // el backend aplica el signo del hitch según el estilo de enganche
      return guardar('dimensiones', {
        wheelbase: disp2m(wb),
        track_width: disp2m(tr),
        hitch_length: disp2m(hi)
      });
    }
  };
  ['nudWheelbase', 'nudTrack', 'nudHitch'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', function () { dim.dirty = true; });
  });

  // =============================================================================
  // Vehículo: antena (tabVAntenna)
  // =============================================================================
  var ANT_IMG = { 0: 'AntennaTractor.png', 1: 'AntennaHarvester.png', 2: 'AntennaArticulated.png' };
  var ant = { lado: 'centro', dirty: false };
  // quirk del original: los límites de antena NO se convierten a imperial
  var LIM_ANT_HEIGHT = [0, 1000], LIM_ANT_PIVOT = [-999, 999], LIM_ANT_OFFSET = [0, 500];

  function antPintarLados() {
    document.querySelectorAll('#antLados .radioimg').forEach(function (el) {
      el.classList.toggle('sel', el.dataset.lado === ant.lado);
    });
  }

  tabs.vantenna = {
    enter: function () {
      document.getElementById('antImg').src = '../img/config/' + ANT_IMG[snap.vehiculo.vehicle_type];
      document.getElementById('nudAntHeight').value = Math.round(m2disp(snap.antena.antenna_height));
      document.getElementById('nudAntPivot').value = Math.round(m2disp(snap.antena.antenna_pivot));
      document.getElementById('nudAntOffset').value = Math.round(m2disp(Math.abs(snap.antena.antenna_offset)));
      // signo del offset en settings: + = izquierda, − = derecha
      ant.lado = snap.antena.antenna_offset > 0 ? 'izq' : (snap.antena.antenna_offset < 0 ? 'der' : 'centro');
      antPintarLados();
      ant.dirty = false;
    },
    leave: function () {
      if (!ant.dirty) return Promise.resolve(true);
      var h = leerNud(document.getElementById('nudAntHeight'), LIM_ANT_HEIGHT[0], LIM_ANT_HEIGHT[1]);
      var p = leerNud(document.getElementById('nudAntPivot'), LIM_ANT_PIVOT[0], LIM_ANT_PIVOT[1], true);
      var o = leerNud(document.getElementById('nudAntOffset'), LIM_ANT_OFFSET[0], LIM_ANT_OFFSET[1]);
      if (h === null || p === null || o === null) {
        setEstado('Revisá los valores marcados en rojo', 'err');
        return Promise.resolve(false);
      }
      // réplica: offset ≠ 0 sin lado elegido asume derecha; centro fuerza 0
      if (o === 0) ant.lado = 'centro';
      else if (ant.lado === 'centro') { ant.lado = 'der'; antPintarLados(); }
      var offsetM = ant.lado === 'izq' ? disp2m(o) : (ant.lado === 'der' ? -disp2m(o) : 0);
      ant.dirty = false;
      return guardar('antena', {
        antenna_height: disp2m(h),
        antenna_pivot: disp2m(p),
        antenna_offset: offsetM
      });
    }
  };
  ['nudAntHeight', 'nudAntPivot', 'nudAntOffset'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', function () { ant.dirty = true; });
  });
  document.querySelectorAll('#antLados .radioimg').forEach(function (el) {
    el.addEventListener('click', function () {
      ant.lado = el.dataset.lado;
      if (ant.lado === 'centro') document.getElementById('nudAntOffset').value = 0; // réplica
      ant.dirty = true;
      antPintarLados();
    });
  });

  // =============================================================================
  // Implemento: estilo de enganche (tabTConfig)
  // =============================================================================
  var tc = { estilo: 'trailing', dirty: false };

  function tcPintar() {
    document.querySelectorAll('#tcRadios .radioimg').forEach(function (el) {
      el.classList.toggle('sel', el.dataset.estilo === tc.estilo);
    });
  }

  tabs.tconfig = {
    enter: function () {
      var esHarvester = snap.vehiculo.vehicle_type === 1;
      document.getElementById('tcRadios').style.display = esHarvester ? 'none' : '';
      document.getElementById('tcHarvester').hidden = !esHarvester;
      tc.estilo = snap.enganche.estilo;
      tc.dirty = false;
      tcPintar();
    },
    leave: function () {
      if (!tc.dirty) return Promise.resolve(true);
      tc.dirty = false;
      // el backend replica el Leave original: TBT implica trailing, harvester
      // fuerza front, y corrige el signo del hitch según el estilo.
      return guardar('enganche_estilo', { estilo: tc.estilo });
    }
  };
  document.querySelectorAll('#tcRadios .radioimg').forEach(function (el) {
    el.addEventListener('click', function () {
      tc.estilo = el.dataset.estilo; tc.dirty = true; tcPintar();
    });
  });

  // =============================================================================
  // Implemento: distancias de enganche (tabTHitch)
  // =============================================================================
  var th = { dirty: false };
  var TH_IMG = {
    front: 'ToolHitchPageFront.png', rear: 'ToolHitchPageRear.png',
    tbt: 'ToolHitchPageTBT.png', trailing: 'ToolHitchPageTrailing.png',
    harvester: 'ToolHitchPageFrontHarvester.png'
  };
  function limDrawbar() { return snap.is_metric ? [0, 3000] : [0, 1181]; }
  function limTrailingHitch() { return snap.is_metric ? [10, 3000] : [4, 1181]; }

  function thModo() {
    if (snap.vehiculo.vehicle_type === 1) return 'harvester';
    return snap.enganche.estilo; // front | rear | tbt | trailing
  }

  tabs.thitch = {
    enter: function () {
      var modo = thModo();
      document.getElementById('thImg').src = '../img/config/' + TH_IMG[modo];
      var conDrawbar = modo === 'front' || modo === 'rear' || modo === 'harvester';
      var conTrailing = modo === 'tbt' || modo === 'trailing';
      var conTank = modo === 'tbt';
      document.getElementById('thFilaDrawbar').style.display = conDrawbar ? '' : 'none';
      document.getElementById('thFilaTrailing').style.display = conTrailing ? '' : 'none';
      document.getElementById('thFilaTank').style.display = conTank ? '' : 'none';
      // siempre valor absoluto — el backend maneja el signo (trasero/tank/trailing ≤ 0)
      document.getElementById('nudDrawbar').value = Math.round(m2disp(Math.abs(snap.enganche.hitch_length)));
      document.getElementById('nudTrailingHitch').value = Math.round(m2disp(Math.abs(snap.enganche.trailing_hitch_length)));
      document.getElementById('nudTankHitch').value = Math.round(m2disp(Math.abs(snap.enganche.tank_trailing_hitch_length)));
      th.dirty = false;
    },
    leave: function () {
      if (!th.dirty) return Promise.resolve(true);
      var modo = thModo();
      var body = {};
      if (modo === 'front' || modo === 'rear' || modo === 'harvester') {
        var d = leerNud(document.getElementById('nudDrawbar'), limDrawbar()[0], limDrawbar()[1]);
        if (d === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
        body.hitch_length = disp2m(d);
      }
      if (modo === 'tbt' || modo === 'trailing') {
        var t = leerNud(document.getElementById('nudTrailingHitch'), limTrailingHitch()[0], limTrailingHitch()[1]);
        if (t === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
        body.trailing_hitch_length = disp2m(t);
      }
      if (modo === 'tbt') {
        var k = leerNud(document.getElementById('nudTankHitch'), limTrailingHitch()[0], limTrailingHitch()[1]);
        if (k === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
        body.tank_trailing_hitch_length = disp2m(k);
      }
      th.dirty = false;
      return guardar('enganche_dist', body);
    }
  };
  ['nudDrawbar', 'nudTrailingHitch', 'nudTankHitch'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', function () { th.dirty = true; });
  });

  // =============================================================================
  // Implemento: offset lateral + overlap/gap (tabToolOffset)
  // =============================================================================
  var to = { lado: null, modo: null, dirty: false };
  // máximos imperiales con el doble-divide del original (2500/2.54² ≈ 387, 1000/2.54² ≈ 155)
  function limToolOffset() { return snap.is_metric ? [0, 2500] : [0, 387]; }
  function limToolOverlap() { return snap.is_metric ? [0, 1000] : [0, 155]; }

  function toPintar() {
    document.querySelectorAll('#toLados .radioimg').forEach(function (el) {
      el.classList.toggle('sel', el.dataset.lado === to.lado);
    });
    document.querySelectorAll('#toModos .radioimg').forEach(function (el) {
      el.classList.toggle('sel', el.dataset.modo === to.modo);
    });
  }

  tabs.tooloffset = {
    enter: function () {
      var off = snap.offset.tool_offset, ovl = snap.offset.tool_overlap;
      document.getElementById('nudToolOffset').value = Math.round(m2disp(Math.abs(off)));
      document.getElementById('nudToolOverlap').value = Math.round(m2disp(Math.abs(ovl)));
      // tri-estado: 0 ⇒ ningún radio marcado (réplica). Signos: + der / − izq; + overlap / − gap
      to.lado = off > 0 ? 'der' : (off < 0 ? 'izq' : null);
      to.modo = ovl > 0 ? 'overlap' : (ovl < 0 ? 'gap' : null);
      to.dirty = false;
      toPintar();
    },
    leave: function () {
      if (!to.dirty) return Promise.resolve(true);
      var o = leerNud(document.getElementById('nudToolOffset'), limToolOffset()[0], limToolOffset()[1]);
      var v = leerNud(document.getElementById('nudToolOverlap'), limToolOverlap()[0], limToolOverlap()[1]);
      if (o === null || v === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
      // réplica: valor ≠ 0 sin lado elegido asume derecha / overlap
      if (o !== 0 && !to.lado) to.lado = 'der';
      if (o === 0) to.lado = null;
      if (v !== 0 && !to.modo) to.modo = 'overlap';
      if (v === 0) to.modo = null;
      toPintar();
      to.dirty = false;
      return guardar('offset_implemento', {
        tool_offset: to.lado === 'izq' ? -disp2m(o) : disp2m(o),
        tool_overlap: to.modo === 'gap' ? -disp2m(v) : disp2m(v)
      });
    }
  };
  document.querySelectorAll('#toLados .radioimg').forEach(function (el) {
    el.addEventListener('click', function () { to.lado = el.dataset.lado; to.dirty = true; toPintar(); });
  });
  document.querySelectorAll('#toModos .radioimg').forEach(function (el) {
    el.addEventListener('click', function () { to.modo = el.dataset.modo; to.dirty = true; toPintar(); });
  });
  document.getElementById('btnZeroOffset').addEventListener('click', function () {
    document.getElementById('nudToolOffset').value = 0; to.lado = null; to.dirty = true; toPintar();
  });
  document.getElementById('btnZeroOverlap').addEventListener('click', function () {
    document.getElementById('nudToolOverlap').value = 0; to.modo = null; to.dirty = true; toPintar();
  });
  ['nudToolOffset', 'nudToolOverlap'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', function () { to.dirty = true; });
  });

  // =============================================================================
  // Implemento: pivote trailing (tabToolPivot)
  // =============================================================================
  var tp = { pivote: null, dirty: false };
  function limPivot() { return snap.is_metric ? [0, 2000] : [0, 787]; }

  function tpPintar() {
    document.querySelectorAll('#tpLados .radioimg').forEach(function (el) {
      el.classList.toggle('sel', el.dataset.pivote === tp.pivote);
    });
  }

  tabs.toolpivot = {
    enter: function () {
      var p = snap.offset.trailing_tool_to_pivot_length;
      document.getElementById('nudPivot').value = Math.round(m2disp(Math.abs(p)));
      // + = pivote detrás / − = adelante; 0 ⇒ ninguno (tri-estado)
      tp.pivote = p > 0 ? 'behind' : (p < 0 ? 'ahead' : null);
      tp.dirty = false;
      tpPintar();
    },
    leave: function () {
      if (!tp.dirty) return Promise.resolve(true);
      var p = leerNud(document.getElementById('nudPivot'), limPivot()[0], limPivot()[1]);
      if (p === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
      // réplica: acá NO se auto-marca un radio — sin selección el valor va negativo
      tp.dirty = false;
      return guardar('pivote', {
        trailing_tool_to_pivot_length: tp.pivote === 'behind' ? disp2m(p) : -disp2m(p)
      });
    }
  };
  document.querySelectorAll('#tpLados .radioimg').forEach(function (el) {
    el.addEventListener('click', function () { tp.pivote = el.dataset.pivote; tp.dirty = true; tpPintar(); });
  });
  document.getElementById('btnZeroPivot').addEventListener('click', function () {
    document.getElementById('nudPivot').value = 0; tp.pivote = null; tp.dirty = true; tpPintar();
  });
  document.getElementById('nudPivot').addEventListener('input', function () { tp.dirty = true; });

  // =============================================================================
  // Implemento: timing look-ahead (tabTSettings)
  // =============================================================================
  var ts = { dirty: false };

  tabs.tsettings = {
    enter: function () {
      document.getElementById('nudLookAheadOn').value = snap.timing.look_ahead_on;
      document.getElementById('nudLookAheadOff').value = snap.timing.look_ahead_off;
      document.getElementById('nudTurnOffDelay').value = snap.timing.turn_off_delay;
      ts.dirty = false;
    },
    leave: function () {
      if (!ts.dirty) return Promise.resolve(true);
      var on = leerNudDec(document.getElementById('nudLookAheadOn'), 0.2, 22);
      var off = leerNudDec(document.getElementById('nudLookAheadOff'), 0, 20);
      var delay = leerNudDec(document.getElementById('nudTurnOffDelay'), 0, 10);
      if (on === null || off === null || delay === null) {
        setEstado('Revisá los valores marcados en rojo', 'err');
        return Promise.resolve(false);
      }
      // clamp del original: off ≤ 0.8 × on
      if (off > on * 0.8) {
        off = Math.round(on * 0.8 * 10) / 10;
        document.getElementById('nudLookAheadOff').value = off;
      }
      ts.dirty = false;
      return guardar('timing', { look_ahead_on: on, look_ahead_off: off, turn_off_delay: delay });
    }
  };
  // XOR del original: usar Apagado pone Retardo en 0 y viceversa
  document.getElementById('nudLookAheadOff').addEventListener('input', function () {
    ts.dirty = true;
    if (parseFloat(String(this.value).replace(',', '.')) > 0)
      document.getElementById('nudTurnOffDelay').value = 0;
  });
  document.getElementById('nudTurnOffDelay').addEventListener('input', function () {
    ts.dirty = true;
    if (parseFloat(String(this.value).replace(',', '.')) > 0)
      document.getElementById('nudLookAheadOff').value = 0;
  });
  document.getElementById('nudLookAheadOn').addEventListener('input', function () { ts.dirty = true; });

  // =============================================================================
  // Secciones (tabTSections) — el tab más complejo del original
  // =============================================================================
  var sec = {
    modo: 'ind',            // 'ind' = secciones individuales | 'zonas' = simétrico
    num: 1, widths: [],     // modo ind: anchos en unidades de display (cm|in)
    defWidth: 0,
    numMulti: 1, widthMulti: 0, zonas: 2, ranges: [0, 0, 0, 0, 0, 0, 0, 0],
    boundary: false, dirty: false
  };
  // caps del original: 5000 cm | 1900 in de ancho total
  function secCapDisp() { return snap.is_metric ? 5000 : 1900; }
  function limDefWidth() { return snap.is_metric ? [10, 1000] : [3, 393]; } // Min/3.0 quirk imperial
  function limCutoff() { return snap.is_metric ? [0, 30] : [0, 18.6]; }     // km/h | MPH

  function secTotalInd() {
    var t = 0;
    for (var i = 0; i < sec.num; i++) t += sec.widths[i];
    return t;
  }

  function secPintar() {
    var esZonas = sec.modo === 'zonas';
    document.getElementById('secModoImg').src = '../img/config/' + (esZonas ? 'ConT_Symmetric.png' : 'ConT_Asymmetric.png');
    document.getElementById('secModoCap').textContent = esZonas ? 'Secciones simétricas (zonas)' : 'Secciones individuales';
    document.getElementById('secCartaInd').style.display = esZonas ? 'none' : '';
    document.getElementById('secCartaZonas').style.display = esZonas ? '' : 'none';
    document.getElementById('secBoundaryImg').src = '../img/config/' + (sec.boundary ? 'SectionOffBoundary.png' : 'SectionOnBoundary.png');
    document.getElementById('secBoundary').classList.toggle('sel', sec.boundary);
    if (esZonas) secPintarZonas(); else secPintarInd();
  }

  function secPintarInd() {
    var selN = document.getElementById('selNumSections');
    if (!selN.options.length) {
      for (var i = 1; i <= 16; i++) {
        var op = document.createElement('option');
        op.textContent = i; selN.appendChild(op);
      }
    }
    selN.value = sec.num;
    document.getElementById('nudDefaultWidth').value = Math.round(sec.defWidth);

    var g = document.getElementById('secGrilla');
    g.innerHTML = '';
    for (var j = 0; j < sec.num; j++) {
      (function (idx) {
        var c = document.createElement('div');
        c.className = 'celda';
        c.innerHTML = '<span class="cap">' + (idx + 1) + '</span>' +
                      '<input class="nud" type="text" inputmode="numeric" />';
        var inp = c.querySelector('input');
        inp.value = Math.round(sec.widths[idx]);
        inp.addEventListener('input', function () {
          var v = parseFloat(String(inp.value).replace(',', '.'));
          if (!isNaN(v)) sec.widths[idx] = Math.abs(v);
          sec.dirty = true;
          secPintarTotalInd();
        });
        g.appendChild(c);
      })(j);
    }
    secPintarTotalInd();
  }

  function secPintarTotalInd() {
    var t = secTotalInd();
    var el = document.getElementById('secTotal');
    el.textContent = Math.round(t) + ' ' + unidad() + '  (' + fmtMedium(disp2m(t)) + ')';
    el.style.color = t > secCapDisp() ? 'var(--rojo)' : '';
  }

  function secZonasDefault() {
    // reparto del original: división entera, la última absorbe el resto
    var defa = Math.floor(sec.numMulti / sec.zonas);
    for (var k = 0; k < 8; k++) sec.ranges[k] = 0;
    for (var k2 = 1; k2 < sec.zonas; k2++) sec.ranges[k2 - 1] = k2 * defa;
    sec.ranges[sec.zonas - 1] = sec.numMulti;
  }

  function secPintarZonas() {
    document.getElementById('nudNumSectionsMulti').value = sec.numMulti;
    document.getElementById('selZonas').value = sec.zonas;
    document.getElementById('nudWidthMulti').value = Math.round(m2disp(sec.widthMulti) * 10) / 10;

    var g = document.getElementById('zonasGrilla');
    g.innerHTML = '';
    var inicio = 1;
    for (var k = 0; k < sec.zonas; k++) {
      (function (idx, start) {
        var ultima = idx === sec.zonas - 1;
        var c = document.createElement('div');
        c.className = 'celda';
        c.innerHTML = '<span class="cap">Zona ' + (idx + 1) + ': ' + start + ' →</span>' +
                      '<input class="nud" type="text" inputmode="numeric" ' + (ultima ? 'disabled' : '') + ' />';
        var inp = c.querySelector('input');
        inp.value = sec.ranges[idx];
        inp.addEventListener('change', function () {
          var v = parseInt(String(inp.value).replace(',', '.'), 10);
          if (!isNaN(v)) sec.ranges[idx] = Math.max(0, Math.min(v, sec.numMulti));
          sec.dirty = true;
          secPintarZonas(); // refresca los "start" siguientes
        });
        g.appendChild(c);
      })(k, inicio);
      inicio = sec.ranges[k] + 1;
    }
    var tot = sec.numMulti * sec.widthMulti;
    document.getElementById('secTotalZonas').textContent =
      Math.round(m2disp(tot)) + ' ' + unidad() + '  (' + fmtMedium(tot) + ')';
  }

  // ---- Trenes de siembra (fuente única: el implemento central) ---------------
  //
  // Vive en ESTA pantalla porque acá se configuran las secciones, y una
  // sección = un surco. La distancia del tren trasero alimenta el corte
  // retardado (GetSectionsAtDistanceBack) de QuantiX/SectionX.
  var trn = {
    impl: null,      // ImplementoDto del server (null = no cargó, no tocar)
    memoria: [],     // última asignación completa surco→tren (sobrevive retipeos)
    pincel: 1,
    dirty: false
  };
  var TRN_COLORES = ['var(--verde)', '#E0A33E', '#5B8DD9', '#B06AC4'];

  function trnCantidad() {
    return sec.modo === 'ind' ? sec.num : sec.numMulti;
  }

  async function trnCargar() {
    trn.impl = null;
    try {
      var r = await fetch('/api/implemento', { cache: 'no-store' });
      var d = await r.json();
      if (d && d.ok && d.implemento) {
        trn.impl = d.implemento;
        if (!trn.impl.trenes || !trn.impl.trenes.length)
          trn.impl.trenes = [{ id: 1, nombre: 'Delantero', distancia_m: 0 }];
        if (!trn.impl.surcos) trn.impl.surcos = [];
        // La verdad del server pisa la memoria local (mismo criterio que el
        // fix de "Recargar" en config-implemento).
        trn.memoria = trn.impl.surcos.map(function (s) {
          return { numero: s.numero, tren_id: s.tren_id || 1 };
        });
        trn.dirty = false;
      }
    } catch (e) { /* sin implemento: la carta avisa y no se guarda nada */ }
    trnPintar();
  }

  // Regenera la tira a la cantidad actual de secciones conservando la
  // asignación por índice desde la MEMORIA (no desde el array vivo, que un
  // retipeo transitorio puede haber truncado). Espejo de ImplementoSurcos.
  function trnSurcosActuales() {
    var n = trnCantidad();
    var out = [];
    var ultimo = trn.memoria.length ? trn.memoria[trn.memoria.length - 1].tren_id : 1;
    for (var i = 1; i <= n; i++) {
      var t = (i <= trn.memoria.length) ? trn.memoria[i - 1].tren_id : ultimo;
      if (!t || t < 1) t = 1;
      out.push({ numero: i, tren_id: t, seccion_pilotx: i });
    }
    return out;
  }

  function trnActualizarMemoria(surcos) {
    trn.memoria = surcos.map(function (s) { return { numero: s.numero, tren_id: s.tren_id }; });
  }

  function trnPintar() {
    var list = document.getElementById('trnList');
    var msg = document.getElementById('trnMsg');
    if (!list) return;
    if (!trn.impl) {
      list.innerHTML = '';
      document.getElementById('trnBrush').innerHTML = '';
      document.getElementById('trnStrip').innerHTML = '';
      msg.textContent = 'No se pudo cargar el implemento — los trenes no se pueden editar ahora.';
      return;
    }
    msg.textContent = '';
    var trenes = trn.impl.trenes;

    // Lista de trenes: nombre + distancia (tren 1 fija en 0) + borrar.
    list.innerHTML = '';
    trenes.forEach(function (t, i) {
      var fila = document.createElement('div');
      fila.className = 'nudfila';
      var esPrimero = t.id === 1;
      fila.innerHTML =
        '<span style="display:inline-block;width:14px;height:14px;border-radius:3px;background:' +
          TRN_COLORES[i % TRN_COLORES.length] + '"></span>' +
        '<input class="nud" type="text" style="width:160px" value="' +
          String(t.nombre || ('Tren ' + t.id)).replace(/"/g, '&quot;') + '" data-trn-nombre="' + t.id + '">' +
        '<label style="min-width:auto">corta</label>' +
        (esPrimero
          ? '<span class="unidad">al paso (0 m)</span>'
          : '<input class="nud" type="text" inputmode="numeric" style="width:90px" value="' +
              (Math.round((t.distancia_m || 0) * 100) / 100) + '" data-trn-dist="' + t.id + '">' +
            '<span class="unidad">m después</span>' +
            '<button type="button" class="btn" data-trn-del="' + t.id + '">Quitar</button>');
      list.appendChild(fila);
    });

    // Pincel + tira de surcos.
    var brush = document.getElementById('trnBrush');
    brush.innerHTML = '';
    if (trenes.length > 1) {
      trenes.forEach(function (t, i) {
        var b = document.createElement('button');
        b.type = 'button';
        b.className = 'btn';
        b.textContent = t.nombre || ('Tren ' + t.id);
        b.style.borderColor = TRN_COLORES[i % TRN_COLORES.length];
        if (trn.pincel === t.id) {
          b.style.background = TRN_COLORES[i % TRN_COLORES.length];
          b.style.color = '#fff';
        }
        b.addEventListener('click', function () { trn.pincel = t.id; trnPintar(); });
        brush.appendChild(b);
      });
    }

    var strip = document.getElementById('trnStrip');
    strip.innerHTML = '';
    document.getElementById('trnSurcos').style.display = trenes.length > 1 ? '' : 'none';
    if (trenes.length > 1) {
      var surcos = trnSurcosActuales();
      surcos.forEach(function (s) {
        var idx = 0;
        for (var k = 0; k < trenes.length; k++) if (trenes[k].id === s.tren_id) { idx = k; break; }
        var c = document.createElement('div');
        c.className = 'celda';
        c.innerHTML = '<span class="cap">' + s.numero + '</span>' +
          '<div style="width:100%;min-height:44px;border-radius:6px;cursor:pointer;background:' +
          TRN_COLORES[idx % TRN_COLORES.length] + '"></div>';
        c.addEventListener('click', function () {
          var arr = trnSurcosActuales();
          arr[s.numero - 1].tren_id = trn.pincel;
          trnActualizarMemoria(arr);
          trn.dirty = true; sec.dirty = true;
          // Directo, no vía el detector delegado de #main: este handler
          // re-dibuja la tira y cuando el click burbujea la celda ya no
          // cuelga del DOM — el closest() del detector no la matchea y el
          // botón Guardar no se enteraba del cambio.
          marcarSucio();
          trnPintar();
        });
        strip.appendChild(c);
      });
    }
  }

  document.getElementById('trnAdd').addEventListener('click', function () {
    if (!trn.impl) return;
    var trenes = trn.impl.trenes;
    if (trenes.length >= 4) { setEstado('Máximo 4 trenes', 'err'); return; }
    var maxId = 0;
    trenes.forEach(function (t) { if (t.id > maxId) maxId = t.id; });
    trenes.push({ id: maxId + 1, nombre: trenes.length === 1 ? 'Trasero' : ('Tren ' + (maxId + 1)), distancia_m: 0 });
    trn.dirty = true; sec.dirty = true;
    marcarSucio(); // directo: ver comentario en el click de la celda
    trnPintar();
  });

  document.getElementById('trnList').addEventListener('change', function (ev) {
    if (!trn.impl) return;
    var t = ev.target;
    var idN = t.getAttribute('data-trn-nombre');
    var idD = t.getAttribute('data-trn-dist');
    if (idN) {
      var tr1 = trn.impl.trenes.filter(function (x) { return x.id === parseInt(idN, 10); })[0];
      if (tr1) { tr1.nombre = t.value.trim() || ('Tren ' + tr1.id); trn.dirty = true; sec.dirty = true; trnPintar(); }
    } else if (idD) {
      var tr2 = trn.impl.trenes.filter(function (x) { return x.id === parseInt(idD, 10); })[0];
      if (tr2) {
        var v = parseFloat(String(t.value).replace(',', '.'));
        if (isNaN(v) || v < 0) v = 0;
        if (v > 20) v = 20;
        tr2.distancia_m = v;
        trn.dirty = true; sec.dirty = true;
        trnPintar();
      }
    }
  });

  document.getElementById('trnList').addEventListener('click', function (ev) {
    var del = ev.target.getAttribute && ev.target.getAttribute('data-trn-del');
    if (!del || !trn.impl) return;
    var id = parseInt(del, 10);
    trn.impl.trenes = trn.impl.trenes.filter(function (t) { return t.id !== id; });
    // Sus surcos vuelven al tren 1 (memoria incluida, para que no reaparezcan).
    trn.memoria.forEach(function (s) { if (s.tren_id === id) s.tren_id = 1; });
    if (trn.pincel === id) trn.pincel = 1;
    trn.dirty = true; sec.dirty = true;
    marcarSucio(); // directo: el botón Quitar se re-dibuja en este mismo click
    trnPintar();
  });

  // Guarda trenes + surcos en el implemento central. Se llama DESPUÉS de que
  // el guardado de secciones salió bien (la cantidad final ya está firme).
  async function trnGuardar() {
    if (!trn.impl || !trn.dirty) return true;
    try {
      var surcos = trnSurcosActuales();
      trn.impl.surcos = surcos;
      trn.impl.numero_surcos = surcos.length;
      // CLAVE: sincronizar también la lista de secciones del implemento.
      // El backend deriva el Tool nativo del implemento activo con
      // NumSections = secciones.Count — si acá viajaba la lista vieja (la de
      // cuando se abrió la pestaña), guardar trenes PISABA la cantidad de
      // secciones recién guardada ("puse 14, grabé y volvió a 3").
      var secsSync = [];
      for (var si = 1; si <= surcos.length; si++) {
        var prev = (trn.impl.secciones || [])[si - 1] || {};
        secsSync.push({
          id: si,
          nombre: prev.nombre || ('Sección ' + si),
          lookahead_on: prev.lookahead_on || 0,
          lookahead_off: prev.lookahead_off || 0
        });
      }
      trn.impl.secciones = secsSync;
      var anchoM = sec.modo === 'ind'
        ? disp2m(secTotalInd())
        : sec.numMulti * sec.widthMulti;
      trn.impl.ancho_total_m = anchoM;
      if (surcos.length > 0) trn.impl.distancia_entre_surcos_m = anchoM / surcos.length;
      var r = await fetch('/api/implemento', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(trn.impl)
      });
      var d = await r.json();
      if (!d.ok) { setEstado('Secciones guardadas, trenes NO: ' + (d.error || 'error'), 'err'); return false; }
      trn.dirty = false;
      trnActualizarMemoria(surcos);
      return true;
    } catch (e) {
      setEstado('Secciones guardadas, trenes NO: ' + e.message, 'err');
      return false;
    }
  }

  tabs.tsections = {
    enter: function () {
      // réplica del Enter nativo: con lote abierto apaga los masters Auto/Manual
      api('/secciones/preparar', {}).catch(function () {});
      var z = snap.secciones;
      sec.modo = z.is_sections_not_zones ? 'ind' : 'zonas';
      sec.num = z.num_sections;
      sec.widths = z.section_widths.map(function (m) { return m2disp(m); });
      sec.defWidth = m2disp(z.default_section_width);
      sec.numMulti = z.num_sections_multi;
      sec.widthMulti = z.section_width_multi;
      sec.zonas = Math.max(2, Math.min(z.zones, 8));
      sec.ranges = z.zone_ranges.slice(0, 8);
      sec.boundary = z.is_section_off_when_out;
      document.getElementById('nudCutoff').value =
        Math.round((snap.is_metric ? z.slow_speed_cutoff : z.slow_speed_cutoff * 0.621371) * 10) / 10;
      document.getElementById('secCutoffUnidad').textContent = snap.is_metric ? 'km/h' : 'MPH';
      document.getElementById('nudMinCoverage').value = z.min_coverage;
      sec.dirty = false;
      secPintar();
      trnCargar(); // trenes de siembra: fuente única, el implemento central
    },
    leave: function () {
      // Los trenes pueden estar sucios aunque la geometría no (pintaste la
      // tira sin tocar cantidades): guardarlos igual, y que el resultado
      // mande — si fallan, el botón queda en dirty y se puede reintentar.
      if (!sec.dirty) return trnGuardar();
      var cut = leerNudDec(document.getElementById('nudCutoff'), limCutoff()[0], limCutoff()[1]);
      var cov = leerNud(document.getElementById('nudMinCoverage'), 0, 100);
      if (cut === null || cov === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
      var body = {
        is_sections_not_zones: sec.modo === 'ind',
        is_section_off_when_out: sec.boundary,
        slow_speed_cutoff: snap.is_metric ? cut : cut / 0.621371, // el setting SIEMPRE km/h
        min_coverage: cov
      };
      if (sec.modo === 'ind') {
        if (secTotalInd() > secCapDisp()) { setEstado('Ancho total excedido (máx ' + secCapDisp() + ' ' + unidad() + ')', 'err'); return Promise.resolve(false); }
        var dw = leerNud(document.getElementById('nudDefaultWidth'), limDefWidth()[0], limDefWidth()[1]);
        if (dw === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
        body.num_sections = sec.num;
        body.default_section_width = disp2m(dw);
        body.section_widths = sec.widths.map(function (w) { return disp2m(w); });
      } else {
        if (sec.zonas > sec.numMulti) { setEstado('No puede haber más zonas que secciones', 'err'); return Promise.resolve(false); }
        body.num_sections_multi = sec.numMulti;
        body.section_width_multi = sec.widthMulti;
        body.zones = sec.zonas;
        body.zone_ranges = sec.ranges;
      }
      sec.dirty = false;
      // Primero la geometría (manda), después los trenes sobre esa cantidad.
      // El resultado de los trenes también cuenta: si fallan, el botón no
      // muestra "Guardado" y el reintento vuelve a intentarlos (la geometría
      // ya quedó firme y sec.dirty=false evita re-guardarla).
      return guardar('secciones', body).then(function (ok) {
        if (!ok) { sec.dirty = true; return false; }
        return trnGuardar();
      });
    }
  };

  document.getElementById('secModo').addEventListener('click', function () {
    sec.modo = sec.modo === 'ind' ? 'zonas' : 'ind';
    sec.dirty = true;
    secPintar();
    trnPintar(); // el modo cambia la cantidad efectiva de surcos
  });
  document.getElementById('secBoundary').addEventListener('click', function () {
    sec.boundary = !sec.boundary;
    sec.dirty = true;
    secPintar();
  });
  document.getElementById('selNumSections').addEventListener('change', function () {
    sec.num = parseInt(this.value, 10);
    // réplica: cambiar la cantidad PISA todos los anchos con el ancho default
    var dw = leerNud(document.getElementById('nudDefaultWidth'), limDefWidth()[0], limDefWidth()[1]);
    var wide = dw === null ? sec.defWidth : dw;
    if (sec.num * wide > secCapDisp()) {
      wide = snap.is_metric ? 99 : 19; // clamp del original + aviso
      setEstado('Demasiado ancho — anchos reseteados a ' + wide + ' ' + unidad(), 'err');
    }
    sec.defWidth = wide;
    for (var i = 0; i < 16; i++) sec.widths[i] = wide;
    sec.dirty = true;
    secPintarInd();
    trnPintar(); // la tira de trenes acompaña la cantidad (regenera desde memoria)
  });
  document.getElementById('nudDefaultWidth').addEventListener('input', function () {
    var v = parseFloat(String(this.value).replace(',', '.'));
    if (!isNaN(v)) sec.defWidth = Math.abs(v);
    sec.dirty = true;
  });
  // Al CONFIRMAR el ancho (blur/enter, no por tecla — tipear "120" pasaría
  // por "1"): pisa el ancho de TODAS las secciones y recalcula el total,
  // igual que cuando se cambia la cantidad. Antes solo guardaba el default
  // y las celdas quedaban con los anchos viejos hasta tocar la cantidad.
  document.getElementById('nudDefaultWidth').addEventListener('change', function () {
    var dw = leerNud(this, limDefWidth()[0], limDefWidth()[1]);
    if (dw === null) return;
    var wide = dw;
    if (sec.num * wide > secCapDisp()) {
      wide = snap.is_metric ? 99 : 19; // mismo clamp que el cambio de cantidad
      setEstado('Demasiado ancho — anchos reseteados a ' + wide + ' ' + unidad(), 'err');
      this.value = wide;
    }
    sec.defWidth = wide;
    for (var i = 0; i < 16; i++) sec.widths[i] = wide;
    sec.dirty = true;
    secPintarInd();
  });
  document.getElementById('nudNumSectionsMulti').addEventListener('change', function () {
    var v = parseInt(String(this.value).replace(',', '.'), 10);
    if (isNaN(v)) { this.classList.add('invalido'); return; }
    v = Math.max(1, Math.min(v, snap.secciones.max_sections));
    if (v < sec.zonas) {
      setEstado('No puede haber más zonas que secciones', 'err');
      this.value = sec.numMulti;
      return;
    }
    this.classList.remove('invalido');
    sec.numMulti = v;
    sec.dirty = true;
    secZonasDefault();
    secPintarZonas();
    trnPintar(); // ídem: la tira sigue a la cantidad en modo zonas
  });
  document.getElementById('selZonas').addEventListener('change', function () {
    var z = parseInt(this.value, 10);
    if (z > sec.numMulti) {
      setEstado('No puede haber más zonas que secciones', 'err');
      this.value = sec.zonas;
      return;
    }
    sec.zonas = z;
    sec.dirty = true;
    secZonasDefault();
    secPintarZonas();
  });
  document.getElementById('nudWidthMulti').addEventListener('input', function () {
    var v = parseFloat(String(this.value).replace(',', '.'));
    if (!isNaN(v)) sec.widthMulti = disp2m(Math.abs(v));
    sec.dirty = true;
  });
  document.getElementById('nudCutoff').addEventListener('input', function () { sec.dirty = true; });
  document.getElementById('nudMinCoverage').addEventListener('input', function () { sec.dirty = true; });

  // =============================================================================
  // Switches work/steer (tabTSwitches)
  // =============================================================================
  var sw = { workOn: false, workManual: false, workLow: false, steerOn: false, steerManual: false, dirty: false };

  function swPintar() {
    document.getElementById('swWorkOn').classList.toggle('sel', sw.workOn);
    document.getElementById('swWorkManual').classList.toggle('sel', sw.workManual);
    document.getElementById('swWorkAuto').classList.toggle('sel', !sw.workManual);
    document.getElementById('swWorkLow').classList.toggle('sel', sw.workLow);
    document.getElementById('swWorkLowImg').src = '../img/config/' + (sw.workLow ? 'SwitchActiveClosed.png' : 'SwitchActiveOpen.png');
    document.getElementById('swSteerOn').classList.toggle('sel', sw.steerOn);
    document.getElementById('swSteerManual').classList.toggle('sel', sw.steerManual);
    document.getElementById('swSteerAuto').classList.toggle('sel', !sw.steerManual);
    // cascada de habilitación (réplica)
    ['swWorkManual', 'swWorkAuto', 'swWorkLow'].forEach(function (id) {
      document.getElementById(id).classList.toggle('deshab', !sw.workOn);
    });
    ['swSteerManual', 'swSteerAuto'].forEach(function (id) {
      document.getElementById(id).classList.toggle('deshab', !sw.steerOn);
    });
  }

  tabs.tswitches = {
    enter: function () {
      var z = snap.switches;
      sw.workOn = z.work_enabled;
      sw.workManual = z.work_manual_sections;
      sw.workLow = z.work_active_low;
      sw.steerOn = z.steer_enabled;
      sw.steerManual = z.steer_manual_sections;
      sw.dirty = false;
      swPintar();
    },
    leave: function () {
      if (!sw.dirty) return Promise.resolve(true);
      sw.dirty = false;
      return guardar('switches', {
        work_enabled: sw.workOn,
        work_active_low: sw.workLow,
        work_manual_sections: sw.workManual,
        steer_enabled: sw.steerOn,
        steer_manual_sections: sw.steerManual
      });
    }
  };
  document.getElementById('swWorkOn').addEventListener('click', function () { sw.workOn = !sw.workOn; sw.dirty = true; swPintar(); });
  document.getElementById('swWorkManual').addEventListener('click', function () { sw.workManual = true; sw.dirty = true; swPintar(); });
  document.getElementById('swWorkAuto').addEventListener('click', function () { sw.workManual = false; sw.dirty = true; swPintar(); });
  document.getElementById('swWorkLow').addEventListener('click', function () { sw.workLow = !sw.workLow; sw.dirty = true; swPintar(); });
  document.getElementById('swSteerOn').addEventListener('click', function () { sw.steerOn = !sw.steerOn; sw.dirty = true; swPintar(); });
  document.getElementById('swSteerManual').addEventListener('click', function () { sw.steerManual = true; sw.dirty = true; swPintar(); });
  document.getElementById('swSteerAuto').addEventListener('click', function () { sw.steerManual = false; sw.dirty = true; swPintar(); });

  // =============================================================================
  // Pines relay (tabRelay) — SIN guardado on-leave: solo "Enviar + Guardar"
  // (réplica: salir de la tab sin enviar descarta los cambios)
  // =============================================================================
  var RELAY_OPCIONES = ['—'];
  for (var ri = 1; ri <= 16; ri++) RELAY_OPCIONES.push('Sección ' + ri);
  RELAY_OPCIONES.push('Hidráulico subir', 'Hidráulico bajar', 'Tram derecha', 'Tram izquierda', 'Geo Stop');

  var relay = { pins: [] };

  function relayPintar() {
    var g = document.getElementById('relayGrilla');
    g.innerHTML = '';
    for (var p = 0; p < 24; p++) {
      (function (idx) {
        var c = document.createElement('div');
        c.className = 'celda';
        var sel = document.createElement('select');
        sel.className = 'nud';
        sel.setAttribute('data-no-keyboard', '');
        RELAY_OPCIONES.forEach(function (t, i) {
          var op = document.createElement('option');
          op.value = i; op.textContent = t;
          sel.appendChild(op);
        });
        sel.value = relay.pins[idx];
        sel.addEventListener('change', function () {
          relay.pins[idx] = parseInt(sel.value, 10);
          document.getElementById('relayPendiente').hidden = false;
        });
        var cap = document.createElement('span');
        cap.className = 'cap'; cap.textContent = 'Pin ' + (idx + 1); // label = índice+1 (réplica)
        c.appendChild(cap); c.appendChild(sel);
        g.appendChild(c);
      })(p);
    }
  }

  tabs.relay = {
    enter: function () {
      relay.pins = snap.relay.pins.slice(0, 24);
      document.getElementById('relayPendiente').hidden = true;
      relayPintar();
    },
    leave: function () {
      // réplica: NO guarda — descarta cambios pendientes
      document.getElementById('relayPendiente').hidden = true;
      return Promise.resolve(true);
    }
  };
  document.getElementById('btnRelayDefault').addEventListener('click', function () {
    for (var i = 0; i < 24; i++) relay.pins[i] = i < 3 ? i + 1 : 0; // Sección 1..3, resto sin función
    document.getElementById('relayPendiente').hidden = false;
    relayPintar();
  });
  document.getElementById('btnRelayNone').addEventListener('click', function () {
    for (var i = 0; i < 24; i++) relay.pins[i] = 0;
    document.getElementById('relayPendiente').hidden = false;
    relayPintar();
  });
  document.getElementById('btnRelaySend').addEventListener('click', async function () {
    var ok = await guardar('relay', { pins: relay.pins }, 'Enviado al módulo ✔');
    if (ok) document.getElementById('relayPendiente').hidden = true;
  });

  // =============================================================================
  // Máquina (tabAMachine) — igual que relay: solo guarda con "Enviar + Guardar"
  // =============================================================================
  var ma = { hydOn: false, invert: false };

  function maPintar() {
    document.getElementById('maHydOnImg').src = '../img/config/' + (ma.hydOn ? 'SwitchOn.png' : 'SwitchOff.png');
    document.getElementById('maHydOn').classList.toggle('sel', ma.hydOn);
    document.getElementById('maInvert').classList.toggle('sel', ma.invert);
    // hyd off deshabilita los 3 nud del grupo (réplica)
    ['nudRaiseTime', 'nudLowerTime', 'nudHydLookAhead'].forEach(function (id) {
      document.getElementById(id).disabled = !ma.hydOn;
    });
  }

  function maPendiente() { document.getElementById('maPendiente').hidden = false; }

  tabs.amachine = {
    enter: function () {
      var z = snap.maquina;
      ma.hydOn = z.hyd_on;
      ma.invert = z.invert_relays;
      document.getElementById('nudRaiseTime').value = z.raise_time;
      document.getElementById('nudLowerTime').value = z.lower_time;
      document.getElementById('nudHydLookAhead').value = z.hyd_lift_look_ahead;
      document.getElementById('nudUser1').value = z.user1;
      document.getElementById('nudUser2').value = z.user2;
      document.getElementById('nudUser3').value = z.user3;
      document.getElementById('nudUser4').value = z.user4;
      document.getElementById('maPendiente').hidden = true;
      maPintar();
    },
    leave: function () {
      // réplica: sin "Enviar + Guardar" los cambios se descartan
      document.getElementById('maPendiente').hidden = true;
      return Promise.resolve(true);
    }
  };
  document.getElementById('maHydOn').addEventListener('click', function () { ma.hydOn = !ma.hydOn; maPendiente(); maPintar(); });
  document.getElementById('maInvert').addEventListener('click', function () { ma.invert = !ma.invert; maPendiente(); maPintar(); });
  ['nudRaiseTime', 'nudLowerTime', 'nudHydLookAhead', 'nudUser1', 'nudUser2', 'nudUser3', 'nudUser4'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', maPendiente);
  });
  document.getElementById('btnMachineSend').addEventListener('click', async function () {
    var raise = leerNud(document.getElementById('nudRaiseTime'), 1, 255);
    var lower = leerNud(document.getElementById('nudLowerTime'), 1, 255);
    var la = leerNudDec(document.getElementById('nudHydLookAhead'), 1, 20);
    var u1 = leerNud(document.getElementById('nudUser1'), 0, 255);
    var u2 = leerNud(document.getElementById('nudUser2'), 0, 255);
    var u3 = leerNud(document.getElementById('nudUser3'), 0, 255);
    var u4 = leerNud(document.getElementById('nudUser4'), 0, 255);
    if ([raise, lower, la, u1, u2, u3, u4].some(function (x) { return x === null; })) {
      setEstado('Revisá los valores marcados en rojo', 'err');
      return;
    }
    var ok = await guardar('maquina', {
      hyd_on: ma.hydOn,
      invert_relays: ma.invert,
      raise_time: raise,
      lower_time: lower,
      hyd_lift_look_ahead: la,
      user1: u1, user2: u2, user3: u3, user4: u4
    }, 'Enviado al módulo ✔');
    if (ok) document.getElementById('maPendiente').hidden = true;
  });

  // =============================================================================
  // Rumbo (tabDHeading)
  // =============================================================================
  var hd = { source: 'Fix', minStep: false, autoSwitch: false, rtk: false,
             rtkKill: false, reverse: false, imu: false, dirty: false };

  // variante 2 decimales (offset dual, radios U-turn)
  function leerNudDec2(input, min, max) {
    var v = parseFloat(String(input.value).replace(',', '.'));
    if (isNaN(v)) { input.classList.add('invalido'); return null; }
    if (v < min) v = min;
    if (v > max) v = max;
    v = Math.round(v * 100) / 100;
    input.classList.remove('invalido');
    input.value = v;
    return v;
  }

  function hdPintar() {
    document.getElementById('rbHeadDual').classList.toggle('sel', hd.source === 'Dual');
    document.getElementById('rbHeadFix').classList.toggle('sel', hd.source === 'Fix');
    document.getElementById('hdAutoSwitch').classList.toggle('sel', hd.autoSwitch);
    document.getElementById('hdRtk').classList.toggle('sel', hd.rtk);
    document.getElementById('hdRtkKill').classList.toggle('sel', hd.rtkKill);
    document.getElementById('hdReverse').classList.toggle('sel', hd.reverse);
    document.getElementById('hdMinStep').classList.toggle('sel', hd.minStep);
    // textos dinámicos de paso mínimo (réplica UpdateStepDistanceUI)
    document.getElementById('hdMinStepCap').textContent =
      'Paso mínimo: ' + (hd.minStep ? (snap.is_metric ? '10 cm' : '3.93 in') : (snap.is_metric ? '5 cm' : '1.96 in'));
    document.getElementById('hdStepDist').textContent =
      hd.minStep ? (snap.is_metric ? '100 cm' : '39.3 in') : (snap.is_metric ? '50 cm' : '19.68 in');
    // habilitación de cartas (réplica SetAutoSwitchDualFixPanelOptions)
    var dualOn = hd.source === 'Dual' || hd.autoSwitch;
    var singleOn = hd.source === 'Fix' || hd.autoSwitch;
    hdCarta('hdCartaDual', dualOn);
    hdCarta('hdCartaSingle', singleOn);
    // con auto-switch activo no se puede pasar a Fix a mano (réplica)
    document.getElementById('rbHeadFix').classList.toggle('deshab', hd.autoSwitch);
    // fusión solo con IMU presente (o auto-switch, que la fuerza)
    document.getElementById('hsbarFusion').disabled = !(hd.imu || hd.autoSwitch);
  }

  function hdCarta(id, on) {
    var el = document.getElementById(id);
    el.style.opacity = on ? '' : '0.45';
    el.style.pointerEvents = on ? '' : 'none';
  }

  function hdPintarFusion() {
    var v = +document.getElementById('hsbarFusion').value;
    document.getElementById('lblFusionIMU').textContent = (100 - v) + '%';
    document.getElementById('lblFusion').textContent = v + '%';
  }

  // velocidad de conmutación: interno SIEMPRE km/h; display km/h | mph
  function hdSwitchSpeedDisp(kmh) { return snap.is_metric ? kmh : kmh * 0.621371; }

  tabs.heading = {
    enter: function () {
      var z = snap.rumbo;
      hd.source = z.heading_source === 'Dual' ? 'Dual' : 'Fix';
      hd.minStep = z.min_gps_step;
      hd.autoSwitch = z.auto_switch_dual_fix;
      hd.rtk = z.is_rtk;
      hd.rtkKill = z.is_rtk_kill_autosteer;
      hd.reverse = z.reverse_on;
      hd.imu = z.imu_present;
      document.getElementById('nudDualHeadingOffset').value = Math.round(z.dual_heading_offset * 10) / 10;
      document.getElementById('nudDualReverseDistance').value = Math.round(z.dual_reverse_distance * 100) / 100;
      document.getElementById('nudAutoSwitchSpeed').value = Math.round(hdSwitchSpeedDisp(z.auto_switch_speed) * 10) / 10;
      document.getElementById('hdSwitchUnidad').textContent = snap.is_metric ? 'km/h' : 'mph';
      document.getElementById('nudFixJump').value = z.jump_fix_distance;
      document.getElementById('hsbarFusion').value = z.fusion;
      hdPintarFusion();
      hd.dirty = false;
      hdPintar();
    },
    leave: function () {
      if (!hd.dirty) return Promise.resolve(true);
      var off = leerNudDec2(document.getElementById('nudDualHeadingOffset'), -100, 100);
      var rev = leerNudDec2(document.getElementById('nudDualReverseDistance'), 0.1, 0.9);
      var spdLim = snap.is_metric ? [1, 10] : [0.62, 6.21];
      var spd = leerNudDec(document.getElementById('nudAutoSwitchSpeed'), spdLim[0], spdLim[1]);
      var jump = leerNud(document.getElementById('nudFixJump'), 0, 1000);
      if (off === null || rev === null || spd === null || jump === null) {
        setEstado('Revisá los valores marcados en rojo', 'err');
        return Promise.resolve(false);
      }
      hd.dirty = false;
      return guardar('rumbo', {
        fusion: +document.getElementById('hsbarFusion').value,
        is_rtk: hd.rtk,
        is_rtk_kill_autosteer: hd.rtkKill,
        reverse_on: hd.reverse,
        auto_switch_dual_fix: hd.autoSwitch,
        auto_switch_speed: snap.is_metric ? spd : spd / 0.621371, // SIEMPRE km/h
        jump_fix_distance: jump,
        dual_heading_offset: off,
        dual_reverse_distance: rev
      });
    }
  };

  document.getElementById('rbHeadDual').addEventListener('click', function () {
    if (hd.source === 'Dual') return;
    hd.source = 'Dual';
    // efecto inmediato del original (escribe setting al togglear)
    api('/rumbo', { heading_source: 'Dual' }).catch(function () {});
    hdPintar();
  });
  document.getElementById('rbHeadFix').addEventListener('click', function () {
    if (hd.source === 'Fix' || hd.autoSwitch) return;
    hd.source = 'Fix';
    api('/rumbo', { heading_source: 'Fix' }).catch(function () {});
    hdPintar();
  });
  document.getElementById('hdMinStep').addEventListener('click', function () {
    hd.minStep = !hd.minStep;
    api('/rumbo', { min_gps_step: hd.minStep }).catch(function () {}); // inmediato (réplica)
    hdPintar();
  });
  document.getElementById('hdAutoSwitch').addEventListener('click', function () {
    hd.autoSwitch = !hd.autoSwitch;
    hd.dirty = true;
    hdPintar();
  });
  document.getElementById('hdRtk').addEventListener('click', function () { hd.rtk = !hd.rtk; hd.dirty = true; hdPintar(); });
  document.getElementById('hdRtkKill').addEventListener('click', function () { hd.rtkKill = !hd.rtkKill; hd.dirty = true; hdPintar(); });
  document.getElementById('hdReverse').addEventListener('click', function () { hd.reverse = !hd.reverse; hd.dirty = true; hdPintar(); });
  document.getElementById('hsbarFusion').addEventListener('input', function () { hd.dirty = true; hdPintarFusion(); });
  ['nudDualHeadingOffset', 'nudDualReverseDistance', 'nudAutoSwitchSpeed', 'nudFixJump'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', function () { hd.dirty = true; });
  });

  // =============================================================================
  // Rolido (tabDRoll)
  // =============================================================================
  var rl = { invert: false, dirty: false };

  function rlPintarZero(v, imuPresente) {
    document.getElementById('rollZeroLbl').textContent =
      imuPresente === false ? '***' : (Math.round(v * 100) / 100).toFixed(2);
  }

  // Tractor de atrás EN VIVO (D9): muestra el rolido QUE USA PILOTX
  // (/api/aog/graph-correction → roll_degrees, con cero e inversión YA
  // aplicados por el motor). Antes mostraba el crudo del ECU y "poner en
  // cero" parecía no hacer nada: el cero es un offset del lado de PilotX,
  // el crudo no cambia (reporte 2026-08-10). Con esta fuente, al tocar el
  // cero el tractor se ENDEREZA — que es lo que el operario espera ver.
  var rollLiveTimer = null;
  function rollLivePoll() {
    fetch('/api/aog/graph-correction', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (s) {
        var g = document.getElementById('rollTractorG');
        var v = document.getElementById('rollLiveVal');
        if (!g || !v) return;
        if (!s || s.roll_present === false) {
          g.setAttribute('transform', 'rotate(0 115 88)');
          v.textContent = 'sin IMU';
          v.style.color = '#D0504A';
          return;
        }
        var roll = +s.roll_degrees || 0;
        var ang = Math.max(-30, Math.min(30, roll));
        g.setAttribute('transform', 'rotate(' + ang.toFixed(1) + ' 115 88)');
        v.textContent = (roll > 0 ? '+' : '') + roll.toFixed(1) + '°';
        v.style.color = '';
      })
      .catch(function () { /* motor ocupado: queda el último cuadro */ });
  }
  function rollLiveStart() {
    if (rollLiveTimer) return;
    rollLivePoll();
    rollLiveTimer = setInterval(function () {
      if (document.visibilityState === 'hidden') return;
      rollLivePoll();
    }, 500);
  }
  function rollLiveStop() {
    if (rollLiveTimer) { clearInterval(rollLiveTimer); rollLiveTimer = null; }
  }

  tabs.roll = {
    enter: function () {
      var z = snap.rolido;
      rl.invert = z.invert_roll;
      rlPintarZero(z.roll_zero, true);
      document.getElementById('hsbarRollFilter').value = z.roll_filter;
      document.getElementById('rollFilterPct').textContent = z.roll_filter + '%';
      document.getElementById('rollInvert').classList.toggle('sel', rl.invert);
      rl.dirty = false;
      rollLiveStart();
    },
    leave: function () {
      rollLiveStop();
      if (!rl.dirty) return Promise.resolve(true);
      rl.dirty = false;
      return guardar('rolido', {
        roll_filter: +document.getElementById('hsbarRollFilter').value,
        invert_roll: rl.invert
      });
    }
  };

  async function rollAccion(accion) {
    try {
      var r = await api('/rolido/accion', { accion: accion });
      if (!r.ok) {
        if (r.error === 'sin-imu') { rlPintarZero(0, false); setEstado('Sin datos de IMU', 'err'); }
        else setEstado('Error: ' + (r.error || 'desconocido'), 'err');
        return;
      }
      rlPintarZero(r.roll_zero, r.imu_present || accion === 'quitar');
      setEstado('Aplicado ✔', 'ok');
    } catch (e) { setEstado('Sin conexión: ' + e.message, 'err'); }
  }
  document.getElementById('btnRollZero').addEventListener('click', function () { rollAccion('zero'); });
  // Tocar el TRACTOR también pone el cero (con la máquina nivelada) — es el
  // gesto natural: "está derecho, marcalo así" (pedido 2026-08-10).
  var svgTractor = document.getElementById('rollTractorSvg');
  if (svgTractor) {
    svgTractor.style.cursor = 'pointer';
    svgTractor.addEventListener('click', function () { rollAccion('zero'); });
  }
  document.getElementById('btnRollRemove').addEventListener('click', function () { rollAccion('quitar'); });
  document.getElementById('btnRollUp').addEventListener('click', function () { rollAccion('subir'); });
  document.getElementById('btnRollDown').addEventListener('click', function () { rollAccion('bajar'); });
  document.getElementById('btnResetImu').addEventListener('click', function () { rollAccion('reset_imu'); });
  document.getElementById('rollInvert').addEventListener('click', function () {
    rl.invert = !rl.invert;
    document.getElementById('rollInvert').classList.toggle('sel', rl.invert);
    // Se aplica AL TOQUE (no al salir de la tab): la calibración es visual —
    // el operario invierte y mira el tractorcito; dejarlo "sucio" hasta
    // Guardar rompía el flujo (reporte 2026-08-10). El filtro sigue con el
    // guardado normal (no es interactivo).
    rl.dirty = false;
    guardar('rolido', {
      roll_filter: +document.getElementById('hsbarRollFilter').value,
      invert_roll: rl.invert
    }, 'Rolido ' + (rl.invert ? 'invertido' : 'normal') + ' ✔');
  });
  document.getElementById('hsbarRollFilter').addEventListener('input', function () {
    rl.dirty = true;
    document.getElementById('rollFilterPct').textContent = this.value + '%';
  });

  // =============================================================================
  // U-Turn (tabUTurn) — settings en METROS; display m | ft (×3.28)
  // =============================================================================
  var ut = { smoothing: 14, ext: 20, dirty: false };
  function utM2disp(m) { return snap.is_metric ? m : m * 3.28; }
  function utDisp2m(v) { return snap.is_metric ? v : v / 3.28; }

  function utPintarExt() {
    document.getElementById('utExtLbl').textContent =
      snap.is_metric ? ut.ext + ' m' : Math.round(ut.ext * 3.28) + ' ft';
    document.getElementById('utSmoothLbl').textContent = ut.smoothing;
  }

  tabs.uturn = {
    enter: function () {
      var z = snap.uturn;
      ut.smoothing = z.smoothing;
      ut.ext = z.extension_length;
      document.getElementById('nudUturnRadius').value = Math.round(utM2disp(z.radius) * 100) / 100;
      document.getElementById('nudUturnDistance').value = Math.round(utM2disp(z.distance_from_boundary) * 100) / 100;
      document.getElementById('utUnidad1').textContent = snap.is_metric ? 'm' : 'ft';
      document.getElementById('utUnidad2').textContent = snap.is_metric ? 'm' : 'ft';
      utPintarExt();
      ut.dirty = false;
    },
    leave: function () {
      if (!ut.dirty) return Promise.resolve(true);
      var rLim = snap.is_metric ? [2, 100] : [6.56, 328];
      var dLim = snap.is_metric ? [0.2, 100] : [0.66, 328];
      var rad = leerNudDec2(document.getElementById('nudUturnRadius'), rLim[0], rLim[1]);
      var dist = leerNudDec2(document.getElementById('nudUturnDistance'), dLim[0], dLim[1]);
      if (rad === null || dist === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
      ut.dirty = false;
      return guardar('uturn', {
        radius: utDisp2m(rad),
        distance_from_boundary: utDisp2m(dist),
        extension_length: ut.ext,
        smoothing: ut.smoothing
      });
    }
  };

  document.getElementById('btnSmoothUp').addEventListener('click', function () {
    ut.smoothing = Math.min(50, ut.smoothing + 2); ut.dirty = true; utPintarExt();
  });
  document.getElementById('btnSmoothDn').addEventListener('click', function () {
    ut.smoothing = Math.max(8, ut.smoothing - 2); ut.dirty = true; utPintarExt();
  });
  document.getElementById('btnExtUp').addEventListener('click', function () {
    ut.ext = Math.min(50, ut.ext + 1); ut.dirty = true; utPintarExt();
  });
  document.getElementById('btnExtDn').addEventListener('click', function () {
    ut.ext = Math.max(3, ut.ext - 1); ut.dirty = true; utPintarExt();
  });
  ['nudUturnRadius', 'nudUturnDistance'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', function () { ut.dirty = true; });
  });

  // =============================================================================
  // Tram (tabTram) — tram_width en METROS; display cm | in
  // =============================================================================
  var tr = { display: false, override: false, dirty: false };

  tabs.tram = {
    enter: function () {
      var z = snap.tram;
      tr.display = z.display_tram_control;
      tr.override = z.outer_inverted;
      document.getElementById('nudTramWidth').value = Math.round(m2disp(Math.abs(z.tram_width)));
      document.getElementById('tramDisplay').classList.toggle('sel', tr.display);
      document.getElementById('tramOverride').classList.toggle('sel', tr.override);
      tr.dirty = false;
    },
    leave: function () {
      if (!tr.dirty) return Promise.resolve(true);
      var lim = snap.is_metric ? [1, 10000] : [1, 3937];
      var w = leerNud(document.getElementById('nudTramWidth'), lim[0], lim[1]);
      if (w === null) { setEstado('Revisá los valores marcados en rojo', 'err'); return Promise.resolve(false); }
      tr.dirty = false;
      return guardar('tram', {
        tram_width: disp2m(w),
        display_tram_control: tr.display,
        outer_inverted: tr.override
      });
    }
  };

  document.getElementById('tramDisplay').addEventListener('click', function () {
    tr.display = !tr.display; tr.dirty = true;
    this.classList.toggle('sel', tr.display);
  });
  document.getElementById('tramOverride').addEventListener('click', function () {
    tr.override = !tr.override; tr.dirty = true;
    this.classList.toggle('sel', tr.override);
  });
  document.getElementById('nudTramWidth').addEventListener('input', function () { tr.dirty = true; });

  // =============================================================================
  // Display (tabDisplay) — toggles data-k espejo del snapshot
  // =============================================================================
  var DISPLAY_KEYS = ['polygons', 'speedo', 'keyboard', 'brightness', 'svenn_arrow',
    'start_full_screen', 'log_elevation', 'floor', 'grid', 'extra_guides',
    'direction_markers', 'section_lines', 'headland_distance', 'line_smooth'];
  var di = { vals: {}, dirty: false };

  function diToggles() {
    return document.querySelectorAll('section[data-tab="display"] .chkimg[data-k]');
  }

  function diBody() {
    var body = {};
    DISPLAY_KEYS.forEach(function (k) { body[k] = !!di.vals[k]; });
    var n = leerNud(document.getElementById('nudNumGuideLines'), 1, 5000);
    if (n !== null) body.num_guide_lines = n;
    return body;
  }

  tabs.display = {
    enter: function () {
      DISPLAY_KEYS.forEach(function (k) { di.vals[k] = !!snap.display[k]; });
      diToggles().forEach(function (el) { el.classList.toggle('sel', !!di.vals[el.dataset.k]); });
      document.getElementById('nudNumGuideLines').value = snap.display.num_guide_lines;
      document.getElementById('rbMetric').classList.toggle('sel', snap.is_metric);
      document.getElementById('rbImperial').classList.toggle('sel', !snap.is_metric);
      di.dirty = false;
    },
    leave: function () {
      if (!di.dirty) return Promise.resolve(true);
      di.dirty = false;
      return guardar('display', diBody());
    }
  };

  diToggles().forEach(function (el) {
    el.addEventListener('click', function () {
      var k = el.dataset.k;
      di.vals[k] = !di.vals[k];
      di.dirty = true;
      el.classList.toggle('sel', di.vals[k]);
    });
  });

  // cambiar unidades guarda TODO y recarga (réplica: el original cierra el form)
  async function cambiarUnidades(metrico) {
    if (snap.is_metric === metrico) return;
    var body = diBody();
    body.is_metric = metrico;
    var ok = await guardar('display', body);
    if (ok) location.reload();
  }
  document.getElementById('rbMetric').addEventListener('click', function () { cambiarUnidades(true); });
  document.getElementById('rbImperial').addEventListener('click', function () { cambiarUnidades(false); });

  // =============================================================================
  // Botones / features (tabBtns) — todo se guarda on-leave (réplica)
  // =============================================================================
  var BOT_KEYS = ['feature_tram', 'feature_headland', 'feature_boundary', 'feature_rec_path',
    'feature_ab_smooth', 'feature_hide_contour', 'feature_webcam', 'feature_offset_fix',
    'feature_uturn', 'feature_lateral', 'feature_nudge',
    'sound_steer', 'sound_turn', 'sound_hyd_lift', 'sound_sections',
    'auto_start_corex', 'auto_off_corex', 'shutdown_no_power', 'hardware_messages'];
  var bo = { vals: {}, dirty: false };

  function boToggles() {
    return document.querySelectorAll('section[data-tab="botones"] .chkimg[data-k]');
  }

  tabs.botones = {
    enter: function () {
      BOT_KEYS.forEach(function (k) { bo.vals[k] = !!snap.botones[k]; });
      boToggles().forEach(function (el) { el.classList.toggle('sel', !!bo.vals[el.dataset.k]); });
      bo.dirty = false;
    },
    leave: function () {
      if (!bo.dirty) return Promise.resolve(true);
      var body = {};
      BOT_KEYS.forEach(function (k) { body[k] = !!bo.vals[k]; });
      bo.dirty = false;
      return guardar('botones', body);
    }
  };

  boToggles().forEach(function (el) {
    el.addEventListener('click', function () {
      var k = el.dataset.k;
      bo.vals[k] = !bo.vals[k];
      bo.dirty = true;
      el.classList.toggle('sel', bo.vals[k]);
    });
  });

  // =============================================================================
  // Navegación (réplica del menú lateral + Leave por tab)
  // =============================================================================
  async function irATab(id) {
    if (!tabs[id]) id = 'summary';
    if (tabActual === id) return;
    if (tabActual && tabs[tabActual]) {
      var ok = await tabs[tabActual].leave();
      if (!ok) return; // valores inválidos o error de guardado: no navega
    }
    tabActual = id;
    document.querySelectorAll('#menu button').forEach(function (b) {
      b.classList.toggle('sel', b.dataset.tab === id);
    });
    // El menú es un acordeón: dejar abierto el grupo del tab activo.
    try { if (window.AgpMenuAcordeon) window.AgpMenuAcordeon.abrirGrupoActivo(); } catch (e) { }
    document.querySelectorAll('section[data-tab]').forEach(function (s) {
      s.classList.toggle('activa', s.dataset.tab === id);
    });
    try {
      var url = new URL(window.location.href);
      url.searchParams.set('tab', id);
      history.replaceState(null, '', url.toString());
    } catch (e) { /* file:// etc. */ }
    tabs[id].enter();
    // Volviendo de un módulo embebido a una pestaña de config, el botón
    // flotante de guardar tiene que reaparecer.
    mostrarGuardar(true);
    // si el leave no guardó nada (sin cambios, o relay que descarta) el
    // botón flotante vuelve a "Sin cambios"; si guardó, guardar() ya puso
    // el tilde y no lo pisamos.
    try { if (btnG.classList.contains('dirty')) limpiarSucio(false); } catch (e) { }
    document.getElementById('main').scrollTop = 0;
  }

  document.querySelectorAll('#menu button').forEach(function (b) {
    b.addEventListener('click', function () {
      // Acciones nativas (no navegan ni embeben): p.ej. WiFi de Windows, que
      // el host WebView2 abre al recibir el postMessage.
      if (b.dataset.action === 'wifi') {
        try {
          var wv = window.chrome && window.chrome.webview;
          if (wv) wv.postMessage('open-wifi-settings');
        } catch (e) { /* fuera de WebView2: no-op */ }
        return;
      }
      // Módulos X-*: se muestran EMBEBIDOS (iframe en modo widget) dentro de
      // config, sin salir ni cambiar de estilo. Carga perezosa: el iframe solo
      // apunta a la página cuando se toca el módulo.
      if (b.dataset.mod) {
        var fr = document.getElementById('modIframe');
        var url = b.dataset.mod + (b.dataset.mod.indexOf('?') < 0 ? '?widget=1' : '&widget=1');
        if (fr.getAttribute('src') !== url) fr.setAttribute('src', url);
        // El botón flotante Guardar es de la config del vehículo; dentro de un
        // módulo no guarda nada y encima tapa los controles del iframe (tapaba
        // el toggle del overlay de FlowX en el Hub). Cada módulo guarda lo suyo.
        mostrarGuardar(false);
        document.querySelectorAll('#menu button').forEach(function (x) {
          x.classList.toggle('sel', x === b);
        });
        try { if (window.AgpMenuAcordeon) window.AgpMenuAcordeon.abrirGrupoActivo(); } catch (e) { }
        document.querySelectorAll('section[data-tab]').forEach(function (s) {
          s.classList.toggle('activa', s.dataset.tab === 'modulo');
        });
        return;
      }
      irATab(b.dataset.tab);
    });
  });

  // =============================================================================
  // Menú lateral en ACORDEÓN (pedido usuario 2026-08-03)
  //
  // El menú listaba los ~40 accesos de corrido: para llegar a Mantenimiento
  // había que scrollear todo. Ahora se ven los TÍTULOS de los grupos y sólo el
  // grupo tocado queda abierto — al abrir uno se cierra el anterior.
  //
  // Se arma acá y no en el markup a propósito: los botones no se tocan (siguen
  // con sus data-tab / data-mod y con los listeners ya enganchados, que viajan
  // con el nodo al moverlo), así que un botón nuevo en el HTML entra solo al
  // grupo que le corresponde sin que haya que acordarse de este archivo.
  // =============================================================================
  (function acordeonMenu() {
    var menu = document.getElementById('menu');
    if (!menu) return;
    var titulos = Array.prototype.slice.call(menu.querySelectorAll('.grupo'));
    if (!titulos.length) return;

    // Cada título se lleva los botones que lo siguen hasta el próximo título.
    titulos.forEach(function (t) {
      var cuerpo = document.createElement('div');
      cuerpo.className = 'grupo-cuerpo';
      var n = t.nextSibling;
      while (n && !(n.nodeType === 1 && n.classList.contains('grupo'))) {
        var sig = n.nextSibling;
        if (n.nodeType === 1) cuerpo.appendChild(n);
        n = sig;
      }
      t.parentNode.insertBefore(cuerpo, t.nextSibling);
      t.setAttribute('role', 'button');
      t.setAttribute('tabindex', '0');
      t.setAttribute('aria-expanded', 'false');
      t.addEventListener('click', function () { alternar(t); });
    });

    function abrir(t) {
      titulos.forEach(function (o) {
        var abierto = (o === t);
        o.classList.toggle('abierto', abierto);
        o.setAttribute('aria-expanded', abierto ? 'true' : 'false');
        var c = o.nextElementSibling;
        if (c && c.classList.contains('grupo-cuerpo')) c.classList.toggle('abierto', abierto);
      });
    }

    function alternar(t) {
      if (t.classList.contains('abierto')) {
        // Volver a tocar el título abierto lo cierra: así se pueden ver todos
        // los grupos de un vistazo sin tener que elegir uno.
        t.classList.remove('abierto');
        t.setAttribute('aria-expanded', 'false');
        var c = t.nextElementSibling;
        if (c && c.classList.contains('grupo-cuerpo')) c.classList.remove('abierto');
        return;
      }
      abrir(t);
    }

    // Deja abierto el grupo del botón activo. Se llama al arrancar y cada vez
    // que se cambia de pantalla, para que el menú no muestre abierto un grupo
    // que no es donde está parado el operario.
    function abrirGrupoActivo() {
      var sel = menu.querySelector('button.sel');
      if (!sel) return;
      var cuerpo = sel.closest('.grupo-cuerpo');
      if (!cuerpo) return;   // "Resumen" vive suelto arriba, sin grupo
      var t = cuerpo.previousElementSibling;
      if (t && t.classList.contains('grupo')) abrir(t);
    }
    window.AgpMenuAcordeon = { abrirGrupoActivo: abrirGrupoActivo };
    abrirGrupoActivo();
  })();

  // ---- deep-link ?mod= --------------------------------------------------------
  // Entrar a Configuración parado en un módulo: config.html?mod=camaras.html
  // (lo usa el "Configurar" del panel nativo de Cámaras — antes abría
  // camaras.html suelta en otra ventana, fuera del flujo de config). Se
  // clickea el botón REAL del menú para reusar todo el camino existente
  // (iframe embebido, .sel, ocultar Guardar) y se abre su grupo del acordeón.
  (function deepLinkMod() {
    var mod;
    try { mod = new URLSearchParams(location.search).get('mod'); } catch (e) { return; }
    if (!mod) return;
    var btn = document.querySelector('#menu button[data-mod="' + mod + '"]');
    if (!btn) return;
    btn.click();
    if (window.AgpMenuAcordeon) window.AgpMenuAcordeon.abrirGrupoActivo();
  })();

  // ---- botón Guardar flotante con estados -------------------------------------
  // neutro = sin cambios · .dirty = hay cambios sin guardar (pulso + aviso) ·
  // .ok = recién guardado (tilde OK64, 1.5s). La detección de cambios es por
  // delegación (input/change + click en toggles), y guardar() la limpia al
  // persistir — cubre también los guardados on-leave/inmediatos.
  var btnG = document.getElementById('btnGuardarFloat');
  var btnGImg = document.getElementById('btnGuardarImg');
  var btnGCap = document.getElementById('btnGuardarCap');
  var btnGTimer = null;

  // Declaración (no expresión) para que se pueda llamar desde irATab y desde el
  // listener del menú, que están escritos más arriba en el archivo.
  function mostrarGuardar(visible) {
    try { btnG.style.display = visible ? '' : 'none'; } catch (e) { /* aún no montado */ }
  }

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

  // detección de cambios: nuds/ranges/selects + toggles y botones ± de imagen.
  // Los botones de ACCIÓN inmediata (roll zero, calibrar, etc.) no marcan.
  var main = document.getElementById('main');
  main.addEventListener('input', marcarSucio);
  main.addEventListener('change', marcarSucio);
  main.addEventListener('click', function (ev) {
    // Trenes: agregar/quitar y pintar la tira modifican datos → botón dirty.
    // (Elegir el pincel no: no cambia nada hasta que se pinta.)
    if (ev.target.closest('.chkimg, .radioimg, #btnOpacUp, #btnOpacDn, #btnSmoothUp, #btnSmoothDn, #btnExtUp, #btnExtDn, #trnAdd, [data-trn-del], #trnStrip .celda')) {
      marcarSucio();
    }
  });

  // En relay/amachine el guardado real es su "Enviar + Guardar" (manda PGN):
  // el flotante lo dispara para no descartar cambios como haría el leave.
  btnG.addEventListener('click', async function () {
    if (!tabActual || !tabs[tabActual]) return;
    if (tabActual === 'relay') { document.getElementById('btnRelaySend').click(); return; }
    if (tabActual === 'amachine') { document.getElementById('btnMachineSend').click(); return; }
    var ok = await tabs[tabActual].leave();
    if (ok) {
      tabs[tabActual].enter();          // resincroniza con lo persistido
      limpiarSucio(true);
      if (tabActual === 'summary') setEstado('Guardado ✔', 'ok');
    }
  });

  // guardar la tab activa si la página se oculta (cierre del widget del Hub)
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden' && tabActual && tabs[tabActual]) {
      tabs[tabActual].leave();
    }
  });

  // ---- footer -----------------------------------------------------------------
  function pintarFooter() {
    document.querySelector('#ftPerfil b').textContent = snap.perfil_activo || '—';
    document.querySelector('#ftAncho b').textContent = fmtMedium(snap.secciones.tool_width);
    document.querySelector('#ftUnidades b').textContent = snap.is_metric ? 'Métrico' : 'Imperial';
    document.querySelectorAll('[data-unidad]').forEach(function (el) { el.textContent = unidad(); });
  }

  // ---- arranque -----------------------------------------------------------------
  (async function init() {
    try {
      var r = await api('');
      if (!r.ok) { setEstado('Servicio de configuración no disponible', 'err'); return; }
      snap = r;
      pintarFooter();
      var tab = new URLSearchParams(window.location.search).get('tab') || 'summary';
      irATab(tab);
    } catch (e) {
      setEstado('Sin conexión: ' + e.message, 'err');
    }
  })();
})();
