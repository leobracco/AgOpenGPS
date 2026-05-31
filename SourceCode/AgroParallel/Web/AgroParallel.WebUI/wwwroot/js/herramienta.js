// ============================================================================
// herramienta.js — CRUD del catálogo de IMPLEMENTOS del Hub.
//
// Esta página NO toca la config nativa de PilotX/AOG (Tool/Vehicle). Edita
// SOLO los archivos data/implementos/<slug>.json del Hub. Los productos
// X-* (VistaX, QuantiX, SectionX) leen del implemento ACTIVO via /api/implemento.
//
// Endpoints usados:
//   GET    /api/implementos                  → { activo, lista:[{slug,nombre}] }
//   GET    /api/implementos/{slug}           → { ok, slug, implemento }
//   PUT    /api/implementos/{slug}           → guarda
//   DELETE /api/implementos/{slug}           → borra (re-activa otro)
//   POST   /api/implementos/activo  {slug}   → cambia activo
//   POST   /api/implementos/nuevo   {nombre} → crea blank + activa
//   POST   /api/implementos/copiar  {from,nombre} → duplica + activa
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  // Estado en memoria.
  var state = {
    activo: '',           // slug del activo
    lista: [],            // [{slug, nombre, activo}]
    impl: null,           // ImplementoDto cargado
    dirty: false          // hay cambios sin guardar
  };

  // ---- helpers --------------------------------------------------------

  function pill(s, text) {
    var el = $('hStatus');
    el.className = 'pill ' + (s === 'ok' ? 'ok' : s === 'err' ? 'bad' : '');
    el.innerHTML = '<span class="dot"></span> ' + text;
  }
  function msg(s, text) {
    var el = $('msgTool');
    el.className = 'msg ' + (s || '');
    el.textContent = text || '';
  }
  function num(v, def) { return (typeof v === 'number' && isFinite(v)) ? v : def; }

  // Repaint debounced de la vista de sembradora — evita parpadeo cuando el
  // usuario tipea rápido en inputs (nombre, distancia, etc.).
  var _visTimer = 0;
  function scheduleVisualRepaint() {
    if (_visTimer) clearTimeout(_visTimer);
    _visTimer = setTimeout(function () {
      _visTimer = 0;
      if (window.SembradoraVisual && state.impl) {
        window.SembradoraVisual.render(state.impl, 'sembVis');
      }
    }, 80);
  }

  function markDirty() {
    state.dirty = true;
    msg('', 'Cambios sin guardar');
    scheduleVisualRepaint();
  }

  // ---- pintar form desde state.impl ----------------------------------

  function ensureImpl() {
    if (!state.impl) state.impl = {};
    var d = state.impl;
    if (!Array.isArray(d.trenes) || d.trenes.length === 0) {
      d.trenes = [{ id: 1, nombre: 'Tren único', distancia_m: 0 }];
    }
    if (!Array.isArray(d.surcos)) d.surcos = [];
    if (!Array.isArray(d.secciones)) d.secciones = [];
    if (typeof d.distancia_entre_surcos_m !== 'number' || !isFinite(d.distancia_entre_surcos_m)) {
      d.distancia_entre_surcos_m = 0.525;
    }
  }

  function paintAll() {
    ensureImpl();
    var d = state.impl;
    $('implNombreInp').value = d.nombre || '';
    // Tipo/Geometría/Look-ahead viven en PilotX. Acá solo manejamos secciones,
    // trenes, surcos y los overrides de look-ahead por sección.
    $('numSections').value = (d.secciones && d.secciones.length) || 1;
    updateSectionsVis();
    paintTrenes();
    paintSurcos();
    paintSeccionesX();
    scheduleVisualRepaint();
  }

  function updateSectionsVis() {
    var bar = $('secBar'); if (!bar) return;
    // El ancho total se administra en PilotX — acá usamos lo guardado en el DTO
    // solo para que la visualización refleje algo. Si no hay valor, dibujamos
    // bandas proporcionales sin etiqueta de ancho.
    var w = num(state.impl && state.impl.ancho_total_m, 0);
    var n = parseInt($('numSections').value, 10) || 0;
    while (bar.firstChild) bar.removeChild(bar.firstChild);
    if (n <= 0) return;
    var x0 = 80, x1 = 440, totalPx = x1 - x0;
    var step = totalPx / n;
    var NS = 'http://www.w3.org/2000/svg';
    for (var i = 0; i < n; i++) {
      var r = document.createElementNS(NS, 'rect');
      r.setAttribute('x', (x0 + i * step + 2).toFixed(1));
      r.setAttribute('y', '64');
      r.setAttribute('width', Math.max(0, step - 4).toFixed(1));
      r.setAttribute('height', '32');
      r.setAttribute('rx', '3');
      r.setAttribute('fill', 'var(--agp-accent)');
      r.setAttribute('opacity', i % 2 === 0 ? '0.85' : '0.55');
      bar.appendChild(r);
      var tx = document.createElementNS(NS, 'text');
      tx.setAttribute('x', (x0 + i * step + step / 2).toFixed(1));
      tx.setAttribute('y', '86');
      tx.setAttribute('text-anchor', 'middle');
      tx.setAttribute('class', 'lbl-txt');
      tx.setAttribute('fill', '#fff');
      tx.textContent = (i + 1);
      bar.appendChild(tx);
    }
    var leg = $('secLegend');
    if (leg) {
      if (w > 0) {
        var secW = w / n;
        leg.innerHTML =
          '<div><b>' + n + '</b> secciones · ancho total <b>' + w.toFixed(2) +
          ' m</b> · cada una ≈ <b>' + secW.toFixed(2) + ' m</b></div>';
      } else {
        leg.innerHTML =
          '<div><b>' + n + '</b> secciones · el ancho total se configura en PilotX</div>';
      }
    }
  }

  // ---- TRENES --------------------------------------------------------

  function paintTrenes() {
    ensureImpl();
    var tb = document.querySelector('#tblTrenes tbody'); if (!tb) return;
    while (tb.firstChild) tb.removeChild(tb.firstChild);
    state.impl.trenes.forEach(function (t, idx) {
      var tr = document.createElement('tr');
      tr.style.borderBottom = '1px solid var(--agp-border)';
      tr.innerHTML =
        '<td style="padding:6px">' + (t.id != null ? t.id : idx) + '</td>' +
        '<td style="padding:6px"><input type="text" data-tren-idx="' + idx + '" data-field="nombre" value="' + (t.nombre || '') + '"></td>' +
        '<td style="padding:6px"><input type="number" step="0.05" data-tren-idx="' + idx + '" data-field="distancia_m" value="' + (t.distancia_m || 0) + '"></td>' +
        '<td style="padding:6px">' + (state.impl.trenes.length > 1 ? '<button class="btn" data-del-tren="' + idx + '">Quitar</button>' : '') + '</td>';
      tb.appendChild(tr);
    });
    tb.querySelectorAll('input[data-tren-idx]').forEach(function (inp) {
      inp.addEventListener('change', function () {
        var i = parseInt(inp.getAttribute('data-tren-idx'), 10);
        var f = inp.getAttribute('data-field');
        if (f === 'nombre') state.impl.trenes[i].nombre = inp.value;
        else state.impl.trenes[i][f] = parseFloat(inp.value) || 0;
        markDirty();
      });
    });
    tb.querySelectorAll('button[data-del-tren]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var i = parseInt(btn.getAttribute('data-del-tren'), 10);
        var removed = state.impl.trenes[i];
        state.impl.trenes.splice(i, 1);
        state.impl.surcos.forEach(function (s) { if (s.tren_id === removed.id) s.tren_id = state.impl.trenes[0].id; });
        paintTrenes(); paintSurcos();
        markDirty();
      });
    });
  }

  // ---- SURCOS --------------------------------------------------------

  function paintSurcos() {
    ensureImpl();
    var nIn = $('implNumSurcos');
    var dIn = $('implDistSurcos');
    if (nIn) nIn.value = state.impl.numero_surcos || state.impl.surcos.length || 0;
    if (dIn) dIn.value = state.impl.distancia_entre_surcos_m;

    var tb = document.querySelector('#tblSurcos tbody'); if (!tb) return;
    while (tb.firstChild) tb.removeChild(tb.firstChild);

    var nSec = parseInt($('numSections').value, 10) || 1;
    state.impl.surcos.forEach(function (s, idx) {
      var tr = document.createElement('tr');
      tr.style.borderBottom = '1px solid var(--agp-border)';
      var trenOpts = state.impl.trenes.map(function (t) {
        var sel = (t.id === s.tren_id) ? ' selected' : '';
        return '<option value="' + t.id + '"' + sel + '>' + (t.nombre || ('Tren ' + t.id)) + '</option>';
      }).join('');
      var secOpts = '<option value="0"' + (s.seccion_pilotx === 0 ? ' selected' : '') + '>—</option>';
      for (var k = 1; k <= nSec; k++) {
        secOpts += '<option value="' + k + '"' + (s.seccion_pilotx === k ? ' selected' : '') + '>' + k + '</option>';
      }
      tr.innerHTML =
        '<td style="padding:6px">' + s.numero + '</td>' +
        '<td style="padding:6px"><select data-surco-idx="' + idx + '" data-field="tren_id">' + trenOpts + '</select></td>' +
        '<td style="padding:6px"><select data-surco-idx="' + idx + '" data-field="seccion_pilotx">' + secOpts + '</select></td>';
      tb.appendChild(tr);
    });
    tb.querySelectorAll('select[data-surco-idx]').forEach(function (sel) {
      sel.addEventListener('change', function () {
        var i = parseInt(sel.getAttribute('data-surco-idx'), 10);
        var f = sel.getAttribute('data-field');
        state.impl.surcos[i][f] = parseInt(sel.value, 10) || 0;
        markDirty();
      });
    });
  }

  function resizeSurcos(n) {
    ensureImpl();
    n = Math.max(0, Math.min(96, n | 0));
    while (state.impl.surcos.length > n) state.impl.surcos.pop();
    while (state.impl.surcos.length < n) {
      var k = state.impl.surcos.length + 1;
      state.impl.surcos.push({ numero: k, tren_id: state.impl.trenes[0].id, seccion_pilotx: 0 });
    }
    state.impl.surcos.forEach(function (s, i) { s.numero = i + 1; });
    state.impl.numero_surcos = n;
  }

  // ---- SECCIONES PILOTX ----------------------------------------------

  function paintSeccionesX() {
    ensureImpl();
    var nSec = parseInt($('numSections').value, 10) || 0;
    while (state.impl.secciones.length > nSec) state.impl.secciones.pop();
    while (state.impl.secciones.length < nSec) {
      var k = state.impl.secciones.length + 1;
      state.impl.secciones.push({ id: k, nombre: 'Sección ' + k, lookahead_on: 0, lookahead_off: 0 });
    }
    var tb = document.querySelector('#tblSeccionesX tbody'); if (!tb) return;
    while (tb.firstChild) tb.removeChild(tb.firstChild);
    state.impl.secciones.forEach(function (s, idx) {
      var tr = document.createElement('tr');
      tr.style.borderBottom = '1px solid var(--agp-border)';
      tr.innerHTML =
        '<td style="padding:6px">' + (s.id != null ? s.id : (idx + 1)) + '</td>' +
        '<td style="padding:6px"><input type="text" data-sec-idx="' + idx + '" data-field="nombre" value="' + (s.nombre || '') + '"></td>' +
        '<td style="padding:6px"><input type="number" step="0.05" min="0" data-sec-idx="' + idx + '" data-field="lookahead_on" value="' + (s.lookahead_on || 0) + '"></td>' +
        '<td style="padding:6px"><input type="number" step="0.05" min="0" data-sec-idx="' + idx + '" data-field="lookahead_off" value="' + (s.lookahead_off || 0) + '"></td>';
      tb.appendChild(tr);
    });
    tb.querySelectorAll('input[data-sec-idx]').forEach(function (inp) {
      inp.addEventListener('change', function () {
        var i = parseInt(inp.getAttribute('data-sec-idx'), 10);
        var f = inp.getAttribute('data-field');
        if (f === 'nombre') state.impl.secciones[i].nombre = inp.value;
        else state.impl.secciones[i][f] = parseFloat(inp.value) || 0;
        markDirty();
      });
    });
  }

  // ---- read form → DTO -----------------------------------------------

  function syncFormToImpl() {
    ensureImpl();
    var d = state.impl;
    d.nombre = ($('implNombreInp').value || '').trim() || d.nombre || 'Implemento';
    // Tipo, geometría y look-ahead global son responsabilidad de PilotX.
    // El Hub sólo administra cantidad de secciones, trenes, surcos y los
    // overrides por sección — eso ya se sincroniza directo a state.impl
    // desde los handlers de cada tabla.
  }

  // ---- API calls -----------------------------------------------------

  async function fetchJson(url, opts) {
    opts = opts || {};
    if (!opts.headers) opts.headers = {};
    if (opts.body && !opts.headers['Content-Type']) opts.headers['Content-Type'] = 'application/json';
    var r = await fetch(url, opts);
    return await r.json();
  }

  async function refreshLista() {
    var data = await fetchJson('/api/implementos', { cache: 'no-store' });
    if (data && data.ok) {
      state.activo = data.activo || '';
      state.lista = data.lista || [];
      paintSelector();
    }
  }

  function paintSelector() {
    var sel = $('implActivoSel');
    sel.innerHTML = state.lista.map(function (e) {
      var s = e.slug === state.activo ? ' selected' : '';
      return '<option value="' + e.slug + '"' + s + '>' + e.nombre + '</option>';
    }).join('');
    if (state.lista.length === 0) {
      sel.innerHTML = '<option value="">(sin implementos)</option>';
    }
  }

  async function loadActivo() {
    pill('', 'Cargando…');
    try {
      await refreshLista();
      if (!state.activo) {
        pill('err', 'Sin implemento activo');
        return;
      }
      var data = await fetchJson('/api/implementos/' + encodeURIComponent(state.activo), { cache: 'no-store' });
      if (!data || !data.ok || !data.implemento) {
        pill('err', 'Error al cargar');
        return;
      }
      state.impl = data.implemento;
      state.dirty = false;
      paintAll();
      pill('ok', 'OK · ' + (state.impl.nombre || state.activo));
      msg('', '');
    } catch (e) {
      pill('err', 'Error');
      msg('err', '✕ ' + e.message);
    }
  }

  async function guardar() {
    if (!state.activo) { msg('err', '✕ sin implemento activo'); return; }
    msg('', 'Guardando…');
    syncFormToImpl();
    try {
      var data = await fetchJson('/api/implementos/' + encodeURIComponent(state.activo), {
        method: 'PUT',
        body: JSON.stringify(state.impl)
      });
      if (data && data.ok) {
        state.dirty = false;
        // Refrescar lista (puede haber cambiado el nombre)
        await refreshLista();
        msg('ok', '✓ Guardado');
        pill('ok', 'OK · ' + (state.impl.nombre || state.activo));
      } else {
        msg('err', '✕ ' + ((data && data.error) || 'no se pudo guardar'));
      }
    } catch (e) { msg('err', '✕ ' + e.message); }
  }

  async function descartar() {
    if (state.dirty && !confirm('Descartar cambios sin guardar?')) return;
    msg('', '');
    await loadActivo();
  }

  async function cambiarActivo(slug) {
    if (state.dirty && !confirm('Hay cambios sin guardar. Cambiar de implemento igual?')) {
      // revertir el select
      $('implActivoSel').value = state.activo;
      return;
    }
    var data = await fetchJson('/api/implementos/activo', {
      method: 'POST',
      body: JSON.stringify({ slug: slug })
    });
    if (data && data.ok) {
      state.activo = data.activo;
      await loadActivo();
    } else {
      msg('err', '✕ no se pudo cambiar');
    }
  }

  async function nuevoImplemento() {
    if (state.dirty && !confirm('Hay cambios sin guardar. Crear uno nuevo igual?')) return;
    var nombre = prompt('Nombre del nuevo implemento:', 'Implemento nuevo');
    if (nombre == null) return;
    nombre = nombre.trim();
    if (!nombre) return;
    var data = await fetchJson('/api/implementos/nuevo', {
      method: 'POST',
      body: JSON.stringify({ nombre: nombre })
    });
    if (data && data.ok) {
      state.activo = data.slug;
      await loadActivo();
      msg('ok', '✓ Implemento creado');
    } else {
      msg('err', '✕ no se pudo crear');
    }
  }

  async function copiarImplemento() {
    if (!state.activo) return;
    if (state.dirty && !confirm('Hay cambios sin guardar. Duplicar igual? (se copia la versión guardada)')) return;
    var sugerido = (state.impl && state.impl.nombre ? state.impl.nombre : state.activo) + ' (copia)';
    var nombre = prompt('Nombre para la copia:', sugerido);
    if (nombre == null) return;
    nombre = nombre.trim();
    if (!nombre) return;
    var data = await fetchJson('/api/implementos/copiar', {
      method: 'POST',
      body: JSON.stringify({ from: state.activo, nombre: nombre })
    });
    if (data && data.ok) {
      state.activo = data.slug;
      await loadActivo();
      msg('ok', '✓ Copia creada');
    } else {
      msg('err', '✕ no se pudo duplicar');
    }
  }

  async function eliminarImplemento() {
    if (!state.activo) return;
    if (state.lista.length <= 1) { alert('No se puede eliminar el único implemento. Creá otro primero.'); return; }
    var nombre = state.impl && state.impl.nombre ? state.impl.nombre : state.activo;
    if (!confirm('Eliminar el implemento "' + nombre + '"? No se puede deshacer.')) return;
    var data = await fetchJson('/api/implementos/' + encodeURIComponent(state.activo), { method: 'DELETE' });
    if (data && data.ok) {
      state.activo = data.activo || '';
      state.dirty = false;
      await loadActivo();
      msg('ok', '✓ Eliminado');
    } else {
      msg('err', '✕ no se pudo eliminar');
    }
  }

  // ---- wire ----------------------------------------------------------

  document.querySelectorAll('.tabs .tab').forEach(function (t) {
    t.addEventListener('click', function () {
      document.querySelectorAll('.tabs .tab').forEach(function (x) { x.classList.remove('on'); });
      document.querySelectorAll('.panel').forEach(function (p) { p.classList.remove('on'); });
      t.classList.add('on');
      var p = document.getElementById('panel-' + t.dataset.panel);
      if (p) p.classList.add('on');
      var k = t.dataset.panel;
      if (k === 'secciones') updateSectionsVis();
      else if (k === 'trenes') paintTrenes();
      else if (k === 'surcos') paintSurcos();
      else if (k === 'seccionesx') paintSeccionesX();
    });
  });

  // Cantidad de secciones — único input que ajusta el shape del DTO desde el form.
  // Al cambiarlo redimensionamos secciones, refrescamos visualización y tablas.
  var nSecInp = $('numSections');
  if (nSecInp) {
    nSecInp.addEventListener('input', function () {
      updateSectionsVis();
      paintSurcos();
      paintSeccionesX();
      markDirty();
    });
  }

  $('implNombreInp').addEventListener('input', markDirty);

  // Trenes
  var btnAddTren = $('btnAddTren');
  if (btnAddTren) btnAddTren.addEventListener('click', function () {
    ensureImpl();
    var nextId = 0;
    state.impl.trenes.forEach(function (t) { if ((t.id | 0) >= nextId) nextId = (t.id | 0) + 1; });
    state.impl.trenes.push({ id: nextId, nombre: 'Tren ' + nextId, distancia_m: 0 });
    paintTrenes(); paintSurcos(); markDirty();
  });

  // Surcos
  var iNum = $('implNumSurcos');
  if (iNum) iNum.addEventListener('change', function () {
    resizeSurcos(parseInt(iNum.value, 10) || 0);
    paintSurcos(); markDirty();
  });
  var iDist = $('implDistSurcos');
  if (iDist) iDist.addEventListener('change', function () {
    ensureImpl();
    state.impl.distancia_entre_surcos_m = parseFloat(iDist.value) || 0.525;
    markDirty();
  });

  // CRUD barra
  $('implActivoSel').addEventListener('change', function (e) {
    cambiarActivo(e.target.value);
  });
  $('btnImplNuevo').addEventListener('click', nuevoImplemento);
  $('btnImplCopiar').addEventListener('click', copiarImplemento);
  $('btnImplEliminar').addEventListener('click', eliminarImplemento);

  // Guardar / descartar
  $('btnSaveTool').addEventListener('click', guardar);
  $('btnReloadTool').addEventListener('click', descartar);

  // ===== Catálogo de modelos de sembradoras =====
  // Carga marcas/modelos del backend y al "Aplicar plantilla" hace POST
  // /api/implemento/aplicar-plantilla. Después recarga el implemento para
  // ver los campos pre-llenados (surcos, distancia, torres, etc.).
  var _cat = { marcas: [], byMarca: {} };

  function chip(text) {
    return '<span style="display:inline-block; padding: 2px 10px; '
      + 'background: var(--agp-bg-soft); border-radius: 12px; '
      + 'font-size: var(--agp-fs-xs); color: var(--agp-text-muted);">'
      + text + '</span>';
  }

  function paintCatChips(tpl) {
    var box = $('catChips');
    if (!box) return;
    if (!tpl) { box.innerHTML = ''; $('catDesc').textContent = ''; return; }
    var chips = [];
    if (tpl.tipo_cultivo)    chips.push(chip('Cultivo: ' + tpl.tipo_cultivo));
    if (tpl.tipo_siembra)    chips.push(chip('Siembra: ' + tpl.tipo_siembra));
    if (tpl.tipo_dosificador) chips.push(chip('Dosificador: ' + tpl.tipo_dosificador));
    if (tpl.numero_torres > 0) chips.push(chip(tpl.numero_torres + ' torres'));
    if (tpl.numero_surcos > 0) chips.push(chip(tpl.numero_surcos + ' surcos @ '
                                + tpl.distancia_entre_surcos_m + ' m'));
    if (tpl.tipo_estructura)   chips.push(chip(tpl.tipo_estructura));
    if (tpl.tiene_fertilizacion) chips.push(chip('Fertilización'));
    box.innerHTML = chips.join(' ');
    $('catDesc').textContent = tpl.descripcion || '';
  }

  function refillModelos() {
    var marca = $('catMarca').value;
    var sel = $('catModelo');
    var modelos = _cat.byMarca[marca] || [];
    sel.innerHTML = modelos.map(function (m) {
      return '<option value="' + m.modelo + '">' + m.modelo + '</option>';
    }).join('');
    if (modelos.length > 0) {
      sel.value = modelos[0].modelo;
      paintCatChips(modelos[0]);
    } else {
      paintCatChips(null);
    }
  }

  async function loadCatalogo() {
    try {
      var data = await fetchJson('/api/catalogo/sembradoras', { cache: 'no-store' });
      if (!data || !data.ok || !data.marcas) return;
      _cat.marcas = data.marcas;
      _cat.byMarca = {};
      _cat.marcas.forEach(function (b) { _cat.byMarca[b.marca] = b.modelos || []; });
      var sm = $('catMarca');
      sm.innerHTML = _cat.marcas.map(function (b) {
        return '<option value="' + b.marca + '">' + b.marca + '</option>';
      }).join('');
      // Pre-seleccionar la marca actual del implemento, si está en el catálogo.
      if (state.impl && state.impl.marca && _cat.byMarca[state.impl.marca]) {
        sm.value = state.impl.marca;
      }
      refillModelos();
      if (state.impl && state.impl.modelo) {
        var opt = Array.prototype.find.call($('catModelo').options,
          function (o) { return o.value === state.impl.modelo; });
        if (opt) {
          $('catModelo').value = state.impl.modelo;
          var tpl = (_cat.byMarca[sm.value] || []).find(function (m) {
            return m.modelo === state.impl.modelo;
          });
          paintCatChips(tpl);
        }
      }
    } catch (_) { /* catálogo opcional */ }
  }

  $('catMarca').addEventListener('change', refillModelos);
  $('catModelo').addEventListener('change', function () {
    var marca = $('catMarca').value;
    var modelo = $('catModelo').value;
    var tpl = (_cat.byMarca[marca] || []).find(function (m) { return m.modelo === modelo; });
    paintCatChips(tpl);
  });
  $('btnAplicarPlantilla').addEventListener('click', async function () {
    var marca = $('catMarca').value;
    var modelo = $('catModelo').value;
    if (!marca || !modelo) return;
    if (state.dirty && !confirm('Hay cambios sin guardar. Aplicar plantilla igual?')) return;
    msg('', 'Aplicando plantilla…');
    try {
      var data = await fetchJson('/api/implemento/aplicar-plantilla', {
        method: 'POST',
        body: JSON.stringify({ marca: marca, modelo: modelo })
      });
      if (data && data.ok) {
        await loadActivo();
        msg('ok', '✓ Plantilla aplicada: ' + marca + ' ' + modelo);
      } else {
        msg('err', '✕ ' + ((data && data.error) || 'no se pudo aplicar'));
      }
    } catch (e) { msg('err', '✕ ' + e.message); }
  });

  loadActivo().then(loadCatalogo);
})();
