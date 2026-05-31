// ============================================================================
// sistema.js — lógica de la página Sistema (brillo + power).
// ============================================================================
(function () {
  'use strict';

  const slider = document.getElementById('brilloSlider');
  const value = document.getElementById('brilloValue');
  const brilloStatus = document.getElementById('brilloStatus');
  const powerStatus = document.getElementById('powerStatus');

  // ---- Brillo ----
  let pendingTimer = null;

  function setBrilloUI(v) {
    slider.value = v;
    value.textContent = v;
  }

  async function loadBrillo() {
    try {
      const r = await agpApi.get('sistema/brillo');
      if (r.ok && r.value >= 0) {
        setBrilloUI(r.value);
        brilloStatus.textContent = 'Brillo actual: ' + r.value + '%';
      } else {
        brilloStatus.textContent = 'No se detectó control de brillo (DDC/CI ni WMI).';
      }
    } catch (e) {
      brilloStatus.textContent = 'Error consultando brillo: ' + e.message;
    }
  }

  async function applyBrillo(v) {
    try {
      const r = await agpApi.post('sistema/brillo', { value: v });
      brilloStatus.textContent = r.ok ? ('Brillo aplicado: ' + v + '%') : ('No se pudo aplicar (' + v + '%).');
    } catch (e) {
      brilloStatus.textContent = 'Error aplicando brillo: ' + e.message;
    }
  }

  slider.addEventListener('input', () => {
    const v = parseInt(slider.value, 10);
    value.textContent = v;
    if (pendingTimer) clearTimeout(pendingTimer);
    pendingTimer = setTimeout(() => applyBrillo(v), 120);
  });

  document.querySelectorAll('[data-brillo]').forEach(btn => {
    btn.addEventListener('click', () => {
      const v = parseInt(btn.getAttribute('data-brillo'), 10);
      setBrilloUI(v);
      applyBrillo(v);
    });
  });

  // ---- Power ----
  document.querySelectorAll('.power-card').forEach(card => {
    card.addEventListener('click', async () => {
      const action = card.getAttribute('data-action');
      const msg = card.getAttribute('data-confirm');
      if (msg && !confirm(msg)) return;
      try {
        const r = await agpApi.post('sistema/power', { action });
        powerStatus.textContent = r.ok ? ('Acción enviada: ' + action) : ('Falló: ' + (r.error || 'desconocido'));
      } catch (e) {
        powerStatus.textContent = 'Error: ' + e.message;
      }
    });
  });

  loadBrillo();
})();
