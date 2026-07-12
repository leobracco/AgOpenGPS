// ============================================================================
// config-implemento.js — GET/PUT /api/tool (ToolConfigDto). Port HTML de las
// solapas de Herramienta del FormConfig de PilotX: acople (tabTConfig),
// enganche+pivote (tabTHitch/tabToolPivot), desfase+traslape (tabToolOffset),
// secciones/zonas (tabTSections, DOS modos como WinForms) y anticipo
// (tabTSettings). UI en cm/segundos; el API habla en metros (misma convención
// que vehiculo.js).
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  function pill(state, text) {
    var el = $('implStatus');
    el.className = 'pill ' + (state === 'ok' ? 'ok' : state === 'err' ? 'bad' : '');
    el.innerHTML = '<span class="dot"></span> ' + text;
  }

  function msg(state, text) {
    var el = $('msgImpl');
    el.className = 'msg ' + (state || '');
    el.textContent = text || '';
  }

  // Toast no-bloqueante: refuerza el mensaje inline para que se note en una
  // pantalla táctil mirada de lejos (guardar, aplicar ancho a todas, errores).
  var toastTimer = null;
  function toast(text, kind, ms) {
    var el = $('implToast');
    if (!el) return;
    el.textContent = text;
    el.className = 'show ' + (kind || '');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.className = ''; }, ms || 2600);
  }

  function num(v, def) {
    return (typeof v === 'number' && isFinite(v)) ? v : def;
  }

  // ---- estado de selección (no vive en inputs) ----
  var acople = 'rear';        // rear | trailing | tbt | front
  var offSide = 'right';      // left | right  (offset API: + derecha, − izquierda)
  var ovMode = 'overlap';     // overlap | gap (overlap API: + traslape, − diferencia)
  var sectionOffOut = false;
  var secMode = 'sections';   // sections | zones (setTool_isSectionsNotZones)
  var secWidthsCm = [];       // modo secciones: ancho por sección (cm), largo 16
  var zoneRanges = [];        // modo zonas: última sección de cada zona

  // Sprite de vista lateral por estilo de acople (mismos PNG que WinForms).
  var HITCH_IMG = {
    rear: 'ToolHitchPageRear.png',
    trailing: 'ToolHitchPageTrailing.png',
    tbt: 'ToolHitchPageTBT.png',
    front: 'ToolHitchPageFront.png'
  };

  // ---- acople: los 4 bool del DTO son mutuamente excluyentes ----
  function setAcople(a) {
    acople = a;
    document.querySelectorAll('#acopleCards .sel-card').forEach(function (c) {
      c.classList.toggle('on', c.dataset.acople === a);
    });
    // La lanza y el pivote solo tienen sentido si el implemento cuelga
    // (arrastre o TBT). Montado/frontal: solo distancia de enganche.
    var trailing = (a === 'trailing' || a === 'tbt');
    $('trailingHitchLength').disabled = !trailing;
    $('toolPivotLength').disabled = !trailing;
    var img = $('hitchDiagram');
    if (img) img.src = '../img/tool/' + (HITCH_IMG[a] || HITCH_IMG.rear);
  }

  function setOffSide(s) {
    offSide = s;
    document.querySelectorAll('#offsetSideCards .sel-card').forEach(function (c) {
      c.classList.toggle('on', c.dataset.offside === s);
    });
  }

  function setOvMode(m) {
    ovMode = m;
    document.querySelectorAll('#overlapModeCards .sel-card').forEach(function (c) {
      c.classList.toggle('on', c.dataset.ovmode === m);
    });
  }

  function setSectionOffOut(v) {
    sectionOffOut = !!v;
    $('sectionOffWhenOut').classList.toggle('on', sectionOffOut);
  }

  // ============================ SECCIONES / ZONAS ============================

  function getNumSections() {
    var max = secMode === 'sections' ? 16 : 64;
    return Math.max(1, Math.min(max, parseInt($('numSections').value, 10) || 1));
  }

  function setSecMode(m) {
    secMode = m;
    document.querySelectorAll('#secModeCards .sel-card').forEach(function (c) {
      c.classList.toggle('on', c.dataset.secmode === m);
    });
    var sections = m === 'sections';
    $('secIndivBlock').hidden = !sections;
    $('secZonesBlock').hidden = sections;
    $('numSections').max = sections ? 16 : 64;
    $('secMaxHint').textContent = sections ? 'De 1 a 16 secciones' : 'De 1 a 64 secciones';
    // al achicar el máximo, recortar el valor actual
    $('numSections').value = getNumSections();
    renderSecWidthInputs();
    renderZoneInputs();
    redrawSections();
  }

  // Grid de anchos individuales (modo secciones)
  function renderSecWidthInputs() {
    var grid = $('secWidthsGrid');
    var cntEl = $('secBulkCount');
    if (!grid || secMode !== 'sections') {
      if (grid) grid.innerHTML = '';
      if (cntEl) cntEl.textContent = '—';
      return;
    }
    var n = getNumSections();
    if (cntEl) cntEl.textContent = n;
    var parts = [];
    for (var i = 0; i < n; i++) {
      var w = secWidthsCm[i] > 0 ? secWidthsCm[i] : 200;
      secWidthsCm[i] = w;
      parts.push(
        '<div class="sec-w-cell">' +
          '<label>Sección ' + (i + 1) + '</label>' +
          '<input type="number" class="sec-w-inp" data-idx="' + i + '" min="1" max="1000" step="1" value="' + w + '">' +
        '</div>');
    }
    grid.innerHTML = parts.join('');
    grid.querySelectorAll('.sec-w-inp').forEach(function (inp) {
      inp.addEventListener('input', function () {
        var i = parseInt(inp.dataset.idx, 10);
        secWidthsCm[i] = Math.max(0, parseFloat(inp.value) || 0);
        redrawSections();
      });
    });
  }

  // Botón "Aplicar a todas": pisa el ancho de las N secciones actuales con un
  // solo valor. Confirma antes (AgpModal) porque sobreescribe anchos ya
  // cargados a mano, y avisa el resultado con un toast.
  function applyBulkWidth() {
    var raw = parseFloat($('secWidthBulk').value);
    if (!isFinite(raw) || raw <= 0) {
      toast('Ingresá un ancho válido en cm', 'bad');
      return;
    }
    var w = Math.max(1, Math.min(1000, raw));
    var n = getNumSections();
    var apply = function () {
      for (var i = 0; i < n; i++) secWidthsCm[i] = w;
      renderSecWidthInputs();
      redrawSections();
      toast('✓ ' + w + ' cm aplicado a las ' + n + ' secciones', 'ok');
    };
    if (window.AgpModal) {
      AgpModal.confirm(
        'Aplicar a todas las secciones',
        'Se va a poner ' + w + ' cm en las ' + n + ' secciones y se pisa lo que tengan cargado. ¿Confirmás?'
      ).then(function (yes) { if (yes) apply(); });
    } else {
      apply();
    }
  }

  // Grid de rangos de zona (modo zonas): un <select> por zona con "hasta qué
  // sección llega" — nunca deja elegir un valor inválido (menor al de la zona
  // anterior, o que no deje al menos 1 sección para las zonas que siguen), así
  // que el reparto siempre queda automático y consistente. La última zona
  // siempre cierra en la última sección (igual que WinForms) y se muestra fija.
  function renderZoneInputs() {
    var grid = $('zoneRangesGrid');
    if (!grid || secMode !== 'zones') { if (grid) grid.innerHTML = ''; return; }
    var n = getNumSections();
    var z = getZoneCount();
    $('zoneCount').value = z;

    // reparto parejo por defecto + auto-corrección: cada zona queda acotada
    // entre "1 más que la anterior" y "n menos 1 sección por cada zona que
    // falta", así ninguna combinación de n/z puede dejar un rango imposible.
    var prev = 0;
    for (var i = 0; i < z; i++) {
      var minAllowed = prev + 1;
      var maxAllowed = n - (z - 1 - i);
      var def = Math.round(((i + 1) * n) / z);
      var v = zoneRanges[i] > 0 ? zoneRanges[i] : def;
      zoneRanges[i] = Math.max(minAllowed, Math.min(maxAllowed, v));
      prev = zoneRanges[i];
    }
    zoneRanges[z - 1] = n;

    var parts = [];
    var from = 1;
    for (var j = 0; j < z; j++) {
      var last = j === z - 1;
      var lbl = 'Zona ' + (j + 1) + ': sección ' + from + (zoneRanges[j] > from ? '–' + zoneRanges[j] : '');
      if (last) {
        parts.push(
          '<div class="field">' +
            '<label>' + lbl + (n > from ? '–' + n : '') + '</label>' +
            '<div class="zone-r-fixed">Hasta el final (sección ' + n + ')</div>' +
          '</div>');
      } else {
        var minA = from, maxA = n - (z - 1 - j);
        var opts = [];
        for (var s = minA; s <= maxA; s++) {
          opts.push('<option value="' + s + '"' + (s === zoneRanges[j] ? ' selected' : '') + '>hasta la sección ' + s + '</option>');
        }
        parts.push(
          '<div class="field">' +
            '<label>' + lbl + '</label>' +
            '<select class="zone-r-inp" data-idx="' + j + '">' + opts.join('') + '</select>' +
          '</div>');
      }
      from = zoneRanges[j] + 1;
    }
    grid.innerHTML = parts.join('');
    grid.querySelectorAll('.zone-r-inp').forEach(function (sel) {
      sel.addEventListener('change', function () {
        var i = parseInt(sel.dataset.idx, 10);
        zoneRanges[i] = parseInt(sel.value, 10) || zoneRanges[i];
        // auto-corrige las zonas siguientes si quedaron pisadas por el cambio
        var p = zoneRanges[i];
        for (var k = i + 1; k < z; k++) {
          if (zoneRanges[k] <= p) zoneRanges[k] = p + 1;
          p = zoneRanges[k];
        }
        zoneRanges[z - 1] = n;
        renderZoneInputs();
        redrawSections();
      });
    });
  }

  function getZoneCount() {
    var n = getNumSections();
    return Math.max(1, Math.min(8, n, parseInt($('zoneCount').value, 10) || 1));
  }

  function totalWidthCm() {
    var n = getNumSections();
    if (secMode === 'sections') {
      var t = 0;
      for (var i = 0; i < n; i++) t += (secWidthsCm[i] > 0 ? secWidthsCm[i] : 0);
      return t;
    }
    return n * (parseFloat($('sectionWidthMulti').value) || 0);
  }

  // Zona a la que pertenece la sección (1-based) según zoneRanges
  function zoneOf(sec1) {
    for (var z = 0; z < zoneRanges.length; z++) {
      if (sec1 <= zoneRanges[z]) return z;
    }
    return zoneRanges.length - 1;
  }

  // ---- barra visual: proporcional en secciones, agrupada por zona en zonas ----
  function redrawSections() {
    var g = $('secBar');
    if (!g) return;
    var n = getNumSections();
    var x0 = 14, x1 = 446, y = 60, h = 34;
    var wBar = x1 - x0;
    var parts = [];
    var showNums = n <= 24;

    if (secMode === 'sections') {
      var tot = totalWidthCm() || 1;
      var x = x0;
      for (var i = 0; i < n; i++) {
        var sw = wBar * ((secWidthsCm[i] || 0) / tot);
        parts.push('<rect x="' + x.toFixed(1) + '" y="' + y + '" width="' + Math.max(0, sw - 2).toFixed(1) +
          '" height="' + h + '" rx="3" fill="var(--agp-accent)" opacity="' + (i % 2 ? 0.55 : 0.8) + '"/>');
        if (showNums && sw > 16) {
          parts.push('<text x="' + (x + sw / 2).toFixed(1) + '" y="' + (y + h / 2 + 4) +
            '" text-anchor="middle" style="fill:#fff; font-size:12px; font-weight:700">' + (i + 1) + '</text>');
        }
        x += sw;
      }
    } else {
      var swz = wBar / n;
      for (var k = 0; k < n; k++) {
        var zi = zoneOf(k + 1);
        var sx = x0 + swz * k;
        parts.push('<rect x="' + sx.toFixed(1) + '" y="' + y + '" width="' + Math.max(0.5, swz - 1).toFixed(1) +
          '" height="' + h + '" rx="2" fill="var(--agp-accent)" opacity="' + (zi % 2 ? 0.45 : 0.85) + '"/>');
      }
      // etiquetas de zona centradas bajo su rango
      var from = 1;
      for (var zz = 0; zz < zoneRanges.length; zz++) {
        var to = Math.min(zoneRanges[zz] || from, n);
        var cx = x0 + swz * ((from - 1 + to) / 2);
        parts.push('<text x="' + cx.toFixed(1) + '" y="' + (y + h + 16) +
          '" text-anchor="middle" style="fill: var(--agp-text-muted); font-size:11px; font-weight:700">Z' + (zz + 1) + '</text>');
        from = to + 1;
        if (from > n) break;
      }
    }
    g.innerHTML = parts.join('');
    var tCm = totalWidthCm();
    $('secWidthEach').textContent = secMode === 'zones'
      ? (parseFloat($('sectionWidthMulti').value) || 0).toFixed(1)
      : (n > 0 && tCm > 0 ? (tCm / n).toFixed(1) : '—');
    $('secWidthTotal').textContent = tCm > 0 ? (tCm / 100).toFixed(2) : '—';
  }

  // ---- form <-> DTO ----
  function fillForm(t) {
    if (!t) return;
    // acople: prioridad TBT > trailing > front > rear (default)
    setAcople(t.isToolTBT ? 'tbt' : t.isToolTrailing ? 'trailing' :
              t.isToolFrontFixed ? 'front' : 'rear');

    $('hitchLength').value          = Math.round(Math.abs(num(t.hitchLength, 1)) * 100);
    $('trailingHitchLength').value  = Math.round(Math.abs(num(t.trailingHitchLength, 0)) * 100);
    $('toolPivotLength').value      = Math.round(num(t.trailingToolToPivotLength, 0) * 100);

    var off = num(t.offset, 0);
    $('toolOffset').value = Math.round(Math.abs(off) * 100);
    setOffSide(off < 0 ? 'left' : 'right');

    var ov = num(t.overlap, 0);
    $('toolOverlap').value = Math.round(Math.abs(ov) * 100);
    setOvMode(ov < 0 ? 'gap' : 'overlap');

    // ---- secciones/zonas ----
    var n = Math.max(1, num(t.numSections, 1) | 0);
    $('numSections').value = n;

    secWidthsCm = [];
    if (t.sectionWidths && t.sectionWidths.length) {
      for (var i = 0; i < t.sectionWidths.length; i++) {
        secWidthsCm[i] = Math.round(num(t.sectionWidths[i], 2) * 100);
      }
    } else if (num(t.width, 0) > 0 && n > 0) {
      var eq = Math.round((t.width / n) * 100);
      for (var j = 0; j < n; j++) secWidthsCm[j] = eq;
    }

    var swm = num(t.sectionWidthMulti, 0);
    $('sectionWidthMulti').value = swm > 0 ? +(swm * 100).toFixed(1)
      : (num(t.width, 0) > 0 && n > 0 ? +((t.width / n) * 100).toFixed(1) : 50);

    $('zoneCount').value = Math.max(1, Math.min(8, num(t.zones, 2) | 0));
    zoneRanges = (t.zoneRanges && t.zoneRanges.length) ? t.zoneRanges.slice() : [];

    setSecMode(t.isSectionsNotZones === false ? 'zones' : 'sections');

    // ---- anticipo ----
    $('lookAheadOn').value  = num(t.lookAheadOn, 0.5);
    $('lookAheadOff').value = num(t.lookAheadOff, 0.4);
    $('turnOffDelay').value = num(t.turnOffDelay, 0);
    setSectionOffOut(t.isSectionOffWhenOut);
  }

  function readForm() {
    // Convención AOG: hitch hacia atrás es negativo; la UI pide magnitudes y el
    // estilo de acople decide el signo (frontal = positivo, resto = negativo).
    var hitchCm = Math.abs(parseFloat($('hitchLength').value) || 0);
    var hitchM = (acople === 'front' ? 1 : -1) * hitchCm / 100;
    var offCm = Math.abs(parseFloat($('toolOffset').value) || 0);
    var ovCm = Math.abs(parseFloat($('toolOverlap').value) || 0);

    var n = getNumSections();
    var widthsM = null;
    if (secMode === 'sections') {
      widthsM = [];
      for (var i = 0; i < n; i++) widthsM[i] = (secWidthsCm[i] > 0 ? secWidthsCm[i] : 200) / 100;
    }
    var z = getZoneCount();
    var zr = [];
    for (var j = 0; j < z; j++) zr[j] = Math.max(1, Math.min(n, zoneRanges[j] || n));
    zr[z - 1] = n;

    return {
      width: totalWidthCm() / 100,
      overlap: (ovMode === 'gap' ? -1 : 1) * ovCm / 100,
      offset: (offSide === 'left' ? -1 : 1) * offCm / 100,
      numSections: n,
      isSectionsNotZones: secMode === 'sections',
      sectionWidths: widthsM,
      sectionWidthMulti: (parseFloat($('sectionWidthMulti').value) || 0) / 100,
      zones: z,
      zoneRanges: zr,
      hitchLength: hitchM,
      trailingHitchLength: -Math.abs(parseFloat($('trailingHitchLength').value) || 0) / 100,
      trailingToolToPivotLength: (parseFloat($('toolPivotLength').value) || 0) / 100,
      lookAheadOn: parseFloat($('lookAheadOn').value) || 0,
      lookAheadOff: parseFloat($('lookAheadOff').value) || 0,
      turnOffDelay: parseFloat($('turnOffDelay').value) || 0,
      // OJO: en PilotX TBT implica trailing (el service hace
      // isToolTBT = cfg.IsToolTBT && trailing) — mandar ambos en true.
      isToolTrailing: acople === 'trailing' || acople === 'tbt',
      isToolTBT: acople === 'tbt',
      isToolRearFixed: acople === 'rear',
      isToolFrontFixed: acople === 'front',
      isSectionOffWhenOut: sectionOffOut
    };
  }

  // Validación suave de zonas antes de mandar (rangos ascendentes).
  function zonesValid() {
    if (secMode !== 'zones') return true;
    var prev = 0;
    for (var i = 0; i < getZoneCount(); i++) {
      var r = zoneRanges[i] || 0;
      if (r <= prev) return false;
      prev = r;
    }
    return true;
  }

  async function load() {
    pill('', 'Cargando…');
    try {
      var res = await fetch('/api/tool', { cache: 'no-store' });
      var data = await res.json();
      if (!data.ok) throw new Error(data.error || 'GET falló');
      fillForm(data.tool);
      pill('ok', 'OK');
    } catch (e) {
      pill('err', 'Error');
      msg('err', '✕ ' + e.message);
      toast('No se pudo cargar la configuración: ' + e.message, 'bad');
    }
  }

  async function save() {
    if (!zonesValid()) {
      var zErr = 'Las zonas deben ser crecientes: cada "hasta la sección" mayor que el de la zona anterior.';
      msg('err', '✕ ' + zErr);
      toast(zErr, 'bad');
      return;
    }
    msg('', 'Guardando…');
    try {
      var cfg = readForm();
      var res = await fetch('/api/tool', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cfg)
      });
      var data = await res.json();
      if (data.ok) {
        msg('ok', '✓ Guardado y aplicado.');
        toast('✓ Guardado y aplicado', 'ok');
      } else {
        msg('err', '✕ ' + (data.error || 'no se pudo guardar'));
        toast(data.error || 'No se pudo guardar', 'bad');
      }
    } catch (e) {
      msg('err', '✕ ' + e.message);
      toast('Error al guardar: ' + e.message, 'bad');
    }
  }

  // ---- tabs ----
  document.querySelectorAll('.tabs .tab').forEach(function (t) {
    t.addEventListener('click', function () {
      document.querySelectorAll('.tabs .tab').forEach(function (x) { x.classList.remove('on'); });
      document.querySelectorAll('.panel').forEach(function (p) { p.classList.remove('on'); });
      t.classList.add('on');
      var p = document.getElementById('panel-' + t.dataset.panel);
      if (p) p.classList.add('on');
    });
  });

  // ---- wiring de selecciones ----
  document.querySelectorAll('#acopleCards .sel-card').forEach(function (c) {
    c.addEventListener('click', function () { setAcople(c.dataset.acople); });
  });
  document.querySelectorAll('#offsetSideCards .sel-card').forEach(function (c) {
    c.addEventListener('click', function () { setOffSide(c.dataset.offside); });
  });
  document.querySelectorAll('#overlapModeCards .sel-card').forEach(function (c) {
    c.addEventListener('click', function () { setOvMode(c.dataset.ovmode); });
  });
  document.querySelectorAll('#secModeCards .sel-card').forEach(function (c) {
    c.addEventListener('click', function () { setSecMode(c.dataset.secmode); });
  });
  $('sectionOffWhenOut').addEventListener('click', function () { setSectionOffOut(!sectionOffOut); });

  // ---- steppers + redibujo ----
  function bumpSections(d) {
    var inp = $('numSections');
    var max = secMode === 'sections' ? 16 : 64;
    inp.value = Math.max(1, Math.min(max, (parseInt(inp.value, 10) || 1) + d));
    renderSecWidthInputs();
    renderZoneInputs();
    redrawSections();
  }
  $('secMinus').addEventListener('click', function () { bumpSections(-1); });
  $('secPlus').addEventListener('click', function () { bumpSections(1); });
  $('numSections').addEventListener('input', function () {
    renderSecWidthInputs(); renderZoneInputs(); redrawSections();
  });
  $('btnSecWidthBulk').addEventListener('click', applyBulkWidth);

  function bumpZones(d) {
    var inp = $('zoneCount');
    var maxZ = Math.min(8, getNumSections());
    inp.value = Math.max(1, Math.min(maxZ, (parseInt(inp.value, 10) || 1) + d));
    renderZoneInputs();
    redrawSections();
  }
  $('zoneMinus').addEventListener('click', function () { bumpZones(-1); });
  $('zonePlus').addEventListener('click', function () { bumpZones(1); });
  $('zoneCount').addEventListener('input', function () { renderZoneInputs(); redrawSections(); });
  $('sectionWidthMulti').addEventListener('input', redrawSections);

  $('btnSaveImpl').addEventListener('click', save);
  $('btnReloadImpl').addEventListener('click', function () { msg('', ''); load(); });

  load();
})();
