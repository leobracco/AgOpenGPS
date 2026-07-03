# Bloque A — Confiabilidad Backend PilotX · Plan de Implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Backend PilotX confiable: errores siempre logueados, JSON unificado snake_case, bridges MQTT sin código duplicado, tests en las rutas críticas y configs validadas al guardar.

**Architecture:** Se apalanca `DebugLogService` (ya existente) con un wrapper estático `AgpLog`; se sube System.Text.Json a 8.0 para usar `SnakeCaseLower` central en un `AgpJson.Options` compartido + clase base de controllers con `WriteJson`; se extrae `MqttLiveServiceBase<TReading>` para los 4 live services; se crea `AgroParallel.Services.Tests` (NUnit 4.3.2, mismo stack que AgOpenGPS.Core.Tests, corre en el CI existente).

**Tech Stack:** C# netstandard2.0 (SDK-style), EmbedIO 3.5.2, System.Text.Json 8.0.x, MQTTnet 4.3.6, NUnit 4.3.2 + Microsoft.NET.Test.Sdk 17.12.

**Convención de idioma:** comentarios nuevos, logs y commits en castellano rioplatense.

**Rutas base (abreviadas en el resto del doc):**
- `SRV` = `SourceCode/AgroParallel/Core/AgroParallel.Services`
- `MOD` = `SourceCode/AgroParallel/Core/AgroParallel.Models`
- `WEB` = `SourceCode/AgroParallel/Web/AgroParallel.WebHost`
- `JS`  = `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js`

**Build/verify:** `dotnet build SourceCode/AgOpenGPS.sln` y `dotnet test SourceCode/AgOpenGPS.sln`. OJO: PilotX RELEASE cachea wwwroot en RAM — para probar JS en vivo hay que reiniciar PilotX.exe.

---

## A1 — Logger central + barrida de catch silenciosos

### Task 1: Wrapper estático `AgpLog`

**Files:**
- Create: `SRV/AgpLog.cs`
- Test: se testea en Task 12 (junto con el proyecto de tests); acá solo build.

- [ ] **Step 1: Crear AgpLog.cs**

```csharp
using System;
using System.Diagnostics;

namespace AgroParallel.Services
{
    /// <summary>
    /// Fachada estática de logging. Rutea a Debug.WriteLine con el formato
    /// "[Modulo] mensaje" que el TraceListener de DebugLogService ya captura
    /// (nivel inferido por heurística). No abre archivos ni lanza nunca.
    /// </summary>
    public static class AgpLog
    {
        public static void Info(string modulo, string msg)
            => Write(modulo, msg);

        public static void Warn(string modulo, string msg)
            => Write(modulo, "WARN: " + msg);

        public static void Error(string modulo, string msg)
            => Write(modulo, "ERROR: " + msg);

        /// <summary>Para catch: loguea tipo + mensaje (sin stacktrace completo, va al ring buffer).</summary>
        public static void Error(string modulo, string contexto, Exception ex)
            => Write(modulo, $"ERROR: {contexto}: {ex.GetType().Name}: {ex.Message}");

        public static void Warn(string modulo, string contexto, Exception ex)
            => Write(modulo, $"WARN: {contexto}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(string modulo, string msg)
        {
            try { Debug.WriteLine($"[{modulo}] {msg}"); } catch { /* jamás romper por loguear */ }
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj`
Expected: 0 errores.

- [ ] **Step 3: Commit** — `feat(log): fachada AgpLog sobre DebugLogService`

### Task 2: Barrida de catch críticos — OrbitX (Sync, OTA, self-update)

**Files:**
- Modify: `SRV/OrbitX/OrbitXSync.cs`, `SRV/OrbitX/FirmwareLanServer.cs`, `SRV/OrbitX/FirmwareOtaCoordinator.cs` (línea ~144), `SRV/OrbitX/PilotXSelfUpdate.cs`, `SRV/OrbitX/FirmwareMirror.cs`, `SRV/OrbitX/FirmwareOtaClient.cs`

- [ ] **Step 1: Reemplazar cada `catch { }` / `catch (Exception) { }` crítico**

Patrón de reemplazo (mantener el flujo — seguimos tragando, pero logueado):

```csharp
// ANTES
catch { }

// DESPUÉS
catch (Exception ex) { AgpLog.Warn("OrbitXSync", "encolando archivos del lote", ex); }
```

Reglas de la barrida:
- Módulo = nombre corto de la clase ("OrbitXSync", "FwLanServer", "OtaCoord", "SelfUpdate", "FwMirror", "OtaClient").
- Contexto = qué se estaba haciendo, en castellano, 3-6 palabras.
- `Error` si la operación fallida implica pérdida de datos o de update; `Warn` si es best-effort/reintentable.
- EXCEPCIONES que quedan sin log (dejar comentario `// silencioso a propósito: <motivo>`): catch en Dispose/Stop de sockets, catch de `ObjectDisposedException`/`OperationCanceledException` en shutdown.

- [ ] **Step 2: Build + commit** — `fix(orbitx): loguear excepciones tragadas en sync/OTA/self-update`

### Task 3: Barrida de catch críticos — CamarasRemoteRelay

**Files:**
- Modify: `SRV/Camaras/CamarasRemoteRelay.cs` (líneas 60, 71, 75, 177, 184, 240, 275, 296, 390, 391, 462, 488, 495, 513, 527)

- [ ] **Step 1: Aplicar el mismo patrón** con módulo `"CamRelay"`. Los catch alrededor de kill/cleanup de procesos ffmpeg quedan silenciosos a propósito (comentar). Los de arranque de stream / relay loop → `AgpLog.Error`.
- [ ] **Step 2: Build + commit** — `fix(camaras): loguear fallos del relay RTSP (antes silenciosos)`

### Task 4: DebugLogService — no auto-silenciarse

**Files:**
- Modify: `SRV/DebugLogService.cs` (12 catch vacíos: 64, 81, 135, 207, 277, 286, 304, 312, 344, 383, 397, 442)

- [ ] **Step 1:** Los catch de I/O de disco (63, 277, 286, 304, 312, 344) NO pueden loguear vía AgpLog (recursión) — dejar comentario `// silencioso a propósito: fallback de I/O del propio logger`. Los demás (parseo de config, suscriptores) → envolver en `Debug.WriteLine` directo con prefijo `[DebugLog]`.
- [ ] **Step 2: Build + commit** — `chore(log): documentar catch intencionales del propio logger`

---

## A2 — Serialización JSON unificada (snake_case)

> Hallazgo clave del relevamiento: NO hay 662 defensas JS (hay ~4). El problema real
> es 63/95 DTOs sin `[JsonPropertyName]` y serialización manual repetida por controller.
> Estrategia: STJ 8.0 + `SnakeCaseLower` central → los DTOs sin atributos pasan a emitir
> snake_case automáticamente. Los atributos existentes tienen precedencia (no cambia nada
> donde ya estaban). El riesgo es el JS que hoy consume PascalCase de esos 63 DTOs:
> cada módulo se migra con su JS en el MISMO commit.

### Task 5: STJ 8.0 + `AgpJson` central

**Files:**
- Modify: `MOD/AgroParallel.Models.csproj`, `WEB/AgroParallel.WebHost.csproj`, `SRV/AgroParallel.Services.csproj` (donde referencien System.Text.Json 6.0.10 → `8.0.5`)
- Create: `MOD/AgpJson.cs`

- [ ] **Step 1: Bump del package** en cada csproj:

```xml
<PackageReference Include="System.Text.Json" Version="8.0.5" />
```

- [ ] **Step 2: Crear AgpJson.cs**

```csharp
using System.Text.Json;

namespace AgroParallel.Models
{
    /// <summary>
    /// Opciones JSON únicas de todo el backend PilotX.
    /// Escritura: snake_case (política global; [JsonPropertyName] tiene precedencia).
    /// Lectura: case-insensitive (tolera payloads viejos PascalCase).
    /// </summary>
    public static class AgpJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
        public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
```

- [ ] **Step 3: Build completo** — `dotnet build SourceCode/AgOpenGPS.sln` (verifica que el bump no rompa Swan/EmbedIO).
- [ ] **Step 4: Commit** — `feat(json): STJ 8.0 + AgpJson central con snake_case global`

### Task 6: Base controller `AgpControllerBase` con WriteJson

**Files:**
- Create: `WEB/Controllers/AgpControllerBase.cs`

- [ ] **Step 1: Crear la base** (calcada del WriteJson que hoy repite cada controller, ej. `ImplementoController.cs:64-70`):

```csharp
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using EmbedIO;
using EmbedIO.WebApi;

namespace AgroParallel.WebHost.Controllers
{
    /// <summary>Base de todos los controllers: JSON out snake_case vía AgpJson, body-in unificado.</summary>
    public abstract class AgpControllerBase : WebApiController
    {
        protected async Task WriteJsonAsync<T>(T payload)
        {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(AgpJson.Serialize(payload));
            await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        protected async Task<T> ReadJsonBodyAsync<T>()
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            return AgpJson.Deserialize<T>(body);
        }

        protected async Task WriteErrorAsync(int status, string code, string friendly, string technical = null)
        {
            HttpContext.Response.StatusCode = status;
            await WriteJsonAsync(new { error = code, mensaje = friendly, detalle = technical });
        }
    }
}
```

- [ ] **Step 2: Build + commit** — `feat(web): AgpControllerBase con WriteJson/ReadJson unificados`

### Task 7: Migración por módulo (controller + JS juntos)

> Un commit por módulo. Para CADA módulo el procedimiento es idéntico; el implementador
> lo repite. Orden (de menor a mayor riesgo): StormX → LineX → SectionX → FlowX →
> Setup/Sistema → Camaras → VistaX → QuantiX → Implemento/Nodos (estos últimos ya
> tienen atributos snake_case — solo migrar a la base, sin cambio de wire format).

**Files (por módulo, ejemplo FlowX):**
- Modify: `WEB/Controllers/FlowXController.cs`, `JS/flowx.js`, DTOs del módulo en `MOD/` si tienen atributos inconsistentes.

Procedimiento por módulo:

- [ ] **Step 1: Inventariar el wire format actual.** Leer el controller: ¿serializa con STJ manual + atributos (ya snake_case) o con opciones default (PascalCase)? Listar las propiedades que HOY viajan PascalCase.
- [ ] **Step 2: Migrar el controller** a `AgpControllerBase`: heredar, borrar su `JsonOpts`/`WriteJson` locales, usar `WriteJsonAsync`/`ReadJsonBodyAsync`.
- [ ] **Step 3: Actualizar el JS del módulo.** Por cada propiedad que cambió de casing (`MeterCal` → `meter_cal`), buscar en el JS del módulo (`grep -n "MeterCal" JS/*.js`) y renombrar. Recordar landmine EmbedIO: el save-config debe seguir haciendo MERGE sobre Load(), no reconstruir el DTO.
- [ ] **Step 4: Verificar a mano** (PilotX corriendo, reiniciar por el cache RELEASE): abrir la página del módulo en el Hub, cargar config, editar un campo, guardar, recargar — ver que persiste y no hay errores en consola WebView2.
- [ ] **Step 5: Commit** — `refactor(<modulo>): wire format snake_case + AgpControllerBase`

Repetir Steps 1-5 para: StormX, LineX, SectionX, FlowX, Setup/Sistema, Camaras, VistaX, QuantiX, Implemento, Nodos, Firmwares, CoreX-ECU (todos los controllers en `WEB/Controllers/`).

- [ ] **Step final: Barrer restos.** `grep -rn "JsonSerializerOptions" WEB/ SRV/` — no debe quedar ninguna instancia local fuera de `AgpJson` (excepto casos con requisitos especiales documentados, ej. payloads hacia OrbitX cloud o firmware que exigen otro formato — esos NO se tocan y se comentan).

⚠️ **No tocar** serialización de: payloads MQTT hacia nodos ESP32 (formato pactado con firmware), requests hacia OrbitX cloud (formato pactado con server Node), `overlayPrefs.json` y archivos locales legacy (compatibilidad de lectura).

---

## A3 — Clase base para live services MQTT

### Task 8: `MqttLiveServiceBase<TReading>`

**Files:**
- Create: `SRV/MqttLiveServiceBase.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/MqttLiveServiceBaseTests.cs` (Task 12 crea el proyecto; si este task corre antes, crear el proyecto acá siguiendo Task 12 Step 1)

- [ ] **Step 1: Crear la base** (extraída del patrón común de FlowX/LineX/StormX/VistaXLiveService):

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    /// <summary>
    /// Base de los live services MQTT (FlowX/LineX/StormX/VistaX).
    /// Cubre el ritual Start/Stop, el filtrado por topic-prefix, el parse JSON
    /// del payload y el registry de lecturas por UID con timeout de online.
    /// </summary>
    public abstract class MqttLiveServiceBase<TReading> where TReading : class
    {
        protected readonly object _lock = new object();
        protected readonly Dictionary<string, TReading> _readings =
            new Dictionary<string, TReading>(StringComparer.OrdinalIgnoreCase);

        private readonly INodoRegistryService _nodos;

        /// <summary>Prefijo de topic, ej. "agp/flow/". El UID se toma de parts[2].</summary>
        protected abstract string TopicPrefix { get; }
        /// <summary>Filtros de suscripción MQTT, ej. ["agp/flow/+/status_live"].</summary>
        protected abstract string[] Subscriptions { get; }
        /// <summary>Timeout para considerar un nodo online (ms).</summary>
        protected virtual int TimeoutMs => 3000;

        /// <summary>Procesa un payload ya parseado. Corre bajo _lock NO tomado — tomarlo si escribe _readings.</summary>
        protected abstract void OnPayload(string uid, string subtopic, JsonElement root);

        protected MqttLiveServiceBase(INodoRegistryService nodos) { _nodos = nodos; }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            _nodos.MessageReceived += OnMqttMessage;
            foreach (var s in Subscriptions) _nodos.SubscribeAsync(s);
            IsRunning = true;
        }

        public void Stop()
        {
            try { _nodos.MessageReceived -= OnMqttMessage; }
            catch (Exception ex) { AgpLog.Warn(GetType().Name, "desuscribiendo MQTT", ex); }
            IsRunning = false;
            lock (_lock) _readings.Clear();
        }

        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Topic)) return;
            if (!e.Topic.StartsWith(TopicPrefix, StringComparison.OrdinalIgnoreCase)) return;
            var parts = e.Topic.Split('/');
            if (parts.Length < 4) return;
            try
            {
                using (var doc = JsonDocument.Parse(e.Payload))
                    OnPayload(parts[2], parts[3], doc.RootElement);
            }
            catch (Exception ex) { AgpLog.Warn(GetType().Name, "payload MQTT inválido en " + e.Topic, ex); }
        }

        protected bool IsOnline(DateTime lastTs, DateTime now)
            => (now - lastTs).TotalMilliseconds <= TimeoutMs;

        // ── Helpers de parse multi-key (antes copiados en cada service) ──
        protected static double ReadDouble(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            return 0;
        }

        protected static bool ReadBool(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!root.TryGetProperty(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble() != 0;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    return s == "true" || s == "1" || s == "ok";
                }
            }
            return false;
        }

        protected static string ReadString(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }
    }
}
```

Nota: ajustar nombres exactos (`MqttMessageReceivedEventArgs`, firma de `SubscribeAsync`) a lo que expone `INodoRegistryService` real — leerlo antes de escribir.

- [ ] **Step 2: Build + commit** — `feat(mqtt): MqttLiveServiceBase con ritual y parse comunes`

### Task 9: Migrar FlowXLiveService y LineXLiveService

**Files:**
- Modify: `SRV/FlowXLiveService.cs` (384 líneas → ~250), `SRV/LineXLiveService.cs` (226 → ~120)

- [ ] **Step 1:** FlowXLiveService: heredar de `MqttLiveServiceBase<Reading>`, borrar Start/Stop/OnMqttMessage/ReadDouble/ReadBool/ReadString propios, mover el cuerpo del handler a `OnPayload(uid, subtopic, root)` con `switch (subtopic)` para status_live / autotune_result / calibrar_result / caracterizar_result. `GetSnapshot()` queda igual usando `IsOnline(...)`.
- [ ] **Step 2:** Ídem LineXLiveService (subtopic único status_live, array `sections` se parsea igual que hoy dentro de OnPayload).
- [ ] **Step 3: Build + prueba manual:** con un nodo FlowX (o publicando un status_live fake con mosquitto_pub/cliente MQTT al broker de CoreX :1883), verificar que la página flowx.html sigue mostrando live.
- [ ] **Step 4: Commit** — `refactor(flowx,linex): live services sobre MqttLiveServiceBase`

### Task 10: Migrar StormXLiveService y VistaXLiveService

**Files:**
- Modify: `SRV/StormXLiveService.cs` (timeout virtual: `LogIntervalSec*2` → override `TimeoutMs`), `SRV/VistaXLiveService.cs` (topic dual `vistax/nodos/telemetria` + `vistax/+/telemetria`: override `Subscriptions` con ambos y contemplar en OnPayload que parts[2] puede ser "nodos"; timeout configurable `SensorTimeoutMs` → override).

- [ ] **Step 1:** Migrar StormX (Delta-T y demás cálculos quedan en OnPayload).
- [ ] **Step 2:** Migrar VistaX (clave compuesta `uid#cable` se mantiene — la base no lo impide, el diccionario es del hijo si hace falta esa clave; usar `_readings` con clave compuesta está OK).
- [ ] **Step 3: Build + prueba manual + commit** — `refactor(stormx,vistax): live services sobre MqttLiveServiceBase`

> **Fuera de alcance A3:** QuantiXMotorBridge y FlowXBridge (conexión MQTT propia,
> publish-only, lógica de dosis). Unificarlos con NodoRegistryService es un cambio de
> arquitectura aparte — anotado para un bloque futuro, no acá.

---

## A4 — Tests de rutas críticas

### Task 11 (fue 12): Proyecto `AgroParallel.Services.Tests`

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj`
- Modify: `SourceCode/AgOpenGPS.sln` (agregar el proyecto: `dotnet sln add`)

- [ ] **Step 1: Crear csproj** (calcado de AgOpenGPS.Core.Tests):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\AgroParallel.Services\AgroParallel.Services.csproj" />
    <ProjectReference Include="..\AgroParallel.Models\AgroParallel.Models.csproj" />
  </ItemGroup>
</Project>
```

(Verificar el TargetFramework que usan AgOpenGPS.Core.Tests y copiarlo si difiere.)

- [ ] **Step 2:** `dotnet sln SourceCode/AgOpenGPS.sln add SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj`
- [ ] **Step 3:** Test humo `SmokeTests.cs`:

```csharp
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class SmokeTests
    {
        [Test]
        public void ElProyectoDeTestsCorre() => Assert.Pass();
    }
}
```

- [ ] **Step 4:** `dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests` → 1 passed. El CI (`.github/workflows/build.yml` corre `dotnet test` sobre la sln) lo toma solo.
- [ ] **Step 5: Commit** — `test: proyecto AgroParallel.Services.Tests (NUnit) en la sln`

### Task 12: Tests de AgpErrorMapper + AgpJson

**Files:**
- Create: `.../AgroParallel.Services.Tests/AgpErrorMapperTests.cs`, `AgpJsonTests.cs`

- [ ] **Step 1: AgpErrorMapperTests** — un test por código conocido. Ejemplos (ajustar a la API real de `AgpErrorMapper.FromException`):

```csharp
using System;
using System.Net.Sockets;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class AgpErrorMapperTests
    {
        [Test]
        public void SocketConnectionRefused_MapeaA_MQTT001()
        {
            var ex = new SocketException((int)SocketError.ConnectionRefused);
            var err = AgpErrorMapper.FromException(ex);
            Assert.That(err.Code, Does.StartWith("AGP-MQTT"));
            Assert.That(err.Friendly, Is.Not.Empty);
        }

        [Test]
        public void ExcepcionDesconocida_MapeaA_SYS009()
        {
            var err = AgpErrorMapper.FromException(new InvalidOperationException("x"));
            Assert.That(err.Code, Is.EqualTo("AGP-SYS-009"));
        }

        [Test]
        public void InnerException_SeInspecciona()
        {
            var inner = new SocketException((int)SocketError.TimedOut);
            var err = AgpErrorMapper.FromException(new Exception("wrap", inner));
            Assert.That(err.Code, Does.StartWith("AGP-"));
        }
    }
}
```

- [ ] **Step 2: AgpJsonTests** — pinnear el contrato wire:

```csharp
using NUnit.Framework;
using AgroParallel.Models;

namespace AgroParallel.Services.Tests
{
    public class AgpJsonTests
    {
        private class Dto { public double MeterCal { get; set; } public string NombreLargo { get; set; } }

        [Test]
        public void Serializa_SnakeCase()
        {
            string json = AgpJson.Serialize(new Dto { MeterCal = 1.5, NombreLargo = "a" });
            Assert.That(json, Does.Contain("\"meter_cal\""));
            Assert.That(json, Does.Contain("\"nombre_largo\""));
        }

        [Test]
        public void Deserializa_ToleraPascalCaseViejo()
        {
            var dto = AgpJson.Deserialize<Dto>("{\"MeterCal\":2.5}");
            Assert.That(dto.MeterCal, Is.EqualTo(2.5));
        }

        [Test]
        public void JsonPropertyName_TienePrecedencia()
        {
            // usar un DTO real con atributo, ej. de ImplementoDto
            // Assert que el nombre del atributo aparece tal cual en el JSON
        }
    }
}
```

- [ ] **Step 3:** `dotnet test` → verde. **Commit** — `test: AgpErrorMapper y contrato snake_case de AgpJson`

### Task 13: Tests de OTA (semver guard + dedup) y MqttLiveServiceBase

**Files:**
- Create: `.../AgroParallel.Services.Tests/FirmwareOtaTests.cs`, `MqttLiveServiceBaseTests.cs`

- [ ] **Step 1: FirmwareOtaTests.** Leer `FirmwareOtaCoordinator.cs` y localizar la lógica anti-downgrade semver y el dedup ring de 64 (memoria: "OTA safety guards"). Si están en métodos privados, extraerlos a `internal static` + `[assembly: InternalsVisibleTo("AgroParallel.Services.Tests")]` en el csproj de Services:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="AgroParallel.Services.Tests" />
</ItemGroup>
```

Tests mínimos:
- `Downgrade_SeRechaza_SinOverride` (2.3.1 → 2.3.0 = false)
- `Downgrade_SeAcepta_ConAllowDowngrade`
- `MismaVersion_SeRechaza`
- `VersionInvalida_NoExplota`

- [ ] **Step 2: MqttLiveServiceBaseTests.** Con un fake de `INodoRegistryService` (implementación en memoria que dispara `MessageReceived`), subclase de prueba que acumula OnPayload:
- `TopicAjeno_SeIgnora` (topic "agp/otro/x/status_live" no llama OnPayload)
- `PayloadInvalido_NoExplota` (payload "no-json" → sin excepción)
- `UidYSubtopic_SeExtraenBien` ("agp/flow/AB12/status_live" → uid=AB12, subtopic=status_live)
- `Stop_LimpiaReadings`
- `ReadDouble_MultiKey_Fallback` y `ReadBool_AceptaStringYNumero`

- [ ] **Step 3:** `dotnet test` verde. **Commit** — `test: guards OTA y comportamiento de MqttLiveServiceBase`

---

## A5 — Validación de configs al guardar

### Task 14: Validador + integración en save-config

**Files:**
- Create: `MOD/ConfigValidation.cs`
- Modify: controllers con save-config (`FlowXController`, `SectionXController`, `LineXController`, `StormXController`, `VistaXController`, `QuantiXController`, `ImplementoController`)
- Test: `.../AgroParallel.Services.Tests/ConfigValidationTests.cs`

- [ ] **Step 1: Crear el validador** (simple, sin frameworks):

```csharp
using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Resultado de validar un config antes de persistir.</summary>
    public class ValidationResult
    {
        public List<string> Errores { get; } = new List<string>();
        public bool Ok => Errores.Count == 0;
        public void Requerir(bool cond, string msg) { if (!cond) Errores.Add(msg); }
    }

    public static class ConfigValidation
    {
        public static ValidationResult ValidarRango(double v, double min, double max, string campo)
        {
            var r = new ValidationResult();
            r.Requerir(v >= min && v <= max, $"{campo} fuera de rango [{min}..{max}]: {v}");
            return r;
        }
    }
}
```

- [ ] **Step 2: Reglas por módulo.** En cada controller, ANTES del merge+save, validar y responder 400 con `WriteErrorAsync(400, "AGP-CFG-001", "Config inválida", string.Join("; ", r.Errores))` si falla. Reglas mínimas (leer cada DTO para los nombres exactos):
  - Común: UIDs de nodos no vacíos y sin espacios; `habilitado` coherente (nodo habilitado ⇒ uid presente).
  - FlowX: `pwm_min` ∈ [0..4095] y `pwm_min < pwm_max`; `meter_cal > 0`; kp/ki/kd ≥ 0; ancho > 0.
  - QuantiX: pulsos/rev > 0; dosis objetivo ≥ 0; relación de tren > 0.
  - VistaX: surcos por nodo ∈ [1..32]; espaciamiento > 0; timeout ≥ 500 ms.
  - SectionX/LineX: cantidad de secciones ∈ [1..16]/[1..32] según hardware (7 salidas por nodo QuantiX es válido).
  - StormX: intervalos de log ≥ 1 s; umbrales min < max.
- [ ] **Step 3: Tests** `ConfigValidationTests.cs`: por cada regla, un caso válido y uno inválido (usar los DTOs reales).
- [ ] **Step 4: Ajustar JS**: en el catch del save de cada página, si la respuesta trae `error: "AGP-CFG-001"`, mostrar `mensaje` + `detalle` en el toast (patrón de error existente con `<details>`).
- [ ] **Step 5:** `dotnet test` verde + prueba manual de un save inválido en el Hub. **Commit** — `feat(config): validación al guardar con AGP-CFG-001 en todos los módulos`

---

## Cierre

- [x] **Task 15: Verificación final.** `dotnet build` + `dotnet test` de la sln completa; smoke manual del Hub (una página por módulo, guardar/recargar); revisar que `grep -rn "catch { }" SRV/` solo devuelva los comentados como intencionales.
- [x] **Task 16: Actualizar AUDITORIA-ARQUITECTURA.md** — corregir los hallazgos desactualizados (662 defensas → ~4; 313 catch → 23 en backend; tests: había infra NUnit + CI, faltaba solo cobertura de Services) y marcar Bloque A como completado.

## Riesgos y decisiones

1. **STJ 6→8:** compatible netstandard2.0; riesgo bajo. Si algo del stack viejo (AgIO/GPS legacy) referencia STJ 6 con binding conflict, alinear versiones en toda la sln.
2. **Cambio de wire format (A2):** el consumidor crítico extra es la PWA del celular (`/m/`) — incluirla en el grep de propiedades PascalCase de cada módulo (Task 7 Step 3 aplica también a `wwwroot/m/`).
3. **No tocar:** payloads MQTT a ESP32, requests a OrbitX cloud, archivos locales legacy.
4. **QuantiXMotorBridge/FlowXBridge:** fuera de alcance de A3 (bloque futuro).
