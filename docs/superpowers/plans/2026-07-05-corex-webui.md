# CoreX Web UI — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dashboard web de CoreX (AgIO) servido por EmbedIO en 127.0.0.1:5181 con el design system de PilotX, mostrando en vivo GPS/NTRIP/MQTT/módulos y con toggles funcionales, sin tocar la lógica PGN/NTRIP/MQTT existente.

**Arquitectura:** Patrón snapshot (igual que VistaXLiveService): el timer de 1 s de FormLoop publica un DTO inmutable a `CoreXState`; el web host lo sirve en `GET /api/corex/status`. Los comandos POST se marshallean al hilo UI con `BeginInvoke` reutilizando los handlers de botones existentes. La UI vieja WinForms sigue viva como validación A/B. La extracción completa de los 4 servicios (SerialCom/UdpNetwork/NtripClient/MqttBroker) queda para un plan posterior — este plan implementa Fases 1 y 2 del spec por la vía snapshot-first, que cumple el criterio de éxito sin mover lógica.

**Tech Stack:** C# net48 WinForms (AgIO), EmbedIO 3.5.2 + AgpControllerBase/AgpJson (vía ProjectReference a `AgroParallel.WebHost`), WebView2 1.0.3856.49, JS vanilla + theme.css/layout.css/modal.js del Hub.

**Spec:** `docs/superpowers/specs/2026-07-05-corex-webui-design.md` (commit f18c0198)

**Contexto operativo (leer antes de empezar):**
- Repo: `G:\agroparallel\productos\CentriX-Spark\Software\App_PC\AgOpenGPS\`, rama `codex/pilotx-ui-new`.
- El working tree tiene MUCHOS archivos modificados ajenos (rama Codex UI). **Commitear SOLO los archivos de cada tarea, nunca `git add -A`.**
- No hay test harness para AgIO: la verificación por tarea es `dotnet build` + curl manual.
- Antes de compilar, cerrar el proceso si está corriendo: `taskkill //IM CoreX.exe //F 2>/dev/null; true` (lockea el output).
- Comando de build por tarea: `dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m` desde la raíz del repo. Esperado: `Build succeeded`.
- Idioma: comentarios y commits en castellano rioplatense.

---

### Task 1: CoreXState + DTOs del snapshot

**Files:**
- Create: `SourceCode/AgIO/Source/Classes/CoreXState.cs`

- [ ] **Step 1: Crear el archivo con el snapshot holder y los DTOs**

```csharp
using System;
using System.Collections.Generic;

namespace AgIO
{
    /// <summary>
    /// Snapshot thread-safe del estado runtime de CoreX. El hilo UI publica
    /// un DTO nuevo cada segundo (oneSecondLoopTimer) y el web host (:5181)
    /// lo lee desde sus threads. Publicar el objeto entero evita tearing:
    /// nunca se muta un DTO ya publicado.
    /// </summary>
    public sealed class CoreXState
    {
        public static readonly CoreXState Instance = new CoreXState();

        private readonly object _lock = new object();
        private CoreXStatusDto _current = new CoreXStatusDto();

        public void Publish(CoreXStatusDto dto)
        {
            if (dto == null) return;
            lock (_lock) _current = dto;
        }

        public CoreXStatusDto Snapshot()
        {
            lock (_lock) return _current;
        }
    }

    // AgpJson serializa a snake_case: Version→version, KbTotal→kb_total, etc.
    public class CoreXStatusDto
    {
        public bool Ok { get; set; } = true;
        public string Version { get; set; } = "";
        public string Profile { get; set; } = "";
        public CoreXGpsDto Gps { get; set; } = new CoreXGpsDto();
        public CoreXNtripDto Ntrip { get; set; } = new CoreXNtripDto();
        public CoreXMqttDto Mqtt { get; set; } = new CoreXMqttDto();
        public CoreXModulesDto Modules { get; set; } = new CoreXModulesDto();
    }

    public class CoreXGpsDto
    {
        public bool Alive { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class CoreXNtripDto
    {
        public bool RequiredOn { get; set; }
        public bool Connected { get; set; }
        public bool Connecting { get; set; }
        public long KbTotal { get; set; }
        public string CasterIp { get; set; } = "";
    }

    public class CoreXMqttDto
    {
        public bool Running { get; set; }
        public int Port { get; set; }
        public int Clients { get; set; }
        public long Messages { get; set; }
        public long UptimeSec { get; set; }
        public List<string> RecentTopics { get; set; } = new List<string>();
    }

    public class CoreXModulesDto
    {
        public bool SteerConfigured { get; set; }
        public bool SteerHello { get; set; }
        public bool MachineConfigured { get; set; }
        public bool MachineHello { get; set; }
        public bool ImuConfigured { get; set; }
        public bool ImuHello { get; set; }
    }
}
```

- [ ] **Step 2: Compilar**

Run: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m`
Expected: `Build succeeded` (el csproj es SDK-style, el .cs nuevo se incluye solo).

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgIO/Source/Classes/CoreXState.cs
git commit -m "feat(corex-web): CoreXState snapshot thread-safe + DTOs de status"
```

---

### Task 2: Alimentar el snapshot desde el timer de 1 s

**Files:**
- Create: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs` (método `oneSecondLoopTimer_Tick`, ~línea 347-505)

Contexto: todos los campos que necesitamos ya existen en los partials de FormLoop y se escriben mayormente en el hilo UI: `latitude`/`longitude` y `traffic` (`FormLoop.cs`/`UDP.designer.cs`), `isNTRIP_*`/`tripBytes`/`broadCasterIP` (`NTRIPComm.Designer.cs`), `_mqtt*` (`MQTT.Designer.cs`), `isConnected*`/`lastHelloGPS` (`FormLoop.cs:64` y `DoHelloAlarmLogic`). Al leerlos desde el mismo hilo UI que los escribe, no hace falta sincronización extra — solo `_mqttRecentTopics` requiere su lock existente (`_mqttLock`).

- [ ] **Step 1: Crear el partial con `UpdateCoreXSnapshot()` (y los toggles públicos que usa la Task 4)**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgIO
{
    // Alimenta CoreXState desde el hilo UI (oneSecondLoopTimer). Extracción
    // mecánica: refleja lo mismo que hoy muestran los labels de FormLoop.
    public partial class FormLoop
    {
        private void UpdateCoreXSnapshot()
        {
            List<string> topics;
            lock (_mqttLock)
            {
                topics = _mqttRecentTopics.Take(20).ToList();
            }

            CoreXState.Instance.Publish(new CoreXStatusDto
            {
                Version = Program.Version,
                Profile = RegistrySettings.profileName,
                Gps = new CoreXGpsDto
                {
                    Alive = lastHelloGPS,
                    Latitude = latitude,
                    Longitude = longitude,
                },
                Ntrip = new CoreXNtripDto
                {
                    RequiredOn = isNTRIP_RequiredOn,
                    Connected = isNTRIP_Connected,
                    Connecting = isNTRIP_Connecting,
                    KbTotal = tripBytes >> 10,
                    CasterIp = broadCasterIP ?? "",
                },
                Mqtt = new CoreXMqttDto
                {
                    Running = _mqttRunning,
                    Port = _mqttPort,
                    Clients = _mqttClientsConnected,
                    Messages = _mqttMessagesTotal,
                    UptimeSec = _mqttRunning
                        ? (long)(DateTime.Now - _mqttStartTime).TotalSeconds : 0,
                    RecentTopics = topics,
                },
                Modules = new CoreXModulesDto
                {
                    SteerConfigured = isConnectedSteer,
                    SteerHello = traffic.helloFromAutoSteer < 3,
                    MachineConfigured = isConnectedMachine,
                    MachineHello = traffic.helloFromMachine < 3,
                    ImuConfigured = isConnectedIMU,
                    ImuHello = traffic.helloFromIMU < 3,
                },
            });
        }

        // Puentes para el web host (Task 4): reusan los handlers de los
        // botones WinForms para que web y UI vieja hagan exactamente lo mismo.
        public void ToggleMqttBrokerFromWeb() => btnMQTT_Click(null, EventArgs.Empty);
        public void ToggleNtripFromWeb() => btnStartStopNtrip_Click(null, EventArgs.Empty);
    }
}
```

Notas para el implementador:
- Si algún nombre de campo no compila (p. ej. `_mqttMessagesTotal` es `int` vs `long`, o `broadCasterIP` tiene otra capitalización), ajustar el snapshot al tipo/nombre real del partial — NO cambiar el partial existente.
- `_mqttRecentTopics` es `LinkedList<string>` — `Take(20).ToList()` funciona vía LINQ.

- [ ] **Step 2: Llamar al snapshot desde el tick de 1 s**

En `FormLoop.cs`, método `oneSecondLoopTimer_Tick` (arranca ~línea 347): agregar como **última línea del método** (después del bloque que alterna `lblNTRIPBytes.BackColor` entre CornflowerBlue/DarkOrange, ~líneas 493-501):

```csharp
            // Publica el snapshot para el dashboard web (:5181).
            UpdateCoreXSnapshot();
```

- [ ] **Step 3: Compilar**

Run: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m`
Expected: `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs SourceCode/AgIO/Source/Forms/FormLoop.cs
git commit -m "feat(corex-web): publicar snapshot de estado cada 1s desde el timer UI"
```

---

### Task 3: CoreXWebHost EmbedIO :5181 + GET /api/corex/status

**Files:**
- Modify: `SourceCode/AgIO/Source/AgIO.csproj`
- Create: `SourceCode/AgIO/Source/Web/CoreXWebHost.cs`
- Create: `SourceCode/AgIO/Source/Web/CoreXStatusController.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs` (`FormLoop_Load` ~línea 298 y `FormLoop_FormClosing` ~línea 338)

- [ ] **Step 1: Agregar la referencia al proyecto WebHost en AgIO.csproj**

En el `<ItemGroup>` de ProjectReference (líneas 27-30), agregar:

```xml
    <ProjectReference Include="..\..\AgroParallel\Web\AgroParallel.WebHost\AgroParallel.WebHost.csproj" />
```

Esto trae transitivamente EmbedIO 3.5.2, `AgpControllerBase` y `AgpJson` (AgroParallel.Models). AgIO es net48 y WebHost netstandard2.0 → compatible (igual que lo hace AOG).

- [ ] **Step 2: Crear el web host**

`SourceCode/AgIO/Source/Web/CoreXWebHost.cs`:

```csharp
using System;
using System.IO;
using EmbedIO;
using EmbedIO.WebApi;

namespace AgIO
{
    /// <summary>
    /// Web host local de CoreX: dashboard + API en 127.0.0.1:5181 (solo
    /// loopback; el :5180 es del Hub PilotX). Sirve wwwroot-corex/ y
    /// /api/corex/*. Ciclo de vida atado a FormLoop (Load/Closing).
    /// </summary>
    public sealed class CoreXWebHost : IDisposable
    {
        public const int Port = 5181;
        public static string Url => "http://127.0.0.1:" + Port + "/";

        private WebServer _server;
        private readonly FormLoop _form;

        public CoreXWebHost(FormLoop form)
        {
            _form = form;
        }

        public void Start()
        {
            if (_server != null) return;

            string wwwroot = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "wwwroot-corex");

            _server = new WebServer(o => o
                    .WithUrlPrefix(Url)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithWebApi("/api", m =>
                {
                    m.WithController(() => new CoreXStatusController());
                    m.WithController(() => new CoreXCommandController(_form));
                });

            if (Directory.Exists(wwwroot))
            {
                // Sin cache: CoreX corre en el tractor y queremos que un
                // reemplazo de archivos se vea al refrescar.
                _server = _server.WithStaticFolder("/", wwwroot, false,
                    m => m.WithContentCaching(false));
            }

            _ = _server.RunAsync();
            AgLibrary.Logging.Log.EventWriter("CoreX web host escuchando en " + Url);
        }

        public void Dispose()
        {
            try { _server?.Dispose(); } catch { /* shutdown */ }
            _server = null;
        }
    }
}
```

Notas para el implementador:
- `CoreXCommandController` se crea en la Task 4. Para que esta task compile sola, crear en el mismo paso un stub mínimo (archivo `CoreXCommandController.cs` con la clase vacía heredando `AgpControllerBase` y el ctor `(FormLoop form)`) — la Task 4 lo completa.
- El logger de AgIO: mirar cómo loguea FormLoop (`Log.EventWriter(...)`). Si `Log` está accesible con un `using` distinto, usar el mismo patrón que FormLoop.cs y ajustar.

- [ ] **Step 3: Crear el controller de status**

`SourceCode/AgIO/Source/Web/CoreXStatusController.cs`:

```csharp
using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>GET /api/corex/status — snapshot JSON (snake_case) @1Hz de polling.</summary>
    public sealed class CoreXStatusController : AgpControllerBase
    {
        [Route(HttpVerbs.Get, "/corex/status")]
        public Task Status() => WriteJsonAsync(CoreXState.Instance.Snapshot());
    }
}
```

Nota: verificar el namespace real de `AgpControllerBase` en `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/AgpControllerBase.cs` (línea 28) y ajustar el `using` si no es `AgroParallel.WebHost.Controllers`.

- [ ] **Step 4: Ciclo de vida en FormLoop**

En `FormLoop.cs`:

(a) Campo de instancia, junto a los demás campos de la clase (cerca de línea 64):

```csharp
        // Web host del dashboard CoreX (127.0.0.1:5181).
        private CoreXWebHost corexWebHost;
```

(b) En `FormLoop_Load`, después de `StartMqttBroker();` (línea 299):

```csharp
            // Dashboard web CoreX.
            corexWebHost = new CoreXWebHost(this);
            corexWebHost.Start();
```

(c) En `FormLoop_FormClosing`, después de `StopMqttBroker();` (línea 339):

```csharp
            corexWebHost?.Dispose();
```

- [ ] **Step 5: Compilar y probar el endpoint**

Run:
```bash
taskkill //IM CoreX.exe //F 2>/dev/null
dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m
```
Expected: `Build succeeded`.

Luego lanzar el exe compilado (buscar `CoreX.exe` en `SourceCode/AgIO/Source/bin/Debug/`) y:
```bash
curl -s http://127.0.0.1:5181/api/corex/status
```
Expected: JSON snake_case tipo `{"ok":true,"version":"...","profile":"...","gps":{...},"ntrip":{...},"mqtt":{"running":true,...},"modules":{...}}` con `mqtt.running:true` (el broker arranca solo). Cerrar CoreX después de probar.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgIO/Source/AgIO.csproj SourceCode/AgIO/Source/Web/ SourceCode/AgIO/Source/Forms/FormLoop.cs
git commit -m "feat(corex-web): EmbedIO :5181 con GET /api/corex/status (AgpJson snake_case)"
```

---

### Task 4: Endpoints POST de comando (MQTT / NTRIP toggle)

**Files:**
- Create/Complete: `SourceCode/AgIO/Source/Web/CoreXCommandController.cs`

- [ ] **Step 1: Completar el controller de comandos**

```csharp
using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>
    /// POST /api/corex/{mqtt,ntrip}/toggle — comandos del dashboard.
    /// Los handlers WinForms tocan controles, así que marshalleamos al
    /// hilo UI con BeginInvoke (mismo patrón que los callbacks async viejos).
    /// </summary>
    public sealed class CoreXCommandController : AgpControllerBase
    {
        private readonly FormLoop _form;

        public CoreXCommandController(FormLoop form)
        {
            _form = form;
        }

        [Route(HttpVerbs.Post, "/corex/mqtt/toggle")]
        public Task ToggleMqtt()
        {
            _form.BeginInvoke((MethodInvoker)(() => _form.ToggleMqttBrokerFromWeb()));
            return WriteJsonAsync(new { ok = true });
        }

        [Route(HttpVerbs.Post, "/corex/ntrip/toggle")]
        public Task ToggleNtrip()
        {
            _form.BeginInvoke((MethodInvoker)(() => _form.ToggleNtripFromWeb()));
            return WriteJsonAsync(new { ok = true });
        }
    }
}
```

(Los métodos `ToggleMqttBrokerFromWeb`/`ToggleNtripFromWeb` ya existen desde la Task 2.)

- [ ] **Step 2: Compilar y probar**

Run: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m` → `Build succeeded`.

Lanzar CoreX.exe y:
```bash
curl -s -X POST http://127.0.0.1:5181/api/corex/mqtt/toggle
sleep 2
curl -s http://127.0.0.1:5181/api/corex/status
```
Expected: primer curl `{"ok":true}`; el status siguiente muestra `mqtt.running:false` (y el botón MQTT de la UI vieja pasa a gris — validación A/B). Repetir el toggle para dejarlo en `true`. Cerrar CoreX.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgIO/Source/Web/CoreXCommandController.cs
git commit -m "feat(corex-web): POST mqtt/ntrip toggle marshalleados al hilo UI"
```

---

### Task 5: Dashboard wwwroot-corex (HTML + JS + copia del design system)

**Files:**
- Create: `SourceCode/AgIO/Source/wwwroot-corex/index.html`
- Create: `SourceCode/AgIO/Source/wwwroot-corex/js/corex.js`
- Modify: `SourceCode/AgIO/Source/AgIO.csproj` (copy al output)

- [ ] **Step 1: Copiar estáticos al output en AgIO.csproj**

Agregar al final del csproj (antes de `</Project>`):

```xml
  <ItemGroup>
    <None Include="wwwroot-corex\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <!-- Design system compartido: misma fuente de verdad que el Hub PilotX.
       Se copia en build a wwwroot-corex/ del output (spec decisión 5). -->
  <Target Name="CopyCoreXDesignSystem" AfterTargets="Build">
    <ItemGroup>
      <AgpDesignCss Include="..\..\AgroParallel\Web\AgroParallel.WebUI\wwwroot\theme.css;..\..\AgroParallel\Web\AgroParallel.WebUI\wwwroot\layout.css;..\..\AgroParallel\Web\AgroParallel.WebUI\wwwroot\keyboard.css" />
      <AgpDesignJs Include="..\..\AgroParallel\Web\AgroParallel.WebUI\wwwroot\js\modal.js;..\..\AgroParallel\Web\AgroParallel.WebUI\wwwroot\js\keyboard.js" />
    </ItemGroup>
    <Copy SourceFiles="@(AgpDesignCss)" DestinationFolder="$(OutDir)wwwroot-corex" SkipUnchangedFiles="true" />
    <Copy SourceFiles="@(AgpDesignJs)" DestinationFolder="$(OutDir)wwwroot-corex\js" SkipUnchangedFiles="true" />
  </Target>
```

- [ ] **Step 2: Crear index.html**

`SourceCode/AgIO/Source/wwwroot-corex/index.html`:

```html
<!DOCTYPE html>
<html lang="es">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>CoreX</title>
  <link rel="stylesheet" href="theme.css">
  <link rel="stylesheet" href="layout.css">
  <style>
    /* Grid del dashboard — solo layout local, la piel viene del theme. */
    .corex-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: 14px; padding: 14px;
    }
    .corex-card {
      background: var(--agp-surface); border: 1px solid var(--agp-border);
      border-radius: 8px; padding: 14px;
    }
    .corex-card h2 { margin: 0 0 10px; font-size: 1.05em; }
    .kv { display: flex; justify-content: space-between; padding: 3px 0;
          font-size: 0.95em; }
    .kv .k { color: var(--agp-text-muted); }
    .kv .v { font-family: var(--agp-font-mono); }
    .dot { display: inline-block; width: 10px; height: 10px;
           border-radius: 50%; margin-right: 6px;
           background: var(--agp-text-muted); }
    .dot.on { background: var(--agp-state-ok, #4ABA3E); }
    .dot.bad { background: var(--agp-state-err, #c0392b); }
    .topics { font-family: var(--agp-font-mono); font-size: 0.8em;
              max-height: 160px; overflow-y: auto; white-space: pre;
              color: var(--agp-text-muted); margin-top: 8px; }
    header.corex-top {
      display: flex; align-items: center; justify-content: space-between;
      padding: 12px 16px; border-bottom: 1px solid var(--agp-border);
      background: var(--agp-surface);
    }
    header.corex-top h1 { margin: 0; font-size: 1.2em; }
    .muted { color: var(--agp-text-muted); font-size: 0.85em; }
  </style>
</head>
<body>
  <header class="corex-top">
    <h1>CoreX</h1>
    <div class="muted"><span id="hdrVersion">—</span> · perfil <span id="hdrProfile">—</span></div>
  </header>

  <main class="corex-grid">
    <section class="corex-card" id="cardGps">
      <h2><span class="dot" id="dotGps"></span>GPS</h2>
      <div class="kv"><span class="k">Latitud</span><span class="v" id="gpsLat">—</span></div>
      <div class="kv"><span class="k">Longitud</span><span class="v" id="gpsLon">—</span></div>
    </section>

    <section class="corex-card" id="cardNtrip">
      <h2><span class="dot" id="dotNtrip"></span>NTRIP</h2>
      <div class="kv"><span class="k">Estado</span><span class="v" id="ntripEstado">—</span></div>
      <div class="kv"><span class="k">Recibido</span><span class="v" id="ntripKb">—</span></div>
      <div class="kv"><span class="k">Caster</span><span class="v" id="ntripCaster">—</span></div>
      <button class="btn" id="btnNtrip" style="margin-top:10px;">Iniciar / Detener</button>
    </section>

    <section class="corex-card" id="cardMqtt">
      <h2><span class="dot" id="dotMqtt"></span>MQTT broker</h2>
      <div class="kv"><span class="k">Puerto</span><span class="v" id="mqttPort">—</span></div>
      <div class="kv"><span class="k">Clientes</span><span class="v" id="mqttClients">—</span></div>
      <div class="kv"><span class="k">Mensajes</span><span class="v" id="mqttMsgs">—</span></div>
      <div class="kv"><span class="k">Uptime</span><span class="v" id="mqttUptime">—</span></div>
      <button class="btn" id="btnMqtt" style="margin-top:10px;">Encender / Apagar</button>
      <div class="topics" id="mqttTopics"></div>
    </section>

    <section class="corex-card" id="cardModulos">
      <h2>Módulos</h2>
      <div class="kv"><span class="k"><span class="dot" id="dotSteer"></span>Dirección (Steer)</span><span class="v" id="modSteer">—</span></div>
      <div class="kv"><span class="k"><span class="dot" id="dotMachine"></span>Máquina</span><span class="v" id="modMachine">—</span></div>
      <div class="kv"><span class="k"><span class="dot" id="dotImu"></span>IMU</span><span class="v" id="modImu">—</span></div>
    </section>
  </main>

  <script src="js/modal.js"></script>
  <script src="js/corex.js"></script>
</body>
</html>
```

- [ ] **Step 3: Crear corex.js**

`SourceCode/AgIO/Source/wwwroot-corex/js/corex.js`:

```javascript
// ============================================================================
// corex.js — dashboard live de CoreX. Polling de /api/corex/status @1Hz,
// mismo refresco que tenía el timer WinForms. Toggles vía POST.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }
  function setText(id, v) { var el = $(id); if (el) el.textContent = v; }
  function setDot(id, state) {
    var el = $(id); if (!el) return;
    el.classList.remove('on', 'bad');
    if (state === true) el.classList.add('on');
    else if (state === false) el.classList.add('bad');
    // null/undefined → gris neutro (no configurado)
  }
  function fmtUptime(sec) {
    if (!sec) return '—';
    var h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
    return h > 0 ? h + ' h ' + m + ' min' : m + ' min';
  }

  async function poll() {
    try {
      var res = await fetch('/api/corex/status', { cache: 'no-store' });
      var d = await res.json();
      if (!d || !d.ok) return;

      setText('hdrVersion', 'v' + (d.version || '—'));
      setText('hdrProfile', d.profile || '—');

      var g = d.gps || {};
      setDot('dotGps', !!g.alive);
      setText('gpsLat', g.latitude != null ? g.latitude.toFixed(7) : '—');
      setText('gpsLon', g.longitude != null ? g.longitude.toFixed(7) : '—');

      var n = d.ntrip || {};
      setDot('dotNtrip', n.connected ? true : (n.required_on ? false : null));
      setText('ntripEstado', n.connected ? 'conectado'
        : n.connecting ? 'conectando…'
        : n.required_on ? 'esperando' : 'apagado');
      setText('ntripKb', (n.kb_total != null ? n.kb_total : 0) + ' kB');
      setText('ntripCaster', n.caster_ip || '—');

      var m = d.mqtt || {};
      setDot('dotMqtt', !!m.running);
      setText('mqttPort', m.port != null ? m.port : '—');
      setText('mqttClients', m.clients != null ? m.clients : '—');
      setText('mqttMsgs', m.messages != null ? m.messages : '—');
      setText('mqttUptime', m.running ? fmtUptime(m.uptime_sec) : '—');
      var tp = $('mqttTopics');
      if (tp) tp.textContent = (m.recent_topics || []).join('\n');

      var mods = d.modules || {};
      setDot('dotSteer', mods.steer_configured ? !!mods.steer_hello : null);
      setText('modSteer', mods.steer_configured
        ? (mods.steer_hello ? 'online' : 'sin hello') : 'no configurado');
      setDot('dotMachine', mods.machine_configured ? !!mods.machine_hello : null);
      setText('modMachine', mods.machine_configured
        ? (mods.machine_hello ? 'online' : 'sin hello') : 'no configurado');
      setDot('dotImu', mods.imu_configured ? !!mods.imu_hello : null);
      setText('modImu', mods.imu_configured
        ? (mods.imu_hello ? 'online' : 'sin hello') : 'no configurado');
    } catch (e) { /* offline: el próximo tick reintenta */ }
  }

  function post(url) {
    return fetch(url, { method: 'POST' }).then(function (r) { return r.json(); });
  }

  var btnMqtt = $('btnMqtt');
  if (btnMqtt) btnMqtt.addEventListener('click', function () {
    AgpModal.confirm('Broker MQTT', '¿Cambiar el estado del broker MQTT? Los nodos se desconectan si lo apagás.')
      .then(function (ok) { if (ok) return post('/api/corex/mqtt/toggle'); })
      .then(poll);
  });

  var btnNtrip = $('btnNtrip');
  if (btnNtrip) btnNtrip.addEventListener('click', function () {
    post('/api/corex/ntrip/toggle').then(poll);
  });

  // Polling — pausa con la tab oculta (mismo patrón que setup.js del Hub).
  var handle = null;
  function start() { if (!handle) handle = setInterval(poll, 1000); }
  function stop() { if (handle) { clearInterval(handle); handle = null; } }
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) stop(); else { poll(); start(); }
  });

  poll();
  start();
})();
```

- [ ] **Step 4: Compilar y verificar estáticos + dashboard**

Run: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m` → `Build succeeded`.

Verificar que el output tenga `wwwroot-corex/theme.css`, `layout.css`, `js/modal.js`, `js/keyboard.js`, `index.html`, `js/corex.js` (en `SourceCode/AgIO/Source/bin/Debug/wwwroot-corex/`).

Lanzar CoreX.exe y abrir `http://127.0.0.1:5181/` en un browser: el dashboard muestra versión/perfil, MQTT running con contadores subiendo, y los toggles funcionan (validar contra la UI vieja al lado).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgIO/Source/wwwroot-corex/ SourceCode/AgIO/Source/AgIO.csproj
git commit -m "feat(corex-web): dashboard web con design system PilotX (GPS/NTRIP/MQTT/modulos)"
```

---

### Task 6: FormWebShell (WebView2)

**Files:**
- Modify: `SourceCode/AgIO/Source/AgIO.csproj` (package WebView2)
- Create: `SourceCode/AgIO/Source/Forms/FormWebShell.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs` (`FormLoop_Load`)

- [ ] **Step 1: Agregar el package WebView2**

En el `<ItemGroup>` de PackageReference de AgIO.csproj:

```xml
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.3856.49" />
```

(Misma versión que AgroParallel.Shell.csproj:22 y AgOpenGPS.csproj:87 — no introducir otra.)

- [ ] **Step 2: Crear FormWebShell**

`SourceCode/AgIO/Source/Forms/FormWebShell.cs`:

```csharp
using System;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace AgIO
{
    /// <summary>
    /// Ventana web de CoreX: WebView2 fullscreen contra el host local :5181.
    /// Convive con FormLoop (UI vieja) durante la migración — spec decisión 4.
    /// </summary>
    public sealed class FormWebShell : Form
    {
        private readonly WebView2 _web = new WebView2();

        public FormWebShell()
        {
            Text = "CoreX";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1100;
            Height = 720;
            // Mismo ícono que el exe (isotipo Agro Parallel).
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { /* sin ícono no es fatal */ }

            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);

            Load += async (s, e) =>
            {
                try
                {
                    await _web.EnsureCoreWebView2Async();
                    _web.CoreWebView2.Navigate(CoreXWebHost.Url);
                }
                catch (Exception ex)
                {
                    // WebView2 runtime ausente: no rompemos CoreX, queda la UI vieja.
                    AgLibrary.Logging.Log.EventWriter("FormWebShell sin WebView2: " + ex.Message);
                    Close();
                }
            };
        }
    }
}
```

(Ajustar el `using`/llamada de log al mismo patrón que use FormLoop.cs, igual que en Task 3.)

- [ ] **Step 3: Abrir el shell al arrancar**

En `FormLoop_Load`, después del `corexWebHost.Start();` agregado en Task 3:

```csharp
            // Ventana web (convive con la UI vieja durante la migración).
            new FormWebShell().Show(this);
```

- [ ] **Step 4: Compilar y validar visualmente**

Run: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m` → `Build succeeded`.

Lanzar CoreX.exe: deben aparecer la ventana vieja Y la ventana "CoreX" con el dashboard. Verificar que los datos coinciden con los labels viejos (lat/lon, NTRIP, MQTT).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgIO/Source/AgIO.csproj SourceCode/AgIO/Source/Forms/FormWebShell.cs SourceCode/AgIO/Source/Forms/FormLoop.cs
git commit -m "feat(corex-web): FormWebShell WebView2 fullscreen contra :5181"
```

---

### Task 7: Validación integral de regresiones

**Files:** ninguno nuevo (solo validación; fixes si aparecen).

- [ ] **Step 1: Build completo del ecosistema**

Run desde la raíz del repo: `powershell -ExecutionPolicy Bypass -File build.ps1` (cierra PilotX/CoreX solo; si falla por lock, `taskkill //IM PilotX.exe //F; taskkill //IM CoreX.exe //F` y reintentar).
Expected: build OK con SHA impreso.

- [ ] **Step 2: Checklist de criterio de éxito (spec)**

Con PilotX + CoreX corriendo (ojo: PilotX puede quedar en la pantalla de Términos tras un build — aceptarla):

1. `netstat -ano | grep -E "5181|1883"` → CoreX.exe escuchando en ambos (cuidado con la trampa conocida de dobles bind en :1883/:5180).
2. `curl -s http://127.0.0.1:5181/api/corex/status` → `mqtt.running:true`, `mqtt.clients` > 0 si hay nodos ESP32/PilotX conectados.
3. Los nodos siguen anunciándose: en el dashboard, `recent_topics` muestra `agp/.../announcement` o `status_live`.
4. PGN loopback AOG↔CoreX sin regresiones: PilotX recibe GPS (o al menos el hello — botones GPS/Steer de la UI vieja con el mismo color que los dots del dashboard).
5. Toggle MQTT desde el dashboard: nodos se desconectan/reconectan; el botón viejo cambia de color a la par.
6. Toggle NTRIP (si hay caster configurado): estado y kB coherentes entre dashboard y UI vieja.

- [ ] **Step 3: Commit de fixes (si hubo)**

Solo archivos propios de este plan; mensaje `fix(corex-web): ...`.

---

## Fuera de este plan (siguientes)

- Extracción real de los 4 servicios (`SerialComService`, `UdpNetworkService`, `NtripClientService`, `MqttBrokerService`) — la mecánica queda demostrada por el snapshot; se hace cuando se retiren los partials.
- Páginas web de configuración (puertos serie, caster NTRIP, red UDP) — Fase 3 del spec.
- Ocultar FormLoop (queda visible como validación A/B hasta que el dashboard esté probado en campo).
- Instalador: agregar `wwwroot-corex/` a `Installer/AgroParallelPilot.iss` cuando se empaquete la próxima release.
