// ============================================================================
// sectionx.js — editor de mapeo de cables → secciones AOG.
// Lee /api/sectionx/config, edita en vivo el nodo seleccionado, hace POST.
// El estado live de secciones (qué está abierta) se actualiza desde
// /api/aog/state (snapshot publicado por TelemetryHub).
// ============================================================================

(function () {
  'use strict';

  var liveEl    = document.getElementById('liveSections');
  var nodoSel   = document.getElementById('nodoSel');
  var nodoForm  = document.getElementById('nodoForm');
  var cablesEl  = document.getElementById('cablesGrid');
  var jsonOut   = document.getElementById('jsonOut');
  var btnSave   = document.getElementById('btnSave');
  var btnTest   = document.getElementById('btnTest');
  var hintEl    = document.getElementById('hint');
  var enabledChk = document.getElementById('enabledChk');

  var cfg = null;
  var selectedIdx = 0;

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function currentNode() {
    if (!cfg || !cfg.nodos || cfg.nodos.length === 0) return null;
    return cfg.nodos[Math.max(0, Math.min(selectedIdx, cfg.nodos.length - 1))];
  }

  function renderNodoSel() {
    if (!nodoSel || !cfg) return;
    var opts = (cfg.nodos || []).map(function (n, i) {
      var label = (n.nombre || 'SectionX controller') + ' · ' + (n.uid || '—');
      return '<option value="' + i + '"' + (i === selectedIdx ? ' selected' : '') + '>' + escapeHtml(label) + '</option>';
    }).join('');
    if (!opts) opts = '<option value="-1">(sin nodos configurados)</option>';
    nodoSel.innerHTML = opts;
    if (enabledChk) enabledChk.checked = !!cfg.enabled;
  }

  function renderForm() {
    var n = currentNode();
    if (!nodoForm) return;
    if (!n) {
      nodoForm.innerHTML = '<p class="subtitle">No hay nodos SectionX configurados todavía.</p>';
      cablesEl.innerHTML = '';
      jsonOut.value = '';
      return;
    }
    nodoForm.innerHTML = '' +
      '<div style="display:grid; grid-template-columns: 1fr 1fr; gap: var(--agp-sp-4)">' +
        '<div class="field"><label>Nombre</label><input data-name="nombre" type="text" value="' + escapeHtml(n.nombre || '') + '"></div>' +
        '<div class="field"><label>UID nodo</label><input data-name="uid" type="text" value="' + escapeHtml(n.uid || '') + '"></div>' +
        '<div class="field"><label>Distancia entre trenes (m)</label><input data-name="distancia_entre_trenes" type="number" step="0.05" value="' + (n.distanciaEntreTrenes ?? n.distancia_entre_trenes ?? 0) + '"></div>' +
        '<label class="switch" style="align-self:center"><input data-name="habilitado" type="checkbox" ' + (n.habilitado ? 'checked' : '') + '><span class="track"></span> Habilitado</label>' +
      '</div>';

    renderCables();
    renderJson();
  }

  function renderCables() {
    var n = currentNode();
    if (!cablesEl || !n) return;

    // Asegurar 16 cables (1..16). Si el legacy guarda 1..14, llenamos a 16.
    var byCable = {};
    (n.cables || []).forEach(function (c) { byCable[c.cable] = c; });

    var html = '<div class="cable-grid">';
    for (var i = 1; i <= 16; i++) {
      var c = byCable[i] || { cable: i, seccionAOG: 0, seccion_aog: 0, tren: 0 };
      var sec = c.seccionAOG ?? c.seccion_aog ?? 0;
      var on = sec > 0;
      html += '<div class="cable-cell' + (on ? ' on' : '') + '" data-cable="' + i + '">' +
                '<div class="cable-num">' + i + '</div>' +
                '<input type="number" min="0" max="16" data-cable-sec="' + i + '" value="' + sec + '" title="Sección AOG (0 = no asignado)">' +
                '<select data-cable-tren="' + i + '" title="Tren">' +
                  '<option value="0"' + ((c.tren ?? 0) === 0 ? ' selected' : '') + '>Tren 1</option>' +
                  '<option value="1"' + ((c.tren ?? 0) === 1 ? ' selected' : '') + '>Tren 2</option>' +
                '</select>' +
              '</div>';
      if (i === 8) html += '</div><div class="cable-grid">';
    }
    html += '</div>';

    cablesEl.innerHTML = html;
  }

  function collectFromUi() {
    var n = currentNode();
    if (!n) return;
    nodoForm.querySelectorAll('input[data-name],select[data-name]').forEach(function (el) {
      var k = el.getAttribute('data-name');
      if (el.type === 'checkbox') n[k === 'habilitado' ? 'habilitado' : k] = el.checked;
      else if (el.type === 'number') {
        var v = parseFloat(el.value);
        if (k === 'distancia_entre_trenes') n.distanciaEntreTrenes = isNaN(v) ? 0 : v;
        else n[k] = isNaN(v) ? 0 : v;
      } else {
        n[k] = el.value;
      }
    });

    // Cables: reconstruir desde la grilla
    var cables = [];
    for (var i = 1; i <= 16; i++) {
      var secEl  = cablesEl.querySelector('[data-cable-sec="' + i + '"]');
      var trenEl = cablesEl.querySelector('[data-cable-tren="' + i + '"]');
      var sec = parseInt(secEl ? secEl.value : '0', 10) || 0;
      var tren = parseInt(trenEl ? trenEl.value : '0', 10) || 0;
      if (sec > 0) cables.push({ cable: i, seccionAOG: sec, tren: tren });
    }
    n.cables = cables;
  }

  function renderJson() {
    if (!jsonOut) return;
    jsonOut.value = JSON.stringify(currentNode() || {}, null, 2);
  }

  function renderLive(snapshot) {
    if (!liveEl) return;
    var num = snapshot.numSections || 0;
    var on = snapshot.sectionOnRequest || [];
    if (num <= 0) {
      liveEl.innerHTML = '<div class="subtitle">No hay job activo en AOG.</div>';
      return;
    }
    var rows = '';
    for (var i = 0; i < num; i++) {
      var open = !!on[i];
      rows += '<div class="seg-row' + (open ? ' on' : '') + '">' +
                '<div class="name">Sec ' + (i + 1) + '</div>' +
                '<div class="state">' + (open ? 'ABIERTA' : 'CERRADA') + '</div>' +
              '</div>';
    }
    liveEl.innerHTML = rows;
  }

  async function loadCfg() {
    try {
      var res = await fetch('/api/sectionx/config', { cache: 'no-store' });
      cfg = await res.json();
      if (!cfg.nodos) cfg.nodos = [];
      // Normalizar keys snake_case → camelCase para edición en JS
      cfg.nodos.forEach(function (n) {
        if (n.distancia_entre_trenes != null && n.distanciaEntreTrenes == null)
          n.distanciaEntreTrenes = n.distancia_entre_trenes;
        (n.cables || []).forEach(function (c) {
          if (c.seccion_aog != null && c.seccionAOG == null) c.seccionAOG = c.seccion_aog;
        });
      });
      renderNodoSel();
      renderForm();
    } catch (e) {
      if (hintEl) hintEl.textContent = 'No se pudo cargar la config: ' + e.message;
    }
  }

  async function pollLive() {
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      var snap = await res.json();
      renderLive(snap);
    } catch (e) { /* offline */ }
  }

  // ----- eventos -----
  if (nodoSel) {
    nodoSel.addEventListener('change', function () {
      selectedIdx = parseInt(nodoSel.value, 10) || 0;
      renderForm();
    });
  }

  if (cablesEl) {
    cablesEl.addEventListener('input', function () {
      renderJson();
    });
  }
  if (nodoForm) {
    nodoForm.addEventListener('input', function () {
      renderJson();
    });
  }
  if (enabledChk) {
    enabledChk.addEventListener('change', function () {
      if (cfg) cfg.enabled = enabledChk.checked;
    });
  }

  if (btnSave) {
    btnSave.addEventListener('click', async function () {
      try {
        collectFromUi();
        btnSave.disabled = true;
        hintEl.textContent = 'Guardando…';
        // Convertir camelCase → snake_case en el body (las DTOs C# usan JsonPropertyName)
        var payload = JSON.parse(JSON.stringify(cfg));
        (payload.nodos || []).forEach(function (n) {
          if (n.distanciaEntreTrenes != null) {
            n.distancia_entre_trenes = n.distanciaEntreTrenes;
            delete n.distanciaEntreTrenes;
          }
          (n.cables || []).forEach(function (c) {
            if (c.seccionAOG != null) {
              c.seccion_aog = c.seccionAOG;
              delete c.seccionAOG;
            }
          });
        });
        var res = await fetch('/api/sectionx/config', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
        var ok = (await res.json()).ok;
        hintEl.textContent = ok ? 'Guardado.' : 'Error al guardar.';
      } catch (e) {
        hintEl.textContent = 'Error: ' + e.message;
      }
      btnSave.disabled = false;
    });
  }

  loadCfg();
  pollLive();
  setInterval(pollLive, 1000);
})();
