// ============================================================================
// widget-quantix.js — overlay HTML del widget QuantiX en pantalla principal.
//
// Reemplaza al WinForms ShapefileLegendControl. Conversa con:
//   GET  /api/widget-quantix/state              (poll cada 500ms)
//   POST /api/widget-quantix/manual             (toggle MAN/AUTO + dosis)
//
// Reglas de la UI:
//   · Toggle MAN/AUTO pide confirmación (modal).
//   · Al pasar a MAN, si manual_dosis está en 0, se inicializa con
//     dosis_fija_config (la dosis "fuera de mapa" configurada en el Hub).
//   · Al pasar a AUTO no se pierde el valor manual: queda persistido en
//     quantiX_motores.json y reaparece la próxima vez que se vuelva a MAN.
//   · La dosis NO es <input>: es un .dose-display read-only que al click abre
//     un keypad numérico dentro del propio widget (220x240). El teclado global
//     de keyboard.js es de 320px de alto y taparía todo, por eso no se usa.
//   · El paginador (‹/›) se persiste en localStorage (selección de nodo).
// ============================================================================
(function () {
  'use strict';

  const POLL_MS = 500;
  const LS_SEL = 'agp_widget_qx_sel';

  const $ = (id) => document.getElementById(id);
  const motoresWrap = $('motoresWrap');
  const emptyMsg = $('emptyMsg');
  const onlineDot = $('onlineDot');
  const nodoNombreEl = $('nodoNombre');
  const pagerIdx = $('pagerIdx');
  const btnPrev = $('btnPrev');
  const btnNext = $('btnNext');

  // Modal helpers ------------------------------------------------------------
  const modalBackdrop = $('modalBackdrop');
  const modalTitle = $('modalTitle');
  const modalMsg = $('modalMsg');
  const modalOk = $('modalOk');
  const modalCancel = $('modalCancel');
  let modalResolver = null;
  function ask(title, msg) {
    modalTitle.textContent = title;
    modalMsg.textContent = msg;
    modalBackdrop.classList.add('show');
    return new Promise((res) => { modalResolver = res; });
  }
  modalOk.addEventListener('click', () => { modalBackdrop.classList.remove('show'); if (modalResolver) modalResolver(true); });
  modalCancel.addEventListener('click', () => { modalBackdrop.classList.remove('show'); if (modalResolver) modalResolver(false); });

  // Estado local -------------------------------------------------------------
  let state = { nodos: [], connected: false };
  let nodoIdx = 0;
  // Keypad in-widget: cuál motor estamos editando + buffer del display.
  let editing = { uid: null, idx: -1, value: '' };

  const keypadBackdrop = $('keypadBackdrop');
  const keypadTitle = $('keypadTitle');
  const keypadDisplay = $('keypadDisplay');
  const keypadGrid = $('keypadGrid');

  function findNodoIdxByUid(uid) {
    if (!uid || !state.nodos) return -1;
    for (let i = 0; i < state.nodos.length; i++) {
      if (state.nodos[i].uid === uid) return i;
    }
    return -1;
  }

  function restoreSelection() {
    const uid = localStorage.getItem(LS_SEL);
    const idx = findNodoIdxByUid(uid);
    if (idx >= 0) nodoIdx = idx;
    else if (nodoIdx >= state.nodos.length) nodoIdx = 0;
  }

  function persistSelection() {
    const n = state.nodos[nodoIdx];
    if (n) localStorage.setItem(LS_SEL, n.uid);
  }

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
    if (decimals == null) decimals = doseDecimals(value);
    if (!isFinite(value)) return '—';
    return Number(value).toFixed(decimals);
  }

  // Render -------------------------------------------------------------------
  function render() {
    const total = state.nodos.length;
    if (total === 0) {
      motoresWrap.innerHTML = '';
      emptyMsg.hidden = false;
      nodoNombreEl.textContent = 'QuantiX';
      onlineDot.classList.remove('on');
      pagerIdx.textContent = '0/0';
      btnPrev.disabled = true; btnNext.disabled = true;
      return;
    }
    emptyMsg.hidden = true;
    if (nodoIdx >= total) nodoIdx = 0;
    const nodo = state.nodos[nodoIdx];

    nodoNombreEl.textContent = nodo.nombre || 'QuantiX';
    onlineDot.classList.toggle('on', !!nodo.online);
    pagerIdx.textContent = (nodoIdx + 1) + '/' + total;
    btnPrev.disabled = total <= 1;
    btnNext.disabled = total <= 1;

    // Render motores (dos cards). La dosis es un .dose-display (no input),
    // que al tap abre el keypad in-widget.
    const html = [];
    (nodo.motores || []).forEach((m) => {
      const idx = m.idx;
      const dosisShown = m.manual_mode ? m.manual_dosis : m.objetivo;
      const decimals = doseDecimals(dosisShown);
      const inputValue = fmt(dosisShown, decimals);
      const disabled = m.manual_mode ? '' : 'disabled';
      const disabledClass = m.manual_mode ? '' : ' disabled';
      const activoClass = m.activo ? ' activo' : '';

      html.push(
        '<div class="motor' + activoClass + '" data-uid="' + nodo.uid + '" data-idx="' + idx + '">' +
          '<div class="row1">' +
            '<div class="nombre" title="' + escapeAttr(m.nombre) + '">' + escapeHtml(m.nombre) + '</div>' +
            '<div class="obj">OBJ <b>' + fmt(m.objetivo) + '</b> · REAL ' + fmt(m.real) + '</div>' +
          '</div>' +
          '<div class="row2">' +
            '<button class="btn-man' + (m.manual_mode ? ' manual' : '') + '" ' +
                    'data-act="toggle" type="button">' +
              (m.manual_mode ? 'MAN' : 'AUTO') +
            '</button>' +
            '<button class="dose-step" data-act="dn" type="button" ' + disabled + '>−</button>' +
            '<div class="dose-display' + disabledClass + '" data-act="edit" role="button" tabindex="0">' +
               escapeHtml(inputValue) +
            '</div>' +
            '<button class="dose-step" data-act="up" type="button" ' + disabled + '>+</button>' +
          '</div>' +
        '</div>'
      );
    });
    motoresWrap.innerHTML = html.join('');
  }

  function escapeHtml(s) {
    return String(s || '').replace(/[&<>]/g, (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
  }
  function escapeAttr(s) {
    return String(s || '').replace(/"/g, '&quot;');
  }

  // Event delegation --------------------------------------------------------
  motoresWrap.addEventListener('click', async (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLElement)) return;
    const motorEl = t.closest('.motor');
    if (!motorEl) return;
    const uid = motorEl.getAttribute('data-uid');
    const idx = parseInt(motorEl.getAttribute('data-idx'), 10);
    const motor = findMotor(uid, idx);
    if (!motor) return;
    const act = t.getAttribute('data-act');
    if (act === 'toggle') {
      const nextManual = !motor.manual_mode;
      const ok = await ask(
        nextManual ? 'Pasar a MANUAL' : 'Pasar a AUTOMÁTICO',
        nextManual
          ? '¿Confirmás MAN? Vas a sobreescribir la dosis del mapa.'
          : '¿Confirmás AUTO? Vuelve a dosis del mapa / configuración.'
      );
      if (!ok) return;
      // Dosis inicial al entrar a MAN: respeta manual_dosis persistido;
      // si está en 0, usa la dosis_fija_config (fuera de mapa default).
      let dosisAEnviar = 0;
      if (nextManual) {
        dosisAEnviar = motor.manual_dosis > 0 ? motor.manual_dosis : (motor.dosis_fija_config || 0);
      }
      await sendManual(uid, idx, nextManual, dosisAEnviar);
    } else if (act === 'up' || act === 'dn') {
      if (!motor.manual_mode) return;
      const cur = motor.manual_dosis > 0 ? motor.manual_dosis : (motor.dosis_fija_config || 0);
      const step = doseStep(cur);
      const next = Math.max(0, cur + (act === 'up' ? step : -step));
      const rounded = Math.round(next * 10) / 10;
      await sendManual(uid, idx, true, rounded);
    } else if (act === 'edit') {
      if (!motor.manual_mode) return; // en AUTO la dosis no se edita
      openKeypad(uid, idx, motor);
    }
  });

  // ---- Keypad in-widget ----------------------------------------------------
  function openKeypad(uid, idx, motor) {
    const cur = motor.manual_dosis > 0 ? motor.manual_dosis : (motor.dosis_fija_config || 0);
    editing.uid = uid;
    editing.idx = idx;
    editing.value = fmt(cur, doseDecimals(cur)); // arranca con la dosis actual
    keypadTitle.textContent = (motor.nombre || ('Motor ' + idx)) + ' · dosis (kg/ha)';
    keypadDisplay.textContent = editing.value;
    keypadBackdrop.classList.add('show');
  }
  function closeKeypad() {
    keypadBackdrop.classList.remove('show');
    editing = { uid: null, idx: -1, value: '' };
  }
  function keypadPress(k) {
    if (k === 'cancel') { closeKeypad(); return; }
    if (k === 'ok') {
      const raw = (editing.value || '').replace(',', '.');
      const v = parseFloat(raw);
      const uid = editing.uid, idx = editing.idx;
      closeKeypad();
      if (!isFinite(v) || v < 0) { render(); return; }
      sendManual(uid, idx, true, v);
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
  // Botones Cancelar/OK están en .keypad-actions (fuera del grid).
  keypadBackdrop.addEventListener('click', (ev) => {
    const b = ev.target.closest('.keypad-actions .kp');
    if (!b) return;
    keypadPress(b.getAttribute('data-k'));
  });

  function findMotor(uid, idx) {
    const n = (state.nodos || []).find((x) => x.uid === uid);
    if (!n) return null;
    return (n.motores || []).find((m) => m.idx === idx);
  }

  // Paginador ---------------------------------------------------------------
  btnPrev.addEventListener('click', () => {
    if (state.nodos.length <= 1) return;
    nodoIdx = (nodoIdx - 1 + state.nodos.length) % state.nodos.length;
    persistSelection();
    render();
  });
  btnNext.addEventListener('click', () => {
    if (state.nodos.length <= 1) return;
    nodoIdx = (nodoIdx + 1) % state.nodos.length;
    persistSelection();
    render();
  });

  // Backend I/O -------------------------------------------------------------
  async function refresh() {
    try {
      const r = await fetch('/api/widget-quantix/state', { cache: 'no-store' });
      if (!r.ok) return;
      const j = await r.json();
      if (!j || !j.ok) return;
      state = j;
      // Mantener selección por uid si existe.
      const curUid = state.nodos[nodoIdx] ? state.nodos[nodoIdx].uid : null;
      if (curUid) {
        const idx = findNodoIdxByUid(curUid);
        if (idx >= 0) nodoIdx = idx;
        else nodoIdx = 0;
      } else {
        restoreSelection();
      }
      render();
    } catch (e) { /* ignorar — el next tick reintenta */ }
  }

  async function sendManual(uid, motorIdx, manual, dosis) {
    try {
      const r = await fetch('/api/widget-quantix/manual', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          uid, motor_idx: motorIdx, manual, dosis: dosis || 0
        })
      });
      if (!r.ok) return;
      // Forzar refresh inmediato (no esperar al próximo poll).
      await refresh();
    } catch (e) { /* ignorar */ }
  }

  // Init --------------------------------------------------------------------
  refresh().then(() => { restoreSelection(); render(); });
  setInterval(refresh, POLL_MS);
})();
