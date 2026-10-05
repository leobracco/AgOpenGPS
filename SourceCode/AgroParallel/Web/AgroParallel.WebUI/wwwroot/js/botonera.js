// ============================================================================
// botonera.js — launcher de comandos del piloto, TOTALMENTE ordenable:
//   · modo normal  → tap = POST /api/aog/guidance/command {cmd}
//   · modo ORDENAR → arrastrás cualquier botón: sobre otro botón lo REORDENA
//     (se inserta antes), sobre una carpeta lo mete adentro
// El layout (orden completo + carpetas) se persiste como CSV en el equipo vía
// GET/POST /api/aog/botonera (archivo botonera-layout.csv junto al exe) — no
// en localStorage: queda FIJO en el equipo y es editable/commiteable a mano.
// Catálogo de comandos: window.PILOT_COMMANDS (pilot-commands.js).
// ============================================================================
(function () {
  'use strict';

  var CAT = window.PILOT_COMMANDS || [];
  var byCmd = {};
  CAT.forEach(function (c) { byCmd[c.cmd] = c; });

  // state.order   = [cmd...] sueltos, en orden
  // state.folders = [{name, cmds: [cmd...]}] en orden
  var state = { order: CAT.map(function (c) { return c.cmd; }), folders: [] };

  // ---- CSV <-> state ----------------------------------------------------
  // formato: header "carpeta,cmd"; línea con carpeta vacía = suelto;
  // línea con cmd vacío = carpeta declarada (permite carpetas vacías).
  function toCsv() {
    var lines = ['carpeta,cmd'];
    state.order.forEach(function (cmd) { lines.push(',' + cmd); });
    state.folders.forEach(function (f) {
      var name = f.name.replace(/,/g, ' ');  // sin comas en nombres
      if (f.cmds.length === 0) lines.push(name + ',');
      f.cmds.forEach(function (cmd) { lines.push(name + ',' + cmd); });
    });
    return lines.join('\n');
  }

  function fromCsv(csv) {
    var order = [], folders = [], byName = {};
    csv.split(/\r?\n/).forEach(function (line, i) {
      if (i === 0 && /carpeta/i.test(line)) return; // header
      var idx = line.indexOf(',');
      if (idx < 0) return;
      var carpeta = line.slice(0, idx).trim();
      var cmd = line.slice(idx + 1).trim();
      if (cmd && !byCmd[cmd]) return;              // comando que ya no existe
      if (!carpeta) {
        if (cmd) order.push(cmd);
        return;
      }
      var f = byName[carpeta];
      if (!f) { f = { name: carpeta, cmds: [] }; byName[carpeta] = f; folders.push(f); }
      if (cmd) f.cmds.push(cmd);
    });
    // comandos nuevos del catálogo que el CSV no conoce → al final de sueltos
    var known = {};
    order.forEach(function (c) { known[c] = true; });
    folders.forEach(function (f) { f.cmds.forEach(function (c) { known[c] = true; }); });
    CAT.forEach(function (c) { if (!known[c.cmd]) order.push(c.cmd); });
    // dedup defensivo de sueltos
    var seen = {};
    order = order.filter(function (c) { if (seen[c]) return false; seen[c] = true; return true; });
    return { order: order, folders: folders };
  }

  async function loadLayout() {
    try {
      var res = await fetch('/api/aog/botonera', { cache: 'no-store' });
      var data = await res.json();
      if (data.ok && data.csv) state = fromCsv(data.csv);
    } catch (e) { /* sin backend (browser suelto): default de catálogo */ }
    render();
  }

  var saveTimer = null;
  function saveLayout() {
    // debounce: varias mutaciones seguidas (drags) → un solo POST
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(async function () {
      try {
        var res = await fetch('/api/aog/botonera', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ csv: toCsv() })
        });
        var data = await res.json();
        if (!data.ok) toast('No se pudo guardar el layout: ' + (data.error || ''));
      } catch (e) {
        toast('Sin conexión: el layout no se guardó');
      }
    }, 400);
  }

  // ---- ejecutar comando ---------------------------------------------------
  async function exec(cmd, el) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (el) {
        el.classList.add('flash');
        setTimeout(function () { el.classList.remove('flash'); }, 300);
      }
      if (!data.ok) toast('Comando no disponible: ' + cmd);
    } catch (e) {
      toast('Sin conexión con PilotX');
    }
  }

  var toastTimer = null;
  function toast(text) {
    var t = document.getElementById('toast');
    t.textContent = text;
    t.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.classList.remove('show'); }, 2200);
  }

  // ---- render -------------------------------------------------------------
  function cmdTile(c) {
    var d = document.createElement('div');
    d.className = 'cmd';
    d.dataset.cmd = c.cmd;
    d.innerHTML = '<span class="ico">' + c.ico + '</span><span>' + c.label + '</span>';
    hookPointer(d, c);
    return d;
  }

  function render() {
    var fg = document.getElementById('foldersGrid');
    var lg = document.getElementById('looseGrid');
    var ft = document.getElementById('foldersTitle');
    fg.innerHTML = ''; lg.innerHTML = '';

    ft.hidden = state.folders.length === 0;
    state.folders.forEach(function (f) {
      var d = document.createElement('div');
      d.className = 'folder';
      d.dataset.folder = f.name;
      d.innerHTML = '<span class="count">' + f.cmds.length + '</span>' +
        '<span class="ico">📁</span><span>' + escapeHtml(f.name) + '</span>';
      d.addEventListener('click', function () {
        if (!arranging) openFolder(f.name);
      });
      fg.appendChild(d);
    });

    state.order.forEach(function (cmd) {
      var c = byCmd[cmd];
      if (c) lg.appendChild(cmdTile(c));
    });
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
    });
  }

  // ---- drag & drop con modo ORDENAR explícito -----------------------------
  var arranging = false;
  var drag = null; // {cmd, srcEl, started, startX, startY}
  var ghost = document.getElementById('ghost');

  function setArranging(v) {
    arranging = v;
    document.body.classList.toggle('arrange', v);
    document.getElementById('btnArrange').textContent = v ? '✔ Listo' : '✋ Ordenar';
    document.getElementById('modeHint').textContent = v
      ? 'Arrastrá: sobre otro botón lo reordena, sobre una carpeta lo guarda'
      : 'Tocá un botón y ejecuta';
    if (!v) saveLayout(); // al salir de ordenar, fijar el layout en el CSV
  }
  document.getElementById('btnArrange').addEventListener('click', function () {
    setArranging(!arranging);
  });

  function hookPointer(el, c) {
    el.addEventListener('pointerdown', function (e) {
      if (drag || !arranging) return;
      e.preventDefault();
      drag = { cmd: c.cmd, srcEl: el, started: false, startX: e.clientX, startY: e.clientY };
      el.setPointerCapture(e.pointerId);
    });
    el.addEventListener('pointermove', function (e) {
      if (!drag || drag.srcEl !== el) return;
      if (!drag.started) {
        var dx = e.clientX - drag.startX, dy = e.clientY - drag.startY;
        if (Math.abs(dx) > 6 || Math.abs(dy) > 6) startDrag();
        else return;
      }
      e.preventDefault();
      moveGhost(e.clientX, e.clientY);
      markTargets(e.clientX, e.clientY);
    });
    el.addEventListener('pointerup', function (e) {
      if (!arranging || !drag || drag.srcEl !== el) return;
      if (drag.started) {
        var dropped = drag.cmd;
        var folder = folderAt(e.clientX, e.clientY);
        var overCmd = cmdAt(e.clientX, e.clientY, drag.srcEl);
        endDrag();
        if (folder) {
          var f = findFolder(folder);
          if (f && f.cmds.indexOf(dropped) < 0) {
            removeEverywhere(dropped);
            f.cmds.push(dropped);
            saveLayout();
            render();
            toast('✓ Guardado en "' + f.name + '"');
          }
        } else if (overCmd && overCmd !== dropped) {
          // reordenar: insertar ANTES del botón sobre el que soltaste
          removeEverywhere(dropped);
          var idx = state.order.indexOf(overCmd);
          if (idx < 0) idx = state.order.length;
          state.order.splice(idx, 0, dropped);
          saveLayout();
          render();
        }
      } else {
        endDrag();
      }
    });
    // modo normal: click = ejecutar (el navegador ya filtra scroll vs tap)
    el.addEventListener('click', function () {
      if (arranging) return;
      exec(c.cmd, el);
    });
    el.addEventListener('pointercancel', function () { endDrag(); });
  }

  function findFolder(name) {
    return state.folders.find(function (x) { return x.name === name; });
  }

  function removeEverywhere(cmd) {
    state.order = state.order.filter(function (x) { return x !== cmd; });
    state.folders.forEach(function (f) {
      f.cmds = f.cmds.filter(function (x) { return x !== cmd; });
    });
  }

  function startDrag() {
    if (!drag) return;
    drag.started = true;
    drag.srcEl.classList.add('lifted');
    var c = byCmd[drag.cmd];
    ghost.querySelector('.ico').textContent = c.ico;
    ghost.querySelector('.glabel').textContent = c.label;
    ghost.style.display = 'flex';
    moveGhost(drag.startX, drag.startY);
  }

  function moveGhost(x, y) {
    ghost.style.left = (x - 48) + 'px';
    ghost.style.top = (y - 42) + 'px';
  }

  function folderAt(x, y) {
    var el = document.elementFromPoint(x, y);
    while (el && el !== document.body) {
      if (el.dataset && el.dataset.folder) return el.dataset.folder;
      el = el.parentNode;
    }
    return null;
  }

  function cmdAt(x, y, exceptEl) {
    var el = document.elementFromPoint(x, y);
    while (el && el !== document.body) {
      if (el.dataset && el.dataset.cmd && el !== exceptEl) return el.dataset.cmd;
      el = el.parentNode;
    }
    return null;
  }

  function markTargets(x, y) {
    var fid = folderAt(x, y);
    document.querySelectorAll('.folder').forEach(function (f) {
      f.classList.toggle('dropTarget', f.dataset.folder === fid);
    });
    var cid = fid ? null : cmdAt(x, y, drag && drag.srcEl);
    document.querySelectorAll('#looseGrid .cmd').forEach(function (t) {
      t.classList.toggle('insertBefore', !!cid && t.dataset.cmd === cid);
    });
  }

  function endDrag() {
    if (drag && drag.srcEl) drag.srcEl.classList.remove('lifted');
    drag = null;
    ghost.style.display = 'none';
    document.querySelectorAll('.folder').forEach(function (f) { f.classList.remove('dropTarget'); });
    document.querySelectorAll('.cmd').forEach(function (t) { t.classList.remove('insertBefore'); });
  }

  // ---- carpetas: crear ------------------------------------------------------
  var row = document.getElementById('newFolderRow');
  var inpName = document.getElementById('newFolderName');
  document.getElementById('btnNewFolder').addEventListener('click', function () {
    row.classList.add('show');
    inpName.value = '';
    inpName.focus();
  });
  document.getElementById('btnFolderCancel').addEventListener('click', function () {
    row.classList.remove('show');
  });
  document.getElementById('btnFolderOk').addEventListener('click', function () {
    var name = (inpName.value || '').trim().replace(/,/g, ' ');
    if (!name) { toast('Poné un nombre'); return; }
    if (findFolder(name)) { toast('Ya hay una carpeta con ese nombre'); return; }
    state.folders.push({ name: name, cmds: [] });
    saveLayout();
    row.classList.remove('show');
    render();
    toast('Carpeta creada — tocá "✋ Ordenar" y arrastrá botones adentro');
  });

  // ---- carpeta abierta -------------------------------------------------------
  var fvFolderName = null;
  function openFolder(name) {
    var f = findFolder(name);
    if (!f) return;
    fvFolderName = name;
    document.getElementById('fvTitle').textContent = '📁 ' + f.name;
    var grid = document.getElementById('fvGrid');
    grid.innerHTML = '';
    if (f.cmds.length === 0) {
      grid.innerHTML = '<div style="grid-column:1/-1; color:#5a675c; font-size:13px; padding:12px">' +
        'Vacía. Cerrá, tocá "✋ Ordenar" y arrastrá botones acá.</div>';
    }
    f.cmds.forEach(function (cmd) {
      var c = byCmd[cmd];
      if (!c) return;
      var d = document.createElement('div');
      d.className = 'cmd';
      d.innerHTML = '<button type="button" class="rm" title="Sacar de la carpeta">✕</button>' +
        '<span class="ico">' + c.ico + '</span><span>' + c.label + '</span>';
      d.addEventListener('click', function (e) {
        if (e.target.classList.contains('rm')) return;
        exec(cmd, d);
      });
      d.querySelector('.rm').addEventListener('click', function () {
        f.cmds = f.cmds.filter(function (x) { return x !== cmd; });
        if (state.order.indexOf(cmd) < 0) state.order.push(cmd);
        saveLayout();
        openFolder(name);
        render();
      });
      grid.appendChild(d);
    });
    document.getElementById('folderView').classList.add('show');
  }
  document.getElementById('fvClose').addEventListener('click', function () {
    document.getElementById('folderView').classList.remove('show');
  });
  document.getElementById('fvDelete').addEventListener('click', function () {
    if (!fvFolderName) return;
    var f = findFolder(fvFolderName);
    if (f) {
      f.cmds.forEach(function (cmd) {
        if (state.order.indexOf(cmd) < 0) state.order.push(cmd);
      });
      state.folders = state.folders.filter(function (x) { return x.name !== fvFolderName; });
      saveLayout();
    }
    fvFolderName = null;
    document.getElementById('folderView').classList.remove('show');
    render();
    toast('Carpeta eliminada — sus botones volvieron a sueltos');
  });

  loadLayout();
})();
