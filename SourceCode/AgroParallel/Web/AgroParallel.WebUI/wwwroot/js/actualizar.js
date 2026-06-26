// ============================================================================
// actualizar.js - control del auto-update del PilotX.
//
// Flujo UI:
//   1. Buscar    → POST /api/pilotx/update/check
//   2. Descargar → POST /api/pilotx/update/download (con polling de progreso)
//   3. Aplicar   → POST /api/pilotx/update/apply, luego la app se cierra y
//                  reinicia desde el updater externo en ~10s. El cliente
//                  intenta reconectarse periódicamente.
// ============================================================================
(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const btnCheck = $('btnCheck');
  const btnDownload = $('btnDownload');
  const btnApply = $('btnApply');
  const phasePill = $('phasePill');
  const progBar = $('progBar');
  const errLine = $('errLine');
  const cl = $('changelogBox');

  let pollTimer = null;

  function fmtBytes(n) {
    if (!n || n <= 0) return '—';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / (1024 * 1024)).toFixed(2) + ' MB';
  }

  function fmtTs(ms) {
    if (!ms) return '—';
    const d = new Date(ms);
    return d.toLocaleTimeString() + ' ' + d.toLocaleDateString();
  }

  function render(st) {
    if (!st) return;
    $('vCurrent').textContent = st.currentVersion || '—';
    $('vAvail').textContent = st.availableVersion || '—';
    $('vSize').textContent = fmtBytes(st.sizeBytes);
    $('vHash').textContent = st.sha256 ? st.sha256.slice(0, 16) + '…' : '—';
    $('vChecked').textContent = fmtTs(st.lastCheckUnixMs);

    const phaseName = ['Idle','Checking','UpdateAvailable','Downloading','ReadyToApply','Applying','','','','Error'][st.phase] || 'Idle';
    phasePill.className = 'phase-pill phase-' + phaseName;
    phasePill.textContent = phaseName;

    progBar.style.width = (st.progressPct > 0 ? st.progressPct : 0) + '%';

    if (st.lastError) {
      errLine.style.display = '';
      errLine.textContent = 'Error: ' + st.lastError;
    } else {
      errLine.style.display = 'none';
      errLine.textContent = '';
    }

    if (st.changelog) {
      cl.style.display = '';
      cl.textContent = st.changelog;
    } else {
      cl.style.display = 'none';
    }

    // Botones según fase.
    const isBusy = (st.phase === 1 /* Checking */ || st.phase === 3 /* Downloading */ || st.phase === 5 /* Applying */);
    btnCheck.disabled = isBusy;
    btnDownload.disabled = isBusy || !(st.phase === 2 /* UpdateAvailable */ || st.phase === 9 /* Error */);
    btnApply.disabled = isBusy || !(st.phase === 4 /* ReadyToApply */ || st.stagingReady);
  }

  async function refresh() {
    try {
      const r = await agpApi.get('pilotx/update/status');
      render(r.status);
    } catch (e) {
      console.warn('status fail', e);
    }
  }

  function startPolling(ms) {
    stopPolling();
    pollTimer = setInterval(refresh, ms || 1000);
  }
  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  }

  btnCheck.addEventListener('click', async () => {
    btnCheck.disabled = true;
    try {
      const r = await agpApi.post('pilotx/update/check');
      render(r.status);
    } catch (e) {
      errLine.style.display = '';
      errLine.textContent = 'No se pudo consultar el cloud: ' + e.message;
    } finally {
      btnCheck.disabled = false;
    }
  });

  btnDownload.addEventListener('click', async () => {
    btnDownload.disabled = true;
    startPolling(700);
    try {
      const r = await agpApi.post('pilotx/update/download');
      render(r.status);
    } catch (e) {
      errLine.style.display = '';
      errLine.textContent = 'Descarga falló: ' + e.message;
    } finally {
      stopPolling();
      refresh();
    }
  });

  btnApply.addEventListener('click', async () => {
    if (!confirm('La aplicación se va a cerrar para aplicar la actualización. ¿Continuar?')) return;
    btnApply.disabled = true;
    try {
      await agpApi.post('pilotx/update/apply');
      // Mensaje al operario y reintentar reconexión.
      phasePill.className = 'phase-pill phase-Applying';
      phasePill.textContent = 'Aplicando…';
      document.body.style.opacity = '0.5';
      let attempts = 0;
      const recon = setInterval(async () => {
        attempts++;
        try {
          await agpApi.get('pilotx/update/status');
          // si responde, PilotX volvió: recargamos.
          clearInterval(recon);
          location.href = '/';
        } catch {
          if (attempts > 60) clearInterval(recon);
        }
      }, 2000);
    } catch (e) {
      errLine.style.display = '';
      errLine.textContent = 'No se pudo iniciar la aplicación del update: ' + e.message;
      btnApply.disabled = false;
    }
  });

  refresh();
})();
