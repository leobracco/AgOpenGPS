// ============================================================================
// tracks.js — Gestor de guías (reemplazo de FormBuildTracks).
// CRUD de tracks: lista con toggle visibilidad, select, delete, duplicate,
// rename, move up/down, swap AB, use/cancel. Sin poll.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/tracks';

  var listEl = document.getElementById('trackList');
  var btnMoveUp = document.getElementById('btnMoveUp');
  var btnMoveDn = document.getElementById('btnMoveDn');
  var btnSwapAB = document.getElementById('btnSwapAB');
  var btnToggleAll = document.getElementById('btnToggleAll');
  var btnDuplicate = document.getElementById('btnDuplicate');
  var btnRename = document.getElementById('btnRename');
  var btnDelete = document.getElementById('btnDelete');
  var btnCancel = document.getElementById('btnCancel');
  var btnUse = document.getElementById('btnUse');

  var inputPane = document.getElementById('inputPane');
  var inpName = document.getElementById('inpName');
  var btnInputOk = document.getElementById('btnInputOk');
  var btnInputCancel = document.getElementById('btnInputCancel');

  var state = {};
  var closed = false;
  var inputAction = null; // 'duplicate' | 'rename'
  var allVisible = true;

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      return await res.json();
    } catch (e) { return null; }
  }

  function apply(s) {
    if (!s) return;
    state = s;
    renderList();
  }

  function renderList() {
    listEl.innerHTML = '';
    var tracks = state.tracks || [];
    for (var i = 0; i < tracks.length; i++) {
      var t = tracks[i];
      var item = document.createElement('div');
      item.className = 'tk-item' + (i === state.selected_idx ? ' selected' : '');
      item.dataset.idx = i;

      var vis = document.createElement('div');
      vis.className = 'tk-vis ' + (t.is_visible ? 'on' : 'off');
      vis.dataset.idx = i;
      vis.addEventListener('click', onToggleVis);

      var mode = document.createElement('span');
      mode.className = 'tk-mode';
      mode.textContent = t.mode;

      var name = document.createElement('span');
      name.className = 'tk-name';
      name.textContent = t.name;

      item.appendChild(vis);
      item.appendChild(mode);
      item.appendChild(name);

      if (t.is_active) {
        var act = document.createElement('span');
        act.className = 'tk-active';
        act.textContent = '● activa';
        item.appendChild(act);
      }

      item.addEventListener('click', onSelect);
      listEl.appendChild(item);
    }
  }

  function onToggleVis(ev) {
    ev.stopPropagation();
    var idx = parseInt(ev.currentTarget.dataset.idx);
    post('/toggle-visibility', { index: idx }).then(apply);
  }

  function onSelect(ev) {
    var el = ev.currentTarget;
    var idx = parseInt(el.dataset.idx);
    post('/select', { index: idx }).then(apply);
  }

  btnMoveUp.addEventListener('click', function () { post('/move-up').then(apply); });
  btnMoveDn.addEventListener('click', function () { post('/move-down').then(apply); });
  btnSwapAB.addEventListener('click', function () { post('/swap-ab').then(apply); });
  btnDelete.addEventListener('click', function () { post('/delete').then(apply); });

  btnToggleAll.addEventListener('click', function () {
    allVisible = !allVisible;
    post('/toggle-all', { visible: allVisible }).then(apply);
  });

  btnDuplicate.addEventListener('click', function () {
    if (state.selected_idx < 0) return;
    var tracks = state.tracks || [];
    if (state.selected_idx >= tracks.length) return;
    inputAction = 'duplicate';
    inpName.value = tracks[state.selected_idx].name + ' Copia';
    inputPane.classList.add('show');
  });

  btnRename.addEventListener('click', function () {
    if (state.selected_idx < 0) return;
    var tracks = state.tracks || [];
    if (state.selected_idx >= tracks.length) return;
    inputAction = 'rename';
    inpName.value = tracks[state.selected_idx].name;
    inputPane.classList.add('show');
  });

  btnInputOk.addEventListener('click', function () {
    var name = inpName.value.trim();
    inputPane.classList.remove('show');
    if (inputAction === 'duplicate') post('/duplicate', { name: name }).then(apply);
    else if (inputAction === 'rename') post('/rename', { name: name }).then(apply);
    inputAction = null;
  });

  btnInputCancel.addEventListener('click', function () {
    inputPane.classList.remove('show');
    inputAction = null;
  });

  function closeWidget(endpoint) {
    if (closed) return;
    closed = true;
    try { navigator.sendBeacon(API + endpoint, '{}'); } catch (e) { }
    try {
      if (window.chrome && window.chrome.webview)
        window.chrome.webview.postMessage('close-hub');
    } catch (e) { }
  }

  btnUse.addEventListener('click', function () { closeWidget('/use'); });
  btnCancel.addEventListener('click', function () { closeWidget('/cancel'); });
  window.addEventListener('pagehide', function () { closeWidget('/use'); });

  // Arranque
  (async function () {
    var data = await post('/open');
    if (data) apply(data);
  })();
})();
