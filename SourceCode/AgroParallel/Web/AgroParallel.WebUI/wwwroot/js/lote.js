// ============================================================================
// lote.js — gestión de lotes desde la UI HTML (tabla + buscador + paginado).
//   GET  /api/lotes              → lista de FieldInfo
//   GET  /api/lotes/current      → { name: string|null }
//   POST /api/lotes/{open,close,create}
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

  // Backend (EmbedIO/Swan) serializa POCO con PascalCase. Normalizamos a camelCase
  // para uniformar el JS.
  function lkeys(v) {
    if (v == null) return v;
    if (Array.isArray(v)) return v.map(lkeys);
    if (typeof v !== 'object') return v;
    var out = {};
    for (var k in v) {
      if (!Object.prototype.hasOwnProperty.call(v, k)) continue;
      var nk = k.length > 0 ? k.charAt(0).toLowerCase() + k.substring(1) : k;
      out[nk] = lkeys(v[k]);
    }
    return out;
  }

  // ---------- State ----------

  var state = {
    current: null,    // nombre del lote abierto
    all: [],          // lista completa de FieldInfo (camelCase)
    busy: false,
    search: '',
    sort: { key: 'lastModifiedUtc', dir: 'desc' }, // por defecto: más recientes primero
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
        if (!!a.isCurrent !== !!b.isCurrent) return a.isCurrent ? -1 : 1;
      }
      if (key === 'lastModifiedUtc') {
        var ta = va ? new Date(va).getTime() : 0;
        var tb = vb ? new Date(vb).getTime() : 0;
        return (ta - tb) * dir;
      }
      if (key === 'hasBoundary') {
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
      body.innerHTML = '<tr class="empty-row"><td colspan="4">' +
        (state.search ? 'Ningún lote coincide con "' + esc(state.search) + '".' :
          'No hay lotes en Fields/. Creá uno arriba.') +
        '</td></tr>';
    } else {
      body.innerHTML = slice.map(function (f) {
        var isCur = !!f.isCurrent;
        var hasB  = !!f.hasBoundary;
        var ha    = (f.areaHa && f.areaHa > 0) ? f.areaHa.toFixed(2) + ' ha' : '';

        var flags = '';
        if (isCur) flags += '<span class="pill ok"><span class="dot"></span> abierto</span>';
        if (hasB)  flags += '<span class="pill">contorno</span>';
        if (ha)    flags += '<span class="pill">' + esc(ha) + '</span>';
        if (!flags) flags = '<span style="color:var(--agp-text-muted); font-size: var(--agp-fs-xs)">—</span>';

        var actions = isCur
          ? '<button class="btn" data-act="close">Cerrar</button>'
          : '<button class="btn primary" data-act="open" data-name="' + esc(f.name) + '">Abrir</button>';

        return '' +
          '<tr class="' + (isCur ? 'current' : '') + '" data-name="' + esc(f.name) + '">' +
            '<td class="col-name">' + esc(f.name) + '</td>' +
            '<td class="col-date">' + fmtDate(f.lastModifiedUtc) + '</td>' +
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
      var d = lkeys(await r.json());
      state.current = (d && d.name) || null;
    } catch (e) { state.current = null; }
    renderCurrent();
  }

  async function loadList() {
    try {
      var r = await fetch('/api/lotes', { cache: 'no-store' });
      var d = lkeys(await r.json());
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
      var d = lkeys(await r.json());
      setMsg($('msgCur'), (d && d.ok) ? ('✓ Lote abierto: ' + name) : '✕ No se pudo abrir el lote.', (d && d.ok) ? 'ok' : 'err');
    } catch (e) { setMsg($('msgCur'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  async function closeLote() {
    if (state.busy || !state.current) return;
    if (!confirm('¿Cerrar el lote "' + state.current + '"? Se guardan boundary, sections, contour y tracks.')) return;
    state.busy = true;
    setMsg($('msgCur'), '… cerrando "' + state.current + '" …');
    renderCurrent();
    try {
      var r = await fetch('/api/lotes/close', { method: 'POST' });
      var d = lkeys(await r.json());
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
      if (!confirm('Ya existe un lote llamado "' + clean + '". ¿Querés abrirlo en lugar de crear?')) return;
      return openLote(clean);
    }
    state.busy = true;
    setMsg($('msgNew'), '… creando "' + clean + '" …');
    try {
      var r = await fetch('/api/lotes/create?name=' + encodeURIComponent(clean), { method: 'POST' });
      var d = lkeys(await r.json());
      if (d && d.ok) { setMsg($('msgNew'), '✓ Lote creado y abierto: ' + clean, 'ok'); inp.value = ''; }
      else            setMsg($('msgNew'), '✕ No se pudo crear el lote.', 'err');
    } catch (e) { setMsg($('msgNew'), '✕ ' + e.message, 'err'); }
    state.busy = false;
    await refresh();
  }

  function addDateSuffix() {
    var inp = $('newName');
    if (!inp) return;
    var d = new Date();
    var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
    var s = ' ' + d.getFullYear() + pad(d.getMonth() + 1) + pad(d.getDate());
    var v = (inp.value || '').trim();
    if (/\s\d{8}$/.test(v)) return;
    inp.value = (v ? v + s : s.trim());
    inp.focus();
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
        state.sort.dir = (key === 'name') ? 'asc' : 'desc';
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
  var btnA = $('btnAddDate'); if (btnA)  btnA.addEventListener('click', addDateSuffix);
  var btnN = $('btnCreate');  if (btnN)  btnN.addEventListener('click', createLote);
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
