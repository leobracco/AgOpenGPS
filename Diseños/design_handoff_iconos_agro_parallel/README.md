# Handoff: Set de íconos · Agro Parallel

## Overview
Set de 197 íconos SVG para la interfaz de una pantalla de guiado agrícola, redibujados
en el lenguaje visual Agro Parallel: lineales, trazo uniforme, gris como estado neutro, verde
solo como estado activo, rojo solo para destructivo/alarma, ámbar solo para advertencia.
Reemplazan el set original (PNG rasterizados, con perspectiva, degradados y colores dispares).

Grupos:
1. **Barra de funciones** (AutoSteer, secciones manual/automático, izquierda, centrar, derecha, giro).
2. **Menú lateral** (líneas AB, AutoTrack, contorno, marca, enderezar implemento, fijar línea,
   huellas, salto de pasada, cabecera).
3. **Interfaz general** (OK, cancelar, archivos, ajustes, zoom, flechas, reproducción, brillo,
   día/noche, cámaras, captura, orden, selección, banderines).
4. **Campo, límites y líneas** (límites y su grabación, cabecera, puntos, líneas AB/curva/pivote,
   visibilidad de líneas, GPS/RTK, lat-lon, desplazamiento de antena).
5. **Dirección, datos y sistema** (volante, modos de dirección, asistentes, nube/subida/descarga,
   levante hidráulico, mapeo, ISOBUS, interruptores, huellas, giros en U/H/retroceso, retomar
   recorrido, colores, batería, inclinación, secciones vs. límite, ventanas).

## About the Design Files
Los archivos de este paquete son **referencias de diseño hechas en HTML/SVG**: prototipos que
muestran la apariencia y los estados previstos, no código de producción para copiar tal cual.
La tarea es **recrear estos diseños en el entorno existente del codebase destino** (WinForms/WPF,
React, Qt, etc.) usando sus patrones establecidos. Los SVG de `icons/` sí pueden usarse
directamente como assets; si el destino requiere PNG, exportarlos a 48 y 96 px.

## Fidelity
**Alta fidelidad (hifi).** Colores, geometría, grosor de trazo y estados son definitivos.

## Reglas de construcción
- viewBox `0 0 48 48`, sin relleno salvo elementos de estado activo, puntos y banderines.
- `stroke-width` 2.4 al dibujar a 42–60 px; **3.0** cuando el ícono se dibuja a ≤30 px.
- `stroke-linecap="round"`, `stroke-linejoin="round"`.
- Un solo acento verde por ícono; el resto en gris. Nunca dos colores de acento juntos.
- Estado OFF: mismo dibujo, trazo `#8B948B`, sin rellenos.
- Estado deshabilitado sugerido: opacidad 0.4 sobre la versión OFF.

## Layout de la hoja de estilo (`Iconos Agro Parallel.dc.html`)
- Lienzo 1440 px, padding 56 px, fondo `#F5F7F4`; encabezado 34px/600 + subtítulo 17px `#535E54`.
- Tarjetas: `#FFFFFF`, borde 1px `#E2E7E2`, radio 18px, padding 24–28px.
- Casilla de estado: alto 108px, radio 14px, ícono 60px. ON fondo `#EFF7ED` borde `#D3E8CE`;
  OFF fondo `#F5F7F4` borde `#E2E7E2`. Etiquetas ON/OFF 12px/600, letter-spacing 0.12em.
- Grillas densas: 8 columnas, gap 20px, casilla 76px alto radio 12px, ícono 42px,
  etiqueta 9.5px/600 letter-spacing 0.05em.
- Barra de funciones horizontal: celdas iguales, borde 1px entre celdas, ícono 30px +
  etiqueta 11px/600; celda activa fondo `#EFF7ED`, texto `#4ABA3E`.
- Riel vertical: ancho 116px, ítems padding 12px/8px, ícono 30px + etiqueta 10px/600.
- Modo noche: panel `#101612`; activos `#5FD152`, inactivos `#C5CFC5`.

## Design Tokens
| Token | Hex | Uso |
|---|---|---|
| verde activo | `#4ABA3E` | estado ON, acento |
| verde noche | `#5FD152` | acento sobre fondo oscuro |
| verde fondo | `#EFF7ED` | fondo de celda activa |
| verde borde | `#D3E8CE` | borde de celda activa |
| gris texto | `#535E54` | trazo por defecto, texto secundario |
| gris inactivo | `#8B948B` | estado OFF |
| gris claro | `#C5CFC5` | elementos de apoyo, líneas de referencia |
| borde | `#E2E7E2` | bordes y separadores |
| fondo | `#F5F7F4` | fondo de página y de casilla |
| blanco | `#FFFFFF` | tarjetas |
| negro | `#101612` | títulos, modo noche |
| rojo | `#C2452F` | destructivo, alarma, banderín rojo |
| ámbar | `#D89A2B` | advertencia, banderín ámbar |

Radios 7 / 12 / 14 / 18 px. Espaciado 4, 8, 9, 10, 12, 16, 20, 24, 28, 40, 56 px.
Tipografía Helvetica Neue / Helvetica, sans-serif. Escala 34/600, 18/600, 17/400, 13/400,
12/600 (0.12em), 11/600 (0.08em), 10/600 (0.06em), 9.5/600 (0.05em).

## Estados y comportamiento
- **Toggles** (ícono, etiqueta y fondo de celda cambian juntos): AutoSteer, secciones manual/auto,
  giro, AutoTrack, contorno, huellas, salto de pasada, cabecera, mapeo, ISOBUS, levante,
  subida/auto-subida, visibilidad de líneas, interruptores. Transición `120ms ease-out`.
- **Excluyentes:** secciones manual ↔ automático; guiado AB ↔ contorno ↔ pivote;
  modo Stanley ↔ Pure Pursuit; día ↔ noche.
- **Acciones momentáneas:** izquierda, derecha, centrar, línea siguiente/anterior, fijar línea,
  marca, enderezar implemento, zoom, brillo, capturas, guardar/abrir, puntos, reinicios.
- Área táctil mínima 44×44 px. Los íconos rojos requieren confirmación antes de ejecutar.

## Pendiente / decisiones a tomar
- Logos de terceros (GitHub, YouTube, Google Earth, Bing, Discourse, AgIO, AgShare) se dejan
  fuera del restyle: usar el logo oficial de cada marca. `google-earth.svg` y `agshare.svg`
  son íconos funcionales genéricos, no logos.
- Íconos decorativos o de diagnóstico del set original (robot, "headache", indicadores de
  hardware específicos) no se rediseñaron; pedirlos si hacen falta.
- Diagramas de vehículo y enganche (vista lateral con acotaciones) quedaron fuera de este
  paquete; se pueden agregar como láminas aparte.

## Files
- `Iconos Agro Parallel.dc.html` — hoja de estilo completa (abre en el navegador).
- `icons/` — 197 SVG:
  - `ab-draw.svg`
  - `ab-lines-hide-show.svg`
  - `ab-pivot-circle.svg`
  - `ab-pivot.svg`
  - `ab-smooth.svg`
  - `ab-swap-points.svg`
  - `ab-track-a-plus.svg`
  - `ab-track-ab.svg`
  - `ab-track-curve.svg`
  - `add-new.svg`
  - `agshare.svg`
  - `antenna-left-offset.svg`
  - `antenna-no-offset.svg`
  - `antenna-right-offset.svg`
  - `arrow-down.svg`
  - `arrow-left.svg`
  - `arrow-right.svg`
  - `arrow-up.svg`
  - `auto-stop.svg`
  - `auto-upload-off.svg`
  - `auto-upload-on.svg`
  - `autosteer-config.svg`
  - `autosteer-off.svg`
  - `autosteer-on.svg`
  - `autotrack-off.svg`
  - `autotrack-on.svg`
  - `back-button.svg`
  - `backspace.svg`
  - `boundary-curve-line.svg`
  - `boundary-from-tracks.svg`
  - `boundary-left.svg`
  - `boundary-make-line.svg`
  - `boundary-outer.svg`
  - `boundary-pause.svg`
  - `boundary-play.svg`
  - `boundary-record-pivot.svg`
  - `boundary-record-tool.svg`
  - `boundary-record.svg`
  - `boundary-reduce.svg`
  - `boundary-right.svg`
  - `boundary-section-control.svg`
  - `boundary-stop.svg`
  - `boundary.svg`
  - `brightness-down.svg`
  - `brightness-up.svg`
  - `cabecera-off.svg`
  - `cabecera-on.svg`
  - `camera-2d.svg`
  - `camera-3d.svg`
  - `camera-north-2d.svg`
  - `cancel.svg`
  - `centrar.svg`
  - `charge-indicator.svg`
  - `charging-no.svg`
  - `chart.svg`
  - `color-locked.svg`
  - `color-unlocked.svg`
  - `colour-pick.svg`
  - `contorno-off.svg`
  - `contorno-on.svg`
  - `derecha.svg`
  - `deselect-all.svg`
  - `download-all.svg`
  - `download-and-use.svg`
  - `enderezar-implemento.svg`
  - `error.svg`
  - `field-stats.svg`
  - `field-tools.svg`
  - `fijar-linea.svg`
  - `file-close.svg`
  - `file-copy.svg`
  - `file-edit-name.svg`
  - `file-existing.svg`
  - `file-explorer.svg`
  - `file-menu.svg`
  - `file-new.svg`
  - `file-open.svg`
  - `file-previous.svg`
  - `file-save-as.svg`
  - `file-save.svg`
  - `flag-delete.svg`
  - `flag-green.svg`
  - `flag-yellow.svg`
  - `force-overwrite.svg`
  - `giro-off.svg`
  - `giro-on.svg`
  - `google-earth.svg`
  - `gps-dual.svg`
  - `gps-quality.svg`
  - `gps-single.svg`
  - `grid-rotate.svg`
  - `headland-build.svg`
  - `headland-delete-points.svg`
  - `headland-menu.svg`
  - `headland-off.svg`
  - `headland-on.svg`
  - `headland-reset.svg`
  - `headland-slice.svg`
  - `huellas-off.svg`
  - `huellas-on.svg`
  - `hydraulic-lift-off.svg`
  - `hydraulic-lift-on.svg`
  - `increment-minus.svg`
  - `increment-plus.svg`
  - `info.svg`
  - `isobus-section-off.svg`
  - `isobus-section-on.svg`
  - `isoxml.svg`
  - `izquierda.svg`
  - `job-active.svg`
  - `job-name-calendar.svg`
  - `job-name-time.svg`
  - `lat-lon.svg`
  - `letter-a.svg`
  - `letter-b.svg`
  - `linea-anterior.svg`
  - `linea-siguiente.svg`
  - `mapping-off.svg`
  - `mapping-on.svg`
  - `marca.svg`
  - `menu-hide-show.svg`
  - `mode-pure-pursuit.svg`
  - `mode-stanley.svg`
  - `navigation-settings.svg`
  - `next.svg`
  - `ok.svg`
  - `pan.svg`
  - `path-resume-close.svg`
  - `path-resume-last.svg`
  - `path-resume-start.svg`
  - `pause.svg`
  - `play.svg`
  - `point-add.svg`
  - `point-delete.svg`
  - `previous.svg`
  - `rec-path.svg`
  - `record.svg`
  - `reset-colors.svg`
  - `reset-default.svg`
  - `rtk-alarm.svg`
  - `salto-off.svg`
  - `salto-on.svg`
  - `save-to-cloud.svg`
  - `screen-to-png.svg`
  - `screenshot.svg`
  - `secciones-auto-off.svg`
  - `secciones-auto-on.svg`
  - `secciones-manual-off.svg`
  - `secciones-manual-on.svg`
  - `section-mapping.svg`
  - `section-off-below.svg`
  - `section-off-boundary.svg`
  - `section-on-boundary.svg`
  - `select-all.svg`
  - `settings.svg`
  - `sort.svg`
  - `special-functions.svg`
  - `steer-drive-off.svg`
  - `steer-drive-on.svg`
  - `steer-left.svg`
  - `steer-right.svg`
  - `steer-zero.svg`
  - `stop.svg`
  - `switch-active-closed.svg`
  - `switch-active-open.svg`
  - `switch-off.svg`
  - `switch-on.svg`
  - `tilt-down.svg`
  - `tilt-up.svg`
  - `track-curve.svg`
  - `track-line.svg`
  - `track-pivot.svg`
  - `tracks-invisible.svg`
  - `tracks-visible.svg`
  - `tram-lines.svg`
  - `tram-multi.svg`
  - `tram-outer.svg`
  - `trash.svg`
  - `upload-off.svg`
  - `upload-on.svg`
  - `warning.svg`
  - `webcam.svg`
  - `window-close.svg`
  - `window-day-mode.svg`
  - `window-maximize.svg`
  - `window-minimize.svg`
  - `window-night-mode.svg`
  - `wiz-steer-dot.svg`
  - `wiz-was-zero-reset.svg`
  - `wizard-wand.svg`
  - `youturn-h.svg`
  - `youturn-off.svg`
  - `youturn-on.svg`
  - `youturn-reverse.svg`
  - `youturn-u.svg`
  - `zoom-in.svg`
  - `zoom-out.svg`
