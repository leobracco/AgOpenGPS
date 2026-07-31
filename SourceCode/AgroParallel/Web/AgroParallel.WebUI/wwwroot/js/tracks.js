// ============================================================================
// tracks.js — clon del wizard FormBuildTracks (AgOpenGPS 6.8.5).
// Navegación multi-panel (una pantalla visible a la vez, ventana se
// redimensiona por paso) + lista dinámica de guías + creación AB/A+/Curva/
// Lat-Lon/Pivote contra la API /api/tracks (+ pivot en vivo de /api/aog/state).
// Wire snake_case.
// ============================================================================
(function () {
  'use strict';

  var API = '/api/tracks';
  var win = document.getElementById('tkWin');
  var title = document.getElementById('tkTitle');
  var listEl = document.getElementById('trackList');

  var state = { tracks: [], selected_idx: -1 };
  // Puntos capturados para crear AB conduciendo.
  var ptA = null, ptB = null, abHeadingDeg = 0;

  // ---- helpers HTTP ----
  async function apiGet(path) {
    try { var r = await fetch(API + path); return await r.json(); } catch (e) { return null; }
  }
  async function apiPost(path, body) {
    try {
      var r = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: body ? JSON.stringify(body) : '{}'
      });
      return await r.json();
    } catch (e) { return null; }
  }
  async function aogState() {
    try { var r = await fetch('/api/aog/state'); return await r.json(); } catch (e) { return null; }
  }

  // ---- navegación de pantallas ----
  var TITLES = {
    main: 'Guías', choose: 'Nueva guía', abline: 'AB Line', aplus: 'A+',
    curve: 'Curva', pivot: 'Pivote', latlonplus: 'Lat/Lon + Rumbo',
    latlonlatlon: 'Lat/Lon A–B', name: 'Nombre', editname: 'Editar nombre', kml: 'KML'
  };
  function show(screen) {
    var screens = win.querySelectorAll('.screen');
    for (var i = 0; i < screens.length; i++) {
      screens[i].classList.toggle('active', screens[i].getAttribute('data-screen') === screen);
    }
    win.setAttribute('data-screen', screen);
    title.textContent = TITLES[screen] || 'Guías';
    if (screen === 'main') refreshList();
  }

  // data-goto en cualquier botón → navega a esa pantalla
  document.addEventListener('click', function (e) {
    var b = e.target.closest('[data-goto]');
    if (b) { show(b.getAttribute('data-goto')); }
  });

  // ---- lista de guías (panelMain) ----
  function iconFor(mode) {
    if (mode === 'ab' || mode === 'AB') return 'TrackLine.png';
    if (mode === 'pivot' || mode === 'waterPivot') return 'TrackPivot.png';
    return 'TrackCurve.png';
  }
  function refreshList() {
    if (!listEl) return;
    listEl.innerHTML = '';
    var tracks = state.tracks || [];
    for (var i = 0; i < tracks.length; i++) {
      (function (idx) {
        var t = tracks[idx];
        var row = document.createElement('div');
        row.className = 'tk-row-item';

        var ico = document.createElement('div');
        ico.className = 'tk-ico';
        ico.innerHTML = '<img src="../img/tracks/' + iconFor(t.mode) + '">';

        var name = document.createElement('button');
        // El wire es snake_case (AgpJson): la API manda "is_visible", no
        // "visible". Con el nombre viejo t.visible daba siempre undefined
        // (falsy) - la guía quedaba con la clase "hidden" puesta SIEMPRE y
        // el guard de abajo bloqueaba el click para cualquier guía, visible
        // o no. Por eso "seleccionar una guía existente no dejaba": no era
        // un problema de visibilidad real, el campo nunca se leía.
        name.className = 'tk-name' + (idx === state.selected_idx ? ' sel' : '') + (t.is_visible ? '' : ' hidden');
        name.textContent = t.name || ('Guía ' + (idx + 1));
        name.onclick = async function () {
          if (!t.is_visible) return; // el nativo solo selecciona guías visibles
          state = await apiPost('/select', { index: idx }) || state;
          refreshList();
        };

        var vis = document.createElement('button');
        vis.className = 'tk-vis ' + (t.is_visible ? 'on' : 'off');
        vis.onclick = async function () {
          state = await apiPost('/toggle-visibility', { index: idx }) || state;
          refreshList();
        };

        row.appendChild(ico); row.appendChild(name); row.appendChild(vis);
        listEl.appendChild(row);
      })(i);
    }
  }

  async function loadState() {
    var s = await apiGet('/state');
    if (s && s.ok !== false) { state = s; refreshList(); }
  }

  // ---- botones de gestión (panelMain) ----
  function on(id, fn) { var el = document.getElementById(id); if (el) el.onclick = fn; }

  on('btnDelete', async function () { state = await apiPost('/delete') || state; refreshList(); });
  on('btnMoveUp', async function () { state = await apiPost('/move-up') || state; refreshList(); });
  on('btnMoveDn', async function () { state = await apiPost('/move-down') || state; refreshList(); });
  on('btnSwapAB', async function () { state = await apiPost('/swap-ab') || state; refreshList(); });
  var allVisible = true;
  on('btnHideShow', async function () { allVisible = !allVisible; state = await apiPost('/toggle-all', { visible: allVisible }) || state; refreshList(); });
  on('btnNewTrack', function () { show('choose'); });
  on('btnUse', async function () { await apiPost('/use'); closeWidget(); });
  on('btnCancel', async function () { await apiPost('/cancel'); closeWidget(); });

  // Renombrar → panelEditName
  on('btnEditName', function () {
    var t = (state.tracks || [])[state.selected_idx];
    document.getElementById('txtEditName').value = t ? (t.name || '') : '';
    show('editname');
  });
  on('btnSaveEditName', async function () {
    var name = document.getElementById('txtEditName').value.trim();
    state = await apiPost('/rename', { name: name }) || state;
    show('main');
  });
  on('btnAddTimeEdit', function () { addTime('txtEditName'); });

  // Duplicar → panelName con "<nombre> Copia"
  on('btnDuplicate', function () {
    var t = (state.tracks || [])[state.selected_idx];
    document.getElementById('txtName').value = (t ? (t.name || 'Guía') : 'Guía') + ' Copia';
    pendingCreate = { kind: 'duplicate' };
    show('name');
  });

  function addTime(id) {
    var el = document.getElementById(id);
    var d = new Date();
    var hh = ('0' + d.getHours()).slice(-2), mm = ('0' + d.getMinutes()).slice(-2), ss = ('0' + d.getSeconds()).slice(-2);
    el.value = (el.value + ' ' + hh + ':' + mm + ':' + ss).trim();
  }

  // ---- crear AB conduciendo (panelABLine) ----
  var refRight = true;
  function toggleRef(imgBtnId) {
    refRight = !refRight;
    var img = document.querySelector('#' + imgBtnId + ' img');
    if (img) img.src = '../img/tracks/' + (refRight ? 'BoundaryRight.png' : 'BoundaryLeft.png');
  }
  on('btnRefSideAB', function () { toggleRef('btnRefSideAB'); });
  on('btnRefSideAPlus', function () { toggleRef('btnRefSideAPlus'); });
  on('btnRefSideCurve', function () { toggleRef('btnRefSideCurve'); });

  on('btnALine', async function () {
    var s = await aogState();
    if (!s) return;
    ptA = { e: s.pivot_easting, n: s.pivot_northing };
    document.getElementById('btnBLine').disabled = false;
    document.getElementById('btnALine').disabled = true;
  });
  on('btnBLine', async function () {
    var s = await aogState();
    if (!s || !ptA) return;
    ptB = { e: s.pivot_easting, n: s.pivot_northing };
    abHeadingDeg = Math.atan2(ptB.e - ptA.e, ptB.n - ptA.n) * 180 / Math.PI;
    if (abHeadingDeg < 0) abHeadingDeg += 360;
    document.getElementById('btnEnterAB').disabled = false;
  });
  on('btnEnterAB', function () {
    pendingCreate = { kind: 'ab', heading: abHeadingDeg };
    document.getElementById('txtName').value = 'AB ' + abHeadingDeg.toFixed(1) + '°';
    show('name');
  });

  // ---- A+ (punto A + rumbo numérico) ----
  on('btnAPlusSetA', async function () {
    var s = await aogState();
    if (s) document.getElementById('nudHeadingAPlus').value = (s.heading * 180 / Math.PI).toFixed(1);
  });
  on('btnEnterAPlus', function () {
    var h = parseFloat(document.getElementById('nudHeadingAPlus').value) || 0;
    pendingCreate = { kind: 'ab', heading: h };
    document.getElementById('txtName').value = 'A+ ' + h.toFixed(1) + '°';
    show('name');
  });

  // ---- Lat/Lon + rumbo ----
  on('btnFillLatLonPlus', async function () { await fillLatLon('nudLatPlus', 'nudLonPlus'); });
  on('btnEnterLatLonPlus', function () {
    var h = parseFloat(document.getElementById('nudHeadingPlus').value) || 0;
    pendingCreate = { kind: 'ab', heading: h };
    document.getElementById('txtName').value = 'A+ ' + h.toFixed(1) + '°';
    show('name');
  });

  // ---- Lat/Lon A–B ----
  on('btnFillA', async function () { await fillLatLon('nudLatA', 'nudLonA'); });
  on('btnFillB', async function () { await fillLatLon('nudLatB', 'nudLonB'); });
  on('btnEnterLatLonLatLon', function () {
    // heading A→B en grados (aprox por lat/lon)
    var latA = parseFloat(document.getElementById('nudLatA').value) || 0;
    var lonA = parseFloat(document.getElementById('nudLonA').value) || 0;
    var latB = parseFloat(document.getElementById('nudLatB').value) || 0;
    var lonB = parseFloat(document.getElementById('nudLonB').value) || 0;
    var dLat = latB - latA, dLon = (lonB - lonA) * Math.cos((latA + latB) / 2 * Math.PI / 180);
    var h = Math.atan2(dLon, dLat) * 180 / Math.PI; if (h < 0) h += 360;
    pendingCreate = { kind: 'ab', heading: h };
    document.getElementById('txtName').value = 'AB ' + h.toFixed(1) + '°';
    show('name');
  });

  // ---- Pivote ----
  on('btnFillPivot', async function () { await fillLatLon('nudLatPivot', 'nudLonPivot'); });
  on('btnEnterPivot', function () {
    pendingCreate = { kind: 'ab', heading: 0 }; // pivote real necesita endpoint dedicado; placeholder
    document.getElementById('txtName').value = 'Piv';
    show('name');
  });

  async function fillLatLon(latId, lonId) {
    var s = await aogState();
    if (!s) return;
    document.getElementById(latId).value = (s.latitude || 0).toFixed(7);
    document.getElementById(lonId).value = (s.longitude || 0).toFixed(7);
  }

  // ---- Curva (grabar conduciendo) ----
  var recTimer = null;
  on('btnACurve', async function () {
    await apiPost('/record-curve-a');
    document.getElementById('btnPausePlay').disabled = false;
    document.getElementById('lblCurveExists').textContent = 'Grabando';
    if (recTimer) clearInterval(recTimer);
    recTimer = setInterval(async function () {
      var st = await apiGet('/record-status');
      if (st) document.getElementById('lblCurveExists').textContent = 'Grabando · ' + (st.points || 0) + ' pts';
    }, 700);
  });
  on('btnPausePlay', async function () { await apiPost('/record-curve-pause'); });
  on('btnEnterCurve', function () {
    if (recTimer) { clearInterval(recTimer); recTimer = null; }
    pendingCreate = { kind: 'curve' };
    document.getElementById('txtName').value = 'Cu';
    show('name');
  });

  // ---- panelName: confirmar creación ----
  var pendingCreate = null;
  on('btnAddTime', function () { addTime('txtName'); });
  on('btnAddName', async function () {
    var name = document.getElementById('txtName').value.trim();
    if (pendingCreate) {
      if (pendingCreate.kind === 'ab') {
        state = await apiPost('/create-ab', { heading_deg: pendingCreate.heading, name: name }) || state;
      } else if (pendingCreate.kind === 'curve') {
        state = await apiPost('/record-curve-b', { name: name }) || state;
      } else if (pendingCreate.kind === 'duplicate') {
        state = await apiPost('/duplicate', { name: name }) || state;
      }
      pendingCreate = null;
    }
    // reset botones AB
    ptA = ptB = null;
    var bA = document.getElementById('btnALine'); if (bA) bA.disabled = false;
    var bB = document.getElementById('btnBLine'); if (bB) bB.disabled = true;
    var bE = document.getElementById('btnEnterAB'); if (bE) bE.disabled = true;
    show('main');
  });

  // KML (placeholder — importación real vía picker nativo, pendiente)
  on('btnKML', function () { show('kml'); });

  // ---- cerrar ventana ----
  // El host (PilotX.Desktop) abre esta página como ventana-diálogo hija y cierra
  // esa ventana cuando navegamos a la URL centinela "pilotx-close". Fallback a
  // window.close() para cuando corre como widget/pestaña suelta.
  function closeWidget() {
    try {
      var origin = location.origin && location.origin !== 'null' ? location.origin : '';
      location.href = origin + '/pages/pilotx-close.html';
    } catch (e) {
      try { window.close(); } catch (e2) {}
    }
  }

  // NUD: al tocar, abrir teclado numérico (keyboard.js) si está disponible
  var nuds = document.querySelectorAll('.nud');
  for (var k = 0; k < nuds.length; k++) {
    (function (nud) {
      nud.addEventListener('click', function () {
        if (window.AgpKeyboard && window.AgpKeyboard.openNumeric) {
          window.AgpKeyboard.openNumeric(nud, {
            min: parseFloat(nud.getAttribute('data-min')),
            max: parseFloat(nud.getAttribute('data-max')),
            decimals: parseInt(nud.getAttribute('data-dec') || '2', 10)
          });
        }
      });
    })(nuds[k]);
  }

  // Arranque
  loadState();
})();
