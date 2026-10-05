// ============================================================================
// recpath.js — Picker y guardado de recorded paths.
// Reemplazo de FormRecordName + FormRecordPicker. Dos vistas:
//   - Picker: lista de .rec, Usar/Borrar/Apagar
//   - Save: nombre + fecha/hora al guardar una grabación
// Detecta el modo por query param ?mode=save (desde btnPathRecordStop).
// ============================================================================

(function () {
  'use strict';

  var API = '/api/recpath';

  var viewPicker = document.getElementById('viewPicker');
  var viewSave   = document.getElementById('viewSave');
  var titleText  = document.getElementById('titleText');
  var pathList   = document.getElementById('pathList');
  var msgEmpty   = document.getElementById('msgEmpty');

  var btnLoad   = document.getElementById('btnLoad');
  var btnDelete = document.getElementById('btnDelete');
  var btnOff    = document.getElementById('btnOff');

  var inpName   = document.getElementById('inpName');
  var chkDate   = document.getElementById('chkDate');
  var chkTime   = document.getElementById('chkTime');
  var btnSave   = document.getElementById('btnSave');
  var btnDiscard= document.getElementById('btnDiscard');

  var paths = [];
  var selectedIdx = -1;
  var isSaveMode = (location.search || '').indexOf('mode=save') >= 0;

  function closeWidget() {
    try {
      if (window.chrome && window.chrome.webview)
        window.chrome.webview.postMessage('close-hub');
    } catch (e) { }
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, { method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {}) });
      return await res.json();
    } catch (e) { return null; }
  }

  async function loadList() {
    try {
      var res = await fetch(API + '/list');
      var data = await res.json();
      paths = data.paths || [];
    } catch (e) { paths = []; }
    renderList();
  }

  function renderList() {
    pathList.innerHTML = '';
    selectedIdx = -1;
    msgEmpty.classList.toggle('show', paths.length === 0);
    btnLoad.disabled = true;
    btnDelete.disabled = true;

    for (var i = 0; i < paths.length; i++) {
      var item = document.createElement('div');
      item.className = 'rp-item';
      item.dataset.idx = i;

      var name = document.createElement('span');
      name.className = 'rp-name';
      name.textContent = paths[i];
      item.appendChild(name);

      item.addEventListener('click', onSelect);
      pathList.appendChild(item);
    }
  }

  function onSelect(ev) {
    var idx = parseInt(ev.currentTarget.dataset.idx);
    selectedIdx = idx;
    var items = pathList.querySelectorAll('.rp-item');
    for (var i = 0; i < items.length; i++)
      items[i].classList.toggle('selected', i === idx);
    btnLoad.disabled = false;
    btnDelete.disabled = false;
  }

  btnLoad.addEventListener('click', async function () {
    if (selectedIdx < 0 || selectedIdx >= paths.length) return;
    await post('/load', { name: paths[selectedIdx] });
    closeWidget();
  });

  btnDelete.addEventListener('click', async function () {
    if (selectedIdx < 0 || selectedIdx >= paths.length) return;
    var data = await post('/delete', { name: paths[selectedIdx] });
    if (data && data.paths) { paths = data.paths; renderList(); }
  });

  btnOff.addEventListener('click', async function () {
    await post('/off');
    closeWidget();
  });

  // ── Save mode ─────────────────────────────────────────────────────────
  btnSave.addEventListener('click', async function () {
    var name = (inpName.value || '').trim();
    if (!name) return;
    var now = new Date();
    if (chkDate.checked) name += ' ' + now.toISOString().slice(0, 10);
    if (chkTime.checked) name += ' ' + now.toTimeString().slice(0, 5).replace(':', '-');
    await post('/save', { name: name });
    closeWidget();
  });

  btnDiscard.addEventListener('click', async function () {
    await post('/discard');
    closeWidget();
  });

  // ── Arranque ──────────────────────────────────────────────────────────
  if (isSaveMode) {
    viewPicker.style.display = 'none';
    viewSave.classList.add('show');
    titleText.textContent = 'Guardar ruta grabada';
  } else {
    viewSave.style.display = 'none';
    loadList();
  }
})();
