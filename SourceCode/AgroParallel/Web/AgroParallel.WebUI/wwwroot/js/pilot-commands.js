// ============================================================================
// pilot-commands.js — catálogo de comandos del piloto disponibles para las
// botoneras web (botonera.html, guia-rapida.html). El `cmd` es CONTRATO con
// FormGPS.ExecuteGuidanceCommand (GUI.FloatingMenu.cs): agregar un comando
// requiere tocar los dos lados. `grupo` es solo presentación.
// ============================================================================
window.PILOT_COMMANDS = [
  // --- guías ---
  { cmd: 'build',         label: 'Crear guías',    ico: '📐', grupo: 'Guías' },
  { cmd: 'pick',          label: 'Elegir guía',    ico: '☰',  grupo: 'Guías' },
  { cmd: 'center',        label: 'Centrar guía',   ico: '⌖',  grupo: 'Guías' },
  { cmd: 'nudge_left',    label: 'Mover ‹',        ico: '◀',  grupo: 'Guías' },
  { cmd: 'nudge_right',   label: 'Mover ›',        ico: '▶',  grupo: 'Guías' },
  { cmd: 'contour',       label: 'Curva',          ico: '〰',  grupo: 'Guías' },
  { cmd: 'contour_lock',  label: 'Bloq. contorno', ico: '🔒', grupo: 'Guías' },
  { cmd: 'ab_draw',       label: 'Dibujar AB',     ico: '✏',  grupo: 'Guías' },
  { cmd: 'a_plus',        label: 'A+',             ico: '➕', grupo: 'Guías' },
  { cmd: 'track_prev',    label: 'Guía ant.',      ico: '⏮',  grupo: 'Guías' },
  { cmd: 'track_next',    label: 'Guía sig.',      ico: '⏭',  grupo: 'Guías' },
  { cmd: 'tracks_off',    label: 'Ocultar guías',  ico: '🚫', grupo: 'Guías' },
  { cmd: 'nudge',         label: 'Nudge',          ico: '↔',  grupo: 'Guías' },
  { cmd: 'nudge_ref',     label: 'Nudge ref.',     ico: '⇱',  grupo: 'Guías' },
  { cmd: 'auto_snap_to_pivot', label: 'Auto-centrar al pivote', ico: '🎯', grupo: 'Guías' },
  // --- operación ---
  { cmd: 'autosteer',     label: 'AutoSteer',      ico: '🎯', grupo: 'Operación' },
  { cmd: 'autotrack',     label: 'AutoTrack',      ico: '🧲', grupo: 'Operación' },
  { cmd: 'uturn',         label: 'U-Turn',         ico: '↩',  grupo: 'Operación' },
  { cmd: 'uturn_skips',   label: 'Saltos U-Turn',  ico: '⤵',  grupo: 'Operación' },
  { cmd: 'sec_auto',      label: 'Secc. Auto',     ico: '🟩', grupo: 'Operación' },
  { cmd: 'sec_manual',    label: 'Secc. Manual',   ico: '🟨', grupo: 'Operación' },
  { cmd: 'hidraulico',    label: 'Hidráulico',     ico: '⬍',  grupo: 'Operación' },
  // --- lote ---
  { cmd: 'lote_continuar', label: 'Continuar lote', ico: '▶',  grupo: 'Lote' },
  { cmd: 'lote_menu',     label: 'Abrir / nuevo',  ico: '🗂',  grupo: 'Lote' },
  { cmd: 'lote_cerrar',   label: 'Cerrar lote',    ico: '✖',  grupo: 'Lote' },
  { cmd: 'lote_datos',    label: 'Datos lote',     ico: '📊', grupo: 'Lote' },
  { cmd: 'bandera',       label: 'Bandera',        ico: '🚩', grupo: 'Lote' },
  { cmd: 'mapeo_color',   label: 'Color mapeo',    ico: '🎨', grupo: 'Lote' },
  // --- lindero y cabecera ---
  { cmd: 'lindero',        label: 'Lindero',        ico: '⬠',  grupo: 'Lindero' },
  { cmd: 'cabecera',       label: 'Cabecera',       ico: '⛶',  grupo: 'Lindero' },
  { cmd: 'cabecera_onoff', label: 'Cabecera SÍ/NO', ico: '🔛', grupo: 'Lindero' },
  // --- vista ---
  { cmd: 'v2d',           label: '2D',             ico: '▱',  grupo: 'Vista' },
  { cmd: 'v3d',           label: '3D',             ico: '⛰',  grupo: 'Vista' },
  { cmd: 'norte2d',       label: 'Norte 2D',       ico: '🧭', grupo: 'Vista' },
  { cmd: 'grilla',        label: 'Grilla',         ico: '▦',  grupo: 'Vista' },
  { cmd: 'dia_noche',     label: 'Día/Noche',      ico: '🌗', grupo: 'Vista' },
  { cmd: 'brillo_up',     label: 'Brillo +',       ico: '🔆', grupo: 'Vista' },
  { cmd: 'brillo_dn',     label: 'Brillo −',       ico: '🔅', grupo: 'Vista' }
];
