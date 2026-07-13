// ============================================================================
// barra-abajo.js — espejo HTML de la barra inferior nativa (panelBottom):
// elegir guía, centrar/mover guía, bandera, cabecera, secciones por cabecera,
// hidráulico, tramlines, reset herramienta, color de mapeo, skips de U-turn.
// Comandos = contrato con FormGPS.ExecuteGuidanceCommand (mismo canal que
// barra-derecha.js). Estado en vivo de GET /api/aog/state: replica las
// mismas imágenes y visibilidades que pinta GUI.Designer.cs en el nativo.
// ============================================================================
(function () {
  'use strict';

  var IMG = '../img/barra-abajo/';

  async function send(cmd, el) {
    try {
      var res = await fetch('/api/aog/guidance/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cmd: cmd })
      });
      var data = await res.json();
      if (el) {
        el.classList.add('flash');
        setTimeout(function () { el.classList.remove('flash'); }, 250);
      }
      if (!data.ok) console.warn('[barra-abajo] comando rechazado:', cmd, data.error);
      poll(); // refrescar estado enseguida, sin esperar el próximo tick
    } catch (e) {
      console.warn('[barra-abajo] sin conexión:', e.message);
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

  // cboxpRowWidth: el select manda skips_{n}; mientras el usuario lo tiene
  // abierto no lo pisamos con el valor del poll.
  var selSkips = $('selSkips');
  var skipsTouching = false;
  selSkips.addEventListener('focus', function () { skipsTouching = true; });
  selSkips.addEventListener('blur', function () { skipsTouching = false; });
  selSkips.addEventListener('change', function () {
    send('skips_' + selSkips.value, selSkips);
    selSkips.blur();
  });

  async function poll() {
    var snap;
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      snap = await res.json();
    } catch (e) {
      return; // sin conexión: deja el último estado conocido
    }

    // panelBottom nativo: invisible sin lote abierto
    show($('noLote'), !snap.is_job_started);

    var hayGuia = Number(snap.track_idx) > -1;
    var nudge = !!snap.is_nudge_on;

    // centrar/mover guía: guía activa + modo nudge encendido
    show($('btnCenter'), hayGuia && nudge);
    show($('btnNudgeR'), hayGuia && nudge);
    show($('btnNudgeL'), hayGuia && nudge);

    // btnFlag: color de la próxima bandera (0=roja, 1=verde, 2=amarilla)
    var flags = ['FlagRed.png', 'FlagGrn.png', 'FlagYel.png'];
    img($('imgFlag'), flags[Number(snap.flag_color)] || flags[0]);

    // cabecera: visible solo con hdLine creado
    var hayHdl = !!snap.has_headland;
    show($('btnHeadland'), hayHdl);
    img($('imgHeadland'), snap.is_headland_on ? 'HeadlandOn.png' : 'HeadlandOff.png');
    show($('btnHdlSec'), hayHdl);
    img($('imgHdlSec'), snap.is_section_controlled_by_headland
      ? 'HeadlandSectionOn.png' : 'HeadlandSectionOff.png');

    // hidráulico: módulo habilitado + cabecera creada; solo opera con cabecera activa
    show($('btnHyd'), !!snap.has_hyd_lift && hayHdl);
    $('btnHyd').classList.toggle('disabled', !snap.is_headland_on);
    img($('imgHyd'), snap.is_hyd_lift_on ? 'HydraulicLiftOn.png' : 'HydraulicLiftOff.png');

    // tramlines: visible solo con tram creado; imagen según modo de vista
    show($('btnTram'), !!snap.has_tram);
    var trams = ['TramOff.png', 'TramAll.png', 'TramLines.png', 'TramOuter.png'];
    img($('imgTram'), trams[Number(snap.tram_display_mode)] || trams[0]);

    // salteo de U-turn: visible con guía activa
    show($('btnYouSkip'), hayGuia);
    var skips = ['YouSkipOff.png', 'YouSkipOn.png', 'YouSkipWorkedTracks.png'];
    img($('imgYouSkip'), skips[Number(snap.you_skip_mode)] || skips[0]);
    show(selSkips, hayGuia);
    var w = String(Number(snap.row_skips_width) || 1);
    if (!skipsTouching && selSkips.value !== w) selSkips.value = w;
  }

  poll();
  setInterval(poll, 500);
})();
