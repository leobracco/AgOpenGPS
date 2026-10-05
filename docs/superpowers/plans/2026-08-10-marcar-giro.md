# Marcar giro — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** dos líneas perpendiculares a la guía, marcadas con un botón en cada extremo del lote, disparan el U-turn automático sin recorrer el lindero (spec: `docs/superpowers/specs/2026-08-10-marcar-giro-design.md`).

**Architecture:** las marcas se materializan como un `CBoundaryList` VIRTUAL en `Bnd.bndList` (rectángulo sintético sin lindero; recorte de la `turnLine` real con lindero). Así `CYouTurn`/`CYouTurnUpdater` corren SIN tocarlos — los tres guards (bndList.Count, IsPointInsideTurnArea, ToggleYouTurn) pasan solos. Un flag `isVirtualTurnBoundary` excluye el lindero virtual de guardado, snapshot y dibujo; las marcas se dibujan aparte.

**Tech Stack:** C# net9 (AgOpenGPS.Core + PilotX.GuidanceEngine.Core + adapters), Avalonia (PilotX.UI), NUnit (AgOpenGPS.Core.Tests).

## Global Constraints

- Sin marcas ⇒ CERO cambio de comportamiento (garantía "no rompe nada").
- El lindero virtual NUNCA se persiste en `Boundary.txt` ni aparece como boundary en el snapshot.
- Textos UI en castellano + entradas en `idiomas.json`; branding PilotX (no AOG).
- Commits frecuentes; `dotnet test` de `AgOpenGPS.Core.Tests` y `AgroParallel.Services.Tests` verdes antes de cada commit.
- `ayuda.html` se actualiza EN EL MISMO commit que agrega el botón (Task 6).
- Comentarios de código nuevos en castellano rioplatense.

---

### Task 1: Geometría pura `CTurnMarks` (Core) + tests

**Files:**
- Create: `SourceCode/AgOpenGPS.Core/Classes/CTurnMarks.cs`
- Test: `SourceCode/AgOpenGPS.Core.Tests/TurnMarksTests.cs` (proyecto NUnit existente)

**Interfaces:**
- Produces:
  - `class TurnMark { public double easting, northing, heading; }` (heading = rumbo de la GUÍA al marcar, rad)
  - `static class CTurnMarks`:
    - `static List<vec3> BuildVirtualFence(TurnMark a, TurnMark b, double halfWidthM = 500.0)` → rectángulo CERRADO (winding lo arregla el caller con `CalculateFenceArea`). Los lados "marca" pasan por cada punto con dirección perpendicular al heading de su marca; los laterales van a ±halfWidthM del eje.
    - `static List<vec3> ClipRingWithHalfPlane(List<vec3> ring, vec3 pointOnLine, double lineHeading, vec2 keepSidePoint)` → Sutherland-Hodgman contra UNA recta (la marca): conserva el lado donde cae `keepSidePoint` (el centro del lote). Devuelve anillo cerrado; lista vacía si el recorte vacía el polígono.

- [ ] **Step 1: test que falla** — crear `TurnMarksTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using AgOpenGPS;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    public class TurnMarksTests
    {
        // Guía apuntando al norte (heading 0). Marca A en n=0, marca B en n=100.
        private static TurnMark MarkA() => new TurnMark { easting = 0, northing = 0, heading = 0 };
        private static TurnMark MarkB() => new TurnMark { easting = 0, northing = 100, heading = 0 };

        [Test]
        public void RectanguloVirtual_ContieneElCentro_YNoLosExtremos()
        {
            var ring = CTurnMarks.BuildVirtualFence(MarkA(), MarkB(), 500);
            Assert.That(ring.Count, Is.GreaterThanOrEqualTo(5)); // 4 esquinas + cierre
            Assert.That(ring.IsPointInPolygon(new vec3(0, 50, 0)), Is.True);   // centro adentro
            Assert.That(ring.IsPointInPolygon(new vec3(0, -10, 0)), Is.False); // atrás de A afuera
            Assert.That(ring.IsPointInPolygon(new vec3(0, 110, 0)), Is.False); // adelante de B afuera
            Assert.That(ring.IsPointInPolygon(new vec3(499, 50, 0)), Is.True); // ancho lateral
        }

        [Test]
        public void RecorteConSemiplano_AchicaElAnillo_DelLadoCorrecto()
        {
            // Cuadrado 0..100 y una marca horizontal en n=60: me quedo con n<60.
            var ring = new List<vec3> {
                new vec3(0,0,0), new vec3(100,0,0), new vec3(100,100,0),
                new vec3(0,100,0), new vec3(0,0,0) };
            var recortado = CTurnMarks.ClipRingWithHalfPlane(
                ring, new vec3(0, 60, 0), lineHeading: Math.PI / 2, // línea este-oeste
                keepSidePoint: new vec2(50, 10));
            Assert.That(recortado.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(recortado.IsPointInPolygon(new vec3(50, 50, 0)), Is.True);
            Assert.That(recortado.IsPointInPolygon(new vec3(50, 70, 0)), Is.False);
        }

        [Test]
        public void RecorteQueVaciaElPoligono_DevuelveVacio()
        {
            var ring = new List<vec3> {
                new vec3(0,0,0), new vec3(10,0,0), new vec3(10,10,0),
                new vec3(0,10,0), new vec3(0,0,0) };
            var r = CTurnMarks.ClipRingWithHalfPlane(
                ring, new vec3(0, -50, 0), Math.PI / 2, new vec2(5, -100));
            Assert.That(r, Is.Empty);
        }
    }
}
```

- [ ] **Step 2: correr y ver que falla** — `dotnet test SourceCode/AgOpenGPS.Core.Tests -v q --filter TurnMarks` → FAIL (CTurnMarks no existe).
- [ ] **Step 3: implementar `CTurnMarks.cs`** (completo):

```csharp
// ============================================================================
// CTurnMarks.cs — geometría PURA de "Marcar giro": dos líneas perpendiculares
// a la guía marcadas por el operario definen dónde gira el U-turn sin lindero.
// Ver docs/superpowers/specs/2026-08-10-marcar-giro-design.md.
// Sin estado ni host: solo cuentas, para poder testearlas.
// ============================================================================
using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public class TurnMark
    {
        public double easting, northing;
        // Rumbo de la GUÍA al momento de marcar (rad). La línea de la marca es
        // PERPENDICULAR a este rumbo.
        public double heading;
    }

    public static class CTurnMarks
    {
        // Rectángulo cerrado: lados "marca" por A y B (perpendiculares al rumbo
        // de cada una), laterales a ±halfWidthM del eje de la guía. El winding
        // lo normaliza después CalculateFenceArea del CBoundaryList.
        public static List<vec3> BuildVirtualFence(TurnMark a, TurnMark b, double halfWidthM = 500.0)
        {
            var ring = new List<vec3>();
            if (a == null || b == null) return ring;

            // Eje de avance = promedio de headings (misma guía; difieren poco).
            double hdg = Math.Atan2(
                Math.Sin(a.heading) + Math.Sin(b.heading),
                Math.Cos(a.heading) + Math.Cos(b.heading));
            // heading AOG: 0 = norte, crece horario. Avance y perpendicular:
            double fx = Math.Sin(hdg), fy = Math.Cos(hdg);     // adelante (E,N)
            double px = Math.Cos(hdg), py = -Math.Sin(hdg);    // perpendicular

            // Esquinas: (A - perp*w), (A + perp*w), (B + perp*w), (B - perp*w)
            ring.Add(new vec3(a.easting - px * halfWidthM, a.northing - py * halfWidthM, 0));
            ring.Add(new vec3(a.easting + px * halfWidthM, a.northing + py * halfWidthM, 0));
            ring.Add(new vec3(b.easting + px * halfWidthM, b.northing + py * halfWidthM, 0));
            ring.Add(new vec3(b.easting - px * halfWidthM, b.northing - py * halfWidthM, 0));
            ring.Add(ring[0]); // cerrado
            return ring;
        }

        // Sutherland-Hodgman contra UNA recta (la línea de la marca). Se queda
        // con el semiplano donde cae keepSidePoint (el interior del lote): la
        // marca solo ACERCA la cabecera, nunca agranda el polígono.
        public static List<vec3> ClipRingWithHalfPlane(
            List<vec3> ring, vec3 pointOnLine, double lineHeading, vec2 keepSidePoint)
        {
            var outRing = new List<vec3>();
            if (ring == null || ring.Count < 3) return outRing;

            // Normal de la recta (perpendicular a su dirección).
            double dx = Math.Sin(lineHeading), dy = Math.Cos(lineHeading);
            double nx = dy, ny = -dx;
            double Side(double e, double n) =>
                (e - pointOnLine.easting) * nx + (n - pointOnLine.northing) * ny;
            double keep = Math.Sign(Side(keepSidePoint.easting, keepSidePoint.northing));
            if (keep == 0) keep = 1;

            // Trabajar sin el punto de cierre duplicado.
            int count = ring.Count;
            bool cerrado = ring[0].easting == ring[count - 1].easting &&
                           ring[0].northing == ring[count - 1].northing;
            int n2 = cerrado ? count - 1 : count;

            for (int i = 0; i < n2; i++)
            {
                vec3 cur = ring[i];
                vec3 nxt = ring[(i + 1) % n2];
                double sc = Side(cur.easting, cur.northing) * keep;
                double sn = Side(nxt.easting, nxt.northing) * keep;

                if (sc >= 0) outRing.Add(cur);
                if ((sc >= 0) != (sn >= 0))
                {
                    // Intersección segmento-recta.
                    double t = sc / (sc - sn);
                    outRing.Add(new vec3(
                        cur.easting + t * (nxt.easting - cur.easting),
                        cur.northing + t * (nxt.northing - cur.northing), 0));
                }
            }
            if (outRing.Count < 3) { outRing.Clear(); return outRing; }
            outRing.Add(outRing[0]); // re-cerrar
            return outRing;
        }
    }
}
```

- [ ] **Step 4: correr tests** → PASS los 3.
- [ ] **Step 5: commit** — `feat(core): CTurnMarks — geometria pura de Marcar giro (rectangulo virtual + recorte)`

---

### Task 2: flag `isVirtualTurnBoundary` + exclusiones (guardar / snapshot / stats)

**Files:**
- Modify: `SourceCode/AgOpenGPS.Core/Classes/CBoundaryList.cs` (agregar campo)
- Modify: `SourceCode/AgOpenGPS.Core/IO/BoundaryFiles.cs` (Save: saltear virtuales)
- Modify: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Cobertura.cs` — `GuardarLinderos()` (~línea 375): saltear virtuales
- Modify: `SourceCode/PilotX.GuidanceEngine/Adapters/EngineStateProvider.cs:252-295` — no emitir el virtual en `snap.Boundaries`/`snap.Headlands` ni en `BoundaryAreaM2`

**Interfaces:**
- Produces: `CBoundaryList.isVirtualTurnBoundary` (bool, default false).

- [ ] **Step 1**: en `CBoundaryList.cs`, después de `public List<vec3> turnLine ...`:

```csharp
// Lindero VIRTUAL de "Marcar giro" (rectángulo sintético o clon recortado):
// existe solo en memoria para que el U-turn tenga línea de giro. NUNCA se
// guarda a Boundary.txt, no aparece en el snapshot del mapa como lindero y
// no cuenta para el área del lote.
public bool isVirtualTurnBoundary = false;
```

- [ ] **Step 2**: grep de TODOS los save-paths de boundary antes de tocar: `grep -rn "BoundaryFiles.Save\|GuardarLinderos" SourceCode/` — en cada caller, confirmar que la lista pasada filtra `!isVirtualTurnBoundary` (filtrar en `BoundaryFiles.Save` mismo, al inicio: `boundaries = boundaries.Where(b => !b.isVirtualTurnBoundary).ToList();`).
- [ ] **Step 3**: en `EngineStateProvider.cs` (bucle ~262): `if (b.isVirtualTurnBoundary) continue;` antes de agregar a `bnds`/`hdls`; ídem en el cálculo de `BoundaryAreaM2`.
- [ ] **Step 4**: `dotnet build` de `PilotX.GuidanceEngine` + `dotnet test` de los dos proyectos de test → verdes.
- [ ] **Step 5: commit** — `feat(core): isVirtualTurnBoundary — el lindero virtual no se guarda ni se dibuja como lindero`

---

### Task 3: servicio del motor — estado, persistencia y materialización

**Files:**
- Create: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.TurnMarks.cs` (partial del host)
- Modify: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Job.cs` — `OpenField` (cargar tras boundaries) y `CloseField` (limpiar)

**Interfaces:**
- Produces (en `GuidanceEngineHost`):
  - `public readonly List<TurnMark> TurnMarks` (0..2, orden por posición sobre el eje de la guía)
  - `public bool MarcarGiroAca()` — marca en `PivotAxlePos` con el rumbo de la guía activa; decide extremo; pisa la del mismo lado; persiste; re-materializa. `false` si no hay lote o guía.
  - `public void BorrarMarcasGiro()` — limpia, persiste (borra archivo), re-materializa.
  - `private void MaterializarMarcasGiro()` — sin lindero real: agrega/reemplaza el `CBoundaryList` virtual (receta `EngineContornoService.cs:280-292`: `CalculateFenceArea` + `FixFenceLine` + `BuildTurnLines`); con lindero real: `Bnd.BuildTurnLines()` y después `ClipRingWithHalfPlane` sobre `bndList[0].turnLine` por cada marca (keepSidePoint = punto medio entre marcas o centroide del fence), re-cerrando y recalculando headings con `CalculateTurnHeadings()` (`CTurnLines.cs:7`). Si el recorte da vacío → ignorar esa marca + `Log.EventWriter`.
  - Persistencia `TurnMarks.txt` (formato de la spec) con `Load/Save` locales al partial (mismo estilo `BoundaryFiles`).

**Reglas de decisión de extremo:** proyectar el pivote sobre el eje (producto punto con el versor de avance de la guía). Si no hay marcas → primera marca. Si hay una → la nueva va al otro lado si su proyección queda del otro lado de la existente; si cae del mismo lado, PISA la existente. Si hay dos → pisa la más cercana en proyección.

**Rumbo de la guía activa:** `Trk.idx >= 0` → para AB: `ABLineField.abHeading`; para curva: heading del punto de la curva más cercano al pivote (mismo acceso que usa `BuildManualYouTurn`, `CYouTurn.cs:2550`). Si `Trk.idx == -1` → `MarcarGiroAca()` devuelve false.

- [ ] **Step 1**: escribir el partial completo (estado + IO + materialización + logs en castellano).
- [ ] **Step 2**: en `OpenField` (Job.cs, después de `Bnd.BuildTurnLines()` ~línea 83): `CargarMarcasGiro(dir); MaterializarMarcasGiro();`. En `CloseField` (junto a `Bnd.bndList.Clear()` ~157): `TurnMarks.Clear();` (el virtual se va con el clear del bndList).
- [ ] **Step 3**: build + tests verdes. Prueba manual rápida contra el engine: abrir lote sin lindero por API, `Bnd.bndList.Count == 0` → OK (sin marcas nada cambia).
- [ ] **Step 4: commit** — `feat(engine): Marcar giro — estado, TurnMarks.txt y cabecera virtual por el circuito del U-turn`

---

### Task 4: comandos + snapshot (API)

**Files:**
- Modify: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Commands.cs` — switch de `ExecuteCommand` (~línea 176)
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Models/AogStateSnapshot.cs` — campo nuevo
- Modify: `SourceCode/PilotX.GuidanceEngine/Adapters/EngineStateProvider.cs` — poblarlo

**Interfaces:**
- Produces:
  - Comandos: `"marcar_giro"` → `MarcarGiroAca()`; `"giro_borrar_marcas"` → `BorrarMarcasGiro()` (ambos devuelven bool del switch).
  - Snapshot: `[JsonPropertyName("turn_marks")] public List<TurnMarkDto> TurnMarks` con `TurnMarkDto { e, n, heading }` (snake_case AgpJson). La UI y el mapa leen de acá (endpoint existente `GET /api/aog/state`).

- [ ] **Step 1**: cases nuevos en el switch (junto a `case "uturn":`):

```csharp
case "marcar_giro":
    return MarcarGiroAca();
case "giro_borrar_marcas":
    BorrarMarcasGiro();
    return true;
```

- [ ] **Step 2**: DTO + snapshot. En `EngineStateProvider`, mapear `_host.TurnMarks` → `snap.TurnMarks` (lista vacía si no hay).
- [ ] **Step 3**: build + verificación por API: `POST /api/aog/guidance/command {"cmd":"marcar_giro"}` con lote y guía → `{"ok":true}`; `GET /api/aog/state` trae `turn_marks` con 1 entrada; `TurnMarks.txt` apareció en la carpeta del lote; reabrir lote → la marca sigue.
- [ ] **Step 4: commit** — `feat(engine): comandos marcar_giro / giro_borrar_marcas + turn_marks en el snapshot`

---

### Task 5: mapa — dibujar las dos líneas

**Files:**
- Modify: `SourceCode/PilotX.UI/Services/HudPoller.cs` (~114-119, DTO cliente): agregar `TurnMarks` (lista de `{E,N,Heading}`)
- Modify: `SourceCode/PilotX.UI/Views/MapPanel.cs` — pass-through en `OnSnapshot`
- Modify: `SourceCode/PilotX.UI/Views/MapGlSurface.cs` — estado + dibujo
- Modify: `SourceCode/PilotX.UI/Views/MapSkiaSurface.cs` — mismo dibujo en el fallback Skia (dos líneas con paint)

**Interfaces:**
- Consumes: `snap.turn_marks` del `GET /api/aog/state` (Task 4).
- Produces: método `SetTurnMarks(IReadOnlyList<(double e, double n, double heading)> marks)` en ambas surfaces, llamado por `MapPanel.OnSnapshot`.

- [ ] **Step 1**: en `MapGlSurface`: campo `_turnMarks` + `SetTurnMarks(...)` (con `RequestNextFrameRendering()`), y en el render (después de `DrawLinderoEnCurso`, ~línea 1258) dibujar cada marca como segmento de ±500 m perpendicular a su heading con `UploadAndDraw(PrimitiveType.Lines, 2, ColTurnMark)` (patrón `DrawAbCreation`, líneas 1743-1767). Color nuevo `ColTurnMark = #FF9E1B` con alpha 0.9 (mismo naranja del path de U-turn, familia visual del giro).
- [ ] **Step 2**: cache para `RehacerSurface` NO hace falta: el estado llega por `HudPoller` cada tick y se re-aplica solo — verificar leyendo `MapPanel.RehacerSurface()` (~270-311) que `OnSnapshot` siga fluyendo tras recrear; si el snapshot no se re-push-ea, agregar `_ultimasMarcas` al cache igual que `_ultimoShape`.
- [ ] **Step 3**: build, correr PilotX en banco: marcar por API y ver las dos líneas naranjas en el mapa (GL y con `--gl=off` Skia).
- [ ] **Step 4: commit** — `feat(mapa): lineas de Marcar giro en GL y Skia`

---

### Task 6: botones en la barra de la pasada + ayuda + idiomas

**Files:**
- Modify: `SourceCode/PilotX.UI/MainWindow.axaml` — `StackPanel` del `NudgeOverlay` (línea 306), después del bloque `BtnOvGuias` (309-316)
- Modify: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraDerechaViewModel.cs` — propiedad `[ObservableProperty] bool _hayMarcasGiro` (alimenta visibilidad de "Borrar")
- Modify: `SourceCode/PilotX.UI/MainWindow.axaml.cs` — alimentar `HayMarcasGiro` desde el HudPoller; toast de feedback al marcar
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html` — sección del giro automático: cómo marcar/borrar (ruta: Barra de la pasada › Marcar)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/idiomas.json` — "Marcar" / "Borrar marcas" / tooltips en en/pt

**XAML (después de BtnOvGuias):**

```xml
<Button x:Name="BtnOvMarcarGiro" Width="64" Height="56"
        Command="{Binding SendCommand}" CommandParameter="marcar_giro"
        ToolTip.Tip="Marcar giro: línea perpendicular a la guía acá, el U-turn gira ahí">
  <StackPanel>
    <Image Source="avares://PilotX.Cockpit.Bars/Assets/barra-abajo/YouTurnU.png" Height="26"/>
    <TextBlock Classes="cap" Text="Marcar"/>
  </StackPanel>
</Button>
<Button x:Name="BtnOvBorrarMarcas" Width="64" Height="56"
        Command="{Binding SendCommand}" CommandParameter="giro_borrar_marcas"
        IsVisible="{Binding HayMarcasGiro}"
        ToolTip.Tip="Borrar las marcas de giro">
  <StackPanel>
    <Image Source="avares://PilotX.Cockpit.Bars/Assets/barra-abajo/YouTurnU.png" Height="26" Opacity="0.5"/>
    <TextBlock Classes="cap" Text="Borrar"/>
  </StackPanel>
</Button>
```

(ícono: si el handoff de íconos trae uno de "marcar giro", usarlo; si no, YouTurnU existente. Verificar el nombre real del asset en `PilotX.Cockpit.Bars/Assets/` antes de commitear.)

- [ ] **Step 1**: XAML + propiedad VM + wiring del HudPoller (`_vmDer.HayMarcasGiro = snap.TurnMarks?.Count > 0`).
- [ ] **Step 2**: los comandos NO se rutean local (RouteCockpitCommand no los conoce → siguen por HTTP al engine, que es lo que queremos). Verificar con el engine corriendo que el botón marca y el mapa dibuja. Toast al marcar: en el poller, si el count de marcas sube, `MostrarToast(Traductor.T("Marca de giro puesta"))`.
- [ ] **Step 3**: `ayuda.html` (misma sección donde se explica el U-turn / Giro) + `idiomas.json` (claves: "Marcar", "Borrar", "Marca de giro puesta", tooltips). Verificar la RUTA contra el XAML real antes de escribirla.
- [ ] **Step 4**: `UbicarNudgeOverlay` (MainWindow.axaml.cs:2989): correr en pantalla y verificar que la barra no se pasa del borde en 1080×720 (el clamp existente lo maneja; mirar que "Centrar" siga alineado).
- [ ] **Step 5: commit** — `feat(ui): botones Marcar giro / Borrar en la barra de la pasada (+ayuda, +idiomas)`

---

### Task 7: validación en banco + tablero

- [ ] ModSim/banco, lote SIN lindero: abrir lote, guía AB, marcar los dos extremos, U-turn ON → el path del giro se arma llegando a la marca y el tractor (sim) dobla en la línea, en ambos sentidos. Verificar distancia configurada de U-turn respetada.
- [ ] Lote CON lindero: marcar un extremo adentro → el giro pasa a la marca en ese extremo; el otro sigue en el lindero. Marca afuera del lindero → se ignora y queda logueado.
- [ ] Persistencia: cerrar/reabrir lote → marcas y giro siguen. `Boundary.txt` NO cambió (diff).
- [ ] Sin marcas: comportamiento idéntico a hoy (regresión rápida de U-turn con lindero).
- [ ] Deploy a `Build\` (publish engine + copiar wwwroot) y probar en la pantalla.
- [ ] Tablero: recién después de probarse en banco/cabina — `Prueba: YouTurnU` en el commit final si aplica (no inventar `anda`).

## Self-review

- Spec coverage: uso/marcar/pisar/borrar (T3+T6), cabecera virtual sin lindero (T1+T3), recorte con lindero (T1+T3), persistencia+sync (T3; OrbitX levanta el archivo por estar en la carpeta), API (T4 — por command+state en vez de endpoints nuevos: menos superficie), mapa (T5), botón+ayuda (T6), validación (T7), riesgos (recorte vacío → ignora+log en T3; cambio de rumbo de guía >20° — se resuelve en `MaterializarMarcasGiro` comparando heading de marca vs guía activa y logueando, T3).
- Sin placeholders: el código de T1 está completo; T3 define reglas exactas (extremo, rumbo, persistencia); T5/T6 citan patrones con líneas.
- Consistencia de nombres: `TurnMark`, `CTurnMarks`, `isVirtualTurnBoundary`, `MarcarGiroAca`, `BorrarMarcasGiro`, `MaterializarMarcasGiro`, comandos `marcar_giro`/`giro_borrar_marcas`, snapshot `turn_marks` — usados igual en todas las tasks.
