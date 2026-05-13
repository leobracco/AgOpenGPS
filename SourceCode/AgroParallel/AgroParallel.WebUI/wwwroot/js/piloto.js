// ============================================================================
// piloto.js — mapa Piloto (Canvas 2D).
//   · Recibe AogStateSnapshot por polling de /api/aog/state (10 Hz).
//   · Render: grid en metros, sprite del tractor según marca configurada
//     (TractorJohnDeere.png, HarvesterClaas.png, ...), barra de herramienta
//     atrás del tractor con N secciones coloreadas por sectionOnRequest[i].
//   · Cámara: sigue al tractor (auto-follow). Pan con drag, zoom con wheel.
//   · Sin tiles OSM — AOG opera en metros locales (Easting/Northing), no lat/lon-tile.
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
    toolWidth: 0,
    shapeCurrentDose: 0, shapeIsInside: false,
    vehicleType: 'Tractor', vehicleBrand: 'AGOpenGPS'
  };

  // Path histórico del tractor (en mundo) — últimos N puntos para dejar rastro.
  var trail = [];
  var TRAIL_MAX = 400;

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
    if (!widthM || widthM <= 0) return { kind: 'sin', label: 'sin herramienta', color: '#C5CFC5' };
    if (widthM >= 12)  return { kind: 'pulverizadora', label: 'pulverizadora · ' + widthM.toFixed(1) + ' m', color: '#3D87C6' };
    if (widthM >= 4)   return { kind: 'siembra',       label: 'siembra · ' + widthM.toFixed(1) + ' m',        color: '#4ABA3E' };
    return { kind: 'implemento', label: 'implemento · ' + widthM.toFixed(1) + ' m', color: '#535E54' };
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

  // ---- dibujo ----
  function drawGrid() {
    var rect = canvas.getBoundingClientRect();
    var step = view.pxPerMeter >= 20 ? 1 : (view.pxPerMeter >= 4 ? 10 : 50); // 1m / 10m / 50m
    var stepPx = step * view.pxPerMeter;
    var originScreen = worldToScreen(0, 0);

    // Líneas finas
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(0,0,0,0.06)';
    var ox = originScreen.x % stepPx;
    var oy = originScreen.y % stepPx;
    ctx.beginPath();
    for (var x = ox; x < rect.width; x += stepPx) { ctx.moveTo(x, 0); ctx.lineTo(x, rect.height); }
    for (var y = oy; y < rect.height; y += stepPx) { ctx.moveTo(0, y); ctx.lineTo(rect.width, y); }
    ctx.stroke();

    // Cada 10 pasos un trazo medio
    ctx.strokeStyle = 'rgba(0,0,0,0.12)';
    ctx.beginPath();
    var bigStep = stepPx * 10;
    ox = originScreen.x % bigStep;
    oy = originScreen.y % bigStep;
    for (var x2 = ox; x2 < rect.width; x2 += bigStep) { ctx.moveTo(x2, 0); ctx.lineTo(x2, rect.height); }
    for (var y2 = oy; y2 < rect.height; y2 += bigStep) { ctx.moveTo(0, y2); ctx.lineTo(rect.width, y2); }
    ctx.stroke();
  }

  function drawTrail() {
    if (trail.length < 2) return;
    ctx.lineWidth = 2;
    ctx.strokeStyle = 'rgba(74,186,62,0.55)';
    ctx.beginPath();
    var p0 = worldToScreen(trail[0].e, trail[0].n);
    ctx.moveTo(p0.x, p0.y);
    for (var i = 1; i < trail.length; i++) {
      var p = worldToScreen(trail[i].e, trail[i].n);
      ctx.lineTo(p.x, p.y);
    }
    ctx.stroke();
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
    ctx.fillStyle = 'rgba(0,0,0,0.10)';
    ctx.fillRect(-wpx/2, backOffset, wpx, hpx);

    // Cada sección
    var segW = wpx / n;
    for (var i = 0; i < n; i++) {
      var on = !!(snap.sectionOnRequest && snap.sectionOnRequest[i]);
      ctx.fillStyle = on ? impl.color : '#C5CFC5';
      ctx.fillRect(-wpx/2 + i*segW + 1, backOffset + 1, segW - 2, hpx - 2);
    }
    // Borde
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(0,0,0,0.35)';
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
        ctx.fillStyle = '#535E54';
        ctx.fillRect(-wpx/2, -hpx/2, wpx, hpx);
      }
    }
    ctx.restore();

    // Punto pivote
    ctx.fillStyle = '#101612';
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
    ctx.fillStyle = 'rgba(255,255,255,0.85)';
    ctx.strokeStyle = '#C5CFC5';
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI*2); ctx.fill(); ctx.stroke();
    // N
    ctx.fillStyle = '#101612';
    ctx.font = 'bold 12px system-ui, sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText('N', cx, cy - r + 9);
    // flecha del rumbo
    ctx.translate(cx, cy);
    ctx.rotate(snap.heading);
    ctx.fillStyle = '#4ABA3E';
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
    ctx.fillStyle = 'rgba(255,255,255,0.8)';
    ctx.fillRect(x - 6, y - 18, px + 60, 24);
    ctx.strokeStyle = '#101612';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x, y); ctx.lineTo(x + px, y);
    ctx.moveTo(x, y - 5); ctx.lineTo(x, y + 2);
    ctx.moveTo(x + px, y - 5); ctx.lineTo(x + px, y + 2);
    ctx.stroke();
    ctx.fillStyle = '#101612';
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'left'; ctx.textBaseline = 'alphabetic';
    ctx.fillText(m + ' m', x + px + 6, y + 3);
  }

  function render() {
    if (!ctx) return;
    var rect = canvas.getBoundingClientRect();

    // Fondo
    ctx.fillStyle = '#F5F7F4';
    ctx.fillRect(0, 0, rect.width, rect.height);

    if (view.follow) {
      view.cameraE = snap.pivotEasting;
      view.cameraN = snap.pivotNorthing;
    }

    drawGrid();
    drawTrail();
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
  function updateHud() {
    if (hudSpeed) hudSpeed.textContent = (snap.avgSpeed || 0).toFixed(1);
    if (hudHead)  hudHead.textContent  = ((snap.heading || 0) * 180 / Math.PI).toFixed(0);
    if (hudLatLon) {
      hudLatLon.textContent = (snap.latitude||0).toFixed(6) + ', ' + (snap.longitude||0).toFixed(6);
    }
    if (hudDose) {
      if (snap.shapeIsInside) hudDose.textContent = (snap.shapeCurrentDose||0).toFixed(0) + ' kg/ha';
      else hudDose.textContent = '—';
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
      snap.toolWidth        = pick(s, 'toolWidth', 'ToolWidth') || 0;
      snap.shapeCurrentDose = pick(s, 'shapeCurrentDose', 'ShapeCurrentDose') || 0;
      snap.shapeIsInside    = !!pick(s, 'shapeIsInside', 'ShapeIsInside');
      snap.vehicleType      = pick(s, 'vehicleType', 'VehicleType') || 'Tractor';
      snap.vehicleBrand     = pick(s, 'vehicleBrand', 'VehicleBrand') || 'AGOpenGPS';

      // Trail (solo si hay job y movimiento real)
      if (snap.isJobStarted) {
        var last = trail[trail.length - 1];
        if (!last || Math.hypot(last.e - snap.pivotEasting, last.n - snap.pivotNorthing) > 0.25) {
          trail.push({ e: snap.pivotEasting, n: snap.pivotNorthing });
          if (trail.length > TRAIL_MAX) trail.shift();
        }
      }
      updateHud();
    } catch (e) {
      if (statusEl) { statusEl.textContent = 'Sin conexión'; statusEl.className = 'pill err'; }
    }
  }

  // ---- loop ----
  function tick() { render(); requestAnimationFrame(tick); }

  resizeCanvas();
  setFollow(true);
  poll();
  setInterval(poll, 100); // 10 Hz
  requestAnimationFrame(tick);
})();
