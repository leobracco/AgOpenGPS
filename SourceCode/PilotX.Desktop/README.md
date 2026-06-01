# PilotX.Desktop

Shell **Avalonia** oficial de PilotX. UI 100% nativa (Skia + Avalonia).
El WebView (Chromium/Edge) NO es el target final: es puente lazy mientras
quedan pantallas sin portar.

## Estrategia (strangler fig + zero residente Chromium)

```
+-------------------+      +--------------------------------+
| FormGPS (WinForms)|      | PilotX.Desktop                 |
| + AgroParallel    |      | (Avalonia 11.2.3, net9.0)      |
|   .Shell WebView2 |      |                                |
+--------+----------+      | MAPA NATIVO (MapPanel/Skia)    |
         |                 |   + HUD nativo (4 Hz)          |
         |                 |   + MiniMap nativo overlay     |
         |                 |   + WebView LAZY on-demand,    |
         |                 |     Dispose al cerrar          |
         v                 +-----------+--------------------+
   +-----+---------------------+   |
   |   AgpWebHost :5180        |<--+ (solo cuando hay WebView abierto)
   +---------------------------+
```

- **WinForms** sigue siendo el productivo en el tractor (intacto).
- **PilotX.Desktop** arranca con render nativo. El WebView se instancia
  *solo* cuando el operario abre una pantalla todavia no portada (Hub/
  productos X-*); al cerrarla, se hace `Children.Remove` + `null` y se
  pide `GC.Collect(2, Optimized, blocking:false)` para devolver la RAM
  del Chromium hijo a baseline.
- **Objetivo**: bajar de ~400MB (Chromium residente) a ~80-150MB. Cada
  pantalla portada a nativo = un WebView menos. Cuando no quede UI web
  residente = eliminar EmbedIO/AgpWebHost (:5180).

## Tuning de runtime

`PilotX.Desktop.csproj`:

| Setting | Valor | Por que |
|---------|-------|---------|
| `ServerGarbageCollection` | `false` | Workstation GC: menos RAM/CPU permanentes en una PC embebida de tractor (vs Server GC = 1 heap/core). |
| `ConcurrentGarbageCollection` | `true` | Marca/sweep en background, sin pausas del UI thread. |
| `TieredCompilation` + `QuickJit` | `true` | Arranque mas barato; recompila hot paths despues. |
| `InvariantGlobalization` | `true` | Evita cargar ICU (~25MB). El cockpit usa `CultureInfo.InvariantCulture` para todos los numeros. |
| Release: `PublishReadyToRun` + `Composite` | `true` | R2R precompila IL a nativo para arranque mas rapido (en Release publish). |

NativeAOT queda pendiente: `WebView.Avalonia` no trimea limpio todavia.
Cuando la dependencia de WebView se elimine (objetivo del pivot) se
habilita NativeAOT y bajamos otro escalon.

## Build / Run

```powershell
dotnet build SourceCode/PilotX.Desktop/PilotX.Desktop.csproj -c Debug
dotnet run --project SourceCode/PilotX.Desktop --no-build
```

El primer paint es el MapPanel nativo (placeholder con grilla + marca
PilotX). El HudPoller pega a `http://127.0.0.1:5180/api/aog/state` a
4 Hz. Si el AgpWebHost no esta corriendo, el HUD muestra "Sin conexion"
y el mapa queda en idle — sin WebView instanciado, footprint minimo.

## Args CLI

| Arg | Default | Que hace |
|-----|---------|----------|
| `--page=pages/camaras.html` | `/` | Pagina del wwwroot a abrir |
| `--url=http://otro/algo` | `http://127.0.0.1:5180/` | URL completa custom |
| `--mode=full` (default) | full | Cockpit con MapPanel nativo + HUD |
| `--mode=float` | full | Ventana chica con WebView lazy (widget HTML) |
| `--title="Camaras"` | "PilotX" | Titulo (solo modo float) |
| `--width=800 --height=480` | 640x400 | Tamano inicial en modo float |
| `--gl=on` | off | Habilita render OpenGL del mapa (Stage 1 migracion). Default off mientras se estabiliza. |

## HUD cockpit

Barra horizontal arriba del mapa, datos vivos a 4 Hz del
`GET /api/aog/state`:

```
[PX]  ● Trabajo activo   HDG 187°   ...   24.3 km/h   AREA 12.4 ha
```

## Mapa nativo (MapPanel)

`Views/MapPanel.cs` es un **wrapper Grid** que hostea una de dos surfaces
internas, decidida en construccion segun `App.UseGl`:

| Surface | Cuando se usa | Detalle |
|---------|---------------|---------|
| `MapSkiaSurface` (legacy) | `--gl=off` (default actual) | Render `DrawingContext` 2D. Validado en cabina. Grilla decorativa hardcoded, boundary + islands + sprite tractor. Sera retirado cuando GL llegue a paridad. |
| `MapGlSurface` (Stages 1+2+3) | `--gl=on` | Render OpenGL real via `Avalonia.OpenGL.Controls.OpenGlControlBase` + bindings `Silk.NET.OpenGL`. World grid en coords reales con step adaptivo, coverage triangulado con alpha blending, polyline de guidance (cian) entre coverage y boundary, boundary GL_LINE_LOOP, sprite tractor (triangulo relleno + borde). MVP ortho fit-to-bbox identico al Skia para validar paridad lado a lado. |

API publica de `MapPanel` es identica en ambos modos:
`OnSnapshot(HudSnapshot)`. Es lo unico que MainWindow y los tests
necesitan saber.

### Plan de migracion OpenGL (stages)

- **Stage 1 (hecho)**: surface GL real con clear + grid + boundary +
  tractor. Sin sections, sin guidance, sin tool. Toggle `--gl=on` opt-in.
- **Stage 2 (hecho)**: coverage / worked area triangulado. Consume el
  endpoint preexistente `GET /api/aog/coverage` (`FormGpsCoverageService`
  ya lo expone). `CoveragePoller` corre 1 Hz en background y solo se
  instancia con `--gl=on`. `MapGlSurface` rendera la capa con alpha
  blending (verde semitransparente) entre el grid y el boundary, usando
  un VBO dedicado + lista de `(start, count)` por strip. Revision-based
  cache: si la rev del snapshot no cambio, no se re-uploadea el VBO —
  solo el draw call vuelve a ejecutarse cada frame.
- **Stage 3 (hecho)**: guidance lines (AB / curves / contour). Endpoint
  nuevo `GET /api/aog/guidance/geometry` separado del control state
  (`/api/aog/guidance`) — la geometria cambia rara vez (solo al
  redefinirse la linea) asi que 1 Hz alcanza. `GuidanceGeometryClient` +
  `GuidanceGeometryPoller` corren solo con `--gl=on`. `MapGlSurface`
  rendera la polyline como `GL_LINE_STRIP` cian `#4DD8FF` entre coverage
  y boundary, con VBO dedicado + revision cache. AB se entrega como dos
  puntos ya extendidos por `CABLine.currentLinePtA/B` (que la propia
  FormGPS extiende ±abLength para su render legacy); Curve/Contour copian
  `curList`/`ctList` tal cual.
- **Stage 4a (hecho)**: barra del implemento + secciones. Endpoint
  `GET /api/aog/tool/geometry` (cadencia 4 Hz, sin revision-cache porque
  los puntos siguen al tractor cada frame). `ToolGeometryClient` +
  `ToolGeometryPoller` corren solo con `--gl=on`. `MapGlSurface` rendera
  un segmento `GL_LINES` por seccion entre coverage/guidance y boundary,
  coloreado por estado: gris (off), verde (`auto+mapping`), rojo
  (`auto+!mapping`), amarillo (manual on). Mapping de `btnStates`
  Off/Auto/On viene desde `FormGpsToolGeometryCalculator` como int 0/1/2.
- **Stage 4b (hecho)**: tram lines (wheel tracks) + outer/inner boundary
  tracks. Endpoint `GET /api/aog/tram` (cadencia 1 Hz con revision-cache —
  solo cambia al regenerar passes/ancho/displayMode). `TramGeometryClient`
  + `TramGeometryPoller` corren solo con `--gl=on`. `MapGlSurface` rendera
  las polylineas y los loops outer/inner en tono `#EDB8BC` semitransparente
  entre coverage y guidance. Respeta `displayMode` (`None`/`All`/
  `FillTracks`/`BoundaryTracks`) y lo emite desde `FormGpsTramCalculator`
  como string (eco del enum `TramMode` de PilotX).
- Stage 5: YouTurn paths + recorded paths.
- Stage 6: camera control (zoom rueda / pan drag / rotate touch).
- Stage 7: retirar `MapSkiaSurface` + `--gl` toggle.

## Mini-mapa cockpit

Overlay 240x180 esquina inf. izq., implementado en
`Controls/MiniMapView.cs`. Mismo principio que el mapa principal pero
en thumbnail. Util cuando el WebView esta abierto: el operario
mantiene referencia espacial. Boton `x` lo oculta; pin `[M]` lo
reabre.

## Toolbar inferior

| Boton      | Habilitado si           | Accion                  |
|------------|-------------------------|-------------------------|
| Engranaje  | hay GPS fix             | abre **SistemaPanel nativo** (sin WebView) |
| FieldTools | hay job iniciado        | abre **FieldDataPanel nativo** (sin WebView) |
| Tools      | siempre                 | MenuFlyout: Hub + productos X-* + utilidades |

Al abrir una pantalla web aparece el boton "`<-`" arriba a la izq.
para cerrar (libera el WebView y vuelve al mapa). `Esc` hace lo mismo
si hay WebView abierto, o cierra la ventana si estabas en el mapa.

## Layout en disco

```
SourceCode/PilotX.Desktop/
  PilotX.Desktop.csproj      net9.0 + tuning bajo consumo + CommunityToolkit.Mvvm
  Program.cs                 entrypoint + parser de args
  App.axaml(.cs)             Application + Theme cockpit
  MainWindow.axaml(.cs)      ventana borderless + MapPanel + WebView lazy
  Views/
    MapPanel.cs              wrapper Grid: hostea MapSkiaSurface o MapGlSurface
    MapSkiaSurface.cs        render Skia 2D legacy (DrawingContext)
    MapGlSurface.cs          render OpenGL real (OpenGlControlBase + Silk.NET) - Stage 1
    FieldDataPanel.axaml     overlay nativo de datos-lote (reemplazo del HTML)
    FieldDataPanel.axaml.cs  KPIs + savings + detalles, computados en proceso
    SistemaPanel.axaml       overlay nativo de sistema (brillo + power)
    SistemaPanel.axaml.cs    slider debounced + tap-to-confirm power actions
  Controls/
    MiniMapView.cs           mini-mapa overlay esquina
  Services/
    HudPoller.cs             HTTP polling /api/aog/state -> HUD + MapPanel + MiniMap
    SistemaClient.cs         cliente HTTP fino para /api/sistema/brillo y /power
    CoverageClient.cs        cliente HTTP de /api/aog/coverage (Stage 2 GL)
    CoveragePoller.cs        polling 1Hz dedicado, dispara solo con --gl=on
    GuidanceGeometryClient.cs cliente HTTP de /api/aog/guidance/geometry (Stage 3 GL)
    GuidanceGeometryPoller.cs polling 1Hz dedicado, dispara solo con --gl=on
  Theme/
    PilotXTheme.axaml        paleta cockpit
  app.manifest
  README.md
```

## Pantallas portadas a nativo (sin WebView)

| Origen HTML                      | Avalonia                       | Estado |
|----------------------------------|--------------------------------|--------|
| `pages/datos-lote.html`          | `Views/FieldDataPanel.axaml`   | OK (1er port) |
| `pages/sistema.html`             | `Views/SistemaPanel.axaml`     | OK (2do port — usa `SistemaClient`) |
| `pages/datos-gps.html`           | `Views/GpsDataPanel.axaml`     | OK (3er port — consume HUD, sin red propia) |
| `pages/stormx.html`              | `Views/StormXPanel.axaml`      | OK (4to port — `StormXClient` polling 1Hz solo cuando esta abierto) |
| `pages/flowx.html`               | `Views/FlowXPanel.axaml`       | Parcial (5to port — live-only: caudal/PWM/PID + ahorro insumo. Editor de productos/cables/PID sigue HTML via boton "Configurar") |
| `pages/sectionx.html`            | `Views/SectionXPanel.axaml`    | Parcial (6to port — live-only: chip bridge + grilla secciones. Editor de mapeo + test reles + debug MQTT siguen HTML via "Configurar") |
| `pages/quantix.html` (tab Monitor) | `Views/QuantiXPanel.axaml`   | Parcial (7mo port — tab Monitor only: dosis real/target + PWM + estado PID por motor. Motores CRUD, Shape, PID-tune, Calibracion, Prueba siguen HTML via "Configurar") |
| `pages/vistax.html` (tab Monitor) | `Views/VistaXPanel.axaml`     | Parcial (8vo port — tab Monitor only: SPM por surco + badges por estado + trenes con tubitos semilla/ferti y barras de otros sensores + grilla de nodos. Insumo & calibracion, Implemento, Nodos, Config siguen HTML via "Configurar") |
| `pages/corex-ecu.html` (tab Live) | `Views/CoreXEcuPanel.axaml`   | Parcial (9no port — tab Live only: cards IMU/WAS/GPS/CAN Keya/Autosteer/Sistema con polling 2Hz a `/api/corex-ecu/status`. Estado (checklist), Calibracion (motor manual + barrido PWM Keya) y Conexion (config Teensy) siguen HTML via "Configurar". Firmware Teensy intacto) |
| `pages/cabina-alarmas.html`       | `Views/CabinaAlarmasOverlay.axaml` | OK (10mo port — overlay top-most cabin-critical: nodos del implemento activo offline. Polling 2s a `/api/nodos/unified`, banner rojo pulsante con lista, beep 880Hz one-shot por UID nuevo, boton Silenciar 10 min. Autogestionado, no requiere navegacion. Solo activo en modo full) |
| `pages/hub.html`                  | `Views/HubPanel.axaml`         | OK (11vo port — home nativo: KPIs Velocidad/Rumbo/Dosis/Shape/Secciones/Posicion/Lote consumiendo `HudSnapshot` (sin red propia), 3 pills Job/Broker/Nodos, lista de hasta 6 nodos via `NodosClient` @3s, toggles QX/VX/FX via `OverlaysClient`. Las acciones rapidas abren overlays nativos (QuantiX/VistaX/Nodos)) |
| `pages/nodos.html`                | `Views/NodosPanel.axaml`       | Parcial (12vo port — live monitor: tabs Pendientes/Aceptados/Off-line/Ignorados con contador, tabla Estado/Alias-UID/Tipo/IP/FW/Ultima senal con badges SAFE + boot_reason, banner alarma offline-del-implemento, pills Broker + Implemento activo. Polling 3s a `/api/nodos/unified`. Las acciones de curado (aceptar/ignorar/renombrar/restaurar) y diag MQTT (wildcard + msg log) siguen HTML via "Configurar". Sin boton "Agregar" — doctrina `feedback_nodos_solo_auto`) |
| `pages/actualizar.html`           | `Views/ActualizarPanel.axaml`  | OK (13vo port — self-update PilotX via OrbitX OTA. 5 filas estado (instalada/disponible/tamano/SHA-256/ultima consulta), progress bar, pill de fase, changelog. 3 botones: Buscar / Descargar / Aplicar y reiniciar (gating segun phase: 0/Idle, 1/Checking, 2/UpdateAvailable, 3/Downloading, 4/ReadyToApply, 5/Applying, 9/Error). Polling 1s a `/api/pilotx/update/status` mientras esta visible. Cuando Aplicar dispara Updater.exe externo, PilotX se cierra y vuelve a abrir con la nueva version) |
| `pages/camaras.html` (tab Monitor) | `Views/CamarasPanel.axaml`    | Parcial (14vo port — tab Monitor only: hasta 4 camaras Hikvision en layouts 1x1/2x1/2x2, snapshot polling JPEG a `/api/camaras/{idx}/snapshot` @config.refrescoMs (default 1000ms), pill de status Broker/N activas, doble-click sobre una camara hace foco 1x1 / vuelta a 2x2. Tab Configuracion (CRUD camara: IP/usuario/clave/activa) sigue HTML via "Configurar" porque el formulario necesita teclado virtual aun no portado) |

## Pendiente

- Migrar render OpenGL del mapa de `FormGPS` a `OpenGlControlBase`
  (GL ES) en `MapPanel`. **Stages 1+2+3 hechos**: surface GL real con grid
  + coverage triangulado + polyline de guidance + boundary + tractor,
  opt-in via `--gl=on`. Pendiente stages 4..7
  (sections, guidance, tool, youturn, camera, cleanup).
- Portar pantallas HTML a controles Avalonia nativos (orden: Hub home
  primero, luego config de vehiculo/implemento, luego productos X-*).
  Cada pantalla portada = un dispatch del WebView menos.
- Cuando no quede UI web residente: borrar dependencia `WebView.Avalonia`
  + cliente HTTP del HudPoller + `EmbedIO/AgpWebHost`. Reemplazar HudPoller
  por IPC directo / shared memory con FormGPS mientras coexistan; despues
  por servicio embebido.
- Habilitar NativeAOT + Trimming (bloqueado por WebView.Avalonia hoy).
