# NOTA para la sesión de bloques 6/9 (rama codex/android-formgps-render)

**⚠️ NO partir de `3fcc32db` — quedó vieja.** Esta sesión (PC del taller,
2026-07-19) empujó 18 commits más a `codex/pilotx-ui-new`
(`52cd48f1..e764949d`) que pisan de lleno los bloques 6 y 9. Recrear la rama
desde el HEAD nuevo de `codex/pilotx-ui-new` antes de tocar nada.

## Qué ya está hecho de TU misión (no rehacer)

**Bloque 9 — ya está al ~30%:**
- `PGN.Designer.cs`: las 11 clases `CPGN_*` ya viven en
  `AgOpenGPS.Core/Protocols/PgnDefinitions.cs` (8cc64394).
- `UDPComm.Designer.cs`: el switch entero de PGNs entrantes ya vive en
  `Core/Protocols/PgnReceiver.cs` + `IPgnReceiveHost` (580a3d91) y quedó
  **validado en runtime** con tráfico 0xD6 real. El form quedó en
  sockets + WndProc + wrapper (957 líneas).
- Auditoría hecha (está en la matriz): `Position.designer.cs` (1616 ln,
  el GPS loop) tiene **0 llamadas GL y ~10 toques UI** (lblSpeed,
  TimedMessageBox, btnAutoSteer.PerformClick) — extraíble con el patrón
  I*Host. `Sections.Designer.cs` (962 ln) tampoco toca GL pero es 90% UI
  de botones: lo extraíble son SectionSetPosition/CalcWidths/CalcMulti/
  BuildMachineByte y la decisión on/off que vive en
  `CalculateSectionLookAhead` (Position.designer).
- Los partials adapter ya son **18** (no 11): se sumaron PgnReceiveHost y
  los tres de assets de dibujo (ver abajo).

**Bloque 6 — el terreno quedó preparado:**
- TODO el draw GL que vivía embebido en las clases de guiado (CABLine,
  CABCurve, CYouTurn, CTram, CRecordedPath, CContour, CFence/CBoundary,
  CTool, CVehicle, Camera.SetLookAt, WorldGrid) ya NO está en Classes/:
  vive concentrado en `DrawLib/GuidanceDrawExtensions.cs` (extensiones),
  `Visuals/WorldGridVisual.cs` y `DrawLib/IDrawAssetHosts.cs`
  (ITextFontHost/IToolTexturesHost/IVehicleTexturesHost). El inventario
  GL para portar a GL ES/Skia es: `DrawLib/`, `Visuals/`, `Drawing/` y
  `GPS/Forms/OpenGL.Designer.cs`. Nada más.
- `AgOpenGPS.Core` y `AgLibrary` multi-targetean `net48;netstandard2.0`
  (netstandard excluye DrawLib/Visuals/Drawing). **Regla nueva: cada
  extracción tiene que dejar compilando AMBOS targets** —
  `dotnet build SourceCode/AgOpenGPS.Core/AgOpenGPS.Core.csproj -c Release`
  compila los dos.

**Además existe `SourceCode/PilotX.Android/`** (shell Fase 1, APK compila):
los servicios que extraigan de FormGPS son los que reemplazan los stubs de
`Fase1Stubs.cs` en la Fase 2.

## Convenciones que venimos usando (replicar)

- Draw a extensiones en DrawLib; `mf` de la clase pasa a `internal` para
  que la extensión lea el host sin inflar firmas (patrón en todos los
  commits `refactor(core)` de hoy).
- Lo que el host no expone entra por parámetro desde el caller GL
  (lineWidth, camSetDistance) — ver GuidanceDrawExtensions.
- IDE0055 (formato) corre como error en Release sobre archivos tocados:
  `dotnet format whitespace --include <archivos>` antes de commitear.
- Comentarios/commits en castellano rioplatense; branding PilotX/CoreX en
  todo texto visible (barrido hecho en 9f9ae12b — no reintroducir AOG/AgIO).

## Reparto para no pisarnos

- Esa rama: `Position.designer.cs`, `Sections.Designer.cs`,
  `OpenGL.Designer.cs`/render, `FormGPS.cs` (extracciones).
- Esta sesión (taller): NO toca `GPS/Forms/` mientras tanto — sigue en
  visual/Hub/Android shell. Coordinación por esta nota y la bitácora
  `COORDINACION-UI.md`.
