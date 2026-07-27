# Inventario de iconos/botones originales (AOG/FormGPS) → migración a UI nativa Avalonia

> Documento de trabajo **sesión ⇄ sesión**. Cataloga TODOS los iconos/botones de la UI
> original (WinForms FormGPS) y qué hace cada uno, para ir **tachando hecho/falta**,
> después **mejorar**, y por último **sumar lo nuevo**.
>
> **Leyenda estado:** ✅ hecho en la UI nueva · 🟡 parcial/por validar · ❌ falta · — n/a
> **Carril (2 carriles):** **L** = Leonardo (UI nativa Avalonia: botón/pantalla/overlay/mapa
> + el HTML del Hub que ya existe, montarlo por WebView y retocarlo) · **S** = Santiago
> (engine/back-end: comando ExecuteCommand, /api, servicios). Muchos ítems son **L+S**.
>
> **Nota 2026-07-24: Codex ya no trabaja.** El carril **C** (HTML del Hub) se **colapsó en L** —
> las pantallas HTML ya están hechas; lo que queda (cablearlas por WebView + ajustes) es de Leonardo.
> Un `(C)` en la columna Estado solo indica "es pantalla HTML", no un carril.
>
> El estado es una **primera pasada** — lo refinamos juntos ítem por ítem.

---

## ✅ VERIFICADO (2026-07-24) — Acciones de la pantalla del piloto (barra derecha + abajo)

> Primer grupo recorrido ítem por ítem: los botones que hacen **acciones mientras el
> piloto está activo**. Se cruzó el `CommandParameter` real de las barras contra el
> `switch` de `GuidanceEngineHost.Commands.cs` (handlers leídos: son reales, no stubs).
> **Casi todos están implementados en el engine.** Solo faltan 3 comandos.

| Botón | cmd | Engine | Estado | Carril pendiente |
|---|---|---|---|---|
| AutoSteer (enganchar) | `autosteer` | `PerformAutoSteerClick` | ✅ probado | — |
| AutoTrack (más cercana) | `autotrack` | toggle + auto-nearest | ✅ probado | — |
| Guía ‹ / › | `track_prev/next` | `CycleTrack` | ✅ probado | — |
| Elegir guía | `pick` | `SelectTrack` | ✅ | — |
| Secc. Auto | `sec_auto` | `SectionMasterAuto` | ✅ (pinta cobertura) | — |
| Secc. Manual | `sec_manual` | `SectionMasterManual` | 🟡 real, validar en mapa | L (validar) |
| Contorno | `contour` | `ToggleContour` | 🟡 real, validar | L (validar) |
| Bloqueo contorno | `contour_lock` | `SetLockToLine` | 🟡 real, validar | L (validar) |
| U-Turn | `uturn` | `ToggleYouTurn` | 🟡 real (req. boundary+track) | L (validar) |
| Centrar guía (snap) | `center` | `Trk.SnapToPivot` | 🟡 real, validar | L (validar) |
| Mover ‹ / › (nudge) | `nudge_left/right` | `Trk.NudgeTrack` | 🟡 real, validar | L (validar) |
| U-Turn skips | `uturn_skips` | `CycleYouTurnSkip` | 🟡 real | L (validar) |
| Cabecera on/off | `cabecera_onoff` | `ToggleHeadland` | 🟡 real | L (validar) |
| Cabecera secciones | `cabecera_secciones` | toggle | 🟡 real | L (validar) |
| Hidráulico | `hidraulico` | `ToggleHydraulicLift` | 🟡 real | L (validar) |
| Rumbo herramienta | `reset_herramienta` | `ResetToolHeading` | 🟡 real | L (validar) |
| Tram vista | `tram_vista` | `CycleTramDisplayMode` | 🟡 real | L (validar) |
| **ISOBUS** | `isobus` | ❌ no está | ❌ falta | **S** (comando + estado ISOBUS) |
| **Bandera** | `bandera` | ❌ no está | ❌ falta | **S** (registrar flag + /api) · **L** (dibujar en mapa) |
| **Color mapeo** | `mapeo_color` | ❌ no está | ❌ falta | **L** (color de cobertura, sin engine) |

**Balance:** 5 ✅ · 12 🟡 (handler real, falta validar el efecto en el mapa) · 3 ❌ (`isobus`, `bandera`, `mapeo_color`).

---

## 🔍 CORRECCIONES DE AUDITORÍA (2026-07-24) — errores del primer catálogo

> Segunda pasada adversarial (verificadores independientes vs la fuente). El catálogo salió
> **mayormente fiel**, pero con estos errores reales:

**Botón que FALTÓ (agregar):**
- `btnChargeStatus` — indicador de **carga/batería** (cluster superior derecho, junto a Datos GPS).
  No interactivo; icono `ChargeIndicator.png`, color verde OK / rojo alerta según carga.

**Filas INVENTADAS (borrar del catálogo crudo — no existen):**
- `track_new_a / track_new_ab / track_new_curve` como "OpenBuildTracksPanel(btnzAPlus/btnzABLine/btnzABCurve)"
  en el menú flotante: **fabricadas**. `OpenBuildTracksPanel` no existe; `btnzAPlus/btnzABLine/btnzABCurve`
  son botones INTERNOS de `FormBuildTracks`, no ítems del menú flotante. El flotante solo tiene "Crear guías"
  (`btnBuildTracks`). *(Nota: los comandos `track_new_ab/a/curve` SÍ existen en las BARRAS nuevas — pero como
  comandos de la UI Avalonia, no como estos ítems del menú flotante viejo.)*

**Funciones/iconos MAL descritos:**
- `lblHz` — NO es "Hz + PPS". Muestra **Hz + tiempo de frame (ms) + calidad de fix** (ej `5.0 ~ 12.3 RTK Fix`).
- `btnPlusAB` — icono real `AddNew` (no ABTrackA+); abre **ab-rapido.html (QuickAB)** = AB rápido por posición/rumbo,
  NO "una paralela desplazada".
- `btnTracksOff` — icono real `SwitchOff`; **deselecciona la guía activa** (`trk.idx=-1`), NO "oculta guías".
- `btnCycleLinesBk` — comportamiento **dual**: con contorno activo hace `SetLockToLine` (bloquea, NO cicla);
  solo cicla a la guía anterior con el contorno apagado.
- `btnContourLock` — iconos reales `ColorLocked/ColorUnlocked` (estaba como placeholder).
- `btnYouSkipEnable` — cicla **3 modos** (Normal→Alternado→Ignorar trabajados), no un on/off.
- `btnResumePath` — NO reanuda; **cicla el estilo de reanudado** (Último punto→Más cercano→Desde el inicio).
- `btnResetSteerAngle` — sin icono, es texto `>0<` (no `SteerZero.png`).
- `btnPathGoStop` / `btnPathRecordStop` — iconos reales `boundaryPlay`/`BoundaryRecord` (no RecPath/Play).

**Ubicaciones mal agrupadas** (el catálogo puso en "barra inferior" cosas que están en otra zona):
- `btnSectionMasterAuto/Manual/Isobus` → **columna derecha** (panelRight). `lblSpeed` → **arriba a la derecha**
  (panelControlBox). Solo los botones individuales de sección/zona están realmente abajo.

**Duplicaciones** (no son errores, pero conviene saberlo): la región "Menús" del catálogo crudo **repite** casi todos
los botones de Guiado/Vista/Secciones/Barra-superior, porque el menú flotante los espeja. En este doc consolidado ya
están deduplicados.

**Solapas de Config — VERIFICADO OK (2026-07-24):** las 20 solapas (vehículo Tipo/Dimensiones/Antena/Guiado,
implemento Tipo/Enganche/Offset/Pivot/Secciones/Switches/Ajustes, fuentes Rumbo/Roll/Módulo/Relés, +
Resumen/U-Turn/Display/Botones/Tram) EXISTEN como `TabPage` reales en `FormConfig.Designer.cs` (60 TabPages en
`Settings/*.Designer.cs`). El menú flotante las abre con `FloatMenuOpenConfig("tabX")` — el catálogo las describió
bien. Sin errores ni faltantes en este sub-grupo.

**Confirmado correcto:** secciones 1..16 + zonas 1..8 (ciclan Off→Auto→On, colores rojo/verde/ámbar), panel de
navegación/vista (11 controles, sin zoom), y el resto de las funciones de guiado.

---

## 1) Barra superior / estado

| Botón (original) | Icono | Qué hace | Estado | Carril |
|---|---|---|---|---|
| Nombre del lote (label) | — | Muestra el lote abierto; toque pausa/reanuda el rotado de info | 🟡 | L |
| Menú de lote (btnJobMenu) | JobActive | Continuar/crear/abrir/borrar lote | 🟡 | L+S |
| Autosteer ON/OFF (btnAutoSteer) | AutoSteerOn/Off | Engancha/desengancha el piloto (bloquea sobre velocidad máx) | ✅ | L+S |
| Config autoguiado (btnAutoSteerConfig) | AutoSteerConf | Abre FormSteer (config dirección); texto = ángulo actual | 🟡 (C) | L+S |
| Datos GPS (btnGPSData) | GPSQuality | Abre datos-gps.html (calidad antena) | 🟡 | L |
| Datos del lote (btnFieldStats) | FieldStats | Abre datos-lote.html (stats del lote) | 🟡 | L |
| Estado de carga (btnChargeStatus) | ChargeIndicator | Indicador batería/carga (no interactivo; color verde OK / rojo alerta) | ❌ | L |
| CoreX/AgIO (btnStartAgIO) | AgIO | Lanza CoreX.exe + abre su dashboard | 🟡 | L+S |
| Engranaje/Ajustes (dropDown1) | Settings48 | Menú config (config/steer/todos/dir/gps/colores) | 🟡 | L |
| Tools/Funciones especiales (dropDown4) | SpecialFunctions | Menú de herramientas/diagnóstico (SIEMPRE activo) | 🟡 | L |
| Herramientas de lote (FieldTools) | FieldTools | Menú del lote (off sin lote) | 🟡 | L |
| Minimizar/Maximizar/Cerrar | WindowMin/Max/Close | Acciones de ventana | ✅ | L |

## 2) Guiado — guías/tracks

| Botón | Icono | Qué hace | Estado | Carril |
|---|---|---|---|---|
| Nueva A/B en mapa (track_new_ab) | ABTrackAB | Flujo A→B: marca A, maneja, marca B | ✅ | L+S |
| AB rápido (btnPlusAB) | AddNew | Abre ab-rapido.html (QuickAB): AB por posición/rumbo actual | 🟡 (C) | L+S |
| Nueva A+ (track_new_a) | APlusPlusA | Crea guía paralela desplazada (A+) | 🟡 | L+S |
| Nueva curva (track_new_curve) | ABTrackCurve | Crea A/B curva | ❌ | L+S |
| Dibujar AB (btnABDraw) | ABDraw | Dibuja AB tocando puntos en el mapa | 🟡 | L+S |
| Construir tracks (btnBuildTracks) | ABTracks | Editor de tracks (tracks.html) | 🟡 (C) | L+S |
| Elegir guía (btnTrack) | TrackOn | Lista para elegir la guía activa | ✅ | L+S |
| Guía siguiente (btnCycleLines) | ABLineCycle | Cicla al track siguiente | ✅ | L+S |
| Guía anterior (btnCycleLinesBk) | ABLineCycleBk | Cicla al track anterior | ✅ | L+S |
| Auto-track (btnAutoTrack) | AutoTrack On/Off | Selección automática del track más cercano | ✅ | L+S |
| Apagar tracks (btnTracksOff) | SwitchOff | Desactiva el track activo (idx=-1) | ✅ | L+S |
| Snap a pivote (btnSnapToPivot) | SnapToPivot | Snap del track al pivote del vehículo | ❌ | L+S |
| Nudge izq/der (btnAdjLeft/Right) | SnapLeft/Right | Mueve el track un paso izq/der | ❌ | L+S |
| Mover guía (btnNudge) | ABSnapNudgeMenu | Menú mover guía (mover-guia.html) | 🟡 (C) | L+S |
| Nudge referencia (btnRefNudge) | ABSnapNudgeMenuRef | Mueve la línea de referencia | ❌ | L+S |
| Contorno ON/OFF (btnContour) | ContourOn/Off | Guiado por contorno (seguir lo aplicado) | 🟡 | L+S |
| Bloqueo contorno (btnContourLock) | ColorLocked | Fija el contorno a la línea actual | ❌ | L+S |
| Suavizar AB (SmoothAB) | ABSmooth | Suaviza la curva AB (suavizar-ab.html) | ❌ (C) | L+S |
| Importar guías (copyTracks) | FileNew | Importa tracks de otro lote (FormCopyTracks) | ❌ | S |
| Índice/total (lblNumCu) | — | Muestra "idx/total" de tracks | ✅ | L |

## 3) Guiado — operación (piloto)

| Botón | Icono | Qué hace | Estado | Carril |
|---|---|---|---|---|
| U-Turn auto (btnAutoYouTurn) | Youturn/No | Giro de cabecera automático (requiere boundary+track) | 🟡 | L+S |
| Modo salto surcos (btnYouSkipEnable) | YouSkip* | Cicla Normal→Alternado→Ignorar trabajados | ❌ | L+S |
| Cant. surcos a saltar (cboxpRowWidth) | — | Cuántos surcos saltar en el giro | ❌ | L+S |
| Auto-snap a pivote (cboxAutoSnapToPivot) | AutoSteerSnapToPivot | Al enganchar, el track salta al pivote | ❌ | L+S |
| Levante hidráulico (btnHydLift) | HydraulicLift On/Off | Activa/desactiva el levante del implemento | ❌ | L+S |

## 4) Secciones (barra inferior)

| Botón | Icono | Qué hace | Estado | Carril |
|---|---|---|---|---|
| Master AUTO (btnSectionMasterAuto) | SectionMasterOn/Off | Control automático de todas las secciones | ✅ (pinta cobertura) | L+S |
| Master MANUAL (btnSectionMasterManual) | ManualOn/Off | Todas las secciones On/Off a mano | 🟡 | L+S |
| Secciones individuales 1..16 | — (numeradas) | Cicla cada sección Off→Auto→On | ❌ | L+S |
| Zonas 1..8 | — (numeradas) | Cicla un grupo de secciones | ❌ | L+S |
| Corte en cabecera (cboxIsSectionControlled) | HeadlandSection On/Off | Apaga secciones al entrar en cabecera | ❌ | L+S |
| Secciones ISOBUS (btnIsobusSectionControl) | IsobusSectionControl | Control de secciones por ISOBUS | ❌ | L+S |
| Velocidad (lblSpeed) | — | Velocidad actual (solo texto) | ✅ | L |

## 5) Lote / campo

| Botón | Icono | Qué hace | Estado | Carril |
|---|---|---|---|---|
| Continuar/Abrir/Cerrar lote | — | Reabrir último / selector / cerrar (JobClose) | 🟡 | L+S |
| Lindero/Boundary (boundaries) | Boundary | Crear/reproducir contorno (contorno.html) | ❌ (C) | L+S |
| Herram. límites (boundaryTool) | BoundaryRecordTool | Límite por implemento (FormBndTool) | ❌ | L+S |
| Cabecera/Headland (headland) | HeadlandOn | Construir/editar cabecera (cabecera.html) | ❌ (C) | L+S |
| Cabecera avanzada (headlandBuild) | Headache | Cabecera por líneas (cabecera-lineas.html) | ❌ (C) | L+S |
| Cabecera SÍ/NO (btnHeadlandOnOff) | HeadlandOn/Off | Activa/desactiva el corte por cabecera | ❌ | L+S |
| TramLines crear (tramLinesMenuField) | TramAll | Editor de tramlines (tramline.html) | 🟡 (C) | L+S |
| Tram vista (btnTramDisplayMode) | Tram* | Cicla modo de visualización de tram | 🟡 | L+S |
| Bandera (btnFlag) | FlagGrn | Deja bandera en la posición actual | ❌ | L+S |
| Bandera lat/lon (flagByLatLon) | FlagRed | Bandera por coordenadas (banderas.html) | ❌ (C) | L+S |
| Borrar aplicado (deleteApplied) | TrashApplied | Borra cobertura/contornos + resetea área | ❌ | L+S |
| Color mapeo (btnChangeMappingColor) | MappingOn | Color de la cobertura de secciones | ❌ | L |
| Rumbo herramienta (btnResetToolHeading) | ResetTool | Endereza el implemento al rumbo GPS | ❌ | L+S |
| Ruta grabada (Go/Stop/Record/Pick/Resume/SwapAB) | RecPath/Play | Grabar/reproducir recorridos | ❌ | L+S |
| Importar tracks (copyTracks) | FileNew | Importa guías de otro lote | ❌ | S |

## 6) Vista / cámara (panel navegación)

| Botón | Icono | Qué hace | Estado | Carril |
|---|---|---|---|---|
| Navegación (btnNavigationSettings) | NavigationSettings | Abre/cierra el panel de cámara/mapa | ✅ | L |
| Inclinar +/− (btnTiltUp/Dn) | TiltUp/Down | Pitch de cámara (2D↔3D) | ✅ (squish+shift, no perspectiva real) | L |
| Vista 2D (btn2D) | Camera2D64 | Cenital siguiendo al tractor | ✅ | L |
| Vista 3D (btn3D) | Camera3D64 | Perspectiva siguiendo al tractor | ✅ (squish+shift, no perspectiva real) | L |
| Norte-2D (btnN2D) | CameraNorth2D | Cenital norte-arriba (no rota) | ✅ | L |
| Grilla (btnGrid) | GridRotate | Muestra/oculta/configura la grilla | ✅ (on/off simple, sin el diálogo de alineación del legacy) | L |
| Día/Noche (btnDayNightMode) | WindowNightMode | Alterna paleta día/noche | ✅ (solo el mapa; el chrome de las barras no cambia) | L |
| Brillo +/− (btnBrightnessUp/Dn) | BrightnessUp/Dn | Brillo de pantalla | 🟡 (UI cableada contra SistemaClient; el motor `--webhost` no tiene `ISistemaService` → PEDIDO a Leonardo) | L+S |
| Hz+frame+fix (lblHz) | — | Frecuencia GPS (Hz) + tiempo de frame (ms) + calidad de fix (NO es PPS) | 🟡 | L |

## 7) Configuración (mayormente HTML por WebView)

| Grupo | Qué hace | Estado | Carril |
|---|---|---|---|
| **Vehículo** (Tipo/Dimensiones/Antena/Guiado) | config.html solapas de vehículo | 🟡 (C) | L+S |
| **Implemento** (Tipo/Enganche/Offset/Pivot/Secciones/Switches/Ajustes) | config.html solapas de herramienta | 🟡 (C) | L+S |
| **Fuentes datos** (Rumbo/Roll/Módulo máquina/Relés) | config.html solapas de fuentes | 🟡 (C) | L+S |
| **U-Turn / Display / Botones / Tram / Resumen** | config.html solapas varias | 🟡 (C) | L+S |
| **Dirección/Autosteer** (FormSteer) | Config del autoguiado | 🟡 (C) | L+S |
| **Todos los ajustes** (ajustes-todos.html) | Volcado solo-lectura | 🟡 (C) | L |
| **Colores** (colores.html) | Colores marco/campo/texto día-noche | 🟡 (C) | L |
| **Colores secciones** (colores-secciones.html) | 16 colores de sección + multicolor | 🟡 (C) | L |
| **Perfiles** (nuevo/cargar, perfiles.html) | Gestión de perfiles de máquina | 🟡 (C) | L+S |
| **Directorios** | Carpeta de trabajo (lotes/vehículos) | 🟡 | L+S |

## 8) Diagnóstico

| Botón | Qué hace | Estado | Carril |
|---|---|---|---|
| Datos GPS (datos-gps.html) | Datos crudos del GPS | 🟡 (C) | L |
| Asistente dirección (FormSteerWiz) | Calibración paso a paso del autosteer | ❌ (C) | L+S |
| Gráfico dirección (grafico-direccion.html) | Ángulo real vs seteado en vivo | 🟡 (C) | L+S |
| Gráfico rumbo (grafico-rumbo.html) | GPS vs IMU corregido | 🟡 (C) | L+S |
| Gráfico XTE (grafico-xte.html) | Error de guiado en vivo | 🟡 (C) | L+S |
| Chequeo roll (grafico-correccion.html) | Corrección roll IMU vs deriva GPS | 🟡 (C) | L+S |
| Corregir posición (corregir-posicion.html) | Corrimiento de deriva GPS | ❌ (C) | L+S |
| Visor eventos (eventos.html) | Registro de eventos solo-lectura | 🟡 (C) | L |
| Webcam/Cámaras (camaras.html) | Cámaras Hikvision RTSP | ✅ (overlay nativo) | L+S |

## 9) Agro Parallel / productos X-*

| Botón | Qué hace | Estado | Carril |
|---|---|---|---|
| Hub Agro Parallel | Abre el Hub como widget flotante | ✅ (nativo) | L |
| CoreX (dashboard) | Dashboard de CoreX/AgIO | 🟡 | L+S |
| Cámaras (widget) | Vista solo-cámaras | ✅ (nativo) | L |
| VistaX · Semilla/Máquina/Densidad | Overlays de siembra VistaX | ✅ (overlays nativos) | L+S |
| Barras HTML ⇄ nativas | Alterna barras nativas/HTML | ✅ (barras nativas) | L |

## 10) Simulador

| Botón | Qué hace | Estado | Carril |
|---|---|---|---|
| Simulador SÍ/NO | Enciende/apaga el sim de GPS | 🟡 (engine --sim) | L+S |
| Coordenadas sim (sim-coords.html) | Reubica el sim a lat/lon | ❌ (C) | L+S |
| Reset / Reversa / Velocidad 0 / Ángulo 0 | Controles del simulador | ❌ | L+S |

## 11) Sistema

| Botón | Qué hace | Estado | Carril |
|---|---|---|---|
| Minimizar/Maximizar | Acciones de ventana | ✅ | L |
| Modo kiosko | Pantalla completa + oculta ventana | 🟡 | L |
| Ayuda (ayuda.html) | Utilidades del Hub + Acerca de | 🟡 (C) | L |
| Reset de fábrica | Restablece toda la config (confirmación) | ❌ | L+S |
| Apagar (btnShutdown) | Cierra PilotX | ✅ | L |

---

## Resumen de reparto (para arrancar)

- **Leonardo (UI nativa):** las botoneras del cockpit ya traen guiado/secciones/tracks;
  **falta**: nudge/snap izq-der, contour-lock, youskip, secciones individuales/zonas,
  banderas, ruta grabada, y montar los diálogos HTML por WebView (config/diagnóstico) en
  ventana chica. **Hecho:** controles de cámara (2D/3D/N-2D/tilt/día-noche/brillo/grilla,
  2026-07-27).
- **Santiago (engine):** los comandos/servicios detrás — snap/nudge de track, youskip,
  secciones individuales/zonas, boundary/headland/tram builders, banderas, ruta grabada,
  hyd-lift, import tracks, reset-tool-heading, controles del simulador por API.
- **Codex (HTML):** las pantallas de config/diagnóstico ya existen; solo hay que
  cablearlas por el WebView (L) + su controller/servicio (S).

> Próximo paso: recorrer ítem por ítem y **tachar** (✅) lo que confirmemos hecho, marcar
> ❌ lo que falta, y asignar dueño final L/S. Después: mejorar. Por último: sumar lo nuevo.
