# Cabecera PilotX — MVP "Build Around" — Diseño

**Fecha:** 2026-07-17
**Autor:** Claude + Leonardo
**Estado:** aprobado (pendiente review del spec escrito)

## Objetivo

Reemplazar la primera mitad del WinForms `FormHeadLine` (creación de cabecera por
"Build Around": offset del contorno hacia adentro N metros) por un widget HTML
servido por EmbedIO en `:5180`, siguiendo el patrón de migración establecido. El
reshape manual (picking A/B, extend/shrink, clip, undo) queda explícitamente fuera
de alcance: es la **fase 2**.

## Contexto

`FormHeadLine` (`SourceCode/GPS/Forms/Guidance/FormHeadLine.cs`, 1023 líneas) es un
editor con viewport OpenGL propio. Tiene dos flujos:

- **A — Build Around** (`btnBndLoop_Click`, líneas 714–806): ~90% del uso real.
  Offsetea todo el `fenceLine` hacia adentro por una distancia, arma la cabecera
  cerrada `hdLine`, la persiste. Si distancia==0, copia fence→hdLine.
- **B — Reshape manual** (mouse picking A/B, `SetLineDistance`, `btnSlice`/clip,
  extend/shrink, undo): edición local, la parte CAD pesada.

Este spec cubre **solo el flujo A** (+ Reset, toggle secciones, Apagar, guardar).

### Arquitectura acordada (cliente fino)

El widget HTML dibuja y comanda; la geometría corre en C# reusando el algoritmo de
offset que ya existe en el form. NO se reescribe geometría en JavaScript. El modelo
(`mf.bnd.bndList[0].fenceLine` / `.hdLine`, en E/N metros, con heading en radianes)
es la fuente de verdad.

### Datos y verdades del código (verificadas)

- `mf.bnd.bndList[0].fenceLine` : `List<vec3>` (easting, northing, heading rad). Es
  el contorno externo. Tiene headings poblados cuando hay contorno.
- `mf.bnd.bndList[0].hdLine` : `List<vec3>` — la cabecera. Es lo que se guarda.
- `mf.bnd.isSectionControlledByHeadland` : bool. Espejado en
  `Properties.Settings.Default.setHeadland_isSectionControlled`.
- `mf.bnd.isHeadlandOn` : bool. Lo setea `GetHeadland()` tras cerrar el form como
  `bndList.Count>0 && hdLine.Count>0`.
- `mf.hdl.desList` : `List<vec3>` temporal usado por el offset.
- `mf.ftOrMtoM` : factor display→metros. `mf.m2FtOrM` : metros→display.
  `mf.unitsFtM` : "ft"|"m". `mf.tool.width`, `mf.tool.overlap` : ancho útil.
- `mf.FileSaveHeadland()` : persiste la cabecera.
- Algoritmo de offset (portar idéntico desde `btnBndLoop_Click`):
  para cada punto del fence, `pt.easting = fence[i].easting - Sin(PIBy2+fence[i].heading)*moveDist`,
  `pt.northing = fence[i].northing - Cos(PIBy2+fence[i].heading)*moveDist`;
  descartar el punto si cae a menos de `moveDist*sqrt(0.999)` de CUALQUIER punto del
  fence (rechazo de esquinas auto-intersectadas); dedup si dist al último > 1 m;
  cerrar el anillo (agregar copia del primero); si `cnt>3`:
  `MakePointMinimumSpacing(ref desList, 1.2)` + `CalculateHeadings(ref desList)` y
  volcar a `hdLine`. (`CABCurve.MakePointMinimumSpacing` y `CABCurve.CalculateHeadings`
  son estáticos y ya existen.)

### Patrón backend establecido (verificado)

- Controllers heredan `AgpControllerBase`, usan `WriteJsonAsync` (snake_case vía
  AgpJson) y `ReadJsonBodyAsync<T>()`.
- La mutación de FormGPS va por un servicio-interfaz (`Abstractions`) cuya
  implementación (adapter) vive en el proyecto GPS con acceso a `FormGPS` y
  marshalea al hilo UI (patrón `IGuidanceCalculator` → adapter → `mf.*`).
- Registro en `AgpWebHost.cs`: campo `_headlandEdit`, `WithController(() => new
  HeadlandController(_headlandEdit))` guardado por null. El adapter se instancia
  en el arranque GPS (donde se construye `AgpWebHost`) y se pasa por ctor.

## Alcance

### Incluido (MVP)
1. Preview read-only del contorno + cabecera resultante en canvas 2D (pan/zoom vista).
2. Construir cabecera por distancia (Build Around) — persiste en el acto.
3. Reset (cabecera = copia del contorno).
4. Toggle "secciones controladas en cabecera".
5. Apagar cabecera (limpia hdLine, isHeadlandOn=false, guarda).
6. "Usar ancho de herramienta" (autocompleta distancia con el ancho útil).
7. Guard: sin contorno → banner + Construir deshabilitado.
8. Repunte del launcher WinForms al widget.

### Excluido (fase 2)
- Reshape manual: picking A/B, curva/recta, extend/shrink, clip line, undo.
- Zoom "tap to zoom" del form original (lo reemplaza wheel+drag del canvas).
- Borrado de `FormHeadLine.{cs,Designer.cs,resx}` (se mantiene como base fase 2).

## Componentes / File structure

### Backend

- **Crear** `SourceCode/AgroParallel/Core/AgroParallel.Models/HeadlandEditDtos.cs`
  - `HeadlandEditStateDto { bool HasField; bool HasBoundary; bool IsHeadlandOn;
    bool IsSectionControlled; string Units; double ToolWidthM; double[][] Fence;
    double[][] Headland; }`
  - `HeadlandEditResultDto { bool Ok; bool IsHeadlandOn; double[][] Headland;
    string Error; }`
  - Nombres de propiedad C# PascalCase; el wire sale snake_case por AgpJson
    (has_field, has_boundary, is_headland_on, is_section_controlled, units,
    tool_width_m, fence, headland, ok, error).

- **Crear** `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/IHeadlandEditService.cs`
  - `HeadlandEditStateDto GetState();`
  - `HeadlandEditResultDto BuildAround(double distanceDisplay);`
  - `HeadlandEditResultDto Reset();`
  - `HeadlandEditResultDto TurnOff();`
  - `bool SetSectionControlled(bool on);`

- **Crear** `SourceCode/GPS/Forms/AgroParallel/HeadlandEdit/FormGPS.HeadlandEdit.cs`
  (partial de FormGPS, namespace `AgOpenGPS`) — la geometría idéntica al form:
  - `internal double[][] HeadlandEdit_GetFenceEN()` / `HeadlandEdit_GetHeadlandEN()`
  - `internal bool HeadlandEdit_BuildAround(double distDisplay)` — offset + save.
  - `internal void HeadlandEdit_Reset()` — hdLine = fence copy + save.
  - `internal void HeadlandEdit_TurnOff()` — clear + isHeadlandOn=false + save.
  - `internal void HeadlandEdit_SetSectionControlled(bool on)` — set + Settings.Save.
  - Cada operación que modifica hdLine setea
    `bnd.isHeadlandOn = bnd.bndList.Count>0 && bnd.bndList[0].hdLine.Count>0`.

- **Crear** `SourceCode/GPS/AgroParallel/Adapters/HeadlandEditAdapter.cs`
  (namespace `AgroParallel.Adapters`, `IHeadlandEditService`). Tiene `FormGPS _form`,
  marshalea con `_form.Invoke` / `InvokeRequired`, arma los DTOs desde los helpers
  del partial, convierte distancia con `_form` (usa el partial que ya aplica
  `ftOrMtoM`). Todo dentro de try/catch → en error devuelve DTO con `Ok=false`.

- **Crear** `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/HeadlandController.cs`
  - `GET  /headland/state`             → `GetState()`
  - `POST /headland/build`   `{distance}` → `BuildAround(distance)`
  - `POST /headland/reset`             → `Reset()`
  - `POST /headland/off`               → `TurnOff()`
  - `POST /headland/section-controlled` `{on}` → `SetSectionControlled(on)`
  - Todos `WriteJsonAsync`. Si `_svc==null` → `{ok:false, error:"service-unavailable"}`.

- **Modificar** `SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs`
  - Campo `private readonly IHeadlandEditService _headlandEdit;`
  - Nuevo parámetro opcional en el ctor (al final, `IHeadlandEditService headlandEdit = null`)
    y asignación. Registro `if (_headlandEdit != null) m.WithController(() => new
    HeadlandController(_headlandEdit));`
  - **Modificar** el sitio de instanciación de `AgpWebHost` en el arranque GPS
    (`AgpWebHostBootstrap.cs` u homólogo): crear `new HeadlandEditAdapter(form)` y
    pasarlo. (El plan fija el archivo/línea exactos.)

### Frontend

- **Crear** `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/cabecera.html`
  - `<aside class="sidebar" data-active="hub">`, page-head con título "Cabecera".
  - `<canvas id="cvPreview">` (preview contorno + cabecera).
  - Panel de controles: input distancia + unidad, botón "Usar ancho de herramienta",
    "Construir cabecera", "Reset", toggle "Secciones controladas", "Apagar cabecera",
    pill `#statusText`, banner `#warnBox` (sin contorno).
  - Scripts: `keyboard.js`, `sidebar.js`, `cabecera.js`.

- **Crear** `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/cabecera.js`
  - `loadState()` → `GET /api/headland/state`; dibuja; setea unidad/ancho; si
    `!has_boundary` muestra banner y deshabilita Construir.
  - Canvas: transform fit-to-bounds sobre `fence` (fallback `headland`), y hacia
    arriba; wheel = zoom sobre el cursor; drag = pan (solo vista).
  - Construir → `POST /headland/build {distance}` (valor en unidades display) →
    redibuja con `headland` de la respuesta.
  - Reset / Apagar / toggle → sus POST; redibujan con la respuesta.
  - Estado de conexión en la pill; errores a `console.warn` + pill.

### WinForms launcher

- **Modificar** `SourceCode/GPS/Forms/Controls.Designer.cs`
  `headlandToolStripMenuItem_Click`: mantener guard `bnd.bndList.Count == 0`
  (TimedMessageBox `gsNoBoundary`/`gsCreateABoundaryFirst`); si hay contorno:
  ```csharp
  if (!LaunchAvaloniaWidget("pages/cabecera.html", "float", "Cabecera", 1000, 720))
  { OpenAgroParallelHub("pages/cabecera.html"); }
  this.Activate();
  ```
  Ya no llama `GetHeadland()`. Verificar `headlandBuildToolStripMenuItem_Click`
  (línea ~744) y repuntar o dejar según lo que haga (el plan lo fija).

## Data flow

1. Widget abre → `GET /headland/state` → adapter (hilo UI) lee `mf.bnd.bndList[0]`
   fence/hdLine → DTO E/N → canvas dibuja.
2. Operario ingresa distancia → "Construir" → `POST /headland/build {distance}` →
   adapter marshalea → partial corre offset idéntico al form + `FileSaveHeadland()`
   → devuelve hdLine E/N → canvas redibuja.
3. Reset / Apagar / toggle idem, cada uno devuelve el estado resultante.

## Errores / edge cases

- Sin lote / sin contorno (`bndList.Count==0`): `has_field`/`has_boundary=false`;
  el widget muestra banner y deshabilita Construir. El launcher además ya frena con
  TimedMessageBox antes de abrir.
- Distancia 0 en Construir: copia fence→hdLine (igual que el form).
- Offset que colapsa (desList vacío por distancia mayor al ancho del lote): la
  operación no rompe; devuelve hdLine sin cambios y `ok=false` con error explicativo.
- `_svc==null` (adapter no inyectado): controller responde service-unavailable; el
  widget cae al banner de "sin conexión".
- Marshaling: toda mutación corre en hilo UI vía `_form.Invoke`.

## Testing / verificación

1. `build.ps1` con 0 errores (matar PilotX.exe/CoreX.exe antes).
2. Lanzar `./Build/PilotX.exe`; sin lote:
   - `GET /api/headland/state` → `has_boundary:false`.
   - `POST /api/headland/build {distance:5}` → `ok:false` (guard).
   - `pages/cabecera.html` y `js/cabecera.js` → 200.
3. Playwright: navegar + screenshot → canvas + controles + banner "creá contorno".
4. Corrección geométrica del offset: garantizada por reusar el algoritmo exacto;
   verificación visual con lote real la hace el usuario (o follow-up con simulador).
5. Limpiar `.playwright-mcp` + png, matar procesos, commit de archivos de migración.

## No incluido / decisiones abiertas confirmadas

- `FormHeadLine.{cs,Designer.cs,resx}` se **mantiene** (sin referenciar) hasta la
  fase 2 (reshape). Confirmado con el usuario.
- Nombres `cabecera.html` / `cabecera.js`. Confirmado.
- Endpoints/DTOs arriba. Confirmado.
