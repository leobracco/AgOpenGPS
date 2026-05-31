// ============================================================================
// firmwares.js — catálogo local de firmwares + upload .bin desde la PC.
// Consume FirmwaresController (GET /api/firmwares, POST /api/firmwares/upload,
// DELETE /api/firmwares/{producto}/{version}).
// ============================================================================
(function () {
  'use strict';

  // Validación local — el backend la repite con regex pero adelantamos el
  // feedback al operario antes de mandar el .bin entero. El guión es necesario
  // para "corex-ecu" (Teensy del módulo de pilotaje).
  const RX_PROD = /^[a-zA-Z][a-zA-Z0-9-]{1,31}$/;
  const RX_VER  = /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,31}$/;

  const $ = (id) => document.getElementById(id);

  function fmtBytes(n) {
    if (!n && n !== 0) return '—';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / (1024 * 1024)).toFixed(2) + ' MB';
  }

  function fmtTs(ts) {
    if (!ts) return '—';
    try { return new Date(ts).toLocaleString(); } catch (_) { return '—'; }
  }

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // ── lista ─────────────────────────────────────────────────────────────────
  async function refresh() {
    const wrap = $('fwList');
    wrap.innerHTML = '<div class="fw-empty"><div class="em-title">Cargando catálogo…</div><div>Leyendo el cache local de firmwares.</div></div>';
    try {
      const r = await fetch('/api/firmwares');
      const data = await r.json();
      if (!data || !data.ok) {
        wrap.innerHTML = '<div class="fw-empty"><div class="em-title" style="color:var(--agp-state-bad)">No se pudo leer el cache</div><div>' + esc((data && data.error) || 'desconocido') + '</div></div>';
        return;
      }

      // Pill LAN + meta cache.
      const pill = $('lanPill');
      if (data.lan_ip) {
        pill.textContent = 'LAN ' + data.lan_ip + ':' + data.http_port;
        pill.className = 'pill-info ok';
      } else {
        pill.textContent = 'LAN sin IP';
        pill.className = 'pill-info err';
      }
      $('metaCache').textContent = data.cache_dir ? 'Carpeta · ' + data.cache_dir : '—';

      const prods = data.productos || [];
      if (prods.length === 0) {
        wrap.innerHTML = '<div class="fw-empty"><div class="em-title">Cache vacío</div><div>Sincronizá desde OrbitX o subí un firmware a la derecha.</div></div>';
        return;
      }

      const html = [];
      for (const p of prods) {
        html.push('<div class="fw-prod">');
        html.push('  <div class="fw-prod-head">');
        html.push('    <span class="name">' + esc(p.producto) + '</span>');
        html.push('    <span class="count">' + p.versiones.length + (p.versiones.length === 1 ? ' versión' : ' versiones') + '</span>');
        html.push('  </div>');
        for (const v of p.versiones) {
          const hash = v.hash_sha256 ? v.hash_sha256.substring(0, 12) + '…' : '—';
          const tag = v.local
            ? '<span class="badge-local">local</span>'
            : '<span class="badge-cloud">cloud</span>';
          html.push('  <div class="fw-ver">');
          html.push('    <div class="v">' + esc(v.version) + ' ' + tag + '</div>');
          html.push('    <div class="hash" title="' + esc(v.hash_sha256 || '') + '">' + esc(hash) + '</div>');
          html.push('    <div class="size">' + fmtBytes(v.tamano_bytes) + '</div>');
          html.push('    <div class="actions">');
          if (v.local) {
            html.push('      <button class="btn compact" data-act="del" data-prod="' + esc(p.producto) + '" data-ver="' + esc(v.version) + '">Borrar</button>');
          } else {
            html.push('      <span class="subtitle">—</span>');
          }
          html.push('    </div>');
          if (v.changelog) {
            html.push('    <div style="grid-column: 1/-1; color: var(--agp-text-muted); font-size: var(--agp-fs-sm); margin-top:2px">' + esc(v.changelog) + '</div>');
          }
          if (v.ts) {
            html.push('    <div style="grid-column: 1/-1; color: var(--agp-text-muted); font-size: 11px">' + fmtTs(v.ts) + '</div>');
          }
          html.push('  </div>');
        }
        html.push('</div>');
      }
      wrap.innerHTML = html.join('');

      // Wire delete buttons.
      wrap.querySelectorAll('button[data-act="del"]').forEach(btn => {
        btn.addEventListener('click', () => onDelete(btn.dataset.prod, btn.dataset.ver));
      });
    } catch (e) {
      wrap.innerHTML = '<div class="fw-empty"><div class="em-title" style="color:var(--agp-state-bad)">Error de red</div><div>' + esc(e.message || e) + '</div></div>';
    }
  }

  async function onDelete(prod, ver) {
    if (!prod || !ver) return;
    if (!confirm('¿Borrar ' + prod + ' ' + ver + ' del cache local?\n\nEl .bin se elimina del disco. Los nodos ya actualizados no se ven afectados.')) return;
    try {
      const r = await fetch('/api/firmwares/' + encodeURIComponent(prod) + '/' + encodeURIComponent(ver), {
        method: 'DELETE'
      });
      const data = await r.json();
      if (!data.ok) {
        alert('No se pudo borrar: ' + (data.error || 'desconocido'));
      }
    } catch (e) {
      alert('Error: ' + (e.message || e));
    }
    refresh();
  }

  // ── upload ────────────────────────────────────────────────────────────────
  // Refleja el archivo elegido en la dropzone visual (texto + clase has-file).
  // Acepta .bin/.hex/.zip y auto-completa producto+versión cuando el nombre
  // del archivo tiene un patrón previsible.
  function applyFile(f) {
    const meta = $('fileMeta');
    const drop = $('fwDrop');
    const dropText = $('fwDropText');
    if (!f) {
      meta.textContent = '';
      if (drop) drop.classList.remove('has-file');
      if (dropText) dropText.textContent = 'Tocá para elegir un archivo';
      return;
    }
    meta.textContent = f.name + ' · ' + fmtBytes(f.size);
    if (drop) drop.classList.add('has-file');
    if (dropText) dropText.textContent = f.name;

    // Auto-detect: "<producto>[-_ ]?v?<version>.<ext>" donde producto puede
    // tener guiones internos (corex-ecu) y ext ∈ {bin, hex, zip}.
    const m = /^([a-zA-Z][a-zA-Z0-9-]*?)[-_ ]?[vV]?([0-9][0-9A-Za-z.\-]*)\.(bin|hex|zip)$/i.exec(f.name);
    if (m) {
      const guessProd = m[1].toLowerCase();
      const sel = $('fwProducto');
      for (const opt of sel.options) {
        if (opt.value === guessProd) { sel.value = guessProd; break; }
      }
      const verIn = $('fwVersion');
      if (!verIn.value) verIn.value = m[2];
    }
  }

  function onFileChange(e) {
    const f = e.target.files && e.target.files[0];
    applyFile(f);
  }

  // Drag & drop sobre la dropzone — el input nativo está oculto, así que
  // recibimos los archivos por el evento drop y se los pasamos al input.
  function wireDropzone() {
    const drop = $('fwDrop');
    const input = $('fwFile');
    if (!drop || !input) return;
    ['dragenter', 'dragover'].forEach(ev => {
      drop.addEventListener(ev, (e) => {
        e.preventDefault();
        e.stopPropagation();
        drop.classList.add('has-file');
      });
    });
    ['dragleave', 'drop'].forEach(ev => {
      drop.addEventListener(ev, (e) => {
        e.preventDefault();
        e.stopPropagation();
        if (ev === 'dragleave' && !input.files.length) drop.classList.remove('has-file');
      });
    });
    drop.addEventListener('drop', (e) => {
      const files = e.dataTransfer && e.dataTransfer.files;
      if (!files || !files.length) return;
      // Asignar archivos al input (DataTransfer es la forma estándar)
      try {
        const dt = new DataTransfer();
        dt.items.add(files[0]);
        input.files = dt.files;
      } catch (_) {
        // Fallback: si el browser no soporta DataTransfer, aplicamos sólo
        // el feedback visual y mandamos el blob directo en el upload.
        input._droppedFile = files[0];
      }
      applyFile(files[0]);
    });
  }

  // Helper: pinta el resultado en #uplResult con la clase semántica adecuada.
  // La clase la consume el CSS (ok/err) — no inyectamos pills inline porque
  // visualmente quedan despegadas del bloque.
  function setResult(kind, msg) {
    const res = $('uplResult');
    res.classList.remove('ok', 'err');
    if (!kind || !msg) { res.textContent = ''; return; }
    res.classList.add(kind);
    res.textContent = msg;
  }

  function pickFile() {
    const input = $('fwFile');
    if (input && input.files && input.files[0]) return input.files[0];
    if (input && input._droppedFile) return input._droppedFile;
    return null;
  }

  async function onUpload() {
    const file = pickFile();
    const prod = $('fwProducto').value.trim();
    const ver  = $('fwVersion').value.trim();
    const chg  = $('fwChangelog').value;
    const bar  = $('uplProgress');
    const barWrap = $('uplProgressWrap');

    setResult(null);
    if (!file)                       { setResult('err', 'Falta el archivo de firmware.'); return; }
    if (!RX_PROD.test(prod))         { setResult('err', 'Producto inválido — elegí uno de la lista.'); return; }
    if (!RX_VER.test(ver))           { setResult('err', 'Versión inválida — usá formato semver (ej 1.5.0).'); return; }
    if (file.size < 1024)            { setResult('err', 'El archivo es demasiado chico (< 1 KB).'); return; }
    if (file.size > 8 * 1024 * 1024) { setResult('err', 'El archivo supera el límite de 8 MB.'); return; }

    barWrap.style.display = 'block';
    bar.style.width = '0%';

    // XHR para poder mostrar progress (fetch no expone upload progress).
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/firmwares/upload', true);
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.setRequestHeader('X-AP-Producto', prod);
    xhr.setRequestHeader('X-AP-Version', ver);
    if (chg) xhr.setRequestHeader('X-AP-Changelog', chg);

    xhr.upload.onprogress = (ev) => {
      if (ev.lengthComputable) {
        const pct = Math.round((ev.loaded / ev.total) * 100);
        bar.style.width = pct + '%';
      }
    };
    xhr.onload = () => {
      barWrap.style.display = 'none';
      let data = null;
      try { data = JSON.parse(xhr.responseText); } catch (_) {}
      if (xhr.status === 200 && data && data.ok) {
        setResult('ok', 'Subido ' + data.producto + ' v' + data.version + ' · ' + fmtBytes(data.tamano_bytes));
        // Reset del form — preservamos el producto elegido para subir otra
        // versión seguida del mismo nodo sin volver a tocar el select.
        const input = $('fwFile');
        if (input) { input.value = ''; delete input._droppedFile; }
        $('fwVersion').value = '';
        $('fwChangelog').value = '';
        applyFile(null);
        refresh();
      } else {
        const msg = (data && data.error) || ('HTTP ' + xhr.status);
        setResult('err', 'Falló: ' + msg);
      }
    };
    xhr.onerror = () => {
      barWrap.style.display = 'none';
      setResult('err', 'Error de red — revisá la conexión local.');
    };
    xhr.send(file);
  }

  // ── init ──────────────────────────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', () => {
    $('btnRefresh').addEventListener('click', refresh);
    $('btnUpload').addEventListener('click', onUpload);
    $('fwFile').addEventListener('change', onFileChange);
    wireDropzone();
    refresh();
  });
})();
