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

## HUD cockpit

Barra horizontal arriba del mapa, datos vivos a 4 Hz del
`GET /api/aog/state`:

```
[PX]  ● Trabajo activo   HDG 187°   ...   24.3 km/h   AREA 12.4 ha
```

## Mapa nativo (MapPanel)

`Views/MapPanel.cs`: render Skia 2D a pantalla completa. Pinta grilla
de fondo, watermark "PilotX", contorno del lote activo (con drive-thru
islands) y sprite tractor en `(PivotEasting, PivotNorthing)` rotado
segun `Heading`. Cuando se migre a `OpenGlControlBase` (GL ES compat
Linux/Pi/Android) se reemplaza esta clase manteniendo la API
`OnSnapshot(HudSnapshot)`.

## Mini-mapa cockpit

Overlay 240x180 esquina inf. izq., implementado en
`Controls/MiniMapView.cs`. Mismo principio que el mapa principal pero
en thumbnail. Util cuando el WebView esta abierto: el operario
mantiene referencia espacial. Boton `x` lo oculta; pin `[M]` lo
reabre.

## Toolbar inferior

| Boton      | Habilitado si           | Accion                  |
|------------|-------------------------|-------------------------|
| Engranaje  | hay GPS fix             | abre WebView lazy en `pages/sistema.html` |
| FieldTools | hay job iniciado        | abre WebView lazy en `pages/datos-lote.html` |
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
    MapPanel.cs              vista principal nativa Skia (placeholder OpenGL)
  Controls/
    MiniMapView.cs           mini-mapa overlay esquina
  Services/
    HudPoller.cs             HTTP polling /api/aog/state -> HUD + MapPanel + MiniMap
  Theme/
    PilotXTheme.axaml        paleta cockpit
  app.manifest
  README.md
```

## Pendiente

- Migrar render OpenGL del mapa de `FormGPS` a `OpenGlControlBase`
  (GL ES) en `MapPanel`. Lo mas caro.
- Portar pantallas HTML a controles Avalonia nativos (orden: Hub home
  primero, luego config de vehiculo/implemento, luego productos X-*).
  Cada pantalla portada = un dispatch del WebView menos.
- Cuando no quede UI web residente: borrar dependencia `WebView.Avalonia`
  + cliente HTTP del HudPoller + `EmbedIO/AgpWebHost`. Reemplazar HudPoller
  por IPC directo / shared memory con FormGPS mientras coexistan; despues
  por servicio embebido.
- Habilitar NativeAOT + Trimming (bloqueado por WebView.Avalonia hoy).
