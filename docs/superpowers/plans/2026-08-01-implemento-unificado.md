# Implemento unificado (fase 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Una sola pantalla configura el implemento (secciones, trenes, surco→tren); la distancia entre trenes que usan QuantiX y SectionX se deriva de ahí, con fallback a las copias por nodo.

**Architecture:** Un resolver puro (`TrenResolver`) deriva tren/distancia desde `ImplementoDto`; los dos consumidores (`QuantiXMotorBridge`, `SectionXCutAdapter`) lo consultan vía una propiedad inyectable `ImplementoProvider` y caen al valor por-nodo si no hay trenes útiles. La UI de `config-implemento.html` gana la sección Trenes + tira surco→tren y guarda en dos PUT (tool primero, implemento después). `GetSectionsAtDistanceBack` no se toca.

**Tech Stack:** C# (.NET 8 Services + net48 host), xUnit (`AgroParallel.Services.Tests`), JS vanilla (Hub wwwroot), EmbedIO.

## Global Constraints

- Respuestas/UI/logs en castellano rioplatense; nombres de producto (PilotX, no AOG).
- Wire JSON snake_case vía `JsonPropertyName` (spec del repo).
- Todo cambio que altere qué sección aplica producto: fallback fase 1 obligatorio (regla CLAUDE.md).
- Compilar y correr `AgroParallel.Services.Tests` antes de declarar nada andando.
- Trenes: `0 ≤ distancia_m ≤ 20`; máx. 4 trenes; tren id=1 = delantero con distancia 0.
- No modificar: `PositionHistory.GetSectionsAtDistanceBack`, núcleo de guiado, pintado.

---

### Task 1: TrenResolver (derivación pura surcos→tren→distancia)

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/Common/TrenResolver.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/TrenResolverTests.cs`

**Interfaces:**
- Consumes: `AgroParallel.Models.ImplementoDto` (existente: `Trenes: List<TrenDto>{Id,Nombre,DistanciaM}`, `Surcos: List<SurcoDto>{Numero,TrenId,SeccionPilotX}`).
- Produces: `public static TrenResultado TrenResolver.Resolver(ImplementoDto impl, IEnumerable<int> surcos)` y `public sealed class TrenResultado { public int TrenId; public double DistanciaM; public bool Conflicto; }`. Devuelve **null** cuando no hay dato derivable (→ el consumidor usa su fallback).

- [ ] **Step 1: Write the failing tests**

```csharp
// SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/TrenResolverTests.cs
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Common;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class TrenResolverTests
    {
        private static ImplementoDto Impl2Trenes()
        {
            var impl = new ImplementoDto();
            impl.Trenes.Add(new TrenDto { Id = 1, Nombre = "Delantero", DistanciaM = 0 });
            impl.Trenes.Add(new TrenDto { Id = 2, Nombre = "Trasero", DistanciaM = 2.5 });
            for (int i = 1; i <= 14; i++)
                impl.Surcos.Add(new SurcoDto { Numero = i, TrenId = (i % 2 == 1) ? 1 : 2, SeccionPilotX = i });
            return impl; // impares delanteros, pares traseros
        }

        [Fact]
        public void SurcosDeUnTren_DevuelveEseTrenSinConflicto()
        {
            var r = TrenResolver.Resolver(Impl2Trenes(), new[] { 2, 4, 6 });
            Assert.NotNull(r);
            Assert.Equal(2, r.TrenId);
            Assert.Equal(2.5, r.DistanciaM, 3);
            Assert.False(r.Conflicto);
        }

        [Fact]
        public void SurcosDelantero_DistanciaCero()
        {
            var r = TrenResolver.Resolver(Impl2Trenes(), new[] { 1, 3 });
            Assert.NotNull(r);
            Assert.Equal(1, r.TrenId);
            Assert.Equal(0, r.DistanciaM, 3);
        }

        [Fact]
        public void SurcosMezclados_ConflictoYGanaElPrimero()
        {
            var r = TrenResolver.Resolver(Impl2Trenes(), new[] { 2, 3 });
            Assert.NotNull(r);
            Assert.True(r.Conflicto);
            Assert.Equal(2, r.TrenId); // el del primer surco pedido
        }

        [Fact]
        public void SinTrenesUtiles_DevuelveNull_ParaFallback()
        {
            Assert.Null(TrenResolver.Resolver(null, new[] { 1 }));
            Assert.Null(TrenResolver.Resolver(new ImplementoDto(), new[] { 1 })); // sin trenes
            var unSolo = new ImplementoDto();
            unSolo.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
            Assert.Null(TrenResolver.Resolver(unSolo, new[] { 1 })); // 1 tren = nada que derivar
        }

        [Fact]
        public void SurcosDesconocidos_SeIgnoran_TodosDesconocidosEsNull()
        {
            var impl = Impl2Trenes();
            var r = TrenResolver.Resolver(impl, new[] { 99, 2 });
            Assert.NotNull(r);
            Assert.Equal(2, r.TrenId);
            Assert.Null(TrenResolver.Resolver(impl, new[] { 99, 100 }));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter TrenResolver -v q --nologo`
Expected: FAIL de compilación — `TrenResolver` no existe.

- [ ] **Step 3: Write minimal implementation**

```csharp
// SourceCode/AgroParallel/Core/AgroParallel.Services/Common/TrenResolver.cs
// ============================================================================
// TrenResolver.cs — deriva a qué tren pertenece un conjunto de surcos y a qué
// distancia corta ese tren, desde el ImplementoDto central.
//
// Reemplaza (fase 1: con fallback) a las TRES copias de "distancia entre
// trenes" que había: sectionX.json por nodo, quantiX_motores.json por nodo y
// el campo `tren` manual por motor. Con dos lugares editables la que se usaba
// para dosificar podía ser la equivocada (spec 2026-08-01).
//
// Contrato: devuelve null cuando NO hay dato derivable (implemento sin trenes,
// un solo tren, surcos desconocidos). El consumidor decide su fallback; acá no
// se inventa un default porque esto decide dónde corta una sección.
// ============================================================================
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Common
{
    public sealed class TrenResultado
    {
        public int TrenId;
        public double DistanciaM;
        /// <summary>Los surcos pedidos pertenecen a más de un tren: error de
        /// configuración. Se resuelve con el tren del primer surco, no
        /// promediando — y el consumidor debería avisarlo.</summary>
        public bool Conflicto;
    }

    public static class TrenResolver
    {
        public static TrenResultado Resolver(ImplementoDto impl, IEnumerable<int> surcos)
        {
            if (impl == null || impl.Trenes == null || impl.Trenes.Count < 2) return null;
            if (impl.Surcos == null || impl.Surcos.Count == 0 || surcos == null) return null;

            var trenPorSurco = new Dictionary<int, int>();
            foreach (var s in impl.Surcos)
                if (s != null) trenPorSurco[s.Numero] = s.TrenId;

            var distPorTren = new Dictionary<int, double>();
            foreach (var t in impl.Trenes)
                if (t != null) distPorTren[t.Id] = t.DistanciaM;

            TrenResultado r = null;
            foreach (int numero in surcos)
            {
                int trenId;
                if (!trenPorSurco.TryGetValue(numero, out trenId)) continue; // surco desconocido: se ignora
                if (!distPorTren.ContainsKey(trenId)) continue;              // tren huérfano: ídem
                if (r == null)
                    r = new TrenResultado { TrenId = trenId, DistanciaM = distPorTren[trenId] };
                else if (trenId != r.TrenId)
                    r.Conflicto = true;
            }
            return r;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter TrenResolver -v q --nologo`
Expected: 5 PASS.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Common/TrenResolver.cs SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/TrenResolverTests.cs
git commit -m "feat(implemento): TrenResolver — tren/distancia derivados de surco→tren, null para fallback"
```

---

### Task 2: Regeneración de surcos al cambiar la cantidad de secciones

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/Common/ImplementoSurcos.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ImplementoSurcosTests.cs`

**Interfaces:**
- Produces: `public static void ImplementoSurcos.Regenerar(ImplementoDto impl, int numSecciones)` — deja `impl.Surcos` con exactamente `numSecciones` entradas (`Numero=i`, `SeccionPilotX=i`), conservando `TrenId` por índice; los nuevos heredan el tren del último surco existente (o 1). También `impl.NumeroSurcos = numSecciones`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ImplementoSurcosTests.cs
using AgroParallel.Models;
using AgroParallel.Services.Common;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class ImplementoSurcosTests
    {
        [Fact]
        public void Crecer_ConservaAsignacionYHeredaUltimoTren()
        {
            var impl = new ImplementoDto();
            impl.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 1, SeccionPilotX = 1 });
            impl.Surcos.Add(new SurcoDto { Numero = 2, TrenId = 2, SeccionPilotX = 2 });
            ImplementoSurcos.Regenerar(impl, 4);
            Assert.Equal(4, impl.Surcos.Count);
            Assert.Equal(1, impl.Surcos[0].TrenId);
            Assert.Equal(2, impl.Surcos[1].TrenId);
            Assert.Equal(2, impl.Surcos[2].TrenId); // hereda del último
            Assert.Equal(2, impl.Surcos[3].TrenId);
            Assert.Equal(3, impl.Surcos[2].Numero);
            Assert.Equal(3, impl.Surcos[2].SeccionPilotX);
            Assert.Equal(4, impl.NumeroSurcos);
        }

        [Fact]
        public void Achicar_Trunca()
        {
            var impl = new ImplementoDto();
            for (int i = 1; i <= 5; i++)
                impl.Surcos.Add(new SurcoDto { Numero = i, TrenId = i <= 2 ? 1 : 2, SeccionPilotX = i });
            ImplementoSurcos.Regenerar(impl, 2);
            Assert.Equal(2, impl.Surcos.Count);
            Assert.Equal(1, impl.Surcos[1].TrenId);
        }

        [Fact]
        public void DesdeVacio_TodosTren1()
        {
            var impl = new ImplementoDto();
            ImplementoSurcos.Regenerar(impl, 3);
            Assert.Equal(3, impl.Surcos.Count);
            Assert.All(impl.Surcos, s => Assert.Equal(1, s.TrenId));
        }
    }
}
```

- [ ] **Step 2: Run to verify FAIL** (`--filter ImplementoSurcos`): error de compilación.

- [ ] **Step 3: Implementation**

```csharp
// SourceCode/AgroParallel/Core/AgroParallel.Services/Common/ImplementoSurcos.cs
// Regenera surcos[] cuando cambia la cantidad de secciones de PilotX.
// Una sección = un surco (decisión de la spec 2026-08-01): la cantidad de
// surcos no se edita por separado, ES numSections. Conserva la asignación
// surco→tren por índice para no perder el trabajo del operario al ajustar.
using AgroParallel.Models;

namespace AgroParallel.Services.Common
{
    public static class ImplementoSurcos
    {
        public static void Regenerar(ImplementoDto impl, int numSecciones)
        {
            if (impl == null || numSecciones < 1) return;
            var viejos = impl.Surcos;
            int ultimoTren = 1;
            if (viejos != null && viejos.Count > 0 && viejos[viejos.Count - 1] != null)
                ultimoTren = viejos[viejos.Count - 1].TrenId;

            var nuevos = new System.Collections.Generic.List<SurcoDto>(numSecciones);
            for (int i = 1; i <= numSecciones; i++)
            {
                int tren = (viejos != null && i <= viejos.Count && viejos[i - 1] != null)
                    ? viejos[i - 1].TrenId : ultimoTren;
                if (tren < 1) tren = 1;
                nuevos.Add(new SurcoDto { Numero = i, TrenId = tren, SeccionPilotX = i });
            }
            impl.Surcos = nuevos;
            impl.NumeroSurcos = numSecciones;
        }
    }
}
```

Nota: si `ImplementoDto.Surcos` no tiene setter público, agregarlo (`public List<SurcoDto> Surcos { get; set; }`) — verificar en `ImplementoDto.cs`.

- [ ] **Step 4: Run to verify PASS** (`--filter ImplementoSurcos`): 3 PASS.

- [ ] **Step 5: Commit** — `feat(implemento): regeneración de surcos al cambiar secciones (1 sección = 1 surco)`

---

### Task 3: Validación de trenes al guardar

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Models/ConfigValidation.cs` (agregar método; seguir el patrón `r.Requerir(...)` existente, ver línea ~186)
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ConfigValidationTests.cs` (agregar al final)

**Interfaces:**
- Produces: `public static ValidationResult ConfigValidation.ValidarTrenes(ImplementoDto impl)` usando el mismo `ValidationResult` del archivo. Reglas: máx. 4 trenes; distancia 0–20 m; tren 1 con distancia 0; ningún surco con `TrenId` inexistente. Son **warnings/errores de reporte**: el guardado no se bloquea (criterio de la spec).

- [ ] **Step 1: Tests** (agregar a `ConfigValidationTests.cs`; usar el helper de asserts del archivo)

```csharp
[Fact]
public void ValidarTrenes_DistanciaFueraDeRango_Falla()
{
    var impl = new ImplementoDto();
    impl.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
    impl.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 25 }); // > 20
    var r = ConfigValidation.ValidarTrenes(impl);
    Assert.False(r.Ok);
}

[Fact]
public void ValidarTrenes_SurcoConTrenInexistente_Falla()
{
    var impl = new ImplementoDto();
    impl.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
    impl.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 2 });
    impl.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 9 });
    Assert.False(ConfigValidation.ValidarTrenes(impl).Ok);
}

[Fact]
public void ValidarTrenes_ConfigSana_Ok()
{
    var impl = new ImplementoDto();
    impl.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
    impl.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 2.5 });
    impl.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 2 });
    Assert.True(ConfigValidation.ValidarTrenes(impl).Ok);
}
```

- [ ] **Step 2: FAIL** (`--filter ValidarTrenes`).
- [ ] **Step 3: Implementación** en `ConfigValidation.cs`, junto a las validaciones de implemento existentes:

```csharp
public static ValidationResult ValidarTrenes(ImplementoDto impl)
{
    var r = new ValidationResult();
    if (impl == null) return r;
    var trenes = impl.Trenes ?? new List<TrenDto>();
    r.Requerir(trenes.Count <= 4, "implemento: más de 4 trenes");
    var ids = new HashSet<int>();
    foreach (var t in trenes)
    {
        if (t == null) continue;
        ids.Add(t.Id);
        r.Requerir(t.DistanciaM >= 0 && t.DistanciaM <= 20,
            $"tren {t.Id}: distancia fuera de rango (0–20 m): {t.DistanciaM}");
        if (t.Id == 1)
            r.Requerir(t.DistanciaM == 0, "tren 1 (delantero) debe tener distancia 0");
    }
    if (impl.Surcos != null && trenes.Count > 0)
        foreach (var s in impl.Surcos)
            if (s != null)
                r.Requerir(ids.Contains(s.TrenId),
                    $"surco {s.Numero}: tren inexistente {s.TrenId}");
    return r;
}
```

(Ajustar `ValidationResult`/`Requerir` al shape real del archivo — mirar los métodos vecinos y copiar su estilo exacto.)

- [ ] **Step 4: PASS** (`--filter ValidarTrenes`) y suite completa sin regresiones.
- [ ] **Step 5: Commit** — `feat(implemento): validación de trenes (rango, delantero=0, surcos huérfanos)`

---

### Task 4: SectionXCutAdapter deriva el tren con fallback

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/Cut/SectionXCutAdapter.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/SectionXCutAdapterTrenTests.cs` (nuevo)

**Interfaces:**
- Consumes: `TrenResolver.Resolver(ImplementoDto, IEnumerable<int>)` (Task 1).
- Produces: propiedad pública `public Func<ImplementoDto> ImplementoProvider { get; set; }` en el adapter. Con provider y resultado no-null: la distancia del cable sale de `TrenResolver.Resolver(impl, new[]{ cable.SeccionAOG })`. Sin provider o resultado null: comportamiento actual intacto (`cable.Tren` + `nodo.DistanciaEntreTrenes`).

- [ ] **Step 1: Test**

El adapter carga `SectionXConfig.Load()` en el constructor; para testearlo hace falta el seam que ya exista o, si `_config` solo se setea ahí, agregar constructor interno `internal SectionXCutAdapter(SectionXConfig cfg)` para tests (patrón habitual del repo — verificar si ya existe algo así; si existe, usarlo). Test:

```csharp
// SectionXCutAdapterTrenTests.cs — esqueleto; completar los DTOs con el shape
// real de SectionXConfig/SxNodo/SxCableMapDto al escribirlo.
[Fact]
public void ConImplementoDosTrenes_CableDeSurcoTrasero_UsaSeccionesRetrasadas()
{
    // nodo con 1 cable → SeccionAOG=2, cable.Tren=0 (viejo: delantero)
    // implemento: surco 2 en tren 2 (distancia 2.5)
    // hist devuelve un patrón distinto al actual para poder distinguir
    // Assert: los bits publicados usan el patrón retrasado (el implemento manda
    // sobre el campo cable.Tren viejo).
}

[Fact]
public void SinTrenesEnImplemento_UsaFallbackPorNodo()
{
    // ImplementoProvider devuelve impl sin trenes → derivación null →
    // los bits salen como hoy (cable.Tren + nodo.DistanciaEntreTrenes).
}
```

Para `hist`: `PositionHistory` — si es difícil de instanciar con datos, extraer interfaz mínima ya existente o pasar por `GetSectionsAtDistanceBack` real alimentando posiciones sintéticas (mirar `SiembraStateMachineTests` que ya simula snapshots — copiar ese approach).

- [ ] **Step 2: FAIL.**
- [ ] **Step 3: Implementación** en `ComputePublishes` (líneas ~57-90). Reemplazar la fuente de la distancia:

```csharp
public Func<AgroParallel.Models.ImplementoDto> ImplementoProvider { get; set; }

// dentro de ComputePublishes, antes del foreach de nodos:
var impl = ImplementoProvider != null ? ImplementoProvider() : null;

// dentro del foreach de cables, reemplazando `bool[] fuente = (cable.Tren == 0) ? secAOG : secTrasero;`:
double distCable = -1;
var tr = AgroParallel.Services.Common.TrenResolver.Resolver(impl, new[] { cable.SeccionAOG });
if (tr != null)
{
    distCable = tr.DistanciaM;
    if (tr.Conflicto) AgpLog.Warn("SectionX", $"cable {cable.Cable}: surcos de trenes distintos");
}
else
{
    // Fallback fase 1: config por nodo como siempre.
    distCable = (cable.Tren == 0) ? 0 : nodo.DistanciaEntreTrenes;
}
bool[] fuente = distCable > 0.05
    ? ObtenerRetrasadas(hist, distCable, secAOG, ref secTraseroCache)
    : secAOG;
```

con `ObtenerRetrasadas` = extracción del bloque de caché existente (mismo `Dictionary<double,bool[]>`, una entrada por distancia). Loguear UNA vez por arranque qué fuente se usó (`AgpLog.Info("SectionX", "trenes: derivados del implemento" / "trenes: fallback por nodo")`).

- [ ] **Step 4: PASS** nuevos + suite completa verde (los escenarios 1-tren no cambian de resultado).
- [ ] **Step 5: Commit** — `feat(sectionx): distancia de tren derivada del implemento con fallback por nodo`

---

### Task 5: QuantiXMotorBridge deriva el tren del motor

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs` (líneas ~205-225)
- Modify (wiring): `SourceCode/PilotX.GuidanceEngine/EngineWebHost.cs` (~línea 287, donde se crea el bridge) y `SourceCode/GPS/Forms/FormGPS.cs` (líneas 1161 y 4220)

**Interfaces:**
- Consumes: `TrenResolver` (Task 1). La lógica derivable ya está testeada ahí; este task es cableado.
- Produces: `public Func<ImplementoDto> ImplementoProvider { get; set; }` en el bridge.

- [ ] **Step 1: Modificar el bridge.** Donde hoy decide `bool[] secMotor = (motor.Tren == 0) ? seccionesPilotX : secTrasero;`:

```csharp
var trM = AgroParallel.Services.Common.TrenResolver.Resolver(
    ImplementoProvider != null ? ImplementoProvider() : null, motor.Cortes);
double distMotor = trM != null
    ? trM.DistanciaM
    : ((motor.Tren == 0) ? 0 : nodo.DistanciaEntreTrenes);   // fallback fase 1
if (trM != null && trM.Conflicto)
    Log(string.Format("  M{0}: surcos de trenes distintos — usando tren {1}", mi, trM.TrenId));
bool[] secMotor = seccionesPilotX;
if (distMotor > 0.05 && seccionesPilotX != null)
    secMotor = _posHistory.GetSectionsAtDistanceBack(distMotor) ?? seccionesPilotX;
```

(El bloque actual que calcula `secTrasero` una vez por nodo se adapta: cachear por distancia como hace el adapter de SectionX, un `Dictionary<double,bool[]>` por tick.)

- [ ] **Step 2: Wiring.** En `EngineWebHost.cs` al crear el bridge: `_quantixBridge.ImplementoProvider = () => _implementoService != null ? _implementoService.GetImplemento() : null;` — usar el `ImplementoService` que EngineWebHost ya tiene (buscar `ImplementoService` en el archivo; si se llama distinto, adaptar). Ídem para el `SectionXCutAdapter`: encontrar dónde se instancia (grep `new SectionXCutAdapter` — está en el arranque del CutDispatcher) y setear el mismo provider. En `FormGPS.cs` (WinForms legacy): mismo patrón en los dos call-sites.

- [ ] **Step 3: Compilar TODO** (`dotnet build` de Services + Engine + GPS) y correr la suite completa: verde.

- [ ] **Step 4: Commit** — `feat(quantix): tren del motor derivado de sus surcos con fallback al campo manual`

---

### Task 6: Pantalla unificada — trenes + surco→tren en config-implemento

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/config-implemento.html` (sección nueva después del bloque de secciones)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/config-implemento.js`
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/sidebar.js` (quitar la entrada `herramienta`)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/herramienta.html` (banner "se movió")

**Interfaces:**
- Consumes: `GET/PUT /api/implemento` (shape existente con `trenes[]`, `surcos[]`), `GET/PUT /api/tool`, `ImplementoSurcos.Regenerar` vía backend (el PUT de implemento debe regenerar si `numSections` cambió — ver Step 3).

- [ ] **Step 1: HTML.** Agregar al final del formulario de secciones:

```html
<!-- ===== TRENES DE SIEMBRA ===== -->
<div class="card" id="cardTrenes">
  <h3>Trenes de siembra</h3>
  <p class="hint">Para máquinas con tren delantero y trasero: cada surco pertenece a un
  tren, y el trasero corta N metros después (sobre la misma pasada). Con un solo tren
  esta sección no afecta nada.</p>
  <div id="trenList"></div>
  <div class="btn-row">
    <button type="button" class="btn" id="btnAddTren">+ Agregar tren</button>
  </div>
  <div id="trenSurcos" style="margin-top: var(--agp-sp-3)">
    <label>Qué surcos van en cada tren (tocá para pintar)</label>
    <div id="trenBrush" class="btn-row"></div>
    <div id="trenStrip" class="qx-strip"></div>
  </div>
  <span class="send-msg" id="trenMsg"></span>
</div>
```

- [ ] **Step 2: JS.** En `config-implemento.js` (seguir el estilo del archivo: IIFE, `el()` helpers):
  - `loadImplemento()`: `GET /api/implemento` → `state.impl`; render de `trenList` (fila por tren: nombre editable, distancia stepper 0–20 paso 0.1 — el tren 1 con distancia fija 0 y sin botón borrar), `trenBrush` (un botón-pincel por tren, coloreado), `trenStrip` (una celda por surco según `tool.numSections`; click = asignar el pincel activo; color por tren).
  - Al cambiar `numSections` en la parte de secciones: regenerar la tira local con el mismo criterio de `ImplementoSurcos.Regenerar` (conservar por índice, heredar el último).
  - `guardarTodo()` (reemplaza el submit actual): 1) `PUT /api/tool` con la geometría (código existente), 2) `PUT /api/implemento` con `state.impl` actualizado (trenes + surcos + `numero_surcos = numSections` + `distancia_entre_surcos_m = width/numSections`). Si (2) falla: mensaje "geometría guardada, sembradora NO — reintentá" (no revertir (1)).
  - Máximo 4 trenes; al borrar un tren, sus surcos pasan al tren 1.
- [ ] **Step 3: Backend.** En el `PUT /api/implemento` (controller `ImplementoController` → `ImplementoService.SaveImplemento`): antes de persistir, llamar `ImplementoSurcos.Regenerar(dto, dto.NumeroSurcos)` SOLO si `dto.Surcos.Count != dto.NumeroSurcos` (defensa contra clientes viejos), y loguear el resultado de `ConfigValidation.ValidarTrenes(dto)` como warning sin bloquear.
- [ ] **Step 4: Sidebar + redirección.** En `sidebar.js` quitar la línea `{ id: 'herramienta', ... }`. En `herramienta.html`, banner arriba del contenido: `<div class="card" style="border-color:var(--agp-state-warn)">Esta pantalla se unificó en <a href="config-implemento.html">Implemento PilotX</a>. Los valores de acá son solo lectura.</div>`.
- [ ] **Step 5: Verificar en navegador (Playwright):** cargar 2 trenes (Trasero 2.5 m), pintar surcos pares al trasero, guardar, `GET /api/implemento` debe devolver `trenes` y `surcos` correctos; recargar la página y ver que persiste. Consola sin errores.
- [ ] **Step 6: Deploy** (`cp` de los 4 archivos a `Build/AgroParallel/wwwroot/...`, reiniciar Engine) y commit — `feat(implemento): pantalla unificada con trenes y surco→tren; herramienta.html deprecada`

---

### Task 7: Pantallas QuantiX/SectionX en solo-lectura para trenes

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js` (selector `tren` por motor, si existe en Siembra: reemplazar por texto derivado + link)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/sectionx.js` (selector `tren` por cable: ídem)

- [ ] **Step 1:** Buscar en ambos JS los controles que editan `tren` / `distancia_entre_trenes` (grep `tren` en cada archivo). Reemplazar el control editable por: `<span>Tren: <b>{derivado}</b> · se configura en <a href="config-implemento.html">Implemento</a></span>`. El derivado sale de `GET /api/implemento` (surcos del motor/cable → tren). Si deriva en conflicto: pill de warning "surcos de trenes distintos".
- [ ] **Step 2:** `node --check` ambos, deploy a Build, reinicio, verificación visual con Playwright de las dos pestañas.
- [ ] **Step 3:** Commit — `feat(ui): tren por motor/cable pasa a solo lectura, derivado del implemento`

---

### Task 8: Verificación integral + bitácora

- [ ] **Step 1:** Suite completa: `dotnet test .../AgroParallel.Services.Tests.csproj` → todo verde.
- [ ] **Step 2:** `build.ps1` completo → deploy a `Build/`, relanzar Engine + Desktop.
- [ ] **Step 3:** Smoke en vivo: con el implemento actual (1 tren) el log del Engine debe decir "trenes: fallback por nodo" y el comportamiento de corte/dosis debe ser idéntico al de antes (mismos targets MQTT ante el mismo estado — comparar con sniffer si hay nodo encendido).
- [ ] **Step 4:** Anotar en `COORDINACION-UI.md` (bitácora): pantalla unificada, herramienta deprecada, IDs nuevos `tren*` como contrato.
- [ ] **Step 5:** Commit final. En el mensaje NO declarar cierre de tablero como "anda" — el corte con dos trenes queda `Prueba:` hasta validarse en cabina.

## Self-review (hecho al escribir)

- Cobertura de spec: pantalla única (T6), N trenes (T1/T6), derivación con conflicto→warning (T1/T4/T5), fallback fase 1 (T4/T5), validaciones (T3), regeneración de surcos (T2/T6), corte intacto (constraint global), solo-lectura en productos (T7), no-regresión (T8). Fase 2 queda explícitamente fuera.
- Tipos consistentes: `TrenResultado{TrenId,DistanciaM,Conflicto}` y `Resolver(ImplementoDto, IEnumerable<int>)` usados igual en T1/T4/T5.
- Riesgo señalado: el shape exacto de `ValidationResult` y el seam de test del adapter deben copiarse del código real al implementar (indicado en los pasos).
