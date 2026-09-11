# CoreX-ECU — Actualización de firmware desde el Hub · Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agregar un botón "Actualizar firmware" en la página del CoreX-ECU que streamea el `.hex` del cache de firmwares a la unidad (Teensy 4.1) vía su OTA por red ya existente (`POST /api/firmware`), más limpieza de wording "ESP32"/"Teensy" en las pantallas.

**Architecture:** El firmware del CoreX-ECU ya expone OTA por red (FlasherX): `POST /api/firmware` recibe Intel-HEX, flashea y reboota; `GET /api/firmware` da estado. El Hub no lo proxea. Agregamos: (1) DTOs, (2) `FlashFirmwareAsync` en `CoreXEcuService` que lee el blob del cache (`FirmwareMirror`) y lo streamea a la unidad, (3) ruta `POST /corex-ecu/firmware/flash` en el controller, (4) UI + JS en la pestaña Config con verificación post-reboot. El `.hex` ya entra al cache por subida local (`firmwares.html`) o sync OrbitX — no se toca esa vía.

**Tech Stack:** C# netstandard2.0 (AgroParallel.Models / .Services / .WebHost), EmbedIO, System.Text.Json, HTML/CSS/JS vanilla servido desde `Build/AgroParallel/wwwroot`.

**Spec:** `docs/superpowers/specs/2026-06-08-corex-ecu-firmware-update-design.md`

**Notas de contexto (no obvias):**
- No hay framework de tests unitarios para estos controllers: se verifican con `curl` contra el Hub corriendo en `:5180` y en el browser. Los pasos de verificación usan eso.
- **Doble edición obligatoria:** todo HTML/JS se edita en la fuente (`SourceCode/.../AgroParallel.WebUI/wwwroot/...`) **y** en la copia de runtime (`Build/AgroParallel/wwwroot/...`). El backend se compila y se copian las DLLs a `Build/`.
- Los 3 proyectos son `netstandard2.0`. Build de `AgroParallel.WebHost.csproj` compila transitivamente Models + Services.
- El cache hoy tiene `firmware-cache/CoreX-ECU/1.16.0/firmware.bin` (1.3 MB = el `.hex`) + `manifest.json`. En Windows (FS case-insensitive) `FirmwareMirror.PathBin(cacheDir, "corex-ecu", "1.16.0")` resuelve a ese archivo.
- `GET /api/firmwares` agrupa por producto **en minúscula** → la key es `"corex-ecu"`.
- El JSON de salida del Hub se reescribe a camelCase (ver `CoreXEcuController.WriteJson`), por eso el JS lee `j.ok`, `j.errorCode`, `s.version`.
- La unidad de prueba está en `192.168.5.126`. El guard de guiado activo lo hace el propio firmware (409).

---

## File Structure

- **Modificar** `SourceCode/AgroParallel/Core/AgroParallel.Models/CoreXEcuDtos.cs` — agregar `CoreXEcuFlashRequestDto` + `CoreXEcuFlashResultDto`.
- **Modificar** `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/ICoreXEcuService.cs` — agregar `FlashFirmwareAsync`.
- **Modificar** `SourceCode/AgroParallel/Core/AgroParallel.Services/CoreXEcu/CoreXEcuService.cs` — implementar `FlashFirmwareAsync` + `using AgroParallel.OrbitX;`.
- **Modificar** `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/CoreXEcuController.cs` — ruta `POST /corex-ecu/firmware/flash`.
- **Modificar** `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/corex-ecu.html` (+ copia Build) — card "Actualizar firmware" + wording.
- **Modificar** `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/corex-ecu.js` (+ copia Build) — `loadFirmwareVersions`, `flashFirmware`, `pollUntilRebooted`, lock + wiring.
- **Modificar** wording: `firmwares.html`, `flowx.html`, `nodos.html` (+ copias Build).

---

## Task 1: DTOs de flash

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Models/CoreXEcuDtos.cs`

- [ ] **Step 1: Agregar los DTOs al final del namespace (antes del último `}`)**

Agregar después de `CoreXEcuOkResultDto` (busca `public sealed class CoreXEcuOkResultDto`) este bloque dentro del `namespace AgroParallel.Models`:

```csharp
    // ====================== /api/firmware (OTA Teensy) ======================
    // Request de la UI al Hub para flashear una versión cacheada a la unidad.
    public sealed class CoreXEcuFlashRequestDto
    {
        [JsonPropertyName("version")] public string Version { get; set; }
    }

    // Resultado del flasheo. El firmware responde 200 antes del flash_move()+reboot;
    // la verificación de que arrancó la versión nueva la hace la UI releyendo /status.
    public sealed class CoreXEcuFlashResultDto
    {
        [JsonPropertyName("ok")]         public bool Ok { get; set; }
        [JsonPropertyName("version")]    public string Version { get; set; }
        [JsonPropertyName("bytes_sent")] public long BytesSent { get; set; }
        [JsonPropertyName("error_code")] public string ErrorCode { get; set; }
        [JsonPropertyName("error")]      public string Error { get; set; }
        [JsonPropertyName("detail")]     public string Detail { get; set; }
    }
```

- [ ] **Step 2: Verificar que compila el proyecto Models**

Run: `dotnet build "SourceCode/AgroParallel/Core/AgroParallel.Models/AgroParallel.Models.csproj" -c Release -v q`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Models/CoreXEcuDtos.cs
git commit -m "feat(corex-ecu): DTOs para flasheo de firmware"
```

---

## Task 2: Firma en la interfaz

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/ICoreXEcuService.cs`

- [ ] **Step 1: Agregar el método a la interfaz**

Dentro de `interface ICoreXEcuService`, después de la línea de `CancelSweepAsync();`, agregar:

```csharp

        /// <summary>POST /api/firmware (Teensy) — streamea el .hex cacheado del producto
        /// "corex-ecu" a la unidad y reboota. 409 si el guiado está activo, 400 si el
        /// HEX es inválido / no es de este equipo. La verificación de versión nueva la
        /// hace la UI releyendo /status tras el reboot.</summary>
        Task<CoreXEcuFlashResultDto> FlashFirmwareAsync(string version);
```

- [ ] **Step 2: Verificar que compila (fallará la implementación — esperado)**

Run: `dotnet build "SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj" -c Release -v q`
Expected: FALLA con `'CoreXEcuService' does not implement interface member 'ICoreXEcuService.FlashFirmwareAsync(string)'`. Esto confirma que la interfaz quedó declarada; la Task 3 lo implementa.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/ICoreXEcuService.cs
git commit -m "feat(corex-ecu): firma FlashFirmwareAsync en ICoreXEcuService"
```

---

## Task 3: Implementación `FlashFirmwareAsync`

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/CoreXEcu/CoreXEcuService.cs`

- [ ] **Step 1: Agregar el using de OrbitX**

En la lista de `using` del archivo (arriba de todo), agregar junto a los demás:

```csharp
using AgroParallel.OrbitX;
```

(Provee `FirmwareMirror` y `OrbitXConfig`, que viven en el assembly AgroParallel.Services bajo el namespace `AgroParallel.OrbitX`.)

- [ ] **Step 2: Implementar el método**

Agregar después del método `MotorTestAsync` (busca `public async Task<CoreXEcuMotorTestResultDto> MotorTestAsync`), antes de `MotorStopAsync`:

```csharp
        // -------- /api/firmware (OTA Teensy via FlasherX) -----------------------

        public async Task<CoreXEcuFlashResultDto> FlashFirmwareAsync(string version)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };
            if (string.IsNullOrWhiteSpace(version))
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-103", Error = "Falta la versión a flashear." };

            // Resolver el .hex en el MISMO cache que sirve el Firmware Manager.
            OrbitXConfig ocfg;
            try { ocfg = OrbitXConfig.Load(); } catch { ocfg = new OrbitXConfig(); }
            string cacheDir = FirmwareMirror.ResolveCacheDir(ocfg);
            string hexPath = FirmwareMirror.PathBin(cacheDir, "corex-ecu", version);
            if (!File.Exists(hexPath))
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-SYS-009", Error = "No encontré ese firmware en el cache. Subilo o sincronizalo primero.", Detail = hexPath };

            string url = BaseUrl(cfg) + "/api/firmware";
            long size = new FileInfo(hexPath).Length;

            // El flasheo bloquea unos segundos: el firmware consume el body char a
            // char (yield al IMU) y recién responde 200 antes del flash_move(). El
            // _http compartido tiene Timeout=15s; usamos un cliente dedicado con
            // timeout amplio para no cortar a mitad de un .hex de ~1.3 MB.
            try
            {
                using (var flashHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                using (var fs = File.OpenRead(hexPath))
                using (var content = new StreamContent(fs))
                {
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = size;

                    var resp = await flashHttp.PostAsync(url, content).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                        return new CoreXEcuFlashResultDto { Ok = true, Version = version, BytesSent = size };

                    var errDto = ParseFirmwareError(body);
                    int code = (int)resp.StatusCode;
                    if (code == 409)
                        return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-409", Error = "No se puede actualizar con el guiado activo. Pausá la dirección automática primero.", Detail = errDto.Detail ?? errDto.Error ?? "" };
                    if (code == 400)
                        return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "El firmware rechazó el archivo (HEX inválido o no es de este equipo).", Detail = errDto.Error ?? errDto.Detail ?? "" };
                    if (code == 413)
                        return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "El archivo es demasiado grande para la unidad.", Detail = errDto.Error ?? "" };
                    return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "HTTP " + code, Detail = errDto.Detail ?? errDto.Error ?? "" };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout enviando el firmware." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }
```

Nota: `BaseUrl`, `ParseFirmwareError`, `AgpErrorMapper`, `LoadConfig` ya existen en esta clase (los usa `MotorTestAsync`). `FirmwareMirror.ResolveCacheDir` y `PathBin` ya existen.

- [ ] **Step 3: Verificar que compila Services**

Run: `dotnet build "SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj" -c Release -v q`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/CoreXEcu/CoreXEcuService.cs
git commit -m "feat(corex-ecu): FlashFirmwareAsync streamea el .hex del cache a la unidad"
```

---

## Task 4: Ruta en el controller

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/CoreXEcuController.cs`

- [ ] **Step 1: Agregar la ruta**

Después del método `MotorStop()` (busca `public async Task MotorStop()` y su cierre `}`), agregar:

```csharp

        // ====================== Firmware OTA (Teensy) =======================

        [Route(HttpVerbs.Post, "/corex-ecu/firmware/flash")]
        public async Task FlashFirmware()
        {
            if (_svc == null) { await WriteServiceUnavailable().ConfigureAwait(false); return; }

            string body;
            using (var sr = new StreamReader(HttpContext.Request.InputStream))
                body = await sr.ReadToEndAsync().ConfigureAwait(false);

            CoreXEcuFlashRequestDto req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CoreXEcuFlashRequestDto>(body, ReadOpts);
            }
            catch { /* req queda null */ }

            if (req == null || string.IsNullOrWhiteSpace(req.Version))
            {
                await WriteJson(new { ok = false, errorCode = "AGP-NET-103", error = "Body inválido. Esperaba { version }." }).ConfigureAwait(false);
                return;
            }

            var dto = await _svc.FlashFirmwareAsync(req.Version).ConfigureAwait(false);
            await WriteJson(dto).ConfigureAwait(false);
        }
```

- [ ] **Step 2: Documentar la ruta en el comentario de cabecera**

En el bloque de comentario del tope (la lista de endpoints), agregar la línea después de la de `motor/stop`:

```csharp
//   POST   /api/corex-ecu/firmware/flash          (body { version }) → CoreXEcuFlashResultDto
```

- [ ] **Step 3: Verificar que compila WebHost (transitivamente Models + Services)**

Run: `dotnet build "SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgroParallel.WebHost.csproj" -c Release -v q`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/CoreXEcuController.cs
git commit -m "feat(corex-ecu): ruta POST /corex-ecu/firmware/flash"
```

---

## Task 5: Desplegar DLLs a Build

**Files:**
- Modify (copia): `Build/AgroParallel.Models.dll`, `Build/AgroParallel.Services.dll`, `Build/AgroParallel.WebHost.dll`

- [ ] **Step 1: Copiar las 3 DLLs compiladas a Build**

```bash
cd "G:/AgroParallel/Productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
SRC="SourceCode/AgroParallel/Web/AgroParallel.WebHost/bin/Release/netstandard2.0"
cp "$SRC/AgroParallel.Models.dll"  Build/AgroParallel.Models.dll
cp "$SRC/AgroParallel.Services.dll" Build/AgroParallel.Services.dll
cp "$SRC/AgroParallel.WebHost.dll"  Build/AgroParallel.WebHost.dll
```

- [ ] **Step 2: Verificar timestamps actualizados**

Run: `ls -la Build/AgroParallel.Models.dll Build/AgroParallel.Services.dll Build/AgroParallel.WebHost.dll`
Expected: las 3 con fecha/hora de ahora.

(No se commitean las DLLs de Build salvo que el repo ya las versione — verificar con `git status Build/*.dll`; si aparecen como tracked, commitearlas; si están en `.gitignore`, omitir.)

---

## Task 6: UI — card "Actualizar firmware"

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/corex-ecu.html`
- Modify (copia): `Build/AgroParallel/wwwroot/pages/corex-ecu.html`

- [ ] **Step 1: Agregar el card al final de la pestaña Config (fuente)**

En `SourceCode/.../wwwroot/pages/corex-ecu.html`, dentro de `<section class="tab-pane" data-tab="config">`, después del `<div class="card">…</div>` que cierra en la línea del botón "Guardar configuración" (busca `id="btnSaveCfg"` y su `</div></div>` de cierre del card), e **inmediatamente antes** del `</section>` de cierre de la pestaña config, agregar:

```html
      <!-- ============ Actualizar firmware ============ -->
      <div class="card" style="margin-top: var(--agp-sp-3);">
        <h2>Actualizar firmware</h2>
        <div class="row"><span>Versión instalada</span><strong id="fwCurrent" style="color:var(--agp-text)">—</strong></div>
        <div style="display:flex; align-items:flex-end; gap:12px; margin-top: var(--agp-sp-3);">
          <div style="display:flex; flex-direction:column; gap:4px;">
            <label class="subtitle">Versión a instalar</label>
            <select id="fwVersionSelect" class="input" style="min-width:180px;"></select>
          </div>
          <button type="button" class="btn" id="btnFlashFw">Actualizar</button>
        </div>
        <div class="subtitle" id="fwFlashMsg" style="margin-top: var(--agp-sp-2);"></div>
        <div class="subtitle" id="fwEmptyHint" style="margin-top: var(--agp-sp-2); display:none;">
          No hay firmwares en el cache. Subí un <code>.hex</code> o sincronizá desde
          <a href="firmwares.html">Firmwares</a>.
        </div>
        <div class="subtitle" id="fwLockWarn" style="margin-top: var(--agp-sp-2); display:none;">
          No se puede actualizar con el guiado activo. Pausá la dirección automática primero.
        </div>
      </div>
```

- [ ] **Step 2: Limpiar wording "Teensy" en este archivo (fuente)**

En el mismo archivo, hacer estos reemplazos exactos de **texto de pantalla**:
- `Autosteer · Teensy 4.1 · firmware` → `Autosteer · CoreX-ECU · firmware`
- `<h2>Conexión con el Teensy</h2>` → `<h2>Conexión con el CoreX-ECU</h2>`
- Las dos ocurrencias de `IP del Teensy` (una en `<div class="row"><span>IP del Teensy</span>` y otra en `<label class="subtitle">IP del Teensy</label>`) → `IP de la unidad`

- [ ] **Step 3: Copiar el HTML editado a Build**

```bash
cp "SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/corex-ecu.html" \
   "Build/AgroParallel/wwwroot/pages/corex-ecu.html"
```

- [ ] **Step 4: Verificar que el card existe en la copia de Build**

Run: `grep -c "fwVersionSelect\|Conexión con el CoreX-ECU" Build/AgroParallel/wwwroot/pages/corex-ecu.html`
Expected: `2` (o más) — confirma card + wording aplicados en runtime.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/corex-ecu.html
git commit -m "feat(corex-ecu): card Actualizar firmware en pestaña Config + wording"
```

(Si `Build/AgroParallel/wwwroot/...` está versionado, incluirlo en el `git add`; si está ignorado, omitir.)

---

## Task 7: JS — cargar versiones, flashear, verificar reboot

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/corex-ecu.js`
- Modify (copia): `Build/AgroParallel/wwwroot/js/corex-ecu.js`

- [ ] **Step 1: Agregar las funciones de firmware (fuente)**

En `SourceCode/.../js/corex-ecu.js`, justo antes del comentario `// -------- Wiring --------` (que precede a `document.addEventListener('DOMContentLoaded'`), agregar:

```javascript
  // -------- Actualizar firmware -------------------------------------------
  var flashing = false;

  function loadFirmwareVersions() {
    fetch('/api/firmwares', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        var sel = $('fwVersionSelect');
        var hint = $('fwEmptyHint');
        var btn = $('btnFlashFw');
        if (!sel || !btn) return;
        sel.innerHTML = '';
        var prod = null;
        if (j && j.productos) {
          prod = j.productos.filter(function (p) {
            return (p.producto || '').toLowerCase() === 'corex-ecu';
          })[0];
        }
        var vers = (prod && prod.versiones) || [];
        if (!vers.length) {
          if (hint) hint.style.display = '';
          btn.disabled = true;
          return;
        }
        if (hint) hint.style.display = 'none';
        vers.forEach(function (v) {
          var o = document.createElement('option');
          o.value = v.version;
          o.textContent = v.version + (v.local ? '' : ' (no descargado)');
          o.disabled = !v.local;   // sólo se puede flashear lo que está en disco
          sel.appendChild(o);
        });
        // Habilitar salvo que el guiado esté bloqueando o un flash en curso.
        btn.disabled = motorLocked || flashing;
      })
      .catch(function () {
        var hint = $('fwEmptyHint');
        if (hint) hint.style.display = '';
        var btn = $('btnFlashFw');
        if (btn) btn.disabled = true;
      });
  }

  function flashFirmware() {
    if (flashing) return;
    var sel = $('fwVersionSelect');
    var version = sel && sel.value;
    if (!version) return;
    if (motorLocked) {
      $('fwFlashMsg').textContent = 'No se puede actualizar con el guiado activo.';
      return;
    }
    if (!window.confirm('¿Actualizar el CoreX-ECU a la versión ' + version +
        '?\n\nLa unidad se va a reiniciar. No la apagues durante el proceso.')) {
      return;
    }
    flashing = true;
    $('btnFlashFw').disabled = true;
    sel.disabled = true;
    $('fwFlashMsg').textContent = 'Enviando firmware… no apagues la unidad (puede tardar ~30 s).';

    fetch('/api/corex-ecu/firmware/flash', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ version: version })
    })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        if (j && j.ok) {
          $('fwFlashMsg').textContent = 'Firmware enviado. La unidad se está reiniciando…';
          pollUntilRebooted(version);
        } else {
          flashing = false;
          $('btnFlashFw').disabled = false;
          sel.disabled = false;
          var code = (j && (j.errorCode || j.error_code)) || 'AGP-NET-201';
          $('fwFlashMsg').textContent = code + ' · ' + ((j && j.error) || 'No se pudo actualizar.');
        }
      })
      .catch(function (e) {
        flashing = false;
        $('btnFlashFw').disabled = false;
        sel.disabled = false;
        $('fwFlashMsg').textContent = 'Error: ' + e;
      });
  }

  // Tras el 200 del flash, la unidad rebootea (~3-5 s inalcanzable). El poll
  // normal de /status (cada 500 ms) actualiza lastSnapshot; acá esperamos a ver
  // la versión nueva o cortamos por timeout.
  function pollUntilRebooted(target) {
    var started = Date.now();
    var TIMEOUT_MS = 60000;
    var t = setInterval(function () {
      var s = lastSnapshot;
      var done = s && s.ok && s.version === target;
      if (done) {
        clearInterval(t);
        flashing = false;
        $('fwVersionSelect').disabled = false;
        $('fwFlashMsg').textContent = '✓ Actualizado a la versión ' + target + '.';
        loadFirmwareVersions();
        return;
      }
      if (Date.now() - started > TIMEOUT_MS) {
        clearInterval(t);
        flashing = false;
        $('btnFlashFw').disabled = motorLocked;
        $('fwVersionSelect').disabled = false;
        $('fwFlashMsg').textContent = 'No pude confirmar la versión nueva. Revisá el estado de la unidad manualmente.';
      }
    }, 2000);
  }
```

- [ ] **Step 2: Reflejar la versión instalada y el lock en `updateMotorLock`**

En `function updateMotorLock(s)`, dentro del bloque `if (locked !== motorLocked) { ... }`, después de las líneas que deshabilitan `customBtn`/`customInput`, agregar:

```javascript
      // El botón de actualizar firmware comparte el lock por guiado activo.
      var flashBtn = $('btnFlashFw');
      if (flashBtn) {
        if (locked) flashBtn.disabled = true;
        else flashBtn.disabled = flashing || ($('fwVersionSelect') && $('fwVersionSelect').options.length === 0);
      }
```

Y al inicio de `updateMotorLock` (junto a la línea que togglea `motorLockWarn`), agregar el toggle del warn de firmware (fuera del guard de transición, para que siga el estado en vivo):

```javascript
    var fwWarn = $('fwLockWarn');
    if (fwWarn) fwWarn.style.display = locked ? '' : 'none';
```

- [ ] **Step 3: Mostrar la versión instalada en el card**

En `function renderStatus(s)`, después de la línea `$('hdrFw').textContent = ...` (la del header), agregar:

```javascript
    var fwc = $('fwCurrent');
    if (fwc) fwc.textContent = s.version || '—';
```

- [ ] **Step 4: Cablear en DOMContentLoaded**

Dentro de `document.addEventListener('DOMContentLoaded', function () { ... })`, después de `initCustomMotor();`, agregar:

```javascript
    var flashBtn = $('btnFlashFw');
    if (flashBtn) flashBtn.addEventListener('click', flashFirmware);
    loadFirmwareVersions();
```

- [ ] **Step 5: Copiar el JS editado a Build**

```bash
cp "SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/corex-ecu.js" \
   "Build/AgroParallel/wwwroot/js/corex-ecu.js"
```

- [ ] **Step 6: Verificar que las funciones están en la copia de Build**

Run: `grep -c "function loadFirmwareVersions\|function flashFirmware\|function pollUntilRebooted" Build/AgroParallel/wwwroot/js/corex-ecu.js`
Expected: `3`.

- [ ] **Step 7: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/corex-ecu.js
git commit -m "feat(corex-ecu): JS de actualización de firmware (cargar versiones, flashear, verificar reboot)"
```

---

## Task 8: Limpieza de wording en otras pantallas

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/firmwares.html` (+ copia Build)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/flowx.html` (+ copia Build)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/nodos.html` (+ copia Build)

- [ ] **Step 1: `firmwares.html` (fuente) — reemplazos de texto**

- `.bin (ESP32) · .hex (Teensy/CoreX-ECU) · .zip (PilotX) · máx. 8 MB` → `.bin · .hex · .zip · máx. 8 MB`
- `<optgroup label="Nodos ESP32 (.bin)">` → `<optgroup label="Nodos (.bin)">`
- `<option value="corex-ecu">CoreX-ECU (Teensy)</option>` → `<option value="corex-ecu">CoreX-ECU</option>`

- [ ] **Step 2: `flowx.html` (fuente) — reemplazo de texto**

- `Se guarda en LittleFS del ESP32.` → `Se guarda en la memoria del nodo.`

- [ ] **Step 3: `nodos.html` (fuente) — reemplazo de texto**

- `Si tu ESP32 no aparece arriba:` → `Si tu nodo no aparece arriba:`

- [ ] **Step 4: Copiar los 3 HTML a Build**

```bash
cd "G:/AgroParallel/Productos/CentriX-Spark/Software/App_PC/AgOpenGPS"
for f in firmwares.html flowx.html nodos.html; do
  cp "SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/$f" \
     "Build/AgroParallel/wwwroot/pages/$f"
done
```

- [ ] **Step 5: Verificar que no queda "ESP32"/"Teensy" en texto de pantalla de runtime**

Run: `grep -rn "ESP32\|Teensy" Build/AgroParallel/wwwroot/pages/*.html`
Expected: sin resultados (0 líneas). Si aparece alguno, revisar que sea sólo en comentarios HTML (que también conviene limpiar) y reemplazar.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/firmwares.html \
        SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/flowx.html \
        SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/nodos.html
git commit -m "chore(ui): sacar wording ESP32/Teensy de las pantallas (nombres de producto)"
```

---

## Task 9: Verificación de integración (Hub corriendo + unidad real)

**Files:** ninguno (smoke test)

- [ ] **Step 1: Reiniciar el Hub con las DLLs nuevas**

Cerrar AgOpenGPS si está abierto, y relanzar AgIO + AgOpenGPS:

```bash
cd "G:/AgroParallel/Productos/CentriX-Spark/Software/App_PC/AgOpenGPS/Build"
powershell -Command "Start-Process -FilePath '.\AgIO.exe' -PassThru | Select-Object Id"
powershell -Command "Start-Process -FilePath '.\AgOpenGPS.exe' -PassThru | Select-Object Id"
```

Esperar ~10 s a que el WebHost levante en `:5180`.

- [ ] **Step 2: Verificar que la ruta responde (sin flashear todavía)**

Run (versión inexistente → debe dar error controlado, NO 404 de ruta):
```bash
curl -s -X POST http://127.0.0.1:5180/api/corex-ecu/firmware/flash \
  -H "Content-Type: application/json" -d '{"version":"0.0.0-noexiste"}'
```
Expected: JSON `{"ok":false,"errorCode":"AGP-SYS-009",...}` (no encontrado en cache). Si devuelve 404/HTML, la ruta no se registró → revisar Task 4 + redeploy DLL.

- [ ] **Step 3: Verificar que la UI lista la versión**

Abrir en el Hub la página CoreX-ECU → pestaña Conexión → card "Actualizar firmware". El `<select>` debe mostrar `1.16.0`. El header debe decir "Autosteer · CoreX-ECU · firmware …" (sin "Teensy").

- [ ] **Step 4: Flasheo real (con la unidad en 192.168.5.126, guiado en pausa)**

Apretar "Actualizar" → confirmar. Esperado: "Enviando firmware…" → "reiniciando…" → "✓ Actualizado a la versión 1.16.0." en menos de ~40 s. Verificar en logs serie/`GET /api/corex-ecu/status` que `version` sea la nueva.

- [ ] **Step 5: Verificar el guard de guiado**

Con guiado activo (autosteer ON desde PilotX), el botón "Actualizar" debe quedar deshabilitado y el aviso `fwLockWarn` visible. Un `curl` directo al flash con guiado activo debe devolver `errorCode AGP-NET-409`.

- [ ] **Step 6: Commit final (si hubo ajustes durante el smoke test)**

```bash
git add -A
git commit -m "test(corex-ecu): ajustes tras verificación de flasheo end-to-end"
```

---

## Self-Review (cobertura del spec)

- OTA por red ya existente en firmware → Tasks 3 (lo consume). ✓
- `.hex` entra por subida local + sync OrbitX → ya existe; el flash lee del mismo cache (Task 3). ✓
- Botón en la página del CoreX-ECU (Enfoque A) → Task 6 (UI) + Task 7 (JS). ✓
- Backend proxy con streaming + Content-Length + timeout largo → Task 3. ✓
- Mapeo de errores (409 guiado / 400 HEX / 413 / timeout) → Task 3. ✓
- Verificación post-reboot releyendo /status → Task 7 (`pollUntilRebooted`). ✓
- Lock por guiado en el botón → Task 7 (`updateMotorLock`). ✓
- Caso sin versiones → Task 7 (`fwEmptyHint` + botón disabled). ✓
- Limpieza de wording ESP32/Teensy → Tasks 6 + 8. ✓
- Doble edición fuente + Build → Tasks 6/7/8 (copias). ✓
- Pruebas (build + flash real + guard) → Task 9. ✓

Fuera de alcance (confirmado en spec): % de progreso real durante el streaming; pipeline OTA MQTT para el CoreX-ECU.
