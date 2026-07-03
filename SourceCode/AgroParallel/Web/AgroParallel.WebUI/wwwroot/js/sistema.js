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

  // ---- Modal de confirmación táctil ----
  const cfmOverlay = document.getElementById('confirmModal');
  const cfmMsg = document.getElementById('cfmMsg');
  const cfmOk = document.getElementById('cfmOk');
  const cfmCancel = document.getElementById('cfmCancel');

  function confirmModal(msg) {
    return new Promise(resolve => {
      cfmMsg.textContent = msg;
      cfmOverlay.style.display = 'flex';
      function close(ans) {
        cfmOverlay.style.display = 'none';
        cfmOk.removeEventListener('click', onOk);
        cfmCancel.removeEventListener('click', onCancel);
        cfmOverlay.removeEventListener('click', onBackdrop);
        resolve(ans);
      }
      function onOk() { close(true); }
      function onCancel() { close(false); }
      function onBackdrop(e) { if (e.target === cfmOverlay) close(false); }
      cfmOk.addEventListener('click', onOk);
      cfmCancel.addEventListener('click', onCancel);
      cfmOverlay.addEventListener('click', onBackdrop);
    });
  }

  // ---- Power ----
  document.querySelectorAll('.power-card').forEach(card => {
    card.addEventListener('click', async () => {
      const action = card.getAttribute('data-action');
      const msg = card.getAttribute('data-confirm');
      if (msg && !(await confirmModal(msg))) return;
      try {
        const r = await agpApi.post('sistema/power', { action });
        powerStatus.textContent = r.ok ? ('Acción enviada: ' + action) : ('Falló: ' + (r.error || 'desconocido'));
      } catch (e) {
        powerStatus.textContent = 'Error: ' + e.message;
      }
    });
  });

  // ---- Configuración: backup manual ----
  const btnBackup = document.getElementById('btnBackupNow');
  const btnInstaller = document.getElementById('btnInstallerBackup');
  const cfgStatusEarly = document.getElementById('cfgStatus');

  async function doBackup(tipo, btn, etiqueta) {
    btn.disabled = true;
    if (btnBackup) btnBackup.disabled = true;
    if (btnInstaller) btnInstaller.disabled = true;
    cfgStatusEarly.textContent = 'Generando ' + etiqueta + '…';
    try {
      const r = await agpApi.post('config/backup', tipo ? { tipo: tipo } : {});
      if (r.ok) {
        cfgStatusEarly.textContent = r.nombre
          ? ('Backup creado: ' + r.nombre)
          : 'Backup creado.';
        loadBackups();
      } else {
        cfgStatusEarly.textContent = 'No se pudo crear el backup' +
          (r.detail ? (': ' + r.detail) : '.');
      }
    } catch (e) {
      cfgStatusEarly.textContent = 'Error creando backup: ' + e.message;
    } finally {
      if (btnBackup) btnBackup.disabled = false;
      if (btnInstaller) btnInstaller.disabled = false;
    }
  }

  if (btnBackup) {
    btnBackup.addEventListener('click', () =>
      doBackup(null, btnBackup, 'backup'));
  }
  if (btnInstaller) {
    btnInstaller.addEventListener('click', () =>
      doBackup('instalador', btnInstaller, 'backup de instalador'));
  }

  // ---- Configuración: lista de backups + restaurar ----
  const bkList = document.getElementById('bkList');
  const bkStatus = document.getElementById('bkStatus');
  const btnReloadBackups = document.getElementById('btnReloadBackups');

  function fmtBytes(n) {
    if (!n || n < 1024) return (n || 0) + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / (1024 * 1024)).toFixed(1) + ' MB';
  }

  async function restoreBackup(nombre, btn) {
    if (!(await confirmModal('¿Restaurar la configuración del backup "' + nombre +
                 '"? Se va a pisar la config actual (se respalda antes).'))) return;
    if (btn) btn.disabled = true;
    bkStatus.textContent = 'Restaurando…';
    try {
      const r = await agpApi.post('config/restore', { nombre: nombre });
      if (r.ok) {
        bkStatus.textContent = 'Restaurados ' + (r.archivos_restaurados || 0) +
          ' archivos. Reiniciá PilotX para aplicar todo.';
        loadBackups();
      } else {
        bkStatus.textContent = 'No se pudo restaurar: ' + (r.error || 'desconocido');
      }
    } catch (e) {
      bkStatus.textContent = 'Error restaurando: ' + e.message;
    } finally {
      if (btn) btn.disabled = false;
    }
  }

  async function toggleDetail(nombre, detail, btn) {
    // Si ya está abierto, lo cerramos.
    if (detail.style.display !== 'none') {
      detail.style.display = 'none';
      btn.textContent = '👁  Ver';
      return;
    }
    btn.textContent = '🠅  Ocultar';
    detail.style.display = 'block';

    // Cargamos el contenido una sola vez (lo cacheamos en el nodo).
    if (detail.dataset.loaded === '1') return;
    detail.innerHTML = '<div class="bk-file">Cargando…</div>';
    try {
      const r = await agpApi.get('config/backup-detalle?nombre=' + encodeURIComponent(nombre));
      const files = (r && r.archivos) || [];
      if (!r.ok) {
        detail.innerHTML = '<div class="bk-file">Error: ' + (r.error || 'desconocido') + '</div>';
        return;
      }
      if (!files.length) {
        detail.innerHTML = '<div class="bk-file">(vacío)</div>';
        return;
      }
      detail.innerHTML = '';
      files.forEach(f => {
        const row = document.createElement('div');
        row.className = 'bk-file';
        const path = document.createElement('span');
        path.textContent = f.ruta;
        const size = document.createElement('span');
        size.className = 'bk-file-size';
        size.textContent = fmtBytes(f.bytes);
        row.appendChild(path);
        row.appendChild(size);
        detail.appendChild(row);
      });
      detail.dataset.loaded = '1';
    } catch (e) {
      detail.innerHTML = '<div class="bk-file">Error: ' + e.message + '</div>';
    }
  }

  async function loadBackups() {
    if (!bkList) return;
    bkList.innerHTML = '';
    bkStatus.textContent = 'Cargando…';
    try {
      const r = await agpApi.get('config/backups');
      const items = (r && r.backups) || [];
      if (!items.length) {
        bkStatus.textContent = '';
        bkList.innerHTML = '<div class="bk-empty">Todavía no hay backups guardados.</div>';
        return;
      }
      bkStatus.textContent = '';
      items.forEach(it => {
        const row = document.createElement('div');
        row.className = 'bk-item';

        const info = document.createElement('div');
        info.className = 'bk-info';
        const when = document.createElement('div');
        when.className = 'bk-when';
        when.textContent = it.fecha || it.nombre;
        const tipo = (it.tipo || '').toLowerCase();
        if (tipo) {
          const tag = document.createElement('span');
          tag.className = 'bk-tag ' + tipo;
          tag.textContent = tipo === 'instalador' ? 'Instalador'
            : (tipo === 'auto' ? 'Diario' : 'Previo');
          when.appendChild(tag);
        }
        const meta = document.createElement('div');
        meta.className = 'bk-meta';
        meta.textContent = (it.archivos || 0) + ' archivos · ' + fmtBytes(it.bytes);
        info.appendChild(when);
        info.appendChild(meta);

        const detail = document.createElement('div');
        detail.className = 'bk-detail';
        detail.style.display = 'none';

        const btnVer = document.createElement('button');
        btnVer.className = 'btn compact view';
        btnVer.textContent = '👁  Ver';
        btnVer.addEventListener('click', () => toggleDetail(it.nombre, detail, btnVer));

        const btn = document.createElement('button');
        btn.className = 'btn restore';
        btn.textContent = '⭯  Restaurar';
        btn.addEventListener('click', () => restoreBackup(it.nombre, btn));

        row.appendChild(info);
        row.appendChild(btnVer);
        row.appendChild(btn);
        row.appendChild(detail);
        bkList.appendChild(row);
      });
    } catch (e) {
      bkStatus.textContent = 'Error cargando backups: ' + e.message;
    }
  }

  if (btnReloadBackups) btnReloadBackups.addEventListener('click', loadBackups);

  // ---- Configuración: export / import ----
  const btnExport = document.getElementById('btnExportCfg');
  const btnImport = document.getElementById('btnImportCfg');
  const fileImport = document.getElementById('fileImportCfg');
  const cfgStatus = document.getElementById('cfgStatus');

  if (btnExport) {
    btnExport.addEventListener('click', () => {
      cfgStatus.textContent = 'Generando copia…';
      // GET que devuelve el .zip como descarga (Content-Disposition).
      const a = document.createElement('a');
      a.href = '/api/config/export';
      a.download = '';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      cfgStatus.textContent = 'Copia descargada. Guardala en el pendrive.';
    });
  }

  if (btnImport && fileImport) {
    btnImport.addEventListener('click', () => fileImport.click());

    fileImport.addEventListener('change', async () => {
      const f = fileImport.files && fileImport.files[0];
      if (!f) return;
      if (!(await confirmModal('¿Restaurar la configuración desde "' + f.name +
                   '"? Se va a pisar la config actual (se respalda antes).'))) {
        fileImport.value = '';
        return;
      }
      cfgStatus.textContent = 'Restaurando…';
      try {
        const buf = await f.arrayBuffer();
        const r = await fetch('/api/config/import', {
          method: 'POST',
          headers: { 'Content-Type': 'application/zip' },
          body: buf
        });
        const j = await r.json();
        if (j.ok) {
          cfgStatus.textContent = 'Restaurados ' + (j.archivos_restaurados || 0) +
            ' archivos. Reiniciá PilotX para aplicar todo.';
          loadBackups();
        } else {
          cfgStatus.textContent = 'No se pudo restaurar: ' + (j.error || 'desconocido');
        }
      } catch (e) {
        cfgStatus.textContent = 'Error restaurando: ' + e.message;
      }
      fileImport.value = '';
    });
  }

  loadBrillo();
  loadBackups();
})();
