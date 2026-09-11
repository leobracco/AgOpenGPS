// ============================================================================
// widget-quantix.js — overlay HTML del widget QuantiX (220x240) en pantalla
// principal de PilotX. Carrusel de motor con foco (escala a N motores).
//
// Conversa con:
//   GET  /api/widget-quantix/state              (poll cada 500ms)
//   POST /api/widget-quantix/manual             (MAN/AUTO + dosis por motor)
//   POST /api/widget-quantix/manual-all         (MAN/AUTO + dosis GLOBAL)
//
// Reglas de la UI:
//   · Zona central: UN motor grande (el enfocado) con real grande, desvío,
//     OBJ/RPM/estado, botón MAN/AUTO propio y stepper de dosis (activo en MAN).
//   · Navegación: botones ‹ › + swipe horizontal + tap en un dot. La tira de
//     dots muestra el estado de TODOS los motores (verde/ámbar/rojo/gris).
//   · Auto-salto de foco al motor en alarma de dosis, con reglas anti-molestia:
//       - solo si el desvío persiste > 3 s
//       - no salta si el operario interactuó en los últimos 8 s
//       - no salta con keypad o modal abiertos
//       - con varias alarmas va al peor |desvío|
//       - el borde del widget parpadea 2 veces al saltar
//   · Tabs AUTO/MAN del header = modo GLOBAL (todos los motores), con
//     confirmación. El stepper del header es la dosis global: habilitado solo
//     si todos están en MAN y comparten unidad (kg/ha vs sem/m).
//   · Al pasar a MAN, si manual_dosis está en 0, se inicializa con
//     dosis_fija_config. Al volver a AUTO el valor manual queda persistido.
//   · Keypad numérico propio (el teclado global de keyboard.js mide 320px y
//     taparía el widget entero).
// ============================================================================
(function () {
  'use strict';

  const POLL_MS = 500;
  const DEV_PCT = 4;          // umbral de desvío verde/ámbar/rojo
  const ALARM_PERSIST_MS = 3000;  // el desvío debe sostenerse antes de saltar
  const INTERACT_QUIET_MS = 8000; // no auto-saltar si hubo interacción reciente

  const $ = (id) => document.getElementById(id);
  const widgetEl = document.querySelector('.quantix-widget');
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
  const focusZone = $('focusZone');
  const focusCard = $('focusCard');
  const navPrev = $('navPrev');
  const navNext = $('navNext');
  const dotsBar = $('dotsBar');
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
  // Foco del carrusel: clave estable "uid#idx" (sobrevive a refreshes).
  let focusKey = null;
  // Auto-salto: primer avistaje de cada alarma + última interacción del operario.
  const alarmSince = new Map(); // key "uid#idx" → timestamp
  let lastInteract = 0;
  document.addEventListener('pointerdown', () => { lastInteract = Date.now(); }, true);

  const keypadBackdrop = $('keypadBackdrop');
  const keypadTitle = $('keypadTitle');
  const keypadDisplay = $('keypadDisplay');
  const keypadGrid = $('keypadGrid');

  // Helpers ------------------------------------------------------------------
  function unidadLabel(u) { return (u === 'sem_m') ? 'sem/m' : 'kg/ha'; }
  function keyOf(f) { return f.nodoUid + '#' + f.m.idx; }

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

  // Motores aplanados de todos los nodos, con numeración 1..N.
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
      return { st: 'off', label: '—', color: 'var(--idle)', dev: 0, variation: '—' };
    const diff = (m.real - m.objetivo) / m.objetivo * 100;
    const variation = (diff >= 0 ? '+' : '') + diff.toFixed(1) + '%';
    if (diff > DEV_PCT)
      return { st: 'high', label: 'Alta', color: 'var(--red)', dev: diff, variation };
    if (diff < -DEV_PCT)
      return { st: 'low', label: 'Baja', color: 'var(--amber)', dev: diff, variation };
    return { st: 'ok', label: 'OK', color: 'var(--green)', dev: diff, variation };
  }

  // Foco ----------------------------------------------------------------------
  function focusedIndex(flat) {
    if (focusKey == null) return 0;
    const i = flat.findIndex((f) => keyOf(f) === focusKey);
    return i >= 0 ? i : 0;
  }
  function setFocus(flat, i, userAction) {
    if (flat.length === 0) { focusKey = null; return; }
    const j = ((i % flat.length) + flat.length) % flat.length;
    focusKey = keyOf(flat[j]);
    if (userAction) lastInteract = Date.now();
    render();
  }

  // Auto-salto al motor en alarma (con reglas anti-molestia).
  function autoJump(flat) {
    const now = Date.now();
    // Actualizar mapa de alarmas (primer avistaje / limpiar las que salieron).
    const seen = new Set();
    let worst = null; // {key, absDev}
    flat.forEach((f) => {
      const s = devStatus(f.m);
      const k = keyOf(f);
      if (s.st === 'high' || s.st === 'low') {
        seen.add(k);
        if (!alarmSince.has(k)) alarmSince.set(k, now);
        if (now - alarmSince.get(k) >= ALARM_PERSIST_MS) {
          const abs = Math.abs(s.dev);
          if (!worst || abs > worst.absDev) worst = { key: k, absDev: abs };
        }
      }
    });
    for (const k of Array.from(alarmSince.keys())) {
      if (!seen.has(k)) alarmSince.delete(k);
    }

    if (!worst || worst.key === focusKey) return;
    if (modalBackdrop.classList.contains('show')) return;
    if (keypadBackdrop.classList.contains('show')) return;
    if (now - lastInteract < INTERACT_QUIET_MS) return;

    focusKey = worst.key;
    // Flash del borde: 2 parpadeos para avisar que el foco saltó solo.
    widgetEl.classList.remove('flash');
    void widgetEl.offsetWidth; // reinicia la animación si ya estaba
    widgetEl.classList.add('flash');
  }
  widgetEl.addEventListener('animationend', () => widgetEl.classList.remove('flash'));

  // Render -------------------------------------------------------------------
  function render() {
    const flat = allMotores();
    const nodos = state.nodos || [];

    if (flat.length === 0) {
      focusZone.hidden = true;
      dotsBar.hidden = true;
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
      focusKey = null;
      return;
    }
    focusZone.hidden = false;
    dotsBar.hidden = false;
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

    // ---- Carrusel: motor enfocado ----
    const fi = focusedIndex(flat);
    focusKey = keyOf(flat[fi]); // normalizar (por si el motor enfocado desapareció)
    const f = flat[fi];
    const m = f.m;
    const s = devStatus(m);
    const manDose = m.manual_dosis > 0 ? m.manual_dosis : (m.dosis_fija_config || 0);

    focusCard.style.setProperty('--status', s.color);
    focusCard.innerHTML =
      '<div class="f-head">' +
        '<span class="f-name">' + f.num + ' · ' + escapeHtml(m.nombre) + '</span>' +
        '<button class="f-man' + (m.manual_mode ? ' manual' : '') + '" id="fMan" type="button">' +
          (m.manual_mode ? 'MAN' : 'AUTO') + '</button>' +
      '</div>' +
      '<div class="f-real">' +
        '<b>' + fmt(m.real) + '</b>' +
        '<small>' + unidadLabel(m.unidad) + '</small>' +
        '<span class="f-dev">' + s.variation + '</span>' +
      '</div>' +
      '<div class="f-data">' +
        '<span>OBJ <b>' + fmt(m.objetivo) + '</b></span>' +
        '<span>RPM <b>' + (m.rpm != null ? m.rpm : '—') + '</b></span>' +
        '<span class="st">' + s.label + '</span>' +
      '</div>' +
      '<div class="f-step' + (m.manual_mode ? ' enabled' : '') + '">' +
        '<button id="fDn" type="button">−</button>' +
        '<div class="f-dose" id="fDose" role="button" tabindex="0">' + fmt(manDose) + '</div>' +
        '<button id="fUp" type="button">+</button>' +
      '</div>';

    // Nav habilitada solo si hay más de un motor.
    navPrev.disabled = flat.length < 2;
    navNext.disabled = flat.length < 2;

    // ---- Dots: estado de todos los motores ----
    dotsBar.innerHTML = flat.map((x, i) => {
      const st = devStatus(x.m);
      return '<i style="--c:' + st.color + '"' + (i === fi ? ' class="cur"' : '') +
        ' data-i="' + i + '"></i>';
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

  // Handlers del carrusel ------------------------------------------------------
  navPrev.addEventListener('click', () => setFocus(allMotores(), focusedIndex(allMotores()) - 1, true));
  navNext.addEventListener('click', () => setFocus(allMotores(), focusedIndex(allMotores()) + 1, true));

  // Swipe horizontal sobre la focus-card.
  let touchX = null;
  focusZone.addEventListener('touchstart', (ev) => {
    if (ev.touches.length === 1) touchX = ev.touches[0].clientX;
  }, { passive: true });
  focusZone.addEventListener('touchend', (ev) => {
    if (touchX == null) return;
    const dx = ev.changedTouches[0].clientX - touchX;
    touchX = null;
    if (Math.abs(dx) < 30) return;
    const flat = allMotores();
    setFocus(flat, focusedIndex(flat) + (dx < 0 ? 1 : -1), true);
  }, { passive: true });

  // Tap en un dot → enfocar ese motor.
  dotsBar.addEventListener('click', (ev) => {
    const dot = ev.target.closest('i[data-i]');
    if (!dot) return;
    setFocus(allMotores(), parseInt(dot.getAttribute('data-i'), 10), true);
  });

  // Acciones sobre el motor enfocado (delegación: el card se re-renderiza).
  focusCard.addEventListener('click', async (ev) => {
    const flat = allMotores();
    const f = flat[focusedIndex(flat)];
    if (!f) return;
    const m = f.m;
    const uid = f.nodoUid;
    const idx = m.idx;

    if (ev.target.closest('#fMan')) {
      if (!m.manual_mode) {
        const r = await ask(
          (m.nombre || 'Motor') + ' a MANUAL',
          '¿Confirmás MAN? Vas a sobreescribir la dosis del mapa.',
          'Confirmar', null
        );
        if (r !== 'ok') return;
        const dosis = m.manual_dosis > 0 ? m.manual_dosis : (m.dosis_fija_config || 0);
        await sendManual(uid, idx, true, dosis);
      } else {
        const r = await ask(
          (m.nombre || 'Motor') + ' a AUTO',
          '¿Volver a dosis del mapa / configuración?',
          'Confirmar', null
        );
        if (r !== 'ok') return;
        await sendManual(uid, idx, false, 0);
      }
      return;
    }

    if (ev.target.closest('#fDn') || ev.target.closest('#fUp')) {
      if (!m.manual_mode) return;
      const dir = ev.target.closest('#fUp') ? 1 : -1;
      const cur = m.manual_dosis > 0 ? m.manual_dosis : (m.dosis_fija_config || 0);
      const next = Math.max(0, cur + dir * doseStep(cur));
      await sendManual(uid, idx, true, Math.round(next * 10) / 10);
      return;
    }

    if (ev.target.closest('#fDose')) {
      if (!m.manual_mode) return;
      openKeypad(uid, idx, m);
    }
  });

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
      autoJump(allMotores());
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
