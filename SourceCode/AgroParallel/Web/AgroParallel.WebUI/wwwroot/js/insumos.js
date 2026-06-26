// ============================================================================
// insumos.js — CRUD del catálogo compartido de insumos.
// Lo consumen VistaX, QuantiX, FlowX y datos-lote.html.
// API: GET/POST /api/insumos · GET/POST /api/insumos/activo
// ============================================================================
(function () {
  'use strict';

  var state = {
    catalogo: { version: '1.0', activo_id: '', items: [] },
    editando: null, // id del item en edición, '' = nuevo
    tipo: 'semilla'
  };

  // ---------------------------------------------------------------- helpers
  function $(id) { return document.getElementById(id); }
  function slug(s) {
    return (s || '')
      .toLowerCase()
      .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/(^-|-$)/g, '');
  }
  function uniqueId(base, items, ignoreId) {
    var id = base || ('insumo-' + Date.now());
    var i = 2;
    while (items.some(function (it) { return it.id === id && it.id !== ignoreId; })) {
      id = (base || 'insumo') + '-' + i++;
    }
    return id;
  }
  function fmtMeta(it) {
    var parts = [];
    if (it.tipo) parts.push(it.tipo);
    if (it.cultivo) parts.push(it.cultivo);
    if (it.tipo === 'semilla' && it.densidad_objetivo_sem_m)
      parts.push(it.densidad_objetivo_sem_m + ' sem/m');
    if (it.dosis_kgha) parts.push(it.dosis_kgha + ' kg/ha');
    if (it.dosis_lha) parts.push(it.dosis_lha + ' L/ha');
    return parts.join(' · ');
  }

  // ---------------------------------------------------------------- load
  async function load() {
    try {
      var r = await fetch('/api/insumos');
      var dto = await r.json();
      state.catalogo = dto || { version: '1.0', activo_id: '', items: [] };
      if (!Array.isArray(state.catalogo.items)) state.catalogo.items = [];
    } catch (e) {
      console.warn('[insumos] load:', e);
    }
    render();
  }

  // ---------------------------------------------------------------- save
  async function persist() {
    try {
      var r = await fetch('/api/insumos', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.catalogo)
      });
      var res = await r.json();
      if (!res || res.ok === false) console.warn('[insumos] save:', res);
    } catch (e) {
      console.warn('[insumos] save:', e);
    }
  }

  async function setActivo(id) {
    try {
      await fetch('/api/insumos/activo', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: id || '' })
      });
      state.catalogo.activo_id = id || '';
    } catch (e) {
      console.warn('[insumos] setActivo:', e);
    }
    render();
  }

  // ---------------------------------------------------------------- render
  function render() {
    renderPill();
    renderLista();
  }

  function renderPill() {
    var pill = $('activoPill');
    if (!pill) return;
    var activo = (state.catalogo.items || []).find(function (it) {
      return it.id === state.catalogo.activo_id;
    });
    if (activo) {
      pill.className = 'pill ok';
      pill.innerHTML = '<span class="dot"></span> Activo: ' + (activo.nombre || activo.id);
    } else {
      pill.className = 'pill idle';
      pill.innerHTML = '<span class="dot"></span> Sin insumo activo';
    }
  }

  function renderLista() {
    var box = $('lista');
    if (!box) return;
    var items = state.catalogo.items || [];
    if (items.length === 0) {
      box.innerHTML = '<div class="subtitle">No hay insumos cargados. Tocá "+ Nuevo insumo" para empezar.</div>';
      return;
    }
    var activoId = state.catalogo.activo_id || '';
    box.innerHTML = items.map(function (it) {
      var cls = 'insumo-row' + (it.id === activoId ? ' is-active' : '');
      return [
        '<div class="' + cls + '" data-id="' + it.id + '">',
        '  <div class="radio-dot" data-action="activar"></div>',
        '  <div data-action="editar">',
        '    <div class="nombre">' + esc(it.nombre || it.id) + '</div>',
        '    <div class="meta">' + esc(fmtMeta(it)) + '</div>',
        '  </div>',
        '  <div class="actions">',
        '    <button type="button" class="btn-ghost" data-action="editar">Editar</button>',
        '  </div>',
        '</div>'
      ].join('');
    }).join('');

    box.querySelectorAll('.insumo-row').forEach(function (row) {
      var id = row.getAttribute('data-id');
      row.addEventListener('click', function (ev) {
        var act = ev.target.getAttribute && ev.target.getAttribute('data-action');
        if (act === 'activar') {
          setActivo(id === state.catalogo.activo_id ? '' : id);
        } else {
          openEditor(id);
        }
      });
    });
  }

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  // ---------------------------------------------------------------- editor
  function openEditor(id) {
    var it = id ? (state.catalogo.items || []).find(function (x) { return x.id === id; }) : null;
    state.editando = id || '';
    var draft = it || {
      id: '',
      nombre: '',
      tipo: 'semilla',
      cultivo: '',
      densidad_objetivo_sem_m: 0,
      densidad_asumida_saturado_sem_m: 0,
      singulacion_objetivo_pct: 97,
      drop_min_sem_m: 0,
      drop_max_sem_m: 0,
      dosis_kgha: 0,
      dosis_lha: 0,
      precio_usd_kg: 0,
      precio_usd_l: 0,
      notas: ''
    };

    $('editorTitle').textContent = it ? 'Editar insumo' : 'Nuevo insumo';
    $('editorIdHint').textContent = it
      ? ('ID: ' + it.id + ' (no se cambia)')
      : 'El ID se genera automáticamente del nombre al guardar.';
    $('fNombre').value = draft.nombre || '';
    $('fCultivo').value = draft.cultivo || '';
    $('fDensObj').value = draft.densidad_objetivo_sem_m || '';
    $('fDensSat').value = draft.densidad_asumida_saturado_sem_m || '';
    $('fSingul').value = draft.singulacion_objetivo_pct || 97;
    $('fDropMin').value = draft.drop_min_sem_m || '';
    $('fDropMax').value = draft.drop_max_sem_m || '';
    $('fDosisKg').value = draft.dosis_kgha || '';
    $('fPrecioKg').value = draft.precio_usd_kg || '';
    $('fDosisL').value = draft.dosis_lha || '';
    $('fPrecioL').value = draft.precio_usd_l || '';
    $('fNotas').value = draft.notas || '';

    setTipo(draft.tipo || 'semilla');
    $('btnEliminar').style.display = it ? '' : 'none';

    $('editorCard').style.display = '';
    $('editorCard').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function closeEditor() {
    state.editando = null;
    $('editorCard').style.display = 'none';
  }

  function setTipo(t) {
    state.tipo = t;
    document.querySelectorAll('.tipo-group button').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-tipo') === t);
    });
    // Show/hide bloques según tipo.
    var showSemilla = (t === 'semilla');
    var showSolido = (t === 'semilla' || t === 'fertilizante');
    var showLiquido = (t === 'fertilizante' || t === 'fitosanitario');
    document.querySelectorAll('.row-semilla').forEach(function (el) {
      el.style.display = showSemilla ? '' : 'none';
    });
    document.querySelectorAll('.row-solido').forEach(function (el) {
      el.style.display = showSolido ? '' : 'none';
    });
    document.querySelectorAll('.row-liquido').forEach(function (el) {
      el.style.display = showLiquido ? '' : 'none';
    });
  }

  function num(v) {
    var n = parseFloat(v);
    return isFinite(n) ? n : 0;
  }

  async function save() {
    var nombre = ($('fNombre').value || '').trim();
    if (!nombre) { alert('Falta el nombre del insumo.'); return; }

    var items = state.catalogo.items || (state.catalogo.items = []);
    var editId = state.editando;
    var dto;
    if (editId) {
      dto = items.find(function (x) { return x.id === editId; });
      if (!dto) { dto = { id: editId }; items.push(dto); }
    } else {
      dto = { id: uniqueId(slug(nombre), items, null) };
      items.push(dto);
    }

    dto.nombre = nombre;
    dto.tipo = state.tipo || 'semilla';
    dto.cultivo = ($('fCultivo').value || '').trim();
    dto.densidad_objetivo_sem_m = num($('fDensObj').value);
    dto.densidad_asumida_saturado_sem_m = num($('fDensSat').value);
    dto.singulacion_objetivo_pct = num($('fSingul').value) || 97;
    dto.drop_min_sem_m = num($('fDropMin').value);
    dto.drop_max_sem_m = num($('fDropMax').value);
    dto.dosis_kgha = num($('fDosisKg').value);
    dto.dosis_lha = num($('fDosisL').value);
    dto.precio_usd_kg = num($('fPrecioKg').value);
    dto.precio_usd_l = num($('fPrecioL').value);
    dto.notas = ($('fNotas').value || '').trim();

    await persist();
    closeEditor();
    render();
  }

  async function eliminar() {
    if (!state.editando) return;
    if (!confirm('¿Eliminar este insumo del catálogo?')) return;
    state.catalogo.items = (state.catalogo.items || []).filter(function (x) {
      return x.id !== state.editando;
    });
    if (state.catalogo.activo_id === state.editando) state.catalogo.activo_id = '';
    await persist();
    closeEditor();
    render();
  }

  // ---------------------------------------------------------------- wire
  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.tipo-group button').forEach(function (b) {
      b.addEventListener('click', function () { setTipo(b.getAttribute('data-tipo')); });
    });
    $('btnNuevo').addEventListener('click', function () { openEditor(''); });
    $('btnCancelar').addEventListener('click', closeEditor);
    $('btnGuardar').addEventListener('click', save);
    $('btnEliminar').addEventListener('click', eliminar);
    // Paso adaptativo en las dosis (kg/ha y L/ha) — ver js/steps.js.
    if (window.AGPSteps) {
      document.querySelectorAll('input[data-adaptive]').forEach(function (inp) {
        window.AGPSteps.attachAdaptive(inp, inp.getAttribute('data-adaptive'));
      });
    }
    load();
  });
})();
