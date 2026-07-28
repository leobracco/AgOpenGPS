# PilotX (Avalonia) vs AgOpenGPS 6.8.5 — comparación de funciones

> Medido el 2026-07-27 contra el baseline pristino en
> `G:\agroparallel\productos\AgOpenGPS\Software\App_PC\AgOpenGPS-6.8.5\`.
> No es una impresión: los números salen de extraer los handlers reales del
> 6.8.5 y cruzarlos contra los comandos que hoy responde el motor
> (`GuidanceEngineHost.Commands.cs`) y contra los que resuelve la UI localmente
> (`MainWindow.RouteCockpitCommand`).

## Cómo se midió

| Lado | Qué se contó | Cómo |
|---|---|---|
| 6.8.5 | **58** botones de la pantalla principal | `btnXxx_Click` en `Controls.Designer.cs` + `GUI.Designer.cs` + `Sections.Designer.cs` |
| 6.8.5 | **19** ítems de menú desplegable | `XxxToolStripMenuItem_Click` en `Forms/` |
| 6.8.5 | **426** handlers en total (incluye las 40+ ventanas de config) | todo el árbol |
| PilotX | **29** comandos que responde el motor | `case "…"` + prefijos en `ExecuteCommand` |
| PilotX | **82** comandos que emite la UI | `CommandParameter` en `PilotX.Cockpit.Bars/Views/*.axaml` |
| PilotX | **29** resueltos localmente por la UI (abren pantalla/panel) | `RouteCockpitCommand` |

**Dato importante**: que la UI emita 82 comandos y el motor responda 29 NO es una
brecha de 53. La mayoría de los 82 son navegación (abrir una pantalla del Hub) y
los resuelve la UI sin tocar el motor — es el diseño acordado.

---

## Lo que YA funciona (verificado en runtime, no solo compilando)

### Guiado
`autosteer`, `autotrack`, `track_next`/`track_prev`, `track_nearest`,
`tracks_off`, `pick`, `center` (snap a pivote), `nudge_left`/`nudge_right`,
`contour`, `contour_lock`, `uturn`, `uturn_skips`, `track_ab_here_<grados>`
(crear AB en la posición actual).

### Secciones
`sec_auto`, `sec_manual`, **`seccion_<1..16>`** y **`zona_<1..8>`** (ciclan
Off→Auto→On), `cabecera_onoff`, `cabecera_secciones`, `hidraulico`,
`reset_herramienta`, `tram_vista`.

### Lote
`job_start_<lote>`, `job_close` / `lote_cerrar`, `borrar_aplicado`,
`borrar_contornos`.

### Pantallas (resueltas por la UI contra el Hub)
Configuración, Dirección, gráficos (dirección/rumbo/XTE/roll), datos GPS y de
lote, colores, directorios, perfiles, cámaras, eventos, actualizar, suavizar AB,
importar guías, lindero, cabecera, tramline, banderas por lat/lon, corregir
posición, sim-coords.

### Cosas que el 6.8.5 NO tiene y PilotX sí
- Sprite del vehículo **y del implemento** dibujados a escala real en el mapa
  Avalonia, con ruedas delanteras giradas por Ackermann.
- Botonera de secciones con los 3 estados y soporte de **zonas** en la misma barra.
- Todo el Hub HTML (QuantiX, VistaX, FlowX, StormX, SectionX, LineX, OrbitX, OTA).
- API HTTP completa: el motor corre headless y cualquier cliente lo maneja.

---

## Brechas reales (emitido por la UI y sin respuesta en ningún lado)

Son **~24** (eran ~30 en la primera medición: la familia "Lote" resultó un falso
positivo, ver abajo), y se agrupan en 4 familias:

### 1. Cámara y vista — 9 · *puro cliente, no necesita motor*
`v2d`, `v3d`, `norte2d`, `tilt_up`, `tilt_dn`, `grilla`, `dia_noche`,
`brillo_up`, `brillo_dn`
> Hoy el mapa es heading-up fijo con grilla fija. Es trabajo de `MapGlSurface`
> solo; el 6.8.5 los tiene todos.

### 2. ~~Lote — 6~~ · **CORREGIDO: era un falso positivo de la medición**
> Al ir a cablearlo se comprobó que **ya estaba ruteado**: `lote_menu`,
> `lote_continuar`, `lote_nuevo` y `lote_kml` abren `pages/lote.html` (con
> deep-link `?do=`) desde `MainWindow.RouteCockpitCommand`, y `lote_datos` abre
> `datos-lote.html`. El cruce automático no los vio porque están en un `switch`
> con `case`, y el grep buscaba la forma de expresión `"x" => …`.
>
> Lo que sí faltaba era del lado del MOTOR, y se implementó el 2026-07-27:
> `DeleteFieldAsync` (con negativa a borrar el lote abierto) y
> `CreateFromExistingAsync` (port 1:1 del nativo). Verificado por HTTP.
>
> **Sigue sin andar a propósito**: `import-kml` / `import-isoxml`, porque en el
> nativo abren un diálogo de archivo de WinForms y la API todavía no recibe una
> ruta. Devuelven `false` en vez de fingir que importaron.
>
> **Lección de método**: medir por grep sobre una sola forma sintáctica
> sobrestima las brechas. Los números de este documento son un punto de partida
> para ir a mirar, no un veredicto.

### 3. Ventana y sistema — 6
`minimizar`, `maximizar`, `apagar`, `kiosco`, `reset_all`, `simulador`
> Los tres primeros son de ventana (fáciles). `simulador` y `reset_all` tocan
> estado real.

### 4. Funciones del 6.8.5 todavía ausentes — 9
`ruta_grabada` (grabar/reproducir recorridos), `isobus`, `webcam`,
`tram_multi`, `datos_gps`/`corex` (navegación no cableada),
`herramientas`/`herrlote`/`navegacion` (solo despliegan submenú),
`idioma_*` (7 idiomas).
> `ruta_grabada` es la más grande: en 6.8.5 son 6 botones
> (`btnPathGoStop`, `btnPathRecordStop`, `btnPickPath`, `btnResumePath`,
> `btnSwapABRecordedPath`) + toda la máquina de grabación.

---

## Del lado del 6.8.5 que no está en el inventario de comandos

Estos existen como botón en el 6.8.5 y no tienen equivalente hoy:

| 6.8.5 | Qué hace | Estado |
|---|---|---|
| `btnSimForward` / `btnSimReverse` / `btnSimReverseDirection` / `btnSimSetSpeedToZero` / `btnResetSim` | Controles del simulador | ❌ (el motor tiene `--sim` pero sin control por API) |
| `btnPathGoStop` / `btnPathRecordStop` / `btnPickPath` / `btnResumePath` / `btnSwapABRecordedPath` | Ruta grabada | ❌ |
| `btnSnapToPivot` / `btnRefNudge` | Snap y nudge de la línea de referencia | ❌ (existe `center` y `nudge_*`, falta el de referencia) |
| `btnNavigationSettings` | Panel de cámara | ❌ |
| `btnResetSteerAngle` | Poner el ángulo de dirección en cero | ✅ (está en la pantalla Dirección) |
| `btnStartAgIO` | Lanzar CoreX | ❌ navegación no cableada |
| `btnFlag` | Bandera en la posición actual | ❌ (falta portar `FlagsFiles` al Core) |
| `btnChangeMappingColor` | Color de la cobertura | ❌ |

---

## Lectura honesta

**En guiado y secciones estamos a la par o mejor**: todo lo que el operario usa
para trabajar una pasada —enganchar el piloto, elegir y ciclar guías, mover la
guía, secciones y zonas, cabecera, U-turn, contorno— está y fue verificado
contra el motor real.

**Las brechas se concentran en dos lugares**, y ninguna es de guiado:

1. **Vista y cámara** (9 comandos) — 2D/3D/Norte/tilt/grilla/día-noche/brillo.
   Es puro cliente (`MapGlSurface`), no toca el motor. El 6.8.5 los tiene todos.
2. **Ruta grabada** — un subsistema entero: 5 botones más toda la máquina de
   grabación y reproducción.

(La "entrada al trabajo" —el submenú LOTE— figuraba acá en la primera medición y
resultó ser un falso positivo: ya estaba cableada.)

**Lo que no conviene medir por cantidad de botones**: el 6.8.5 tiene 426
handlers, pero ~350 son de las ventanas de configuración, que en PilotX
migraron a pantallas HTML del Hub y hoy funcionan contra el motor
(`/api/aog/config` y `/api/tool`, cerrados hoy mismo). Comparar 426 contra 29
sería engañoso.
