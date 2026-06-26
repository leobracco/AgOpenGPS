// ============================================================================
// piloto.js — mapa Piloto (Canvas 2D).
//   · Recibe AogStateSnapshot por polling de /api/aog/state (10 Hz).
//   · Render: grid en metros, sprite del tractor según marca configurada
//     (TractorJohnDeere.png, HarvesterClaas.png, ...), barra de herramienta
//     atrás del tractor con N secciones coloreadas por sectionOnRequest[i].
//   · Cámara: sigue al tractor (auto-follow). Pan con drag, zoom con wheel.
//   · Sin tiles OSM — el piloto opera en metros locales (Easting/Northing), no lat/lon-tile.
// ============================================================================

(function () {
  'use strict';

  // ---- DOM ----
  var canvas    = document.getElementById('map');
  var ctx       = canvas.getContext('2d');
  var statusEl  = document.getElementById('mapStatus');
  var hudSpeed  = document.getElementById('hudSpeed');
  var hudHead   = document.getElementById('hudHeading');
  var hudArea   = document.getElementById('hudArea');
  var hudLatLon = document.getElementById('hudLatLon');
  var hudDose   = document.getElementById('hudDose');
  var btnFollow = document.getElementById('btnFollow');
  var btnZoomIn = document.getElementById('btnZoomIn');
  var btnZoomOut= document.getElementById('btnZoomOut');
  var btnCovReset = document.getElementById('btnCovReset');

  // ---- estado vista ----
  // pxPerMeter: px en pantalla por metro de mundo. 30 px/m = 1 px = 3.3cm aprox.
  // dragOffset: solo activo en modo "no-follow" (cuando el usuario está pannlando).
  var view = {
    pxPerMeter: 30,
    follow: true,
    cameraE: 0, cameraN: 0,    // centro del viewport en coords mundo (metros)
    dragOffsetE: 0, dragOffsetN: 0
  };

  // ---- estado snapshot último ----
  var snap = {
    isJobStarted: false,
    avgSpeed: 0, heading: 0,
    pivotEasting: 0, pivotNorthing: 0,
    latitude: 0, longitude: 0,
    numSections: 0, sectionOnRequest: [],
    sectionPositions: [],
    toolWidth: 0,
    shapeCurrentDose: 0, shapeIsInside: false,
    vehicleType: 'Tractor', vehicleBrand: 'AGOpenGPS',
    boundaries: [], headlands: [], activeTrack: null
  };

  // ---- cobertura persistente (paint como AoG) ----
  // En lugar de re-pintar trapecios desde un array RAM con tope, mantenemos un
  // canvas oculto (`coverage.canvas`) en coords mundo. Cada tick pintamos sobre
  // ese canvas los trapecios por sección entre la última muestra y la actual;
  // en render() blitteamos la región sobre la pantalla con la transformación
  // de la vista. La cobertura sobrevive a la jornada sin perder rastro.
  //
  // - COV_PX_PER_M = 4 → 1 px = 25 cm (suficiente para visualización en cabina).
  // - COV_INIT = 512 → arranca cubriendo 128 × 128 m, crece por duplicación
  //   cuando el tractor se sale del bound, capeado en COV_MAX × COV_MAX.
  // - COV_MAX = 4096 → 64 MB de pixmap como techo (1024 m × 1024 m a 4 px/m).
  //   Para lotes más grandes el operario usa "Borrar cobertura" o se acepta
  //   que no se pinten los bordes lejanos.
  var COV_PX_PER_M = 4;
  var COV_INIT     = 512;
  var COV_MAX      = 4096;
  var coverage = null;        // { canvas, ctx, pxPerM, w, h, originE, originN }
  var lastCovSample = null;   // { e, n, h, on[] } de la muestra anterior

  // Capa shapefile activa (prescripción/dosis). El servidor emite el set
  // completo a 1 Hz (cambia poco). polygons[i] = { r,g,b,a, rings:[[e,n,...], ...] }
  // — primer ring contorno exterior, los siguientes agujeros.
  var shape = { sourceToken: '', count: 0, styleField: null, polygons: [] };
  var shapeVisible = true;

  // ---- sprites ----
  var spriteCache = {};
  // Marcas reconocidas. Si la marca seleccionada no existe para el tipo, caemos a AoG.
  var TRACTOR_BRANDS = ['AGOpenGPS','Case','Claas','Deutz','Fendt','JCB','JohnDeere','Kubota','Massey','NewHolland','Same','Steyr','Ursus','Valtra'];
  var HARVESTER_BRANDS = ['AgOpenGPS','Case','Claas','JohnDeere','NewHolland'];
  var ARTICULATED_BRANDS = ['AgOpenGPS','Case','Challenger','Holder','JohnDeere','NewHolland'];
  function normalizeBrand(b) { return (b || '').replace(/^Ag/i, 'Ag').replace(/^AGOpenGPS$/, 'AoG'); }
  function pickBrand(list, brand) {
    if (!brand) return list[0];
    // map "AGOpenGPS"/"AgOpenGPS" → "AoG"
    if (brand === 'AGOpenGPS' || brand === 'AgOpenGPS') return 'AoG';
    return list.indexOf(brand) >= 0 ? brand : list[0];
  }
  function spritePathFor(type, brand) {
    var t = (type || 'Tractor').toLowerCase();
    if (t === 'harvester') {
      var hb = pickBrand(HARVESTER_BRANDS, brand);
      return '../img/harvesters/Harvester' + hb + '.png';
    }
    if (t === 'articulated') {
      // Devolvemos solo el rear; el front se compone aparte en drawTractor().
      var ab = pickBrand(ARTICULATED_BRANDS, brand);
      return '../img/articulated/ArticulatedRear' + ab + '.png';
    }
    var tb = pickBrand(TRACTOR_BRANDS, brand);
    return '../img/tractors/Tractor' + tb + '.png';
  }
  function articulatedFrontPath(brand) {
    var ab = pickBrand(ARTICULATED_BRANDS, brand);
    return '../img/articulated/ArticulatedFront' + ab + '.png';
  }

  // Detección heurística del tipo de implemento por ancho.
  function detectImplement(widthM, numSections) {
    if (!widthM || widthM <= 0) return { kind: 'sin', label: 'sin herramienta', color: '#4A5055' };
    if (widthM >= 12)  return { kind: 'pulverizadora', label: 'pulverizadora · ' + widthM.toFixed(1) + ' m', color: '#3D8BFD' };
    if (widthM >= 4)   return { kind: 'siembra',       label: 'siembra · ' + widthM.toFixed(1) + ' m',        color: '#4BA63F' };
    return { kind: 'implemento', label: 'implemento · ' + widthM.toFixed(1) + ' m', color: '#8A8F95' };
  }
  function loadSprite(path) {
    if (spriteCache[path]) return spriteCache[path];
    var img = new Image();
    img.src = path;
    img.onerror = function () {
      // Fallback al sprite genérico.
      if (path !== '../img/tractors/TractorAoG.png') {
        spriteCache[path] = spriteCache['../img/tractors/TractorAoG.png'] || loadSprite('../img/tractors/TractorAoG.png');
      }
    };
    spriteCache[path] = img;
    return img;
  }
  // Pre-warm sprite genérico
  loadSprite('../img/tractors/TractorAoG.png');

  // ---- canvas hi-DPI ----
  function resizeCanvas() {
    var dpr = window.devicePixelRatio || 1;
    var rect = canvas.getBoundingClientRect();
    canvas.width  = Math.max(1, Math.floor(rect.width  * dpr));
    canvas.height = Math.max(1, Math.floor(rect.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  window.addEventListener('resize', resizeCanvas);

  // ---- conversiones mundo↔pantalla ----
  function worldToScreen(e, n) {
    var rect = canvas.getBoundingClientRect();
    var cx = rect.width / 2;
    var cy = rect.height / 2;
    var dx = (e - view.cameraE) * view.pxPerMeter;
    var dy = (view.cameraN - n) * view.pxPerMeter; // Y invertido (norte hacia arriba)
    return { x: cx + dx, y: cy + dy };
  }
  function screenToWorld(x, y) {
    var rect = canvas.getBoundingClientRect();
    var cx = rect.width / 2;
    var cy = rect.height / 2;
    var e = view.cameraE + (x - cx) / view.pxPerMeter;
    var n = view.cameraN - (y - cy) / view.pxPerMeter;
    return { e: e, n: n };
  }

  // ---- pan / zoom ----
  var isDragging = false, dragStart = null;
  canvas.addEventListener('mousedown', function (ev) {
    isDragging = true;
    dragStart = { x: ev.clientX, y: ev.clientY, cE: view.cameraE, cN: view.cameraN };
  });
  window.addEventListener('mousemove', function (ev) {
    if (!isDragging) return;
    var dx = (ev.clientX - dragStart.x) / view.pxPerMeter;
    var dy = (ev.clientY - dragStart.y) / view.pxPerMeter;
    view.cameraE = dragStart.cE - dx;
    view.cameraN = dragStart.cN + dy;
    setFollow(false);
  });
  window.addEventListener('mouseup', function () { isDragging = false; });
  canvas.addEventListener('wheel', function (ev) {
    ev.preventDefault();
    var k = ev.deltaY < 0 ? 1.15 : 1/1.15;
    var rect = canvas.getBoundingClientRect();
    var w = screenToWorld(ev.clientX - rect.left, ev.clientY - rect.top);
    view.pxPerMeter = Math.max(2, Math.min(200, view.pxPerMeter * k));
    var w2 = screenToWorld(ev.clientX - rect.left, ev.clientY - rect.top);
    view.cameraE += (w.e - w2.e);
    view.cameraN += (w.n - w2.n);
  }, { passive: false });

  function setFollow(on) {
    view.follow = on;
    if (btnFollow) btnFollow.classList.toggle('primary', on);
  }
  if (btnFollow)  btnFollow.addEventListener('click', function () { setFollow(true); });
  if (btnZoomIn)  btnZoomIn.addEventListener('click', function () { view.pxPerMeter = Math.min(200, view.pxPerMeter * 1.25); });
  if (btnZoomOut) btnZoomOut.addEventListener('click', function () { view.pxPerMeter = Math.max(2, view.pxPerMeter / 1.25); });
  if (btnCovReset) btnCovReset.addEventListener('click', function () {
    if (!confirm('¿Borrar la cobertura pintada del mapa?')) return;
    resetCoverage();
  });

  // ---- dibujo ----
  function drawGrid() {
    var rect = canvas.getBoundingClientRect();
    var step = view.pxPerMeter >= 20 ? 1 : (view.pxPerMeter >= 4 ? 10 : 50); // 1m / 10m / 50m
    var stepPx = step * view.pxPerMeter;
    var originScreen = worldToScreen(0, 0);

    // Líneas finas
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(255,255,255,0.04)';
    var ox = originScreen.x % stepPx;
    var oy = originScreen.y % stepPx;
    ctx.beginPath();
    for (var x = ox; x < rect.width; x += stepPx) { ctx.moveTo(x, 0); ctx.lineTo(x, rect.height); }
    for (var y = oy; y < rect.height; y += stepPx) { ctx.moveTo(0, y); ctx.lineTo(rect.width, y); }
    ctx.stroke();

    // Cada 10 pasos un trazo medio
    ctx.strokeStyle = 'rgba(255,255,255,0.08)';
    ctx.beginPath();
    var bigStep = stepPx * 10;
    ox = originScreen.x % bigStep;
    oy = originScreen.y % bigStep;
    for (var x2 = ox; x2 < rect.width; x2 += bigStep) { ctx.moveTo(x2, 0); ctx.lineTo(x2, rect.height); }
    for (var y2 = oy; y2 < rect.height; y2 += bigStep) { ctx.moveTo(0, y2); ctx.lineTo(rect.width, y2); }
    ctx.stroke();
  }

  function pickPt(p) { return { e: (p.e != null ? p.e : p.E) || 0, n: (p.n != null ? p.n : p.N) || 0 }; }

  function drawPolyline(pts, closed, stroke, lineWidth, fill, dash) {
    if (!pts || pts.length < 2) return;
    ctx.save();
    if (dash) ctx.setLineDash(dash);
    ctx.lineWidth = lineWidth;
    ctx.strokeStyle = stroke;
    if (fill) ctx.fillStyle = fill;
    ctx.beginPath();
    var p0 = pickPt(pts[0]);
    var s0 = worldToScreen(p0.e, p0.n);
    ctx.moveTo(s0.x, s0.y);
    for (var i = 1; i < pts.length; i++) {
      var p = pickPt(pts[i]);
      var s = worldToScreen(p.e, p.n);
      ctx.lineTo(s.x, s.y);
    }
    if (closed) ctx.closePath();
    if (fill) ctx.fill();
    ctx.stroke();
    ctx.restore();
  }

  function drawShape() {
    if (!shapeVisible) return;
    var polys = shape.polygons;
    if (!polys || !polys.length) return;
    ctx.save();
    for (var p = 0; p < polys.length; p++) {
      var pg = polys[p];
      var rings = pg.rings;
      if (!rings || !rings.length) continue;

      var alpha = (pg.a != null ? pg.a : 80) / 255;
      ctx.fillStyle = 'rgba(' + pg.r + ',' + pg.g + ',' + pg.b + ',' + alpha.toFixed(3) + ')';
      ctx.strokeStyle = 'rgba(' + pg.r + ',' + pg.g + ',' + pg.b + ',0.95)';
      ctx.lineWidth = 1.2;

      // Fill (evenodd permite agujeros si vienen rings adicionales).
      ctx.beginPath();
      for (var r = 0; r < rings.length; r++) {
        var ring = rings[r];
        if (!ring || ring.length < 6) continue;
        var s0 = worldToScreen(ring[0], ring[1]);
        ctx.moveTo(s0.x, s0.y);
        for (var i = 2; i < ring.length; i += 2) {
          var s = worldToScreen(ring[i], ring[i + 1]);
          ctx.lineTo(s.x, s.y);
        }
        ctx.closePath();
      }
      ctx.fill('evenodd');

      // Outline solo del contorno exterior (ring 0).
      var outer = rings[0];
      if (outer && outer.length >= 6) {
        ctx.beginPath();
        var o0 = worldToScreen(outer[0], outer[1]);
        ctx.moveTo(o0.x, o0.y);
        for (var k = 2; k < outer.length; k += 2) {
          var o = worldToScreen(outer[k], outer[k + 1]);
          ctx.lineTo(o.x, o.y);
        }
        ctx.closePath();
        ctx.stroke();
      }
    }
    ctx.restore();
  }

  function drawBoundaries() {
    var bnds = snap.boundaries || [];
    var hdls = snap.headlands || [];
    // Fondo del lote: relleno suave del contorno externo (índice 0).
    if (bnds.length > 0 && bnds[0] && bnds[0].length > 2) {
      drawPolyline(bnds[0], true, '#8A8F95', 2, 'rgba(75,166,63,0.08)');
    }
    // Islas (drive-thru): relleno gris distinto, contorno punteado.
    for (var i = 1; i < bnds.length; i++) {
      drawPolyline(bnds[i], true, '#8A8F95', 1.5, 'rgba(22,24,26,0.6)', [6, 4]);
    }
    // Headlands punteado
    for (var j = 0; j < hdls.length; j++) {
      drawPolyline(hdls[j], true, 'rgba(138,143,149,0.7)', 1, null, [4, 4]);
    }
  }

  function drawActiveTrack() {
    var t = snap.activeTrack;
    if (!t) return;
    var mode = (t.mode || '').toLowerCase();
    var stroke = '#E27A0F'; // naranja Agro Parallel

    if (mode.indexOf('curve') >= 0 || mode.indexOf('pivot') >= 0) {
      var pts = t.curvePts || t.CurvePts || [];
      drawPolyline(pts, false, stroke, 2);
      return;
    }

    // AB line: extender desde el heading de A en ambos sentidos ~1km.
    var A = t.A || t.a;
    var B = t.B || t.b;
    if (!A || !B) return;
    var ax = (A.e != null ? A.e : A.E);
    var ay = (A.n != null ? A.n : A.N);
    var bx = (B.e != null ? B.e : B.E);
    var by = (B.n != null ? B.n : B.N);
    var dx = bx - ax, dy = by - ay;
    var len = Math.hypot(dx, dy);
    if (len < 0.1) return;
    var ux = dx/len, uy = dy/len;
    var EXT = 2000; // 2 km a cada lado
    var p1 = worldToScreen(ax - ux*EXT, ay - uy*EXT);
    var p2 = worldToScreen(bx + ux*EXT, by + uy*EXT);

    ctx.save();
    ctx.lineWidth = 2;
    ctx.strokeStyle = stroke;
    ctx.beginPath();
    ctx.moveTo(p1.x, p1.y);
    ctx.lineTo(p2.x, p2.y);
    ctx.stroke();

    // marcadores A y B
    var sA = worldToScreen(ax, ay);
    var sB = worldToScreen(bx, by);
    ctx.fillStyle = stroke;
    ctx.beginPath(); ctx.arc(sA.x, sA.y, 4, 0, Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(sB.x, sB.y, 4, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = '#DDE0E3';
    ctx.font = 'bold 11px system-ui, sans-serif';
    ctx.textAlign = 'left'; ctx.textBaseline = 'middle';
    ctx.fillText('A', sA.x + 6, sA.y);
    ctx.fillText('B', sB.x + 6, sB.y);
    ctx.restore();
  }

  // ---- cobertura: init / grow / paint / blit ----

  function initCoverage(centerE, centerN) {
    var c = document.createElement('canvas');
    c.width = COV_INIT;
    c.height = COV_INIT;
    coverage = {
      canvas:  c,
      ctx:     c.getContext('2d'),
      pxPerM:  COV_PX_PER_M,
      w:       COV_INIT,
      h:       COV_INIT,
      // El origen (px 0,0 del canvas) representa la esquina superior-izquierda
      // del bbox cubierto. Norte arriba: increasing Y en canvas → decreasing N.
      originE: centerE - (COV_INIT / COV_PX_PER_M) / 2,
      originN: centerN + (COV_INIT / COV_PX_PER_M) / 2
    };
  }

  function covX(e) { return (e - coverage.originE) * coverage.pxPerM; }
  function covY(n) { return (coverage.originN - n) * coverage.pxPerM; }

  // Crece el canvas (duplicando) hasta que el rect mundo [minE,maxE]x[minN,maxN]
  // quepa, o hasta llegar al cap. Si llega al cap, devuelve false y los trapecios
  // que caigan fuera serán recortados por el ctx (no rompe nada, solo no se pinta).
  function growCoverage(minE, minN, maxE, maxN) {
    while (true) {
      var curMinE = coverage.originE;
      var curMaxE = coverage.originE + coverage.w / coverage.pxPerM;
      var curMaxN = coverage.originN;
      var curMinN = coverage.originN - coverage.h / coverage.pxPerM;

      var needLeft  = minE < curMinE;
      var needRight = maxE > curMaxE;
      var needDown  = minN < curMinN;
      var needUp    = maxN > curMaxN;

      if (!needLeft && !needRight && !needDown && !needUp) return true;
      if (coverage.w >= COV_MAX && coverage.h >= COV_MAX) return false;

      var newW = coverage.w, newH = coverage.h;
      var offX = 0, offY = 0;
      if ((needLeft || needRight) && newW < COV_MAX) {
        newW = Math.min(COV_MAX, newW * 2);
        if      (needLeft && !needRight) offX = newW - coverage.w;
        else if (needLeft && needRight)  offX = Math.floor((newW - coverage.w) / 2);
      }
      if ((needUp || needDown) && newH < COV_MAX) {
        newH = Math.min(COV_MAX, newH * 2);
        // Y crece hacia abajo en canvas → si necesitamos S (decreasing N),
        // el contenido viejo debe quedar arriba (offY=0). Si necesitamos N,
        // el viejo va abajo (offY = newH - h).
        if      (needUp && !needDown) offY = newH - coverage.h;
        else if (needUp && needDown)  offY = Math.floor((newH - coverage.h) / 2);
      }

      var newCanvas = document.createElement('canvas');
      newCanvas.width = newW;
      newCanvas.height = newH;
      var nctx = newCanvas.getContext('2d');
      nctx.drawImage(coverage.canvas, offX, offY);

      // Ajustar origin: el píxel (0,0) viejo está ahora en (offX, offY).
      coverage.originE = coverage.originE - offX / coverage.pxPerM;
      coverage.originN = coverage.originN + offY / coverage.pxPerM;
      coverage.canvas  = newCanvas;
      coverage.ctx     = nctx;
      coverage.w       = newW;
      coverage.h       = newH;

      if (newW >= COV_MAX && newH >= COV_MAX) {
        // No podemos seguir creciendo. Devolvemos el estado actual: si todavía
        // no cubre, los trapecios fuera se recortan al pintar — sin error.
        return (!needLeft && !needRight && !needUp && !needDown);
      }
    }
  }

  // Pinta el segmento entre dos muestras consecutivas (a → b) sobre el canvas
  // de cobertura, en coords mundo→canvas. Una franja por sección que esté ON
  // en ambos extremos (evita pintar fantasmas cuando se apaga a mitad de pasada).
  function paintCoverageSegment(a, b) {
    if (!a || !b) return;
    var sp = snap.sectionPositions || [];
    var nSec = sp.length;
    if (nSec === 0) return; // sin geometría por sección → no pintamos aún

    if (!coverage) initCoverage((a.e + b.e) / 2, (a.n + b.n) / 2);

    // BBox mundo del segmento + margen por el ancho de la barra.
    var half = (snap.toolWidth || 6) * 0.6;
    var bbMinE = Math.min(a.e, b.e) - half;
    var bbMaxE = Math.max(a.e, b.e) + half;
    var bbMinN = Math.min(a.n, b.n) - half;
    var bbMaxN = Math.max(a.n, b.n) + half;
    growCoverage(bbMinE, bbMinN, bbMaxE, bbMaxN);

    var c = coverage.ctx;
    c.fillStyle = 'rgba(74,186,62,0.55)';

    var hA = (typeof a.h === 'number') ? a.h : 0;
    var hB = (typeof b.h === 'number') ? b.h : 0;
    // easting = sin(h), northing = cos(h) → "derecha" del avance = (cos(h), -sin(h))
    var rxA = Math.cos(hA), ryA = -Math.sin(hA);
    var rxB = Math.cos(hB), ryB = -Math.sin(hB);

    var aOn = a.on || [];
    var bOn = b.on || [];
    for (var j = 0; j < nSec; j++) {
      if (!aOn[j] || !bOn[j]) continue;
      var ext = sp[j];
      if (!ext) continue;
      var L = ext.left, R = ext.right;
      var p1x = covX(a.e + rxA * L), p1y = covY(a.n + ryA * L);
      var p2x = covX(a.e + rxA * R), p2y = covY(a.n + ryA * R);
      var p3x = covX(b.e + rxB * R), p3y = covY(b.n + ryB * R);
      var p4x = covX(b.e + rxB * L), p4y = covY(b.n + ryB * L);
      c.beginPath();
      c.moveTo(p1x, p1y);
      c.lineTo(p2x, p2y);
      c.lineTo(p3x, p3y);
      c.lineTo(p4x, p4y);
      c.closePath();
      c.fill();
    }
  }

  function drawCoverage() {
    if (!coverage) return;
    var tl = worldToScreen(coverage.originE, coverage.originN);
    var dw = (coverage.w / coverage.pxPerM) * view.pxPerMeter;
    var dh = (coverage.h / coverage.pxPerM) * view.pxPerMeter;
    // Clipping al viewport lo hace el browser; sin smoothing para que las
    // franjas no queden borrosas al hacer zoom out.
    var prev = ctx.imageSmoothingEnabled;
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(coverage.canvas, tl.x, tl.y, dw, dh);
    ctx.imageSmoothingEnabled = prev;
  }

  function resetCoverage() {
    coverage = null;
    lastCovSample = null;
  }

  function drawSectionBar() {
    // Barra de herramienta (implemento) detrás del tractor.
    var width = snap.toolWidth || 0;
    var n = snap.numSections || 0;
    if (width <= 0 || n <= 0) return;

    var impl = detectImplement(width, n);
    var pivot = worldToScreen(snap.pivotEasting, snap.pivotNorthing);
    ctx.save();
    ctx.translate(pivot.x, pivot.y);
    ctx.rotate(snap.heading);

    var wpx = width * view.pxPerMeter;
    var hpx = Math.max(8, 0.6 * view.pxPerMeter);
    var backOffset = Math.max(12, 0.8 * view.pxPerMeter);

    // Línea de enganche (chasis a barra)
    ctx.strokeStyle = 'rgba(0,0,0,0.45)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(0, backOffset);
    ctx.stroke();

    // Fondo barra
    ctx.fillStyle = 'rgba(255,255,255,0.05)';
    ctx.fillRect(-wpx/2, backOffset, wpx, hpx);

    // Cada sección
    var segW = wpx / n;
    for (var i = 0; i < n; i++) {
      var on = !!(snap.sectionOnRequest && snap.sectionOnRequest[i]);
      ctx.fillStyle = on ? impl.color : '#4A5055';
      ctx.fillRect(-wpx/2 + i*segW + 1, backOffset + 1, segW - 2, hpx - 2);
    }
    // Borde
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(255,255,255,0.20)';
    ctx.strokeRect(-wpx/2, backOffset, wpx, hpx);

    // Detalles según tipo
    if (impl.kind === 'pulverizadora') {
      // Picos pulverizadores: triangulitos abajo cada ~50cm
      var nozzleStep = Math.max(8, 0.5 * view.pxPerMeter);
      ctx.fillStyle = 'rgba(0,0,0,0.55)';
      for (var x = -wpx/2 + nozzleStep/2; x < wpx/2; x += nozzleStep) {
        ctx.beginPath();
        ctx.moveTo(x, backOffset + hpx);
        ctx.lineTo(x - 2, backOffset + hpx + 5);
        ctx.lineTo(x + 2, backOffset + hpx + 5);
        ctx.closePath();
        ctx.fill();
      }
    } else if (impl.kind === 'siembra') {
      // Sembradora: discos representados como puntos
      ctx.fillStyle = 'rgba(0,0,0,0.55)';
      var nDisks = Math.max(4, Math.round(width / 0.525)); // ~52.5cm entre líneas
      for (var d = 0; d < nDisks; d++) {
        var px = -wpx/2 + (d + 0.5) * (wpx / nDisks);
        ctx.beginPath();
        ctx.arc(px, backOffset + hpx/2, Math.max(1.5, hpx*0.18), 0, Math.PI*2);
        ctx.fill();
      }
    }
    ctx.restore();
  }

  function drawTractor() {
    var pivot = worldToScreen(snap.pivotEasting, snap.pivotNorthing);
    var type = (snap.vehicleType || 'Tractor').toLowerCase();
    var lengthM = type === 'harvester' ? 8 : (type === 'articulated' ? 9 : 5);
    var hpx = lengthM * view.pxPerMeter;

    ctx.save();
    ctx.translate(pivot.x, pivot.y);
    ctx.rotate(snap.heading);

    if (type === 'articulated') {
      // Articulado: dos sprites (rear + front), centrados en el pivote (axe trasero).
      var rear = loadSprite(spritePathFor('Articulated', snap.vehicleBrand));
      var front = loadSprite(articulatedFrontPath(snap.vehicleBrand));
      var halfH = hpx / 2;
      // El "rear" va detrás del pivote, el "front" delante
      if (rear && rear.complete && rear.naturalWidth > 0) {
        var rw = halfH * (rear.naturalWidth / rear.naturalHeight);
        ctx.drawImage(rear, -rw/2, 0, rw, halfH);
      }
      if (front && front.complete && front.naturalWidth > 0) {
        var fw = halfH * (front.naturalWidth / front.naturalHeight);
        ctx.drawImage(front, -fw/2, -halfH, fw, halfH);
      }
    } else {
      var spritePath = spritePathFor(snap.vehicleType, snap.vehicleBrand);
      var img = loadSprite(spritePath);
      var wpx;
      if (img && img.complete && img.naturalWidth > 0) {
        wpx = hpx * (img.naturalWidth / img.naturalHeight);
        ctx.drawImage(img, -wpx/2, -hpx/2, wpx, hpx);
      } else {
        wpx = hpx * 0.5;
        ctx.fillStyle = '#8A8F95';
        ctx.fillRect(-wpx/2, -hpx/2, wpx, hpx);
      }
    }
    ctx.restore();

    // Punto pivote
    ctx.fillStyle = '#DDE0E3';
    ctx.beginPath();
    ctx.arc(pivot.x, pivot.y, 3, 0, Math.PI * 2);
    ctx.fill();
  }

  function drawCompass() {
    var rect = canvas.getBoundingClientRect();
    var cx = rect.width - 50;
    var cy = 50;
    var r = 28;
    ctx.save();
    ctx.fillStyle = 'rgba(32,35,38,0.85)';
    ctx.strokeStyle = '#3B4046';
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI*2); ctx.fill(); ctx.stroke();
    // N
    ctx.fillStyle = '#DDE0E3';
    ctx.font = 'bold 12px system-ui, sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText('N', cx, cy - r + 9);
    // flecha del rumbo
    ctx.translate(cx, cy);
    ctx.rotate(snap.heading);
    ctx.fillStyle = '#4BA63F';
    ctx.beginPath();
    ctx.moveTo(0, -r + 6);
    ctx.lineTo(5, 4);
    ctx.lineTo(0, 0);
    ctx.lineTo(-5, 4);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  function drawScaleBar() {
    var rect = canvas.getBoundingClientRect();
    // elegimos un “lindo” en metros
    var target = 80; // px deseado
    var meters = target / view.pxPerMeter;
    // redondear a 1, 2, 5, 10, 20, 50, ...
    var pow10 = Math.pow(10, Math.floor(Math.log10(meters)));
    var rel = meters / pow10;
    var nice = rel < 1.5 ? 1 : rel < 3.5 ? 2 : rel < 7.5 ? 5 : 10;
    var m = nice * pow10;
    var px = m * view.pxPerMeter;

    var x = 16, y = rect.height - 22;
    ctx.fillStyle = 'rgba(32,35,38,0.85)';
    ctx.fillRect(x - 6, y - 18, px + 60, 24);
    ctx.strokeStyle = '#DDE0E3';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x, y); ctx.lineTo(x + px, y);
    ctx.moveTo(x, y - 5); ctx.lineTo(x, y + 2);
    ctx.moveTo(x + px, y - 5); ctx.lineTo(x + px, y + 2);
    ctx.stroke();
    ctx.fillStyle = '#DDE0E3';
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'left'; ctx.textBaseline = 'alphabetic';
    ctx.fillText(m + ' m', x + px + 6, y + 3);
  }

  function render() {
    if (!ctx) return;
    var rect = canvas.getBoundingClientRect();

    // Fondo
    ctx.fillStyle = '#16181A';
    ctx.fillRect(0, 0, rect.width, rect.height);

    if (view.follow) {
      view.cameraE = snap.pivotEasting;
      view.cameraN = snap.pivotNorthing;
    }

    drawGrid();
    drawShape();
    drawBoundaries();
    drawActiveTrack();
    drawCoverage();
    drawSectionBar();
    drawTractor();
    drawCompass();
    drawScaleBar();
  }

  // ---- HUD ----
  function vehicleLabel() {
    var t = (snap.vehicleType || 'Tractor').toLowerCase();
    var word = t === 'harvester' ? 'Cosechadora'
             : t === 'articulated' ? 'Articulado'
             : 'Tractor';
    var b = snap.vehicleBrand || '';
    if (b === 'AGOpenGPS' || b === 'AgOpenGPS' || b === '') return word;
    return word + ' · ' + b;
  }
  function computeXteCm() {
    var t = snap.activeTrack;
    if (!t) return null;
    var A = t.A || t.a, B = t.B || t.b;
    if (A && B) {
      var ax = A.e != null ? A.e : A.E;
      var ay = A.n != null ? A.n : A.N;
      var bx = B.e != null ? B.e : B.E;
      var by = B.n != null ? B.n : B.N;
      var dx = bx - ax, dy = by - ay;
      var L2 = dx*dx + dy*dy;
      if (L2 < 1e-6) return null;
      // distancia con signo del pivote a la recta AB
      var pe = snap.pivotEasting, pn = snap.pivotNorthing;
      var cross = (pe - ax) * dy - (pn - ay) * dx;
      return (cross / Math.sqrt(L2)) * 100; // cm
    }
    var pts = t.curvePts || t.CurvePts;
    if (pts && pts.length >= 2) {
      // distancia al segmento más cercano
      var best = Infinity, sign = 1;
      var pe = snap.pivotEasting, pn = snap.pivotNorthing;
      for (var i = 1; i < pts.length; i++) {
        var p1 = pickPt(pts[i-1]), p2 = pickPt(pts[i]);
        var dx2 = p2.e - p1.e, dy2 = p2.n - p1.n;
        var L22 = dx2*dx2 + dy2*dy2;
        if (L22 < 1e-6) continue;
        var ux = (pe - p1.e), uy = (pn - p1.n);
        var tt = (ux*dx2 + uy*dy2) / L22;
        tt = Math.max(0, Math.min(1, tt));
        var qx = p1.e + tt*dx2, qy = p1.n + tt*dy2;
        var dd = Math.hypot(pe - qx, pn - qy);
        if (dd < best) {
          best = dd;
          sign = ((pe - p1.e) * dy2 - (pn - p1.n) * dx2) >= 0 ? 1 : -1;
        }
      }
      if (best === Infinity) return null;
      return sign * best * 100;
    }
    return null;
  }
  function updateHud() {
    if (hudSpeed) hudSpeed.textContent = (snap.avgSpeed || 0).toFixed(1);
    if (hudHead)  hudHead.textContent  = ((snap.heading || 0) * 180 / Math.PI).toFixed(0);
    var xteEl = document.getElementById('hudXte');
    if (xteEl) {
      var xte = computeXteCm();
      xteEl.textContent = xte == null ? '—' : ((xte >= 0 ? '+' : '') + xte.toFixed(0) + ' cm');
    }
    if (hudLatLon) {
      hudLatLon.textContent = (snap.latitude||0).toFixed(6) + ', ' + (snap.longitude||0).toFixed(6);
    }
    if (hudDose) {
      if (snap.shapeIsInside) hudDose.textContent = (snap.shapeCurrentDose||0).toFixed(0) + ' kg/ha';
      else hudDose.textContent = '—';
    }
    var secEl = document.getElementById('hudSec');
    if (secEl) {
      var onCount = 0;
      var arr = snap.sectionOnRequest || [];
      for (var i2 = 0; i2 < arr.length; i2++) if (arr[i2]) onCount++;
      secEl.textContent = onCount + '/' + (snap.numSections || 0);
    }
    var vehEl = document.getElementById('hudVehicle');
    if (vehEl) vehEl.textContent = vehicleLabel();
    var implEl = document.getElementById('hudImpl');
    if (implEl) {
      var impl = detectImplement(snap.toolWidth || 0, snap.numSections || 0);
      implEl.textContent = impl.label;
    }
    if (statusEl) {
      statusEl.textContent = snap.isJobStarted ? 'Trabajo activo' : 'Sin trabajo';
      statusEl.className = 'pill ' + (snap.isJobStarted ? 'ok' : 'warn');
    }
  }

  // ---- polling ----
  async function poll() {
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      var s = await res.json();
      // El controller usa PascalCase serializado por System.Text.Json default (camelCase).
      // Soportamos ambos.
      function pick(o, a, b) { return o[a] != null ? o[a] : o[b]; }
      snap.isJobStarted     = !!pick(s, 'isJobStarted', 'IsJobStarted');
      snap.avgSpeed         = pick(s, 'avgSpeed', 'AvgSpeed') || 0;
      snap.heading          = pick(s, 'heading', 'Heading') || 0;
      snap.pivotEasting     = pick(s, 'pivotEasting', 'PivotEasting') || 0;
      snap.pivotNorthing    = pick(s, 'pivotNorthing', 'PivotNorthing') || 0;
      snap.latitude         = pick(s, 'latitude', 'Latitude') || 0;
      snap.longitude        = pick(s, 'longitude', 'Longitude') || 0;
      snap.numSections      = pick(s, 'numSections', 'NumSections') || 0;
      snap.sectionOnRequest = pick(s, 'sectionOnRequest', 'SectionOnRequest') || [];
      var sp = pick(s, 'sectionPositions', 'SectionPositions') || [];
      snap.sectionPositions = sp.map(function (e) {
        return {
          index: e.index != null ? e.index : e.Index,
          left:  e.left  != null ? e.left  : e.Left,
          right: e.right != null ? e.right : e.Right
        };
      });
      snap.toolWidth        = pick(s, 'toolWidth', 'ToolWidth') || 0;
      snap.shapeCurrentDose = pick(s, 'shapeCurrentDose', 'ShapeCurrentDose') || 0;
      snap.shapeIsInside    = !!pick(s, 'shapeIsInside', 'ShapeIsInside');
      snap.vehicleType      = pick(s, 'vehicleType', 'VehicleType') || 'Tractor';
      snap.vehicleBrand     = pick(s, 'vehicleBrand', 'VehicleBrand') || 'AGOpenGPS';
      snap.boundaries       = pick(s, 'boundaries', 'Boundaries') || [];
      snap.headlands        = pick(s, 'headlands', 'Headlands') || [];
      snap.activeTrack      = pick(s, 'activeTrack', 'ActiveTrack') || null;

      // Cobertura persistente (solo si hay job y movimiento real). Cada muestra
      // incluye heading + sectionOnRequest. Pintamos sobre el canvas oculto
      // (coverage.canvas) el segmento entre la muestra anterior y la actual —
      // así sobrevive a la jornada sin tope, igual que en AoG.
      if (snap.isJobStarted) {
        var curSample = {
          e: snap.pivotEasting,
          n: snap.pivotNorthing,
          h: snap.heading,
          on: (snap.sectionOnRequest || []).slice()
        };
        if (!lastCovSample) {
          lastCovSample = curSample;
        } else {
          var dE = lastCovSample.e - curSample.e;
          var dN = lastCovSample.n - curSample.n;
          if ((dE * dE + dN * dN) > 0.0625) { // ≥ 0.25 m de avance
            paintCoverageSegment(lastCovSample, curSample);
            lastCovSample = curSample;
          }
        }
      }
      updateHud();
    } catch (e) {
      if (statusEl) { statusEl.textContent = 'Sin conexión'; statusEl.className = 'pill err'; }
    }
  }

  // ---- shape polling (1 Hz; el shapefile cambia solo al cargarlo). ----
  async function pollShape() {
    try {
      var res = await fetch('/api/aog/shape', { cache: 'no-store' });
      if (!res.ok) { shape.polygons = []; return; }
      var s = await res.json();
      if (!s) { shape.polygons = []; return; }
      function pick(o, a, b) { return o[a] != null ? o[a] : o[b]; }
      shape.sourceToken = pick(s, 'sourceToken', 'SourceToken') || '';
      shape.count       = pick(s, 'count', 'Count') || 0;
      shape.styleField  = pick(s, 'styleField', 'StyleField') || null;

      var polys = pick(s, 'polygons', 'Polygons') || [];
      var out = [];
      for (var i = 0; i < polys.length; i++) {
        var p = polys[i];
        out.push({
          r:     pick(p, 'r', 'R') || 0,
          g:     pick(p, 'g', 'G') || 0,
          b:     pick(p, 'b', 'B') || 0,
          a:     pick(p, 'a', 'A') != null ? pick(p, 'a', 'A') : 80,
          rings: pick(p, 'rings', 'Rings') || []
        });
      }
      shape.polygons = out;
    } catch (e) {
      // silencioso — el shape se mantiene con el último set conocido.
    }
  }

  // ---- monitor de siembra (VistaX live) ----
  var seedMonRoot = document.getElementById('seedmon');
  var smImpl     = document.getElementById('smImpl');
  var smSpm      = document.getElementById('smSpm');
  var smActivos  = document.getElementById('smActivos');
  var smFallas   = document.getElementById('smFallas');
  var smTrenes   = document.getElementById('smTrenes');

  // Color por estado del surco — alineado con vistax.js / paleta theme.css
  //   ok      → verde sólido
  //   bajo    → degradé negro→verde según RatioObjetivo (0..1)
  //   tapado  → negro pleno
  //   exceso  → azul
  //   muted   → gris desaturado
  //   no-data → gris idle
  function smColorForSurco(s) {
    function pk(o, a, b) { return o[a] != null ? o[a] : o[b]; }
    var st = (pk(s, 'estado', 'Estado') || 'no-data').toLowerCase();
    if (st === 'ok')      return 'var(--vx-ok)';
    if (st === 'tapado')  return 'var(--vx-tapado)';
    if (st === 'exceso')  return 'var(--vx-exceso)';
    if (st === 'muted')   return 'var(--vx-muted)';
    if (st === 'no-data') return 'var(--vx-no-data)';
    // bajo → interpolar rgb(5,5,5) → rgb(75,166,63) según ratio
    var ratio = pk(s, 'ratioObjetivo', 'RatioObjetivo');
    if (ratio == null) ratio = 0;
    ratio = Math.max(0, Math.min(1, ratio));
    var r = Math.round(5  + (75  - 5)  * ratio);
    var g = Math.round(5  + (166 - 5)  * ratio);
    var b = Math.round(5  + (63  - 5)  * ratio);
    return 'rgb(' + r + ',' + g + ',' + b + ')';
  }

  function renderSeedMonitor(live) {
    if (!seedMonRoot) return;
    // pick PascalCase/camelCase
    function pk(o, a, b) { return o[a] != null ? o[a] : o[b]; }
    var trenes = pk(live, 'trenes', 'Trenes') || [];
    var activo = !!pk(live, 'monitoreoActivo', 'MonitoreoActivo');
    var hasTrenes = trenes.length > 0;
    // ocultar el panel si no hay implemento VistaX configurado
    seedMonRoot.hidden = !hasTrenes;
    if (!hasTrenes) return;

    if (smImpl)    smImpl.textContent    = pk(live, 'nombreImplemento', 'NombreImplemento') || (activo ? 'activo' : 'inactivo');
    if (smSpm)     smSpm.textContent     = (pk(live, 'spmPromedio', 'SpmPromedio') || 0).toFixed(0);
    if (smActivos) smActivos.textContent = pk(live, 'surcosActivos', 'SurcosActivos') || 0;
    if (smFallas)  smFallas.textContent  = pk(live, 'fallasActivas', 'FallasActivas') || 0;

    // Reconstruir trenes
    var html = '';
    for (var i = 0; i < trenes.length; i++) {
      var tr = trenes[i];
      var name = pk(tr, 'nombre', 'Nombre') || ('Tren ' + (pk(tr, 'tren', 'Tren') || (i + 1)));
      var surcos = pk(tr, 'surcos', 'Surcos') || [];
      var cells = '';
      for (var j = 0; j < surcos.length; j++) {
        var s = surcos[j];
        var st = (pk(s, 'estado', 'Estado') || 'no-data').toLowerCase();
        var cut = !!pk(s, 'seccionCortada', 'SeccionCortada');
        var muted = !!pk(s, 'muted', 'Muted');
        var uid = pk(s, 'uid', 'Uid') || '';
        var cable = pk(s, 'cable', 'Cable');
        if (cable == null) cable = 0;
        var spm = (pk(s, 'spm', 'Spm') || 0).toFixed(0);
        var obj = pk(s, 'objetivo', 'Objetivo');
        var baj = pk(s, 'bajada', 'Bajada') || (j + 1);
        var bg = smColorForSurco(s);
        var cls = 'cell';
        if (muted || st === 'muted') cls += ' muted';
        if (cut) cls += ' cut';
        var title = 'bajada ' + baj + ' · ' + spm + ' spm';
        if (obj != null) title += ' / obj ' + Number(obj).toFixed(0);
        title += ' · ' + st + (muted ? ' (silenciado)' : '');
        cells += '<div class="' + cls + '"'
              + ' style="background:' + bg + ';"'
              + ' data-uid="' + uid + '"'
              + ' data-cable="' + cable + '"'
              + ' data-muted="' + (muted ? '1' : '0') + '"'
              + ' data-bajada="' + baj + '"'
              + ' title="' + title + '"></div>';
      }
      html += '<div class="tren"><div class="lbl">' + name + '</div><div class="strip">' + cells + '</div></div>';
    }
    smTrenes.innerHTML = html;
    bindSeedCellClicks();
  }

  async function smToggleMute(uid, cable, muted) {
    if (!uid) return;
    try {
      await fetch('/api/vistax/sensor/mute', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ uid: uid, cable: cable, muted: !!muted })
      });
      // refresca enseguida
      pollSeedMonitor();
    } catch (e) { /* silencioso */ }
  }

  function bindSeedCellClicks() {
    if (!smTrenes) return;
    var cells = smTrenes.querySelectorAll('.cell');
    for (var i = 0; i < cells.length; i++) {
      cells[i].addEventListener('click', function (ev) {
        var el = ev.currentTarget;
        var uid = el.getAttribute('data-uid');
        var cable = parseInt(el.getAttribute('data-cable'), 10) || 0;
        var wasMuted = el.getAttribute('data-muted') === '1';
        if (!uid) return; // no se puede silenciar sin uid
        smToggleMute(uid, cable, !wasMuted);
      });
    }
  }

  async function pollSeedMonitor() {
    if (!seedMonRoot) return;
    try {
      var res = await fetch('/api/vistax/live', { cache: 'no-store' });
      if (!res.ok) { seedMonRoot.hidden = true; return; }
      var data = await res.json();
      if (!data || data.error) { seedMonRoot.hidden = true; return; }
      renderSeedMonitor(data);
    } catch (e) {
      seedMonRoot.hidden = true;
    }
  }

  // ---- toggle shape overlay ----
  var btnShape = document.getElementById('btnShape');
  function setShape(on) {
    shapeVisible = !!on;
    if (btnShape) btnShape.classList.toggle('primary', !!on);
  }
  if (btnShape) btnShape.addEventListener('click', function () { setShape(!shapeVisible); });

  // ---- loop ----
  function tick() { render(); requestAnimationFrame(tick); }

  resizeCanvas();
  setFollow(true);
  setShape(true);
  poll();
  pollShape();
  pollSeedMonitor();

  // Polling adaptativo:
  //   · pose/HUD a 10 Hz siempre (es el HUD del piloto, no se baja).
  //   · shape + seed normalmente a 1 Hz / 2 Hz. Si no hay job activo (tractor
  //     parado/sin trabajo) bajan a 1 Hz unificados — el operario solo está
  //     mirando el mapa, no necesita refresco fino.
  //   · si la pestaña no es visible, pausa total.
  var poseT = null, shapeT = null, seedT = null;
  function isActive() { return !!snap.isJobStarted; }
  function loopPose() {
    if (document.hidden) { poseT = null; return; }
    poll();
    poseT = setTimeout(loopPose, 100);
  }
  function loopShape() {
    if (document.hidden) { shapeT = null; return; }
    pollShape();
    shapeT = setTimeout(loopShape, isActive() ? 1000 : 2000);
  }
  function loopSeed() {
    if (document.hidden) { seedT = null; return; }
    pollSeedMonitor();
    seedT = setTimeout(loopSeed, isActive() ? 500 : 1000);
  }
  poseT  = setTimeout(loopPose, 100);
  shapeT = setTimeout(loopShape, 1000);
  seedT  = setTimeout(loopSeed, 500);
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) {
      if (poseT)  { clearTimeout(poseT);  poseT  = null; }
      if (shapeT) { clearTimeout(shapeT); shapeT = null; }
      if (seedT)  { clearTimeout(seedT);  seedT  = null; }
    } else {
      if (!poseT)  loopPose();
      if (!shapeT) loopShape();
      if (!seedT)  loopSeed();
    }
  });

  requestAnimationFrame(tick);
})();
