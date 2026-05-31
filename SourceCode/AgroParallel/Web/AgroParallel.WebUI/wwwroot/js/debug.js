// ============================================================================
// debug.js — UI del módulo Debug global.
// - WS /ws/debug push live de cada entrada.
// - Snapshot inicial vía REST /api/debug/snapshot.
// - Toggles por módulo, filtro de nivel, search, pausa, clear, grabación.
// ============================================================================
(function () {
  'use strict';

  const ORDER_LEVELS = ['debug', 'info', 'warn', 'error'];

  const state = {
    cfg: null,
    seq: 0,
    paused: false,
    minLevel: 'info',
    search: '',
    recording: false,
    recordingFile: null,
    buffer: [],          // últimas N entries renderizadas
    maxRender: 1500,
    autoScroll: true,
    ws: null
  };

  const $modules = document.getElementById('dbgModules');
  const $buffer = document.getElementById('dbgBuffer');
  const $search = document.getElementById('dbgSearch');
  const $level = document.getElementById('dbgLevel');
  const $btnPause = document.getElementById('btnPause');
  const $btnClear = document.getElementById('btnClear');
  const $btnRecord = document.getElementById('btnRecord');
  const $btnExport = document.getElementById('btnExport');
  const $status = document.getElementById('dbgStatus');

  function levelRank(l) { return ORDER_LEVELS.indexOf((l || 'info').toLowerCase()); }

  function fmtTs(iso) {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      const hh = String(d.getHours()).padStart(2, '0');
      const mm = String(d.getMinutes()).padStart(2, '0');
      const ss = String(d.getSeconds()).padStart(2, '0');
      const ms = String(d.getMilliseconds()).padStart(3, '0');
      return hh + ':' + mm + ':' + ss + '.' + ms;
    } catch (_) { return iso; }
  }

  function escHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  function renderModules() {
    if (!state.cfg || !state.cfg.modules) return;
    const known = state.cfg.modules;
    const sorted = Object.keys(known).sort();
    $modules.innerHTML = sorted.map(name => {
      const on = !!known[name];
      return '<span class="chip' + (on ? ' on' : '') + '" data-mod="' + escHtml(name) + '">' +
             escHtml(name) + '</span>';
    }).join('');
  }

  function passesFilter(e) {
    if (!state.cfg) return true;
    const mods = state.cfg.modules || {};
    if (mods[e.module] === false) return false;
    if (levelRank(e.level) < levelRank(state.minLevel)) return false;
    if (state.search) {
      const q = state.search.toLowerCase();
      if (!(e.message || '').toLowerCase().includes(q) &&
          !(e.module || '').toLowerCase().includes(q)) return false;
    }
    return true;
  }

  function appendEntry(e) {
    state.seq = Math.max(state.seq, e.seq || 0);
    if (!passesFilter(e)) return;
    state.buffer.push(e);
    if (state.buffer.length > state.maxRender) {
      state.buffer.shift();
      const first = $buffer.firstElementChild;
      if (first && first.classList && first.classList.contains('dbg-line')) first.remove();
    }
    const line = renderLine(e);
    if ($buffer.firstElementChild && $buffer.firstElementChild.classList &&
        $buffer.firstElementChild.classList.contains('dbg-empty')) {
      $buffer.innerHTML = '';
    }
    $buffer.insertAdjacentHTML('beforeend', line);
    if (state.autoScroll && !state.paused) {
      $buffer.scrollTop = $buffer.scrollHeight;
    }
  }

  function renderLine(e) {
    const cls = 'dbg-line l-' + escHtml((e.level || 'info').toLowerCase());
    return '<div class="' + cls + '">' +
      '<span class="ts">' + escHtml(fmtTs(e.ts)) + '</span>' +
      '<span class="mod">' + escHtml(e.module || 'host') + '</span>' +
      '<span class="msg">' + escHtml(e.message || '') + '</span>' +
    '</div>';
  }

  function renderAll() {
    if (!state.buffer.length) {
      $buffer.innerHTML = '<div class="dbg-empty">Sin eventos…</div>';
      return;
    }
    $buffer.innerHTML = state.buffer.map(renderLine).join('');
    $buffer.scrollTop = $buffer.scrollHeight;
  }

  // ---------- WS ------------------------------------------------------------
  function connectWs() {
    try {
      const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws/debug';
      const ws = new WebSocket(url);
      ws.onopen = () => { $status.textContent = ''; $status.innerHTML = '<span class="dot"></span> En vivo'; $status.className = 'pill live'; };
      ws.onclose = () => {
        $status.innerHTML = '<span class="dot"></span> Desconectado';
        $status.className = 'pill paused';
        setTimeout(connectWs, 1200);
      };
      ws.onerror = () => { try { ws.close(); } catch (_) {} };
      ws.onmessage = (msg) => {
        if (state.paused) return;
        try { appendEntry(JSON.parse(msg.data)); } catch (_) {}
      };
      state.ws = ws;
    } catch (_) {
      setTimeout(connectWs, 2000);
    }
  }

  // ---------- REST ----------------------------------------------------------
  async function loadSnapshot() {
    const r = await fetch('/api/debug/snapshot?max=500');
    const j = await r.json();
    state.cfg = j.config || { modules: {}, minLevel: 'debug' };
    state.seq = j.seq || 0;
    state.recording = !!j.recording;
    state.recordingFile = j.recordingFile || null;
    // Sembrar buffer
    state.buffer = (j.entries || []).filter(passesFilter);
    renderAll();
    renderModules();
    renderRecordBtn();
  }

  async function putConfig() {
    try {
      await fetch('/api/debug/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state.cfg)
      });
    } catch (_) {}
  }

  async function toggleModule(name) {
    if (!state.cfg) return;
    const cur = !!(state.cfg.modules || {})[name];
    state.cfg.modules[name] = !cur;
    renderModules();
    try {
      await fetch('/api/debug/module?name=' + encodeURIComponent(name) + '&on=' + (!cur),
        { method: 'POST' });
    } catch (_) {}
    // Re-filtra buffer en RAM (no se descartan eventos viejos del servidor)
    renderAll();
  }

  async function clearBuffer() {
    state.buffer = [];
    renderAll();
    try { await fetch('/api/debug/clear', { method: 'POST' }); } catch (_) {}
  }

  async function toggleRecord() {
    state.recording = !state.recording;
    renderRecordBtn();
    try {
      const r = await fetch('/api/debug/record?on=' + state.recording, { method: 'POST' });
      const j = await r.json();
      state.recordingFile = j.file || null;
      renderRecordBtn();
    } catch (_) {
      state.recording = !state.recording;
      renderRecordBtn();
    }
  }

  function renderRecordBtn() {
    if (state.recording) {
      $btnRecord.textContent = 'Grabando…';
      $btnRecord.classList.add('dbg-rec-on');
      $btnRecord.title = state.recordingFile || '';
    } else {
      $btnRecord.textContent = 'Grabar';
      $btnRecord.classList.remove('dbg-rec-on');
      $btnRecord.title = '';
    }
  }

  function exportBuffer() {
    const lines = state.buffer.map(e =>
      (e.ts || '') + ' [' + (e.module || 'host') + '] ' +
      (e.level || 'info').toUpperCase() + ' ' + (e.message || '')
    );
    const blob = new Blob([lines.join('\n')], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'debug-' + new Date().toISOString().replace(/[:.]/g, '-') + '.log';
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  }

  // ---------- Wiring --------------------------------------------------------
  $modules.addEventListener('click', (e) => {
    const c = e.target.closest('.chip');
    if (c) toggleModule(c.getAttribute('data-mod'));
  });

  $search.addEventListener('input', () => {
    state.search = $search.value || '';
    renderAll();
  });

  $level.addEventListener('click', (e) => {
    const b = e.target.closest('button[data-lvl]');
    if (!b) return;
    state.minLevel = b.getAttribute('data-lvl');
    $level.querySelectorAll('button').forEach(x => x.classList.remove('on'));
    b.classList.add('on');
    if (state.cfg) {
      state.cfg.minLevel = state.minLevel;
      putConfig();
    }
    renderAll();
  });

  $btnPause.addEventListener('click', () => {
    state.paused = !state.paused;
    $btnPause.textContent = state.paused ? 'Reanudar' : 'Pausar';
    $btnPause.classList.toggle('primary', state.paused);
    if (!state.paused) $buffer.scrollTop = $buffer.scrollHeight;
  });

  $btnClear.addEventListener('click', clearBuffer);
  $btnRecord.addEventListener('click', toggleRecord);
  $btnExport.addEventListener('click', exportBuffer);

  // Detecta si el usuario hizo scroll arriba → desactiva auto-scroll
  $buffer.addEventListener('scroll', () => {
    const atBottom = $buffer.scrollHeight - $buffer.scrollTop - $buffer.clientHeight < 32;
    state.autoScroll = atBottom;
  });

  loadSnapshot().then(connectWs).catch(() => {
    $buffer.innerHTML = '<div class="dbg-empty">No se pudo conectar al servicio Debug.</div>';
  });
})();
