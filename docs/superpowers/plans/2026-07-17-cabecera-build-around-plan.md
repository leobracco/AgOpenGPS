# Cabecera "Build Around" — Plan de implementación

> **Para workers agénticos:** SUB-SKILL REQUERIDA: usá superpowers:subagent-driven-development
> (recomendado) o superpowers:executing-plans para implementar este plan tarea por tarea.
> Los pasos usan checkbox (`- [ ]`) para tracking.

**Goal:** Reemplazar el flujo "Build Around" de `FormHeadLine` por un widget HTML
(`cabecera.html`) servido por EmbedIO en `:5180`, con la geometría de offset corriendo
en C# (reuso del algoritmo de `btnBndLoop_Click`).

**Architecture:** Cliente fino. El canvas HTML dibuja contorno + cabecera y despacha
comandos REST; toda la geometría vive en un partial de `FormGPS`, expuesta por
`IHeadlandEditService` → adapter (marshaling al hilo UI) → `HeadlandController`
(`AgpControllerBase`, wire snake_case por AgpJson). El modelo E/N (metros) es la
fuente de verdad; nada de geometría en JavaScript.

**Tech Stack:** C# .NET 4.8 (GPS/WinForms), netstandard2.0 (Models/Services.Abstractions),
EmbedIO + AgpControllerBase (WebHost), HTML5 Canvas + fetch (WebUI).

**Referencia base:** `docs/superpowers/specs/2026-07-17-cabecera-build-around-design.md`

---

## File structure

**Crear:**
- `SourceCode/AgroParallel/Core/AgroParallel.Models/HeadlandEditDtos.cs`
- `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/IHeadlandEditService.cs`
- `SourceCode/GPS/Forms/AgroParallel/FormGPS.HeadlandEdit.cs`
- `SourceCode/GPS/AgroParallel/Common/FormGpsHeadlandEditService.cs`
- `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/HeadlandController.cs`
- `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/cabecera.html`
- `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/cabecera.js`

**Modificar:**
- `SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs` (campo + ctor param + registro)
- `SourceCode/AgroParallel/Web/AgroParallel.Shell/AgpWebHostBootstrap.cs` (param + pasaje)
- `SourceCode/GPS/Forms/FormGPS.cs` (crear adapter + pasarlo, línea ~615/639)
- `SourceCode/GPS/Forms/Controls.Designer.cs` (`headlandToolStripMenuItem_Click`, línea 734)

**Nota de build:** no hay proyecto de tests para forms/adapters GPS. La verificación
de estas migraciones es `build.ps1` (0 errores) + curl de endpoints + Playwright
screenshot, igual que SmoothAB. Cada tarea de código termina con un build limpio
como gate; la verificación runtime completa va en la última tarea.

**Comando de build (git-bash):**
```bash
cd "G:/agroparallel/productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
taskkill //IM PilotX.exe //F 2>/dev/null; taskkill //IM CoreX.exe //F 2>/dev/null
powershell -ExecutionPolicy Bypass -File build.ps1
```

---

## Task 1: DTOs de estado y resultado

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Models/HeadlandEditDtos.cs`

- [x] **Step 1: Escribir el archivo de DTOs**

```csharp
// ============================================================================
// HeadlandEditDtos.cs — POCOs para el editor de cabecera HTML (cabecera.html).
//
//   HeadlandEditStateDto   → GET  /api/headland/state  (estado + geometría)
//   HeadlandEditResultDto  → respuesta de build/reset/off (ok + hdLine nueva)
//
// Geometría en E/N metros: cada punto es un double[2] = { easting, northing }.
// El wire sale snake_case por AgpJson; los nombres C# van PascalCase.
// ============================================================================

namespace AgroParallel.Models
{
    public sealed class HeadlandEditStateDto
    {
        public bool HasField { get; set; }
        public bool HasBoundary { get; set; }
        public bool IsHeadlandOn { get; set; }
        public bool IsSectionControlled { get; set; }
        public string Units { get; set; } = "m";
        public double ToolWidthM { get; set; }
        public double[][] Fence { get; set; } = new double[0][];
        public double[][] Headland { get; set; } = new double[0][];
    }

    public sealed class HeadlandEditResultDto
    {
        public bool Ok { get; set; }
        public bool IsHeadlandOn { get; set; }
        public double[][] Headland { get; set; } = new double[0][];
        public string Error { get; set; }
    }
}
```

- [x] **Step 2: Verificar que el proyecto Models compila**

Este proyecto es netstandard2.0 sin dependencias nuevas. Se compila junto al resto en
Step del build de la Task 6; no hay build aislado. Continuar.

---

## Task 2: Interfaz del servicio

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/IHeadlandEditService.cs`

- [x] **Step 1: Escribir la interfaz**

```csharp
// ============================================================================
// IHeadlandEditService.cs — contrato del editor de cabecera (flujo Build Around).
// Implementado por FormGpsHeadlandEditService (proyecto GPS) que marshalea al
// hilo UI de PilotX. Todas las distancias entran en UNIDADES DISPLAY (ft o m
// según mf.unitsFtM); el adapter/partial las convierte a metros.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IHeadlandEditService
    {
        // Estado actual: contorno + cabecera + flags. Nunca tira: si no hay lote
        // devuelve HasField=false con listas vacías.
        HeadlandEditStateDto GetState();

        // Offset del contorno hacia adentro por distanceDisplay (unidades display).
        // distanceDisplay==0 copia contorno→cabecera. Persiste con FileSaveHeadland.
        HeadlandEditResultDto BuildAround(double distanceDisplay);

        // Cabecera = copia exacta del contorno. Persiste.
        HeadlandEditResultDto Reset();

        // Limpia la cabecera, isHeadlandOn=false. Persiste.
        HeadlandEditResultDto TurnOff();

        // Setea "secciones controladas por cabecera" (espeja Settings). Devuelve el
        // valor efectivo.
        bool SetSectionControlled(bool on);
    }
}
```

- [x] **Step 2: Sin build aislado** — compila en la Task 6.

---

## Task 3: Partial de FormGPS con la geometría

**Files:**
- Create: `SourceCode/GPS/Forms/AgroParallel/FormGPS.HeadlandEdit.cs`

Este partial vive dentro de `partial class FormGPS`, así que accede directo a
`bnd`, `hdl`, `tool`, `ftOrMtoM`, `m2FtOrM`, `unitsFtM`, `FileSaveHeadland()`,
`glm`, `vec3`, `CABCurve` (los mismos miembros que `FormHeadLine` usa vía `mf.`).

- [x] **Step 1: Escribir el partial**

```csharp
// ============================================================================
// FormGPS.HeadlandEdit.cs — geometría del editor de cabecera HTML (cabecera.html).
// Reemplaza el flujo "Build Around" de FormHeadLine. El algoritmo de offset es
// idéntico a FormHeadLine.btnBndLoop_Click; acá vive dentro de FormGPS para que
// el adapter IHeadlandEditService lo invoque sin abrir el form WinForms.
// Todos los métodos asumen que corren en el hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // true si hay contorno externo con puntos.
        internal bool HeadlandEdit_HasBoundary()
        {
            return bnd != null && bnd.bndList.Count > 0
                   && bnd.bndList[0].fenceLine != null
                   && bnd.bndList[0].fenceLine.Count > 0;
        }

        internal double[][] HeadlandEdit_FenceEN()
        {
            if (!HeadlandEdit_HasBoundary()) return new double[0][];
            var src = bnd.bndList[0].fenceLine;
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        internal double[][] HeadlandEdit_HeadlandEN()
        {
            if (!HeadlandEdit_HasBoundary()) return new double[0][];
            var src = bnd.bndList[0].hdLine;
            if (src == null) return new double[0][];
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        internal bool HeadlandEdit_IsHeadlandOn()
        {
            return bnd != null && bnd.bndList.Count > 0
                   && bnd.bndList[0].hdLine != null
                   && bnd.bndList[0].hdLine.Count > 0
                   && bnd.isHeadlandOn;
        }

        // Ancho útil de la herramienta en metros (para "usar ancho de herramienta").
        internal double HeadlandEdit_ToolWidthM()
        {
            return tool != null ? tool.width : 0.0;
        }

        internal string HeadlandEdit_Units()
        {
            return unitsFtM; // "ft" | "m"
        }

        internal bool HeadlandEdit_IsSectionControlled()
        {
            return bnd != null && bnd.isSectionControlledByHeadland;
        }

        // Recalcula isHeadlandOn tras cualquier mutación de hdLine.
        private void HeadlandEdit_RefreshOnFlag()
        {
            bnd.isHeadlandOn = bnd.bndList.Count > 0
                               && bnd.bndList[0].hdLine != null
                               && bnd.bndList[0].hdLine.Count > 0;
        }

        // Offset idéntico a FormHeadLine.btnBndLoop_Click. distanceDisplay en
        // unidades display; ==0 copia contorno→cabecera. Devuelve false si el
        // offset colapsa (sin puntos válidos) — en ese caso NO toca hdLine.
        internal bool HeadlandEdit_BuildAround(double distanceDisplay)
        {
            if (!HeadlandEdit_HasBoundary()) return false;

            int ptCount = bnd.bndList[0].fenceLine.Count;

            if (distanceDisplay == 0)
            {
                hdl.desList.Clear();
                bnd.bndList[0].hdLine?.Clear();
                for (int i = 0; i < ptCount; i++)
                    bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            }
            else
            {
                hdl.desList?.Clear();
                vec3 pt3 = new vec3();

                double moveDist = distanceDisplay * ftOrMtoM;
                double distSq = (moveDist) * (moveDist) * 0.999;

                for (int i = 0; i < ptCount; i++)
                {
                    pt3.easting = bnd.bndList[0].fenceLine[i].easting -
                        (Math.Sin(glm.PIBy2 + bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.northing = bnd.bndList[0].fenceLine[i].northing -
                        (Math.Cos(glm.PIBy2 + bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.heading = bnd.bndList[0].fenceLine[i].heading;

                    bool Add = true;
                    for (int j = 0; j < ptCount; j++)
                    {
                        double check = glm.DistanceSquared(pt3.northing, pt3.easting,
                                            bnd.bndList[0].fenceLine[j].northing, bnd.bndList[0].fenceLine[j].easting);
                        if (check < distSq) { Add = false; break; }
                    }

                    if (Add)
                    {
                        if (hdl.desList.Count > 0)
                        {
                            double dist = ((pt3.easting - hdl.desList[hdl.desList.Count - 1].easting) * (pt3.easting - hdl.desList[hdl.desList.Count - 1].easting))
                                + ((pt3.northing - hdl.desList[hdl.desList.Count - 1].northing) * (pt3.northing - hdl.desList[hdl.desList.Count - 1].northing));
                            if (dist > 1)
                                hdl.desList.Add(pt3);
                        }
                        else hdl.desList.Add(pt3);
                    }
                }

                if (hdl.desList.Count == 0)
                    return false;

                pt3 = new vec3(hdl.desList[0]);
                hdl.desList.Add(pt3);

                int cnt = hdl.desList.Count;
                if (cnt > 3)
                {
                    pt3 = new vec3(hdl.desList[0]);
                    hdl.desList.Add(pt3);

                    CABCurve.MakePointMinimumSpacing(ref hdl.desList, 1.2);
                    CABCurve.CalculateHeadings(ref hdl.desList);

                    bnd.bndList[0].hdLine.Clear();
                    foreach (vec3 item in hdl.desList)
                        bnd.bndList[0].hdLine.Add(item);
                }
            }

            FileSaveHeadland();
            HeadlandEdit_RefreshOnFlag();
            return true;
        }

        // Cabecera = copia del contorno.
        internal void HeadlandEdit_Reset()
        {
            if (!HeadlandEdit_HasBoundary()) return;
            hdl.desList.Clear();
            bnd.bndList[0].hdLine?.Clear();
            int ptCount = bnd.bndList[0].fenceLine.Count;
            for (int i = 0; i < ptCount; i++)
                bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            FileSaveHeadland();
            HeadlandEdit_RefreshOnFlag();
        }

        // Apagar cabecera: limpiar hdLine + persistir.
        internal void HeadlandEdit_TurnOff()
        {
            if (bnd == null || bnd.bndList.Count == 0) return;
            bnd.bndList[0].hdLine?.Clear();
            FileSaveHeadland();
            bnd.isHeadlandOn = false;
        }

        internal void HeadlandEdit_SetSectionControlled(bool on)
        {
            if (bnd == null) return;
            bnd.isSectionControlledByHeadland = on;
            Properties.Settings.Default.setHeadland_isSectionControlled = on;
            Properties.Settings.Default.Save();
        }
    }
}
```

- [x] **Step 2: Verificar acceso a miembros**

Confirmar (grep en `SourceCode/GPS/`) que existen y son accesibles desde FormGPS:
`bnd.bndList[0].fenceLine/.hdLine`, `bnd.isHeadlandOn`, `bnd.isSectionControlledByHeadland`,
`hdl.desList`, `tool.width`, `ftOrMtoM`, `unitsFtM`, `FileSaveHeadland()`,
`glm.PIBy2`, `glm.DistanceSquared`, `CABCurve.MakePointMinimumSpacing`,
`CABCurve.CalculateHeadings`, `Properties.Settings.Default.setHeadland_isSectionControlled`.

Run:
```bash
cd "G:/agroparallel/productos/CentriX-Spark/Software/App_PC/AgOpenGPS/SourceCode/GPS"
grep -rn "setHeadland_isSectionControlled\|isSectionControlledByHeadland" . | head
```
Expected: aparecen usados (el form los usa). Si `m2FtOrM` o algún nombre difiere,
ajustar acá antes del build.

---

## Task 4: Adapter IHeadlandEditService

**Files:**
- Create: `SourceCode/GPS/AgroParallel/Common/FormGpsHeadlandEditService.cs`

- [x] **Step 1: Escribir el adapter**

Mismo patrón que `FormGpsGuidanceCalculator`: namespace `AgroParallel.Adapters`,
`using AgOpenGPS`, `FormGPS _form`, marshaling al hilo UI, try/catch defensivo.

```csharp
// ============================================================================
// FormGpsHeadlandEditService.cs — adapter IHeadlandEditService → PilotX.
// Envuelve FormGPS y corre la geometría del partial FormGPS.HeadlandEdit en el
// hilo UI (Invoke). Devuelve DTOs E/N para cabecera.html. Nunca tira: en error
// devuelve DTO con Ok=false / HasField=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsHeadlandEditService : IHeadlandEditService
    {
        private readonly FormGPS _form;

        public FormGpsHeadlandEditService(FormGPS form) { _form = form; }

        // Marshalea func al hilo UI. Si el form no está listo, corre el fallback.
        private T OnUi<T>(Func<T> body, T fallback)
        {
            if (_form == null || _form.IsDisposed) return fallback;
            try
            {
                if (_form.InvokeRequired)
                    return (T)_form.Invoke(new Func<T>(body));
                return body();
            }
            catch { return fallback; }
        }

        public HeadlandEditStateDto GetState()
        {
            return OnUi(() =>
            {
                var dto = new HeadlandEditStateDto
                {
                    HasField = _form.HeadlandEdit_HasBoundary(),
                    HasBoundary = _form.HeadlandEdit_HasBoundary(),
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    IsSectionControlled = _form.HeadlandEdit_IsSectionControlled(),
                    Units = _form.HeadlandEdit_Units(),
                    ToolWidthM = _form.HeadlandEdit_ToolWidthM(),
                    Fence = _form.HeadlandEdit_FenceEN(),
                    Headland = _form.HeadlandEdit_HeadlandEN()
                };
                return dto;
            }, new HeadlandEditStateDto());
        }

        public HeadlandEditResultDto BuildAround(double distanceDisplay)
        {
            return OnUi(() =>
            {
                bool ok = _form.HeadlandEdit_BuildAround(distanceDisplay);
                return new HeadlandEditResultDto
                {
                    Ok = ok,
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    Headland = _form.HeadlandEdit_HeadlandEN(),
                    Error = ok ? null : "offset-collapsed"
                };
            }, new HeadlandEditResultDto { Ok = false, Error = "ui-error" });
        }

        public HeadlandEditResultDto Reset()
        {
            return OnUi(() =>
            {
                _form.HeadlandEdit_Reset();
                return new HeadlandEditResultDto
                {
                    Ok = true,
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    Headland = _form.HeadlandEdit_HeadlandEN()
                };
            }, new HeadlandEditResultDto { Ok = false, Error = "ui-error" });
        }

        public HeadlandEditResultDto TurnOff()
        {
            return OnUi(() =>
            {
                _form.HeadlandEdit_TurnOff();
                return new HeadlandEditResultDto
                {
                    Ok = true,
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    Headland = _form.HeadlandEdit_HeadlandEN()
                };
            }, new HeadlandEditResultDto { Ok = false, Error = "ui-error" });
        }

        public bool SetSectionControlled(bool on)
        {
            return OnUi(() =>
            {
                _form.HeadlandEdit_SetSectionControlled(on);
                return _form.HeadlandEdit_IsSectionControlled();
            }, false);
        }
    }
}
```

- [x] **Step 2: Sin build aislado** — compila en la Task 6.

---

## Task 5: HeadlandController

**Files:**
- Create: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/HeadlandController.cs`

- [x] **Step 1: Escribir el controller**

Mismo patrón que `GuidanceController`: hereda `AgpControllerBase`, `WriteJsonAsync`,
`ReadJsonBodyAsync<T>()`. Rutas relativas (`/headland/...`) → EmbedIO expone bajo
`/api/headland/...`.

```csharp
// ============================================================================
// HeadlandController.cs — REST del editor de cabecera (cabecera.html).
//   GET  /api/headland/state              → estado + geometría (fence/hdLine E/N)
//   POST /api/headland/build   {distance} → offset Build Around (unidades display)
//   POST /api/headland/reset              → cabecera = copia del contorno
//   POST /api/headland/off                → apaga la cabecera
//   POST /api/headland/section-controlled {on} → toggle secciones por cabecera
// Wire snake_case por AgpJson. Si el servicio no está inyectado → service-unavailable.
// ============================================================================

using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class HeadlandController : AgpControllerBase
    {
        private readonly IHeadlandEditService _svc;

        public HeadlandController(IHeadlandEditService svc)
        {
            _svc = svc;
        }

        [Route(HttpVerbs.Get, "/headland/state")]
        public Task GetState()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.GetState());
        }

        [Route(HttpVerbs.Post, "/headland/build")]
        public async Task PostBuild()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            DistanceBody body;
            try { body = await ReadJsonBodyAsync<DistanceBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            double dist = body != null ? body.Distance : 0.0;
            await WriteJsonAsync(_svc.BuildAround(dist));
        }

        [Route(HttpVerbs.Post, "/headland/reset")]
        public Task PostReset()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.Reset());
        }

        [Route(HttpVerbs.Post, "/headland/off")]
        public Task PostOff()
        {
            if (_svc == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            return WriteJsonAsync(_svc.TurnOff());
        }

        [Route(HttpVerbs.Post, "/headland/section-controlled")]
        public async Task PostSectionControlled()
        {
            if (_svc == null) { await WriteJsonAsync(new { ok = false, error = "service-unavailable" }); return; }
            ToggleBody body;
            try { body = await ReadJsonBodyAsync<ToggleBody>(); }
            catch { await WriteJsonAsync(new { ok = false, error = "bad-json" }); return; }
            bool on = body != null && body.On;
            bool effective = _svc.SetSectionControlled(on);
            await WriteJsonAsync(new { ok = true, is_section_controlled = effective });
        }

        private sealed class DistanceBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("distance")]
            public double Distance { get; set; }
        }

        private sealed class ToggleBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("on")]
            public bool On { get; set; }
        }
    }
}
```

- [x] **Step 2: Sin build aislado** — compila en la Task 6.

---

## Task 6: Wire en AgpWebHost + Bootstrap + FormGPS, y BUILD

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs`
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.Shell/AgpWebHostBootstrap.cs`
- Modify: `SourceCode/GPS/Forms/FormGPS.cs`

- [x] **Step 1: AgpWebHost — declarar el campo**

Buscar la zona de campos privados (junto a `_guidance`) y agregar:
```csharp
        private readonly IHeadlandEditService _headlandEdit;
```
(Si no hay declaración visible de `_guidance` como campo, agregar la línea junto a
las otras `private readonly I*Service` cerca del top de la clase.)

- [x] **Step 2: AgpWebHost — nuevo parámetro de ctor**

En el ctor (`public AgpWebHost(...)`, termina en `IConfigVehiculoService configVehiculo = null)`
en la línea 138), agregar al final de la lista de parámetros:
```csharp
                          IConfigVehiculoService configVehiculo = null,
                          IHeadlandEditService headlandEdit = null)
```
Y en el cuerpo del ctor, junto a `_configVehiculo = configVehiculo;` (línea 160):
```csharp
            _headlandEdit = headlandEdit;     // nullable
```

- [x] **Step 3: AgpWebHost — registrar el controller**

Después de la línea 292 (`if (_configVehiculo != null) m.WithController(...)`), agregar:
```csharp
                // Editor de cabecera HTML (pages/cabecera.html) — flujo Build Around.
                if (_headlandEdit != null) m.WithController(() => new HeadlandController(_headlandEdit));
```

- [x] **Step 4: AgpWebHostBootstrap — nuevo parámetro en EnsureStarted**

En `EnsureStarted(...)` (termina en `IConfigVehiculoService configVehiculo = null)`,
línea 78), agregar al final:
```csharp
            IConfigVehiculoService configVehiculo = null,
            IHeadlandEditService headlandEdit = null)
```
Y en la construcción `new AgpWebHost(...)` (termina en `configVehiculo: configVehiculo);`
línea 143), agregar el named-arg:
```csharp
                    configVehiculo: configVehiculo,
                    headlandEdit: headlandEdit);
```

- [x] **Step 5: FormGPS — crear el adapter y pasarlo**

En `FormGPS.cs` línea ~619 (junto a `var configVehiculo = new ...FormGpsConfigService(this);`):
```csharp
                var headlandEdit = new global::AgroParallel.Adapters.FormGpsHeadlandEditService(this);
```
Y en la llamada `AgpWebHostBootstrap.EnsureStarted(...)` (línea 639-648), agregar al
final de los named-args (después de `configVehiculo: configVehiculo`):
```csharp
                    configVehiculo: configVehiculo,
                    headlandEdit: headlandEdit);
```

- [x] **Step 6: Build limpio**

Run:
```bash
cd "G:/agroparallel/productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
taskkill //IM PilotX.exe //F 2>/dev/null; taskkill //IM CoreX.exe //F 2>/dev/null
powershell -ExecutionPolicy Bypass -File build.ps1
```
Expected: `Build succeeded` / 0 errores, genera `PilotX_v1.0.xx.zip`. Si hay errores
de nombre de miembro (p.ej. `tool.width`, `unitsFtM`), corregir en el partial (Task 3)
y volver a buildear.

- [x] **Step 7: Commit backend**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Models/HeadlandEditDtos.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/IHeadlandEditService.cs \
        SourceCode/GPS/Forms/AgroParallel/FormGPS.HeadlandEdit.cs \
        SourceCode/GPS/AgroParallel/Common/FormGpsHeadlandEditService.cs \
        SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/HeadlandController.cs \
        SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs \
        SourceCode/AgroParallel/Web/AgroParallel.Shell/AgpWebHostBootstrap.cs \
        SourceCode/GPS/Forms/FormGPS.cs
git commit -m "feat(cabecera): backend Build Around (DTO/servicio/adapter/controller)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 7: Página cabecera.html

**Files:**
- Create: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/cabecera.html`

- [x] **Step 1: Escribir la página**

Sidebar `data-active="hub"`, page-head con pill de estado, canvas de preview a la
izquierda y panel de controles a la derecha. Scripts: keyboard.js, sidebar.js,
cabecera.js. Usa las vars del design-system (theme.css/layout.css).

```html
<!doctype html>
<html lang="es">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Cabecera · Agro Parallel</title>
  <link rel="stylesheet" href="../theme.css" />
  <link rel="stylesheet" href="../layout.css" />
  <style>
    /* Cabecera (reemplazo del flujo Build Around de FormHeadLine): offsetea el
       contorno hacia adentro N unidades y guarda la cabecera. El canvas es
       preview read-only (pan con drag, zoom con rueda); la geometría corre en
       C# (POST /api/headland/build). */
    .cab-wrap { display: flex; gap: var(--agp-sp-4); align-items: stretch; margin-top: var(--agp-sp-3); flex-wrap: wrap; }
    .cab-canvas-card {
      flex: 1 1 520px; min-height: 420px;
      background: var(--agp-surface);
      border: var(--agp-border-w) solid var(--agp-border);
      border-radius: var(--agp-radius-md);
      padding: var(--agp-sp-2); position: relative;
    }
    #cvPreview { width: 100%; height: 100%; display: block; touch-action: none; }
    .cab-side { flex: 0 0 300px; display: flex; flex-direction: column; gap: var(--agp-sp-3); }
    .cab-side .btn { width: 100%; min-height: 56px; font-size: var(--agp-fs-md); }
    .cab-dist { display: flex; align-items: center; gap: var(--agp-sp-2); }
    .cab-dist input {
      flex: 1 1 auto; font-family: var(--agp-font-mono); font-size: var(--agp-fs-lg);
      text-align: right; padding: var(--agp-sp-2); min-height: 56px;
      border: var(--agp-border-w) solid var(--agp-border); border-radius: var(--agp-radius-sm);
      background: var(--agp-surface); color: var(--agp-text);
    }
    .cab-unit { font-family: var(--agp-font-mono); color: var(--agp-text-muted); min-width: 24px; }
    #btnBuild { background: var(--agp-accent); color: #fff; border-color: var(--agp-accent); }
    .cab-toggle { display: flex; align-items: center; justify-content: space-between; gap: var(--agp-sp-2); }
    .cab-warn {
      display: none; background: #5a2c2c; color: #fff; border-radius: var(--agp-radius-sm);
      padding: var(--agp-sp-3); font-size: var(--agp-fs-sm);
    }
    .cab-warn.show { display: block; }
  </style>
</head>
<body>
  <aside class="sidebar" data-active="hub"></aside>

  <main class="page">
    <div class="page-head">
      <div>
        <h1>Cabecera</h1>
        <div class="subtitle">Construir cabecera por distancia · vista previa</div>
      </div>
      <span class="pill idle"><span class="dot"></span> <span id="statusText">—</span></span>
    </div>

    <div id="warnBox" class="cab-warn">Primero creá un contorno del lote para poder construir la cabecera.</div>

    <div class="cab-wrap">
      <div class="cab-canvas-card">
        <canvas id="cvPreview"></canvas>
      </div>

      <div class="cab-side">
        <div class="cab-dist">
          <input id="inpDist" type="text" inputmode="decimal" value="0" readonly data-keyboard="decimal" />
          <span class="cab-unit" id="unitLabel">m</span>
        </div>
        <button class="btn" id="btnToolWidth">Usar ancho de herramienta</button>
        <button class="btn" id="btnBuild">Construir cabecera</button>
        <button class="btn" id="btnReset">Reset (= contorno)</button>
        <div class="cab-toggle">
          <span>Secciones controladas en cabecera</span>
          <input id="chkSection" type="checkbox" />
        </div>
        <button class="btn" id="btnOff">Apagar cabecera</button>
      </div>
    </div>
  </main>

  <script src="../js/keyboard.js"></script>
  <script src="../js/sidebar.js"></script>
  <script src="../js/cabecera.js"></script>
</body>
</html>
```

- [x] **Step 2: Sin build** — los estáticos se copian a Build/ en la Task 9.

---

## Task 8: Lógica cabecera.js

**Files:**
- Create: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/cabecera.js`

- [x] **Step 1: Escribir el JS**

Carga estado, dibuja fence+headland en canvas con fit-to-bounds, pan (drag) y zoom
(rueda) solo de vista. Construir/Reset/Apagar/toggle → POST; redibujan con la
respuesta. Botón "ancho de herramienta" completa la distancia (tool_width_m → display).

```javascript
// ============================================================================
// cabecera.js
// Reemplazo del flujo "Build Around" de FormHeadLine. El canvas es preview
// read-only: dibuja el contorno (fence) y la cabecera (headland) en E/N metros,
// con fit-to-bounds + pan (drag) + zoom (rueda). La geometría corre en C#:
//   GET  /api/headland/state              → estado inicial + geometría
//   POST /api/headland/build   {distance} → offset (distancia en unidades display)
//   POST /api/headland/reset              → cabecera = contorno
//   POST /api/headland/off                → apaga la cabecera
//   POST /api/headland/section-controlled {on}
// Sin contorno: banner + Construir deshabilitado.
// ============================================================================

(function () {
  'use strict';

  var API = '/api/headland';

  var cv        = document.getElementById('cvPreview');
  var ctx       = cv.getContext('2d');
  var inpDist   = document.getElementById('inpDist');
  var unitLabel = document.getElementById('unitLabel');
  var btnWidth  = document.getElementById('btnToolWidth');
  var btnBuild  = document.getElementById('btnBuild');
  var btnReset  = document.getElementById('btnReset');
  var btnOff    = document.getElementById('btnOff');
  var chkSection= document.getElementById('chkSection');
  var statusEl  = document.getElementById('statusText');
  var warnBox   = document.getElementById('warnBox');

  var fence = [];      // [[e,n], ...]
  var headland = [];   // [[e,n], ...]
  var units = 'm';
  var toolWidthM = 0;
  var hasBoundary = false;

  // Vista (pan/zoom) — solo afecta el dibujo, no el modelo.
  var view = { scale: 1, ox: 0, oy: 0, fitted: false };
  var drag = null;

  function setStatus(txt, live) {
    statusEl.textContent = txt;
  }

  // ── Canvas sizing ─────────────────────────────────────────────────────────
  function resize() {
    var r = cv.getBoundingClientRect();
    var dpr = window.devicePixelRatio || 1;
    cv.width = Math.max(1, Math.round(r.width * dpr));
    cv.height = Math.max(1, Math.round(r.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (!view.fitted) fitToBounds();
    draw();
  }

  function bounds() {
    var pts = fence.length ? fence : headland;
    if (!pts.length) return null;
    var minE = Infinity, maxE = -Infinity, minN = Infinity, maxN = -Infinity;
    for (var i = 0; i < pts.length; i++) {
      var e = pts[i][0], n = pts[i][1];
      if (e < minE) minE = e; if (e > maxE) maxE = e;
      if (n < minN) minN = n; if (n > maxN) maxN = n;
    }
    return { minE: minE, maxE: maxE, minN: minN, maxN: maxN };
  }

  function fitToBounds() {
    var b = bounds();
    var r = cv.getBoundingClientRect();
    if (!b || r.width < 2 || r.height < 2) { view.fitted = true; return; }
    var w = Math.max(1, b.maxE - b.minE);
    var h = Math.max(1, b.maxN - b.minN);
    var pad = 0.9;
    var scale = Math.min(r.width / w, r.height / h) * pad;
    view.scale = scale;
    // Centro del lote → centro del canvas. N crece hacia arriba (y invertido).
    var cE = (b.minE + b.maxE) / 2, cN = (b.minN + b.maxN) / 2;
    view.ox = r.width / 2 - cE * scale;
    view.oy = r.height / 2 + cN * scale;
    view.fitted = true;
  }

  // Modelo (E/N metros) → pantalla (px CSS).
  function toScreen(e, n) {
    return { x: e * view.scale + view.ox, y: -n * view.scale + view.oy };
  }

  function drawPolyline(pts, color, width, close) {
    if (!pts.length) return;
    ctx.beginPath();
    for (var i = 0; i < pts.length; i++) {
      var p = toScreen(pts[i][0], pts[i][1]);
      if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
    }
    if (close) ctx.closePath();
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.stroke();
  }

  function draw() {
    var r = cv.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);
    drawPolyline(fence, '#8a978c', 2, true);    // contorno gris
    drawPolyline(headland, '#4ABA3E', 3, true); // cabecera verde
  }

  // ── Estado ────────────────────────────────────────────────────────────────
  function applyState(s) {
    if (!s || s.has_field === false) {
      hasBoundary = false;
      fence = []; headland = [];
      warnBox.classList.add('show');
      btnBuild.disabled = true; btnWidth.disabled = true; btnReset.disabled = true; btnOff.disabled = true;
      setStatus('sin contorno');
      view.fitted = false; draw();
      return;
    }
    hasBoundary = !!s.has_boundary;
    fence = s.fence || [];
    headland = s.headland || [];
    units = s.units || 'm';
    toolWidthM = s.tool_width_m || 0;
    unitLabel.textContent = units;
    chkSection.checked = !!s.is_section_controlled;
    warnBox.classList.toggle('show', !hasBoundary);
    var dis = !hasBoundary;
    btnBuild.disabled = dis; btnWidth.disabled = dis; btnReset.disabled = dis; btnOff.disabled = dis;
    setStatus(s.is_headland_on ? 'cabecera activa' : 'sin cabecera');
    view.fitted = false;
    resize();
  }

  async function loadState() {
    try {
      var res = await fetch(API + '/state');
      var data = await res.json();
      applyState(data);
    } catch (e) {
      setStatus('sin conexión');
      warnBox.textContent = 'Sin conexión con PilotX.';
      warnBox.classList.add('show');
    }
  }

  async function post(path, body) {
    try {
      var res = await fetch(API + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      return await res.json();
    } catch (e) {
      setStatus('sin conexión');
      return null;
    }
  }

  function applyResult(data) {
    if (!data) return;
    if (data.headland) headland = data.headland;
    if (typeof data.is_headland_on === 'boolean')
      setStatus(data.is_headland_on ? 'cabecera activa' : 'sin cabecera');
    if (data.ok === false && data.error) console.warn('[cabecera]', data.error);
    draw();
  }

  // ── Controles ───────────────────────────────────────────────────────────────
  btnWidth.addEventListener('click', function () {
    if (!toolWidthM) return;
    var disp = units === 'ft' ? toolWidthM * 3.28084 : toolWidthM;
    inpDist.value = (Math.round(disp * 10) / 10).toString();
  });

  btnBuild.addEventListener('click', async function () {
    var d = parseFloat(inpDist.value);
    if (isNaN(d) || d < 0) d = 0;
    setStatus('construyendo…');
    applyResult(await post('/build', { distance: d }));
  });

  btnReset.addEventListener('click', async function () {
    applyResult(await post('/reset', {}));
  });

  btnOff.addEventListener('click', async function () {
    var data = await post('/off', {});
    if (data) { headland = data.headland || []; setStatus('sin cabecera'); draw(); }
  });

  chkSection.addEventListener('change', async function () {
    await post('/section-controlled', { on: chkSection.checked });
  });

  // ── Pan / zoom (solo vista) ─────────────────────────────────────────────────
  cv.addEventListener('pointerdown', function (ev) {
    drag = { x: ev.clientX, y: ev.clientY, ox: view.ox, oy: view.oy };
    cv.setPointerCapture(ev.pointerId);
  });
  cv.addEventListener('pointermove', function (ev) {
    if (!drag) return;
    view.ox = drag.ox + (ev.clientX - drag.x);
    view.oy = drag.oy + (ev.clientY - drag.y);
    draw();
  });
  cv.addEventListener('pointerup', function () { drag = null; });
  cv.addEventListener('wheel', function (ev) {
    ev.preventDefault();
    var r = cv.getBoundingClientRect();
    var mx = ev.clientX - r.left, my = ev.clientY - r.top;
    var factor = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
    // Zoom centrado en el cursor.
    view.ox = mx - (mx - view.ox) * factor;
    view.oy = my - (my - view.oy) * factor;
    view.scale *= factor;
    draw();
  }, { passive: false });

  window.addEventListener('resize', resize);

  // ── Arranque ────────────────────────────────────────────────────────────────
  loadState();
})();
```

---

## Task 9: Repunte del launcher + BUILD estáticos

**Files:**
- Modify: `SourceCode/GPS/Forms/Controls.Designer.cs` (línea 734)

- [x] **Step 1: Repuntar `headlandToolStripMenuItem_Click`**

Reemplazar el cuerpo (líneas 734-743) para abrir el widget en vez de `GetHeadland()`,
manteniendo el guard de contorno:
```csharp
        private void headlandToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (bnd.bndList.Count == 0)
            {
                TimedMessageBox(2000, gStr.gsNoBoundary, gStr.gsCreateABoundaryFirst);
                return;
            }

            if (!LaunchAvaloniaWidget("pages/cabecera.html", "float", "Cabecera", 1000, 720))
            { OpenAgroParallelHub("pages/cabecera.html"); }
            this.Activate();
        }
```
`GetHeadland()` (líneas 721-733) y `FormHeadLine` **se mantienen** (fase 2 reshape).
NO tocar `headlandBuildToolStripMenuItem_Click` (abre FormHeadAche, otro flujo).

- [x] **Step 2: Build + copia de estáticos**

```bash
cd "G:/agroparallel/productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
taskkill //IM PilotX.exe //F 2>/dev/null; taskkill //IM CoreX.exe //F 2>/dev/null
powershell -ExecutionPolicy Bypass -File build.ps1
```
Expected: `Build succeeded`. `build.ps1` recopia `wwwroot/` a `Build/`, así que
cabecera.html/.js quedan servidos por :5180.

---

## Task 10: Verificación end-to-end + commit final

- [x] **Step 1: Lanzar PilotX y curl de endpoints (sin lote)**

```bash
cd "G:/agroparallel/productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
./Build/PilotX.exe &
sleep 12
curl -s http://127.0.0.1:5180/api/headland/state
echo "---"
curl -s -X POST http://127.0.0.1:5180/api/headland/build -H "Content-Type: application/json" -d '{"distance":5}'
echo "---"
curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5180/pages/cabecera.html
echo " (cabecera.html)"
curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5180/js/cabecera.js
echo " (cabecera.js)"
```
Expected (sin lote abierto):
- `/state` → JSON con `has_field:false` / `has_boundary:false` (el prefijo BOM `﻿`
  es normal).
- `/build` → `ok:false` (guard sin contorno).
- ambos estáticos → `200`.

- [x] **Step 2: Playwright — navegar + screenshot**

Navegar a `http://127.0.0.1:5180/pages/cabecera.html`, tomar screenshot a la raíz del
repo (`cabecera-verify.png`) y leerla con Read. Verificar: sidebar, título "Cabecera",
canvas vacío, banner "Primero creá un contorno…", controles deshabilitados.

- [x] **Step 3: Limpiar y matar procesos**

```bash
cd "G:/agroparallel/productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
taskkill //IM PilotX.exe //F 2>/dev/null; taskkill //IM CoreX.exe //F 2>/dev/null
rm -rf .playwright-mcp cabecera-verify.png
```

- [x] **Step 4: Commit frontend + launcher**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/cabecera.html \
        SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/cabecera.js \
        SourceCode/GPS/Forms/Controls.Designer.cs
git commit -m "feat(cabecera): widget HTML Build Around + repunte del launcher

Reemplaza el flujo Build Around de FormHeadLine por pages/cabecera.html
(canvas preview + POST /api/headland/*). FormHeadLine se mantiene para el
reshape manual (fase 2).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

- [ ] **Step 5: Verificación con lote real (manual, follow-up)**

La corrección geométrica del offset la garantiza el reuso del algoritmo exacto de
`btnBndLoop_Click`. La prueba con contorno cargado (Construir 5 m → cabecera hacia
adentro visible en el mapa de PilotX) la hace el usuario o un follow-up con lote de
prueba. Documentar el resultado en `COORDINACION-UI.md` si aplica.

---

## Notas de cierre

- `FormHeadLine.{cs,Designer.cs,resx}` NO se borra (base de la fase 2: reshape manual).
- Al terminar, usar **superpowers:finishing-a-development-branch** para cerrar la rama.
- Fase 2 (reshape) y las migraciones de TramLine + BndTool quedan como trabajo posterior.
