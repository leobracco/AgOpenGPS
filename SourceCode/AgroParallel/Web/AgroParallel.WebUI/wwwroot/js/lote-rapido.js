// lote-rapido.js — widget simple de lote (crear · continuar · abrir · borrar).
// Reemplaza al Hub completo (lote.html) como acceso rápido desde el menú.
// API: GET /api/lotes, GET /api/lotes/current, POST /api/lotes/{open,close,create,delete}
(function () {
  'use strict';
  var $ = function (id) { return document.getElementById(id); };
  var state = { all: [], current: null, busy: false };

  function setMsg(t, cls) {
    var m = $('msg'); m.textContent = t || ''; m.className = 'lr-msg' + (cls ? ' ' + cls : '');
  }

  function fmtMeta(it) {
    var p = [];
    if (it.area_ha > 0) p.push(it.area_ha.toFixed(1) + ' ha');
    if (it.distance_km >= 0) p.push(it.distance_km.toFixed(1) + ' km');
    if (!it.has_boundary) p.push('sin límite');
    return p.join(' · ');
  }

  function render() {
    // pill + caja de lote abierto
    var pill = $('curPill');
    if (state.current) {
      pill.className = 'pill ok'; pill.innerHTML = '<span class="dot"></span> Abierto';
      $('curBox').hidden = false; $('curName').textContent = state.current;
    } else {
      pill.className = 'pill idle'; pill.innerHTML = '<span class="dot"></span> Sin lote';
      $('curBox').hidden = true;
    }

    // ordenar por más reciente (el backend ya suele mandarlos así; reforzamos)
    var lista = state.all.slice().sort(function (a, b) {
      return (b.last_modified_utc || '').localeCompare(a.last_modified_utc || '');
    });

    // continuar = el más reciente que NO sea el ya abierto
    var recientes = lista.filter(function (it) { return it.name !== state.current; });
    var cont = recientes[0];
    var bc = $('btnContinuar');
    if (cont && !state.current) {
      bc.hidden = false; $('contName').textContent = cont.name; bc.dataset.name = cont.name;
    } else {
      bc.hidden = true;
    }

    // lista
    var box = $('list');
    if (!lista.length) { box.innerHTML = '<div class="lr-empty">No hay lotes guardados todavía.</div>'; return; }
    box.innerHTML = '';
    lista.forEach(function (it) {
      var row = document.createElement('div');
      row.className = 'lr-item' + (it.name === state.current ? ' cur' : '');
      var meta = fmtMeta(it);
      row.innerHTML =
        '<div class="info"><div class="n"></div>' + (meta ? '<div class="m">' + meta + '</div>' : '') + '</div>' +
        (it.name === state.current
          ? '<span class="st" style="color:var(--agp-accent);font-size:var(--agp-fs-sm)">abierto</span>'
          : '<button class="btn" data-act="open">Abrir</button>') +
        '<button class="btn del" data-act="del" title="Borrar">🗑</button>';
      row.querySelector('.n').textContent = it.name;
      row.querySelectorAll('[data-act]').forEach(function (b) {
        b.addEventListener('click', function () { onAct(b.dataset.act, it.name); });
      });
      box.appendChild(row);
    });
  }

  async function refresh() {
    try {
      var rc = await fetch('/api/lotes/current', { cache: 'no-store' });
      var dc = await rc.json(); state.current = (dc && dc.name) || null;
    } catch (e) { state.current = null; }
    try {
      var rl = await fetch('/api/lotes', { cache: 'no-store' });
      var dl = await rl.json(); state.all = Array.isArray(dl) ? dl : [];
    } catch (e) { state.all = []; }
    render();
  }

  async function post(url) {
    var r = await fetch(url, { method: 'POST' });
    return await r.json();
  }

  async function onAct(act, name) {
    if (state.busy) return;
    state.busy = true;
    try {
      if (act === 'open') {
        setMsg('Abriendo "' + name + '"…');
        var d = await post('/api/lotes/open?name=' + encodeURIComponent(name));
        setMsg(d && d.ok ? '✓ Abierto: ' + name : '✕ No se pudo abrir.', d && d.ok ? 'ok' : 'err');
      } else if (act === 'del') {
        if (!confirm('¿Borrar el lote "' + name + '"? No se puede deshacer.')) { state.busy = false; return; }
        setMsg('Borrando "' + name + '"…');
        var dd = await post('/api/lotes/delete?name=' + encodeURIComponent(name));
        setMsg(dd && dd.ok ? '✓ Borrado: ' + name
          : '✕ No se pudo borrar (¿está abierto? cerralo primero).', dd && dd.ok ? 'ok' : 'err');
      }
      await refresh();
    } catch (e) { setMsg('✕ ' + e.message, 'err'); }
    state.busy = false;
  }

  // continuar
  $('btnContinuar').addEventListener('click', function () {
    var nm = this.dataset.name; if (nm) onAct('open', nm);
  });

  // cerrar
  $('btnClose').addEventListener('click', async function () {
    if (state.busy) return; state.busy = true;
    setMsg('Cerrando lote…');
    try { var d = await post('/api/lotes/close'); setMsg(d && d.ok ? '✓ Lote cerrado' : '✕ No se pudo cerrar.', d && d.ok ? 'ok' : 'err'); await refresh(); }
    catch (e) { setMsg('✕ ' + e.message, 'err'); }
    state.busy = false;
  });

  // crear
  async function crear() {
    var nm = ($('newName').value || '').trim();
    if (!nm) { setMsg('Poné un nombre para el lote.', 'err'); return; }
    if (state.busy) return; state.busy = true;
    setMsg('Creando "' + nm + '"…');
    try {
      var d = await post('/api/lotes/create?name=' + encodeURIComponent(nm));
      setMsg(d && d.ok ? '✓ Creado y abierto: ' + nm : '✕ No se pudo crear (¿nombre repetido?).', d && d.ok ? 'ok' : 'err');
      if (d && d.ok) $('newName').value = '';
      await refresh();
    } catch (e) { setMsg('✕ ' + e.message, 'err'); }
    state.busy = false;
  }
  $('btnCreate').addEventListener('click', crear);
  $('newName').addEventListener('keydown', function (e) { if (e.key === 'Enter') crear(); });

  refresh();
})();
