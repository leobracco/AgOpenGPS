// ============================================================================
// widget-quantix.js — overlay HTML del widget QuantiX (220x240) en pantalla
// principal de PilotX. Diseño según mockup quantix_agro_parallel_widget_220x240.
//
// Conversa con:
//   GET  /api/widget-quantix/state              (poll cada 500ms)
//   POST /api/widget-quantix/manual             (MAN/AUTO + dosis por motor)
//   POST /api/widget-quantix/manual-all         (MAN/AUTO + dosis GLOBAL)
//
// Reglas de la UI:
//   · La lista muestra TODOS los motores de TODOS los nodos habilitados
//     (scrollea si no entran). Cada fila se colorea por desvío real vs
//     objetivo: verde |desv| ≤ 4%, ámbar baja, rojo alta, gris sin datos.
//   · Tabs AUTO/MAN del header = modo GLOBAL (todos los motores), con
//     confirmación. El stepper −/valor/+ es la dosis global: habilitado solo
//     si todos están en MAN y comparten unidad (kg/ha vs sem/m); con unidades
//     mezcladas la dosis pareja no tiene sentido y queda deshabilitada.
//   · Manual POR MOTOR: tap en la fila → modal. En AUTO ofrece pasar a MAN;
//     en MAN ofrece volver a AUTO o editar su dosis con el keypad.
//   · Al pasar a MAN, si manual_dosis está en 0, se inicializa con
//     dosis_fija_config (la dosis "fuera de mapa" configurada en el Hub).
//   · Al pasar a AUTO no se pierde el valor manual: queda persistido en
//     quantiX_motores.json y reaparece la próxima vez que se vuelva a MAN.
//   · Keypad numérico propio (el teclado global de keyboard.js mide 320px y
//     taparía el widget entero).
// ============================================================================
(function () {
  'use strict';

  const POLL_MS = 500;
  const DEV_PCT = 4; // umbral de desvío verde/ámbar/rojo

  const $ = (id) => document.getElementById(id);
  const onlineDot = $('onlineDot');
  const nodoNombreEl = $('nodoNombre');
  const mainDose = $('mainDose');
  const mainUnit = $('mainUnit');
  const autoBtn = $('autoBtn');
  const manualBtn = $('manualBtn');
  const manualCtl = $('manualCtl');
  const gDn = $('gDn');
  const gUp = $('gUp');
  const gDose = $('gDose');
  const motorCountEl = $('motorCount');
  const targetDoseEl = $('targetDose');
  const realAverageEl = $('realAverage');
  const motorList = $('motorList');
  const emptyMsg = $('emptyMsg');
  const modeText = $('modeText');

  // Modal (Confirmar / [Editar dosis] / Cancelar) -----------------------------
  const modalBackdrop = $('modalBackdrop');
  const modalTitle = $('modalTitle');
  const modalMsg = $('modalMsg');
  const modalOk = $('modalOk');
  const modalAlt = $('modalAlt');
  const modalCancel = $('modalCancel');
  let modalResolver = null;
  // Devuelve 'ok' | 'alt' | false. altText null → modal de 2 botones.
  function ask(title, msg, okText, altText) {
    modalTitle.textContent = title;
    modalMsg.textContent = msg;
    modalOk.textContent = okText || 'Confirmar';
    modalAlt.hidden = !altText;
    if (altText) modalAlt.textContent = altText;
    modalBackdrop.classList.add('show');
    return new Promise((res) => { modalResolver = res; });
  }
  function settleModal(v) {
    modalBackdrop.classList.remove('show');
    if (modalResolver) { modalResolver(v); modalResolver = null; }
  }
  modalOk.addEventListener('click', () => settleModal('ok'));
  modalAlt.addEventListener('click', () => settleModal('alt'));
  modalCancel.addEventListener('click', () => settleModal(false));

  // Estado local -------------------------------------------------------------
  let state = { nodos: [], connected: false };
  // Snapshot derivado global (se recalcula en cada render).
  let gState = { allMan: false, allAuto: true, sameUnit: true, uniformDose: null, unidad: 'kg_ha' };
  // Keypad: qué estamos editando (motor puntual o dosis global).
  let editing = { global: false, uid: null, idx: -1, value: '' };

  const keypadBackdrop = $('keypadBackdrop');
  const keypadTitle = $('keypadTitle');
  const keypadDisplay = $('keypadDisplay');
  const keypadGrid = $('keypadGrid');

  // Helpers ------------------------------------------------------------------
  function unidadLabel(u) { return (u === 'sem_m') ? 'sem/m' : 'kg/ha'; }

  // Paso adaptativo según dosis actual (espejo de AdaptiveStep.cs).
  function doseStep(value) {
    const v = Math.abs(value || 0);
    if (v < 5) return 0.1;
    if (v < 30) return 0.5;
    if (v < 100) return 1;
    if (v < 500) return 5;
    return 10;
  }
  function doseDecimals(value) {
    const v = Math.abs(value || 0);
    if (v < 10) return 1;
    return 0;
  }
  function fmt(value, decimals) {
    if (value == null || !isFinite(value)) return '—';
    if (decimals == null) decimals = doseDecimals(value);
    return Number(value).toFixed(decimals);
  }
  function escapeHtml(s) {
    return String(s || '').replace(/[&<>]/g, (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
  }
  function escapeAttr(s) {
    return String(s || '').replace(/"/g, '&quot;');
  }

  // Motores aplanados de todos los nodos, con numeración 1..N para el badge.
  function allMotores() {
    const out = [];
    let num = 1;
    (state.nodos || []).forEach((n) => (n.motores || []).forEach((m) => {
      out.push({ nodoUid: n.uid, nodoOnline: !!n.online, num: num++, m });
    }));
    return out;
  }

  // Estado del desvío real vs objetivo de un motor.
  function devStatus(m) {
    if (!m.activo || !(m.objetivo > 0))
      return { st: 'off', label: '—', color: 'var(--idle)', bar: 0, variation: '—' };
    const diff = (m.real - m.objetivo) / m.objetivo * 100;
    const variation = (diff >= 0 ? '+' : '') + diff.toFixed(1) + '%';
    if (diff > DEV_PCT)
      return { st: 'high', label: 'Alta', color: 'var(--red)', bar: 100, variation };
    if (diff < -DEV_PCT)
      return { st: 'low', label: 'Baja', color: 'var(--amber)', bar: Math.max(8, 100 + diff), variation };
    return { st: 'ok', label: 'OK', color: 'var(--green)', bar: Math.min(100, Math.max(8, 100 + diff)), variation };
  }

  // Render -------------------------------------------------------------------
  function render() {
    const flat = allMotores();
    const nodos = state.nodos || [];

    if (flat.length === 0) {
      motorList.innerHTML = '';
      emptyMsg.hidden = false;
      nodoNombreEl.textContent = 'QuantiX';
      onlineDot.classList.remove('on');
      mainDose.textContent = '—';
      mainUnit.textContent = '';
      motorCountEl.textContent = '0';
      targetDoseEl.textContent = '—';
      realAverageEl.textContent = '—';
      modeText.textContent = 'AUTO';
      modeText.className = 'badge auto';
      autoBtn.classList.remove('active');
      manualBtn.classList.remove('active');
      manualCtl.classList.remove('enabled');
      gDose.textContent = '—';
      return;
    }
    emptyMsg.hidden = true;

    // Header: nombre del nodo (o cantidad si hay varios) + online.
    nodoNombreEl.textContent = nodos.length === 1
      ? (nodos[0].nombre || 'QuantiX')
      : 'QuantiX ×' + nodos.length;
    const allOnline = nodos.every((n) => n.online);
    onlineDot.classList.toggle('on', allOnline);

    // Derivados globales.
    const motores = flat.map((f) => f.m);
    const allMan = motores.every((x) => x.manual_mode);
    const allAuto = motores.every((x) => !x.manual_mode);
    const unidad = motores[0].unidad;
    const sameUnit = motores.every((x) => x.unidad === unidad);
    let uniformDose = null;
    if (allMan && sameUnit) {
      const d0 = motores[0].manual_dosis;
      if (motores.every((x) => Math.abs(x.manual_dosis - d0) < 0.001)) uniformDose = d0;
    }
    gState = { allMan, allAuto, sameUnit, uniformDose, unidad };

    // Dosis principal: la manual uniforme si todos en MAN, sino el promedio
    // de objetivos (solo tiene sentido con unidad única).
    const avgObj = motores.reduce((a, x) => a + (x.objetivo || 0), 0) / motores.length;
    const avgReal = motores.reduce((a, x) => a + (x.real || 0), 0) / motores.length;
    if (sameUnit) {
      mainDose.textContent = fmt(allMan && uniformDose != null ? uniformDose : avgObj);
      mainUnit.textContent = unidadLabel(unidad);
    } else {
      mainDose.textContent = '—';
      mainUnit.textContent = 'mixto';
    }

    // Tabs + stepper global.
    autoBtn.classList.toggle('active', allAuto);
    manualBtn.classList.toggle('active', allMan);
    const doseEnabled = allMan && sameUnit;
    manualCtl.classList.toggle('enabled', doseEnabled);
    gDose.textContent = doseEnabled ? fmt(uniformDose) : '—';

    // Summary.
    motorCountEl.textContent = String(flat.length);
    targetDoseEl.textContent = sameUnit ? fmt(avgObj) : '—';
    realAverageEl.textContent = sameUnit ? fmt(avgReal) : '—';

    // Footer badge.
    const mixed = !allMan && !allAuto;
    modeText.textContent = allMan ? 'MAN' : (mixed ? 'MIX' : 'AUTO');
    modeText.className = 'badge' + (allMan ? '' : (mixed ? ' mix' : ' auto'));

    // Lista de motores.
    motorList.innerHTML = flat.map((f) => {
      const m = f.m;
      const s = devStatus(m);
      const nombre = escapeHtml(m.nombre) + (m.manual_mode ? ' · MAN' : '');
      return (
        '<article class="motor" style="--status:' + s.color + ';--bar:' + s.bar + '%" ' +
          'data-uid="' + escapeAttr(f.nodoUid) + '" data-idx="' + m.idx + '">' +
          '<div class="id">' + f.num + '</div>' +
          '<div>' +
            '<div class="m-title">' +
              '<span class="n">' + nombre + '</span>' +
              '<span class="state">' + s.label + '</span>' +
            '</div>' +
            '<div class="bar"><div class="fill"></div></div>' +
            '<div class="data">' +
              '<span>OBJ <b>' + fmt(m.objetivo) + '</b></span>' +
              '<span>R <b>' + fmt(m.real) + '</b></span>' +
              '<span>RPM <b>' + (m.rpm != null ? m.rpm : '—') + '</b></span>' +
              '<span>' + unidadLabel(m.unidad) + '</span>' +
            '</div>' +
          '</div>' +
          '<div class="var">' + s.variation + '<small>desv</small></div>' +
        '</article>'
      );
    }).join('');
  }

  // Handlers global (tabs + stepper) ------------------------------------------
  async function setGlobalMode(manual) {
    if (manual && gState.allMan) return;
    if (!manual && gState.allAuto) return;
    const r = await ask(
      manual ? 'TODOS a MANUAL' : 'TODOS a AUTOMÁTICO',
      manual
        ? '¿Confirmás MAN en todos los motores? Se sobreescribe la dosis del mapa.'
        : '¿Confirmás AUTO en todos? Vuelven a dosis del mapa / configuración.',
      'Confirmar', null
    );
    if (r !== 'ok') return;
    await sendManualAll(manual, 0);
  }
  autoBtn.addEventListener('click', () => setGlobalMode(false));
  manualBtn.addEventListener('click', () => setGlobalMode(true));

  async function stepGlobal(dir) {
    if (!gState.allMan || !gState.sameUnit || gState.uniformDose == null) return;
    const cur = gState.uniformDose;
    const next = Math.max(0, cur + dir * doseStep(cur));
    await sendManualAll(true, Math.round(next * 10) / 10);
  }
  gDn.addEventListener('click', () => stepGlobal(-1));
  gUp.addEventListener('click', () => stepGlobal(+1));
  gDose.addEventListener('click', () => {
    if (!gState.allMan || !gState.sameUnit) return;
    const cur = gState.uniformDose != null ? gState.uniformDose : 0;
    editing = { global: true, uid: null, idx: -1, value: fmt(cur, doseDecimals(cur)) };
    keypadTitle.textContent = 'Dosis TODOS (' + unidadLabel(gState.unidad) + ')';
    keypadDisplay.textContent = editing.value;
    keypadBackdrop.classList.add('show');
  });

  // Handlers por motor (tap en la fila) ---------------------------------------
  motorList.addEventListener('click', async (ev) => {
    const row = ev.target.closest('.motor');
    if (!row) return;
    const uid = row.getAttribute('data-uid');
    const idx = parseInt(row.getAttribute('data-idx'), 10);
    const motor = findMotor(uid, idx);
    if (!motor) return;

    if (!motor.manual_mode) {
      const r = await ask(
        escapeText(motor.nombre) + ' a MANUAL',
        '¿Confirmás MAN? Vas a sobreescribir la dosis del mapa.',
        'Confirmar', null
      );
      if (r !== 'ok') return;
      // Dosis inicial: respeta manual_dosis persistido; si está en 0 usa la
      // dosis_fija_config (fuera de mapa default).
      const dosis = motor.manual_dosis > 0 ? motor.manual_dosis : (motor.dosis_fija_config || 0);
      await sendManual(uid, idx, true, dosis);
    } else {
      const r = await ask(
        escapeText(motor.nombre) + ' (MAN)',
        'Dosis manual: ' + fmt(motor.manual_dosis) + ' ' + unidadLabel(motor.unidad),
        'Volver a AUTO', 'Editar dosis'
      );
      if (r === 'ok') {
        await sendManual(uid, idx, false, 0);
      } else if (r === 'alt') {
        openKeypad(uid, idx, motor);
      }
    }
  });
  function escapeText(s) { return String(s || 'Motor'); }

  function findMotor(uid, idx) {
    const n = (state.nodos || []).find((x) => x.uid === uid);
    if (!n) return null;
    return (n.motores || []).find((m) => m.idx === idx);
  }

  // ---- Keypad in-widget ----------------------------------------------------
  function openKeypad(uid, idx, motor) {
    const cur = motor.manual_dosis > 0 ? motor.manual_dosis : (motor.dosis_fija_config || 0);
    editing = { global: false, uid, idx, value: fmt(cur, doseDecimals(cur)) };
    keypadTitle.textContent = (motor.nombre || ('Motor ' + idx)) + ' · dosis (' + unidadLabel(motor.unidad) + ')';
    keypadDisplay.textContent = editing.value;
    keypadBackdrop.classList.add('show');
  }
  function closeKeypad() {
    keypadBackdrop.classList.remove('show');
    editing = { global: false, uid: null, idx: -1, value: '' };
  }
  function keypadPress(k) {
    if (k === 'cancel') { closeKeypad(); return; }
    if (k === 'ok') {
      const raw = (editing.value || '').replace(',', '.');
      const v = parseFloat(raw);
      const wasGlobal = editing.global;
      const uid = editing.uid, idx = editing.idx;
      closeKeypad();
      if (!isFinite(v) || v < 0) { render(); return; }
      if (wasGlobal) sendManualAll(true, v);
      else sendManual(uid, idx, true, v);
      return;
    }
    if (k === 'back') {
      if (editing.value.length > 0) editing.value = editing.value.slice(0, -1);
      if (editing.value === '') editing.value = '0';
    } else if (k === '.') {
      if (editing.value.indexOf('.') < 0) {
        editing.value = (editing.value === '' ? '0' : editing.value) + '.';
      }
    } else {
      // Dígito
      if (editing.value === '0') editing.value = k;
      else editing.value = (editing.value || '') + k;
    }
    keypadDisplay.textContent = editing.value;
  }
  keypadGrid.addEventListener('click', (ev) => {
    const b = ev.target.closest('.kp');
    if (!b) return;
    keypadPress(b.getAttribute('data-k'));
  });
  keypadBackdrop.addEventListener('click', (ev) => {
    const b = ev.target.closest('.keypad-actions .kp');
    if (!b) return;
    keypadPress(b.getAttribute('data-k'));
  });

  // Backend I/O -------------------------------------------------------------
  async function refresh() {
    try {
      const r = await fetch('/api/widget-quantix/state', { cache: 'no-store' });
      if (!r.ok) return;
      const j = await r.json();
      if (!j || !j.ok) return;
      state = j;
      render();
    } catch (e) { /* ignorar — el next tick reintenta */ }
  }

  async function sendManual(uid, motorIdx, manual, dosis) {
    try {
      const r = await fetch('/api/widget-quantix/manual', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ uid, motor_idx: motorIdx, manual, dosis: dosis || 0 })
      });
      if (!r.ok) return;
      await refresh(); // refresh inmediato, no esperar al próximo poll
    } catch (e) { /* ignorar */ }
  }

  async function sendManualAll(manual, dosis) {
    try {
      const r = await fetch('/api/widget-quantix/manual-all', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ manual, dosis: dosis || 0 })
      });
      if (!r.ok) return;
      await refresh();
    } catch (e) { /* ignorar */ }
  }

  // Init --------------------------------------------------------------------
  refresh();
  setInterval(refresh, POLL_MS);
})();
