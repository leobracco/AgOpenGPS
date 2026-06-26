// ============================================================================
// vistax-insumo.js — controla el tab "Insumo & calibración" de vistax.html.
// No depende de vistax.js (el monitor live). Lo carga la página aparte.
//
//   · Dropdown del insumo activo (POST /api/insumos/activo)
//   · Slider del límite del sensor (PUT /api/vistax/implemento parcial)
//   · Botones "Detectar densidad" y "Configurar modo flujo" → modal con
//     polling a /api/vistax/calibrar/state cada 250 ms hasta que termine
//     la ventana de 5 s. Apply persiste al InsumoCatalogService.
// ============================================================================
(function () {
  'use strict';

  var POLL_MS = 250;
  var pollTimer = null;
  var modoActual = 'objetivo';

  function $(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------- catálogo
  async function loadInsumos() {
    try {
      var r = await fetch('/api/insumos');
      var cat = await r.json();
      var sel = $('vxInsumoSel');
      if (!sel || !cat || !Array.isArray(cat.items)) return;
      sel.innerHTML = '<option value="">— Sin insumo activo —</option>' +
        cat.items.map(function (it) {
          var sel = (it.id === cat.activo_id) ? ' selected' : '';
          return '<option value="' + esc(it.id) + '"' + sel + '>' + esc(it.nombre || it.id) + '</option>';
        }).join('');
      renderInsumoMeta(cat);
    } catch (e) { console.warn('[vx-insumo] insumos:', e); }
  }

  function renderInsumoMeta(cat) {
    var box = $('vxInsumoMeta');
    if (!box) return;
    var activo = (cat.items || []).find(function (x) { return x.id === cat.activo_id; });
    if (!activo) { box.innerHTML = '<em>Ningún insumo activo. Elegí uno para que VistaX use sus densidades.</em>'; return; }
    var parts = [];
    if (activo.densidad_objetivo_sem_m) parts.push('Objetivo: <strong>' + activo.densidad_objetivo_sem_m + ' sem/m</strong>');
    if (activo.densidad_asumida_saturado_sem_m) parts.push('Modo flujo: <strong>' + activo.densidad_asumida_saturado_sem_m + ' sem/m</strong>');
    if (activo.singulacion_objetivo_pct) parts.push('Singulación obj: <strong>' + activo.singulacion_objetivo_pct + ' %</strong>');
    if (activo.cultivo) parts.push('Cultivo: ' + esc(activo.cultivo));
    box.innerHTML = parts.join(' · ');
  }

  async function setInsumoActivo(id) {
    try {
      await fetch('/api/insumos/activo', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: id || '' })
      });
      await loadInsumos();
    } catch (e) { console.warn('[vx-insumo] setActivo:', e); }
  }

  // ---------------------------------------------------------------- maxSensor
  async function loadMaxSensor() {
    try {
      var r = await fetch('/api/vistax/implemento');
      var dto = await r.json();
      var max = (dto && dto.implemento && dto.implemento.setup && dto.implemento.setup.max_densidad_sensor) || 20;
      var input = $('vxMaxSensor');
      if (input) input.value = max;
      var lbl = $('vxMaxSensorLabel');
      if (lbl) lbl.textContent = String(max);
    } catch (e) { console.warn('[vx-insumo] maxSensor load:', e); }
  }

  async function saveMaxSensor() {
    try {
      var r = await fetch('/api/vistax/implemento');
      var dto = await r.json();
      if (!dto || !dto.implemento) return;
      var imp = dto.implemento;
      if (!imp.setup) imp.setup = {};
      var val = parseFloat($('vxMaxSensor').value);
      if (!(val > 0 && val < 500)) { alert('Valor inválido.'); return; }
      imp.setup.max_densidad_sensor = val;
      await fetch('/api/vistax/implemento', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(imp)
      });
      var lbl = $('vxMaxSensorLabel');
      if (lbl) lbl.textContent = String(val);
      flash($('vxSaveMaxSensor'), 'Guardado');
    } catch (e) { console.warn('[vx-insumo] saveMax:', e); }
  }

  function flash(btn, txt) {
    if (!btn) return;
    var orig = btn.textContent;
    btn.textContent = txt;
    setTimeout(function () { btn.textContent = orig; }, 1100);
  }

  // ---------------------------------------------------------------- calibración
  async function startCalib(modo) {
    modoActual = modo;
    var sel = $('vxInsumoSel');
    var id = sel ? sel.value : '';
    if (!id) {
      alert('No hay insumo activo. Elegí uno antes de calibrar.');
      return;
    }
    try {
      var r = await fetch('/api/vistax/calibrar/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ insumo_id: id, modo: modo, segundos: 5 })
      });
      var res = await r.json();
      if (!res || res.ok === false) {
        alert('No se pudo iniciar la calibración: ' + (res && res.error ? res.error : 'error'));
        return;
      }
    } catch (e) {
      alert('Error al iniciar: ' + e);
      return;
    }
    openModal(modo);
    startPolling();
  }

  function openModal(modo) {
    var modal = $('vxCalibModal');
    if (!modal) return;
    modal.style.display = 'flex';
    $('vxCalibTitle').textContent = modo === 'saturado'
      ? 'Configurando modo flujo…'
      : 'Detectando densidad…';
    var sel = $('vxInsumoSel');
    var nombre = sel && sel.options[sel.selectedIndex] ? sel.options[sel.selectedIndex].text : '';
    $('vxCalibInsumoLabel').textContent = 'Insumo: ' + nombre;
    $('vxCalibLive').textContent = '—';
    $('vxCalibTimer').textContent = '5.0';
    $('vxCalibSamples').textContent = '0';
    $('vxCalibSurcos').textContent = '0';
    $('vxCalibSatPill').style.display = 'none';
    $('vxCalibResult').style.display = 'none';
    $('vxCalibApply').style.display = 'none';
    $('vxCalibCancel').textContent = 'Cancelar';
  }

  function closeModal() {
    var modal = $('vxCalibModal');
    if (modal) modal.style.display = 'none';
    stopPolling();
  }

  function startPolling() {
    stopPolling();
    pollTimer = setInterval(pollState, POLL_MS);
    pollState();
  }
  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  }

  async function pollState() {
    try {
      var r = await fetch('/api/vistax/calibrar/state');
      var s = await r.json();
      if (!s) return;
      $('vxCalibLive').textContent = (s.sem_m_actual != null) ? s.sem_m_actual.toFixed(1) : '—';
      $('vxCalibTimer').textContent = s.running ? (s.segundos_restantes || 0).toFixed(1) : '0.0';
      $('vxCalibSamples').textContent = s.muestras || 0;
      $('vxCalibSurcos').textContent = (s.surcos && s.surcos.length) || 0;
      $('vxCalibSatPill').style.display = s.saturado ? '' : 'none';
      $('vxCalibSatPill').className = 'pill ' + (s.saturado ? 'warn' : 'ok');

      if (!s.running && s.listo_para_aplicar) {
        stopPolling();
        mostrarResultado(s);
      }
    } catch (e) { console.warn('[vx-insumo] state:', e); }
  }

  function mostrarResultado(s) {
    var box = $('vxCalibResult');
    var lbl = $('vxCalibResultLabel');
    var input = $('vxCalibOverride');
    var btnApply = $('vxCalibApply');

    var modo = s.modo || modoActual;
    var valor = s.valor_final_sem_m || 0;
    var saturado = !!s.saturado;

    // Si el operario pidió "objetivo" pero el sensor saturó, le proponemos
    // cambiar a modo flujo automáticamente (escribe en otro campo del insumo).
    if (modo !== 'saturado' && saturado) {
      modo = 'saturado';
      modoActual = 'saturado';
    }

    if (modo === 'saturado') {
      lbl.innerHTML = 'Sensor saturado a <strong>' + valor.toFixed(1) + ' sem/m</strong>. ' +
        'Asumimos esto como densidad cuando hay flujo constante. ' +
        'Podés editar el número antes de guardar (típico: 80–120 soja, 6–10 maíz).';
      // Si no hay valor confiable porque el sensor satura, sugerimos 90 default.
      input.value = valor > 0 ? valor.toFixed(1) : '90';
    } else {
      lbl.innerHTML = 'Densidad detectada: <strong>' + valor.toFixed(1) + ' sem/m</strong>. ' +
        '¿Guardar como densidad objetivo del insumo?';
      input.value = valor.toFixed(1);
    }
    box.style.display = '';
    btnApply.style.display = '';
    btnApply.textContent = modo === 'saturado' ? 'Guardar modo flujo' : 'Guardar densidad objetivo';
    $('vxCalibCancel').textContent = 'Descartar';
  }

  async function applyCalib() {
    var input = $('vxCalibOverride');
    var val = parseFloat(input.value);
    if (!(val > 0)) { alert('Valor inválido.'); return; }
    try {
      var r = await fetch('/api/vistax/calibrar/apply', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ aceptar: true, valor_override: val })
      });
      var res = await r.json();
      if (!res || !res.ok) {
        alert('No se pudo guardar: ' + (res && res.error ? res.error : 'error'));
        return;
      }
    } catch (e) { alert('Error: ' + e); return; }
    closeModal();
    await loadInsumos();
  }

  async function cancelCalib() {
    try { await fetch('/api/vistax/calibrar/cancel', { method: 'POST' }); } catch (e) {}
    closeModal();
  }

  // ---------------------------------------------------------------- util
  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  // ---------------------------------------------------------------- wire
  document.addEventListener('DOMContentLoaded', function () {
    var sel = $('vxInsumoSel');
    if (sel) sel.addEventListener('change', function () { setInsumoActivo(sel.value); });

    var bSave = $('vxSaveMaxSensor');
    if (bSave) bSave.addEventListener('click', saveMaxSensor);

    var bObj = $('vxBtnCalibObjetivo');
    if (bObj) bObj.addEventListener('click', function () { startCalib('objetivo'); });
    var bSat = $('vxBtnCalibSaturado');
    if (bSat) bSat.addEventListener('click', function () { startCalib('saturado'); });
    var bCancel = $('vxCalibCancel');
    if (bCancel) bCancel.addEventListener('click', cancelCalib);
    var bApply = $('vxCalibApply');
    if (bApply) bApply.addEventListener('click', applyCalib);

    loadInsumos();
    loadMaxSensor();
  });
})();
