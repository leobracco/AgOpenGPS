# Stage 5 — Render GL de caminos (youturn + recorded) en PilotX.Desktop

Replicación 1:1 del patrón tram (Stage 4b), colapsado en UNA sola capa combinada
"paths" que entrega ambas polilíneas (youturn + recorded) por un único
endpoint/calculator/cliente/poller/capa-de-render.

## Fuente de datos confirmada

- **YouTurn**: `mf.yt` (`public CYouTurn yt;` en FormGPS.cs:317). Polilínea =
  `mf.yt.ytList` → `List<vec3>` (`CYouTurn.cs:48`). `vec3` tiene `.easting` /
  `.northing`. Activo cuando `ytList.Count >= 2`.
- **Recorded**: accesor confirmado `public CRecordedPath recPath;` (FormGPS.cs:357).
  Polilínea = `mf.recPath.recList` → `List<CRecPathPt>` (`CRecordedPath.cs:19`).
  Campos reales de `CRecPathPt` (`AgOpenGPS.Core/Classes/CRecPathPt.cs`, namespace
  `AgOpenGPS`): `easting`, `northing`, `heading`, `speed`, `autoBtnState`. Usamos
  `.easting` / `.northing`. Activo cuando `recList.Count >= 2`.

## Archivos creados

1. `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/IPathsGeometryCalculator.cs`
   — interface `IPathsGeometryCalculator` (método `PathsGeometrySnapshot GetGeometry()`).
2. `SourceCode/GPS/AgroParallel/Common/FormGpsPathsCalculator.cs` — adaptador
   (namespace `AgroParallel.Adapters`) que lee `mf.yt.ytList` + `mf.recPath.recList`.
   Defensivo (try/catch, listas vacías sin job). Revision bumpea cuando cambian
   `YouTurn.Count` / `Recorded.Count`.
3. `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/PathsController.cs`
   — `GET /api/aog/paths` → `{ ok, snapshot: { you_turn, recorded, revision } }`.
4. `SourceCode/PilotX.Desktop/Services/PathsGeometryClient.cs` — cliente HTTP
   (`api/aog/paths`, timeout 3s, case-insensitive JSON).
5. `SourceCode/PilotX.Desktop/Services/PathsGeometryPoller.cs` — poller 1000ms
   con revision-cache, Dispatcher.UIThread.Post.

## Archivos modificados

- `AgroParallel.Models/PilotCoreDtos.cs` — DTO `PathsGeometrySnapshot`
  (`List<FieldPoint> YouTurn`, `List<FieldPoint> Recorded`, `long Revision`).
- `AgroParallel.WebHost/AgpWebHost.cs` — campo `_paths`, param ctor
  `IPathsGeometryCalculator paths = null`, asignación, y registro
  `if (_paths != null) m.WithController(() => new PathsController(_paths));`
  (justo después del TramController).
- `AgroParallel.Shell/AgpWebHostBootstrap.cs` — param `paths = null` +
  `paths: paths` al ctor del host.
- `GPS/Forms/FormGPS.cs:649` — `var paths = new ...FormGpsPathsCalculator(this);`
  + `paths: paths` en la llamada a `AgpWebHostBootstrap.EnsureStarted`.
- `PilotX.Desktop/Views/MapGlSurface.cs` — campos `_pathsVbo`,
  `_pathsYouTurnStart/Count`, `_pathsRecordedStart/Count`, `_pathsRevisionUploaded`,
  `_pendingPaths`; colores `ColPathsYouTurn` {1.0, 0.62, 0.106, 1.0} naranja
  #FF9E1B y `ColPathsRecorded` {0.706, 0.470, 1.0, 1.0} violeta #B478FF; método
  `OnPaths(snap)`; init/dispose del VBO; llamada de render en el frame DESPUÉS de
  guidance; `UploadPaths` (VBO concatenado youturn+recorded, rangos start/count,
  STATIC_DRAW, revision-cache) y `DrawPaths` (dos `GL_LINE_STRIP`, un color por
  polilínea).
- `PilotX.Desktop/Views/MapPanel.cs` — delegación `OnPaths(snap)` → `_gl?.OnPaths`
  (GL-only; la surface Skia legacy no pinta paths).
- `PilotX.Desktop/MainWindow.axaml.cs` — campo `_pathsPoller`; dentro del bloque
  `if (App.UseGl)` creación de `PathsGeometryClient` + `PathsGeometryPoller`
  (1000ms) → `_mapHost?.OnPaths(snap)`, `.Start()` y `Closed += _pathsPoller?.Stop()`.

## Wiring DI

Mismo camino que tram: `FormGpsPathsCalculator` se instancia en FormGPS.cs:649,
viaja como arg opcional `paths:` por `AgpWebHostBootstrap.EnsureStarted` →
`new AgpWebHost(...)`, que registra `PathsController` en el módulo WebApi solo si
`_paths != null`. Cliente/poller solo se crean con `App.UseGl == true` (paths es
específico de GL, como coverage — a diferencia de tram/tool/guidance que corren
en ambos modos).

## Correctitud

- Cada polilínea se rendea como `GL_LINE_STRIP` (camino abierto, no `LineLoop`
  cerrado como el boundary del tram). Correcto: youturn y recorded son caminos,
  no polígonos.
- VBO único concatenado `[youturn...][recorded...]` con rangos en vértices;
  `DrawArrays(LineStrip, start, count)` por cada uno con su color. Idéntico al
  patrón `UploadTram`/`DrawTram`.
- Revision bumpea al cambiar las cuentas de puntos; el poller y `OnPaths` filtran
  por revision para saltar re-uploads. Snapshot completo en cada poll → si la
  revision no sube pero (raro) cambian puntos, el cliente igual tiene la geometría
  al siguiente cambio de cuenta.
- Guard de render: `if (_pathsYouTurnCount > 0 || _pathsRecordedCount > 0)`.
  Con `<2` puntos el calculator devuelve listas vacías y `UploadPaths` deja los
  counts en 0 → no se dibuja nada.
- Defensivo end-to-end: calculator try/catch, cliente/poller devuelven null y
  toleran fallos.

## Builds

- **net48** `dotnet build SourceCode/GPS/AgOpenGPS.csproj -c Debug`:
  Compilación correcta. 0 Advertencias, 0 Errores.
- **net9** `dotnet build SourceCode/PilotX.Desktop/PilotX.Desktop.csproj -c Debug`:
  Compilación correcta. 0 Advertencias, 0 Errores.

## Concerns

- No se pudo probar el render visual (requiere PilotX.Desktop con `--gl=on` + un
  lote con youturn activo / camino grabado). Verificación limitada a compilación
  limpia + fidelidad al patrón + razonamiento de correctitud.
