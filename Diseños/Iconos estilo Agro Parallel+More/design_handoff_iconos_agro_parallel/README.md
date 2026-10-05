# Handoff: Set de íconos de guiado · Agro Parallel

## Overview
Set de 28 archivos de ícono (18 funciones) para una pantalla de tractor / monitor de guiado,
en el lenguaje visual Agro Parallel: lineales, trazo uniforme, gris como estado neutro y verde
únicamente como estado activo. Se divide en dos grupos de navegación:

- **Barra de funciones (horizontal, pie de pantalla):** AutoSteer, Secciones Manual,
  Secciones Automático, Izquierda, Centrar, Derecha, Giro.
- **Menú lateral (vertical):** Línea siguiente, Línea anterior, AutoTrack, Contorno, Marca,
  Enderezar implemento, Fijar línea, Huellas, Salto de pasada, Cabecera.

## About the Design Files
Los archivos de este paquete son **referencias de diseño hechas en HTML/SVG**: prototipos que
muestran la apariencia y los estados previstos, no código de producción para copiar tal cual.
La tarea es **recrear estos diseños en el entorno existente del codebase destino** (React, Vue,
Qt, SwiftUI, nativo, etc.) usando sus patrones y librerías establecidas. Si todavía no hay
entorno, elegir el framework más apropiado e implementar allí. Los SVG de `icons/` sí pueden
usarse directamente como assets.

## Fidelity
**Alta fidelidad (hifi).** Colores, geometría, grosor de trazo y estados son definitivos.
Los íconos deben verse pixel-perfect respecto a los SVG entregados.

## Screens / Views

### Hoja de estilo de íconos (`Iconos Agro Parallel.dc.html`)
- **Propósito:** documentar cada ícono con sus estados y su uso en barra y en menú lateral.
- **Layout:** lienzo de 1440 px de ancho, padding 56px, fondo `#F5F7F4`. Encabezado con
  título 34px/600 y subtítulo 17px `#535E54`, separador 1px `#E2E7E2`. Luego: grilla de 3
  columnas (gap 24px) con las funciones de la barra; sección "menú lateral" con el riel
  vertical a la izquierda (116px) y grilla de 3 columnas a la derecha; barra de funciones
  horizontal; y panel de modo noche.
- **Tarjeta:** fondo `#FFFFFF`, borde 1px `#E2E7E2`, radio 18px, padding 24px, gap 20px.
  Título 18px/600 `#101612`, descripción 13px/400 `#535E54`.
- **Casilla de estado:** alto 108px, radio 14px, ícono a 60×60.
  ON: fondo `#EFF7ED`, borde `#D3E8CE`, etiqueta `ON` 12px/600 `#4ABA3E`, letter-spacing 0.12em.
  OFF: fondo `#F5F7F4`, borde `#E2E7E2`, etiqueta `OFF` 12px/600 `#8B948B`.
- **Barra horizontal:** fila blanca, radio 18px, celdas de igual ancho separadas por borde 1px
  `#E2E7E2`, padding 18px/12px, ícono 30px + etiqueta 11px/600 letter-spacing 0.08em.
  Celda activa: fondo `#EFF7ED`, texto `#4ABA3E`.
- **Riel vertical:** ancho 116px, fondo `#FFFFFF`, borde 1px `#E2E7E2`, radio 18px,
  padding 10px arriba/abajo; cada ítem padding 12px/8px, ícono 30px + etiqueta 10px/600
  letter-spacing 0.06em centrada. Ítem activo: fondo `#EFF7ED`, texto `#4ABA3E`.
- **Modo noche:** panel `#101612`, radio 18px; activos `#5FD152`, inactivos `#C5CFC5`.

## Íconos (contenido)
| Archivo(s) | Nombre en UI | Significado | Estados |
|---|---|---|---|
| autosteer-on/off | AutoSteer | guiado automático | ON / OFF |
| secciones-manual-on/off | Secciones · Manual | apertura y cierre a criterio del operario | ON / OFF |
| secciones-auto-on/off | Secciones · Automático | conmutación por mapa de cobertura | ON / OFF |
| giro-on/off | Giro | giro automático en cabecera | ON / OFF |
| izquierda | Izquierda | desplazar línea a la izquierda | único |
| derecha | Derecha | desplazar línea a la derecha | único |
| centrar | Centrar | ajustar a la línea de referencia | único |
| linea-siguiente | Línea siguiente | ciclar líneas AB hacia adelante | único |
| linea-anterior | Línea anterior | ciclar líneas AB hacia atrás | único |
| fijar-linea | Fijar línea | tomar la línea actual como referencia | único |
| autotrack-on/off | AutoTrack | seguimiento automático de línea | ON / OFF |
| contorno-on/off | Contorno | guiado por líneas curvas | ON / OFF |
| marca | Marca | banderín de punto de interés | único |
| enderezar-implemento | Enderezar implemento | alinea el implemento detrás del tractor | único |
| huellas-on/off | Huellas | trochas de pulverización | ON / OFF |
| salto-on/off | Salto de pasada | trabajar salteando líneas | ON / OFF |
| cabecera-on/off | Cabecera | corte de secciones en cabecera | ON / OFF |

Construcción: viewBox 48×48, `stroke-width` 2.4 (subir a 3 cuando se dibuja a 30px o menos),
`stroke-linecap` y `stroke-linejoin` redondos, sin relleno salvo elementos de estado activo
(hub del volante, secciones llenas, banderín).

## Interactions & Behavior
- **Toggles** (cambian color de ícono, etiqueta y fondo de celda): AutoSteer, Secciones
  manual/auto, Giro, AutoTrack, Contorno, Huellas, Salto de pasada, Cabecera.
  Transición sugerida `color/background 120ms ease-out`.
- **Excluyentes:** Secciones manual ↔ automático; Contorno ↔ guiado recto (AB).
- **Acciones momentáneas** (sin estado persistente, feedback de pulsado con fondo `#E2E7E2`
  ~120ms): Izquierda, Derecha, Centrar, Línea siguiente, Línea anterior, Fijar línea,
  Marca, Enderezar implemento.
- Área táctil mínima 44×44 px; las celdas de la barra ocupan todo el alto disponible y los
  ítems del riel todo el ancho.
- Modo noche: mismo comportamiento, paleta oscura.

## State Management
- `autoSteer: boolean`
- `sectionMode: 'manual' | 'auto' | 'off'`
- `youTurn: boolean` (Giro)
- `autoTrack: boolean`
- `guidanceMode: 'ab' | 'contour'`
- `tramlines: boolean`
- `skipPass: boolean` (+ cantidad de pasadas a saltear)
- `headlandSections: boolean`
- `activeLineIndex: number` (línea siguiente / anterior / fijar línea)
- `nightMode: boolean`
- Acciones que emiten eventos sin estado propio: `nudgeLeft`, `nudgeRight`, `snapToLine`,
  `setFlag`, `straightenImplement`.

## Design Tokens
Colores: verde activo `#4ABA3E`; verde noche `#5FD152`; fondo activo `#EFF7ED`; borde activo
`#D3E8CE`; gris texto `#535E54`; gris inactivo `#8B948B`; gris claro `#C5CFC5`; borde
`#E2E7E2`; fondo página `#F5F7F4`; blanco tarjeta `#FFFFFF`; negro `#101612`;
rojo de alerta/marca `#C2452F`.
Radios: 7 / 14 / 18 px. Espaciado: 4, 7, 9, 10, 12, 16, 20, 24, 40, 56 px.
Tipografía: Helvetica Neue / Helvetica, sans-serif. Escala: 34/600, 18/600, 17/400, 13/400,
12/600 (0.12em), 11/600 (0.08em), 10/600 (0.06em).
Trazo de ícono: 2.4 (48–60px) · 3.0 (≤30px). Dash de cabecera: `5 4`.

## Assets
`icons/*.svg` — 28 archivos exportados desde este diseño, listos para usar.
Redibujados en estilo Agro Parallel a partir de los PNG originales del cliente (AutoSteer,
Manual, SectionMaster, Snap Left/Right/Pivot, YouTurn, ABLineCycle, AutoTrack, Contour,
Flag, HeadlandSection, ResetTool, TrackOn, Tram, YouSkip).

## Files
- `Iconos Agro Parallel.dc.html` — hoja de estilo completa (abre en el navegador).
- `icons/` — SVG individuales por ícono y estado.
