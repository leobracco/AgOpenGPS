// ============================================================================
// barra-derecha.js — espejo HTML de la barra lateral derecha nativa
// (panelRight): Piloto/U-turn/Secciones/ISOBUS/AutoTrack/guías/Contorno.
// Comandos = contrato con FormGPS.ExecuteGuidanceCommand (mismo canal que
// barra-superior.js). Estado en vivo de GET /api/aog/state: replica las
// mismas imágenes y visibilidades que pinta GUI.Designer.cs en el nativo.
// ============================================================================
(function () {
  'use strict';

  var IMG = '../img/barra-derecha/';

  async function send(cmd, btn) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (btn) {
        btn.classList.add('flash');
        setTimeout(function () { btn.classList.remove('flash'); }, 250);
      }
      if (!data.ok) console.warn('[barra-derecha] comando rechazado:', cmd, data.error);
      poll(); // refrescar estado enseguida, sin esperar el próximo tick
    } catch (e) {
      console.warn('[barra-derecha] sin conexión:', e.message);
    }
  }

  document.querySelectorAll('.rbtn[data-cmd]').forEach(function (b) {
    b.addEventListener('click', function () { send(b.dataset.cmd, b); });
  });

  function $(id) { return document.getElementById(id); }
  function show(el, vis) { el.classList.toggle('hidden', !vis); }
  function img(el, file) {
    var src = IMG + file;
    if (!el.src.endsWith(src)) el.src = src; // evita reflashes por repintado
  }

  async function poll() {
    var snap;
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      snap = await res.json();
    } catch (e) {
      return; // sin conexión: deja el último estado conocido
    }

    // panelRight nativo: invisible sin lote abierto
    show($('noLote'), !snap.is_job_started);

    var idx = Number(snap.track_idx);
    var total = Number(snap.tracks_total || 0);
    var visibles = Number(snap.tracks_visible || 0);
    var contour = !!snap.is_contour_on;
    var hayGuia = idx > -1;

    // btnAutoSteer: imagen según on/off + snap-to-pivot; enabled si hay guía o contorno
    var steerImg = (snap.is_auto_steer_on ? 'AutoSteerOn' : 'AutoSteerOff')
      + (snap.is_auto_snap_to_pivot ? 'SnapToPivot' : '') + '.png';
    img($('imgPiloto'), steerImg);
    $('btnPiloto').classList.toggle('disabled', !(hayGuia || contour));

    // btnAutoYouTurn: visible con guía + sin contorno + lindero cargado
    show($('btnUturn'), hayGuia && !contour && !!snap.has_boundary);
    img($('imgUturn'), snap.is_you_turn_on ? 'YouTurn80.png' : 'YouTurnNo.png');

    // Secciones auto / manual (btnStates del nativo)
    img($('imgSecAuto'), snap.is_section_auto_on ? 'SectionMasterOn.png' : 'SectionMasterOff.png');
    img($('imgSecManual'), snap.is_section_manual_on ? 'ManualOn.png' : 'ManualOff.png');

    // ISOBUS: visible solo con comunicación viva
    show($('btnIsobus'), !!snap.isobus_alive);
    img($('imgIsobus'), snap.isobus_on ? 'IsobusSectionControlOn.png' : 'IsobusSectionControlOff.png');

    // AutoTrack + ciclado de guías: 2+ guías visibles, guía activa, sin contorno
    var cicla = visibles > 1 && hayGuia && !contour;
    show($('btnAutoTrack'), cicla);
    img($('imgAutoTrack'), snap.is_auto_track_on ? 'AutoTrack.png' : 'AutoTrackOff.png');
    show($('btnTrackPrev'), cicla);
    show($('btnTrackNext'), cicla);

    // Contorno + candado
    img($('imgContour'), contour ? 'ContourOn.png' : 'ContourOff.png');
    show($('btnContourLock'), contour);
    img($('imgContourLock'), snap.is_contour_locked ? 'ColorLocked.png' : 'ColorUnlocked.png');

    // lblNumCu: "n/total" con guía activa y sin contorno
    var numCu = $('numCu');
    var verNum = hayGuia && total > 0 && !contour;
    show(numCu, verNum);
    numCu.textContent = verNum ? (idx + 1) + '/' + total : '';
  }

  // Auto-ocultado: cualquier interacción sobre la barra (hover/touch/click)
  // reinicia el contador de 15 s en PilotX vía "paneles_keepalive".
  // Throttle 2 s para no inundar el canal de comandos con el pointermove.
  var kaLast = 0;
  function keepalive() {
    var now = Date.now();
    if (now - kaLast < 2000) return;
    kaLast = now;
    fetch('/api/aog/guidance/command', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cmd: 'paneles_keepalive' })
    }).catch(function () {});
  }
  ['pointermove', 'pointerdown', 'touchstart'].forEach(function (ev) {
    document.addEventListener(ev, keepalive, { passive: true });
  });

  poll();
  setInterval(poll, 500);
})();
