// ============================================================================
// lote.js — gestión de lotes desde la UI HTML (tabla + buscador + paginado).
//   GET  /api/lotes              → lista de FieldInfo
//   GET  /api/lotes/current      → { name: string|null }
//   POST /api/lotes/{open,close,create}
//   POST /api/lotes/from-existing        → clonar template (ex FormFieldExisting)
//   POST /api/lotes/import-{kml,isoxml}  → diálogos nativos (ex FormJob)
//
// Tabla compacta (no cards), search por nombre, sort por columna,
// paginado configurable. Refresh pasivo cada 5s — preservando el estado de
// search/sort/pagina/scroll para no romper la interacción del operario.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function fmtDate(iso) {
    try {
      var d = new Date(iso);
      if (isNaN(d.getTime())) return '—';
      var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
      return pad(d.getDate()) + '/' + pad(d.getMonth() + 1) + '/' + d.getFullYear() +
             ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    } catch (e) { return '—'; }
  }

  function fmtDistKm(km) {
    if (typeof km !== 'number' || !isFinite(km) || km < 0) return '—';
    if (km < 1) return Math.round(km * 1000) + ' m';
    return km.toFixed(2) + ' km';
  }

  // ---------- State ----------

  var state = {
    current: null,    // nombre del lote abierto
    all: [],          // lista completa de FieldInfo (snake_case)
    busy: false,
    search: '',
    sort: { key: 'last_modified_utc', dir: 'desc' }, // por defecto: más recientes primero
    page: 1,
    pageSize: 25
  };

  // ---------- Render: header (lote actual) ----------

  function setMsg(el, text, kind) {
    if (!el) return;
    el.textContent = text || '';
    el.className = 'msg' + (kind ? ' ' + kind : '');
  }

  function renderCurrent() {
    var pill = $('curPill');
    var name = $('curName');
    var btn  = $('btnClose');
    if (state.current) {
      if (pill) { pill.className = 'pill ok'; pill.innerHTML = '<span class="dot"></span> Abierto'; }
      if (name) { name.textContent = state.current; name.style.color = 'var(--agp-text)'; }
      if (btn)  { btn.disabled = state.busy; }
    } else {
      if (pill) { pill.className = 'pill idle'; pill.innerHTML = '<span class="dot"></span> Sin lote abierto'; }
      if (name) { name.textContent = '—'; name.style.color = 'var(--agp-text-muted)'; }
      if (btn)  { btn.disabled = true; }
    }
  }

  // ---------- Render: tabla ----------

  function getFiltered() {
    var q = state.search.trim().toLowerCase();
    var arr = state.all.slice();
    if (q) {
      arr = arr.filter(function (f) {
        return f.name && f.name.toLowerCase().indexOf(q) >= 0;
      });
    }
    var key = state.sort.key;
    var dir = state.sort.dir === 'asc' ? 1 : -1;
    arr.sort(function (a, b) {
      var va = a[key], vb = b[key];
      // Current siempre primero (override del sort), salvo si search activa
      if (!q) {
        if (!!a.is_current !== !!b.is_current) return a.is_current ? -1 : 1;
      }
      if (key === 'last_modified_utc') {
        var ta = va ? new Date(va).getTime() : 0;
        var tb = vb ? new Date(vb).getTime() : 0;
        return (ta - tb) * dir;
      }
      if (key === 'distance_km') {
        // Distancias desconocidas (-1) siempre al final.
        var da = (typeof va === 'number' && va >= 0) ? va : Infinity;
        var db = (typeof vb === 'number' && vb >= 0) ? vb : Infinity;
        if (da === Infinity && db === Infinity) return 0;
        if (da === Infinity) return 1;
        if (db === Infinity) return -1;
        return (da - db) * dir;
      }
      if (key === 'has_boundary') {
        return ((va ? 1 : 0) - (vb ? 1 : 0)) * dir;
      }
      // name (default string)
      va = (va || '').toString().toLowerCase();
      vb = (vb || '').toString().toLowerCase();
      if (va < vb) return -1 * dir;
      if (va > vb) return  1 * dir;
      return 0;
    });
    return arr;
  }

  function renderTable() {
    var body = $('ltBody');
    var pager = $('ltPager');
    var count = $('ltCount');
    if (!body) return;

    var filtered = getFiltered();
    var total = filtered.length;
    var pages = Math.max(1, Math.ceil(total / state.pageSize));
    if (state.page > pages) state.page = pages;
    if (state.page < 1) state.page = 1;
    var from = (state.page - 1) * state.pageSize;
    var to   = Math.min(from + state.pageSize, total);
    var slice = filtered.slice(from, to);

    if (count) {
      var totalAll = state.all.length;
      count.textContent = state.search
        ? (total + ' de ' + totalAll + ' lote' + (totalAll !== 1 ? 's' : ''))
        : (totalAll + ' lote' + (totalAll !== 1 ? 's' : ''));
    }

    // Flechas de sort
    document.querySelectorAll('th[data-arr]').forEach(function (el) {
      var k = el.getAttribute('data-arr');
      el.textContent = (k === state.sort.key) ? (state.sort.dir === 'asc' ? '▲' : '▼') : '';
    });

    if (!slice.length) {
      body.innerHTML = '<tr class="empty-row"><td colspan="5">' +
        (state.search ? 'Ningún lote coincide con "' + esc(state.search) + '".' :
          'No hay lotes en Fields/. Creá uno arriba.') +
        '</td></tr>';
    } else {
      body.innerHTML = slice.map(function (f) {
        var isCur = !!f.is_current;
        var hasB  = !!f.has_boundary;
        var ha    = (f.area_ha && f.area_ha > 0) ? f.area_ha.toFixed(2) + ' ha' : '';

        var flags = '';
        if (isCur) flags += '<span class="pill ok"><span class="dot"></span> abierto</span>';
        if (hasB)  flags += '<span class="pill">contorno</span>';
        if (ha)    flags += '<span class="pill">' + esc(ha) + '</span>';
        if (!flags) flags = '<span style="color:var(--agp-text-muted); font-size: var(--agp-fs-xs)">—</span>';

        var actions =
          '<button class="btn" data-act="clone" data-name="' + esc(f.name) + '">Clonar</button>' +
          (isCur
            ? '<button class="btn" data-act="close">Cerrar</button>'
            : '<button class="btn primary" data-act="open" data-name="' + esc(f.name) + '">Abrir</button>');

        return '' +
          '<tr class="' + (isCur ? 'current' : '') + '" data-name="' + esc(f.name) + '">' +
            '<td class="col-name">' + esc(f.name) + '</td>' +
            '<td class="col-date">' + fmtDate(f.last_modified_utc) + '</td>' +
            '<td class="col-dist">' + fmtDistKm(f.distance_km) + '</td>' +
            '<td class="col-flags">' + flags + '</td>' +
            '<td class="col-act">' + actions + '</td>' +
          '</tr>';
      }).join('');
    }

    // Pager
    if (pager) {
      if (pages <= 1) {
        pager.innerHTML = '';
      } else {
        pager.innerHTML =
          '<button class="btn" data-pg="first" ' + (state.page === 1 ? 'disabled' : '') + '>« Primero</button>' +
          '<button class="btn" data-pg="prev"  ' + (state.page === 1 ? 'disabled' : '') + '>‹ Anterior</button>' +
          '<span class="info">Página ' + state.page + ' / ' + pages + '  ·  ' +
            (from + 1) + '–' + to + ' de ' + total + '</span>' +
          '<button class="btn" data-pg="next"  ' + (state.page === pages ? 'disabled' : '') + '>Siguiente ›</button>' +
          '<button class="btn" data-pg="last"  ' + (state.page === pages ? 'disabled' : '') + '>Último »</button>';
      }
    }
  }

  // ---------- Fetch ----------

  async function loadCurrent() {
    try {
      var r = await fetch('/api/lotes/current', { cache: 'no-store' });
      var d = await r.json();
      state.current = (d && d.name) || null;
    } catch (e) { state.current = null; }
    renderCurrent();
  }

  async function loadList() {
    try {
      var r = await fetch('/api/lotes', { cache: 'no-store' });
      var d = await r.json();
      state.all = Array.isArray(d) ? d : [];
    } catch (e) { state.all = []; }
    renderTable();
  }

  async function refresh() {
    await Promise.all([loadCurrent(), loadList()]);
  }

  // ---------- Acciones ----------

  async function openLote(name) {
    if (!name || state.busy) return;
    state.busy = true;
    setMsg($('msgCur'), '… abriendo "' + name + '" …');
    renderCurrent();
    try {
      var r = await fetch('/api/lotes/open?name=' + encodeURIComponent(name), { method: 'POST' });
      var d = await r.json();
      setMsg($('msgCur'), (d && d.ok) ? ('✓ Lote abierto: ' + name) : '✕ No se pudo abrir el lote.', (d && d.ok) ? 'ok' : 'err');
    } catch (e) { setMsg($('msgCur'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  async function closeLote() {
    if (state.busy || !state.current) return;
    if (!await AgpModal.confirm('Cerrar lote', '¿Cerrar el lote "' + state.current + '"? Se guardan boundary, sections, contour y tracks.')) return;
    state.busy = true;
    setMsg($('msgCur'), '… cerrando "' + state.current + '" …');
    renderCurrent();
    try {
      var r = await fetch('/api/lotes/close', { method: 'POST' });
      var d = await r.json();
      setMsg($('msgCur'), (d && d.ok) ? '✓ Lote cerrado.' : '✕ No se pudo cerrar el lote.', (d && d.ok) ? 'ok' : 'err');
    } catch (e) { setMsg($('msgCur'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  async function createLote() {
    if (state.busy) return;
    var inp = $('newName');
    var raw = (inp.value || '').trim();
    if (!raw) { setMsg($('msgNew'), '✕ Escribí un nombre primero.', 'err'); inp.focus(); return; }
    var clean = raw.replace(/[\\/:*?"<>|.]/g, '').trim();
    if (!clean) { setMsg($('msgNew'), '✕ Nombre inválido (sin caracteres especiales).', 'err'); return; }
    var dup = state.all.some(function (f) { return f.name && f.name.toLowerCase() === clean.toLowerCase(); });
    if (dup) {
      if (!await AgpModal.confirm('Lote existente', 'Ya existe un lote llamado "' + clean + '". ¿Querés abrirlo en lugar de crear?')) return;
      return openLote(clean);
    }
    state.busy = true;
    setMsg($('msgNew'), '… creando "' + clean + '" …');
    try {
      var r = await fetch('/api/lotes/create?name=' + encodeURIComponent(clean), { method: 'POST' });
      var d = await r.json();
      if (d && d.ok) { setMsg($('msgNew'), '✓ Lote creado y abierto: ' + clean, 'ok'); inp.value = ''; }
      else            setMsg($('msgNew'), '✕ No se pudo crear el lote.', 'err');
    } catch (e) { setMsg($('msgNew'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  function addDateSuffix(inpId) {
    var inp = $(inpId || 'newName');
    if (!inp) return;
    var d = new Date();
    var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
    var s = ' ' + d.getFullYear() + pad(d.getMonth() + 1) + pad(d.getDate());
    var v = (inp.value || '').trim();
    if (/\s\d{8}$/.test(v)) return;
    inp.value = (v ? v + s : s.trim());
    inp.focus();
  }

  // ---------- Clonar desde existente (ex FormFieldExisting) ----------

  var cloneTemplate = null;

  function showClonePane(template) {
    cloneTemplate = template || null;
    var pane = $('clonePane');
    if (!pane) return;
    pane.style.display = cloneTemplate ? '' : 'none';
    if (cloneTemplate) {
      var lbl = $('cloneTemplate');
      if (lbl) lbl.textContent = cloneTemplate;
      var inp = $('cloneName');
      if (inp) { inp.value = cloneTemplate; }
      setMsg($('msgClone'), '');
      pane.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
  }

  async function cloneLote() {
    if (state.busy || !cloneTemplate) return;
    var inp = $('cloneName');
    var raw = (inp && inp.value || '').trim();
    var clean = raw.replace(/[\\/:*?"<>|.]/g, '').trim();
    if (!clean) { setMsg($('msgClone'), '✕ Escribí un nombre para el lote nuevo.', 'err'); if (inp) inp.focus(); return; }
    var dup = state.all.some(function (f) { return f.name && f.name.toLowerCase() === clean.toLowerCase(); });
    if (dup) { setMsg($('msgClone'), '✕ Ya existe un lote llamado "' + clean + '". Cambiá el nombre.', 'err'); return; }
    state.busy = true;
    setMsg($('msgClone'), '… creando "' + clean + '" desde "' + cloneTemplate + '" …');
    try {
      var r = await fetch('/api/lotes/from-existing', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          template: cloneTemplate,
          name: clean,
          applied:  !!($('chkApplied')  && $('chkApplied').checked),
          flags:    !!($('chkFlags')    && $('chkFlags').checked),
          guidance: !!($('chkGuidance') && $('chkGuidance').checked),
          headland: !!($('chkHeadland') && $('chkHeadland').checked)
        })
      });
      var d = await r.json();
      if (d && d.ok) { setMsg($('msgClone'), '✓ Lote creado y abierto: ' + clean, 'ok'); showClonePane(null); }
      else            setMsg($('msgClone'), '✕ No se pudo crear el lote desde el existente.', 'err');
    } catch (e) { setMsg($('msgClone'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  // ---------- Imports nativos (KML / ISO-XML) ----------

  async function importLote(kind) {
    if (state.busy) return;
    state.busy = true;
    setMsg($('msgImport'), '… esperando el selector de archivo en PilotX …');
    try {
      var r = await fetch('/api/lotes/import-' + kind, { method: 'POST' });
      var d = await r.json();
      setMsg($('msgImport'), (d && d.ok) ? '✓ Lote importado y abierto.' : 'Import cancelado o sin lote abierto.', (d && d.ok) ? 'ok' : '');
    } catch (e) { setMsg($('msgImport'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  // ---------- Wire-up ----------

  // Tabla: delegación de click para abrir/cerrar
  var body = $('ltBody');
  if (body) {
    body.addEventListener('click', function (ev) {
      var btn = ev.target.closest('button[data-act]');
      if (!btn) return;
      var act = btn.getAttribute('data-act');
      if (act === 'open') openLote(btn.getAttribute('data-name'));
      else if (act === 'close') closeLote();
      else if (act === 'clone') showClonePane(btn.getAttribute('data-name'));
    });
  }

  // Header: sort
  var thead = document.querySelector('.lt-table thead');
  if (thead) {
    thead.addEventListener('click', function (ev) {
      var th = ev.target.closest('th[data-sort]');
      if (!th) return;
      var key = th.getAttribute('data-sort');
      if (state.sort.key === key) {
        state.sort.dir = state.sort.dir === 'asc' ? 'desc' : 'asc';
      } else {
        state.sort.key = key;
        state.sort.dir = (key === 'name' || key === 'distance_km') ? 'asc' : 'desc';
      }
      renderTable();
    });
  }

  // Pager
  var pager = $('ltPager');
  if (pager) {
    pager.addEventListener('click', function (ev) {
      var btn = ev.target.closest('button[data-pg]');
      if (!btn || btn.disabled) return;
      var pages = Math.max(1, Math.ceil(getFiltered().length / state.pageSize));
      var op = btn.getAttribute('data-pg');
      if      (op === 'first') state.page = 1;
      else if (op === 'prev')  state.page = Math.max(1, state.page - 1);
      else if (op === 'next')  state.page = Math.min(pages, state.page + 1);
      else if (op === 'last')  state.page = pages;
      renderTable();
    });
  }

  // Search + page size + acciones
  var searchInp = $('searchInp');
  if (searchInp) {
    var debounce = null;
    searchInp.addEventListener('input', function () {
      clearTimeout(debounce);
      debounce = setTimeout(function () {
        state.search = searchInp.value || '';
        state.page = 1;
        renderTable();
      }, 120);
    });
  }
  var pageSizeSel = $('pageSize');
  if (pageSizeSel) {
    pageSizeSel.addEventListener('change', function () {
      state.pageSize = parseInt(pageSizeSel.value, 10) || 25;
      state.page = 1;
      renderTable();
    });
  }

  var btnC = $('btnClose');   if (btnC)  btnC.addEventListener('click', closeLote);
  var btnA = $('btnAddDate'); if (btnA)  btnA.addEventListener('click', function () { addDateSuffix('newName'); });
  var btnN = $('btnCreate');  if (btnN)  btnN.addEventListener('click', createLote);

  var btnKml = $('btnImportKml'); if (btnKml) btnKml.addEventListener('click', function () { importLote('kml'); });
  var btnIso = $('btnImportIso'); if (btnIso) btnIso.addEventListener('click', function () { importLote('isoxml'); });

  var btnCd = $('btnCloneDate');   if (btnCd) btnCd.addEventListener('click', function () { addDateSuffix('cloneName'); });
  var btnCx = $('btnCloneCancel'); if (btnCx) btnCx.addEventListener('click', function () { showClonePane(null); });
  var btnCc = $('btnCloneCreate'); if (btnCc) btnCc.addEventListener('click', cloneLote);
  var inpCn = $('cloneName');
  if (inpCn) {
    inpCn.addEventListener('keydown', function (ev) {
      if (ev.key === 'Enter') { ev.preventDefault(); cloneLote(); }
    });
  }
  var inpN = $('newName');
  if (inpN) {
    inpN.addEventListener('keydown', function (ev) {
      if (ev.key === 'Enter') { ev.preventDefault(); createLote(); }
    });
  }

  // ---------- Init ----------

  refresh();
  setInterval(refresh, 5000);
})();
