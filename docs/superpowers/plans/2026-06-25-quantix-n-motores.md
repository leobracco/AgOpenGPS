# QuantiX N Motores — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permitir N motores eléctricos QuantiX (1..24, escalando a 36/48), cada uno con dosis independiente (mapa manda, si no hay cae a fija) y asignación táctil motor↔surco, con una tira de surcos unificada que sirve para configurar y para monitorear en vivo.

**Architecture:** El back-end (modelo `QxMotorConfig[]`, DTO `QxMotorConfigDto[]`, `QuantiXController`, loop del bridge `0..Motores.Length`) **ya es N-capable**: no clampea a 2. El único cambio funcional de back-end es **invertir la cascada de dosis** en `QuantiXMotorBridge` para que el mapa tenga prioridad sobre la dosis fija, extrayendo esa lógica a una función pura testeable. El grueso del trabajo es la **UI** (`quantix.html` + `quantix.js`): fusionar las pestañas Monitor y Motores en un componente "tira de surcos" con pincel táctil, toolbar (+Motor / Auto-repartir / Quitar surco), lista de motores con dosis fija+efectiva, render live (surcos grises + PPS real/obj + badges), y toggle Planter/Tabla.

**Tech Stack:** C# / .NET Framework 4.8 (bridge en `AgroParallel.Services`, netstandard), NUnit 4 (tests del bridge), HTML/CSS/Vanilla JS (UI en WebView2, sin framework, DOM crawling con `data-*`), MQTT (MQTTnet, contrato `agp/quantix/{uid}/target` sin cambios).

**Scope guard:** Firmware NO se toca en este plan (diferido). El contrato MQTT (`{"id":N,"pps":...,"seccion_on":...}`) no cambia. Granularidad de corte = motor (ya implementada como "eje solidario": el motor frena cuando todos sus surcos están cortados) — NO se modifica.

---

## File Structure

| Archivo | Responsabilidad | Acción |
|---|---|---|
| `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxDoseResolver.cs` | Función pura: resolver dosis efectiva de un motor (manual > mapa > fija) | **Crear** |
| `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs` | Usar `QxDoseResolver` en el loop (reemplaza el bloque inline líneas 251-270) | **Modificar** |
| `SourceCode/AgOpenGPS.Tests/QuantiX/QxDoseResolverTests.cs` | Tests de la cascada de dosis | **Crear** |
| `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/quantix.html` | Título + tira de surcos + toggle Planter/Tabla + contenedores | **Modificar** |
| `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js` | Estado de surcos, render strip, pincel, toolbar, lista, live, toggle | **Modificar** |

**Nota de granularidad UI:** El JS de QuantiX (2010 líneas) no tiene harness de tests automatizado (es UI WebView2). Las tareas de UI se verifican con **checkpoint visual** (Playwright screenshot contra el host en `http://localhost:5180/pages/quantix.html` o el companion). Solo el back-end (`QxDoseResolver`) lleva tests NUnit reales (TDD).

---

### Task 1: Extraer y testear la cascada de dosis (mapa manda)

**Contexto:** Hoy `QuantiXMotorBridge.OnTick` (líneas 251-270) resuelve la dosis así: `Manual` → `DosisFija` (si >0) → `CampoDosis` → mapa global. O sea **la fija gana** sobre el mapa. El spec pide **mapa manda, si no hay cae a fija**. Extraemos la lógica a una función pura y la invertimos.

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxDoseResolver.cs`
- Create: `SourceCode/AgOpenGPS.Tests/QuantiX/QxDoseResolverTests.cs`
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs:251-270`

- [ ] **Step 1: Escribir el test que falla**

Crear `SourceCode/AgOpenGPS.Tests/QuantiX/QxDoseResolverTests.cs`:

```csharp
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgOpenGPS.Tests.QuantiX
{
    public class QxDoseResolverTests
    {
        // Helper: resuelve con CampoDosis vacío y un lookup que nunca aplica.
        private static double Resolve(
            bool manualMode, double manualDosis, double dosisFija,
            string campoDosis, double mapaGlobal,
            double campoLookup = 0)
        {
            return QxDoseResolver.Resolve(
                manualMode, manualDosis, dosisFija, campoDosis, mapaGlobal,
                _ => campoLookup);
        }

        [Test]
        public void ManualMode_overrides_everything()
        {
            // Manual gana aunque haya mapa y fija.
            double d = Resolve(true, 99, 60, "", 70);
            Assert.That(d, Is.EqualTo(99));
        }

        [Test]
        public void MapaGlobal_manda_sobre_DosisFija()
        {
            // mapa=70 y fija=60 → debe ganar el mapa (mapa manda).
            double d = Resolve(false, 0, 60, "", 70);
            Assert.That(d, Is.EqualTo(70));
        }

        [Test]
        public void Cae_a_DosisFija_cuando_no_hay_mapa()
        {
            // mapa=0 (fuera del lote) y fija=60 → usa fija.
            double d = Resolve(false, 0, 60, "", 0);
            Assert.That(d, Is.EqualTo(60));
        }

        [Test]
        public void CampoDosis_especifico_manda_sobre_fija()
        {
            // CampoDosis con lookup=55, fija=60 → gana el campo del shapefile.
            double d = Resolve(false, 0, 60, "DOSIS_A", 0, campoLookup: 55);
            Assert.That(d, Is.EqualTo(55));
        }

        [Test]
        public void Sin_mapa_ni_fija_da_cero()
        {
            double d = Resolve(false, 0, 0, "", 0);
            Assert.That(d, Is.EqualTo(0));
        }
    }
}
```

- [ ] **Step 2: Correr el test para verificar que falla por compilación**

Run (desde `SourceCode/`):
```bash
dotnet test AgOpenGPS.Tests/AgOpenGPS.Tests.csproj --filter QxDoseResolverTests
```
Expected: FALLA al compilar — `QxDoseResolver` no existe.

- [ ] **Step 3: Crear `QxDoseResolver` con la cascada invertida**

Crear `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxDoseResolver.cs`:

```csharp
using System;

namespace AgroParallel.QuantiX
{
    /// <summary>
    /// Resuelve la dosis efectiva (kg/ha o L/ha) de un motor QuantiX.
    /// Cascada (spec N motores): Manual > Mapa > Fija.
    ///   1) ManualMode → ManualDosis (override total del widget en pantalla).
    ///   2) Mapa: si CampoDosis está seteado, el valor del shapefile (DBF) de
    ///      ese campo; si no, el mapa global del tick (mapaGlobal).
    ///   3) Si el mapa no aporta dosis (&lt;= 0) → DosisFija (default fuera de mapa).
    /// </summary>
    public static class QxDoseResolver
    {
        /// <param name="campoLookup">
        /// Función que devuelve la dosis del campo DBF dado su nombre
        /// (típicamente IAogStateProvider.GetShapeFieldDose). Solo se invoca
        /// cuando CampoDosis no está vacío.
        /// </param>
        public static double Resolve(
            bool manualMode,
            double manualDosis,
            double dosisFija,
            string campoDosis,
            double mapaGlobal,
            Func<string, double> campoLookup)
        {
            if (manualMode)
                return manualDosis;

            // Mapa manda: campo específico del motor, o mapa global del tick.
            double mapa = !string.IsNullOrEmpty(campoDosis)
                ? (campoLookup != null ? campoLookup(campoDosis) : 0)
                : mapaGlobal;

            if (mapa > 0)
                return mapa;

            // Sin mapa → cae a la dosis fija configurada.
            return dosisFija;
        }
    }
}
```

- [ ] **Step 4: Correr los tests para verificar que pasan**

Run:
```bash
dotnet test AgOpenGPS.Tests/AgOpenGPS.Tests.csproj --filter QxDoseResolverTests
```
Expected: PASS (5 tests).

- [ ] **Step 5: Cablear el resolver en el bridge**

En `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs`, reemplazar el bloque de líneas 251-270 (desde `double dosisEfectiva;` hasta el cierre del `else`) por:

```csharp
                        // Dosis efectiva: Manual > Mapa > Fija (ver QxDoseResolver).
                        // Antes la DosisFija ganaba sobre el mapa; ahora "mapa manda".
                        // 'dosis' es el mapa global del tick (0 si fuera del lote/sin vel).
                        double dosisEfectiva = QxDoseResolver.Resolve(
                            motor.ManualMode,
                            motor.ManualDosis,
                            motor.DosisFija,
                            motor.CampoDosis,
                            dosis,
                            campo => _state.GetShapeFieldDose(campo));
```

- [ ] **Step 6: Compilar la solución y correr toda la suite**

Run (desde `SourceCode/`):
```bash
dotnet build AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj
dotnet test AgOpenGPS.Tests/AgOpenGPS.Tests.csproj --filter QxDoseResolverTests
```
Expected: build OK, 5 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxDoseResolver.cs \
        SourceCode/AgOpenGPS.Tests/QuantiX/QxDoseResolverTests.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs
git commit -m "feat(quantix): cascada de dosis 'mapa manda' extraída a QxDoseResolver"
```

---

### Task 2: Título + scaffold de la tira de surcos en el HTML

**Contexto:** `quantix.html` tiene 6 tabs (`#tabMonitor` líneas 304-315, `#tabMotores` líneas 318-331). Fusionamos Monitor+Motores en una sola pestaña "Siembra" con la tira de surcos unificada. El `<style>` está en líneas 10-284 con variables `--agp-accent` (verde), etc. El subtítulo "Control PID · 2 motores por nodo" está en la línea 290.

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/quantix.html`

- [ ] **Step 1: Cambiar el subtítulo**

Reemplazar (línea ~290) `Control PID · 2 motores por nodo` por `Siembra · N motores · dosis y corte por surco`.

- [ ] **Step 2: Renombrar la tab Monitor → Siembra y eliminar la tab Motores**

En la barra de tabs (líneas 294-301), reemplazar los dos `<div class="tab" data-tab="monitor">` y `data-tab="motores"` por una sola: `<div class="tab active" data-tab="siembra">Siembra</div>`. Conservar Shape, PID, Calibración, Prueba.

- [ ] **Step 3: Reemplazar los contenedores `#tabMonitor` y `#tabMotores` por `#tabSiembra`**

Reemplazar todo el bloque de líneas 304-331 por:

```html
<div id="tabSiembra">
  <div class="card">
    <div class="qx-head">
      <span class="qx-title" id="qxModeLabel">Configurar</span>
      <div class="seg">
        <button id="segPlanter" class="on" data-view="planter">Planter</button>
        <button id="segTabla" data-view="tabla">Tabla</button>
      </div>
    </div>

    <!-- Tira de surcos (config + live, mismo objeto) -->
    <div class="planter-wrap">
      <div class="planter-cap">
        <span id="planterCapL">Sembradora · color = motor</span>
        <span id="planterCapR">tocá un surco para pintarlo con el motor activo</span>
      </div>
      <div class="strip" id="qxStrip"></div>
    </div>

    <!-- Toolbar (solo en Configurar) -->
    <div class="qx-tools" id="qxTools">
      <span class="brushchip" id="qxBrush"><span class="sw"></span>Pincel: —</span>
      <button class="btn accent" id="btnAddMotor">+ Motor</button>
      <button class="btn" id="btnAutoReparto">Auto-repartir</button>
      <button class="btn warn" id="btnQuitarSurco">Quitar surco</button>
      <span id="qxOrphanWarn" class="pill warn" style="display:none"></span>
    </div>

    <!-- Lista de motores (config: dosis fija + efectiva / live: pps + badge) -->
    <div class="mlist" id="qxMotorList"></div>

    <!-- Vista Tabla (densa) -->
    <table class="dense" id="qxTabla" style="display:none"></table>

    <div class="qx-actions">
      <button class="btn" id="btnSaveMotores">Guardar</button>
      <button class="btn" id="btnSendAll">Enviar a nodos</button>
      <div id="mtMsg" class="msg"></div>
    </div>
  </div>
</div>
```

- [ ] **Step 4: Agregar el CSS de la tira al `<style>`**

Antes del cierre `</style>` (línea ~284), agregar:

```css
.qx-head{display:flex;align-items:center;gap:10px;margin-bottom:10px;flex-wrap:wrap}
.qx-title{color:var(--agp-text);font-weight:700;font-size:15px}
.seg{display:flex;background:var(--agp-bg-soft);border:1px solid var(--agp-border);border-radius:8px;overflow:hidden;margin-left:auto}
.seg button{background:transparent;border:0;color:var(--agp-text-muted);padding:8px 14px;font-size:13px;font-weight:600;cursor:pointer}
.seg button.on{background:#1c2a1e;color:var(--agp-accent)}
.planter-wrap{background:var(--agp-bg-soft);border:1px solid var(--agp-border);border-radius:10px;padding:10px}
.planter-cap{display:flex;justify-content:space-between;color:var(--agp-text-muted);font-size:11px;margin-bottom:6px;text-transform:uppercase;letter-spacing:.04em}
.strip{display:flex;gap:3px;flex-wrap:wrap}
.cell{width:24px;height:36px;border-radius:4px;font-size:10px;color:#0a0f0c;display:flex;align-items:center;justify-content:center;font-weight:800;position:relative;cursor:pointer;user-select:none}
.cell.off{background:#2c352e;color:#5f6a61;box-shadow:inset 0 0 0 1px #3a443c}
.cell.orphan{background:#1a211c;color:#5f6a61;box-shadow:inset 0 0 0 1px #3a443c}
.cell.brush{outline:2px solid #fff;outline-offset:1px}
.qx-tools{display:flex;gap:8px;align-items:center;margin-top:10px;flex-wrap:wrap}
.brushchip{display:flex;align-items:center;gap:6px;background:#16201a;border:1px solid var(--agp-border);border-radius:20px;padding:6px 14px;color:var(--agp-text);font-weight:600}
.brushchip .sw{width:14px;height:14px;border-radius:4px;background:#2c352e}
.mlist{margin-top:12px;display:flex;flex-direction:column;gap:6px}
.mrow{display:flex;align-items:center;gap:10px;background:#121a14;border:1px solid var(--agp-border);border-radius:9px;padding:10px 11px}
.mrow.sel{border-color:var(--agp-accent);box-shadow:0 0 0 1px var(--agp-accent) inset}
.mrow .sw{width:16px;height:16px;border-radius:5px;flex:0 0 auto}
.mrow .nm{width:90px;color:var(--agp-text);font-weight:700}
.mrow .cnt{color:var(--agp-text-muted);font-size:11px;width:120px}
.dosebox input{width:64px;background:var(--agp-bg-soft);border:1px solid var(--agp-border);border-radius:6px;color:var(--agp-accent);padding:6px;font-size:13px;text-align:right}
.eff{margin-left:6px;font-size:11px;padding:3px 8px;border-radius:10px}
.eff.mapa{background:#13233a;color:#5fb0e0}.eff.fija{background:#2a2418;color:#e0a33e}
.pps{margin-left:auto;color:#9fb0a4;font-variant-numeric:tabular-nums}
.bar{width:80px;height:6px;background:var(--agp-bg-soft);border-radius:4px;overflow:hidden;margin-left:8px}
.bar>i{display:block;height:100%;background:var(--agp-accent)}
.bar.warn>i{background:#e0a33e}
.badge{font-size:10px;padding:3px 7px;border-radius:8px;background:#2c352e;color:#9fb0a4;margin-left:8px}
.badge.cut{background:#3a2230;color:#e08bb0}.badge.dev{background:#3a2f18;color:#e0a33e}
.cell-colors{--c1:#4ABA3E;--c2:#7F6BE0;--c3:#E0A33E;--c4:#3E9BE0;--c5:#E06B8B;--c6:#46C5B0}
table.dense{width:100%;border-collapse:collapse;font-size:12px}
table.dense th{text-align:left;color:var(--agp-text-muted);font-weight:600;padding:6px;border-bottom:1px solid var(--agp-border)}
table.dense td{padding:6px;border-bottom:1px solid #1d271f;color:var(--agp-text)}
```

- [ ] **Step 5: Verificación visual (checkpoint)**

Abrir `http://localhost:5180/pages/quantix.html` con Playwright (o el companion) y tomar screenshot. Expected: la pestaña "Siembra" muestra el header con toggle Planter/Tabla, el contenedor de la tira vacío, la toolbar y la lista vacía (el JS aún no la puebla). Sin errores de consola por CSS.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/quantix.html
git commit -m "feat(quantix-ui): scaffold tira de surcos unificada + toggle Planter/Tabla"
```

---

### Task 3: Estado de surcos + render de la tira (modo Configurar)

**Contexto:** `loadMotores()` (quantix.js 317-326) guarda la config en `state.motoresCfg.nodos[].motores[]`. Cada motor tiene `cortes[]` (1-based) = surcos asignados. Construimos el modelo de surcos derivando el total del implemento (máximo corte configurado o secciones de PilotX) y un índice surco→motor.

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Agregar estado de surcos y helpers de color**

Cerca del bloque de estado inicial (donde se define `state`, arriba de `loadMotores`), agregar:

```javascript
// --- Estado de la tira de surcos (Tarea 3-8) ---
state.brushMotor = 0;        // índice del motor activo (pincel)
state.siembraView = 'planter'; // 'planter' | 'tabla'

const MOTOR_COLORS = ['#4ABA3E','#7F6BE0','#E0A33E','#3E9BE0','#E06B8B','#46C5B0'];
function motorColor(idx){ return MOTOR_COLORS[idx % MOTOR_COLORS.length]; }

// Nodo activo (config plana: 1 nodo lógico de N motores). Si hay varios nodos
// físicos, se opera sobre el primero habilitado; el resto conserva su config.
function activeNodo(){
  const ns = (state.motoresCfg && state.motoresCfg.nodos) || [];
  return ns.find(n => n.habilitado) || ns[0] || null;
}

// Total de surcos = máx(corte asignado, nº de secciones de PilotX, nº motores).
function totalSurcos(){
  const nodo = activeNodo();
  let max = state.aogSections ? state.aogSections.length : 0;
  if (nodo) nodo.motores.forEach(m => (m.cortes||[]).forEach(c => { if (c > max) max = c; }));
  return Math.max(max, 1);
}

// Índice surco(1-based) → motorIdx (o -1 si huérfano).
function surcoOwner(nodo, surco){
  for (let i = 0; i < nodo.motores.length; i++)
    if ((nodo.motores[i].cortes||[]).indexOf(surco) >= 0) return i;
  return -1;
}
```

- [ ] **Step 2: Escribir `renderStrip()` (modo Configurar)**

Agregar la función:

```javascript
function renderStrip(){
  const el = document.getElementById('qxStrip');
  if (!el) return;
  const nodo = activeNodo();
  if (!nodo){ el.innerHTML = '<span class="msg">No hay nodos QuantiX configurados</span>'; return; }
  el.classList.add('cell-colors');
  const total = totalSurcos();
  let html = '';
  for (let s = 1; s <= total; s++){
    const owner = surcoOwner(nodo, s);
    if (owner < 0){
      html += `<div class="cell orphan" data-surco="${s}">${s}</div>`;
    } else {
      const brush = owner === state.brushMotor ? ' brush' : '';
      html += `<div class="cell${brush}" data-surco="${s}" `
            + `style="background:${motorColor(owner)}">${s}</div>`;
    }
  }
  el.innerHTML = html;
}
```

- [ ] **Step 3: Cablear `renderStrip` al cambio de pestaña y carga**

En `showTab(name)` (quantix.js 125-142), donde hoy llama `renderMotores()` al entrar a la tab de config, reemplazar por la entrada a `siembra`: cuando `name === 'siembra'`, llamar `renderSiembra()` (que renderiza strip + lista, ver Tarea 6). Por ahora, agregar dentro del handler de `siembra`:

```javascript
    if (name === 'siembra'){
      renderStrip();
    }
```

Y al final de `loadMotores()` (después de guardar `state.motoresCfg`), agregar `if (state.activeTab === 'siembra') renderStrip();`.

- [ ] **Step 4: Verificación visual (checkpoint)**

Recargar `quantix.html`, entrar a "Siembra". Expected: la tira muestra una celda por surco coloreada por su motor (Producto 1 = verde con surcos 1-7 por default), surcos sin asignar en gris "orphan". Screenshot.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): render de la tira de surcos coloreada por motor"
```

---

### Task 4: Pincel táctil — asignar surco↔motor por tap/drag

**Contexto:** El pincel es el motor activo (`state.brushMotor`). Tap o arrastre sobre la tira asigna esos surcos al motor activo y los quita del motor anterior (un surco = un solo motor). Sin teclado (operario táctil).

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Escribir la lógica de pintado**

```javascript
// Asigna 'surco' al motor del pincel, quitándolo de cualquier otro motor.
function paintSurco(surco){
  const nodo = activeNodo();
  if (!nodo || state.brushMotor >= nodo.motores.length) return;
  nodo.motores.forEach((m, i) => {
    m.cortes = (m.cortes||[]).filter(c => c !== surco);
    if (i === state.brushMotor && m.cortes.indexOf(surco) < 0){
      m.cortes.push(surco); m.cortes.sort((a,b)=>a-b);
    }
  });
  state.dirty = true;
}
```

- [ ] **Step 2: Cablear los eventos de tap/drag en la tira**

Después de definir `renderStrip`, agregar el binding de eventos (delegación, una sola vez). En la función de init de la página (donde se cablean los demás botones), agregar:

```javascript
(function bindStripPaint(){
  const strip = document.getElementById('qxStrip');
  if (!strip || strip._painted) return;
  strip._painted = true;
  let painting = false;
  const cellSurco = (t) => t && t.classList && t.classList.contains('cell')
      ? parseInt(t.getAttribute('data-surco'), 10) : NaN;
  function apply(t){
    const s = cellSurco(t);
    if (!isNaN(s)){ paintSurco(s); renderStrip(); renderMotorList(); }
  }
  strip.addEventListener('pointerdown', e => {
    if (state.siembraView !== 'planter' || state.activeTab !== 'siembra') return;
    painting = true; apply(e.target); strip.setPointerCapture(e.pointerId);
  });
  strip.addEventListener('pointermove', e => {
    if (!painting) return;
    apply(document.elementFromPoint(e.clientX, e.clientY));
  });
  strip.addEventListener('pointerup',   () => { painting = false; });
  strip.addEventListener('pointercancel',() => { painting = false; });
})();
```

> Nota: `renderMotorList()` se define en la Tarea 6. Si se ejecuta esta tarea antes, dejar un stub `function renderMotorList(){}` temporal y completarlo en la Tarea 6 (NO commitear el stub solo; las Tareas 4 y 6 pueden agruparse en un mismo checkpoint si se ejecutan secuencialmente).

- [ ] **Step 3: Verificación visual (checkpoint)**

Entrar a "Siembra", tocar un motor en la lista para fijarlo como pincel (Tarea 6) — para esta tarea, setear `state.brushMotor = 1` por consola y arrastrar sobre surcos 5-8: deben pasar al color del Motor 2 y salir del Motor 1. Screenshot antes/después.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): pincel táctil para asignar surcos al motor activo"
```

---

### Task 5: Toolbar — +Motor, Quitar surco, Auto-repartir

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Implementar las tres acciones**

```javascript
function addMotor(){
  const nodo = activeNodo();
  if (!nodo) return;
  const m = defaultMotor();              // reusa el factory existente (qx.js ~328)
  m.nombre = 'Motor ' + (nodo.motores.length + 1);
  m.cortes = [];
  nodo.motores.push(m);
  state.brushMotor = nodo.motores.length - 1;
  state.dirty = true;
  renderStrip(); renderMotorList();
}

// Quita el surco del pincel actual (queda huérfano). Usa el último surco
// tocado; si no hay, no hace nada (UI: el operario toca un surco y luego Quitar).
function quitarSurco(){
  const s = state.lastTouchedSurco;
  if (!s) return;
  const nodo = activeNodo();
  nodo.motores.forEach(m => { m.cortes = (m.cortes||[]).filter(c => c !== s); });
  state.dirty = true;
  renderStrip(); renderMotorList();
}

// Auto-repartir: 'uno' = 1 motor por surco (crea motores si faltan);
// 'grupos' = reparte los surcos existentes en N grupos parejos entre los motores.
function autoReparto(modo){
  const nodo = activeNodo();
  if (!nodo) return;
  const total = totalSurcos();
  if (modo === 'uno'){
    while (nodo.motores.length < total){
      const m = defaultMotor(); m.nombre = 'Motor ' + (nodo.motores.length+1); m.cortes=[];
      nodo.motores.push(m);
    }
    nodo.motores.forEach((m,i)=>{ m.cortes = (i < total) ? [i+1] : []; });
  } else { // 'grupos' parejos entre los motores actuales
    const n = nodo.motores.length;
    nodo.motores.forEach(m => m.cortes = []);
    for (let s = 1; s <= total; s++){
      const g = Math.floor((s-1) * n / total);
      nodo.motores[g].cortes.push(s);
    }
  }
  state.dirty = true;
  renderStrip(); renderMotorList();
}
```

- [ ] **Step 2: Registrar `lastTouchedSurco` en el pintado**

En `paintSurco(surco)` (Tarea 4), agregar al final: `state.lastTouchedSurco = surco;`. Y en el `apply()` del binding también setearlo en taps que no pintan (cuando ya es del pincel).

- [ ] **Step 3: Cablear los botones de la toolbar**

En el init de la página, agregar:

```javascript
document.getElementById('btnAddMotor')   ?.addEventListener('click', addMotor);
document.getElementById('btnQuitarSurco')?.addEventListener('click', quitarSurco);
document.getElementById('btnAutoReparto')?.addEventListener('click', () => {
  // Toggle simple: si nº motores < surcos, ofrecer "uno por surco"; si no, "grupos".
  const nodo = activeNodo(); if (!nodo) return;
  autoReparto(nodo.motores.length < totalSurcos() ? 'uno' : 'grupos');
});
```

- [ ] **Step 4: Verificación visual (checkpoint)**

"+ Motor" agrega una fila nueva y la fija como pincel. "Auto-repartir" con 24 surcos y 3 motores → reparte 8/8/8. "Quitar surco" tras tocar un surco lo deja gris. Screenshot de cada uno.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): toolbar +Motor / Quitar surco / Auto-repartir"
```

---

### Task 6: Lista de motores (Configurar) — dosis fija editable + efectiva

**Contexto:** Reemplaza la `renderMotorCfg()` por fila por motor en la tira. Cada fila: color, nombre, resumen de surcos, input de dosis fija (abre el teclado HTML propio vía `/js/keyboard.js`, que se engancha por `focus` en inputs), pastilla de dosis efectiva (mapa/fija). Tocar la fila la fija como pincel.

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Escribir `renderMotorList()` (modo Configurar)**

```javascript
function fmtCortes(cortes){
  if (!cortes || !cortes.length) return 'sin surcos';
  return 'surcos ' + cortes.slice().sort((a,b)=>a-b).join(',');
}

function renderMotorList(){
  const el = document.getElementById('qxMotorList');
  if (!el) return;
  const nodo = activeNodo();
  if (!nodo){ el.innerHTML=''; return; }

  // Si estamos en vivo, delega al render live (Tarea 7).
  if (state.siembraEnMarcha){ renderMotorListLive(); return; }

  el.innerHTML = nodo.motores.map((m, i) => {
    const sel = i === state.brushMotor ? ' sel' : '';
    const efClass = m.campo_dosis ? 'mapa' : 'fija';
    const efTxt = m.campo_dosis ? ('mapa ' + (m.campo_dosis)) : 'fija';
    return `<div class="mrow${sel}" data-mi="${i}">
      <span class="sw" style="background:${motorColor(i)}"></span>
      <span class="nm">${m.nombre||('Motor '+(i+1))}</span>
      <span class="cnt">${fmtCortes(m.cortes)}</span>
      <span class="dosebox"><input type="number" step="0.1" data-mi="${i}"
        class="qxDosisFija" value="${(m.dosis_fija||0).toFixed(1)}"> <span class="u">kg/ha</span></span>
      <span class="eff ${efClass}">${efTxt}</span>
    </div>`;
  }).join('');

  // Tap a la fila = fijar pincel.
  el.querySelectorAll('.mrow').forEach(row => {
    row.addEventListener('click', e => {
      if (e.target.classList.contains('qxDosisFija')) return; // no robar el foco al input
      state.brushMotor = parseInt(row.getAttribute('data-mi'), 10);
      updateBrushChip(); renderStrip(); renderMotorList();
    });
  });
  // Editar dosis fija.
  el.querySelectorAll('.qxDosisFija').forEach(inp => {
    inp.addEventListener('change', () => {
      const i = parseInt(inp.getAttribute('data-mi'), 10);
      nodo.motores[i].dosis_fija = parseFloat(inp.value) || 0;
      state.dirty = true;
      renderMotorList();
    });
  });
}

function updateBrushChip(){
  const chip = document.getElementById('qxBrush');
  const nodo = activeNodo();
  if (!chip || !nodo) return;
  const m = nodo.motores[state.brushMotor];
  chip.querySelector('.sw').style.background = motorColor(state.brushMotor);
  chip.lastChild.textContent = 'Pincel: ' + (m ? (m.nombre||('Motor '+(state.brushMotor+1))) : '—');
}
```

- [ ] **Step 2: Crear `renderSiembra()` orquestadora y enchufarla a `showTab`**

```javascript
function renderSiembra(){
  renderStrip();
  renderMotorList();
  updateBrushChip();
  renderTabla();           // Tarea 8 (no-op si la vista es planter)
  updateOrphanWarn();      // Tarea 9
}
```

Reemplazar la llamada `renderStrip()` agregada en la Tarea 3 Step 3 por `renderSiembra()`.

- [ ] **Step 3: Verificación visual (checkpoint)**

Entrar a "Siembra": cada motor aparece como fila con su color, surcos, input de dosis fija y pastilla. Tocar una fila la marca como pincel (borde verde + chip "Pincel: Motor X"). Editar dosis fija persiste en `state`. Tocar el input abre el teclado HTML. Screenshot.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): lista de motores con dosis fija editable y pincel por fila"
```

---

### Task 7: Modo "En marcha" — surcos cortados grises + PPS real/obj + badges

**Contexto:** `pollLive()` (quantix.js 275-311) ya poolea `GET /api/quantix/live` cada 500ms y guarda telemetría por nodo/motor (`m.ppsReal`, `m.ppsTarget`). En vivo: surcos cortados (sección OFF) se pintan grises; cada motor muestra PPS real/objetivo, barra de cumplimiento y badge OK/corte/desvío. El mismo strip, sin pincel.

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Determinar el estado "en marcha"**

Definir el flag a partir del estado de PilotX que ya se consulta (secciones activas / job started). Agregar:

```javascript
// En marcha = hay job y alguna sección activa (telemetría live).
function computeEnMarcha(){
  state.siembraEnMarcha = !!(state.aogJobStarted &&
      state.liveByUid && Object.keys(state.liveByUid).length > 0);
}
```

Llamar `computeEnMarcha()` dentro de `pollLive()` antes de renderizar, y actualizar el label: `document.getElementById('qxModeLabel').textContent = state.siembraEnMarcha ? 'En marcha' : 'Configurar';` y ocultar la toolbar (`qxTools.style.display = state.siembraEnMarcha ? 'none':'flex'`).

- [ ] **Step 2: `renderStrip` debe grisar surcos cortados en vivo**

Modificar `renderStrip()` (Tarea 3): cuando `state.siembraEnMarcha`, un surco cuya sección PilotX está OFF se pinta `off` (gris) en vez del color del motor. Agregar dentro del loop, antes de armar la celda:

```javascript
    const cut = state.siembraEnMarcha && state.sectionOn && state.sectionOn[s-1] === false;
    if (cut){ html += `<div class="cell off" data-surco="${s}">${s}</div>`; continue; }
```

(`state.sectionOn` se llena en `pollLive` desde el snapshot de secciones de PilotX que ya consume el monitor; si no existe aún, exponerlo en el fetch de live.)

- [ ] **Step 3: Escribir `renderMotorListLive()`**

```javascript
function renderMotorListLive(){
  const el = document.getElementById('qxMotorList');
  const nodo = activeNodo();
  if (!el || !nodo) return;
  el.innerHTML = nodo.motores.map((m, i) => {
    const live = liveMotor(nodo.uid, i);   // {ppsReal, ppsTarget} del registry
    const real = live ? live.ppsReal : 0;
    const target = live ? live.ppsTarget : 0;
    const cutAll = motorAllCut(nodo, i);    // todos sus surcos cortados
    let badge = '<span class="badge">OK</span>';
    let barClass = 'bar', pct = target>0 ? Math.min(100, real/target*100) : 0;
    if (cutAll){ badge = '<span class="badge cut">corte</span>'; pct = 0; }
    else if (target>0 && Math.abs(real-target)/target > 0.15){
      badge = '<span class="badge dev">desvío</span>'; barClass = 'bar warn';
    }
    const obj = cutAll ? '—' : target.toFixed(1);
    return `<div class="mrow" data-mi="${i}">
      <span class="sw" style="background:${motorColor(i)}"></span>
      <span class="nm">${m.nombre||('Motor '+(i+1))}</span>
      <span class="dosebox"><span class="u">obj</span> <b style="color:var(--agp-accent)">${obj}</b></span>
      <span class="pps">${real.toFixed(1)} / ${target.toFixed(1)} pps</span>
      <span class="${barClass}"><i style="width:${pct.toFixed(0)}%"></i></span>
      ${badge}
    </div>`;
  }).join('');
}

// Todos los surcos del motor están cortados (sección OFF) → motor frenado.
function motorAllCut(nodo, i){
  const cortes = nodo.motores[i].cortes || [];
  if (!cortes.length || !state.sectionOn) return false;
  return cortes.every(s => state.sectionOn[s-1] === false);
}

function liveMotor(uid, i){
  const byUid = state.liveByUid && state.liveByUid[uid];
  return byUid && byUid.motores ? byUid.motores[i] : null;
}
```

- [ ] **Step 4: Hacer que `pollLive` renderice la tira en vivo**

En `pollLive()` (275-311), donde hoy renderiza el monitor solo si `state.activeTab === 'monitor'`, cambiar la condición a `state.activeTab === 'siembra'` y llamar `computeEnMarcha(); renderSiembra();` en vez de `renderNodoMonitor()`.

- [ ] **Step 5: Verificación visual (checkpoint)**

Con un nodo emitiendo `status_live` (o mock del registry), entrar a "Siembra" con job abierto: el label dice "En marcha", la toolbar se oculta, surcos cortados grises, cada motor muestra `real / obj pps` + barra + badge (OK/desvío/corte). Screenshot.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): modo en marcha con surcos cortados, pps real/obj y badges"
```

---

### Task 8: Toggle Planter / Tabla + vista densa

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Escribir `renderTabla()`**

```javascript
function renderTabla(){
  const tbl = document.getElementById('qxTabla');
  const nodo = activeNodo();
  if (!tbl || !nodo) return;
  if (state.siembraView !== 'tabla'){ tbl.style.display='none'; return; }
  let rows = `<tr><th>Motor</th><th>Surcos</th><th>Dosis fija</th>
              <th>Efectiva</th><th>PPS</th><th>Estado</th></tr>`;
  nodo.motores.forEach((m,i)=>{
    const live = liveMotor(nodo.uid, i);
    const real = live ? live.ppsReal.toFixed(1) : '—';
    const ef = m.campo_dosis ? ('mapa '+m.campo_dosis) : ((m.dosis_fija||0).toFixed(1)+' fija');
    const estado = (state.siembraEnMarcha && motorAllCut(nodo,i)) ? '○ corte' : '● dosif.';
    rows += `<tr><td><span class="sw" style="display:inline-block;width:10px;height:10px;border-radius:2px;background:${motorColor(i)}"></span> ${m.nombre||('M'+(i+1))}</td>
      <td>${(m.cortes||[]).join(',')||'—'}</td>
      <td>${(m.dosis_fija||0).toFixed(1)}</td>
      <td>${ef}</td><td>${real}</td><td>${estado}</td></tr>`;
  });
  tbl.innerHTML = rows;
  tbl.style.display = 'table';
}
```

- [ ] **Step 2: Cablear el toggle**

En el init:

```javascript
function setSiembraView(v){
  state.siembraView = v;
  document.getElementById('segPlanter').classList.toggle('on', v==='planter');
  document.getElementById('segTabla').classList.toggle('on', v==='tabla');
  document.querySelector('.planter-wrap').style.display = v==='planter' ? 'block':'none';
  document.getElementById('qxMotorList').style.display = v==='planter' ? 'flex':'none';
  renderSiembra();
}
document.getElementById('segPlanter')?.addEventListener('click', ()=>setSiembraView('planter'));
document.getElementById('segTabla')  ?.addEventListener('click', ()=>setSiembraView('tabla'));
```

- [ ] **Step 3: Verificación visual (checkpoint)**

Toggle a "Tabla": muestra una fila por motor (Motor, Surcos, Dosis fija, Efectiva, PPS, Estado); con 24 motores la tabla scrollea. Toggle de vuelta a "Planter". Screenshot de ambas.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): vista Tabla densa y toggle Planter/Tabla"
```

---

### Task 9: Guardar N motores + aviso de surcos huérfanos + limpieza

**Contexto:** `readMotoresFromUI()` (quantix.js 554-605) hoy crawlea el DOM viejo (`.card[data-ni]`, `[data-f]`, checkboxes `[data-sec]`). Con el nuevo modelo, la fuente de verdad es `state.motoresCfg` (mutado en vivo por el pincel/toolbar/inputs), así que el guardado serializa `state.motoresCfg` directo. Hay que avisar de surcos huérfanos antes de guardar.

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js`

- [ ] **Step 1: Escribir el chequeo de huérfanos**

```javascript
function surcosHuerfanos(){
  const nodo = activeNodo(); if (!nodo) return [];
  const total = totalSurcos(); const huerf = [];
  for (let s=1; s<=total; s++) if (surcoOwner(nodo, s) < 0) huerf.push(s);
  return huerf;
}
function updateOrphanWarn(){
  const el = document.getElementById('qxOrphanWarn');
  if (!el) return;
  const h = surcosHuerfanos();
  if (h.length){ el.style.display='inline-block'; el.textContent = h.length+' surco(s) sin motor: '+h.join(','); }
  else el.style.display='none';
}
```

Llamar `updateOrphanWarn()` dentro de `renderSiembra()` (ya agregado en Tarea 6 Step 2).

- [ ] **Step 2: Reemplazar el guardado para serializar `state.motoresCfg`**

Reemplazar el cuerpo de `saveMotores()` (607-619) por:

```javascript
async function saveMotores(){
  const h = surcosHuerfanos();
  if (h.length && !confirm('Hay '+h.length+' surco(s) sin motor asignado ('+h.join(',')+'). ¿Guardar igual?'))
    return;
  try {
    const res = await fetch('/api/quantix/motores', {
      method:'PUT', headers:{'Content-Type':'application/json'},
      body: JSON.stringify(state.motoresCfg)
    });
    const data = await res.json();
    const msg = document.getElementById('mtMsg');
    if (data.ok){ state.dirty=false; if(msg){msg.textContent='Guardado'; msg.className='msg ok';} }
    else if(msg){ msg.textContent='Error: '+(data.error||'?'); msg.className='msg err'; }
  } catch(e){ const msg=document.getElementById('mtMsg'); if(msg){msg.textContent='Error de red'; msg.className='msg err';} }
}
```

> El controller (`PutMotores`, QuantiXController.cs:78-91) deserializa a `QxMotoresConfigDto` cuyo `Motores[]` es array de tamaño libre — ya acepta N motores sin cambios.

- [ ] **Step 3: Borrar/neutralizar el código muerto del DOM viejo**

`readMotoresFromUI()` (554-605), `renderMotores()` (515-552), `renderMotorCfg()` (342-427) y `renderNodoMonitor()`/`renderMotorCard()` (173-242) quedan sin uso. Borrarlos (o, si algún otro tab los referencia, dejar solo lo referenciado). Verificar con búsqueda de cada nombre antes de borrar:

```bash
grep -n "renderMotorCfg\|readMotoresFromUI\|renderMotores\b\|renderNodoMonitor\|renderMotorCard" \
  SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
```
Borrar solo las funciones sin más referencias que su definición.

- [ ] **Step 4: Verificación end-to-end (checkpoint)**

1. Crear 24 surcos: "+ Motor" hasta 3 motores, "Auto-repartir" → 8/8/8.
2. Editar dosis fija de cada motor.
3. Dejar 1 surco huérfano (Quitar surco) → aparece el aviso.
4. Guardar → confirma huérfano → `Guardado`.
5. Recargar la página → la config vuelve con los 3 motores y sus surcos (round-trip por `quantiX_motores.json`).
Screenshot del estado final + verificar el JSON en disco tiene 3 motores.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js
git commit -m "feat(quantix-ui): guardar N motores desde state + aviso de surcos huérfanos"
```

---

## Self-Review

**Spec coverage:**
- N motores (1..24) agregar/quitar → Task 5 (+Motor), Task 9 (round-trip). ✓
- Asignación motor↔surco con pincel → Task 4. ✓
- Surcos huérfanos visibles + aviso → Task 3 (orphan cells), Task 9 (warn). ✓
- Cascada mapa>fija → Task 1 (QxDoseResolver, con tests). ✓
- En marcha: grises + pps real/obj + badges → Task 7. ✓
- Motor frena cuando todos sus surcos cortados → Task 7 (`motorAllCut`). ✓
- Bridge publica target por motor sin romper contrato → Task 1 (solo cambia el cálculo de dosis; el publish de líneas 320-336 no se toca). ✓
- Toggle Planter/Tabla, fila por motor → Task 8. ✓

**Placeholder scan:** El stub temporal `renderMotorList(){}` en Task 4 Step 2 está explícitamente marcado como temporal y resuelto en Task 6; no se commitea solo. Sin otros TODO/TBD.

**Type/nombre consistency:** `renderStrip`, `renderMotorList`, `renderMotorListLive`, `renderSiembra`, `renderTabla`, `paintSurco`, `activeNodo`, `totalSurcos`, `surcoOwner`, `motorColor`, `motorAllCut`, `liveMotor`, `state.brushMotor`, `state.siembraView`, `state.siembraEnMarcha`, `state.sectionOn`, `state.liveByUid` — usados consistentemente entre tareas. `QxDoseResolver.Resolve(...)` con la misma firma en test (Task 1 Step 1), definición (Step 3) y call site del bridge (Step 5).

**Dependencias entre tareas:** Task 1 es independiente (back-end). Tasks 2→3→4→5→6→7→8→9 son secuenciales (UI incremental). Task 4 y 6 comparten `renderMotorList` (resuelto con la nota del stub).

**Riesgos conocidos a validar en ejecución:**
- `state.aogSections`, `state.aogJobStarted`, `state.liveByUid`, `state.sectionOn` deben existir o exponerse desde `pollLive`/`loadMotores`. Verificar sus nombres reales en quantix.js al ejecutar Task 7 (la estructura `liveByUid[uid].motores[i].ppsReal/ppsTarget` debe confirmarse contra `pollLive`).
- `defaultMotor()` (qx.js ~328) se reusa en Task 5 — confirmar que produce un motor válido con todos los campos.
- El teclado HTML (`/js/keyboard.js`) se engancha por `focus`; confirmar que los nuevos `<input class="qxDosisFija">` lo disparan.
