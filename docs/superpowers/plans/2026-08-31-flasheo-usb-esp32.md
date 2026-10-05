# Flasheo de módulos ESP32 por USB — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agregar a PilotX un sector para flashear un ESP32 (FlowX y demás X-*) por cable USB con esptool.exe, con los drivers USB-serial bundleados, sin depender de internet ni del OTA por MQTT.

**Architecture:** Backend nuevo `UsbFlashService` (AgroParallel.Services) que envuelve `esptool.exe` (proceso hijo) + `UsbFlashController` (AgroParallel.WebHost, EmbedIO) que lo expone en `/api/usb/*`. El firmware sale del cache local de firmwares que ya existe (alimentado por upload local u OrbitX). UI nativa: sección "Flashear por USB" dentro del `FirmwaresPanel` (Avalonia) que ya existe, hablando con el backend vía un `UsbFlashClient`.

**Tech Stack:** C# / .NET 9 (Engine + WebHost), EmbedIO 3.5.2, Avalonia, `esptool.exe` v5.3.1 (bundleado), `pnputil` (drivers), `System.IO.Ports` / registro para puertos COM.

## Global Constraints

- **Wire en snake_case** vía `AgpJson` / `AgpControllerBase` (todos los controllers heredan de `AgpControllerBase`; usar `WriteJsonAsync`, `ReadJsonBodyAsync<T>`, `WriteErrorAsync`).
- **Branding**: en UI/logs/comentarios nuevos usar PilotX / Agro Parallel / CoreX (no AOG/AgOpenGPS/AgIO). No tocar namespaces/clases.
- **Castellano rioplatense** en textos de UI, comentarios nuevos, logs, commits.
- **Regex de validación** (ya usados en `FirmwaresController`): producto `^[a-zA-Z][a-zA-Z0-9-]{1,31}$`, versión `^[a-zA-Z0-9][a-zA-Z0-9._-]{0,31}$`. Reusar idénticos (anti path-traversal).
- **Nunca shell-concat**: lanzar procesos con `ProcessStartInfo.ArgumentList` (un argumento por item), jamás construir una línea de comando con strings del cliente.
- **Un flasheo a la vez**: el servicio es singleton con lock; segundo flasheo concurrente → 409.
- **Verificar antes de declarar**: compilar y correr `AgroParallel.Services.Tests` antes de dar por hecha una tarea. El flasheo real contra hardware es prueba de banco (no se declara "anda" sin el nodo).
- **esptool/drivers ya versionados** en `Tools/esptool/esptool.exe` y `Tools/usb-drivers/cp210x/` (commit be81b9c9). El CH340 está pendiente (dir vacío) — no bloquea.

---

## File Structure

- `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbFlashService.cs` — CREATE. Núcleo: listar puertos, armar args, correr esptool, parsear progreso, exponer estado. Sin dependencias de EmbedIO ni Avalonia (testeable).
- `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/EsptoolOutputParser.cs` — CREATE. Parser puro de stdout de esptool → `(fase, pct)`. Static, sin estado.
- `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbDriverInstaller.cs` — CREATE. Instala drivers con pnputil (elevado).
- `SourceCode/AgroParallel/Core/AgroParallel.Models/UsbFlashDtos.cs` — CREATE. DTOs snake_case del wire.
- `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/UsbFlashController.cs` — CREATE. Endpoints `/api/usb/*`.
- `SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs:307` — MODIFY. Registrar el controller.
- `SourceCode/AgroParallel/Core/AgroParallel.Services/Common/FirmwareMirror.cs` — MODIFY. Agregar `PathFactory` + `HasFactory` al listado.
- `SourceCode/AgroParallel/Core/AgroParallel.Services/Common/AgpErrorMapper.cs` — MODIFY. Códigos `AGP-USB-001..006`.
- `SourceCode/PilotX.UI/Services/UsbFlashClient.cs` — CREATE. Cliente HTTP de la UI.
- `SourceCode/PilotX.UI/Views/FirmwaresPanel.axaml.cs` — MODIFY. Sección "Flashear por USB".
- `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html` — MODIFY. Documentar el flujo.
- `build.ps1` — MODIFY. Copiar `Tools/esptool` + `Tools/usb-drivers` a `Build/Engine/tools/`.
- `SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs` — CREATE. Tests del parser y del arg builder.

---

## Task 1: build.ps1 copia esptool + drivers al Build

**Files:**
- Modify: `build.ps1` (sección de copia al Build, después de que el Engine se publica en `Build/Engine`)

**Interfaces:**
- Produces: en runtime `AppContext.BaseDirectory` del Engine (= `Build/Engine`) tiene `tools/esptool/esptool.exe` y `tools/usb-drivers/cp210x/silabser.inf`.

- [ ] **Step 1: Ubicar en build.ps1 dónde se copia el Engine a Build/Engine**

Run: `grep -n "Engine" build.ps1 | head`
Buscar la línea que copia/publica el Engine a `$OutDir\Engine` (o `Build\Engine`).

- [ ] **Step 2: Agregar copia de tools después de esa línea**

```powershell
# Herramientas del flasheo por USB: esptool + drivers USB-serial.
# Viajan en el build para que la pantalla flashee sin internet.
$toolsSrc = Join-Path $root "Tools"
$toolsDst = Join-Path $OutDir "Engine\tools"
foreach ($t in @("esptool", "usb-drivers")) {
    $src = Join-Path $toolsSrc $t
    if (Test-Path $src) {
        $dst = Join-Path $toolsDst $t
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item "$src\*" $dst -Recurse -Force
        Write-Host "Copiado tools/$t -> $dst" -ForegroundColor DarkGray
    }
}
```

- [ ] **Step 3: Correr el build y verificar que las tools quedaron en el paquete**

Run: `powershell -ExecutionPolicy Bypass -File build.ps1 -SinLinux`
Después: `ls Build/Engine/tools/esptool/esptool.exe && ls Build/Engine/tools/usb-drivers/cp210x/silabser.inf`
Expected: ambos archivos existen.

- [ ] **Step 4: Commit**

```bash
git add build.ps1
git commit -m "build(usb): copiar esptool + drivers USB a Build/Engine/tools"
```

---

## Task 2: FirmwareMirror expone el factory.bin

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/Common/FirmwareMirror.cs`
- Test: `SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs` (se crea acá, se amplía en Task 4)

**Interfaces:**
- Consumes: `FirmwareMirror.DirVersion(cacheDir, prod, ver)` (ya existe), `PathBin(cacheDir, prod, ver)` (ya existe).
- Produces: `FirmwareMirror.PathFactory(string cacheDir, string producto, string version) : string` → ruta de `factory.bin` en el dir de la versión.

- [ ] **Step 1: Escribir el test que fija la ruta del factory**

```csharp
[Fact]
public void PathFactory_apunta_a_factory_bin_en_el_dir_de_version()
{
    string cache = Path.Combine(Path.GetTempPath(), "agp_fw_test");
    string p = AgroParallel.OrbitX.FirmwareMirror.PathFactory(cache, "flowx", "1.0.0");
    Assert.EndsWith(Path.Combine("flowx", "1.0.0", "factory.bin"), p);
}
```

- [ ] **Step 2: Correr y verificar que falla (método inexistente)**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release --filter PathFactory`
Expected: no compila / método no existe.

- [ ] **Step 3: Implementar PathFactory junto a PathBin**

Ubicar `PathBin` en FirmwareMirror.cs y agregar al lado:
```csharp
/// <summary>Ruta del factory.bin (merge bootloader+particiones+boot_app0+app)
/// usado por el flasheo USB en modo completo. Puede no existir.</summary>
public static string PathFactory(string cacheDir, string producto, string version)
    => Path.Combine(DirVersion(cacheDir, producto, version), "factory.bin");
```

- [ ] **Step 4: Correr y verificar que pasa**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release --filter PathFactory`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Common/FirmwareMirror.cs SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs
git commit -m "feat(firmware): FirmwareMirror.PathFactory para el factory.bin del flasheo USB"
```

---

## Task 3: DTOs del wire USB

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Models/UsbFlashDtos.cs`

**Interfaces:**
- Produces: `PuertoComDto { string port; string descripcion }`, `UsbFlashRequest { string producto; string version; string puerto; string modo; bool borrar_antes }`, `UsbFlashEstadoDto { bool en_curso; string fase; int pct; string resultado; string codigo; string log }`, `UsbDriverRequest { string driver }`. Todos con `[JsonPropertyName]` snake_case (aunque AgpJson ya hace snake, se declaran explícitos para claridad).

- [ ] **Step 1: Crear el archivo con los DTOs**

```csharp
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    public sealed class PuertoComDto
    {
        [JsonPropertyName("port")]        public string Port { get; set; }
        [JsonPropertyName("descripcion")] public string Descripcion { get; set; }
    }

    public sealed class UsbFlashRequest
    {
        [JsonPropertyName("producto")]     public string Producto { get; set; }
        [JsonPropertyName("version")]      public string Version { get; set; }
        [JsonPropertyName("puerto")]       public string Puerto { get; set; }
        [JsonPropertyName("modo")]         public string Modo { get; set; }        // "completo" | "app"
        [JsonPropertyName("borrar_antes")] public bool BorrarAntes { get; set; }
    }

    public sealed class UsbFlashEstadoDto
    {
        [JsonPropertyName("en_curso")]  public bool EnCurso { get; set; }
        [JsonPropertyName("fase")]      public string Fase { get; set; }       // conectando|borrando|escribiendo|verificando|reset|idle
        [JsonPropertyName("pct")]       public int Pct { get; set; }
        [JsonPropertyName("resultado")] public string Resultado { get; set; }  // null | "ok" | "fail"
        [JsonPropertyName("codigo")]    public string Codigo { get; set; }     // null | "AGP-USB-00X"
        [JsonPropertyName("log")]       public string Log { get; set; }
    }

    public sealed class UsbDriverRequest
    {
        [JsonPropertyName("driver")] public string Driver { get; set; }  // "cp210x" | "ch340" | "ambos"
    }
}
```

- [ ] **Step 2: Compilar el proyecto Models**

Run: `dotnet build SourceCode/AgroParallel/Core/AgroParallel.Models/AgroParallel.Models.csproj -c Release`
Expected: Compilación correcta, 0 errores.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Models/UsbFlashDtos.cs
git commit -m "feat(usb): DTOs snake_case del flasheo por USB"
```

---

## Task 4: EsptoolOutputParser (parser puro de progreso)

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/EsptoolOutputParser.cs`
- Test: `SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs`

**Interfaces:**
- Produces: `EsptoolOutputParser.Parse(string linea, ref string fase, ref int pct) : void` — actualiza fase/pct según la línea. `EsptoolOutputParser.ClasificarError(string log) : string` → devuelve código `AGP-USB-00X` o null.

- [ ] **Step 1: Escribir los tests del parser**

```csharp
[Theory]
[InlineData("Connecting........", "conectando", 0)]
[InlineData("Erasing flash...", "borrando", 0)]
[InlineData("Writing at 0x00010000... (37 %)", "escribiendo", 37)]
[InlineData("Hash of data verified.", "verificando", 100)]
[InlineData("Hard resetting via RTS pin...", "reset", 100)]
public void Parse_actualiza_fase_y_pct(string linea, string faseEsp, int pctEsp)
{
    string fase = "idle"; int pct = 0;
    AgroParallel.Usb.EsptoolOutputParser.Parse(linea, ref fase, ref pct);
    Assert.Equal(faseEsp, fase);
    Assert.Equal(pctEsp, pct);
}

[Theory]
[InlineData("A fatal error occurred: Failed to connect to ESP32: No serial data received.", "AGP-USB-002")]
[InlineData("A fatal error occurred: Could not open COM5, the port doesn't exist", "AGP-USB-001")]
[InlineData("Serial port COM5: Access is denied.", "AGP-USB-001")]
[InlineData("Hash of data verified.", null)]
public void ClasificarError_mapea_fallas_conocidas(string log, string codigoEsp)
{
    Assert.Equal(codigoEsp, AgroParallel.Usb.EsptoolOutputParser.ClasificarError(log));
}
```

- [ ] **Step 2: Correr y verificar que fallan (clase inexistente)**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release --filter Esptool`
Expected: no compila.

- [ ] **Step 3: Implementar el parser**

```csharp
using System.Text.RegularExpressions;

namespace AgroParallel.Usb
{
    // Parser puro (sin estado, sin IO) del stdout de esptool v5.x. Traduce las
    // líneas a una fase en castellano + un porcentaje 0..100 para la barra de
    // progreso de la UI, y clasifica las fallas conocidas a códigos AGP-USB.
    public static class EsptoolOutputParser
    {
        private static readonly Regex RxPct = new Regex(@"\((\d+)\s*%\)", RegexOptions.Compiled);

        public static void Parse(string linea, ref string fase, ref int pct)
        {
            if (string.IsNullOrEmpty(linea)) return;
            if (linea.Contains("Connecting"))        { fase = "conectando"; }
            else if (linea.Contains("Erasing") || linea.Contains("Erase")) { fase = "borrando"; }
            else if (linea.Contains("Writing at") || linea.StartsWith("Wrote")) { fase = "escribiendo"; }
            else if (linea.Contains("Hash of data verified")) { fase = "verificando"; pct = 100; }
            else if (linea.Contains("Hard resetting") || linea.Contains("Leaving")) { fase = "reset"; pct = 100; }

            var m = RxPct.Match(linea);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int p)) pct = p;
        }

        public static string ClasificarError(string log)
        {
            if (string.IsNullOrEmpty(log)) return null;
            if (log.Contains("Access is denied") || log.Contains("Could not open") ||
                log.Contains("the port doesn't exist") || log.Contains("PermissionError"))
                return "AGP-USB-001";
            if (log.Contains("Failed to connect") || log.Contains("No serial data received") ||
                log.Contains("Timed out waiting for packet"))
                return "AGP-USB-002";
            return null;
        }
    }
}
```

- [ ] **Step 4: Correr y verificar que pasan**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release --filter Esptool`
Expected: todos PASS.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/EsptoolOutputParser.cs SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs
git commit -m "feat(usb): parser de progreso de esptool + clasificador de errores"
```

---

## Task 5: UsbFlashService — listar puertos y armar argumentos

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbFlashService.cs`
- Test: `SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs`

**Interfaces:**
- Consumes: `EsptoolOutputParser`, `FirmwareMirror.PathBin/PathFactory`, `PuertoComDto`, `UsbFlashEstadoDto`.
- Produces:
  - `UsbFlashService(string enginBaseDir)` — baseDir donde está `tools/esptool/esptool.exe`.
  - `IReadOnlyList<PuertoComDto> ListarPuertos()`.
  - `static IReadOnlyList<string> ArmarArgs(string puerto, string modo, string binPath, bool borrarAntes)` — args para `ArgumentList` (testeable, sin IO).
  - `UsbFlashEstadoDto Estado()` (snapshot).
  - `bool Iniciar(string binPath, string modo, string puerto, bool borrarAntes, out string codigoError)`.

- [ ] **Step 1: Tests del arg builder**

```csharp
[Fact]
public void ArmarArgs_completo_escribe_factory_a_0x0()
{
    var a = AgroParallel.Usb.UsbFlashService.ArmarArgs("COM5", "completo", @"C:\f\factory.bin", false);
    Assert.Contains("--port", a); Assert.Contains("COM5", a);
    Assert.Contains("write_flash", a);
    int i = a.IndexOf("write_flash");
    Assert.Equal("0x0", a[i + 1]);
    Assert.Equal(@"C:\f\factory.bin", a[i + 2]);
}

[Fact]
public void ArmarArgs_app_escribe_firmware_a_0x10000()
{
    var a = AgroParallel.Usb.UsbFlashService.ArmarArgs("COM3", "app", @"C:\f\firmware.bin", false);
    int i = a.IndexOf("write_flash");
    Assert.Equal("0x10000", a[i + 1]);
    Assert.Equal(@"C:\f\firmware.bin", a[i + 2]);
}

[Fact]
public void ArmarArgs_borrar_antes_agrega_erase_flag()
{
    var a = AgroParallel.Usb.UsbFlashService.ArmarArgs("COM3", "app", @"C:\f\firmware.bin", true);
    Assert.Contains("--erase-all", a);   // write_flash -e / --erase-all
}
```

- [ ] **Step 2: Correr y verificar que fallan**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release --filter ArmarArgs`
Expected: no compila.

- [ ] **Step 3: Implementar UsbFlashService (listar puertos + ArmarArgs + esqueleto de estado)**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using AgroParallel.Models;

namespace AgroParallel.Usb
{
    // Envuelve esptool.exe (bundleado en <engineBase>/tools/esptool/) para
    // flashear un ESP32 por USB. Un flasheo a la vez (lock). El progreso se
    // parsea con EsptoolOutputParser y se expone por Estado().
    public sealed class UsbFlashService
    {
        private readonly string _esptool;
        private readonly object _lock = new object();
        private UsbFlashEstadoDto _estado = new UsbFlashEstadoDto { Fase = "idle", Resultado = null };

        public UsbFlashService(string engineBaseDir)
        {
            _esptool = Path.Combine(engineBaseDir, "tools", "esptool", "esptool.exe");
        }

        public bool EsptoolPresente => File.Exists(_esptool);

        public IReadOnlyList<PuertoComDto> ListarPuertos()
        {
            var lista = new List<PuertoComDto>();
            foreach (var p in SerialPort.GetPortNames())
                lista.Add(new PuertoComDto { Port = p, Descripcion = DescribirPuerto(p) });
            return lista;
        }

        // Descripción best-effort desde el registro (no rompe si no está).
        private static string DescribirPuerto(string port)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    // No siempre hay nombre lindo; devolvemos el port si no.
                }
            }
            catch { }
            return port;
        }

        // Args para ProcessStartInfo.ArgumentList (NUNCA shell-concat).
        public static IReadOnlyList<string> ArmarArgs(string puerto, string modo, string binPath, bool borrarAntes)
        {
            var a = new List<string> { "--chip", "auto", "--port", puerto, "--baud", "921600", "write_flash" };
            if (borrarAntes) a.Add("--erase-all");
            if (modo == "completo") { a.Add("0x0"); a.Add(binPath); }
            else                    { a.Add("0x10000"); a.Add(binPath); }
            return a;
        }

        public UsbFlashEstadoDto Estado()
        {
            lock (_lock)
                return new UsbFlashEstadoDto
                {
                    EnCurso = _estado.EnCurso, Fase = _estado.Fase, Pct = _estado.Pct,
                    Resultado = _estado.Resultado, Codigo = _estado.Codigo, Log = _estado.Log
                };
        }

        // Iniciar() se implementa en Task 6 (corre el proceso). Placeholder de firma:
        public bool Iniciar(string binPath, string modo, string puerto, bool borrarAntes, out string codigoError)
        {
            codigoError = null;
            return false; // reemplazado en Task 6
        }
    }
}
```

- [ ] **Step 4: Agregar referencia a System.IO.Ports si falta**

Run: `grep -n "System.IO.Ports" SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj`
Si no aparece, agregar `<PackageReference Include="System.IO.Ports" Version="9.0.0" />` (misma versión que usa el resto de la solución — verificar con `grep -rn "System.IO.Ports" SourceCode/**/*.csproj`).

- [ ] **Step 5: Correr y verificar que pasan**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release --filter ArmarArgs`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbFlashService.cs SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj SourceCode/AgroParallel.Services.Tests/UsbFlashTests.cs
git commit -m "feat(usb): UsbFlashService listar puertos + armado de args esptool"
```

---

## Task 6: UsbFlashService — correr esptool y trackear progreso

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbFlashService.cs`

**Interfaces:**
- Produces: `Iniciar(binPath, modo, puerto, borrarAntes, out codigoError)` real: lanza esptool como proceso hijo, lee stdout línea a línea, actualiza `_estado` con `EsptoolOutputParser`, marca `Resultado` "ok"/"fail" al terminar. Devuelve false + código si ya hay uno en curso (`AGP-USB-007`) o falta esptool (`AGP-USB-005`).

- [ ] **Step 1: Implementar Iniciar() reemplazando el placeholder**

```csharp
public bool Iniciar(string binPath, string modo, string puerto, bool borrarAntes, out string codigoError)
{
    codigoError = null;
    if (!EsptoolPresente) { codigoError = "AGP-USB-005"; return false; }
    if (!File.Exists(binPath)) { codigoError = "AGP-USB-004"; return false; }

    lock (_lock)
    {
        if (_estado.EnCurso) { codigoError = "AGP-USB-007"; return false; }
        _estado = new UsbFlashEstadoDto { EnCurso = true, Fase = "conectando", Pct = 0, Resultado = null, Log = "" };
    }

    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = _esptool,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var arg in ArmarArgs(puerto, modo, binPath, borrarAntes)) psi.ArgumentList.Add(arg);

    var sbLog = new System.Text.StringBuilder();
    void OnLinea(string linea)
    {
        if (linea == null) return;
        lock (_lock)
        {
            sbLog.AppendLine(linea);
            string fase = _estado.Fase; int pct = _estado.Pct;
            EsptoolOutputParser.Parse(linea, ref fase, ref pct);
            _estado.Fase = fase; _estado.Pct = pct; _estado.Log = sbLog.ToString();
        }
    }

    System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            using (var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                proc.OutputDataReceived += (_, e) => OnLinea(e.Data);
                proc.ErrorDataReceived  += (_, e) => OnLinea(e.Data);
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                lock (_lock)
                {
                    string log = sbLog.ToString();
                    string cod = EsptoolOutputParser.ClasificarError(log);
                    bool ok = proc.ExitCode == 0 && cod == null;
                    _estado.EnCurso = false;
                    _estado.Resultado = ok ? "ok" : "fail";
                    _estado.Codigo = ok ? null : (cod ?? "AGP-USB-003");
                    _estado.Pct = ok ? 100 : _estado.Pct;
                    _estado.Fase = ok ? "listo" : "error";
                }
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _estado.EnCurso = false; _estado.Resultado = "fail";
                _estado.Codigo = "AGP-USB-003"; _estado.Fase = "error";
                _estado.Log = sbLog.ToString() + "\n" + ex.Message;
            }
        }
    });
    return true;
}
```

- [ ] **Step 2: Compilar**

Run: `dotnet build SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj -c Release`
Expected: 0 errores.

- [ ] **Step 3: Correr toda la suite de tests (regresión)**

Run: `dotnet test SourceCode/AgroParallel.Services.Tests -c Release`
Expected: todo PASS (los tests existentes + los nuevos del parser/args).

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbFlashService.cs
git commit -m "feat(usb): UsbFlashService corre esptool y trackea progreso/resultado"
```

---

## Task 7: UsbDriverInstaller (pnputil elevado)

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbDriverInstaller.cs`

**Interfaces:**
- Produces: `UsbDriverInstaller.Instalar(string engineBaseDir, string driver, out string codigoError) : bool`. `driver` ∈ {cp210x, ch340, ambos}. Lanza pnputil elevado por cada `.inf` bajo `tools/usb-drivers/<driver>/`. Si falta el dir del driver → `AGP-USB-004`. Si UAC rechazado / pnputil falla → `AGP-USB-006`.

- [ ] **Step 1: Implementar el instalador**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AgroParallel.Usb
{
    // Instala los drivers USB-serial bundleados con pnputil (elevado). La acción
    // la dispara SIEMPRE el operario con un botón + consentimiento UAC; nunca es
    // automática. Idempotente (reinstalar no rompe).
    public static class UsbDriverInstaller
    {
        public static bool Instalar(string engineBaseDir, string driver, out string codigoError)
        {
            codigoError = null;
            var dirs = new List<string>();
            string baseDrv = Path.Combine(engineBaseDir, "tools", "usb-drivers");
            if (driver == "ambos") { dirs.Add(Path.Combine(baseDrv, "cp210x")); dirs.Add(Path.Combine(baseDrv, "ch340")); }
            else dirs.Add(Path.Combine(baseDrv, driver));

            bool algo = false;
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var inf in Directory.GetFiles(d, "*.inf"))
                {
                    algo = true;
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        UseShellExecute = true,   // requerido para Verb=runas (UAC)
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    psi.ArgumentList.Add("/add-driver");
                    psi.ArgumentList.Add(inf);
                    psi.ArgumentList.Add("/install");
                    psi.ArgumentList.Add("/subdirs");
                    try
                    {
                        using (var p = Process.Start(psi)) { p.WaitForExit(); if (p.ExitCode != 0 && p.ExitCode != 259) { codigoError = "AGP-USB-006"; return false; } }
                    }
                    catch (Exception) { codigoError = "AGP-USB-006"; return false; }  // UAC rechazado o pnputil ausente
                }
            }
            if (!algo) { codigoError = "AGP-USB-004"; return false; }
            return true;
        }
    }
}
```

- [ ] **Step 2: Compilar**

Run: `dotnet build SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj -c Release`
Expected: 0 errores.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Usb/UsbDriverInstaller.cs
git commit -m "feat(usb): instalador de drivers USB-serial via pnputil elevado"
```

---

## Task 8: Códigos AGP-USB en AgpErrorMapper

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/Common/AgpErrorMapper.cs`

**Interfaces:**
- Produces: mensajes amigables en castellano para `AGP-USB-001..007`, consultables por el controller/UI. Verificar primero cómo se declaran los códigos existentes (`grep -n "AGP-MQTT-001\|AGP-NET" AgpErrorMapper.cs`) y seguir ese patrón exacto (diccionario o switch).

- [ ] **Step 1: Ver el patrón existente de códigos**

Run: `grep -n "AGP-" SourceCode/AgroParallel/Core/AgroParallel.Services/Common/AgpErrorMapper.cs | head`
Identificar si es un `Dictionary<string,string>` o un switch de código→mensaje.

- [ ] **Step 2: Agregar los códigos USB siguiendo ese patrón**

Mensajes (adaptar a la estructura real del archivo):
```
AGP-USB-001 → "El puerto {port} está en uso o no se puede abrir. ¿Otra app lo tiene abierto?"
AGP-USB-002 → "El módulo no respondió. Mantené BOOT apretado y reintentá, o revisá el cable."
AGP-USB-003 → "Falló la escritura del firmware. Reintentá; si sigue, cambiá el cable/puerto."
AGP-USB-004 → "No encontré el firmware a flashear en el cache."
AGP-USB-005 → "Falta esptool en la instalación (build incompleto)."
AGP-USB-006 → "No se pudo instalar el driver USB (¿se rechazó el permiso de administrador?)."
AGP-USB-007 → "Ya hay un flasheo en curso. Esperá a que termine."
```

- [ ] **Step 3: Compilar**

Run: `dotnet build SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj -c Release`
Expected: 0 errores.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/Common/AgpErrorMapper.cs
git commit -m "feat(usb): mensajes amigables AGP-USB-001..007"
```

---

## Task 9: UsbFlashController + registro

**Files:**
- Create: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/UsbFlashController.cs`
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs:307` (agregar `.WithController(...)`)

**Interfaces:**
- Consumes: `UsbFlashService` (singleton, inyectado), `UsbDriverInstaller`, `FirmwareMirror.PathBin/PathFactory`, `OrbitXConfig.Load`, DTOs de Task 3. Regex de `FirmwaresController`.
- Produces: rutas `GET /api/usb/puertos`, `POST /api/usb/flash`, `GET /api/usb/flash/estado`, `POST /api/usb/driver/instalar`.

- [ ] **Step 1: Crear el controller**

```csharp
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Usb;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class UsbFlashController : AgpControllerBase
    {
        private static readonly Regex RxProducto = new Regex("^[a-zA-Z][a-zA-Z0-9-]{1,31}$");
        private static readonly Regex RxVersion  = new Regex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,31}$");

        private readonly UsbFlashService _usb;
        private readonly string _engineBaseDir;
        public UsbFlashController(UsbFlashService usb, string engineBaseDir)
        { _usb = usb; _engineBaseDir = engineBaseDir; }

        [Route(HttpVerbs.Get, "/usb/puertos")]
        public Task Puertos() => WriteJsonAsync(new { ok = true, puertos = _usb.ListarPuertos() });

        [Route(HttpVerbs.Get, "/usb/flash/estado")]
        public Task Estado() => WriteJsonAsync(_usb.Estado());

        [Route(HttpVerbs.Post, "/usb/flash")]
        public async Task Flash()
        {
            var req = await ReadJsonBodyAsync<UsbFlashRequest>().ConfigureAwait(false);
            if (req == null || !RxProducto.IsMatch(req.Producto ?? "") || !RxVersion.IsMatch(req.Version ?? ""))
            { await WriteErrorAsync(400, "AGP-USB-004", "Producto o versión inválidos.").ConfigureAwait(false); return; }
            string modo = req.Modo == "app" ? "app" : "completo";

            var cfg = SafeLoadOrbitX();
            string cacheDir = FirmwareMirror.ResolveCacheDir(cfg);
            string prodLo = req.Producto.ToLowerInvariant();
            string bin = modo == "completo"
                ? FirmwareMirror.PathFactory(cacheDir, prodLo, req.Version)
                : FirmwareMirror.PathBin(cacheDir, prodLo, req.Version);

            if (!File.Exists(bin))
            { await WriteErrorAsync(404, "AGP-USB-004", modo == "completo"
                ? "Esta versión no tiene factory.bin para flasheo completo."
                : "No encontré el firmware en el cache.").ConfigureAwait(false); return; }

            if (!_usb.Iniciar(bin, modo, req.Puerto, req.BorrarAntes, out string cod))
            { await WriteErrorAsync(409, cod ?? "AGP-USB-003", "No se pudo iniciar el flasheo.").ConfigureAwait(false); return; }
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        [Route(HttpVerbs.Post, "/usb/driver/instalar")]
        public async Task InstalarDriver()
        {
            var req = await ReadJsonBodyAsync<UsbDriverRequest>().ConfigureAwait(false);
            string drv = (req?.Driver == "cp210x" || req?.Driver == "ch340") ? req.Driver : "ambos";
            if (!UsbDriverInstaller.Instalar(_engineBaseDir, drv, out string cod))
            { await WriteErrorAsync(500, cod ?? "AGP-USB-006", "No se pudo instalar el driver.").ConfigureAwait(false); return; }
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }

        private OrbitXConfig SafeLoadOrbitX() { try { return OrbitXConfig.Load(); } catch { return new OrbitXConfig(); } }
    }
}
```

- [ ] **Step 2: Registrar el controller y crear el singleton en AgpWebHost.cs**

Ver cómo AgpWebHost recibe/crea servicios (constructor). Crear un `UsbFlashService` con el baseDir del Engine (`AppContext.BaseDirectory` o el que use el host) como campo, e insertarlo en la cadena `.WithController(...)` junto a FirmwaresController (línea ~307):
```csharp
.WithController(() => new UsbFlashController(_usbFlash, AppContext.BaseDirectory))
```
Y donde se inicializan los campos del host: `private readonly UsbFlashService _usbFlash = new UsbFlashService(AppContext.BaseDirectory);`

- [ ] **Step 3: Compilar el WebHost + Engine**

Run: `dotnet build SourceCode/PilotX.GuidanceEngine/PilotX.GuidanceEngine.csproj -c Release`
Expected: 0 errores.

- [ ] **Step 4: Smoke test manual del endpoint de puertos**

Correr el Engine (`Build/Engine/PilotX.GuidanceEngine.exe --webhost --corex`) y:
Run: `curl http://127.0.0.1:5180/api/usb/puertos`
Expected: `{"ok":true,"puertos":[...]}` (lista, vacía si no hay nada conectado).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/UsbFlashController.cs SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs
git commit -m "feat(usb): UsbFlashController /api/usb/* + registro en el WebHost"
```

---

## Task 10: UsbFlashClient (cliente HTTP de la UI)

**Files:**
- Create: `SourceCode/PilotX.UI/Services/UsbFlashClient.cs`

**Interfaces:**
- Consumes: patrón de `SourceCode/PilotX.UI/Services/FirmwaresClient.cs` (mismo `HttpClient`, base URL, `HttpClient.Timeout` con el cuidado de `HttpClientTimeout` — ver memoria: `TaskCanceledException` es `OperationCanceledException`).
- Produces: `Task<IReadOnlyList<PuertoComDto>> PuertosAsync(CancellationToken)`, `Task<bool> FlashAsync(UsbFlashRequest, CancellationToken)`, `Task<UsbFlashEstadoDto> EstadoAsync(CancellationToken)`, `Task<bool> InstalarDriverAsync(string driver, CancellationToken)`.

- [ ] **Step 1: Leer FirmwaresClient.cs para copiar el patrón (base URL, deserialización snake_case, manejo de timeout)**

Run: `sed -n '1,80p' SourceCode/PilotX.UI/Services/FirmwaresClient.cs`

- [ ] **Step 2: Implementar UsbFlashClient siguiendo ese patrón**

Métodos GET (`/api/usb/puertos`, `/api/usb/flash/estado`) y POST (`/api/usb/flash`, `/api/usb/driver/instalar`), deserializando con el mismo serializer que FirmwaresClient. Respetar el patrón de timeout: envolver el polling de estado con un `CancellationToken` y `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` para no congelar la UI (ver memoria `HttpClient.Timeout`).

- [ ] **Step 3: Compilar PilotX.UI**

Run: `dotnet build SourceCode/PilotX.UI/PilotX.UI.csproj -c Release`
Expected: 0 errores.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/PilotX.UI/Services/UsbFlashClient.cs
git commit -m "feat(usb): UsbFlashClient (cliente HTTP del panel)"
```

---

## Task 11: Sección "Flashear por USB" en FirmwaresPanel

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/FirmwaresPanel.axaml.cs`

**Interfaces:**
- Consumes: `UsbFlashClient` (Task 10), `PuertoComDto`, `UsbFlashEstadoDto`.
- Produces: UI dentro del FirmwaresPanel con selector producto/versión (reusa el catálogo ya cargado), selector de puerto COM + refrescar, radio Completo/Solo app (Completo deshabilitado si no hay factory), check "Borrar antes", botón Flashear con barra de progreso + fase + log en un expander, botón "Borrar chip", y botón "Instalar driver USB" visible si no hay puertos.

- [ ] **Step 1: Leer la estructura actual del FirmwaresPanel para ubicar dónde insertar la sección y seguir su estilo**

Run: `sed -n '1,120p' SourceCode/PilotX.UI/Views/FirmwaresPanel.axaml.cs`
Seguir el design system (memoria `pilotx_design_system`: paleta clara, táctil) y el guardián de estilo.

- [ ] **Step 2: Agregar los controles de la sección USB y el cableado al UsbFlashClient**

- Selector puerto: `ComboBox` poblado con `PuertosAsync`; botón "↻".
- Si `PuertosAsync` devuelve vacío: mostrar botón "Instalar driver USB" → `InstalarDriverAsync("ambos")` y luego refrescar.
- Radio modo: Completo (default) / Solo app. Completo `IsEnabled` = la versión elegida tiene factory (agregar `has_factory` al listado de FirmwaresController.List() en un sub-paso, o inferir con un HEAD; simplest: extender List() con `has_factory = File.Exists(PathFactory(...))`).
- Botón Flashear → `FlashAsync`, y arrancar un poller (`DispatcherTimer` 500 ms) que llama `EstadoAsync` y actualiza barra/fase/log; parar el poller cuando `resultado != null`.
- Mostrar el `codigo` + mensaje amigable en error; log crudo en un `Expander`.

- [ ] **Step 3: Sub-paso backend — agregar has_factory al listado**

En `FirmwaresController.List()` (dentro del `Select` de versiones) agregar:
```csharp
has_factory = File.Exists(FirmwareMirror.PathFactory(cacheDir, g.Key, f.version)),
```
(y `using System.IO;` ya está). Recompilar WebHost.

- [ ] **Step 4: Compilar UI + Engine y lanzar PilotX.Desktop para ver la sección**

Run: `dotnet build SourceCode/PilotX.Desktop/PilotX.Desktop.csproj -c Release`
Expected: 0 errores. Lanzar y confirmar visualmente que la sección aparece y lista puertos.

- [ ] **Step 5: Guardián de estilo**

Correr el agente `guardian-estilo` sobre los cambios de UI; corregir lo que marque.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.UI/Views/FirmwaresPanel.axaml.cs SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/FirmwaresController.cs
git commit -m "feat(usb): seccion Flashear por USB en el FirmwaresPanel"
```

---

## Task 12: Manual (ayuda.html)

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

**Interfaces:** ninguna (doc).

- [ ] **Step 1: Ubicar la sección de firmwares/OTA en ayuda.html**

Run: `grep -n -i "firmware\|flashe\|ota" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html | head`

- [ ] **Step 2: Agregar el flujo "Flashear por USB" con la ruta REAL del sector**

Verificar la ruta real en la UI (dónde quedó la sección dentro del FirmwaresPanel) y documentarla, incluyendo: conectar el módulo por USB; si no aparece el puerto, tocar "Instalar driver USB" y reconectar; elegir producto/versión; modo Completo (chip nuevo) o Solo app; Flashear. Redactar en castellano rioplatense, mismo tono que el resto del manual.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html
git commit -m "docs(ayuda): flasheo de modulos por USB"
```

---

## Task 13: Generar el factory.bin de FlowX (insumo para mañana)

**Files:**
- Genera artefacto (no código): `factory.bin` de FlowX, subido al cache local / OrbitX.

**Interfaces:** ninguna (operación one-shot).

- [ ] **Step 1: Compilar el firmware FlowX con PlatformIO**

Run:
```bash
cd "G:/AgroParallel/Productos/FlowX/Software/Firmware_Embebido/FlowXNode"
python -m platformio run
```
Expected: genera `.pio/build/esp32doit-devkit-v1/{firmware.bin,bootloader.bin,partitions.bin}`.

- [ ] **Step 2: Ubicar boot_app0.bin del framework**

Run: `ls "$USERPROFILE/.platformio/packages/framework-arduinoespressif32/tools/partitions/boot_app0.bin"`

- [ ] **Step 3: Mergear a factory.bin con el esptool bundleado**

Run (ajustar rutas):
```bash
"G:/AgroParallel/Productos/CentriX-Spark/Software/App_PC/AgOpenGPS/Tools/esptool/esptool.exe" \
  --chip esp32 merge_bin -o factory.bin --flash_mode dio --flash_freq 40m --flash_size 4MB \
  0x1000 .pio/build/esp32doit-devkit-v1/bootloader.bin \
  0x8000 .pio/build/esp32doit-devkit-v1/partitions.bin \
  0xe000 "$USERPROFILE/.platformio/packages/framework-arduinoespressif32/tools/partitions/boot_app0.bin" \
  0x10000 .pio/build/esp32doit-devkit-v1/firmware.bin
```
Expected: crea `factory.bin` (~1-2 MB).

- [ ] **Step 4: Colocar factory.bin + firmware.bin en el cache local de la pantalla**

El cache vive en `FirmwareMirror.ResolveCacheDir(...)` → `<...>/flowx/<version>/`. Copiar ahí `factory.bin` y `firmware.bin` (app), o subir el app por `/api/firmwares/upload` (que crea firmware.bin + manifest) y dejar `factory.bin` al lado. Verificar que `/api/firmwares` liste FlowX con `has_factory=true`.

- [ ] **Step 5: (opcional) Subir factory a OrbitX** para que otras pantallas lo bajen — fuera del alcance de código; se decide con el equipo.

---

## Self-Review

**Spec coverage:** esptool bundleado (T1) ✓; drivers bundleados + instalar (T1, T7, T9) ✓; listar puertos (T5, T9) ✓; flash completo/app (T5, T6, T9) ✓; progreso polleable (T4, T6, T9, T11) ✓; origen cache/OrbitX (T9, T13) ✓; factory.bin (T2, T13) ✓; UI en FirmwaresPanel (T11) ✓; errores AGP-USB (T8) ✓; seguridad ArgumentList/regex (T5, T7, T9) ✓; manual (T12) ✓; tests parser/args (T4, T5) ✓.

**Nota de código:** `AGP-USB-007` (flasheo en curso) se usa en T6/T9 y se define en T8 — consistente. `PathFactory` definido en T2, usado en T9/T11/T13. `UsbFlashEstadoDto`/`UsbFlashRequest`/`PuertoComDto`/`UsbDriverRequest` definidos en T3, usados en T5/T6/T9/T10. Firmas de `UsbFlashService` (`ListarPuertos`, `ArmarArgs`, `Estado`, `Iniciar`) consistentes T5→T6→T9.

**Pendiente conocido:** CH340 (dir vacío) — el instalador lo saltea (no hay `.inf`); no rompe. El `factory.bin` de FlowX depende de poder compilar el firmware (T13).
