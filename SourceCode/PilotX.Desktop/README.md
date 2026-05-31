# PilotX.Desktop

Shell **Avalonia** oficial de PilotX. Reemplazo gradual del shell WinForms
actual (FormGPS + `AgroParallel.Shell` WebView2). Mientras dura la migracion,
ambos coexisten apuntando al mismo `AgpWebHost` en `127.0.0.1:5180`.

## Estrategia (strangler fig)

```
+-------------------+    +-------------------+
| FormGPS (WinForms)|    | PilotX.Desktop    |
| + AgroParallel    |    | (Avalonia 11.2.3) |
|   .Shell WebView2 |    |                   |
+--------+----------+    +----------+--------+
         |                          |
         |   127.0.0.1:5180         |
         v                          v
   +-----+--------------------------+-----+
   |        AgroParallel.WebHost          |
   |   (EmbedIO + wwwroot HTML/JS/CSS)    |
   +--------------------------------------+
```

- **WinForms** sigue siendo el productivo en el tractor (incluye el render
  OpenGL del mapa, que es lo mas caro de migrar y queda fuera del scope
  inicial).
- **PilotX.Desktop** arranca como ventana borderless cockpit con el Hub
  embebido, mas una toolbar inferior placeholder. Crece hasta paridad.

## Build / Run

```powershell
# Build aislado
dotnet build SourceCode/PilotX.Desktop/PilotX.Desktop.csproj -c Debug

# Run (requiere AgpWebHost corriendo en :5180; lo levanta el shell WinForms
# automaticamente, o se puede arrancar standalone via AgroParallel.Shell).
dotnet run --project SourceCode/PilotX.Desktop --no-build
```

Si el WebView muestra "No se puede acceder a este sitio web" -> el AgpWebHost
no esta corriendo en :5180. Es esperable cuando el shell WinForms no esta
levantado. Arrancalo y refresca (F5 dentro del WebView).

## Args CLI

| Arg | Default | Que hace |
|-----|---------|----------|
| `--page=pages/camaras.html` | `/` (Hub home) | Pagina del wwwroot a abrir |
| `--url=http://otro/algo` | `http://127.0.0.1:5180/` | URL completa custom |
| `--mode=full` (default) | full | Maximizado borderless (Hub) |
| `--mode=float` | full | Ventana chica con header arrastrable |
| `--title="Camaras"` | "PilotX" | Titulo (solo modo float) |
| `--width=800 --height=480` | 640x400 | Tamano inicial en modo float |

Ejemplos:

```powershell
# Hub completo (default)
dotnet run --project SourceCode/PilotX.Desktop

# Widget Camaras flotante encima del Hub
dotnet run --project SourceCode/PilotX.Desktop -- --mode=float --page=pages/camaras.html --title=Camaras --width=720 --height=480
```

Esc cierra; F12 abre DevTools del WebView2 subyacente.

## HUD cockpit

Modo full incluye una barra horizontal arriba del WebView con datos
vivos del estado de PilotX (lee `GET /api/aog/state` a 4 Hz):

```
[PX]  ● Trabajo activo   HDG 187°   ...   24.3 km/h   AREA 12.4 ha
```

- Chip estado: verde (trabajo + GPS fix), ámbar (sin trabajo), rojo
  (trabajo sin fix), gris (host caído).
- Speed en km/h (1 decimal).
- Heading en grados, normalizado 0..360.
- Área neta cubierta (sin solapamiento) en hectáreas.

Implementado en `Services/HudPoller.cs` (HttpClient + JSON, case-
insensitive para tolerar PascalCase/camelCase de EmbedIO/Swan).
En modo float el HUD no se muestra y el poller no arranca.

## Toolbar inferior

Los 3 botones de abajo navegan el WebView al wwwroot embebido y reflejan
el estado del PilotX (project_pilotx_toolbar_icons):

| Boton      | Habilitado si           | Navega a                |
|------------|-------------------------|-------------------------|
| Engranaje  | hay GPS fix             | `pages/sistema.html`    |
| FieldTools | hay job iniciado        | `pages/datos-lote.html` |
| Tools      | siempre                 | MenuFlyout con productos X-* + Hub |

El estado lo maneja `OnHudSnapshot` en `MainWindow.axaml.cs`: cada snapshot
del `/api/aog/state` actualiza `IsEnabled` de Engranaje (mira `Latitude`/
`Longitude` != 0) y FieldTools (`IsJobStarted`). Si el host cae, ambos se
deshabilitan; Tools sigue activo.

El menu de Tools incluye: Hub, Camaras, VistaX, QuantiX, SectionX, FlowX,
StormX, CoreX ECU, Nodos, Firmwares, OrbitX, Debug. Cada item navega a su
pagina del wwwroot manteniendo el origen del WebView actual (no hardcodea
127.0.0.1, asi si arrancaste con `--url=http://otra-pc:5180/` los botones
siguen apuntando al host correcto).

## Mini-mapa cockpit

En modo full hay un overlay 240x180 en la esquina inferior izquierda
con render nativo Avalonia (no OpenGL todavia):

- Outline del primer boundary (lote activo) + drive-thru islands grises.
- Sprite tractor (triangulo verde) en `(PivotEasting, PivotNorthing)`
  rotado segun `Heading` (rad, convencion compass: 0 = norte, +CW).
- Sin lote: cruz de referencia + hint "Sin lote activo".
- Sin datos: hint "Esperando datos...".

Datos consumidos del mismo `/api/aog/state` que el HUD (a 4 Hz). El
control cachea el bbox del boundary (key = primer punto + count) asi
que en cada tick solo recalcula la proyeccion, no el bbox.

Controles:
- `x` chiquito esquina sup. der. del mapa -> oculta el mini-mapa.
- Pin `[M]` aparece en su lugar para reabrirlo.
- El estado no persiste entre sesiones (arranca siempre visible).

Implementado en `Controls/MiniMapView.cs` (`override Render(DrawingContext)`).
Es la base para el render del mapa completo: cuando se migre el OpenGL
de `FormGPS` se reemplaza este Control por uno Silk.NET / Avalonia.OpenGL
manteniendo la misma API `OnSnapshot(HudSnapshot)`.

## Pendiente (no esta cubierto en este esqueleto)

- Migrar el render OpenGL del mapa principal (a pantalla completa) de
  `FormGPS` a Avalonia (`Silk.NET` o `Avalonia.OpenGL`). Lo mas caro.
  El mini-mapa actual es 2D-only y no escala a la vista principal.
- Mover los servicios del shell WinForms (`AogStateProvider`, `lotes`,
  `vehicleTool`, etc.) detras de interfaces que ambos shells puedan
  hostear sin tocar el codigo del Hub HTML.

## Cold-start

Al arrancar, el shell mide el tiempo desde `Main()` hasta el primer
`NavigationCompleted` del WebView y lo imprime a stdout:

```
[PilotX.Desktop] Cold-start (Main -> NavigationCompleted): 412 ms  url=http://127.0.0.1:5180/
```

Objetivo: <500 ms al primer paint, comparable al shell WinForms+WebView2.

## Layout en disco

```
SourceCode/PilotX.Desktop/
  PilotX.Desktop.csproj      net9.0-windows + Avalonia 11.2.3 + WebView.Avalonia
  Program.cs                 entrypoint + parser de args
  App.axaml(.cs)             Application + ResourceDictionary cockpit
  MainWindow.axaml(.cs)      ventana borderless + HUD + WebView + toolbar
  Services/
    HudPoller.cs             HTTP polling de /api/aog/state -> HUD live
  Theme/
    PilotXTheme.axaml        paleta cockpit (verde marca + grises cabina)
  app.manifest               DPI per-monitor + supportedOS Win10/11
  README.md                  este archivo
```
