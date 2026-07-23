// ============================================================================
// lote.js — clon del menú de lote FormJob (AgOpenGPS 6.8.5).
// Pantalla principal (grid 2 col de botones) + sub-pantallas Abrir (lista) y
// Nuevo (nombre). Wire contra /api/lotes:
//   GET  /api/lotes                 → lista de lotes
//   GET  /api/lotes/current         → { name }
//   POST /api/lotes/open?name=      → abrir
//   POST /api/lotes/close           → cerrar
//   POST /api/lotes/create?name=    → nuevo
//   POST /api/lotes/from-existing   → desde existente
//   POST /api/lotes/import-{kml,isoxml}
// ============================================================================
(function () {
  'use strict';

  var win = document.getElementById('ltWin');
  var title = document.querySelector('.lt-title');
  var lblResume = document.getElementById('lblResume');
  var listEl = document.getElementById('loteList');

  var current = null; // nombre del lote abierto (o null)

  function $(id) { return document.getElementById(id); }

  // ---- navegación de pantallas ----
  var TITLES = { main: 'Lote', open: 'Abrir lote', new: 'Nuevo lote' };
  function show(screen) {
    var screens = win.querySelectorAll('.screen');
    for (var i = 0; i < screens.length; i++)
      screens[i].classList.toggle('active', screens[i].getAttribute('data-screen') === screen);
    if (title) title.textContent = TITLES[screen] || 'Lote';
    if (screen === 'open') loadList();
  }
  document.addEventListener('click', function (e) {
    var b = e.target.closest('[data-goto]');
    if (b) show(b.getAttribute('data-goto'));
  });

  // ---- helpers HTTP ----
  async function jget(url) { try { var r = await fetch(url, { cache: 'no-store' }); return await r.json(); } catch (e) { return null; } }
  async function jpost(url) { try { var r = await fetch(url, { method: 'POST' }); return await r.json ? await r.json() : null; } catch (e) { return null; } }

  // ---- estado inicial: lote actual → habilita/deshabilita Cerrar/Continuar ----
  async function loadCurrent() {
    var c = await jget('/api/lotes/current');
    current = c && c.name ? c.name : null;
    if (current) {
      lblResume.textContent = 'Abierto: ' + current;
      $('btnClose').disabled = false;
      $('btnResume').disabled = true; // ya está abierto, no hay que continuar
    } else {
      $('btnClose').disabled = true;
      // Continuar usa el último lote (lo resuelve el backend). Si no hay, se
      // deja habilitado y el backend responde vacío.
      lblResume.textContent = 'Sin lote abierto';
    }
  }

  // ---- lista de lotes (Abrir) ----
  async function loadList() {
    listEl.innerHTML = '<div class="lt-item"><span class="nm">Cargando…</span></div>';
    var arr = await jget('/api/lotes');
    listEl.innerHTML = '';
    var lotes = Array.isArray(arr) ? arr : (arr && arr.lotes) || [];
    if (!lotes.length) {
      listEl.innerHTML = '<div class="lt-item"><span class="nm">No hay lotes</span></div>';
      return;
    }
    lotes.forEach(function (l) {
      var name = l.name || l.nombre || l;
      var area = (l.area_ha != null) ? (l.area_ha.toFixed(1) + ' ha') : '';
      var it = document.createElement('div');
      it.className = 'lt-item';
      it.innerHTML = '<span class="nm"></span><span class="meta">' + area + '</span>';
      it.querySelector('.nm').textContent = name;
      it.onclick = async function () {
        await jpost('/api/lotes/open?name=' + encodeURIComponent(name));
        closeWin();
      };
      listEl.appendChild(it);
    });
  }

  // ---- botones principales (FormJob) ----
  $('btnClose').onclick = async function () { await jpost('/api/lotes/close'); closeWin(); };
  $('btnOpen').onclick = function () { show('open'); };
  $('btnNew').onclick = function () { $('inpNewName').value = ''; show('new'); };
  $('btnResume').onclick = async function () {
    // Continuar: abrir el último lote. El backend resuelve "Resume".
    await jpost('/api/lotes/open?name=' + encodeURIComponent(current || '__resume__'));
    closeWin();
  };
  $('btnDriveIn').onclick = async function () {
    // Entrar al lote: abrir el más cercano a la posición GPS actual.
    var arr = await jget('/api/lotes?near=1');
    var lotes = Array.isArray(arr) ? arr : (arr && arr.lotes) || [];
    if (lotes.length) { await jpost('/api/lotes/open?name=' + encodeURIComponent(lotes[0].name || lotes[0])); closeWin(); }
    else { show('open'); } // sin cercanos → mostrar lista
  };
  $('btnFromExisting').onclick = async function () { await fetch('/api/lotes/from-existing', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' }); closeWin(); };
  $('btnFromKML').onclick = async function () { await jpost('/api/lotes/import-kml'); closeWin(); };
  $('btnFromISOXML').onclick = async function () { await jpost('/api/lotes/import-isoxml'); closeWin(); };

  // ---- crear nuevo lote ----
  $('btnCreate').onclick = async function () {
    var name = $('inpNewName').value.trim();
    if (!name) return;
    await jpost('/api/lotes/create?name=' + encodeURIComponent(name));
    closeWin();
  };

  // Teclado virtual para el nombre
  var inp = $('inpNewName');
  if (inp) inp.addEventListener('focus', function () {
    if (window.AgpKeyboard && window.AgpKeyboard.openText) window.AgpKeyboard.openText(inp);
  });

  // ---- cerrar ventana (host intercepta URL centinela) ----
  function closeWin() {
    try {
      var origin = location.origin && location.origin !== 'null' ? location.origin : '';
      location.href = origin + '/pages/pilotx-close.html';
    } catch (e) { try { window.close(); } catch (e2) {} }
  }

  loadCurrent();
})();
