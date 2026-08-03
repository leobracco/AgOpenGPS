// ============================================================================
// quantix.js — UI completa del módulo QuantiX.
// Tabs:
//   Monitor    → WS /ws/quantix (push) con fallback a /api/quantix/live (poll)
//   Motores    → /api/quantix/motores GET/PUT + POST /{uid}/send
//   PID live   → POST /{uid}/cmd?verb=config con {configs:[...]} para tune en vivo
//   Calibrar   → POST /{uid}/cmd?verb=calibrar para start/stop, lee pulsos de live
//   Prueba     → POST /{uid}/cmd?verb=test para diagnóstico de motores
// ============================================================================

(function () {
  'use strict';

  // ---------- Shared helpers ----------

  function $(id) { return document.getElementById(id); }
  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function ageMs(iso) {
    if (!iso) return Infinity;
    var t = Date.parse(iso);
    if (isNaN(t)) return Infinity;
    return Date.now() - t;
  }

  // Formato corto para min..max en el dropdown de shape: 3 dígitos significativos,
  // sin colas de ceros (50000 → "50000", 0.005 → "0.005", 12.345678 → "12.3").
  function fmtNum(v) {
    if (v == null || !isFinite(v)) return '?';
    var a = Math.abs(v);
    if (a >= 1000) return v.toFixed(0);
    if (a >= 10)   return v.toFixed(1);
    if (a >= 1)    return v.toFixed(2);
    return v.toPrecision(2);
  }

  // ---------- State ----------

  var state = {
    activeTab: 'siembra',
    motoresCfg: { nodos: [], ignorados: [] },
    liveByUid: {},
    // NumSections viene del piloto (snapshot del tool actual). Se usa para
    // pintar el grid de selección de secciones en Motores. 0 = sin info aún.
    aogNumSections: 0,
    // Campos DBF del shapefile activo en el piloto. shapeSource cambia cuando se
    // abre otro .shp; usamos eso para refrescar sin pegarle a /aog/shape-fields
    // en cada render. shapeFields = [{name, numeric, min, max, count}].
    shapeSource: null,
    shapeFields: [],
    // Implemento central (Herramienta) — fuente única de verdad para trenes,
    // surcos y mapping surco→sección. QuantiX lee de acá; NO edita.
    implCentral: null
  };

  // --- Estado de la tira de surcos (Tarea 3-8) ---
  state.brushMotor = 0;          // índice del motor activo (pincel)
  state.siembraView = 'planter'; // 'planter' | 'tabla'

  var MOTOR_COLORS = ['#4ABA3E', '#7F6BE0', '#E0A33E', '#3E9BE0', '#E06B8B', '#46C5B0'];
  function motorColor(idx) { return MOTOR_COLORS[idx % MOTOR_COLORS.length]; }

  // Lista PLANA de todos los motores de todos los nodos habilitados.
  // Cada entrada: { nodo, nodeIdx, motorIdx, motor, uid }. El índice en este
  // arreglo es el "índice plano" que usa el pincel (state.brushMotor) y el color.
  // Así un solo planter muestra los N motores de los 2 (o más) nodos juntos.
  function allMotors() {
    var out = [];
    var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
    for (var n = 0; n < ns.length; n++) {
      var nodo = ns[n];
      if (!nodo || nodo.habilitado === false) continue;
      var ms = nodo.motores || [];
      for (var i = 0; i < ms.length; i++) {
        out.push({ nodo: nodo, nodeIdx: n, motorIdx: i, motor: ms[i], uid: nodo.uid });
      }
    }
    return out;
  }

  // Busca el motor de config (no live) por uid + índice de motor.
  function findMotor(uid, mi) {
    var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
    for (var i = 0; i < ns.length; i++) {
      if (ns[i] && ns[i].uid === uid && ns[i].motores && ns[i].motores[mi])
        return ns[i].motores[mi];
    }
    return null;
  }

  // rpm del eje del dosificador = pps / pulsos-por-vuelta * 60. El operario no ve
  // pps; ve rpm (velocidad del motor) y la dosis agronómica aparte.
  function ppsToRpm(motor, pps) {
    var ppr = (motor && motor.dientes_engranaje > 0) ? motor.dientes_engranaje : 24;
    return ppr > 0 ? (pps / ppr * 60) : 0;
  }

  // ¿Hay más de un nodo habilitado? (para etiquetar N1/N2 en la lista).
  function nodoCount() {
    var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
    var c = 0;
    for (var i = 0; i < ns.length; i++) {
      if (ns[i] && ns[i].habilitado !== false) c++;
    }
    return c;
  }

  // Etiqueta de nodo para un motor plano (solo si hay varios nodos). Un nodo
  // (ESP32) maneja 2 motores; la tolva es otra cosa. Por eso etiquetamos "N" (nodo).
  function nodoTag(entry) {
    if (nodoCount() <= 1) return '';
    return ' · N' + (entry.nodeIdx + 1);
  }

  // Total de surcos = máx(secciones de PilotX, surcos cubiertos por motores, 1).
  // Usa state.aogNumSections (entero) que carga loadAogSections().
  function totalSurcos() {
    var max = state.aogNumSections | 0;
    allMotors().forEach(function (e) {
      (e.motor.cortes || []).forEach(function (c) { if ((c | 0) > max) max = c | 0; });
    });
    return Math.max(max, 1);
  }

  // Índice surco (1-based) → índice plano del motor dueño (o -1 si huérfano).
  function surcoOwner(surco) {
    var all = allMotors();
    for (var i = 0; i < all.length; i++) {
      if ((all[i].motor.cortes || []).indexOf(surco) >= 0) return i;
    }
    return -1;
  }

  // Renderiza la tira de celdas coloreadas por motor en #qxStrip.
  function renderStrip() {
    var el = document.getElementById('qxStrip');
    if (!el) return;
    var hayMotores = allMotors().length > 0;
    el.classList.toggle('cell-colors', hayMotores);
    if (!hayMotores) {
      el.innerHTML = '<span class="msg">No hay nodos QuantiX configurados</span>';
      return;
    }
    var total = totalSurcos();
    var html = '';
    for (var s = 1; s <= total; s++) {
      if (state.siembraEnMarcha && state.sectionOn && state.sectionOn[s - 1] === false) {
        html += '<div class="cell off" data-surco="' + s + '">' + s + '</div>';
        continue;
      }
      var owner = surcoOwner(s);
      if (owner < 0) {
        html += '<div class="cell orphan" data-surco="' + s + '">' + s + '</div>';
      } else {
        var brush = owner === state.brushMotor ? ' brush' : '';
        html += '<div class="cell' + brush + '" data-surco="' + s + '" '
              + 'style="background:' + motorColor(owner) + '">' + s + '</div>';
      }
    }
    el.innerHTML = html;
  }

  // Asigna 'surco' al motor del pincel, quitándolo de cualquier otro motor de
  // CUALQUIER nodo (1 surco = 1 motor en todo el conjunto).
  function paintSurco(surco) {
    var all = allMotors();
    if (state.brushMotor >= all.length) return;
    all.forEach(function (e, i) {
      var m = e.motor;
      m.cortes = (m.cortes || []).filter(function (c) { return c !== surco; });
      if (i === state.brushMotor && m.cortes.indexOf(surco) < 0) {
        m.cortes.push(surco); m.cortes.sort(function (a, b) { return a - b; });
      }
    });
    state.dirty = true;
    state.lastTouchedSurco = surco;
  }

  function fmtCortes(cortes) {
    if (!cortes || !cortes.length) return 'sin surcos';
    var sorted = cortes.slice().sort(function (a, b) { return a - b; });
    return 'surcos ' + sorted.join(',');
  }

  // Columnas numéricas del shapefile activo, candidatas a ser mapa de dosis.
  function shapeDoseFields() {
    var out = [];
    (state.shapeFields || []).forEach(function (f) {
      if (typeof f === 'string') { out.push(f); return; }
      if (f && f.name && f.numeric !== false) out.push(f.name);
    });
    return out;
  }

  // Trenes físicos disponibles (del implemento central). [{id,nombre,distancia_m}].
  function trenesDisponibles() {
    var ts = (state.implCentral && state.implCentral.trenes) || [];
    return Array.isArray(ts) ? ts : [];
  }

  // Mapa sección PilotX (motor.cortes) → surcos físicos que la componen, según
  // el implemento central. Espejo JS de SurcosPorSeccion.cs (backend).
  function surcosPorSeccionMapa() {
    var surcos = (state.implCentral && Array.isArray(state.implCentral.surcos)) ? state.implCentral.surcos : [];
    var mapa = {};
    surcos.forEach(function (s) {
      var sec = s.seccion_pilotx | 0;
      if (sec < 1) return;
      if (!mapa[sec]) mapa[sec] = [];
      mapa[sec].push(s.numero | 0);
    });
    return mapa;
  }

  // Mapa surco físico (numero) → id de tren, según el implemento central.
  function trenPorSurcoMapa() {
    var surcos = (state.implCentral && Array.isArray(state.implCentral.surcos)) ? state.implCentral.surcos : [];
    var mapa = {};
    surcos.forEach(function (s) { mapa[s.numero | 0] = s.tren_id | 0; });
    return mapa;
  }

  // Deriva a qué tren pertenece un motor a partir de los surcos que alimenta
  // (motor.cortes → surcos → tren). Espejo JS de TrenResolver.cs (backend):
  // mismo criterio de "sin trenes reales" (ningún tren con distancia_m > 0.05)
  // y mismo desempate en conflicto (gana el tren del primer surco).
  // Devuelve null cuando no hay dato derivable — el caller usa el fallback
  // manual del nodo (fase 1, spec 2026-08-01-implemento-unificado-design.md).
  function derivarTrenMotor(cortes) {
    var ts = trenesDisponibles();
    if (ts.length < 2) return null;
    var hayDistanciaReal = ts.some(function (t) { return (t.distancia_m || 0) > 0.05; });
    if (!hayDistanciaReal) return null;

    var porSeccion = surcosPorSeccionMapa();
    var surcos = [];
    (cortes || []).forEach(function (sec) {
      (porSeccion[sec] || []).forEach(function (n) { surcos.push(n); });
    });
    if (!surcos.length) return null;

    var porSurco = trenPorSurcoMapa();
    var trenId = null, conflicto = false;
    surcos.forEach(function (n) {
      if (!(n in porSurco)) return;
      var tid = porSurco[n];
      if (trenId === null) trenId = tid;
      else if (tid !== trenId) conflicto = true;
    });
    if (trenId === null) return null;
    var t = ts.filter(function (x) { return (x.id | 0) === trenId; })[0];
    return { id: trenId, nombre: t ? (t.nombre || ('Tren ' + trenId)) : ('Tren ' + trenId), conflicto: conflicto };
  }

  // Presentación SOLO LECTURA del tren de un motor (reemplaza el <select>
  // editable de antes): el tren ya no se elige acá, se deriva del implemento
  // central y se configura en Implemento. Fallback al valor manual del nodo
  // (m.tren) cuando el implemento no tiene trenes reales cargados (fase 1).
  function trenReadOnlyHtml(tren, cortes) {
    var d = derivarTrenMotor(cortes);
    if (!d) {
      return '<span class="pill" title="El implemento no tiene trenes configurados: se usa el valor manual del nodo">'
        + 'Tren: <b>manual (nodo)</b></span>';
    }
    if (d.conflicto) {
      return '<span class="pill warn" title="Los surcos de este motor pertenecen a trenes distintos del implemento — se usa el del primero">'
        + '⚠ surcos de trenes distintos</span>';
    }
    return '<span class="pill" title="Tren derivado del implemento — se configura en Implemento">'
      + 'Tren: <b>' + escapeHtml(d.nombre) + '</b></span>';
  }

  // <select> mapa/fija para un motor: "Dosis fija" + cada columna del shape.
  // Si el motor ya apunta a una columna que el shape actual no tiene (lote sin
  // prescripción cargada), la conservamos VISIBLE pero deshabilitada: así no se
  // pierde la config y queda claro que el mapa no es seleccionable hasta cargar
  // el lote con shapefile. Sin esto, el motor configurado mostraba 2 opciones y
  // el resto solo "Dosis fija" (inconsistencia que confundía al operario).
  function mapaSelectHtml(i, campoDosis) {
    var fields = shapeDoseFields();
    var cur = campoDosis || '';
    var curMissing = cur && fields.indexOf(cur) < 0;
    var opts = '<option value=""' + (cur ? '' : ' selected') + '>Dosis fija</option>';
    fields.forEach(function (nm) {
      var sel = (nm === cur) ? ' selected' : '';
      opts += '<option value="' + escapeHtml(nm) + '"' + sel + '>Mapa: ' + escapeHtml(nm) + '</option>';
    });
    if (curMissing) {
      opts += '<option value="' + escapeHtml(cur) + '" selected disabled>Mapa: '
        + escapeHtml(cur) + ' (sin shape)</option>';
    }
    return '<select class="qxMapa" data-mi="' + i + '" title="Dosis fija o mapa (columna del shapefile)">'
      + opts + '</select>';
  }

  function renderMotorList() {
    var el = document.getElementById('qxMotorList');
    if (!el) return;
    var all = allMotors();
    if (!all.length) { el.innerHTML = ''; return; }

    // Si estamos en vivo, delega al render live (se implementa en una tarea posterior).
    if (state.siembraEnMarcha && typeof renderMotorListLive === 'function') { renderMotorListLive(); return; }

    // Nota única (no una por motor): el tren de cada fila es solo-lectura,
    // derivado del implemento central — se configura en config-implemento.html.
    var html = '<div style="font-size:11px;color:var(--agp-text-muted);margin-bottom:6px">'
      + 'Tren: derivado del implemento — <a href="config.html?tab=tsections">configurar en Configuración</a></div>';
    for (var i = 0; i < all.length; i++) {
      var m = all[i].motor;
      var sel = (i === state.brushMotor) ? ' sel' : '';
      var nombre = escapeHtml(m.nombre || ('Motor ' + (i + 1)));
      var dosis = (typeof m.dosis_fija === 'number' ? m.dosis_fija : 0).toFixed(1);
      var esSem = (m.unidad_dosis === 'sem_m');
      var unidadLbl = esSem ? 'sem/m' : 'kg/ha';
      // En sem/m hace falta la calibración: semillas por vuelta del dosificador.
      var calBox = esSem
        ? ('<span class="calbox"><input type="number" step="1" min="0" data-mi="' + i + '" '
           + 'class="qxSemVuelta" value="' + (typeof m.semillas_vuelta === 'number' ? m.semillas_vuelta : 0)
           + '"> <span class="u">sem/vuelta</span></span>')
        : '';
      var nombreRaw = (m.nombre != null ? String(m.nombre) : ('Motor ' + (i + 1)));
      // Canal sin motor cableado: se destildá y PilotX le manda consigna nula.
      var hab = (m.habilitado !== false);
      html += '<div class="mrow' + sel + (hab ? '' : ' mdis') + '" data-mi="' + i + '">'
        + '<span class="sw" style="background:' + motorColor(i) + '"></span>'
        + '<input class="qxHab" type="checkbox" data-mi="' + i + '"' + (hab ? ' checked' : '')
        + ' title="Motor conectado. Destildado no recibe dosis.">'
        + '<input class="qxNombre" type="text" data-mi="' + i + '" value="' + escapeHtml(nombreRaw)
        + '" title="Nombre del motor">'
        + '<span class="cnt">' + fmtCortes(m.cortes) + escapeHtml(nodoTag(all[i])) + '</span>'
        + trenReadOnlyHtml(m.tren, m.cortes)
        + '<span class="dosebox"><input type="number" step="0.1" data-mi="' + i + '" '
        + 'class="qxDosisFija" value="' + dosis + '"> '
        + '<button class="uToggle" type="button" data-mi="' + i + '" title="Cambiar unidad (kg/ha ↔ sem/m)">'
        + unidadLbl + '</button></span>'
        + calBox
        + mapaSelectHtml(i, m.campo_dosis)
        + '<button class="mdel" type="button" data-del="' + i + '" title="Borrar motor">\u00D7</button>'
        + '</div>';
    }
    el.innerHTML = html;

    // Tap a la fila = fijar pincel (sin robar foco al input de dosis ni al borrar).
    var rows = el.querySelectorAll('.mrow');
    for (var r = 0; r < rows.length; r++) {
      rows[r].addEventListener('click', function (e) {
        if (e.target && e.target.classList &&
            (e.target.classList.contains('qxDosisFija') || e.target.classList.contains('mdel') ||
             e.target.classList.contains('uToggle') || e.target.classList.contains('qxSemVuelta') ||
             e.target.classList.contains('qxMapa') || e.target.classList.contains('qxNombre') ||
             e.target.classList.contains('qxHab'))) return;
        state.brushMotor = parseInt(this.getAttribute('data-mi'), 10);
        updateBrushChip(); renderStrip(); renderMotorList();
      });
    }
    // Botón × = borrar ese motor (sus surcos quedan huérfanos).
    var dels = el.querySelectorAll('.mdel');
    for (var d = 0; d < dels.length; d++) {
      dels[d].addEventListener('click', function (e) {
        e.stopPropagation();
        deleteMotor(parseInt(this.getAttribute('data-del'), 10));
      });
    }
    // Editar dosis fija (escribe sobre el motor plano correspondiente).
    var inputs = el.querySelectorAll('.qxDosisFija');
    for (var k = 0; k < inputs.length; k++) {
      inputs[k].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var entry = allMotors()[idx];
        if (entry && entry.motor) {
          entry.motor.dosis_fija = parseFloat(this.value) || 0;
          state.dirty = true;
          renderMotorList();
        }
      });
    }
    // Cambiar unidad de dosis del motor (kg/ha <-> sem/m).
    var togs = el.querySelectorAll('.uToggle');
    for (var t = 0; t < togs.length; t++) {
      togs[t].addEventListener('click', function (e) {
        e.stopPropagation();
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var entry = allMotors()[idx];
        if (entry && entry.motor) {
          entry.motor.unidad_dosis = (entry.motor.unidad_dosis === 'sem_m') ? 'kg_ha' : 'sem_m';
          state.dirty = true;
          renderMotorList();
        }
      });
    }
    // Renombrar motor (no re-renderiza en vivo para no perder el foco mientras
    // se tipea; el pincel/brushchip se actualiza al confirmar).
    var noms = el.querySelectorAll('.qxNombre');
    for (var nm = 0; nm < noms.length; nm++) {
      noms[nm].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var entry = allMotors()[idx];
        if (entry && entry.motor) {
          entry.motor.nombre = (this.value || '').trim() || ('Motor ' + (idx + 1));
          state.dirty = true;
          updateBrushChip();
        }
      });
    }
    // Habilitar/deshabilitar el canal. Re-renderiza para atenuar la fila.
    var habs = el.querySelectorAll('.qxHab');
    for (var hb = 0; hb < habs.length; hb++) {
      habs[hb].addEventListener('click', function (e) { e.stopPropagation(); });
      habs[hb].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var entry = allMotors()[idx];
        if (entry && entry.motor) {
          entry.motor.habilitado = this.checked;
          state.dirty = true;
          renderMotorList();
        }
      });
    }
    // Tren físico del motor: ya NO se edita acá (era <select class="qxTren">).
    // Se muestra solo-lectura, derivado del implemento central — ver
    // trenReadOnlyHtml()/derivarTrenMotor() más arriba.
    // Mapa/fija: vacío = dosis fija; nombre de columna = mapa del shapefile.
    var mapas = el.querySelectorAll('.qxMapa');
    for (var mp = 0; mp < mapas.length; mp++) {
      ['pointerdown', 'mousedown', 'touchstart', 'click'].forEach(function (evName) {
        mapas[mp].addEventListener(evName, function (e) { e.stopPropagation(); });
      });
      mapas[mp].addEventListener('change', function (e) {
        e.stopPropagation();
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var entry = allMotors()[idx];
        if (entry && entry.motor) {
          entry.motor.campo_dosis = this.value || '';
          state.dirty = true;
          updateBrushChip();
        }
      });
    }
    // Calibración sem/m: semillas que entrega el dosificador por vuelta.
    var sems = el.querySelectorAll('.qxSemVuelta');
    for (var s = 0; s < sems.length; s++) {
      sems[s].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-mi'), 10);
        var entry = allMotors()[idx];
        if (entry && entry.motor) {
          entry.motor.semillas_vuelta = parseFloat(this.value) || 0;
          state.dirty = true;
          renderMotorList();
        }
      });
    }
  }

  function updateBrushChip() {
    var chip = document.getElementById('qxBrush');
    if (!chip) return;
    var entry = allMotors()[state.brushMotor];
    var sw = chip.querySelector('.sw');
    if (sw) sw.style.background = motorColor(state.brushMotor);
    var m = entry ? entry.motor : null;
    var label = m ? ((m.nombre || ('Motor ' + (state.brushMotor + 1))) + nodoTag(entry)) : '\u2014';
    if (chip.lastChild) chip.lastChild.textContent = 'Pincel: ' + label;
  }

  function renderTabla() {
    var tbl = document.getElementById('qxTabla');
    if (!tbl) return;
    var all = allMotors();
    if (state.siembraView !== 'tabla' || !all.length) { tbl.style.display = 'none'; return; }
    var ctx = qxAgro.ctxFrom(state.implCentral, state.aogSpeed);
    var rows = '<tr><th>Motor</th><th>Surcos</th><th>Dosis fija</th>'
             + '<th>Efectiva</th><th>Real</th><th>RPM</th><th>Estado</th></tr>';
    for (var i = 0; i < all.length; i++) {
      var m = all[i].motor;
      var live = liveMotor(all[i].uid, all[i].motorIdx);
      var realPps = live ? (live.pps_real || 0) : 0;
      var real = live ? qxAgro.label(qxAgro.units(m, realPps, ctx)) : '\u2014';
      var rpm = live ? (live.rpm | 0) : '\u2014';
      var unidad = (m.unidad_dosis === 'sem_m') ? 'sem/m' : 'kg/ha';
      var fija = (typeof m.dosis_fija === 'number' ? m.dosis_fija : 0).toFixed(1) + ' ' + unidad;
      var ef = m.campo_dosis ? ('mapa ' + escapeHtml(m.campo_dosis)) : (fija + ' fija');
      var estado = (state.siembraEnMarcha && motorAllCut(m)) ? '\u25CB corte' : '\u25CF dosif.';
      var surcos = (m.cortes || []).join(',') || '\u2014';
      var nombre = escapeHtml((m.nombre || ('M' + (i + 1))) + nodoTag(all[i]));
      rows += '<tr>'
        + '<td><span class="sw" style="display:inline-block;width:10px;height:10px;'
        + 'border-radius:2px;background:' + motorColor(i) + '"></span> ' + nombre + '</td>'
        + '<td>' + surcos + '</td>'
        + '<td>' + fija + '</td>'
        + '<td>' + ef + '</td>'
        + '<td>' + real + '</td>'
        + '<td>' + rpm + '</td>'
        + '<td>' + estado + '</td>'
        + '</tr>';
    }
    tbl.innerHTML = rows;
    tbl.style.display = 'table';
  }

  function setSiembraView(v) {
    state.siembraView = v;
    var sp = document.getElementById('segPlanter');
    var st = document.getElementById('segTabla');
    if (sp) sp.classList.toggle('on', v === 'planter');
    if (st) st.classList.toggle('on', v === 'tabla');
    var wrap = document.querySelector('.planter-wrap');
    if (wrap) wrap.style.display = (v === 'planter') ? 'block' : 'none';
    var list = document.getElementById('qxMotorList');
    if (list) list.style.display = (v === 'planter') ? 'flex' : 'none';
    renderSiembra();
  }
  // Devuelve los surcos (1-based) sin motor asignado en NINGÚN nodo.
  function surcosHuerfanos() {
    if (!allMotors().length) return [];
    var total = totalSurcos();
    var huerfanos = [];
    for (var s = 1; s <= total; s++) {
      if (surcoOwner(s) < 0) huerfanos.push(s);
    }
    return huerfanos;
  }

  // Muestra/oculta el aviso de surcos sin motor en #qxOrphanWarn.
  function updateOrphanWarn() {
    var el = document.getElementById('qxOrphanWarn');
    if (!el) return;
    var h = surcosHuerfanos();
    if (h.length === 0) { el.style.display = 'none'; el.textContent = ''; return; }
    el.style.display = '';
    el.textContent = h.length + ' surco' + (h.length !== 1 ? 's' : '') +
      ' sin motor: ' + h.join(', ');
  }

  function renderSiembra() {
    renderStrip();
    renderMotorList();
    updateBrushChip();
    renderTabla();
    updateOrphanWarn();
  }

  // Busca el motor live (por id) del nodo en el registry de telemetría.
  function liveMotor(uid, i) {
    var by = state.liveByUid && state.liveByUid[uid];
    var ms = by && by.motors ? by.motors : [];
    for (var k = 0; k < ms.length; k++) {
      if ((ms[k].id | 0) === i) return ms[k];
    }
    return null;
  }

  // Todos los surcos del motor están cortados (sección OFF) → motor frenado.
  function motorAllCut(motor) {
    var cortes = (motor && motor.cortes) || [];
    if (!cortes.length || !state.sectionOn) return false;
    for (var c = 0; c < cortes.length; c++) {
      if (state.sectionOn[cortes[c] - 1] !== false) return false;
    }
    return true;
  }

  // En marcha = job abierto en PilotX y hay telemetría live de algún nodo.
  function computeEnMarcha() {
    state.siembraEnMarcha = !!(state.aogJobStarted &&
        state.liveByUid && Object.keys(state.liveByUid).length > 0);
  }

  function renderMotorListLive() {
    var el = document.getElementById('qxMotorList');
    if (!el) return;
    var all = allMotors();
    if (!all.length) return;
    var ctx = qxAgro.ctxFrom(state.implCentral, state.aogSpeed);
    var html = '';
    for (var i = 0; i < all.length; i++) {
      var m = all[i].motor;
      var live = liveMotor(all[i].uid, all[i].motorIdx);
      var real = live ? (live.pps_real || 0) : 0;
      var target = live ? (live.pps_target || 0) : 0;
      var rpm = live ? (live.rpm | 0) : 0;
      var cutAll = motorAllCut(m);
      var badge = '<span class="badge">OK</span>';
      var barClass = 'bar';
      var pct = target > 0 ? Math.min(100, real / target * 100) : 0;
      if (cutAll) {
        badge = '<span class="badge cut">corte</span>'; pct = 0;
      } else if (target > 0 && Math.abs(real - target) / target > 0.15) {
        badge = '<span class="badge dev">desvío</span>'; barClass = 'bar warn';
      }
      // Unidades agronómicas (el operario no ve pps): sem/m·sem/ha o kg/ha.
      var uObj = qxAgro.primary(qxAgro.units(m, target, ctx));
      var objVal = cutAll ? '\u2014' : uObj.v;
      var realLine = cutAll ? ('\u2014 ' + uObj.u) : qxAgro.label(qxAgro.units(m, real, ctx));
      var nombre = escapeHtml((m.nombre || ('Motor ' + (i + 1))) + nodoTag(all[i]));
      html += '<div class="mrow" data-mi="' + i + '">'
        + '<span class="sw" style="background:' + motorColor(i) + '"></span>'
        + '<span class="nm">' + nombre + '</span>'
        + '<span class="dosebox"><span class="u">obj ' + uObj.u + '</span> '
        + '<b style="color:var(--agp-accent)">' + objVal + '</b></span>'
        + '<span class="pps">' + realLine + '</span>'
        + '<span class="rpm">' + rpm + ' rpm</span>'
        + '<span class="' + barClass + '"><i style="width:' + pct.toFixed(0) + '%"></i></span>'
        + badge
        + '</div>';
    }
    el.innerHTML = html;
  }

  // Agrega un motor al nodo del pincel activo (o al último nodo habilitado).
  function addMotor() {
    // Tope de 24 motores totales (spec: hasta 24, escalando a 36/48 más adelante).
    if (allMotors().length >= 24) {
      var mm = $('mtMsg');
      if (mm) { mm.textContent = 'Máximo 24 motores'; mm.className = 'msg err'; }
      return;
    }
    var all = allMotors();
    var entry = all[state.brushMotor] || all[all.length - 1];
    var nodo = entry ? entry.nodo : null;
    if (!nodo) {
      // Sin pincel: usar el último nodo habilitado.
      var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
      for (var n = ns.length - 1; n >= 0; n--) {
        if (ns[n] && ns[n].habilitado !== false) { nodo = ns[n]; break; }
      }
    }
    if (!nodo) return;
    nodo.motores = nodo.motores || [];
    var m = defaultMotor('Motor ' + (nodo.motores.length + 1));
    m.cortes = [];
    nodo.motores.push(m);
    // El nuevo motor es el último de la lista plana de su nodo: recalcular índice.
    var after = allMotors();
    for (var i = 0; i < after.length; i++) {
      if (after[i].nodo === nodo && after[i].motor === m) { state.brushMotor = i; break; }
    }
    state.dirty = true;
    renderStrip(); renderMotorList();
  }

  // Borra un motor de su nodo (lista plana). Sus surcos quedan huérfanos (gris)
  // y se pueden reasignar pintándolos. El pincel se reajusta para no apuntar a
  // un índice inexistente. Persiste recién al tocar Guardar.
  async function deleteMotor(flatIdx) {
    var all = allMotors();
    var entry = all[flatIdx];
    if (!entry || !entry.nodo) return;
    var nombre = (entry.motor && entry.motor.nombre) || ('Motor ' + (flatIdx + 1));
    if (!await AgpModal.confirm('Borrar motor', '\u00BFBorrar ' + nombre + '? Sus surcos quedan sin motor.')) return;
    var ms = entry.nodo.motores || [];
    ms.splice(entry.motorIdx, 1);
    var n = allMotors().length;
    if (state.brushMotor >= n) state.brushMotor = n > 0 ? n - 1 : 0;
    state.dirty = true;
    renderStrip(); renderMotorList(); updateBrushChip(); updateOrphanWarn();
  }

  // Quita el último surco tocado de cualquier motor de cualquier nodo (queda huérfano).
  function quitarSurco() {
    var s = state.lastTouchedSurco;
    if (!s) return;
    allMotors().forEach(function (e) {
      e.motor.cortes = (e.motor.cortes || []).filter(function (c) { return c !== s; });
    });
    state.dirty = true;
    renderStrip(); renderMotorList();
  }

  // Auto-repartir sobre TODOS los motores de TODOS los nodos (lista plana):
  // 'uno' = 1 surco por motor en orden; 'grupos' = surcos en N grupos parejos.
  function autoReparto(modo) {
    var all = allMotors();
    if (!all.length) return;
    var total = totalSurcos();
    all.forEach(function (e) { e.motor.cortes = []; });
    if (modo === 'uno') {
      for (var i = 0; i < all.length && i < total; i++) all[i].motor.cortes = [i + 1];
    } else {
      var n = all.length;
      for (var s = 1; s <= total; s++) {
        var g = Math.floor((s - 1) * n / total);
        all[g].motor.cortes.push(s);
      }
    }
    state.dirty = true;
    renderStrip(); renderMotorList();
  }

  // El ancho de labor lo administra PilotX (/api/tool): el implemento Agro
  // Parallel guarda una COPIA que nadie sincroniza. Esa copia alimentaba el
  // cálculo de dosis en pantalla, así que un implemento cargado bien en PilotX
  // (7,28 m = 14 surcos a 0,52) convivía con 4 m y 0,191 m acá y sem/ha salía
  // 2,7 veces más alto. Tomamos el ancho de PilotX como fuente de verdad y, si
  // hay cantidad de surcos, derivamos de ahí el espaciamiento real.
  async function loadImplCentral() {
    try {
      var r = await fetch('/api/implemento', { cache: 'no-store' });
      var d = await r.json();
      state.implCentral = (d && d.ok) ? d.implemento : null;
    } catch (e) { state.implCentral = null; }

    if (!state.implCentral) return;

    // La geometría sale de la CONFIGURACIÓN DE SECCIONES de PilotX y de ningún
    // otro lado: ancho de labor, cantidad de surcos y distancia entre hileras.
    // Una sección = un surco, que es como ya venía trabajando el planter de
    // esta misma pantalla (la tira de surcos usa aogNumSections). Tener esos
    // valores duplicados en el implemento Agro Parallel fue lo que hizo que
    // sem/ha saliera 2,7 veces más alto con el implemento desfasado.
    try {
      var rt = await fetch('/api/tool', { cache: 'no-store' });
      var dt = await rt.json();
      var tool = (dt && dt.tool) || {};
      var w = Number(tool.width) || 0;
      var nSec = tool.numSections | 0;
      state.anchoPilotX = w;
      if (w > 0) {
        state.implCentral.ancho_total_m = w;
        if (nSec > 0) {
          state.implCentral.numero_surcos = nSec;
          state.implCentral.distancia_entre_surcos_m = w / nSec;
        }
      }
    } catch (e) { state.anchoPilotX = 0; }
  }

  // Sin secciones configuradas no hay geometría de la que calcular dosis por
  // hectárea. Se avisa; no se inventa un ancho por defecto.
  function avisoAnchoDistinto() {
    if (!state.implCentral) return '';
    if (state.anchoPilotX > 0) return '';
    return 'PilotX no tiene ancho de labor configurado. Cargalo en Implemento ' +
      'PilotX (ancho y cantidad de secciones): de ahí salen los surcos, la ' +
      'distancia entre hileras y la dosis por hectárea.';
  }

  async function loadAogSections() {
    try {
      var r = await fetch('/api/aog/state', { cache: 'no-store' });
      var d = await r.json();
      // /api/aog/state serializa en snake_case (AgpJson).
      var n = (d && d.num_sections) || 0;
      state.aogNumSections = n;
    } catch (e) { /* keep previous */ }
  }

  // Trae las columnas DBF del shapefile activo. Cachea por SourceToken para
  // que abrir y cerrar la tab no genere requests innecesarios.
  async function loadShapeFields() {
    try {
      var r = await fetch('/api/aog/shape-fields', { cache: 'no-store' });
      var d = await r.json();
      // /api/aog/shape-fields serializa en snake_case: sourceToken → source_token
      if (d && d.ok) {
        state.shapeSource = d.source_token || '';
        state.shapeFields = Array.isArray(d.fields) ? d.fields : [];
      } else {
        state.shapeSource = '';
        state.shapeFields = [];
      }
    } catch (e) {
      state.shapeSource = '';
      state.shapeFields = [];
    }
  }

  // ---------- Tabs ----------

  function showTab(name) {
    state.activeTab = name;
    document.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.getAttribute('data-tab') === name);
    });
    ['Siembra', 'Motores', 'Shape', 'Pid', 'Calibrar', 'Prueba'].forEach(function (k) {
      var el = $('tab' + k);
      if (el) el.style.display = (k.toLowerCase() === name) ? '' : 'none';
    });
    if (name === 'siembra')  {
      computeEnMarcha(); applyMarchaChrome(); renderSiembra();
      // Refresca columnas del shapefile activo para poblar el selector mapa/fija
      // por motor, y re-renderiza cuando llegan.
      if (!state.siembraEnMarcha) loadShapeFields().then(function () { renderSiembra(); });
    }
    if (name === 'motores')  renderMotores();
    if (name === 'shape')    refreshShapeActive();
    if (name === 'pid')      renderPid();
    if (name === 'calibrar') renderCalibrar();
    if (name === 'prueba')   renderPrueba();
  }

  document.querySelectorAll('.tab').forEach(function (t) {
    t.addEventListener('click', function () { showTab(t.getAttribute('data-tab')); });
  });

  // ============================================================================
  // MONITOR
  // ============================================================================

  var FRESH_MS = 3000;
  var statusEl = $('qxStatus');

  function deltaClass(target, real) {
    if (!target || target <= 0) return '';
    var pct = Math.abs((real - target) / target) * 100;
    if (pct <= 2) return 'delta-ok';
    if (pct <= 8) return 'delta-warn';
    return 'delta-err';
  }
  function deltaPct(target, real) {
    if (!target || target <= 0) return 0;
    return ((real - target) / target) * 100;
  }
  function pwmPct(pwm) {
    if (!pwm) return 0;
    return Math.max(0, Math.min(100, Math.round((pwm / 4095) * 100)));
  }

  // Refresca el estado de PilotX (job, secciones cortadas, velocidad, área).
  async function refreshAogLiveState() {
    try {
      // /api/aog/state serializa en snake_case (AgpJson).
      var r = await fetch('/api/aog/state', { cache: 'no-store' });
      var d = await r.json();
      state.aogJobStarted = !!d.is_job_started;
      state.sectionOn = d.section_on_request || null;
      if (d.num_sections != null) state.aogNumSections = d.num_sections;
      state.aogSpeed = d.avg_speed || 0;
      var areaM2 = d.actual_area_covered_m2 || 0;
      state.aogAreaHa = areaM2 * 0.0001;
    } catch (e) { /* mantené el último estado */ }
  }

  // Ajusta el "chrome" de Siembra según modo (label, toolbar, caption).
  function applyMarchaChrome() {
    var label = document.getElementById('qxModeLabel');
    var tools = document.getElementById('qxTools');
    var capL = document.getElementById('planterCapL');
    var capR = document.getElementById('planterCapR');
    var live = state.siembraEnMarcha;
    if (label) label.textContent = live ? 'En marcha' : 'Configurar';
    if (tools) tools.style.display = live ? 'none' : 'flex';
    if (capL) capL.textContent = live
      ? 'Sembradora en vivo \xb7 gris = surco cortado'
      : 'Sembradora \xb7 color = motor';
    if (capR) capR.textContent = live
      ? ((state.aogSpeed || 0).toFixed(1) + ' km/h \xb7 ' + (state.aogAreaHa || 0).toFixed(1) + ' ha')
      : 'toc\xe1 un surco para pintarlo con el motor activo';
  }

  // Aplica un payload live {ok, count, nodos} — mismo shape por WS y por HTTP,
  // así el push y el fallback comparten este único camino de procesamiento.
  function applyLive(data) {
    var nodos = (data && data.nodos) || [];
    if (statusEl) {
      statusEl.className = 'pill ' + (nodos.length > 0 ? 'ok' : 'warn');
      statusEl.innerHTML = '<span class="dot"></span> ' + nodos.length + ' nodo' + (nodos.length !== 1 ? 's' : '') + ' QuantiX';
    }
    // Mantener cache para Calibración (necesita pulsos)
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      var uid = n.uid;
      var motors = n.motors_live || [];
      state.liveByUid[uid] = { online: !!n.online, motors: motors, firmware: n.firmware || '' };
    }
    // Sin gate por state.activeTab: escribir unos textContent en tabs
    // ocultas es gratis y el gate solo agregaba estados congelables. Cada
    // updater aislado en try para que uno roto no mate a los siguientes.
    try { updateCalibrarPulses(); } catch (_) {}
    try { updateMotoresEstado(); } catch (_) {}
    try { updatePidLive(); } catch (_) {}
    try { updatePruebaLive(); } catch (_) {}
    // Siembra se re-renderiza con el estado AOG cacheado; el fetch de
    // /api/aog/state lo hace el loop de polling (1 vez por período, no
    // por cada push WS).
    if (state.activeTab === 'siembra') {
      computeEnMarcha();
      applyMarchaChrome();
      renderSiembra();
    }
  }

  async function pollLive() {
    try {
      var res = await fetch('/api/quantix/live', { cache: 'no-store' });
      var data = await res.json();
      applyLive(data);
    } catch (e) {
      if (statusEl) {
        statusEl.className = 'pill err';
        statusEl.innerHTML = '<span class="dot"></span> Sin conexión';
      }
    }
  }

  // ============================================================================
  // MOTORES — CRUD persistente + send config MQTT
  // ============================================================================

  async function loadMotores() {
    try {
      var res = await fetch('/api/quantix/motores', { cache: 'no-store' });
      var data = await res.json();
      if (data && data.ok && data.config) {
        state.motoresCfg = data.config;
        if (!state.motoresCfg.nodos) state.motoresCfg.nodos = [];
      }
    } catch (e) { /* ignore */ }
    if (state.activeTab === 'siembra') renderSiembra();
  }

  function defaultMotor(nombre) {
    return {
      nombre: nombre || 'Motor', dosis_fija: 0,
      unidad_dosis: 'kg_ha', semillas_vuelta: 0, campo_dosis: '',
      kp: 80, ki: 30, kd: 0, pwm_min: 600, pwm_max: 4095, meter_cal: 50,
      max_integral: 1200, deadband: 2, slew_rate: 40, dientes_engranaje: 20,
      sensor_tipo: 'inductivo', pulse_min: 2000,
      motor_type: 0, max_hz: 40, ff_gain: 1.0, alpha: 0.4,
      habilitado: true,
      slew_rate_per_sec: 5000, target_slew_hz_per_sec: 300, pid_time: 50,
      // cortes[] = surcos/secciones PilotX que alimenta este motor (se pintan
      // directo en la tira de surcos). Es lo que consume el bridge.
      cortes: [], tren: 0
    };
  }


  // Sincroniza cfg.trenes desde el implemento central antes de persistir
  // (los trenes ya no se editan en QuantiX; vienen del implemento).
  function syncTrenes() {
    var cfg = state.motoresCfg;
    if (state.implCentral && Array.isArray(state.implCentral.trenes)) {
      cfg.trenes = state.implCentral.trenes.map(function (t) {
        return { id: t.id | 0, nombre: t.nombre || ('Tren ' + t.id), distancia_m: t.distancia_m || 0 };
      });
    }
  }

  async function saveMotores() {
    var msg = $('mtMsg');
    var huerfanos = surcosHuerfanos();
    if (huerfanos.length > 0) {
      if (!await AgpModal.confirm('Surcos sin motor', 'Hay ' + huerfanos.length + ' surco' + (huerfanos.length !== 1 ? 's' : '') +
        ' sin motor asignado (' + huerfanos.join(', ') + '). ¿Guardar igual?')) {
        if (msg) msg.textContent = 'Guardado cancelado.';
        return;
      }
    }
    if (msg) msg.textContent = 'Guardando…';
    syncTrenes();
    try {
      var res = await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.motoresCfg)
      });
      var data = await res.json();
      if (msg) {
        if (data.ok) msg.textContent = '✓ Guardado.';
        else if (data.error === 'AGP-CFG-001')
          // Config inválida: código + friendly + detalle técnico del backend.
          msg.textContent = '✕ ' + data.error + ' · ' + data.mensaje + (data.detalle ? ' — ' + data.detalle : '');
        else msg.textContent = '✕ ' + (data.error || 'error');
      }
    } catch (e) { if (msg) msg.textContent = '✕ ' + e.message; }
  }

  async function sendAllNodos() {
    var msg = $('mtMsg'); if (msg) msg.textContent = 'Enviando todos…';
    syncTrenes();
    var cfg = state.motoresCfg;
    await fetch('/api/quantix/motores', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cfg)
    });
    var sent = 0, fail = 0;
    for (var i = 0; i < cfg.nodos.length; i++) {
      var n = cfg.nodos[i];
      if (!n.habilitado || !n.uid) continue;
      try {
        var res = await fetch('/api/quantix/' + encodeURIComponent(n.uid) + '/send', { method: 'POST' });
        var data = await res.json();
        if (data.ok) sent++; else fail++;
      } catch (e) { fail++; }
    }
    if (msg) msg.textContent = 'Enviados: ' + sent + ' · Fallos: ' + fail;
  }

  // Los nodos solo se incorporan vía announcement MQTT real (auto-descubrimiento).
  // Esto evita typos y desalineación con el firmware del nodo físico.

  $('btnSaveMotores').addEventListener('click', saveMotores);
  $('btnSendAll').addEventListener('click', sendAllNodos);

  (function bindStripPaint() {
    var strip = document.getElementById('qxStrip');
    if (!strip || strip._painted) return;
    strip._painted = true;
    var painting = false;
    function cellSurco(t) {
      return t && t.classList && t.classList.contains('cell')
        ? parseInt(t.getAttribute('data-surco'), 10) : NaN;
    }
    function apply(t) {
      var s = cellSurco(t);
      if (!isNaN(s)) { state.lastTouchedSurco = s; paintSurco(s); renderStrip(); renderMotorList(); }
    }
    strip.addEventListener('pointerdown', function (e) {
      if (state.siembraView !== 'planter' || state.activeTab !== 'siembra') return;
      painting = true; apply(e.target);
      try { strip.setPointerCapture(e.pointerId); } catch (err) {}
    });
    strip.addEventListener('pointermove', function (e) {
      if (!painting) return;
      apply(document.elementFromPoint(e.clientX, e.clientY));
    });
    strip.addEventListener('pointerup', function () { painting = false; });
    strip.addEventListener('pointercancel', function () { painting = false; });
  })();

  var btnAdd = document.getElementById('btnAddMotor');
  if (btnAdd) btnAdd.addEventListener('click', addMotor);
  var btnQuit = document.getElementById('btnQuitarSurco');
  if (btnQuit) btnQuit.addEventListener('click', quitarSurco);
  var btnAuto = document.getElementById('btnAutoReparto');
  if (btnAuto) btnAuto.addEventListener('click', function () {
    var all = allMotors();
    if (!all.length) return;
    autoReparto(all.length < totalSurcos() ? 'uno' : 'grupos');
  });

  var segP = document.getElementById('segPlanter');
  if (segP) segP.addEventListener('click', function () { setSiembraView('planter'); });
  var segT = document.getElementById('segTabla');
  if (segT) segT.addEventListener('click', function () { setSiembraView('tabla'); });

  // ============================================================================
  // MOTORES — config del fierro (sensor, motor, PWM, PID) en un solo lugar
  // ============================================================================
  //
  // Antes esto estaba repartido: el PPR en Calibración, el PWM mínimo en Prueba,
  // Kp/Ki/Kd y Max Hz en PID live, y el filtro del sensor en ningún lado (solo
  // en el JSON). Para saber cómo estaba armado un motor había que recorrer tres
  // pestañas. Acá va todo junto; las otras pestañas quedan para operar y medir.

  // Tipo de sensor → filtro antirrebote del nodo y PPR típico.
  //
  // Importa más de lo que parece: el filtro le pone techo a lo que el nodo puede
  // leer (1e6/pulse_min pulsos por segundo). Pasado ese techo NO se queda en el
  // máximo — cuenta uno de cada dos y reporta la mitad de las vueltas. Con
  // encoder de 600 ppr y el filtro de inductivo (2000 µs) el techo son 50 rpm:
  // el motor gira a 67 y el nodo dice 33 (medido en banco, 2026-08-01).
  var SENSORES = {
    inductivo: { pulse_min: 2000, ppr: 20,  label: 'Inductivo (pocos pulsos)' },
    encoder:   { pulse_min: 100,  ppr: 600, label: 'Encoder (cientos de pulsos)' }
  };

  function tipoSensorDe(m) {
    if (m && m.sensor_tipo) return m.sensor_tipo;
    // Config vieja sin el campo: lo deducimos del PPR guardado.
    return (m && m.dientes_engranaje >= 100) ? 'encoder' : 'inductivo';
  }

  // Vueltas por minuto que el nodo puede llegar a leer con ese filtro y ese PPR.
  function techoRpm(pulseMin, ppr) {
    if (!pulseMin || !ppr) return 0;
    return Math.round(60000000 / (pulseMin * ppr));
  }

  // Escribe un stepper de AGPSteps por código. OJO: el valor vive en un <input
  // type="hidden"> y el número que se VE es un <span class="agp-step-val">. Si
  // solo se toca el input, el operario no ve ningún cambio y parece que el
  // control no anda — que es exactamente lo que pasó con el primer intento.
  function setStepper(scope, campo, valor) {
    var inp = scope.querySelector('[data-mf="' + campo + '"]');
    if (!inp) return;
    inp.value = valor;
    var box = inp.closest('.agp-stepper');
    var span = box && box.querySelector('.agp-step-val');
    if (span) span.textContent = valor;
  }

  function readMf(scope, campo, fallback) {
    var inp = scope.querySelector('[data-mf="' + campo + '"]');
    if (!inp) return fallback;
    var v = parseFloat(inp.value);
    return isNaN(v) ? fallback : v;
  }

  function renderMotores() {
    var listEl = $('motList');
    var emptyEl = $('motEmpty');
    if (!listEl) return;
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (!nodos.length) {
      listEl.innerHTML = '';
      if (emptyEl) emptyEl.style.display = '';
      return;
    }
    if (emptyEl) emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      var live = state.liveByUid[n.uid];
      var fw = (live && live.firmware) ? (' · fw ' + escapeHtml(live.firmware)) : '';
      var nHab = (n.habilitado !== false);
      html += '<div class="card' + (nHab ? '' : ' node-dis') + '" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<div class="node-head">' +
          '<input class="qxNodoHab" type="checkbox" data-uid="' + escapeHtml(n.uid) + '"' + (nHab ? ' checked' : '') +
            ' title="Nodo activo en este perfil. Destildado no recibe dosis ni config.">' +
          '<h3 style="margin:0">' + escapeHtml(n.nombre || 'Nodo') +
            ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' +
            escapeHtml(n.uid) + '<span data-mot-fw>' + fw + '</span></span></h3>' +
          '<span class="pill ' + (live && live.online ? 'ok' : 'err') + '" data-mot-pill><span class="dot"></span> ' +
            (live && live.online ? 'en línea' : 'fuera de línea') + '</span>' +
          '<button class="btn danger qxNodoDel" type="button" data-uid="' + escapeHtml(n.uid) +
            '" title="Sacar el nodo del perfil y borrar su configuración">Eliminar nodo</button>' +
        '</div>' +
        '<div class="live-tune-grid">' +
          motorCfgCard(n, 0) +
          motorCfgCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
    if (window.AGPSteps) window.AGPSteps.bindSteppers(listEl);

    // Activar/desactivar el nodo en este perfil. Es reversible y NO borra nada:
    // el bridge saltea los nodos con habilitado===false (ver allMotors()).
    var nhabs = listEl.querySelectorAll('.qxNodoHab');
    for (var nh = 0; nh < nhabs.length; nh++) {
      nhabs[nh].addEventListener('change', function () {
        var uid = this.getAttribute('data-uid');
        var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
        for (var j = 0; j < ns.length; j++) {
          if (ns[j].uid === uid) { ns[j].habilitado = this.checked; break; }
        }
        state.dirty = true;
        guardarMotoresCfg();
      });
    }

    // Eliminar el nodo: lo saca del registro curado (y de la asignación del
    // implemento activo, que lo hace el backend) y borra su config de motores.
    var ndels = listEl.querySelectorAll('.qxNodoDel');
    for (var nd = 0; nd < ndels.length; nd++) {
      ndels[nd].addEventListener('click', async function () {
        var uid = this.getAttribute('data-uid');
        if (!confirm('¿Eliminar el nodo ' + uid + '?\n\nSe borra su configuración de motores y se lo saca del perfil. Si el nodo vuelve a anunciarse por MQTT va a reaparecer como pendiente.')) return;
        this.disabled = true;
        try {
          await fetch('/api/nodos/' + encodeURIComponent(uid), { method: 'DELETE' });
        } catch (e) { /* seguimos igual: la config local se limpia abajo */ }
        var ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
        state.motoresCfg.nodos = ns.filter(function (x) { return x.uid !== uid; });
        state.dirty = true;
        await guardarMotoresCfg();
        renderMotores();
        renderMotorList();
      });
    }
  }

  // Persiste state.motoresCfg. Se usa desde los controles de nodo, que aplican
  // al instante en vez de esperar al botón de guardar general.
  async function guardarMotoresCfg() {
    try {
      var r = await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.motoresCfg)
      });
      var d = await r.json();
      if (d && d.ok) state.dirty = false;
      return d && d.ok;
    } catch (e) { return false; }
  }

  function motorCfgCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    var tipo = tipoSensorDe(m);
    var ppr = m.dientes_engranaje || SENSORES[tipo].ppr;
    var pulseMin = m.pulse_min || SENSORES[tipo].pulse_min;
    var esHid = (m.motor_type | 0) === 1;
    var stepInt = function (campo, val, opts) {
      opts = opts || {};
      var attrs = 'data-mf="' + campo + '"';
      if (window.AGPSteps) {
        return window.AGPSteps.stepperHTML({
          value: val, min: opts.min, max: opts.max, mode: 'int', step: opts.step || 1, attrs: attrs
        });
      }
      return '<input type="number" ' + attrs + ' value="' + val + '">';
    };
    var stepPid = function (campo, val, mx) {
      var attrs = 'data-mf="' + campo + '"';
      if (window.AGPSteps) {
        return window.AGPSteps.stepperHTML({ value: val, min: 0, max: mx, mode: 'pid', attrs: attrs });
      }
      return '<input type="number" ' + attrs + ' value="' + val + '">';
    };

    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +

      // ── Sensor: qué cuenta las vueltas ──────────────────────────────────
      '<div class="cfg-block"><span class="cfg-block-t">Sensor</span>' +
        '<div class="fld-grid">' +
          '<div class="field wide"><label>Tipo</label>' +
            '<select data-mf="sensor_tipo">' +
              '<option value="inductivo"' + (tipo === 'encoder' ? '' : ' selected') + '>' + SENSORES.inductivo.label + '</option>' +
              '<option value="encoder"' + (tipo === 'encoder' ? ' selected' : '') + '>' + SENSORES.encoder.label + '</option>' +
            '</select></div>' +
          '<div class="field"><label>Pulsos por vuelta</label>' +
            stepInt('dientes_engranaje', ppr, { min: 1, max: 4000, step: 1 }) + '</div>' +
          '<div class="field"><label>Filtro antirrebote</label>' +
            stepInt('pulse_min', pulseMin, { min: 20, max: 20000, step: 10 }) + '</div>' +
        '</div>' +
        '<div class="cfg-hint" data-mf-out="techo">Con este filtro el nodo lee hasta ' +
          techoRpm(pulseMin, ppr) + ' rpm</div>' +
      '</div>' +

      // ── Motor: qué se está moviendo y con qué PWM ───────────────────────
      '<div class="cfg-block"><span class="cfg-block-t">Motor</span>' +
        '<div class="fld-grid">' +
          '<div class="field wide"><label>Tipo</label>' +
            '<select data-mf="motor_type">' +
              '<option value="0"' + (esHid ? '' : ' selected') + '>Eléctrico</option>' +
              '<option value="1"' + (esHid ? ' selected' : '') + '>Hidráulico</option>' +
            '</select></div>' +
          '<div class="field"><label>PWM mínimo</label>' +
            stepInt('pwm_min', m.pwm_min || 600, { min: 0, max: 4095, step: 10 }) + '</div>' +
          '<div class="field"><label>PWM máximo</label>' +
            stepInt('pwm_max', m.pwm_max || 4095, { min: 0, max: 4095, step: 10 }) + '</div>' +
        '</div>' +
      '</div>' +

      // ── PID: lo que usa el lazo para seguir la dosis ────────────────────
      '<div class="cfg-block"><span class="cfg-block-t">PID</span>' +
        '<div class="fld-grid">' +
          '<div class="field"><label>Kp</label>' + stepPid('kp', m.kp || 0, 300) + '</div>' +
          '<div class="field"><label>Ki</label>' + stepPid('ki', m.ki || 0, 200) + '</div>' +
          '<div class="field"><label>Kd</label>' + stepPid('kd', m.kd || 0, 50) + '</div>' +
        '</div>' +
        '<div class="kv" style="margin-top: var(--agp-sp-2)">' +
          '<div class="k">Tope del motor (Max Hz)</div>' +
          '<div class="v"><span data-mf-out="max_hz">' + (m.max_hz || 0) + '</span> Hz' +
            ' <span style="color:var(--agp-text-muted)">· <span data-mf-out="max_rpm">' +
            Math.round(((m.max_hz || 0) * 60) / (ppr || 1)) + '</span> rpm</span></div>' +
        '</div>' +
      '</div>' +

      '<div class="btn-row" style="margin-top: var(--agp-sp-3)">' +
        '<button class="btn primary" data-mot-act="save" data-mi="' + mi + '">Guardar y enviar</button>' +
        '<button class="btn" data-mot-act="maxhz" data-mi="' + mi + '" title="Gira el motor a PWM máximo 4 s y guarda el tope medido">⏱ Medir tope</button>' +
        '<span class="send-msg" data-mot-msg="' + mi + '"></span>' +
      '</div>' +
    '</div>';
  }

  // Cambiar el tipo de sensor precarga PPR y filtro típicos. No guarda solo:
  // el operario confirma con "Guardar y enviar" (puede querer corregir el PPR).
  var tabMotoresEl = document.getElementById('tabMotores');
  if (tabMotoresEl) {
    tabMotoresEl.addEventListener('change', function (ev) {
      var sel = ev.target.closest('select[data-mf="sensor_tipo"]');
      if (!sel) return;
      var mc = sel.closest('.motor-cfg');
      if (!mc) return;
      var def = SENSORES[sel.value === 'encoder' ? 'encoder' : 'inductivo'];
      setStepper(mc, 'dientes_engranaje', def.ppr);
      setStepper(mc, 'pulse_min', def.pulse_min);
      refrescarTecho(mc);
    });

    // El techo de lectura se recalcula al tocar PPR o filtro (los [−]/[+] de
    // AGPSteps disparan 'input' sobre el hidden).
    tabMotoresEl.addEventListener('input', function (ev) {
      var inp = ev.target.closest('[data-mf="pulse_min"], [data-mf="dientes_engranaje"]');
      if (!inp) return;
      var mc = inp.closest('.motor-cfg');
      if (mc) refrescarTecho(mc);
    });

    tabMotoresEl.addEventListener('click', async function (ev) {
      var btn = ev.target.closest('button[data-mot-act]');
      if (!btn) return;
      var card = btn.closest('.card[data-uid]');
      var mc = btn.closest('.motor-cfg');
      var uid = card.getAttribute('data-uid');
      var mi = parseInt(btn.getAttribute('data-mi'), 10);
      var msgEl = mc.querySelector('span[data-mot-msg="' + mi + '"]');

      if (btn.getAttribute('data-mot-act') === 'save') {
        await guardarMotorCfg(uid, mi, mc, msgEl);
      } else {
        // Reusa la medición de la pestaña PID: gira a 4095 y guarda el pico.
        await pidMaxHzHandler(uid, mi, mc, btn, msgEl);
        var m2 = findMotor(uid, mi);
        var out = mc.querySelector('[data-mf-out="max_hz"]');
        var outR = mc.querySelector('[data-mf-out="max_rpm"]');
        if (out && m2) out.textContent = m2.max_hz || 0;
        if (outR && m2) {
          var pprAct = readMf(mc, 'dientes_engranaje', m2.dientes_engranaje || 1);
          outR.textContent = Math.round(((m2.max_hz || 0) * 60) / (pprAct || 1));
        }
      }
    });
  }

  // El estado del nodo llega por WS después de dibujar las tarjetas: sin esto
  // la pestaña se quedaba diciendo "fuera de línea" con el nodo andando.
  function updateMotoresEstado() {
    var listEl = $('motList');
    if (!listEl) return;
    listEl.querySelectorAll('.card[data-uid]').forEach(function (card) {
      var live = state.liveByUid[card.getAttribute('data-uid')];
      var pill = card.querySelector('[data-mot-pill]');
      if (pill) {
        pill.className = 'pill ' + (live && live.online ? 'ok' : 'err');
        pill.innerHTML = '<span class="dot"></span> ' + (live && live.online ? 'en línea' : 'fuera de línea');
      }
      var fw = card.querySelector('[data-mot-fw]');
      if (fw) fw.textContent = (live && live.firmware) ? (' · fw ' + live.firmware) : '';
    });
  }

  function refrescarTecho(mc) {
    var out = mc.querySelector('[data-mf-out="techo"]');
    if (!out) return;
    var pm = readMf(mc, 'pulse_min', 0);
    var pr = readMf(mc, 'dientes_engranaje', 0);
    var rpm = techoRpm(pm, pr);
    out.textContent = 'Con este filtro el nodo lee hasta ' + rpm + ' rpm';
    // Menos de 120 rpm de techo es sospechoso: arriba de ahí el nodo empieza a
    // reportar la mitad de las vueltas sin avisar.
    out.className = 'cfg-hint' + (rpm > 0 && rpm < 120 ? ' warn' : '');
  }

  async function guardarMotorCfg(uid, mi, mc, msgEl) {
    var motor = findMotor(uid, mi);
    if (!motor) { msgEl.textContent = '✕ no encuentro el motor'; msgEl.className = 'send-msg err'; return; }

    var sel = mc.querySelector('select[data-mf="sensor_tipo"]');
    var selMt = mc.querySelector('select[data-mf="motor_type"]');
    motor.sensor_tipo = (sel && sel.value === 'encoder') ? 'encoder' : 'inductivo';
    motor.motor_type = selMt ? (parseInt(selMt.value, 10) || 0) : 0;
    motor.dientes_engranaje = readMf(mc, 'dientes_engranaje', motor.dientes_engranaje);
    motor.pulse_min = readMf(mc, 'pulse_min', motor.pulse_min);
    motor.pwm_min = readMf(mc, 'pwm_min', motor.pwm_min);
    motor.pwm_max = readMf(mc, 'pwm_max', motor.pwm_max);
    motor.kp = readMf(mc, 'kp', motor.kp);
    motor.ki = readMf(mc, 'ki', motor.ki);
    motor.kd = readMf(mc, 'kd', motor.kd);

    msgEl.textContent = '… guardando'; msgEl.className = 'send-msg';
    try {
      await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.motoresCfg)
      });
      var res = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
      var data = await res.json();
      msgEl.textContent = data.ok
        ? '✓ guardado y enviado al nodo'
        : '✕ guardado, pero el nodo no contestó';
      msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message;
      msgEl.className = 'send-msg err';
    }
  }

  // ============================================================================
  // PID LIVE-TUNE
  // ============================================================================

  function renderPid() {
    var listEl = $('pidList');
    var emptyEl = $('pidEmpty');
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (nodos.length === 0) { listEl.innerHTML = ''; emptyEl.style.display = ''; return; }
    emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      var live = state.liveByUid[n.uid];
      html += '<div class="card" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<div class="node-head">' +
          '<h3 style="margin:0">' + escapeHtml(n.nombre || 'Nodo') + ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' + escapeHtml(n.uid) + '</span></h3>' +
          '<span class="pill ' + (live && live.online ? 'ok' : 'err') + '"><span class="dot"></span> ' + (live && live.online ? 'online' : 'offline') + '</span>' +
        '</div>' +
        '<div class="live-tune-grid">' +
          pidTuneCard(n, 0) +
          pidTuneCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;

    // Enganchar [−]/[+] de los steppers Kp/Ki/Kd. El paso se calcula sobre el
    // valor *actual* (adaptativo PID): v<1→0.01, v<10→0.1, v<100→1, v≥100→5.
    // bindSteppers es idempotente sobre el mismo root.
    if (window.AGPSteps) window.AGPSteps.bindSteppers(listEl);
  }

  function pidTuneCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    // Texto inicial con decimales acordes al paso adaptativo. El step queda
    // como hint; AGPSteps.attachAdaptive lo va a sobreescribir en runtime.
    var fmtPid = function (v) {
      return window.AGPSteps ? window.AGPSteps.fmt(v, window.AGPSteps.pid(v)) : String(v);
    };
    // PID con steppers adaptativos (sin slider). Paso = pid(value):
    //   v < 1   → 0.01    v < 10  → 0.1
    //   v < 100 → 1       v ≥ 100 → 5
    // El step acompaña al valor actual — fácil afinar Kd=0.5 y a la vez Kp=120.
    var stepperPid = function (name, val, mx) {
      return window.AGPSteps
        ? window.AGPSteps.stepperHTML({
            value: val, min: 0, max: mx, mode: 'pid',
            attrs: 'data-tune="' + name + '"'
          })
        : '<input type="number" data-tune="' + name + '" value="' + val + '" min="0" max="' + mx + '">';
    };
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +
      '<div class="kv" style="margin-top:0">' +
        '<div class="k">rpm</div><div class="v" data-live="rpm">—</div>' +
        '<div class="k">Dosis real</div><div class="v" data-live="dosis_real">—</div>' +
        '<div class="k">Dosis obj.</div><div class="v" data-live="dosis_target">—</div>' +
        '<div class="k">PWM</div><div class="v" data-live="pwm">—</div>' +
      '</div>' +
      '<div class="fld-grid" style="margin-top: var(--agp-sp-3)">' +
        '<div class="field"><label>Kp <span data-show="kp">' + fmtPid(m.kp) + '</span></label>' +
          stepperPid('kp', m.kp, 300) + '</div>' +
        '<div class="field"><label>Ki <span data-show="ki">' + fmtPid(m.ki) + '</span></label>' +
          stepperPid('ki', m.ki, 200) + '</div>' +
        '<div class="field"><label>Kd <span data-show="kd">' + fmtPid(m.kd) + '</span></label>' +
          stepperPid('kd', m.kd, 50) + '</div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-tune-act="push" data-mi="' + mi + '">Aplicar Kp/Ki/Kd</button>' +
        '<button class="btn" data-tune-act="maxhz" data-mi="' + mi + '" title="Mide Hz pico con PWM máximo durante 4s y guarda en max_hz">⏱ Medir Max Hz</button>' +
        '<button class="btn" data-tune-act="autotune" data-mi="' + mi + '" title="Auto-Tune PID (Ziegler-Nichols). Tarda ~30s">🎯 Auto-Tune</button>' +
        '<span class="send-msg" data-tune-msg="' + mi + '"></span>' +
      '</div>' +
    '</div>';
  }

  function updatePidLive() {
    document.querySelectorAll('#pidList .card[data-uid]').forEach(function (card) {
      var uid = card.getAttribute('data-uid');
      var live = state.liveByUid[uid];
      if (!live || !live.motors) return;
      card.querySelectorAll('.motor-cfg').forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = null;
        for (var k = 0; k < live.motors.length; k++) {
          if ((live.motors[k].id | 0) === mi) { m = live.motors[k]; break; }
        }
        if (!m) return;
        var t = m.pps_target || 0;
        var r = m.pps_real || 0;
        var p = m.pwm || 0;
        var cfg = findMotor(uid, mi);
        var ctx = qxAgro.ctxFrom(state.implCentral, state.aogSpeed);
        var elRpm = mc.querySelector('[data-live="rpm"]');
        var elDR  = mc.querySelector('[data-live="dosis_real"]');
        var elDT  = mc.querySelector('[data-live="dosis_target"]');
        var elP   = mc.querySelector('[data-live="pwm"]');
        if (elRpm) elRpm.textContent = ppsToRpm(cfg, r).toFixed(0) + ' rpm';
        if (elDR) elDR.textContent = cfg ? qxAgro.label(qxAgro.units(cfg, r, ctx)) : '\u2014';
        if (elDT) elDT.textContent = cfg ? qxAgro.label(qxAgro.units(cfg, t, ctx)) : '\u2014';
        if (elP) elP.textContent = p;
      });
    });
  }

  document.getElementById('tabPid').addEventListener('input', function (ev) {
    var s = ev.target.closest('input[data-tune]');
    if (!s) return;
    // Antes el input era hijo directo del .field (al lado del label que tiene
    // el [data-show]). Con el stepper, el input vive dentro del .agp-stepper,
    // así que subimos hasta .field para encontrar el data-show del label.
    var field = s.closest('.field');
    var sh = field && field.querySelector('span[data-show]');
    if (sh) {
      // Decimales acordes al paso adaptativo (0.01 → "0.30", 1 → "80").
      sh.textContent = window.AGPSteps
        ? window.AGPSteps.fmt(s.value, window.AGPSteps.pid(s.value))
        : s.value;
    }
  });

  document.getElementById('tabPid').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-tune-act]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(mc.getAttribute('data-mi'), 10);
    var act = btn.getAttribute('data-tune-act');
    var msgEl = mc.querySelector('span[data-tune-msg="' + mi + '"]');

    if (act === 'push') return pidPushHandler(uid, mi, mc, msgEl);
    if (act === 'maxhz') return pidMaxHzHandler(uid, mi, mc, btn, msgEl);
    if (act === 'autotune') return pidAutoTuneHandler(uid, mi, mc, btn, msgEl);
  });

  async function pidPushHandler(uid, mi, mc, msgEl) {
    var kp = parseFloat(mc.querySelector('input[data-tune="kp"]').value);
    var ki = parseFloat(mc.querySelector('input[data-tune="ki"]').value);
    var kd = parseFloat(mc.querySelector('input[data-tune="kd"]').value);
    msgEl.textContent = '… enviando'; msgEl.className = 'send-msg';

    // Persistir en motores cfg local
    var nIdx = -1;
    for (var i = 0; i < state.motoresCfg.nodos.length; i++) {
      if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
    }
    if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
      var mref = state.motoresCfg.nodos[nIdx].motores[mi];
      mref.kp = kp; mref.ki = ki; mref.kd = kd;
    }

    // Payload "config" parcial: solo idx + config_pid (firmware lo mergea)
    var payload = JSON.stringify({ configs: [{ idx: mi, config_pid: { kp: kp, ki: ki, kd: kd } }] });
    try {
      var res = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=config&retain=true', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload
      });
      var data = await res.json();
      msgEl.textContent = data.ok ? '✓ aplicado' : '✕ ' + (data.error || 'fallo');
      msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');

      // Persistir motores.json
      await fetch('/api/quantix/motores', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.motoresCfg)
      });
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message; msgEl.className = 'send-msg err';
    }
  }

  // ── Medir Max Hz ──────────────────────────────────────────────────────────
  // Sube el motor por rampa hasta PWM=4095, muestrea ppsReal cada 200ms
  // durante 4s, manda PWM=0 (stop) y guarda el máximo como motor.max_hz en
  // motores.json + push por MQTT /config.
  async function pidMaxHzHandler(uid, mi, mc, btn, msgEl) {
    if (btn.disabled) return;
    btn.disabled = true;
    var origText = btn.textContent;
    btn.textContent = '⏳ Midiendo…';
    msgEl.textContent = '… motor a PWM máximo 4s'; msgEl.className = 'send-msg';

    var peak = 0;
    // El nodo publica el PWM que REALMENTE aplicó. Si algo se lo pisa (corte
    // por sección, PID, watchdog), el pico de Hz sale bajo y guardarlo arruina
    // el feedforward del PID. Por eso medimos también el PWM aplicado.
    var pwmMax = 0, pwmMin = 4095, muestras = 0;
    try {
      // ARRANQUE POR RAMPA — no saltar de 0 a 4095.
      //
      // Un motor parado al que se le tira el PWM máximo de golpe no arranca:
      // se queda clavado. Medido en banco (2026-08-01, mismo motor, mismo
      // mensaje MQTT, mismo PWM final):
      //     salto directo a 4095 → 134 Hz (13 rpm)
      //     rampa 600→4095       → 672 Hz (67 rpm)
      // Por eso este botón venía guardando topes 5 veces más bajos que el
      // real, y con ese max_hz el feedforward del PID arranca mal.
      var cfgM = findMotor(uid, mi);
      var pwmIni = Math.max(400, (cfgM && cfgM.pwm_min) || 600);
      msgEl.textContent = '… subiendo el motor por rampa';
      for (var p = pwmIni; p < 4095; p += 250) {
        await sendTest(uid, mi, p);
        await new Promise(function (r) { setTimeout(r, 250); });
      }
      await sendTest(uid, mi, 4095);
      msgEl.textContent = '… midiendo a PWM máximo';

      // Muestrear ppsReal 20 veces * 200ms = 4s.
      //
      // Pedimos el live FRESCO en cada muestra en vez de leer state.liveByUid:
      // esa caché se refresca cada 500-2000 ms según la pestaña (y con WS mudo,
      // menos), así que muestrearla 20 veces devolvía 2 o 3 valores distintos —
      // casi siempre del arranque del motor. Así medimos 133 Hz un motor que
      // hace 695 (banco 2026-08-01).
      for (var k = 0; k < 20; k++) {
        await new Promise(function (r) { setTimeout(r, 200); });
        var m = await fetchLiveMotor(uid, mi);
        if (m) {
          var pps = m.pps_real || 0;
          if (pps > peak) peak = pps;
          // Las 2 primeras muestras son arranque: el nodo puede no haber
          // procesado el start todavía.
          if (k >= 2) {
            var pw = m.pwm || 0;
            if (pw > pwmMax) pwmMax = pw;
            if (pw < pwmMin) pwmMin = pw;
            muestras++;
          }
        }
      }
    } finally {
      try { await sendTest(uid, mi, 0); } catch (e) {}
    }

    btn.disabled = false;
    btn.textContent = origText;

    if (peak < 1) {
      msgEl.textContent = '✕ no se detectaron pulsos — revisá el sensor';
      msgEl.className = 'send-msg err';
      return;
    }

    // El nodo nunca sostuvo el PWM máximo: la medición no sirve, no la guardamos.
    if (muestras > 0 && pwmMin < 3900) {
      msgEl.textContent = '✕ el nodo no sostuvo el PWM: aplicó ' + pwmMin + '–' + pwmMax +
        ' de 4095. No se guardó. Apagá la dosis/secciones y actualizá el firmware del nodo.';
      msgEl.className = 'send-msg err';
      return;
    }

    // Aplicar y guardar.
    var maxHz = Math.round(peak * 10) / 10;
    var nIdx = -1;
    for (var i = 0; i < state.motoresCfg.nodos.length; i++)
      if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
    if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
      state.motoresCfg.nodos[nIdx].motores[mi].max_hz = maxHz;
      try {
        await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
      } catch (e) {}
    }
    msgEl.textContent = '✓ Max Hz = ' + maxHz.toFixed(1) + ' (aplicado)';
    msgEl.className = 'send-msg ok';
  }

  // ── Auto-Tune PID ─────────────────────────────────────────────────────────
  // Manda {"cmd":"autotune_start","id":mi} al topic /cmd, después poolea el
  // endpoint /autotune cada 1s buscando un resultado posterior al inicio,
  // hasta 50s de timeout. Si llega ok=true, pide confirmación y aplica
  // Kp/Ki/Kd.
  async function pidAutoTuneHandler(uid, mi, mc, btn, msgEl) {
    if (btn.disabled) return;
    btn.disabled = true;
    var origText = btn.textContent;
    btn.textContent = '⏳ Tuning…';
    msgEl.textContent = '… autotune en curso (hasta 50s)'; msgEl.className = 'send-msg';

    var startedAt = Date.now();

    try {
      var startRes = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cmd&retain=false', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: 'autotune_start', id: mi })
      });
      var startData = await startRes.json();
      if (!startData.ok) {
        msgEl.textContent = '✕ no se pudo enviar start: ' + (startData.error || 'fallo');
        msgEl.className = 'send-msg err';
        btn.disabled = false; btn.textContent = origText;
        return;
      }
    } catch (e) {
      msgEl.textContent = '✕ ' + e.message;
      msgEl.className = 'send-msg err';
      btn.disabled = false; btn.textContent = origText;
      return;
    }

    // Poll: cada 1s, buscar resultado con receivedUtc > startedAt
    var TIMEOUT_MS = 50000;
    var result = null;
    while (Date.now() - startedAt < TIMEOUT_MS) {
      await new Promise(function (r) { setTimeout(r, 1000); });
      try {
        var pr = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/autotune', { cache: 'no-store' });
        var pd = await pr.json();
        if (pd && pd.ok && pd.has_result && pd.result) {
          var ts = Date.parse(pd.result.received_utc);
          if (!isNaN(ts) && ts >= startedAt - 1000 && (pd.result.motor_id === mi || pd.result.motor_id === 0)) {
            result = pd.result; break;
          }
        }
      } catch (e) {}
    }

    btn.disabled = false;
    btn.textContent = origText;

    if (!result) {
      msgEl.textContent = '✕ timeout — el firmware no respondió en 50s';
      msgEl.className = 'send-msg err';
      return;
    }

    if (!result.ok) {
      msgEl.textContent = '✕ autotune falló — el motor no oscilo lo suficiente';
      msgEl.className = 'send-msg err';
      return;
    }

    var kp = parseFloat(result.kp).toFixed(1);
    var ki = parseFloat(result.ki).toFixed(1);
    var kd = parseFloat(result.kd).toFixed(1);
    var apply = await AgpModal.confirm('Auto-Tune completado', 'Kp = ' + kp + '\nKi = ' + ki + '\nKd = ' + kd + '\n\n¿Aplicar estos valores?');
    if (!apply) {
      msgEl.textContent = 'Resultado descartado (Kp=' + kp + ' Ki=' + ki + ' Kd=' + kd + ')';
      msgEl.className = 'send-msg';
      return;
    }

    // Actualizar steppers (antes sliders) y persistir.
    // El input vive adentro del .agp-stepper; subimos a .field para encontrar
    // tanto el [data-show] del label como el [.agp-step-val] del propio stepper.
    var pidFmt = function (v) {
      return window.AGPSteps ? window.AGPSteps.fmt(v, window.AGPSteps.pid(v)) : v;
    };
    var setTune = function (name, val) {
      var inp = mc.querySelector('input[data-tune="' + name + '"]');
      if (!inp) return;
      inp.value = val;
      var field = inp.closest('.field');
      var sh = field && field.querySelector('span[data-show]');
      if (sh) sh.textContent = pidFmt(val);
      var stepVal = inp.parentElement && inp.parentElement.querySelector('.agp-step-val');
      if (stepVal) stepVal.textContent = pidFmt(val);
      inp.dispatchEvent(new Event('input', { bubbles: true }));
    };
    setTune('kp', kp); setTune('ki', ki); setTune('kd', kd);

    return pidPushHandler(uid, mi, mc, msgEl);
  }

  // ============================================================================
  // CALIBRACIÓN — el flujo viejo (FormQuantiXCalibrar) restaurado en HTML.
  //
  // Por qué este flujo y no "girá libre + ingresá lo que salió":
  //   · el operario quiere indicar de antemano CUÁNTO girar (10 vueltas,
  //     N pulsos) — el firmware para solo cuando llega a la meta y queda
  //     un Δ pulsos prolijo y repetible.
  //   · medir varios surcos y promediar es la única forma de bajar el ruido
  //     de las balanzas/conteos manuales.
  //   · "Calcular" debe ser un paso explícito — si lo hacemos implícito,
  //     el usuario no sabe cuándo el número de pantalla refleja lo que va
  //     a guardarse.
  //
  // Comando MQTT (lo espera firmware Quantix2Motors → MQTT_Custom.cpp:288):
  //   topic: agp/quantix/{uid}/cal
  //   start: {"cmd":"start","id":N,"pulsos":META,"pwm":P}   ← META = vueltas*ppr
  //   stop : {"cmd":"stop","id":N}
  // ============================================================================

  // uid -> mi -> { startPulsos, endPulsos, ppr, pwm, vueltas, surcoVals[] }
  var calState = {};

  function renderCalibrar() {
    var listEl = $('calList');
    var emptyEl = $('calEmpty');
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (nodos.length === 0) { listEl.innerHTML = ''; emptyEl.style.display = ''; return; }
    emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      html += '<div class="card" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<h3 style="margin-top:0">' + escapeHtml(n.nombre || 'Nodo') + ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' + escapeHtml(n.uid) + '</span></h3>' +
        '<p style="color: var(--agp-text-muted); margin-top: 0">' +
          '1) Configurá <strong>vueltas a girar</strong> y <strong>PWM</strong>. 2) Apretá <strong>Iniciar</strong>: ' +
          'el motor gira hasta llegar a la meta de pulsos y para solo. 3) Pesá/contá el producto recolectado por surco. ' +
          '4) Apretá <strong>Calcular</strong>: promedia los surcos y calcula <code>MeterCal = pulsos / unidades</code>.' +
        '</p>' +
        '<div class="live-tune-grid">' +
          calCard(n, 0) +
          calCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
    if (window.AGPSteps) window.AGPSteps.bindSteppers(listEl);
    // Render inicial de los inputs por surco (default 6) y suscripción a cambios.
    listEl.querySelectorAll('.motor-cfg').forEach(function (mc) { rebuildSurcoInputs(mc); });
    listEl.addEventListener('input', function (ev) {
      var inp = ev.target;
      if (!inp || !inp.getAttribute) return;
      // Cuando cambia "vueltas" o "ppr" recalculamos la meta total mostrada.
      var f = inp.getAttribute('data-cal-f');
      if (f === 'vueltas' || f === 'ppr') {
        var mc = inp.closest('.motor-cfg'); if (mc) updateMetaPulsos(mc);
      }
      // Cuando cambia "surcos" rearmamos la lista dinámica.
      if (f === 'surcos') {
        var mc2 = inp.closest('.motor-cfg'); if (mc2) rebuildSurcoInputs(mc2);
      }
    });
  }

  // Stepper helper para los campos numéricos enteros de calibración.
  // Reusa AGPSteps.stepperHTML con modo 'int' (paso fijo) para PWM/vueltas/ppr/surcos.
  function calIntStepper(field, value, opts) {
    opts = opts || {};
    var attrs = 'data-cal-f="' + field + '"';
    if (window.AGPSteps) {
      return window.AGPSteps.stepperHTML({
        value: value, min: opts.min, max: opts.max, mode: 'int',
        step: opts.step || 1, attrs: attrs
      });
    }
    return '<input type="number" ' + attrs +
      (opts.min != null ? ' min="' + opts.min + '"' : '') +
      (opts.max != null ? ' max="' + opts.max + '"' : '') +
      ' step="' + (opts.step || 1) + '" value="' + value + '">';
  }

  function calCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    // El PPR y el sensor son config del fierro: se editan en la pestaña
    // Motores. Acá se usan para la cuenta y se muestran, nada más — dos
    // lugares editando lo mismo es como se llegó al lío anterior.
    var ppr = m.dientes_engranaje || SENSORES[tipoSensorDe(m)].ppr;
    var pwmDef = Math.round(((m.pwm_min || 600) + (m.pwm_max || 4095)) / 2);
    var defaultVueltas = 10;
    var defaultSurcos = 6;
    var metaIni = defaultVueltas * ppr;
    var esSemCal = m.unidad_dosis === 'sem_m';
    var actualLbl = esSemCal ? 'Sem/vuelta actual' : 'MeterCal actual';
    var actualVal = esSemCal ? (m.semillas_vuelta || 0) : (m.meter_cal || 0);
    var calcLbl = esSemCal ? 'Sem/vuelta calculado' : 'MeterCal calculado';
    var applyLbl = esSemCal ? '💾 Guardar sem/vuelta' : '💾 Guardar MeterCal';
    var unidadHint = esSemCal
      ? 'Contá las <strong>semillas</strong> caídas en cada surco. Promediar varios mejora la precisión.'
      : 'Ingresá <strong>gramos</strong> medidos en cada surco. Promediar varios mejora la precisión.';
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') +
        ' <span style="font-weight:normal;color:var(--agp-text-muted);font-size:var(--agp-fs-sm)">· ' +
        (esSemCal ? 'semilla (sem/m)' : 'masa (kg/ha)') + '</span></h4>' +

      // ── Parámetros (lo que el operario configura ANTES de iniciar) ──────
      '<div class="fld-grid" style="margin-top:0">' +
        '<div class="field"><label>Vueltas a girar</label>' +
          calIntStepper('vueltas', defaultVueltas, { min: 1, max: 100, step: 1 }) + '</div>' +
        '<div class="field"><label>PWM</label>' +
          calIntStepper('pwm', pwmDef, { min: 0, max: 4095, step: 10 }) + '</div>' +
        '<div class="field"><label>Cantidad de surcos</label>' +
          calIntStepper('surcos', defaultSurcos, { min: 1, max: 20, step: 1 }) + '</div>' +
      '</div>' +
      // PPR fijo: viene de Motores. Se muestra porque define la meta de pulsos.
      '<input type="hidden" data-cal-f="ppr" value="' + ppr + '">' +
      '<div class="kv" style="margin-top: var(--agp-sp-2)">' +
        '<div class="k">Pulsos por vuelta</div><div class="v">' + ppr +
          ' <span style="color:var(--agp-text-muted)">· se configura en Motores</span></div>' +
        '<div class="k">Meta total</div><div class="v"><span data-cal="meta">' + metaIni + '</span> pulsos</div>' +
        '<div class="k">' + actualLbl + '</div><div class="v">' + actualVal + '</div>' +
      '</div>' +

      // ── Botones de control del motor ────────────────────────────────────
      '<div class="btn-row" style="margin-top: var(--agp-sp-3)">' +
        '<button class="btn primary" data-cal-act="start" data-mi="' + mi + '">▶ Iniciar</button>' +
        '<button class="btn" data-cal-act="stop" data-mi="' + mi + '">■ Detener</button>' +
        '<button class="btn" data-cal-act="reset" data-mi="' + mi + '">⟲ Reset</button>' +
        '<span class="send-msg" data-cal-msg="' + mi + '"></span>' +
      '</div>' +

      // ── Estado en vivo (refrescado por updateCalibrarPulses) ────────────
      '<div class="kv" style="margin-top: var(--agp-sp-3)">' +
        '<div class="k">Pulsos contados</div><div class="v" data-cal="pulsos">—</div>' +
        '<div class="k">Δ pulsos (desde Iniciar)</div><div class="v" data-cal="delta">—</div>' +
        '<div class="k">Vueltas reales</div><div class="v" data-cal="vueltasReales">—</div>' +
        '<div class="k">PWM actual</div><div class="v" data-cal="pwmCur">—</div>' +
      '</div>' +

      // ── Surcos (lista dinámica) + Calcular ──────────────────────────────
      '<h4 style="margin-top: var(--agp-sp-4)">Resultado por surco</h4>' +
      '<p style="color: var(--agp-text-muted); margin-top:0">' + unidadHint + '</p>' +
      '<div class="fld-grid" data-cal="surcoList"></div>' +
      '<div class="btn-row" style="margin-top: var(--agp-sp-3)">' +
        '<button class="btn primary" data-cal-act="calc" data-mi="' + mi + '">✓ Calcular</button>' +
        '<button class="btn" data-cal-act="apply" data-mi="' + mi + '" disabled>' + applyLbl + '</button>' +
        '<span class="send-msg" data-cal-result="' + mi + '"></span>' +
      '</div>' +
      '<div class="kv" style="margin-top: var(--agp-sp-2)" data-cal="resultBox" hidden>' +
        '<div class="k">Promedio por surco</div><div class="v" data-cal="prom">—</div>' +
        '<div class="k">Unidades / pulso</div><div class="v" data-cal="upp">—</div>' +
        '<div class="k">' + calcLbl + '</div><div class="v" data-cal="newcal">—</div>' +
      '</div>' +
    '</div>';
  }

  // Vueltas × PPR = meta de pulsos que el ESP debe contar antes de parar.
  function updateMetaPulsos(mc) {
    var v = parseInt((mc.querySelector('input[data-cal-f="vueltas"]') || {}).value, 10) || 0;
    var p = parseInt((mc.querySelector('input[data-cal-f="ppr"]')     || {}).value, 10) || 0;
    var meta = v * p;
    var el = mc.querySelector('[data-cal="meta"]');
    if (el) el.textContent = meta.toLocaleString();
  }

  // Rebuild dinámico de los inputs por surco. Se llama en mount y cada vez que
  // cambia el stepper "surcos". Conservamos valores ya cargados si el nuevo
  // count es mayor o igual al anterior.
  function rebuildSurcoInputs(mc) {
    var box = mc.querySelector('[data-cal="surcoList"]');
    if (!box) return;
    var count = parseInt((mc.querySelector('input[data-cal-f="surcos"]') || {}).value, 10) || 6;
    // Capturamos valores actuales para no perder lo que el operario ya escribió.
    var prev = {};
    box.querySelectorAll('input[data-cal-surco]').forEach(function (inp) {
      prev[inp.getAttribute('data-cal-surco')] = inp.value;
    });
    var html = '';
    for (var i = 0; i < count; i++) {
      var val = prev[String(i)] != null ? prev[String(i)] : '';
      html += '<div class="field">' +
        '<label>Surco ' + (i + 1) + ' (g / semillas / L)</label>' +
        '<input type="number" data-cal-surco="' + i + '" min="0" step="0.1" value="' + val + '">' +
      '</div>';
    }
    box.innerHTML = html;
  }

  function updateCalibrarPulses() {
    document.querySelectorAll('#calList .card[data-uid]').forEach(function (card) {
      var uid = card.getAttribute('data-uid');
      var live = state.liveByUid[uid];
      if (!live || !live.motors) return;
      card.querySelectorAll('.motor-cfg').forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = null;
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id | 0) === mi) { m = live.motors[k]; break; }
        if (!m) return;

        var pul   = m.pulsos || 0;
        var pwmA  = m.pwm || 0;
        var ppr   = parseInt((mc.querySelector('input[data-cal-f="ppr"]') || {}).value, 10) || 1;

        var pEl  = mc.querySelector('[data-cal="pulsos"]');     if (pEl)  pEl.textContent  = pul.toLocaleString();
        var pwEl = mc.querySelector('[data-cal="pwmCur"]');     if (pwEl) pwEl.textContent = pwmA + ' / 4095';

        var st = calState[uid] && calState[uid][mi];
        var dEl  = mc.querySelector('[data-cal="delta"]');
        var vEl  = mc.querySelector('[data-cal="vueltasReales"]');
        if (st && st.startPulsos != null) {
          var delta = pul - st.startPulsos;
          if (dEl) dEl.textContent = delta.toLocaleString();
          if (vEl) vEl.textContent = (ppr > 0 ? (delta / ppr).toFixed(2) : '—');
          // El firmware gira hasta st.meta pulsos y frena solo. La telemetría no
          // expone un flag de calibración, así que detectamos la meta en el PC:
          // grabamos endPulsos cuando el Δ de pulsos alcanza el objetivo comandado
          // (vueltas*ppr, el mismo valor que se le mandó al nodo en 'start').
          if (st.meta && st.endPulsos == null && delta >= st.meta) {
            st.endPulsos = pul;
            var msgEl = mc.querySelector('span[data-cal-msg="' + mi + '"]');
            if (msgEl) { msgEl.textContent = '✓ Meta alcanzada — pesá los surcos y apretá Calcular'; msgEl.className = 'send-msg ok'; }
          }
        } else {
          if (dEl) dEl.textContent = '—';
          if (vEl) vEl.textContent = '—';
        }
      });
    });
  }

  document.getElementById('tabCalibrar').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-cal-act]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(btn.getAttribute('data-mi'), 10);
    var act = btn.getAttribute('data-cal-act');
    var msgEl = mc.querySelector('span[data-cal-msg="' + mi + '"]');
    var resEl = mc.querySelector('span[data-cal-result="' + mi + '"]');

    if (!calState[uid]) calState[uid] = {};
    if (!calState[uid][mi]) calState[uid][mi] = {};
    var st = calState[uid][mi];

    var readInt = function (f, dflt) {
      var v = parseInt((mc.querySelector('input[data-cal-f="' + f + '"]') || {}).value, 10);
      return isNaN(v) ? dflt : v;
    };

    if (act === 'start') {
      var vueltas = readInt('vueltas', 10);
      var ppr     = readInt('ppr', 20);
      var pwm     = readInt('pwm', 2000);
      var meta    = vueltas * ppr;
      if (meta <= 0) { msgEl.textContent = '✕ vueltas/PPR inválidos'; msgEl.className = 'send-msg err'; return; }

      // Snapshot del contador actual antes de arrancar — Δ se calcula contra esto.
      var live = state.liveByUid[uid]; var pulNow = 0;
      if (live && live.motors) {
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id | 0) === mi)
            pulNow = live.motors[k].pulsos || 0;
      }
      st.startPulsos = pulNow; st.endPulsos = null;
      st.vueltas = vueltas; st.ppr = ppr; st.pwm = pwm; st.meta = meta;

      msgEl.textContent = '… girando hasta ' + meta + ' pulsos (' + vueltas + ' vueltas)';
      msgEl.className = 'send-msg';

      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cmd: 'start', id: mi, pulsos: meta, pwm: pwm })
        });
      } catch (e) {
        msgEl.textContent = '✕ no se pudo enviar MQTT'; msgEl.className = 'send-msg err';
      }
    } else if (act === 'stop') {
      // Stop manual: cierre del Δ con el pulso actual.
      var live2 = state.liveByUid[uid]; var pulEnd = 0;
      if (live2 && live2.motors) {
        for (var j = 0; j < live2.motors.length; j++)
          if ((live2.motors[j].id | 0) === mi)
            pulEnd = live2.motors[j].pulsos || 0;
      }
      st.endPulsos = pulEnd;
      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cmd: 'stop', id: mi })
        });
      } catch (e) {}
      msgEl.textContent = '✓ Detenido. Pesá los surcos y apretá Calcular.'; msgEl.className = 'send-msg ok';
      updateCalibrarPulses();
    } else if (act === 'reset') {
      // Limpia los inputs por surco y el estado — el operario reintenta desde cero.
      calState[uid][mi] = {};
      mc.querySelectorAll('input[data-cal-surco]').forEach(function (inp) { inp.value = ''; });
      var box = mc.querySelector('[data-cal="resultBox"]'); if (box) box.hidden = true;
      var applyBtn = mc.querySelector('button[data-cal-act="apply"]'); if (applyBtn) applyBtn.disabled = true;
      if (msgEl) { msgEl.textContent = ''; msgEl.className = 'send-msg'; }
      if (resEl) { resEl.textContent = ''; resEl.className = 'send-msg'; }
      var deltaEl  = mc.querySelector('[data-cal="delta"]');         if (deltaEl)  deltaEl.textContent  = '—';
      var vueltasEl= mc.querySelector('[data-cal="vueltasReales"]'); if (vueltasEl)vueltasEl.textContent= '—';
      // Stop por las dudas que el motor todavía esté girando.
      try {
        await fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cmd: 'stop', id: mi })
        });
      } catch (e) {}
    } else if (act === 'calc') {
      // Promediamos los surcos cargados (ignoramos los vacíos / 0).
      var suma = 0, count = 0;
      mc.querySelectorAll('input[data-cal-surco]').forEach(function (inp) {
        var v = parseFloat(inp.value);
        if (!isNaN(v) && v > 0) { suma += v; count++; }
      });
      if (count === 0) { resEl.textContent = '✕ ingresá al menos un surco con valor > 0'; resEl.className = 'send-msg err'; return; }

      // Pulsos totales = Δ entre start y end. Si endPulsos no llegó (operario no
      // detuvo o no esperó la meta), usamos el último pulso conocido.
      var pulsosTot = 0;
      if (st.startPulsos != null) {
        var endP = st.endPulsos;
        if (endP == null) {
          // Tomá el pulso actual del live.
          var liveC = state.liveByUid[uid];
          if (liveC && liveC.motors) {
            for (var kk = 0; kk < liveC.motors.length; kk++)
              if ((liveC.motors[kk].id | 0) === mi)
                endP = liveC.motors[kk].pulsos || 0;
          }
        }
        if (endP != null) pulsosTot = endP - st.startPulsos;
      }
      if (pulsosTot <= 0) {
        resEl.textContent = '✕ no hay pulsos contados. Apretá Iniciar primero.';
        resEl.className = 'send-msg err'; return;
      }

      var promedio = suma / count;
      var motorCal = findMotor(uid, mi);
      var esSemCal = motorCal && motorCal.unidad_dosis === 'sem_m';
      var pprC = readInt('ppr', (motorCal && motorCal.dientes_engranaje) || 20);

      var promEl   = mc.querySelector('[data-cal="prom"]');
      var uppEl    = mc.querySelector('[data-cal="upp"]');
      var newcalEl = mc.querySelector('[data-cal="newcal"]');
      var box2 = mc.querySelector('[data-cal="resultBox"]'); if (box2) box2.hidden = false;
      var applyBtn2 = mc.querySelector('button[data-cal-act="apply"]'); if (applyBtn2) applyBtn2.disabled = false;

      if (esSemCal) {
        // Semilla (sem/m): el motor giró pulsosTot pulsos = vueltasReales vueltas.
        // promedio = semillas contadas por surco. semillas/vuelta = promedio / vueltas.
        var vueltasReales = pprC > 0 ? (pulsosTot / pprC) : 0;
        if (vueltasReales <= 0) { resEl.textContent = '✕ PPR inválido para calcular sem/vuelta'; resEl.className = 'send-msg err'; return; }
        var semVuelta = promedio / vueltasReales;
        var semPorPulso = semVuelta / pprC;
        if (promEl)   promEl.textContent   = promedio.toFixed(1) + ' sem (' + count + ' surcos)';
        if (uppEl)    uppEl.textContent    = semPorPulso.toFixed(4) + ' sem/pulso';
        if (newcalEl) newcalEl.textContent = semVuelta.toFixed(2) + ' sem/vuelta';
        st.semVueltaCalc = semVuelta; st.meterCalCalc = null;
        resEl.textContent = '✓ ' + semVuelta.toFixed(2) + ' sem/vuelta'; resEl.className = 'send-msg ok';
      } else {
        // Masa (kg/ha): meter_cal = pulsos por unidad (gramos) → lo que el bridge multiplica.
        var unidadesPorPulso = promedio / pulsosTot;
        var meterCal = pulsosTot / promedio;
        if (promEl)   promEl.textContent   = promedio.toFixed(2) + ' (' + count + ' surcos)';
        if (uppEl)    uppEl.textContent    = unidadesPorPulso.toFixed(4) + ' u/pulso';
        if (newcalEl) newcalEl.textContent = meterCal.toFixed(4);
        st.meterCalCalc = meterCal; st.semVueltaCalc = null;
        resEl.textContent = '✓ MeterCal = ' + meterCal.toFixed(4); resEl.className = 'send-msg ok';
      }
    } else if (act === 'apply') {
      var esSemApply = st && st.semVueltaCalc != null;
      var newCal = esSemApply ? st.semVueltaCalc : (st && st.meterCalCalc);
      if (!newCal || newCal <= 0) { resEl.textContent = '✕ apretá Calcular primero'; resEl.className = 'send-msg err'; return; }
      var nIdx = -1;
      for (var i = 0; i < state.motoresCfg.nodos.length; i++)
        if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
      if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
        var motorApply = state.motoresCfg.nodos[nIdx].motores[mi];
        if (esSemApply) {
          motorApply.semillas_vuelta = Math.round(newCal * 100) / 100;
        } else {
          motorApply.meter_cal = Math.round(newCal * 10000) / 10000;
        }
        // PPR también lo persistimos por si el operario lo ajustó.
        var pprCfg = readInt('ppr', 0);
        if (pprCfg > 0) motorApply.dientes_engranaje = pprCfg;
        await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        var res = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
        var data = await res.json();
        var okLbl = esSemApply ? (newCal.toFixed(2) + ' sem/vuelta') : ('MeterCal=' + newCal.toFixed(4));
        resEl.textContent = data.ok ? ('✓ ' + okLbl + ' guardado y enviado') : '✕ guardado pero MQTT falló';
        resEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
      }
    }
  });

  // ============================================================================
  // PRUEBA — girar X pulsos + buscador de PWM mínimo (rampa)
  // ============================================================================
  //
  // 1) Girar X pulsos: publica agp/quantix/{uid}/cal con
  //    {cmd:"start", id, pulsos, pwm}. Firmware corre el motor a PWM fijo
  //    hasta acumular `pulsos`. Útil para probar dosificación o sensor.
  //
  // 2) PWM mínimo: rampa client-side. Empezamos en pwm=0, sumamos `paso`
  //    cada `interval` ms, leemos pps_real del feed live. El primer PWM
  //    que produce pps_real >= umbral_hz es el pwm_min. Se guarda en
  //    motores.json y se reenvía la config al ESP32.

  var pruebaState = {};   // uid -> { mi: { rampActive, rampPwm, rampPaso, rampHzMin, rampTimer } }

  function renderPrueba() {
    var listEl = $('prList');
    var emptyEl = $('prEmpty');
    var nodos = (state.motoresCfg.nodos || []).filter(function (n) { return n.uid; });
    if (nodos.length === 0) { listEl.innerHTML = ''; emptyEl.style.display = ''; return; }
    emptyEl.style.display = 'none';

    var html = '';
    for (var i = 0; i < nodos.length; i++) {
      var n = nodos[i];
      html += '<div class="card" style="margin-bottom: var(--agp-sp-4)" data-uid="' + escapeHtml(n.uid) + '">' +
        '<h3 style="margin-top:0">' + escapeHtml(n.nombre || 'Nodo') +
        ' <span style="font-family: var(--agp-font-mono); color: var(--agp-text-muted); font-size: var(--agp-fs-sm); font-weight: normal">' + escapeHtml(n.uid) + '</span></h3>' +
        '<div class="live-tune-grid">' +
          pruebaCard(n, 0) +
          pruebaCard(n, 1) +
        '</div>' +
      '</div>';
    }
    listEl.innerHTML = html;
  }

  function pruebaCard(n, mi) {
    var m = (n.motores && n.motores[mi]) || defaultMotor();
    return '<div class="motor-cfg ' + (mi === 0 ? '' : 'm1') + '" data-mi="' + mi + '">' +
      '<h4>M' + mi + ' — ' + escapeHtml(m.nombre || 'Motor') + '</h4>' +

      '<div class="kv" style="margin-top:0">' +
        '<div class="k">rpm</div><div class="v" data-pr="rpm">—</div>' +
        '<div class="k">PWM actual</div><div class="v" data-pr="pwm">—</div>' +
        '<div class="k">Pulsos</div><div class="v" data-pr="pulsos">—</div>' +
        '<div class="k">PWM min cfg</div><div class="v" data-pr="pwm_min_cfg">' + (m.pwm_min || 0) + '</div>' +
      '</div>' +

      '<h4 style="margin-top: var(--agp-sp-3)">Girar X pulsos</h4>' +
      '<div class="fld-grid">' +
        '<div class="field"><label>Pulsos meta</label>' +
          '<input type="number" data-pr-f="pulsos" min="1" step="1" value="500"></div>' +
        '<div class="field"><label>PWM</label>' +
          '<input type="number" data-pr-f="pwm" min="0" max="4095" step="50" value="2000"></div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-pr-act="spin-start" data-mi="' + mi + '">▶ Girar</button>' +
        '<button class="btn" data-pr-act="spin-stop" data-mi="' + mi + '">■ Detener</button>' +
        '<span class="send-msg" data-pr-msg="spin-' + mi + '"></span>' +
      '</div>' +

      '<h4 style="margin-top: var(--agp-sp-4)">Buscar PWM mínimo (rampa)</h4>' +
      '<div class="fld-grid">' +
        '<div class="field"><label>Paso PWM</label>' +
          '<input type="number" data-pr-f="step" min="10" max="500" step="10" value="50"></div>' +
        '<div class="field"><label>Intervalo (ms)</label>' +
          '<input type="number" data-pr-f="interval" min="200" max="3000" step="100" value="600"></div>' +
        '<div class="field"><label>Umbral Hz</label>' +
          '<input type="number" data-pr-f="hzmin" min="0.5" step="0.5" value="2"></div>' +
        '<div class="field"><label>PWM max</label>' +
          '<input type="number" data-pr-f="pwmmax" min="500" max="4095" step="50" value="4095"></div>' +
      '</div>' +
      '<div class="kv">' +
        '<div class="k">Rampa estado</div><div class="v" data-pr="ramp-state">idle</div>' +
        '<div class="k">PWM rampa</div><div class="v" data-pr="ramp-pwm">—</div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-pr-act="ramp-start" data-mi="' + mi + '">▶ Buscar PWM min</button>' +
        '<button class="btn" data-pr-act="ramp-stop" data-mi="' + mi + '">■ Cancelar</button>' +
        '<span class="send-msg" data-pr-msg="ramp-' + mi + '"></span>' +
      '</div>' +

      // Calibración avanzada: lo que antes vivía en la tab Motores. Se edita
      // acá porque son parámetros que se ajustan a fuerza de prueba/ramp,
      // no en seteo inicial del motor.
      '<h4 style="margin-top: var(--agp-sp-4)">Calibración avanzada</h4>' +
      '<div class="fld-grid">' +
        '<div class="field"><label>PWM min</label>' +
          '<input type="number" data-cal-f="pwm_min" min="0" max="4095" step="10" value="' + (m.pwm_min || 0) + '"></div>' +
        '<div class="field"><label>PWM max</label>' +
          '<input type="number" data-cal-f="pwm_max" min="0" max="4095" step="10" value="' + (m.pwm_max || 4095) + '"></div>' +
        '<div class="field"><label>Max Hz (FF)</label>' +
          '<input type="number" data-cal-f="max_hz" min="0" step="1" value="' + (m.max_hz || 0) + '"></div>' +
        '<div class="field"><label>FF gain</label>' +
          '<input type="number" data-cal-f="ff_gain" min="0" step="0.05" value="' + (m.ff_gain != null ? m.ff_gain : 1.0) + '"></div>' +
        '<div class="field"><label>Alpha</label>' +
          '<input type="number" data-cal-f="alpha" min="0" max="1" step="0.05" value="' + (m.alpha != null ? m.alpha : 0.4) + '"></div>' +
        '<div class="field"><label>PID time (ms)</label>' +
          '<input type="number" data-cal-f="pid_time" min="10" step="5" value="' + (m.pid_time || 50) + '"></div>' +
        '<div class="field"><label>Slew/s</label>' +
          '<input type="number" data-cal-f="slew_rate_per_sec" min="0" step="100" value="' + (m.slew_rate_per_sec || 0) + '"></div>' +
        '<div class="field"><label>Rampa dosis (Hz/s)</label>' +
          '<input type="number" data-cal-f="target_slew_hz_per_sec" min="0" step="25" value="' + (m.target_slew_hz_per_sec || 300) + '"></div>' +
      '</div>' +
      '<div class="btn-row">' +
        '<button class="btn primary" data-pr-act="cal-apply" data-mi="' + mi + '">Aplicar calibración</button>' +
        '<span class="send-msg" data-pr-msg="cal-' + mi + '"></span>' +
      '</div>' +

    '</div>';
  }

  function updatePruebaLive() {
    document.querySelectorAll('#prList .card[data-uid]').forEach(function (card) {
      var uid = card.getAttribute('data-uid');
      var live = state.liveByUid[uid];
      if (!live || !live.motors) return;
      card.querySelectorAll('.motor-cfg').forEach(function (mc) {
        var mi = parseInt(mc.getAttribute('data-mi'), 10);
        var m = null;
        for (var k = 0; k < live.motors.length; k++)
          if ((live.motors[k].id | 0) === mi) { m = live.motors[k]; break; }
        if (!m) return;
        var pps = m.pps_real || 0;
        var pwm = m.pwm || 0;
        var pul = m.pulsos || 0;
        var cfgP = findMotor(uid, mi);
        var p1 = mc.querySelector('[data-pr="rpm"]'); if (p1) p1.textContent = ppsToRpm(cfgP, pps).toFixed(0) + ' rpm';
        var p2 = mc.querySelector('[data-pr="pwm"]'); if (p2) p2.textContent = pwm;
        var p3 = mc.querySelector('[data-pr="pulsos"]'); if (p3) p3.textContent = pul.toLocaleString();
      });
    });
  }

  async function sendTest(uid, mi, pwm) {
    var payload = pwm > 0
      ? JSON.stringify({ cmd: 'start', id: mi, pwm: pwm })
      : JSON.stringify({ cmd: 'stop',  id: mi, pwm: 0 });
    return fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=test&retain=false', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload
    });
  }

  async function sendCal(uid, mi, pulsos, pwm) {
    var payload = pulsos > 0
      ? JSON.stringify({ cmd: 'start', id: mi, pulsos: pulsos, pwm: pwm })
      : JSON.stringify({ cmd: 'stop',  id: mi });
    return fetch('/api/quantix/' + encodeURIComponent(uid) + '/cmd?verb=cal&retain=false', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload
    });
  }

  // Live del motor pedido al server en el momento, sin pasar por la caché
  // compartida. Para MEDIR (Max Hz, rampas) hay que usar este: la caché la
  // refresca el loop de polling y muestrearla más rápido que eso devuelve el
  // mismo valor repetido.
  async function fetchLiveMotor(uid, mi) {
    try {
      var res = await fetch('/api/quantix/live', { cache: 'no-store' });
      var data = await res.json();
      var nodos = (data && data.nodos) || [];
      for (var i = 0; i < nodos.length; i++) {
        if (nodos[i].uid !== uid) continue;
        var ms = nodos[i].motors_live || [];
        for (var k = 0; k < ms.length; k++)
          if ((ms[k].id | 0) === mi) return ms[k];
      }
    } catch (e) {}
    return null;
  }

  function getLiveMotor(uid, mi) {
    var live = state.liveByUid[uid];
    if (!live || !live.motors) return null;
    for (var k = 0; k < live.motors.length; k++)
      if ((live.motors[k].id | 0) === mi) return live.motors[k];
    return null;
  }

  function stopRamp(uid, mi, reason) {
    var st = pruebaState[uid] && pruebaState[uid][mi];
    if (!st) return;
    if (st.rampTimer) { clearInterval(st.rampTimer); st.rampTimer = null; }
    st.rampActive = false;
    // Detener motor.
    sendTest(uid, mi, 0).catch(function () {});
    var card = document.querySelector('#prList .card[data-uid="' + uid + '"]');
    if (card) {
      var mc = card.querySelector('.motor-cfg[data-mi="' + mi + '"]');
      if (mc) {
        var stEl = mc.querySelector('[data-pr="ramp-state"]');
        if (stEl) stEl.textContent = reason || 'idle';
      }
    }
  }

  async function startRamp(uid, mi, params) {
    if (!pruebaState[uid]) pruebaState[uid] = {};
    if (!pruebaState[uid][mi]) pruebaState[uid][mi] = {};
    var st = pruebaState[uid][mi];
    if (st.rampActive) return;

    var card = document.querySelector('#prList .card[data-uid="' + uid + '"]');
    var mc = card.querySelector('.motor-cfg[data-mi="' + mi + '"]');
    var msgEl = mc.querySelector('span[data-pr-msg="ramp-' + mi + '"]');
    var stEl = mc.querySelector('[data-pr="ramp-state"]');
    var pwmEl = mc.querySelector('[data-pr="ramp-pwm"]');
    msgEl.className = 'send-msg'; msgEl.textContent = '… midiendo';

    st.rampActive = true;
    st.rampPwm = 0;
    st.rampPaso = params.step;
    st.rampHzMin = params.hzmin;
    st.rampPwmMax = params.pwmmax;
    if (stEl) stEl.textContent = 'rampando';

    // Settle delay antes de cada paso (sin parar el motor entre pasos —
    // queremos rampa contínua, no escalonada con paradas).
    st.rampTimer = setInterval(async function () {
      if (!st.rampActive) return;
      st.rampPwm += st.rampPaso;
      if (st.rampPwm > st.rampPwmMax) {
        msgEl.textContent = '✕ No detectó pulsos hasta PWM ' + st.rampPwmMax;
        msgEl.className = 'send-msg err';
        stopRamp(uid, mi, 'sin pulsos');
        return;
      }
      if (pwmEl) pwmEl.textContent = st.rampPwm;
      try { await sendTest(uid, mi, st.rampPwm); } catch (e) {}
      // Leer Hz tras settle.
      setTimeout(async function () {
        if (!st.rampActive) return;
        var m = getLiveMotor(uid, mi);
        var pps = m ? (m.pps_real || 0) : 0;
        if (pps >= st.rampHzMin) {
          // Encontrado.
          var found = st.rampPwm;
          stopRamp(uid, mi, 'encontrado');
          msgEl.textContent = '✓ PWM mínimo = ' + found + ' (Hz=' + pps.toFixed(1) + ')';
          msgEl.className = 'send-msg ok';
          // Persistir.
          var nIdx = -1;
          for (var i = 0; i < state.motoresCfg.nodos.length; i++)
            if (state.motoresCfg.nodos[i].uid === uid) { nIdx = i; break; }
          if (nIdx >= 0 && state.motoresCfg.nodos[nIdx].motores[mi]) {
            state.motoresCfg.nodos[nIdx].motores[mi].pwm_min = found;
            try {
              await fetch('/api/quantix/motores', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(state.motoresCfg)
              });
              await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
            } catch (e) {}
            var cfgEl = mc.querySelector('[data-pr="pwm_min_cfg"]');
            if (cfgEl) cfgEl.textContent = found;
          }
        }
      }, Math.max(150, params.interval - 50));
    }, params.interval);
  }

  document.getElementById('tabPrueba').addEventListener('click', async function (ev) {
    var btn = ev.target.closest('button[data-pr-act]');
    if (!btn) return;
    var card = btn.closest('.card[data-uid]');
    var mc = btn.closest('.motor-cfg');
    var uid = card.getAttribute('data-uid');
    var mi = parseInt(btn.getAttribute('data-mi'), 10);
    var act = btn.getAttribute('data-pr-act');

    if (act === 'spin-start') {
      var pulsos = parseInt(mc.querySelector('input[data-pr-f="pulsos"]').value, 10);
      var pwm = parseInt(mc.querySelector('input[data-pr-f="pwm"]').value, 10);
      var msgEl = mc.querySelector('span[data-pr-msg="spin-' + mi + '"]');
      msgEl.className = 'send-msg';
      if (!(pulsos > 0) || !(pwm > 0)) { msgEl.textContent = '✕ valores inválidos'; msgEl.className = 'send-msg err'; return; }
      msgEl.textContent = '… girando ' + pulsos + ' pulsos a PWM ' + pwm;
      try {
        var res = await sendCal(uid, mi, pulsos, pwm);
        var data = await res.json();
        msgEl.textContent = data.ok ? '✓ enviado' : '✕ ' + (data.error || 'fallo');
        msgEl.className = 'send-msg ' + (data.ok ? 'ok' : 'err');
      } catch (e) {
        msgEl.textContent = '✕ ' + e.message; msgEl.className = 'send-msg err';
      }
    } else if (act === 'spin-stop') {
      var msgEl2 = mc.querySelector('span[data-pr-msg="spin-' + mi + '"]');
      msgEl2.className = 'send-msg';
      try { await sendCal(uid, mi, 0, 0); msgEl2.textContent = '✓ detenido'; msgEl2.className = 'send-msg ok'; }
      catch (e) { msgEl2.textContent = '✕ ' + e.message; msgEl2.className = 'send-msg err'; }
    } else if (act === 'ramp-start') {
      var step = parseInt(mc.querySelector('input[data-pr-f="step"]').value, 10) || 50;
      var interval = parseInt(mc.querySelector('input[data-pr-f="interval"]').value, 10) || 600;
      var hzmin = parseFloat(mc.querySelector('input[data-pr-f="hzmin"]').value) || 2;
      var pwmmax = parseInt(mc.querySelector('input[data-pr-f="pwmmax"]').value, 10) || 4095;
      startRamp(uid, mi, { step: step, interval: interval, hzmin: hzmin, pwmmax: pwmmax });
    } else if (act === 'ramp-stop') {
      stopRamp(uid, mi, 'cancelado');
      var msgEl3 = mc.querySelector('span[data-pr-msg="ramp-' + mi + '"]');
      msgEl3.className = 'send-msg'; msgEl3.textContent = '■ cancelado';
    } else if (act === 'cal-apply') {
      // Persistir calibración avanzada (PWM min/max, max_hz, ff_gain, alpha,
      // pid_time, slew) y publicar la config completa por MQTT.
      var msgCal = mc.querySelector('span[data-pr-msg="cal-' + mi + '"]');
      msgCal.textContent = '… enviando'; msgCal.className = 'send-msg';
      var nIdx2 = -1;
      for (var j = 0; j < state.motoresCfg.nodos.length; j++) {
        if (state.motoresCfg.nodos[j].uid === uid) { nIdx2 = j; break; }
      }
      if (nIdx2 < 0 || !state.motoresCfg.nodos[nIdx2].motores[mi]) {
        msgCal.textContent = '✕ motor no encontrado'; msgCal.className = 'send-msg err'; return;
      }
      var mref2 = state.motoresCfg.nodos[nIdx2].motores[mi];
      mref2.pwm_min = parseInt(mc.querySelector('input[data-cal-f="pwm_min"]').value, 10) || 0;
      mref2.pwm_max = parseInt(mc.querySelector('input[data-cal-f="pwm_max"]').value, 10) || 4095;
      mref2.max_hz = parseFloat(mc.querySelector('input[data-cal-f="max_hz"]').value) || 0;
      mref2.ff_gain = parseFloat(mc.querySelector('input[data-cal-f="ff_gain"]').value);
      if (isNaN(mref2.ff_gain)) mref2.ff_gain = 1.0;
      mref2.alpha = parseFloat(mc.querySelector('input[data-cal-f="alpha"]').value);
      if (isNaN(mref2.alpha)) mref2.alpha = 0.4;
      mref2.pid_time = parseInt(mc.querySelector('input[data-cal-f="pid_time"]').value, 10) || 50;
      mref2.slew_rate_per_sec = parseInt(mc.querySelector('input[data-cal-f="slew_rate_per_sec"]').value, 10) || 0;
      mref2.target_slew_hz_per_sec = parseInt(mc.querySelector('input[data-cal-f="target_slew_hz_per_sec"]').value, 10) || 300;
      try {
        var pr1 = await fetch('/api/quantix/motores', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(state.motoresCfg)
        });
        var d1 = await pr1.json();
        if (!d1.ok) { msgCal.textContent = '✕ ' + (d1.error || 'guardar falló'); msgCal.className = 'send-msg err'; return; }
        var pr2 = await fetch('/api/quantix/' + encodeURIComponent(uid) + '/send', { method: 'POST' });
        var d2 = await pr2.json();
        msgCal.textContent = d2.ok ? '✓ aplicado' : '✕ ' + (d2.error || 'send falló');
        msgCal.className = 'send-msg ' + (d2.ok ? 'ok' : 'err');
      } catch (e) {
        msgCal.textContent = '✕ ' + e.message; msgCal.className = 'send-msg err';
      }
    }
  });

  // ============================================================================
  // SHAPE — upload de .shp/.shx/.dbf y carga en PilotX vía POST /api/aog/shape
  // ============================================================================

  var SHAPE_REQ = ['.shp', '.shx', '.dbf'];
  var SHAPE_OPT = ['.prj', '.cpg'];
  var shapeSelected = []; // [{name, ext, bytes (Uint8Array)}]

  function $sf(id) { return document.getElementById(id); }

  function shapeExtOf(name) {
    var i = name.lastIndexOf('.');
    return i < 0 ? '' : name.substring(i).toLowerCase();
  }

  function fmtBytes(n) {
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(2) + ' MB';
  }

  function shapeRenderFileList() {
    var box = $sf('shapeFileList');
    if (!box) return;
    if (shapeSelected.length === 0) { box.innerHTML = ''; updateShapeUploadEnabled(); return; }

    // Verificar requeridos
    var have = {};
    shapeSelected.forEach(function (f) { have[f.ext] = true; });

    var html = shapeSelected.map(function (f) {
      var isReq = SHAPE_REQ.indexOf(f.ext) >= 0;
      return '<div class="shape-file-row">' +
        '<span class="ext' + (isReq ? '' : ' opt') + '">' + f.ext + '</span>' +
        '<span class="nm">' + f.name + '</span>' +
        '<span class="sz">' + fmtBytes(f.bytes.length) + '</span>' +
        '</div>';
    }).join('');

    // Chips de estado de requeridos
    var chips = SHAPE_REQ.map(function (ext) {
      var cls = have[ext] ? 'ok' : 'missing';
      return '<span class="chip ' + cls + '">' + ext + (have[ext] ? ' ✓' : ' falta') + '</span>';
    }).join('') + SHAPE_OPT.map(function (ext) {
      var cls = have[ext] ? 'ok' : '';
      return '<span class="chip ' + cls + '">' + ext + (have[ext] ? ' ✓' : '') + '</span>';
    }).join('');

    box.innerHTML = html + '<div class="shape-required-grid">' + chips + '</div>';
    updateShapeUploadEnabled();
  }

  function updateShapeUploadEnabled() {
    var btn = $sf('btnShapeUpload');
    if (!btn) return;
    var have = {};
    shapeSelected.forEach(function (f) { have[f.ext] = true; });
    btn.disabled = !(have['.shp'] && have['.shx'] && have['.dbf']);
  }

  function shapeAddFiles(fileList) {
    var arr = Array.prototype.slice.call(fileList);
    var promises = arr.map(function (f) {
      return new Promise(function (resolve) {
        var ext = shapeExtOf(f.name);
        if (SHAPE_REQ.indexOf(ext) < 0 && SHAPE_OPT.indexOf(ext) < 0) {
          // Ignorar extensiones no aceptadas (no rompemos el flujo)
          resolve(null); return;
        }
        var rd = new FileReader();
        rd.onload = function () {
          resolve({ name: f.name, ext: ext, bytes: new Uint8Array(rd.result) });
        };
        rd.onerror = function () { resolve(null); };
        rd.readAsArrayBuffer(f);
      });
    });
    Promise.all(promises).then(function (results) {
      results.forEach(function (item) {
        if (!item) return;
        // Reemplazo por extensión: si ya hay .shp y se elige otra .shp nueva, gana la nueva.
        shapeSelected = shapeSelected.filter(function (x) { return x.ext !== item.ext; });
        shapeSelected.push(item);
      });
      shapeRenderFileList();
    });
  }

  function shapeBytesToBase64(uint8) {
    var CHUNK = 0x8000;
    var s = '';
    for (var i = 0; i < uint8.length; i += CHUNK) {
      s += String.fromCharCode.apply(null, uint8.subarray(i, i + CHUNK));
    }
    return btoa(s);
  }

  async function refreshShapeActive() {
    var box = $sf('shapeActive');
    if (!box) return;
    try {
      var r = await fetch('/api/aog/shape-fields', { cache: 'no-store' });
      var d = await r.json();
      // /api/aog/shape-fields serializa en snake_case: sourceToken → source_token
      if (!d || !d.ok || !d.source_token) {
        box.innerHTML = '<div class="subtitle">No hay shapefile activo en este lote.</div>';
        return;
      }
      var fields = Array.isArray(d.fields) ? d.fields : [];
      var fieldsHtml = fields.length === 0
        ? '<span style="color:var(--agp-text-muted)">— sin columnas DBF —</span>'
        : fields.map(function (f) {
            var nm = (typeof f === 'string') ? f : (f && f.name);
            return '<span class="chip ok" style="margin:2px">' + escapeHtml(nm || '') + '</span>';
          }).join('');
      box.innerHTML =
        '<div style="display:grid;grid-template-columns:auto 1fr;gap:var(--agp-sp-2) var(--agp-sp-3);align-items:baseline">' +
          '<div class="lbl" style="font-size:var(--agp-fs-xs);color:var(--agp-text-muted);text-transform:uppercase">Archivo</div>' +
          '<div style="font-family:var(--agp-font-mono)">' + escapeHtml(d.source_token) + '</div>' +
          '<div class="lbl" style="font-size:var(--agp-fs-xs);color:var(--agp-text-muted);text-transform:uppercase">Columnas DBF</div>' +
          '<div class="shape-required-grid" style="margin:0">' + fieldsHtml + '</div>' +
        '</div>';
    } catch (e) {
      box.innerHTML = '<div class="subtitle">Error consultando PilotX: ' + e.message + '</div>';
    }
  }

  function shapeSetMsg(state, text) {
    var el = $sf('shapeMsg');
    if (!el) return;
    el.className = 'send-msg ' + (state || '');
    el.textContent = text || '';
  }

  async function shapeUpload() {
    if (shapeSelected.length === 0) return;
    shapeSetMsg('', 'Subiendo y cargando en PilotX…');
    var btn = $sf('btnShapeUpload');
    if (btn) btn.disabled = true;
    try {
      var payload = {
        files: shapeSelected.map(function (f) {
          return { name: f.name, b64: shapeBytesToBase64(f.bytes) };
        })
      };
      var r = await fetch('/api/aog/shape', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      var d = await r.json();
      if (d && d.ok) {
        shapeSetMsg('ok', '✓ Cargado · ' + (d.polygon_count || 0) + ' polígonos');
        shapeSelected = [];
        shapeRenderFileList();
        var inp = $sf('shapeFiles'); if (inp) inp.value = '';
        // Invalidar cache de campos para que la tab Motores reciba las nuevas columnas
        state.shapeSource = '';
        await loadShapeFields();
        refreshShapeActive();
      } else {
        shapeSetMsg('err', '✕ ' + (d && d.error ? d.error : 'Error desconocido'));
      }
    } catch (e) {
      shapeSetMsg('err', '✕ ' + e.message);
    } finally {
      updateShapeUploadEnabled();
    }
  }

  async function shapeRemove() {
    if (!await AgpModal.confirm('Quitar capa', '¿Quitar la capa de shapefile activa del lote?')) return;
    shapeSetMsg('', 'Quitando…');
    try {
      var r = await fetch('/api/aog/shape', { method: 'DELETE' });
      var d = await r.json();
      if (d && d.ok) {
        shapeSetMsg('ok', '✓ Capa quitada');
        state.shapeSource = '';
        await loadShapeFields();
        refreshShapeActive();
      } else {
        shapeSetMsg('err', '✕ ' + (d && d.error ? d.error : 'no se pudo quitar'));
      }
    } catch (e) {
      shapeSetMsg('err', '✕ ' + e.message);
    }
  }

  (function bindShapeUI() {
    var drop = $sf('shapeDrop');
    var inp  = $sf('shapeFiles');
    if (!drop || !inp) return;
    // Pantalla táctil: tap en la zona abre el file picker.
    // Filtramos el click que viene desde el propio <input> para no entrar en loop.
    drop.addEventListener('click', function (e) {
      if (e.target === inp) return;
      inp.click();
    });
    inp.addEventListener('change', function () { if (inp.files && inp.files.length) shapeAddFiles(inp.files); });
    ['dragenter', 'dragover'].forEach(function (ev) {
      drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.add('over'); });
    });
    ['dragleave', 'drop'].forEach(function (ev) {
      drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.remove('over'); });
    });
    drop.addEventListener('drop', function (e) {
      if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) {
        shapeAddFiles(e.dataTransfer.files);
      }
    });
    var bu = $sf('btnShapeUpload'); if (bu) bu.addEventListener('click', shapeUpload);
    var br = $sf('btnShapeRemove'); if (br) br.addEventListener('click', shapeRemove);
  })();

  // ============================================================================
  // Init
  // ============================================================================

  loadMotores();
  // Cargamos columnas DBF, secciones e implemento central en background.
  // implCentral alimenta syncTrenes() (cfg.trenes que espera el bridge) y el
  // mapeo surco→sección; sin esta carga, guardar dejaría cfg.trenes sin actualizar.
  loadShapeFields();
  loadAogSections();
  loadImplCentral();

  // Transporte live:
  //   · WS /ws/quantix → push en tiempo real (el server manda solo cuando cambia).
  //   · Fallback HTTP: si el WS no abre / se cae, polling adaptativo:
  //       tabs "live" (monitor/pid/calibrar/prueba) → 500ms
  //       tabs "config" (motores/shape) → 2000ms (solo pill de "X nodos")
  //   · El estado AOG (/api/aog/state para la tab Siembra) se sigue refrescando
  //     por polling aunque el WS esté abierto — no viaja por /ws/quantix.
  //   · Pestaña del WebView no visible → pausa total (WS cerrado + sin polls).
  var LIVE_TABS = { siembra: 1, motores: 1, pid: 1, calibrar: 1, prueba: 1 };
  var pollTimer = null;
  var liveWs = null;
  var liveWsOpen = false;
  var lastWsMsgTs = 0;

  function connectLiveWs() {
    if (liveWs || document.hidden || !('WebSocket' in window)) return;
    try {
      var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      var ws = new WebSocket(proto + '//' + location.host + '/ws/quantix');
      liveWs = ws;
      ws.onopen = function () { liveWsOpen = true; lastWsMsgTs = Date.now(); };
      ws.onmessage = function (ev) {
        lastWsMsgTs = Date.now();
        // El hub puede mandar frames de TEXTO (saludo) o BINARIOS (broadcast
        // con byte[]): en el browser los binarios llegan como Blob y
        // JSON.parse(Blob) revienta silencioso — la causa histórica de las
        // tabs congeladas en "—" con el overlay andando.
        if (typeof ev.data === 'string') {
          try { applyLive(JSON.parse(ev.data)); } catch (_) {}
        } else if (ev.data && typeof ev.data.text === 'function') {
          ev.data.text().then(function (t) {
            try { applyLive(JSON.parse(t)); } catch (_) {}
          }).catch(function () {});
        }
      };
      ws.onclose = ws.onerror = function () {
        if (liveWs === ws) { liveWs = null; liveWsOpen = false; }
        try { ws.close(); } catch (_) {}
        // Reintento en 5s (si la pestaña sigue visible). Mientras tanto,
        // el loop de polling cubre el live por HTTP.
        if (!document.hidden) setTimeout(connectLiveWs, 5000);
      };
    } catch (_) { liveWs = null; liveWsOpen = false; }
  }

  function schedulePoll() {
    if (pollTimer) clearTimeout(pollTimer);
    if (document.hidden) { pollTimer = null; return; }
    var period = LIVE_TABS[state.activeTab] ? 500 : 2000;
    pollTimer = setTimeout(async function () {
      // Con WS abierto Y FRESCO, el live viene por push; solo falta el estado
      // AOG que consume la tab Siembra. "Fresco" = mandó algo hace <2,5 s: el
      // hub broadcastea en serie y un cliente zombie (WebViews viejos) puede
      // trabar el push para todos — si el WS enmudece, el poll HTTP cubre.
      var wsFresco = liveWsOpen && (Date.now() - lastWsMsgTs) < 2500;
      try {
        if (!wsFresco) await pollLive();
        // La velocidad se refresca SIEMPRE, no solo en Siembra: sem/m, sem/ha y
        // kg/ha se calculan dividiendo por la velocidad, así que una velocidad
        // congelada da dosis inventadas en PID live, Calibración y Prueba. Con
        // el WS fresco esta era la única tab que la actualizaba: yendo a
        // 3,5 km/h con 5 km/h viejos en memoria, PID live mostraba 3,5 sem/m
        // donde el overlay (que lee la velocidad real) mostraba 4,9.
        await refreshAogLiveState();
      } catch (_) {}
      // WS abierto pero mudo >10 s → reconectar (el server pudo purgar mal).
      if (liveWsOpen && Date.now() - lastWsMsgTs > 10000 && liveWs) {
        try { liveWs.close(); } catch (_) {}
        liveWs = null; liveWsOpen = false;
        connectLiveWs();
      }
      schedulePoll();
    }, period);
  }

  (async function bootPoll() {
    connectLiveWs();
    // schedulePoll ANTES del primer poll: si ese fetch inicial se cuelga
    // (engine reiniciando, nodo reconectando) no puede matar el loop entero.
    schedulePoll();
    try { await pollLive(); } catch (_) {}
  })();
  // Latido de última línea: pase lo que pase con el WS o el scheduler, un
  // poll HTTP cada 2 s mantiene la UI viva. Solo actúa si el WS está mudo.
  setInterval(function () {
    if (!document.hidden && Date.now() - lastWsMsgTs > 2000) {
      try { pollLive().catch(function () {}); } catch (_) {}
    }
  }, 2000);
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) {
      if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
      if (liveWs) { try { liveWs.close(); } catch (_) {} liveWs = null; liveWsOpen = false; }
    } else {
      connectLiveWs();
      schedulePoll();
    }
  });
})();
