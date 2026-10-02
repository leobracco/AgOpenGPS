# Instalador online — Parte B: PilotX-Instalador.exe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un `PilotX-Instalador.exe` único (C# .NET 4.8 WinForms, admin) que respalda la instalación anterior, se vincula a OrbitX con un código que aprueba un superadmin, baja perfil + PilotX + RustDesk de la nube, provisiona el equipo, repara la red Ethernet USB + WiFi, se autoverifica y recién ahí activa el kiosko.

**Architecture:** Toda la lógica testeable vive en `PilotX.Instalador.Core` (netstandard2.0, sin dependencias de Windows): device_id, código de vinculación, cliente OrbitX, estado reanudable, motor de pasos, diagnóstico de red, self-test y conteo de lotes. El exe `PilotX.Instalador` (net48 WinForms) **compila esos mismos .cs como links** (sale un único .exe, sin DLL), agrega las implementaciones Windows (PowerShell, DPAPI, sondas de red) y la UI. Los scripts que ya funcionan (`Provision-Pantalla.ps1`, `Rescatar-AOG.ps1`, `Restaurar-AOG.ps1`) van embebidos como recursos tomados de `Tools/` sin copiarlos. Spec: `docs/superpowers/specs/2026-10-02-instalador-online-design.md`. Requiere la Parte A (OrbitX) desplegada para la prueba integrada (Task 13).

**Tech Stack:** C# (.NET Framework 4.8 WinForms + netstandard2.0), `System.Net.Http`, `DataContractJsonSerializer`, `System.Security.Cryptography.ProtectedData` (DPAPI, LocalMachine), PowerShell 5.1; tests NUnit 4 sobre net8.0.

## Desvíos del spec (decididos al planificar)

- **Red persistente:** en lugar de sumar un modo `reparar` a `NetApplyWatcher.ps1` (vive en el repo de ViewX y duplicaría la lógica en PowerShell), el mismo exe corre `--red --auto` desde la tarea SYSTEM `PilotXRed` (al arrancar + evento de cambio de red). Una sola lógica de red, testeada. `NetApplyWatcher` queda como está (IP fija desde PilotX).
- **Orden:** PilotX se baja/instala **antes** de "Equipo", porque `Provision-Pantalla.ps1` necesita el branding que viene en el paquete.
- **Alcance a nodos** (barrido de la subred del cable) queda fuera: el broker se verifica en el self-test (`:1883`), y el diagnóstico decide solo con salida a internet + gateways + métricas.
- **RustDesk** se baja del OTA (producto `RustDesk`) con servidor/clave del perfil, no del `Desktop\RustDesk` del paquete (ese .exe apunta a un servidor viejo, `165.227.90.158`).

## Global Constraints

- Repo: `G:\AgroParallel\Productos\CentriX-Spark\Software\App_PC\AgOpenGPS`. Rutas relativas a esa raíz.
- Hay otras sesiones trabajando en este repo: **commitear solo los archivos de cada task** (`git add <archivos>`; nunca `git add -A`).
- Texto visible en castellano rioplatense. **Nunca** nombrar el producto anterior en la UI ni en mensajes al operario: decir "instalación anterior". Visible: PilotX / Agro Parallel.
- UI: fondo `#F5F7F4`, superficies blancas, acento `#4BA63F`, texto `#2B2B2B`, estados verde/amarillo/rojo/gris; botones táctiles ≥ 56 px de alto; colores en un único `Tema`.
- device_id: **mismo cálculo que PilotX** (`OrbitXConfig.cs:182-206`): MD5 UTF-8 del `PhysicalAddress.ToString()` de la primera interfaz Up no-loopback con MAC ≥ 12 chars → `"OX-" + 12 hex mayúsculas`. Referencia: MAC `001122334455` → `OX-572E74CDF761`.
- Si el respaldo de la instalación anterior falla, **no se instala nada**. El kiosko se activa **solo** si el self-test pasa.
- Servidor por defecto `https://orbitx.agroparallel.com`; instalación en `C:\PilotX`; estado en `C:\PilotX\instalador-estado.json`; log en `C:\PilotX\instalador-log.txt`; rescate en `C:\Rescate-PilotX`.
- Todo nombre/texto hacia OrbitX viaja como JSON UTF-8 armado en C# (nunca por línea de comandos).
- Tests: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release`.

## File Structure

```
SourceCode/AgroParallel/Tools/
  PilotX.Instalador.Core/            netstandard2.0 — lógica pura
    PilotX.Instalador.Core.csproj
    Json.cs                          serializar/leer con DataContractJsonSerializer
    Hash.cs                          sha256 de archivo/stream
    IdEquipo.cs                      device_id (MD5 del MAC)
    CodigoVinculacion.cs             código 6 chars + secreto 32 bytes
    Modelos.cs                       DTOs de OrbitX (PairInit, PairStatus, Perfil, …)
    OrbitXCliente.cs                 HTTP contra OrbitX (pairing, perfil, progreso, red, OTA, heartbeat)
    Estado.cs                        estado reanudable con campos protegidos (IProtector)
    Motor.cs                         IPaso, ResultadoPaso, IReportero, Contexto, Motor
    Red/Adaptador.cs                 medición de un adaptador
    Red/AccionRed.cs                 acciones de reparación → comando PowerShell
    Red/Diagnosticador.cs            mediciones → diagnóstico + plan
    Rescate.cs                       contar lotes en un ZIP de rescate
    SelfTest.cs                      Chequeo + agregador
  PilotX.Instalador/                 net48 WinForms exe (linkea Core)
    PilotX.Instalador.csproj
    app.manifest
    Program.cs                       modos: UI instalador, UI red (--red), headless (--red --auto)
    Tema.cs
    MainForm.cs
    Windows/PowerShell.cs            correr scripts/comandos capturando salida
    Windows/ProtectorDpapi.cs
    Windows/Kit.cs                   extraer recursos embebidos a C:\PilotX\kit
    Windows/RedWindows.cs            medir adaptadores + aplicar acciones
    Windows/SondasWindows.cs         self-test real
    Windows/Equipo.cs                MACs, hostname, versión de Windows, resumen
    Pasos/*.cs                       un archivo por paso
    Kit/RustDesk-Configurar.ps1      nuevo (embebido)
  PilotX.Instalador.Tests/           net8.0 NUnit → referencia Core
```

---

### Task 1: Esqueleto de los tres proyectos + `Json` y `Hash`

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/PilotX.Instalador.Core.csproj`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Json.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Hash.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/PilotX.Instalador.Tests.csproj`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/JsonHashTests.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/app.manifest`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Program.cs` (mínimo, se completa en Task 11)
- Modify: `SourceCode/AgOpenGPS.sln` (agregar los tres proyectos)

**Interfaces:**
- Produces: `Json.Serializar<T>(T): string`, `Json.Leer<T>(string): T`, `Hash.Sha256Archivo(string ruta): string` (hex minúsculas), `Hash.Sha256(byte[]): string`.

- [ ] **Step 1: Proyecto Core**

`PilotX.Instalador.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Lógica pura del instalador (sin Windows). La compila el exe net48 como
       links (un solo .exe, sin DLL) y la testea PilotX.Instalador.Tests. -->
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <RootNamespace>PilotX.Instalador.Core</RootNamespace>
    <LangVersion>7.3</LangVersion>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
```

`Json.cs`:

```csharp
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PilotX.Instalador.Core
{
    // DataContractJsonSerializer: viene en net48 y netstandard2.0, sin DLL extra.
    // Escribe UTF-8 sin escapar acentos (la Ñ llega entera a OrbitX).
    public static class Json
    {
        public static string Serializar<T>(T obj)
        {
            var s = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                s.WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Leer<T>(string json)
        {
            var s = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                return (T)s.ReadObject(ms);
        }
    }
}
```

`Hash.cs`:

```csharp
using System;
using System.IO;
using System.Security.Cryptography;

namespace PilotX.Instalador.Core
{
    public static class Hash
    {
        public static string Sha256(byte[] datos)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(datos));
        }

        public static string Sha256Archivo(string ruta)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(ruta))
                return Hex(sha.ComputeHash(f));
        }

        static string Hex(byte[] h) => BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Proyecto de tests**

`PilotX.Instalador.Tests.csproj` (mismas versiones que `AgroParallel.Services.Tests`):

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
    <PackageReference Include="NUnit.Analyzers" Version="4.6.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PilotX.Instalador.Core\PilotX.Instalador.Core.csproj" />
  </ItemGroup>
</Project>
```

`JsonHashTests.cs`:

```csharp
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using NUnit.Framework;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Tests
{
    [DataContract]
    public class Muestra
    {
        [DataMember(Name = "nombre")] public string Nombre;
        [DataMember(Name = "n")] public int N;
    }

    public class JsonHashTests
    {
        [Test]
        public void Json_IdaYVuelta_ConservaLaEnie()
        {
            var json = Json.Serializar(new Muestra { Nombre = "LOS PEQUEÑOS TURPIALES S.A.", N = 3 });
            Assert.That(json, Does.Contain("PEQUEÑOS"), "la Ñ no se escapa ni se rompe");
            var m = Json.Leer<Muestra>(json);
            Assert.That(m.Nombre, Is.EqualTo("LOS PEQUEÑOS TURPIALES S.A."));
            Assert.That(m.N, Is.EqualTo(3));
        }

        [Test]
        public void Json_CamposDesconocidosSeIgnoran()
        {
            var m = Json.Leer<Muestra>("{\"nombre\":\"x\",\"otro\":1}");
            Assert.That(m.Nombre, Is.EqualTo("x"));
        }

        [Test]
        public void Sha256_DeArchivo_IgualQueDeBytes()
        {
            var tmp = Path.GetTempFileName();
            File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes("abc"));
            Assert.That(Hash.Sha256Archivo(tmp), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.That(Hash.Sha256(Encoding.UTF8.GetBytes("abc")), Is.EqualTo(Hash.Sha256Archivo(tmp)));
            File.Delete(tmp);
        }
    }
}
```

- [ ] **Step 3: Proyecto exe (mínimo)**

`PilotX.Instalador.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- PilotX-Instalador.exe: un solo .exe (net48, WinForms, admin). Compila los
       .cs de PilotX.Instalador.Core como links para no arrastrar una DLL. -->
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>PilotX.Instalador</RootNamespace>
    <AssemblyName>PilotX-Instalador</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <LangVersion>7.3</LangVersion>
    <Company>Agro Parallel</Company>
    <Product>PilotX</Product>
    <PlatformTarget>x64</PlatformTarget>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System.Net.Http" />
    <Reference Include="System.Runtime.Serialization" />
    <Reference Include="System.Security" />
    <Reference Include="System.IO.Compression" />
    <Reference Include="System.IO.Compression.FileSystem" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\PilotX.Instalador.Core\**\*.cs"
             Exclude="..\PilotX.Instalador.Core\obj\**;..\PilotX.Instalador.Core\bin\**"
             LinkBase="Core" />
  </ItemGroup>
  <!-- Scripts probados, tomados de Tools/ sin copiarlos. -->
  <ItemGroup>
    <EmbeddedResource Include="..\..\..\..\Tools\provision-pantalla\Provision-Pantalla.ps1" LogicalName="Kit.Provision-Pantalla.ps1" />
    <EmbeddedResource Include="..\..\..\..\Tools\migrar-desde-aog\Rescatar-AOG.ps1" LogicalName="Kit.Rescatar-AOG.ps1" />
    <EmbeddedResource Include="..\..\..\..\Tools\migrar-desde-aog\Restaurar-AOG.ps1" LogicalName="Kit.Restaurar-AOG.ps1" />
    <EmbeddedResource Include="Kit\RustDesk-Configurar.ps1" LogicalName="Kit.RustDesk-Configurar.ps1" />
  </ItemGroup>
</Project>
```

`app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="PilotX.Instalador"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10/11: sin esto Environment.OSVersion miente. -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    </windowsSettings>
  </application>
</assembly>
```

`Program.cs` (provisorio):

```csharp
using System;
namespace PilotX.Instalador
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args) { return 0; }
    }
}
```

Crear vacío `Kit/RustDesk-Configurar.ps1` (se completa en Task 9) para que el `EmbeddedResource` resuelva.

- [ ] **Step 4: Agregar a la solución**

```bash
dotnet sln SourceCode/AgOpenGPS.sln add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/PilotX.Instalador.Core.csproj SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/PilotX.Instalador.Tests.csproj
```

- [ ] **Step 5: Compilar y correr tests**

Run: `dotnet build SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj -c Release`
Expected: `Build succeeded`, existe `SourceCode/AgroParallel/Tools/PilotX.Instalador/bin/Release/net48/PilotX-Instalador.exe`.
Run: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release`
Expected: `Passed: 3`.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core SourceCode/AgroParallel/Tools/PilotX.Instalador SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests SourceCode/AgOpenGPS.sln
git commit -m "feat(instalador): esqueleto de PilotX-Instalador (Core netstandard, exe net48, tests)"
```

---

### Task 2: `IdEquipo` y `CodigoVinculacion`

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/IdEquipo.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/CodigoVinculacion.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/IdEquipoTests.cs`

**Interfaces:**
- Produces: `IdEquipo.Calcular(IEnumerable<string> macs): string` (null si no hay MAC válido); `CodigoVinculacion.Alfabeto`, `CodigoVinculacion.NuevoCodigo(RandomNumberGenerator): string`, `CodigoVinculacion.NuevoSecreto(RandomNumberGenerator): string` (64 hex minúsculas).

- [ ] **Step 1: Tests**

```csharp
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Tests
{
    public class IdEquipoTests
    {
        [Test]
        public void MismoIdQuePilotX_ParaUnMacConocido()
        {
            // OX-572E74CDF761 = "OX-" + md5("001122334455")[0..12] (calculado aparte).
            Assert.That(IdEquipo.Calcular(new[] { "001122334455" }), Is.EqualTo("OX-572E74CDF761"));
        }

        [Test]
        public void SaltaMacsVaciosOCortos()
        {
            Assert.That(IdEquipo.Calcular(new[] { "", "ABC", "001122334455" }), Is.EqualTo("OX-572E74CDF761"));
        }

        [Test]
        public void SinMacValido_DevuelveNull()
        {
            Assert.That(IdEquipo.Calcular(new[] { "", null }), Is.Null);
        }

        [Test]
        public void Codigo_Tiene6CharsDelAlfabeto()
        {
            using (var rng = RandomNumberGenerator.Create())
                for (int i = 0; i < 200; i++)
                {
                    var c = CodigoVinculacion.NuevoCodigo(rng);
                    Assert.That(c.Length, Is.EqualTo(6));
                    Assert.That(c.All(ch => CodigoVinculacion.Alfabeto.Contains(ch)), Is.True, c);
                }
        }

        [Test]
        public void Secreto_Son64HexMinusculas()
        {
            using (var rng = RandomNumberGenerator.Create())
                Assert.That(CodigoVinculacion.NuevoSecreto(rng), Does.Match("^[0-9a-f]{64}$"));
        }
    }
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release`
Expected: error de compilación `IdEquipo` / `CodigoVinculacion` no existe.

- [ ] **Step 3: Implementar**

`IdEquipo.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PilotX.Instalador.Core
{
    // MISMO cálculo que PilotX (AgroParallel.Services/OrbitX/OrbitXConfig.cs) y que
    // instalar.ps1: si cada uno lo derivara distinto, la misma pantalla aparecería
    // dos veces en OrbitX. El llamador pasa los MAC de las interfaces Up
    // no-loopback en el orden de NetworkInterface.GetAllNetworkInterfaces().
    public static class IdEquipo
    {
        public static string Calcular(IEnumerable<string> macs)
        {
            foreach (var mac in macs)
            {
                if (string.IsNullOrEmpty(mac) || mac.Length < 12) continue;
                using (var md5 = MD5.Create())
                {
                    var h = md5.ComputeHash(Encoding.UTF8.GetBytes(mac));
                    return "OX-" + BitConverter.ToString(h).Replace("-", "").Substring(0, 12).ToUpperInvariant();
                }
            }
            return null;
        }
    }
}
```

`CodigoVinculacion.cs`:

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace PilotX.Instalador.Core
{
    // Igual que OrbitXConfigService.cs y lib/pairing.js de OrbitX.
    public static class CodigoVinculacion
    {
        public const string Alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        public static string NuevoCodigo(RandomNumberGenerator rng)
        {
            var buf = new byte[6];
            rng.GetBytes(buf);
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++) sb.Append(Alfabeto[buf[i] % Alfabeto.Length]);
            return sb.ToString();
        }

        public static string NuevoSecreto(RandomNumberGenerator rng)
        {
            var buf = new byte[32];
            rng.GetBytes(buf);
            return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
        }
    }
}
```

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release`
Expected: `Passed: 8`.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/IdEquipo.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/CodigoVinculacion.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/IdEquipoTests.cs
git commit -m "feat(instalador): device_id igual a PilotX y código de vinculación"
```

---

### Task 3: Modelos y `OrbitXCliente`

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Modelos.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/OrbitXCliente.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/OrbitXClienteTests.cs`

**Interfaces:**
- Produces:
  - DTOs `[DataContract]`: `ResumenAdaptador {nombre,tipo,ip,internet}`, `ResumenEquipo {hostname,windows,instalacion_anterior,lotes,adaptadores}`, `PairInit {code,device_secret,device_id,hostname,version,origen,resumen}`, `PairStatus {status,device_id,token,estab_slug,nombre}`, `PerfilRustdesk {host,key,version,sha256}`, `Perfil {cliente,cuit,estab_slug,version,sha256,tamano_bytes,kiosko,rustdesk,soporte_pass,rustdesk_pass}`, `Progreso {paso,msg,estado}`, `Heartbeat {hostname,version,rustdesk_id}`, `HeartbeatResp {ok,asignado,estab_slug}`.
  - `OrbitXCliente(string baseUrl, HttpMessageHandler handler = null)`; propiedades `DeviceId`, `Token`; métodos async (todos con `CancellationToken ct`):
    `PairInitAsync(PairInit)`, `PairStatusAsync(string code, string secret): PairStatus` (404/410 → `status="expired"`),
    `PerfilAsync(): Perfil`, `ProgresoAsync(Progreso)`, `RedAsync(string jsonDiag)`, `HeartbeatAsync(Heartbeat): HeartbeatResp`,
    `DescargarAsync(string producto, string version, string destino, IProgress<long> progreso)`.
  - `OrbitXError : Exception { int Status }` — mensaje = campo `error` del JSON si existe.

- [ ] **Step 1: Tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Tests
{
    // HttpMessageHandler falso: responde según la ruta y guarda lo que recibió.
    class FakeHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Pedidos = new List<HttpRequestMessage>();
        public readonly List<string> Cuerpos = new List<string>();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Pedidos.Add(r);
            Cuerpos.Add(r.Content == null ? null : await r.Content.ReadAsStringAsync());
            return Responder(r);
        }

        public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public class OrbitXClienteTests
    {
        [Test]
        public async Task PairInit_MandaOrigenInstaladorYResumenEnUtf8()
        {
            var h = new FakeHandler { Responder = r => FakeHandler.Json(HttpStatusCode.OK, "{\"ok\":true}") };
            var c = new OrbitXCliente("https://x.test/", h);
            await c.PairInitAsync(new PairInit { code = "ABC234", device_secret = new string('a', 64), device_id = "OX-1", hostname = "TABLET", origen = "instalador",
                resumen = new ResumenEquipo { hostname = "TABLET", windows = "Windows 11", instalacion_anterior = true, lotes = 4, adaptadores = new List<ResumenAdaptador>() } }, CancellationToken.None);
            Assert.That(h.Pedidos[0].RequestUri.ToString(), Is.EqualTo("https://x.test/api/devices/pair/init"));
            Assert.That(h.Cuerpos[0], Does.Contain("\"origen\":\"instalador\""));
            Assert.That(h.Cuerpos[0], Does.Contain("\"lotes\":4"));
        }

        [Test]
        public async Task PairStatus_410_EsExpired()
        {
            var h = new FakeHandler { Responder = r => FakeHandler.Json((HttpStatusCode)410, "{\"status\":\"expired\"}") };
            var s = await new OrbitXCliente("https://x.test", h).PairStatusAsync("ABC234", "sec", CancellationToken.None);
            Assert.That(s.status, Is.EqualTo("expired"));
            Assert.That(h.Pedidos[0].RequestUri.Query, Is.EqualTo("?secret=sec"));
        }

        [Test]
        public async Task PairStatus_Claimed_TraeToken()
        {
            var h = new FakeHandler { Responder = r => FakeHandler.Json(HttpStatusCode.OK, "{\"status\":\"claimed\",\"device_id\":\"OX-1\",\"token\":\"tk\",\"estab_slug\":\"campo\"}") };
            var s = await new OrbitXCliente("https://x.test", h).PairStatusAsync("ABC234", "sec", CancellationToken.None);
            Assert.That(s.token, Is.EqualTo("tk"));
            Assert.That(s.estab_slug, Is.EqualTo("campo"));
        }

        [Test]
        public async Task Perfil_MandaHeadersDeEquipo()
        {
            var h = new FakeHandler { Responder = r => FakeHandler.Json(HttpStatusCode.OK,
                "{\"cliente\":\"LOS PEQUEÑOS TURPIALES S.A.\",\"version\":\"1.0.87\",\"sha256\":\"ab\",\"kiosko\":true,\"rustdesk\":{\"host\":\"h\",\"key\":\"k\",\"version\":\"1.4.1\",\"sha256\":\"cd\"},\"soporte_pass\":\"s\"}") };
            var c = new OrbitXCliente("https://x.test", h) { DeviceId = "OX-1", Token = "tk" };
            var p = await c.PerfilAsync(CancellationToken.None);
            Assert.That(h.Pedidos[0].Headers.GetValues("X-Device-ID"), Is.EqualTo(new[] { "OX-1" }));
            Assert.That(h.Pedidos[0].Headers.GetValues("X-Auth-Token"), Is.EqualTo(new[] { "tk" }));
            Assert.That(p.cliente, Is.EqualTo("LOS PEQUEÑOS TURPIALES S.A."));
            Assert.That(p.rustdesk.host, Is.EqualTo("h"));
            Assert.That(p.kiosko, Is.True);
        }

        [Test]
        public void Error_UsaElMensajeDelServidor()
        {
            var h = new FakeHandler { Responder = r => FakeHandler.Json((HttpStatusCode)503, "{\"error\":\"Falta INSTALADOR_SOPORTE_PASS\"}") };
            var c = new OrbitXCliente("https://x.test", h) { DeviceId = "OX-1", Token = "tk" };
            var e = Assert.ThrowsAsync<OrbitXError>(() => c.PerfilAsync(CancellationToken.None));
            Assert.That(e.Status, Is.EqualTo(503));
            Assert.That(e.Message, Is.EqualTo("Falta INSTALADOR_SOPORTE_PASS"));
        }

        [Test]
        public async Task Descargar_EscribeElArchivoCompletoConAuth()
        {
            var bytes = Encoding.UTF8.GetBytes("contenido-del-zip");
            var h = new FakeHandler { Responder = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } };
            var c = new OrbitXCliente("https://x.test", h) { DeviceId = "OX-1", Token = "tk" };
            var dst = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
            await c.DescargarAsync("PilotX", "1.0.87", dst, null, CancellationToken.None);
            Assert.That(h.Pedidos[0].RequestUri.AbsolutePath, Is.EqualTo("/api/ota/firmware/PilotX/1.0.87"));
            Assert.That(File.ReadAllBytes(dst), Is.EqualTo(bytes));
            Assert.That(File.Exists(dst + ".part"), Is.False);
            File.Delete(dst);
        }
    }
}
```

- [ ] **Step 2: Correr y ver que falla** — Run: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release` → error de compilación.

- [ ] **Step 3: Implementar `Modelos.cs`**

```csharp
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PilotX.Instalador.Core
{
    // Nombres en minúscula = los del JSON de OrbitX (routes/devices.js, lib/instalacion.js).
    [DataContract] public class ResumenAdaptador
    {
        [DataMember] public string nombre;
        [DataMember] public string tipo;
        [DataMember] public string ip;
        [DataMember] public bool internet;
    }

    [DataContract] public class ResumenEquipo
    {
        [DataMember] public string hostname;
        [DataMember] public string windows;
        [DataMember] public bool instalacion_anterior;
        [DataMember] public int lotes;
        [DataMember] public List<ResumenAdaptador> adaptadores;
    }

    [DataContract] public class PairInit
    {
        [DataMember] public string code;
        [DataMember] public string device_secret;
        [DataMember] public string device_id;
        [DataMember] public string hostname;
        [DataMember] public string version;
        [DataMember] public string origen;
        [DataMember] public ResumenEquipo resumen;
    }

    [DataContract] public class PairStatus
    {
        [DataMember] public string status;
        [DataMember] public string device_id;
        [DataMember] public string token;
        [DataMember] public string estab_slug;
        [DataMember] public string nombre;
    }

    [DataContract] public class PerfilRustdesk
    {
        [DataMember] public string host;
        [DataMember] public string key;
        [DataMember] public string version;
        [DataMember] public string sha256;
    }

    [DataContract] public class Perfil
    {
        [DataMember] public string cliente;
        [DataMember] public string cuit;
        [DataMember] public string estab_slug;
        [DataMember] public string version;
        [DataMember] public string sha256;
        [DataMember] public long tamano_bytes;
        [DataMember] public bool kiosko;
        [DataMember] public PerfilRustdesk rustdesk;
        [DataMember] public string soporte_pass;
        [DataMember] public string rustdesk_pass;
    }

    [DataContract] public class Progreso
    {
        [DataMember] public string paso;
        [DataMember] public string msg;
        [DataMember(EmitDefaultValue = false)] public string estado;
    }

    [DataContract] public class Heartbeat
    {
        [DataMember] public string hostname;
        [DataMember] public string version;
        [DataMember(EmitDefaultValue = false)] public string rustdesk_id;
    }

    [DataContract] public class HeartbeatResp
    {
        [DataMember] public bool ok;
        [DataMember] public bool asignado;
        [DataMember] public string estab_slug;
    }

    [DataContract] class CuerpoError
    {
        [DataMember] public string error;
    }
}
```

- [ ] **Step 4: Implementar `OrbitXCliente.cs`**

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Instalador.Core
{
    public class OrbitXError : Exception
    {
        public int Status { get; }
        public OrbitXError(int status, string mensaje) : base(mensaje) { Status = status; }
    }

    public class OrbitXCliente
    {
        readonly HttpClient _http;
        readonly string _base;
        public string DeviceId { get; set; }
        public string Token { get; set; }

        public OrbitXCliente(string baseUrl, HttpMessageHandler handler = null)
        {
            _base = baseUrl.TrimEnd('/');
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = Timeout.InfiniteTimeSpan; // cada llamada pone su propio límite con ct
        }

        public async Task PairInitAsync(PairInit cuerpo, CancellationToken ct)
        {
            await Enviar(HttpMethod.Post, "/api/devices/pair/init", Json.Serializar(cuerpo), false, ct).ConfigureAwait(false);
        }

        public async Task<PairStatus> PairStatusAsync(string code, string secret, CancellationToken ct)
        {
            var url = _base + "/api/devices/pair/status/" + Uri.EscapeDataString(code) + "?secret=" + Uri.EscapeDataString(secret);
            using (var r = await _http.GetAsync(url, ct).ConfigureAwait(false))
            {
                var txt = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                var st = (int)r.StatusCode;
                if (st == 404 || st == 410) return new PairStatus { status = "expired" };
                if (!r.IsSuccessStatusCode) throw Error(st, txt);
                return Json.Leer<PairStatus>(txt);
            }
        }

        public async Task<Perfil> PerfilAsync(CancellationToken ct) =>
            Json.Leer<Perfil>(await Enviar(HttpMethod.Get, "/api/devices/instalacion", null, true, ct).ConfigureAwait(false));

        public Task ProgresoAsync(Progreso p, CancellationToken ct) =>
            Enviar(HttpMethod.Post, "/api/devices/instalacion/progreso", Json.Serializar(p), true, ct);

        public Task RedAsync(string jsonDiag, CancellationToken ct) =>
            Enviar(HttpMethod.Post, "/api/devices/instalacion/red", jsonDiag, true, ct);

        public async Task<HeartbeatResp> HeartbeatAsync(Heartbeat hb, CancellationToken ct) =>
            Json.Leer<HeartbeatResp>(await Enviar(HttpMethod.Post, "/api/devices/heartbeat", Json.Serializar(hb), true, ct).ConfigureAwait(false));

        public async Task DescargarAsync(string producto, string version, string destino, IProgress<long> progreso, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, _base + "/api/ota/firmware/" + Uri.EscapeDataString(producto) + "/" + Uri.EscapeDataString(version));
            Autenticar(req);
            using (var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!r.IsSuccessStatusCode)
                    throw Error((int)r.StatusCode, await r.Content.ReadAsStringAsync().ConfigureAwait(false));
                var parte = destino + ".part";
                using (var src = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(parte))
                {
                    var buf = new byte[1 << 16];
                    long total = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        total += n;
                        progreso?.Report(total);
                    }
                }
                if (File.Exists(destino)) File.Delete(destino);
                File.Move(parte, destino);
            }
        }

        async Task<string> Enviar(HttpMethod m, string ruta, string json, bool conAuth, CancellationToken ct)
        {
            var req = new HttpRequestMessage(m, _base + ruta);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (conAuth) Autenticar(req);
            using (var r = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                var txt = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) throw Error((int)r.StatusCode, txt);
                return txt;
            }
        }

        void Autenticar(HttpRequestMessage req)
        {
            req.Headers.Add("X-Device-ID", DeviceId ?? "");
            req.Headers.Add("X-Auth-Token", Token ?? "");
        }

        static OrbitXError Error(int status, string cuerpo)
        {
            string msg = null;
            try { msg = Json.Leer<CuerpoError>(cuerpo).error; } catch { }
            return new OrbitXError(status, string.IsNullOrEmpty(msg) ? "OrbitX respondió HTTP " + status : msg);
        }
    }
}
```

- [ ] **Step 5: Correr y ver que pasa** — Run: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release` → `Passed: 14`.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Modelos.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/OrbitXCliente.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/OrbitXClienteTests.cs
git commit -m "feat(instalador): cliente OrbitX (pairing, perfil, progreso, red, OTA)"
```

---

### Task 4: `Estado` reanudable con campos protegidos

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Estado.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/EstadoTests.cs`

**Interfaces:**
- Produces:
  - `interface IProtector { byte[] Proteger(byte[] d); byte[] Desproteger(byte[] d); }`
  - `class Estado`: `static Estado Cargar(string ruta, IProtector p)` (archivo inexistente o corrupto → estado vacío), `void Guardar()` (atómico: `.tmp` + reemplazo), propiedades `DeviceId`, `ZipRescate`, `LotesRespaldo (int)`, `RustdeskId`; protegidas: `Token`, `PairSecret`, `Perfil (Perfil)`; `PairCode`, `PairDesde (long, ms epoch)`; `bool PasoOk(string id)`, `void MarcarOk(string id)`, `void OlvidarPaso(string id)`.

- [ ] **Step 1: Tests**

```csharp
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Tests
{
    // Protector de prueba: invierte los bytes (alcanza para ver que no queda en claro).
    class ProtectorFalso : IProtector
    {
        public byte[] Proteger(byte[] d) => d.Reverse().ToArray();
        public byte[] Desproteger(byte[] d) => d.Reverse().ToArray();
    }

    public class EstadoTests
    {
        string _ruta;
        [SetUp] public void Ini() => _ruta = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        [TearDown] public void Fin() { if (File.Exists(_ruta)) File.Delete(_ruta); }

        [Test]
        public void SinArchivo_EsEstadoVacio()
        {
            var e = Estado.Cargar(_ruta, new ProtectorFalso());
            Assert.That(e.Token, Is.Null);
            Assert.That(e.PasoOk("red"), Is.False);
        }

        [Test]
        public void IdaYVuelta_ConPasosYSecretosProtegidos()
        {
            var e = Estado.Cargar(_ruta, new ProtectorFalso());
            e.DeviceId = "OX-1";
            e.Token = "token-secreto";
            e.Perfil = new Perfil { cliente = "TELLECHEA, JOSE PEDRO", soporte_pass = "clave-soporte" };
            e.MarcarOk("respaldo");
            e.Guardar();

            var crudo = File.ReadAllText(_ruta);
            Assert.That(crudo, Does.Not.Contain("token-secreto"));
            Assert.That(crudo, Does.Not.Contain("clave-soporte"));

            var e2 = Estado.Cargar(_ruta, new ProtectorFalso());
            Assert.That(e2.DeviceId, Is.EqualTo("OX-1"));
            Assert.That(e2.Token, Is.EqualTo("token-secreto"));
            Assert.That(e2.Perfil.soporte_pass, Is.EqualTo("clave-soporte"));
            Assert.That(e2.PasoOk("respaldo"), Is.True);
        }

        [Test]
        public void ArchivoCorrupto_EsEstadoVacio()
        {
            File.WriteAllText(_ruta, "{no es json");
            Assert.That(Estado.Cargar(_ruta, new ProtectorFalso()).DeviceId, Is.Null);
        }

        [Test]
        public void OlvidarPaso_LoVuelveACorrer()
        {
            var e = Estado.Cargar(_ruta, new ProtectorFalso());
            e.MarcarOk("vinculacion");
            e.OlvidarPaso("vinculacion");
            Assert.That(e.PasoOk("vinculacion"), Is.False);
        }
    }
}
```

- [ ] **Step 2: Correr y ver que falla** (compilación).

- [ ] **Step 3: Implementar `Estado.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;

namespace PilotX.Instalador.Core
{
    public interface IProtector
    {
        byte[] Proteger(byte[] datos);
        byte[] Desproteger(byte[] datos);
    }

    // Lo que hace falta para retomar una instalación cortada (luz, internet).
    // Token, secreto del pairing y perfil (trae las claves) van protegidos:
    // en el exe real con DPAPI LocalMachine, así la tarea SYSTEM de red los lee.
    public class Estado
    {
        [DataContract] class Datos
        {
            [DataMember] public string device_id;
            [DataMember] public string token_p;
            [DataMember] public string pair_code;
            [DataMember] public string pair_secret_p;
            [DataMember] public long pair_desde;
            [DataMember] public string perfil_p;
            [DataMember] public string zip_rescate;
            [DataMember] public int lotes_respaldo;
            [DataMember] public string rustdesk_id;
            [DataMember] public List<string> pasos_ok;
        }

        readonly string _ruta;
        readonly IProtector _p;
        Datos _d;

        Estado(string ruta, IProtector p, Datos d) { _ruta = ruta; _p = p; _d = d; if (_d.pasos_ok == null) _d.pasos_ok = new List<string>(); }

        public static Estado Cargar(string ruta, IProtector p)
        {
            Datos d = null;
            try { if (File.Exists(ruta)) d = Json.Leer<Datos>(File.ReadAllText(ruta, Encoding.UTF8)); } catch { d = null; }
            return new Estado(ruta, p, d ?? new Datos());
        }

        public void Guardar()
        {
            var dir = Path.GetDirectoryName(_ruta);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _ruta + ".tmp";
            File.WriteAllText(tmp, Json.Serializar(_d), new UTF8Encoding(false));
            if (File.Exists(_ruta)) File.Replace(tmp, _ruta, null); else File.Move(tmp, _ruta);
        }

        public string DeviceId { get => _d.device_id; set => _d.device_id = value; }
        public string PairCode { get => _d.pair_code; set => _d.pair_code = value; }
        public long PairDesde { get => _d.pair_desde; set => _d.pair_desde = value; }
        public string ZipRescate { get => _d.zip_rescate; set => _d.zip_rescate = value; }
        public int LotesRespaldo { get => _d.lotes_respaldo; set => _d.lotes_respaldo = value; }
        public string RustdeskId { get => _d.rustdesk_id; set => _d.rustdesk_id = value; }

        public string Token { get => Abrir(_d.token_p); set => _d.token_p = Cerrar(value); }
        public string PairSecret { get => Abrir(_d.pair_secret_p); set => _d.pair_secret_p = Cerrar(value); }
        public Perfil Perfil
        {
            get { var j = Abrir(_d.perfil_p); return j == null ? null : Json.Leer<Perfil>(j); }
            set => _d.perfil_p = value == null ? null : Cerrar(Json.Serializar(value));
        }

        public bool PasoOk(string id) => _d.pasos_ok.Contains(id);
        public void MarcarOk(string id) { if (!_d.pasos_ok.Contains(id)) _d.pasos_ok.Add(id); }
        public void OlvidarPaso(string id) => _d.pasos_ok.Remove(id);

        string Cerrar(string claro) => claro == null ? null : Convert.ToBase64String(_p.Proteger(Encoding.UTF8.GetBytes(claro)));
        string Abrir(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            try { return Encoding.UTF8.GetString(_p.Desproteger(Convert.FromBase64String(b64))); } catch { return null; }
        }
    }
}
```

- [ ] **Step 4: Correr y ver que pasa** → `Passed: 18`.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Estado.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/EstadoTests.cs
git commit -m "feat(instalador): estado reanudable con token y claves protegidos"
```

---

### Task 5: Motor de pasos

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Motor.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/MotorTests.cs`

**Interfaces:**
- Produces:
  - `enum Resultado { Ok, Fallo, Saltado }`; `class ResultadoPaso { Resultado Estado; string Mensaje; static Ok(string m = ""); static Fallo(string m); static Saltado(string m); }`
  - `interface IPaso { string Id {get;} string Titulo {get;} bool Critico {get;} bool SiempreCorre {get;} Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct); }`
  - `interface IReportero { void Paso(string id, string titulo, int nro, int total); void Linea(string texto); void Resultado(string id, ResultadoPaso r); void MostrarCodigo(string codigo); }`
  - `class Contexto { Estado Estado; IReportero Reporte; List<string> Problemas; Contexto(Estado e, IReportero r) }`
  - `class Motor { Task<bool> CorrerAsync(IList<IPaso> pasos, Contexto ctx, CancellationToken ct) }` → `true` si terminó sin problemas; crítico que falla corta.

- [ ] **Step 1: Tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Tests
{
    class PasoFalso : IPaso
    {
        public string Id { get; set; }
        public string Titulo => Id;
        public bool Critico { get; set; }
        public bool SiempreCorre { get; set; }
        public Func<ResultadoPaso> Hace = () => ResultadoPaso.Ok();
        public int Veces;
        public Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct) { Veces++; return Task.FromResult(Hace()); }
    }

    class ReporteroNulo : IReportero
    {
        public readonly List<string> Lineas = new List<string>();
        public void Paso(string id, string titulo, int nro, int total) => Lineas.Add("paso " + id);
        public void Linea(string t) => Lineas.Add(t);
        public void Resultado(string id, ResultadoPaso r) => Lineas.Add(id + "=" + r.Estado);
        public void MostrarCodigo(string c) { }
    }

    public class MotorTests
    {
        Contexto Ctx() => new Contexto(Estado.Cargar(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new ProtectorFalso()), new ReporteroNulo());

        [Test]
        public async Task TodoOk_MarcaLosPasosYDevuelveTrue()
        {
            var ctx = Ctx();
            var a = new PasoFalso { Id = "a" }; var b = new PasoFalso { Id = "b" };
            Assert.That(await new Motor().CorrerAsync(new IPaso[] { a, b }, ctx, CancellationToken.None), Is.True);
            Assert.That(ctx.Estado.PasoOk("a") && ctx.Estado.PasoOk("b"), Is.True);
        }

        [Test]
        public async Task Retoma_SaltaLosPasosYaHechos()
        {
            var ctx = Ctx();
            ctx.Estado.MarcarOk("a");
            var a = new PasoFalso { Id = "a" }; var b = new PasoFalso { Id = "b" };
            await new Motor().CorrerAsync(new IPaso[] { a, b }, ctx, CancellationToken.None);
            Assert.That(a.Veces, Is.EqualTo(0));
            Assert.That(b.Veces, Is.EqualTo(1));
        }

        [Test]
        public async Task SiempreCorre_SeEjecutaAunqueYaEsteHecho()
        {
            var ctx = Ctx();
            ctx.Estado.MarcarOk("red");
            var red = new PasoFalso { Id = "red", SiempreCorre = true };
            await new Motor().CorrerAsync(new IPaso[] { red }, ctx, CancellationToken.None);
            Assert.That(red.Veces, Is.EqualTo(1));
        }

        [Test]
        public async Task CriticoQueFalla_CortaYNoCorreLoQueSigue()
        {
            var ctx = Ctx();
            var respaldo = new PasoFalso { Id = "respaldo", Critico = true, Hace = () => ResultadoPaso.Fallo("no se pudo leer la instalación anterior") };
            var instalar = new PasoFalso { Id = "pilotx" };
            Assert.That(await new Motor().CorrerAsync(new IPaso[] { respaldo, instalar }, ctx, CancellationToken.None), Is.False);
            Assert.That(instalar.Veces, Is.EqualTo(0), "si el respaldo falla no se instala nada");
            Assert.That(ctx.Problemas, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task NoCriticoQueFalla_SigueNoMarcaYDevuelveFalse()
        {
            var ctx = Ctx();
            var marca = new PasoFalso { Id = "marca", Hace = () => ResultadoPaso.Fallo("sin fondo") };
            var sig = new PasoFalso { Id = "sig" };
            Assert.That(await new Motor().CorrerAsync(new IPaso[] { marca, sig }, ctx, CancellationToken.None), Is.False);
            Assert.That(sig.Veces, Is.EqualTo(1));
            Assert.That(ctx.Estado.PasoOk("marca"), Is.False, "se reintenta la próxima vez");
        }

        [Test]
        public async Task Excepcion_SeTrataComoFallo()
        {
            var ctx = Ctx();
            var p = new PasoFalso { Id = "x", Critico = true, Hace = () => throw new InvalidOperationException("boom") };
            Assert.That(await new Motor().CorrerAsync(new IPaso[] { p }, ctx, CancellationToken.None), Is.False);
            Assert.That(ctx.Problemas[0], Does.Contain("boom"));
        }
    }
}
```

- [ ] **Step 2: Correr y ver que falla** (compilación).

- [ ] **Step 3: Implementar `Motor.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Instalador.Core
{
    public enum Resultado { Ok, Fallo, Saltado }

    public class ResultadoPaso
    {
        public Resultado Estado;
        public string Mensaje;
        public static ResultadoPaso Ok(string m = "") => new ResultadoPaso { Estado = Resultado.Ok, Mensaje = m };
        public static ResultadoPaso Fallo(string m) => new ResultadoPaso { Estado = Resultado.Fallo, Mensaje = m };
        public static ResultadoPaso Saltado(string m) => new ResultadoPaso { Estado = Resultado.Saltado, Mensaje = m };
    }

    public interface IPaso
    {
        string Id { get; }
        string Titulo { get; }
        bool Critico { get; }        // si falla, no se sigue
        bool SiempreCorre { get; }   // se re-ejecuta aunque ya esté hecho (ej. red)
        Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct);
    }

    public interface IReportero
    {
        void Paso(string id, string titulo, int nro, int total);
        void Linea(string texto);
        void Resultado(string id, ResultadoPaso r);
        void MostrarCodigo(string codigo);
    }

    public class Contexto
    {
        public Estado Estado { get; }
        public IReportero Reporte { get; }
        public List<string> Problemas { get; } = new List<string>();
        public Contexto(Estado e, IReportero r) { Estado = e; Reporte = r; }
    }

    public class Motor
    {
        public async Task<bool> CorrerAsync(IList<IPaso> pasos, Contexto ctx, CancellationToken ct)
        {
            for (int i = 0; i < pasos.Count; i++)
            {
                var p = pasos[i];
                if (!p.SiempreCorre && ctx.Estado.PasoOk(p.Id))
                {
                    ctx.Reporte.Resultado(p.Id, ResultadoPaso.Saltado("ya estaba hecho"));
                    continue;
                }
                ctx.Reporte.Paso(p.Id, p.Titulo, i + 1, pasos.Count);
                ResultadoPaso r;
                try { r = await p.EjecutarAsync(ctx, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { r = ResultadoPaso.Fallo(e.Message); }
                ctx.Reporte.Resultado(p.Id, r);

                if (r.Estado == Resultado.Fallo)
                {
                    ctx.Problemas.Add(p.Titulo + ": " + r.Mensaje);
                    if (p.Critico) return false;
                    continue;
                }
                ctx.Estado.MarcarOk(p.Id);
                ctx.Estado.Guardar();
            }
            return ctx.Problemas.Count == 0;
        }
    }
}
```

- [ ] **Step 4: Correr y ver que pasa** → `Passed: 24`.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Motor.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/MotorTests.cs
git commit -m "feat(instalador): motor de pasos reanudable con pasos críticos"
```

---

### Task 6: Diagnóstico y plan de reparación de red

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Red/Adaptador.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Red/AccionRed.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Red/Diagnosticador.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/RedTests.cs`

**Interfaces:**
- Produces:
  - `[DataContract] class Adaptador { int indice; string nombre; string tipo /* "ethernet"|"wifi"|"celular"|"otro" */; string ip; int prefijo; string gateway; int metrica; bool dhcp; bool internet_ip; bool internet_nombre; }`
  - `abstract class AccionRed { abstract string Descripcion(); abstract string Comando(); }` con `QuitarGateway(Adaptador)`, `FijarMetrica(Adaptador, int)`, `FijarDns(Adaptador)`; `static string AccionRed.Mascara(int prefijo)`.
  - `enum CodigoRed { Ok, CableSeLlevaInternet, MetricasDesordenadas, DnsRoto, SinInternet, SinAdaptadores }`
  - `class DiagnosticoRed { CodigoRed Codigo; string Mensaje; Adaptador Salida; List<AccionRed> Acciones; }`
  - `static DiagnosticoRed Diagnosticador.Diagnosticar(IList<Adaptador> adaptadores, int indiceRutaPorDefecto)`
  - `[DataContract] class InformeRed { List<Adaptador> antes; List<Adaptador> despues; string codigo_antes; string codigo_despues; List<string> acciones; }` (lo que va a OrbitX)

- [ ] **Step 1: Tests**

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PilotX.Instalador.Core.Red;

namespace PilotX.Instalador.Tests
{
    // El caso real: Ethernet USB a la red de nodos (192.168.5.x, sin salida) y
    // WiFi con internet. Si el cable tiene gateway, Windows manda internet por
    // ahí y la pantalla queda sin nube.
    public class RedTests
    {
        static Adaptador Cable(string gw = "192.168.5.1", int metrica = 5, bool dhcp = false) => new Adaptador
            { indice = 7, nombre = "Ethernet 2", tipo = "ethernet", ip = "192.168.5.10", prefijo = 24, gateway = gw, metrica = metrica, dhcp = dhcp };
        static Adaptador Wifi(bool ip = true, bool nombre = true, int metrica = 35) => new Adaptador
            { indice = 12, nombre = "Wi-Fi", tipo = "wifi", ip = "192.168.0.50", prefijo = 24, gateway = "192.168.0.1", metrica = metrica, internet_ip = ip, internet_nombre = nombre };

        [Test]
        public void CableConGatewaySinSalida_GanaLaRuta_EsCableSeLlevaInternet()
        {
            var d = Diagnosticador.Diagnosticar(new List<Adaptador> { Cable(), Wifi() }, 7);
            Assert.That(d.Codigo, Is.EqualTo(CodigoRed.CableSeLlevaInternet));
            Assert.That(d.Salida.nombre, Is.EqualTo("Wi-Fi"));
            Assert.That(d.Acciones.OfType<QuitarGateway>().Single().Adaptador.indice, Is.EqualTo(7));
            Assert.That(d.Acciones.OfType<FijarMetrica>().Single(a => a.Adaptador.indice == 12).Metrica, Is.EqualTo(10));
            Assert.That(d.Acciones.OfType<FijarMetrica>().Single(a => a.Adaptador.indice == 7).Metrica, Is.EqualTo(60));
        }

        [Test]
        public void CableSinGatewayYWifiGana_EsOkSinAcciones()
        {
            var d = Diagnosticador.Diagnosticar(new List<Adaptador> { Cable(gw: "", metrica: 60), Wifi(metrica: 10) }, 12);
            Assert.That(d.Codigo, Is.EqualTo(CodigoRed.Ok));
            Assert.That(d.Acciones, Is.Empty);
        }

        [Test]
        public void SinGatewayPeroMetricasAlReves_EsMetricasDesordenadas()
        {
            var d = Diagnosticador.Diagnosticar(new List<Adaptador> { Cable(gw: "", metrica: 5), Wifi(metrica: 35) }, 12);
            Assert.That(d.Codigo, Is.EqualTo(CodigoRed.MetricasDesordenadas));
            Assert.That(d.Acciones.OfType<QuitarGateway>(), Is.Empty);
        }

        [Test]
        public void SaleAInternetPorIpPeroNoResuelveNombres_EsDnsRoto()
        {
            var d = Diagnosticador.Diagnosticar(new List<Adaptador> { Cable(gw: "", metrica: 60), Wifi(nombre: false, metrica: 10) }, 12);
            Assert.That(d.Codigo, Is.EqualTo(CodigoRed.DnsRoto));
            Assert.That(d.Acciones.OfType<FijarDns>().Single().Adaptador.indice, Is.EqualTo(12));
        }

        [Test]
        public void NadieSale_EsSinInternetSinAcciones()
        {
            var d = Diagnosticador.Diagnosticar(new List<Adaptador> { Cable(), Wifi(ip: false, nombre: false) }, 7);
            Assert.That(d.Codigo, Is.EqualTo(CodigoRed.SinInternet));
            Assert.That(d.Acciones, Is.Empty);
            Assert.That(d.Mensaje, Does.Contain("WiFi"));
        }

        [Test]
        public void SinAdaptadores()
        {
            Assert.That(Diagnosticador.Diagnosticar(new List<Adaptador>(), -1).Codigo, Is.EqualTo(CodigoRed.SinAdaptadores));
        }

        [Test]
        public void SiElCableSaleYGanaLaRuta_SeRespetaLoQueYaAnda()
        {
            var cable = Cable(); cable.internet_ip = cable.internet_nombre = true;
            var d = Diagnosticador.Diagnosticar(new List<Adaptador> { cable, Wifi() }, 7);
            Assert.That(d.Salida.tipo, Is.EqualTo("ethernet"), "si el cable sale, se respeta lo que ya anda");
            Assert.That(d.Codigo, Is.EqualTo(CodigoRed.Ok));
        }

        [Test]
        public void Comandos_SonLosEsperados()
        {
            Assert.That(new QuitarGateway(Cable()).Comando(),
                Is.EqualTo("netsh interface ipv4 set address name=\"Ethernet 2\" static 192.168.5.10 255.255.255.0"));
            Assert.That(new FijarMetrica(Wifi(), 10).Comando(),
                Is.EqualTo("Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 10"));
            Assert.That(new FijarDns(Wifi()).Comando(),
                Is.EqualTo("Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses 1.1.1.1,8.8.8.8"));
            Assert.That(AccionRed.Mascara(24), Is.EqualTo("255.255.255.0"));
            Assert.That(AccionRed.Mascara(16), Is.EqualTo("255.255.0.0"));
        }
    }
}
```

- [ ] **Step 2: Correr y ver que falla** (compilación).

- [ ] **Step 3: Implementar**

`Red/Adaptador.cs`:

```csharp
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PilotX.Instalador.Core.Red
{
    [DataContract] public class Adaptador
    {
        [DataMember] public int indice;
        [DataMember] public string nombre;
        [DataMember] public string tipo;      // ethernet | wifi | celular | otro
        [DataMember] public string ip;
        [DataMember] public int prefijo;
        [DataMember] public string gateway;   // "" = sin puerta de enlace
        [DataMember] public int metrica;
        [DataMember] public bool dhcp;
        [DataMember] public bool internet_ip;     // TCP a 1.1.1.1:443 saliendo por este adaptador
        [DataMember] public bool internet_nombre; // TCP a orbitx (resuelto por DNS) por este adaptador
    }

    [DataContract] public class InformeRed
    {
        [DataMember] public List<Adaptador> antes;
        [DataMember] public List<Adaptador> despues;
        [DataMember] public string codigo_antes;
        [DataMember] public string codigo_despues;
        [DataMember] public List<string> acciones;
    }
}
```

`Red/AccionRed.cs`:

```csharp
namespace PilotX.Instalador.Core.Red
{
    public abstract class AccionRed
    {
        public Adaptador Adaptador { get; }
        protected AccionRed(Adaptador a) { Adaptador = a; }
        public abstract string Descripcion();
        public abstract string Comando();

        public static string Mascara(int prefijo)
        {
            uint m = prefijo <= 0 ? 0u : 0xFFFFFFFFu << (32 - prefijo);
            return $"{m >> 24 & 255}.{m >> 16 & 255}.{m >> 8 & 255}.{m & 255}";
        }
    }

    // netsh: apaga DHCP y fija IP SIN gateway en un solo comando (igual que
    // NetApplyWatcher). El cable queda en la red de nodos y deja de pelear la
    // ruta a internet.
    public class QuitarGateway : AccionRed
    {
        public QuitarGateway(Adaptador a) : base(a) { }
        public override string Descripcion() => $"Sacarle la salida a internet al cable \"{Adaptador.nombre}\" (queda {Adaptador.ip} para los nodos)";
        public override string Comando() =>
            $"netsh interface ipv4 set address name=\"{Adaptador.nombre}\" static {Adaptador.ip} {Mascara(Adaptador.prefijo)}";
    }

    public class FijarMetrica : AccionRed
    {
        public int Metrica { get; }
        public FijarMetrica(Adaptador a, int metrica) : base(a) { Metrica = metrica; }
        public override string Descripcion() => Metrica <= 10
            ? $"Internet sale por \"{Adaptador.nombre}\""
            : $"\"{Adaptador.nombre}\" queda en segundo plano";
        public override string Comando() =>
            $"Set-NetIPInterface -InterfaceIndex {Adaptador.indice} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Metrica}";
    }

    public class FijarDns : AccionRed
    {
        public FijarDns(Adaptador a) : base(a) { }
        public override string Descripcion() => $"DNS de respaldo en \"{Adaptador.nombre}\"";
        public override string Comando() =>
            $"Set-DnsClientServerAddress -InterfaceIndex {Adaptador.indice} -ServerAddresses 1.1.1.1,8.8.8.8";
    }
}
```

`Red/Diagnosticador.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace PilotX.Instalador.Core.Red
{
    public enum CodigoRed { Ok, CableSeLlevaInternet, MetricasDesordenadas, DnsRoto, SinInternet, SinAdaptadores }

    public class DiagnosticoRed
    {
        public CodigoRed Codigo;
        public string Mensaje;
        public Adaptador Salida;
        public List<AccionRed> Acciones = new List<AccionRed>();
    }

    public static class Diagnosticador
    {
        const int MetricaSalida = 10, MetricaResto = 60;

        public static DiagnosticoRed Diagnosticar(IList<Adaptador> ads, int indiceRutaPorDefecto)
        {
            var d = new DiagnosticoRed();
            if (ads == null || ads.Count == 0)
            {
                d.Codigo = CodigoRed.SinAdaptadores;
                d.Mensaje = "No hay ninguna placa de red conectada.";
                return d;
            }

            // Salida: la que ya gana la ruta si sale; si no, WiFi > celular > cable.
            var conSalida = ads.Where(a => a.internet_ip).ToList();
            if (conSalida.Count == 0)
            {
                d.Codigo = CodigoRed.SinInternet;
                var wifi = ads.FirstOrDefault(a => a.tipo == "wifi");
                d.Mensaje = wifi != null
                    ? "El WiFi está conectado pero no tiene internet. Probá con otra red o con el celular."
                    : "Ningún adaptador sale a internet. Conectá el WiFi o el celular.";
                return d;
            }
            d.Salida = conSalida.FirstOrDefault(a => a.indice == indiceRutaPorDefecto)
                       ?? conSalida.OrderBy(a => Prioridad(a.tipo)).ThenBy(a => a.metrica).First();

            // Cables sin salida con gateway: pelean la ruta por defecto.
            foreach (var a in ads.Where(a => a != d.Salida && !a.internet_ip && !string.IsNullOrEmpty(a.gateway) && a.tipo == "ethernet"))
                d.Acciones.Add(new QuitarGateway(a));
            bool cableSeLaLleva = d.Acciones.Count > 0 && indiceRutaPorDefecto != d.Salida.indice;

            // Métricas: la salida tiene que tener la menor de todas.
            bool metricasMal = ads.Any(a => a != d.Salida && a.metrica <= d.Salida.metrica);
            if (metricasMal || d.Acciones.Count > 0)
                foreach (var a in ads)
                    d.Acciones.Add(new FijarMetrica(a, a == d.Salida ? MetricaSalida : MetricaResto));

            bool dnsMal = !d.Salida.internet_nombre;
            if (dnsMal) d.Acciones.Add(new FijarDns(d.Salida));

            if (cableSeLaLleva)
            {
                d.Codigo = CodigoRed.CableSeLlevaInternet;
                d.Mensaje = "El cable USB se está llevando el tráfico de internet y no tiene salida.";
            }
            else if (d.Acciones.OfType<QuitarGateway>().Any() || metricasMal)
            {
                d.Codigo = CodigoRed.MetricasDesordenadas;
                d.Mensaje = $"Internet anda por \"{d.Salida.nombre}\" pero el orden de las redes puede cambiar solo.";
            }
            else if (dnsMal)
            {
                d.Codigo = CodigoRed.DnsRoto;
                d.Mensaje = $"\"{d.Salida.nombre}\" tiene internet pero no resuelve nombres (DNS).";
            }
            else
            {
                d.Codigo = CodigoRed.Ok;
                d.Mensaje = $"Internet sale por \"{d.Salida.nombre}\".";
            }
            return d;
        }

        static int Prioridad(string tipo) => tipo == "wifi" ? 0 : tipo == "celular" ? 1 : tipo == "ethernet" ? 2 : 3;
    }
}
```

- [ ] **Step 4: Correr y ver que pasa** → `Passed: 32`.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Red SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/RedTests.cs
git commit -m "feat(instalador): diagnóstico de red Ethernet USB + WiFi con plan de reparación"
```

---

### Task 7: `Rescate.ContarLotes`, `SelfTest` y `LineaComandos`

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Rescate.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/SelfTest.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/LineaComandos.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/RescateSelfTestTests.cs`

**Interfaces:**
- Produces:
  - `LineaComandos.Arg(string): string` — entrecomilla un argumento con las reglas de `CommandLineToArgvW` (las claves y nombres de cliente pueden traer comillas o terminar en `\`).
  - `Rescate.ContarLotes(IEnumerable<string> entradasZip): int` — carpetas distintas bajo `Fields/` (también `origenN/Fields/`).
  - `Rescate.TieneDatos(IEnumerable<string> entradasZip): bool` — hay algo bajo `Fields/` o `Vehicles/`.
  - `[DataContract] class Chequeo { string nombre; bool ok; string detalle; }`
  - `interface ISonda { string Nombre {get;} Task<Chequeo> ProbarAsync(CancellationToken ct); }`
  - `static Task<List<Chequeo>> SelfTest.CorrerAsync(IEnumerable<ISonda> sondas, CancellationToken ct)` (una sonda que tira excepción = chequeo fallido); `static bool SelfTest.TodoOk(IEnumerable<Chequeo>)`.

- [ ] **Step 1: Tests**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Tests
{
    class SondaFalsa : ISonda
    {
        public string Nombre { get; set; }
        public Func<Chequeo> Hace;
        public Task<Chequeo> ProbarAsync(CancellationToken ct) => Task.FromResult(Hace());
    }

    public class RescateSelfTestTests
    {
        [Test]
        public void ContarLotes_CuentaCarpetasDistintasIncluyendoOrigenes()
        {
            var e = new[] {
                "Fields/Lote Norte/Field.txt", "Fields/Lote Norte/Boundary.txt", "Fields/La Paloma/Field.txt",
                "origen2/Fields/Lote Sur/Field.txt", "Vehicles/Articulado.XML", "_informe.txt" };
            Assert.That(Rescate.ContarLotes(e), Is.EqualTo(3));
            Assert.That(Rescate.TieneDatos(e), Is.True);
        }

        [Test]
        public void ZipSinFieldsNiVehicles_NoTieneDatos()
        {
            Assert.That(Rescate.TieneDatos(new[] { "_informe.txt", "config.json" }), Is.False);
            Assert.That(Rescate.ContarLotes(new[] { "_informe.txt" }), Is.EqualTo(0));
        }

        [Test]
        public async Task SelfTest_UnaSondaQueExplota_EsChequeoFallido()
        {
            var r = await SelfTest.CorrerAsync(new ISonda[] {
                new SondaFalsa { Nombre = "PilotX arranca", Hace = () => new Chequeo { nombre = "PilotX arranca", ok = true } },
                new SondaFalsa { Nombre = "Usuarios", Hace = () => throw new Exception("Get-LocalUser falló") },
            }, CancellationToken.None);
            Assert.That(r, Has.Count.EqualTo(2));
            Assert.That(r[1].ok, Is.False);
            Assert.That(r[1].detalle, Does.Contain("Get-LocalUser"));
            Assert.That(SelfTest.TodoOk(r), Is.False);
        }

        [Test]
        public void TodoOk_SiTodasPasan()
        {
            Assert.That(SelfTest.TodoOk(new[] { new Chequeo { ok = true }, new Chequeo { ok = true } }), Is.True);
        }

        [Test]
        public void Arg_EscapaComillasYBarraFinal()
        {
            Assert.That(LineaComandos.Arg("TELLECHEA, JOSE PEDRO"), Is.EqualTo("\"TELLECHEA, JOSE PEDRO\""));
            Assert.That(LineaComandos.Arg("cla\"ve"), Is.EqualTo("\"cla\\\"ve\""));
            Assert.That(LineaComandos.Arg(@"C:\PilotX\"), Is.EqualTo("\"C:\\PilotX\\\\\""));
            Assert.That(LineaComandos.Arg(@"a\\""b"), Is.EqualTo("\"a\\\\\\\\\\\"b\""));
        }

        [Test]
        public void Arg_NullEsVacio()
        {
            Assert.That(LineaComandos.Arg(null), Is.EqualTo("\"\""));
        }
    }
}
```

- [ ] **Step 2: Correr y ver que falla** (compilación).

- [ ] **Step 3: Implementar**

`Rescate.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace PilotX.Instalador.Core
{
    // Lee el ZIP que arma Rescatar-AOG.ps1: Fields/ y Vehicles/ en la raíz, o
    // bajo origenN/ si había más de una carpeta de datos.
    public static class Rescate
    {
        public static int ContarLotes(IEnumerable<string> entradas) =>
            entradas.Select(LoteDe).Where(l => l != null).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        public static bool TieneDatos(IEnumerable<string> entradas) =>
            entradas.Any(e => Partes(e).Any(p => p.Equals("Fields", StringComparison.OrdinalIgnoreCase) || p.Equals("Vehicles", StringComparison.OrdinalIgnoreCase)));

        static string LoteDe(string entrada)
        {
            var p = Partes(entrada);
            for (int i = 0; i < p.Length - 2; i++)
                if (p[i].Equals("Fields", StringComparison.OrdinalIgnoreCase))
                    return string.Join("/", p.Take(i + 2));
            return null;
        }

        static string[] Partes(string e) => (e ?? "").Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
```

`SelfTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Instalador.Core
{
    [DataContract] public class Chequeo
    {
        [DataMember] public string nombre;
        [DataMember] public bool ok;
        [DataMember] public string detalle;
    }

    public interface ISonda
    {
        string Nombre { get; }
        Task<Chequeo> ProbarAsync(CancellationToken ct);
    }

    public static class SelfTest
    {
        public static async Task<List<Chequeo>> CorrerAsync(IEnumerable<ISonda> sondas, CancellationToken ct)
        {
            var r = new List<Chequeo>();
            foreach (var s in sondas)
            {
                try { r.Add(await s.ProbarAsync(ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { r.Add(new Chequeo { nombre = s.Nombre, ok = false, detalle = e.Message }); }
            }
            return r;
        }

        public static bool TodoOk(IEnumerable<Chequeo> r) => r.All(c => c.ok);
    }
}
```

`LineaComandos.cs`:

```csharp
using System.Text;

namespace PilotX.Instalador.Core
{
    public static class LineaComandos
    {
        // Reglas de CommandLineToArgvW: las barras solo son especiales justo
        // antes de una comilla (o del cierre). Así una clave con " o una ruta
        // terminada en \ llegan enteras al script.
        public static string Arg(string s)
        {
            s = s ?? "";
            var sb = new StringBuilder("\"");
            int barras = 0;
            foreach (var c in s)
            {
                if (c == '\\') { barras++; continue; }
                if (c == '"') { sb.Append('\\', barras * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', barras); sb.Append(c); }
                barras = 0;
            }
            sb.Append('\\', barras * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: Correr y ver que pasa** → `Passed: 38`.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/Rescate.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/SelfTest.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Core/LineaComandos.cs SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests/RescateSelfTestTests.cs
git commit -m "feat(instalador): conteo de lotes, self-test y argumentos de línea de comandos"
```

---

### Task 8: Capa Windows (PowerShell, DPAPI, kit, equipo, red)

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/PowerShell.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/ProtectorDpapi.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/Kit.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/Equipo.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/RedWindows.cs`

Estos archivos tocan el sistema real: se verifican compilando y con la prueba manual de Task 12 (no hay tests unitarios, la lógica que deciden ya está testeada en Core).

**Interfaces:**
- Produces:
  - `PowerShell.CorrerScriptAsync(string rutaPs1, string argumentos, Action<string> linea, CancellationToken ct): Task<int>` (exit code; cada línea de stdout/stderr a `linea`)
  - `PowerShell.CorrerComandoAsync(string comando, Action<string> linea, CancellationToken ct): Task<(int codigo, string salida)>`
  - `PowerShell.CorrerExeAsync(string exe, string argumentos, Action<string> linea, CancellationToken ct): Task<int>` (stdin cerrado)
  - `PowerShell.Arg(string): string` (comillas según la línea de comandos de Windows)
  - `class ProtectorDpapi : IProtector` (DPAPI `LocalMachine` + entropía fija)
  - `Kit.Extraer(string nombreRecurso, string carpeta): string` (ruta del archivo escrito); constantes `Kit.Dir = @"C:\PilotX\kit"`
  - `Equipo.Macs(): IEnumerable<string>`, `Equipo.VersionWindows(): string`, `Equipo.Hostname`, `Equipo.InstalacionAnteriorDetectada(): bool`, `Equipo.EsAdmin(): bool`, `Equipo.LibreEnC(): long`
  - `RedWindows.MedirAsync(CancellationToken): Task<(List<Adaptador> ads, int rutaPorDefecto)>`, `RedWindows.AplicarAsync(IEnumerable<AccionRed>, Action<string>, CancellationToken): Task`, `RedWindows.RevisarYRepararAsync(Action<string> linea, CancellationToken): Task<(DiagnosticoRed antes, DiagnosticoRed despues, InformeRed informe)>`

- [ ] **Step 1: `PowerShell.cs`**

```csharp
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Instalador.Windows
{
    internal static class PowerShell
    {
        const string Exe = "powershell.exe";

        public static Task<int> CorrerScriptAsync(string rutaPs1, string argumentos, Action<string> linea, CancellationToken ct) =>
            Correr(Exe, $"-NoProfile -ExecutionPolicy Bypass -File \"{rutaPs1}\" {argumentos}", linea, ct);

        // Exe directo (kiosko, .bat, schtasks) con stdin cerrado: equivale al
        // "< NUL" de los scripts viejos, nada se queda esperando una tecla.
        public static Task<int> CorrerExeAsync(string exe, string argumentos, Action<string> linea, CancellationToken ct) =>
            Correr(exe, argumentos, linea, ct);

        public static string Arg(string s) => Core.LineaComandos.Arg(s);

        public static async Task<(int codigo, string salida)> CorrerComandoAsync(string comando, Action<string> linea, CancellationToken ct)
        {
            var sb = new StringBuilder();
            // -EncodedCommand: sin problemas de comillas ni de acentos.
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';" + comando));
            int c = await Correr(Exe, "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64, l => { sb.AppendLine(l); linea?.Invoke(l); }, ct).ConfigureAwait(false);
            return (c, sb.ToString());
        }

        static Task<int> Correr(string exe, string args, Action<string> linea, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<int>();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) linea?.Invoke(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) linea?.Invoke(e.Data); };
            p.Exited += (s, e) => { p.WaitForExit(); tcs.TrySetResult(p.ExitCode); p.Dispose(); };
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } tcs.TrySetCanceled(); });
            return tcs.Task;
        }
    }
}
```

- [ ] **Step 2: `ProtectorDpapi.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Windows
{
    // LocalMachine: lo puede abrir la tarea SYSTEM de red (--red --auto) en la
    // misma pantalla, y no sirve si alguien copia el archivo a otra máquina.
    internal class ProtectorDpapi : IProtector
    {
        static readonly byte[] Entropia = Encoding.UTF8.GetBytes("PilotX.Instalador/v1");
        public byte[] Proteger(byte[] d) => ProtectedData.Protect(d, Entropia, DataProtectionScope.LocalMachine);
        public byte[] Desproteger(byte[] d) => ProtectedData.Unprotect(d, Entropia, DataProtectionScope.LocalMachine);
    }
}
```

- [ ] **Step 3: `Kit.cs`**

```csharp
using System.IO;
using System.Reflection;

namespace PilotX.Instalador.Windows
{
    internal static class Kit
    {
        public const string Dir = @"C:\PilotX\kit";

        // nombre = LogicalName sin el prefijo "Kit." (ej. "Rescatar-AOG.ps1").
        // Se escribe con BOM: PowerShell 5.1 lee como ANSI un .ps1 sin BOM y
        // rompe los acentos de los mensajes.
        public static string Extraer(string nombre, string carpeta = Dir)
        {
            Directory.CreateDirectory(carpeta);
            var destino = Path.Combine(carpeta, nombre);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kit." + nombre))
            {
                if (s == null) throw new FileNotFoundException("Falta en el instalador: " + nombre);
                using (var r = new StreamReader(s))
                    File.WriteAllText(destino, r.ReadToEnd(), new System.Text.UTF8Encoding(true));
            }
            return destino;
        }
    }
}
```

- [ ] **Step 4: `Equipo.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Win32;

namespace PilotX.Instalador.Windows
{
    internal static class Equipo
    {
        public static string Hostname => Environment.MachineName;

        // Mismo orden y filtro que OrbitXConfig.cs de PilotX.
        public static IEnumerable<string> Macs() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString());

        public static string VersionWindows()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                return $"{k?.GetValue("ProductName")} {k?.GetValue("DisplayVersion")} ({Environment.OSVersion.Version.Build})".Trim();
        }

        public static bool EsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static long LibreEnC() => new DriveInfo("C").AvailableFreeSpace;

        // Datos de una instalación anterior: la clave de registro de la carpeta
        // de trabajo o las carpetas de lotes/vehículos en Documentos.
        public static bool InstalacionAnteriorDetectada()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS")) if (k != null) return true;
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Directory.Exists(Path.Combine(docs, "AgOpenGPS", "Fields")) || Directory.Exists(Path.Combine(docs, "AgOpenGPS", "Vehicles"));
        }
    }
}
```

- [ ] **Step 5: `RedWindows.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PilotX.Instalador.Core;
using PilotX.Instalador.Core.Red;

namespace PilotX.Instalador.Windows
{
    internal static class RedWindows
    {
        const string HostOrbitX = "orbitx.agroparallel.com";

        [DataContract] class IfMetrica { [DataMember] public int ifIndex; [DataMember] public int InterfaceMetric; }

        public static async Task<(List<Adaptador> ads, int rutaPorDefecto)> MedirAsync(CancellationToken ct)
        {
            var metricas = await Metricas(ct).ConfigureAwait(false);
            IPAddress[] orbitx = new IPAddress[0];
            try { orbitx = (await Dns.GetHostAddressesAsync(HostOrbitX).ConfigureAwait(false)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray(); } catch { }

            var ads = new List<Adaptador>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var p = n.GetIPProperties();
                var v4 = p.GetIPv4Properties();
                var uni = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null || uni == null) continue;
                var gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
                var a = new Adaptador
                {
                    indice = v4.Index,
                    nombre = n.Name,
                    tipo = Tipo(n),
                    ip = uni.Address.ToString(),
                    prefijo = uni.PrefixLength,
                    gateway = gw?.ToString() ?? "",
                    metrica = metricas.TryGetValue(v4.Index, out var m) ? m : 0,
                    dhcp = v4.IsDhcpEnabled,
                };
                a.internet_ip = await Conecta(uni.Address, IPAddress.Parse("1.1.1.1"), ct).ConfigureAwait(false);
                a.internet_nombre = orbitx.Length > 0 && await Conecta(uni.Address, orbitx[0], ct).ConfigureAwait(false);
                ads.Add(a);
            }
            return (ads, await RutaPorDefecto(ct).ConfigureAwait(false));
        }

        public static async Task AplicarAsync(IEnumerable<AccionRed> acciones, Action<string> linea, CancellationToken ct)
        {
            foreach (var a in acciones)
            {
                linea?.Invoke("  · " + a.Descripcion());
                var (codigo, salida) = await PowerShell.CorrerComandoAsync(a.Comando(), null, ct).ConfigureAwait(false);
                if (codigo != 0) linea?.Invoke("    (no se pudo: " + salida.Trim() + ")");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); // Windows reacomoda rutas
        }

        public static async Task<(DiagnosticoRed antes, DiagnosticoRed despues, InformeRed informe)> RevisarYRepararAsync(Action<string> linea, CancellationToken ct)
        {
            var (ads, ruta) = await MedirAsync(ct).ConfigureAwait(false);
            var antes = Diagnosticador.Diagnosticar(ads, ruta);
            linea?.Invoke(antes.Mensaje);
            var despues = antes;
            var adsDespues = ads;
            if (antes.Acciones.Count > 0)
            {
                await AplicarAsync(antes.Acciones, linea, ct).ConfigureAwait(false);
                var (ads2, ruta2) = await MedirAsync(ct).ConfigureAwait(false);
                adsDespues = ads2;
                despues = Diagnosticador.Diagnosticar(ads2, ruta2);
                linea?.Invoke("Después de reparar: " + despues.Mensaje);
            }
            var informe = new InformeRed
            {
                antes = ads, despues = adsDespues,
                codigo_antes = antes.Codigo.ToString(), codigo_despues = despues.Codigo.ToString(),
                acciones = antes.Acciones.Select(a => a.Descripcion()).ToList(),
            };
            return (antes, despues, informe);
        }

        static string Tipo(NetworkInterface n)
        {
            switch (n.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Wireless80211: return "wifi";
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT: return "ethernet";
                case NetworkInterfaceType.Wwanpp:
                case NetworkInterfaceType.Wwanpp2: return "celular";
                default: return "otro";
            }
        }

        // Socket ligado a la IP del adaptador: Windows (modelo strong host) sale
        // por ese adaptador, así se sabe si ESE adaptador tiene internet.
        static async Task<bool> Conecta(IPAddress origen, IPAddress destino, CancellationToken ct)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    s.Bind(new IPEndPoint(origen, 0));
                    var t = Task.Factory.FromAsync(s.BeginConnect, s.EndConnect, new IPEndPoint(destino, 443), null);
                    return await Task.WhenAny(t, Task.Delay(3000, ct)).ConfigureAwait(false) == t && !t.IsFaulted && s.Connected;
                }
                catch { return false; }
            }
        }

        static async Task<Dictionary<int, int>> Metricas(CancellationToken ct)
        {
            var (c, salida) = await PowerShell.CorrerComandoAsync(
                "Get-NetIPInterface -AddressFamily IPv4 | Select-Object ifIndex,InterfaceMetric | ConvertTo-Json -Compress", null, ct).ConfigureAwait(false);
            var r = new Dictionary<int, int>();
            try
            {
                var txt = salida.Trim();
                if (txt.StartsWith("{")) txt = "[" + txt + "]";
                foreach (var m in Json.Leer<List<IfMetrica>>(txt)) r[m.ifIndex] = m.InterfaceMetric;
            }
            catch { }
            return r;
        }

        static async Task<int> RutaPorDefecto(CancellationToken ct)
        {
            var (c, salida) = await PowerShell.CorrerComandoAsync(
                "(Find-NetRoute -RemoteIPAddress 1.1.1.1 -ErrorAction SilentlyContinue | Select-Object -First 1).InterfaceIndex", null, ct).ConfigureAwait(false);
            return int.TryParse(salida.Trim(), out var i) ? i : -1;
        }
    }
}
```

- [ ] **Step 6: Compilar**

Run: `dotnet build SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj -c Release`
Expected: `Build succeeded` sin errores.

- [ ] **Step 7: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows
git commit -m "feat(instalador): capa Windows (PowerShell, DPAPI, kit, medición y reparación de red)"
```

---

### Task 9: Pasos de la instalación

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Pasos/PasoBase.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Pasos/Pasos.cs` (los 12 pasos, cada uno una clase corta)
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Kit/RustDesk-Configurar.ps1` (reemplaza el vacío)
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/SondasWindows.cs`

**Interfaces:**
- Consumes: todo Core (Tasks 1-7) y la capa Windows (Task 8).
- Produces: `static List<IPaso> Pasos.Todos(OrbitXCliente ox)`; `SondasWindows.Todas(OrbitXCliente ox, Estado e): IEnumerable<ISonda>`.

- [ ] **Step 1: `Kit/RustDesk-Configurar.ps1`**

```powershell
# RustDesk-Configurar.ps1 — instala (si hace falta) y configura RustDesk contra
# el servidor de Agro Parallel. Misma config que Tools/provision-server/rustdesk.ps1.
# Última línea de salida = ID de RustDesk.
param(
    [Parameter(Mandatory=$true)][string]$Instalador,
    [Parameter(Mandatory=$true)][string]$Servidor,
    [Parameter(Mandatory=$true)][string]$Clave,
    [Parameter(Mandatory=$true)][string]$Password
)
$ErrorActionPreference = "Stop"
$inst = "C:\Program Files\RustDesk\rustdesk.exe"
if (-not (Test-Path $inst)) {
    Write-Host "Instalando RustDesk..."
    Start-Process $Instalador -ArgumentList "--silent-install" -Wait
    foreach ($i in 1..90) { if (Test-Path $inst) { break }; Start-Sleep -Seconds 2 }
    if (-not (Test-Path $inst)) { throw "RustDesk no quedó instalado en $inst" }
}
Stop-Service RustDesk -Force -ErrorAction SilentlyContinue
Get-Process rustdesk -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$toml = "rendezvous_server = '${Servidor}:21116'`nnat_type = 1`nserial = 0`n`n[options]`ncustom-rendezvous-server = '$Servidor'`nrelay-server = '$Servidor'`nkey = '$Clave'`napprove-mode = 'password'`nverification-method = 'use-permanent-password'`nenable-file-transfer = 'Y'`n"
$dirs = @("C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config") + (Get-ChildItem C:\Users -Directory | ForEach-Object { $_.FullName + "\AppData\Roaming\RustDesk\config" })
foreach ($dir in $dirs) {
    try {
        if (-not (Test-Path (Split-Path $dir -Parent))) { continue }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Set-Content (Join-Path $dir "RustDesk2.toml") $toml -Encoding ascii
    } catch { }
}
Start-Service RustDesk
Start-Sleep -Seconds 8
& $inst --password $Password 2>$null | Out-Null
Start-Sleep -Seconds 2
(& $inst --get-id 2>$null | Select-Object -Last 1).Trim()
```

- [ ] **Step 2: `Pasos/PasoBase.cs`**

```csharp
using System.Threading;
using System.Threading.Tasks;
using PilotX.Instalador.Core;

namespace PilotX.Instalador.Pasos
{
    internal abstract class PasoBase : IPaso
    {
        public abstract string Id { get; }
        public abstract string Titulo { get; }
        public virtual bool Critico => true;
        public virtual bool SiempreCorre => false;
        protected OrbitXCliente Ox { get; }
        protected PasoBase(OrbitXCliente ox) { Ox = ox; }
        public abstract Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct);

        // Avance a OrbitX: solo cuando ya hay token; nunca frena la instalación.
        protected async Task Avisar(Contexto ctx, string msg, string estado = null)
        {
            ctx.Reporte.Linea(msg);
            if (string.IsNullOrEmpty(Ox.Token)) return;
            try
            {
                using (var cts = new CancellationTokenSource(10000))
                    await Ox.ProgresoAsync(new Progreso { paso = Id, msg = msg, estado = estado }, cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }
}
```

- [ ] **Step 3: `Pasos/Pasos.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PilotX.Instalador.Core;
using PilotX.Instalador.Windows;

namespace PilotX.Instalador.Pasos
{
    internal static class Pasos
    {
        public const string Destino = @"C:\PilotX";
        public const string Rescate = @"C:\Rescate-PilotX";

        public static List<IPaso> Todos(OrbitXCliente ox) => new List<IPaso>
        {
            new PasoChequeo(ox), new PasoRed(ox), new PasoRespaldo(ox), new PasoVinculacion(ox),
            new PasoPerfil(ox), new PasoPilotX(ox), new PasoEquipo(ox), new PasoRustDesk(ox),
            new PasoRedPersistente(ox), new PasoDevolucion(ox), new PasoSelfTest(ox), new PasoKiosko(ox),
        };

        public static string Cita(string s) => PowerShell.Arg(s);
    }

    // 0. Chequeo previo — no toca nada.
    internal class PasoChequeo : PasoBase
    {
        public PasoChequeo(OrbitXCliente ox) : base(ox) { }
        public override string Id => "chequeo";
        public override string Titulo => "Chequeo del equipo";
        public override bool SiempreCorre => true;
        public override Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            if (!Equipo.EsAdmin()) return Task.FromResult(ResultadoPaso.Fallo("Hay que abrir el instalador como administrador."));
            if (!Environment.Is64BitOperatingSystem) return Task.FromResult(ResultadoPaso.Fallo("PilotX necesita Windows de 64 bits."));
            if (Environment.OSVersion.Version.Major < 10) return Task.FromResult(ResultadoPaso.Fallo("PilotX necesita Windows 10 u 11."));
            var libreGb = Equipo.LibreEnC() / (1024.0 * 1024 * 1024);
            if (libreGb < 2) return Task.FromResult(ResultadoPaso.Fallo($"Hay {libreGb:0.0} GB libres en C:, hacen falta 2 GB."));
            return Task.FromResult(ResultadoPaso.Ok($"{Equipo.VersionWindows()}, {libreGb:0} GB libres"));
        }
    }

    // 1. Red — sin internet no hay vinculación. Repara sola y re-mide.
    internal class PasoRed : PasoBase
    {
        public PasoRed(OrbitXCliente ox) : base(ox) { }
        public override string Id => "red";
        public override string Titulo => "Red e internet";
        public override bool SiempreCorre => true;
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var (antes, despues, informe) = await RedWindows.RevisarYRepararAsync(ctx.Reporte.Linea, ct);
            if (!string.IsNullOrEmpty(Ox.Token))
                try { await Ox.RedAsync(Json.Serializar(informe), ct); } catch { }
            if (despues.Codigo == Core.Red.CodigoRed.SinInternet || despues.Codigo == Core.Red.CodigoRed.SinAdaptadores)
                return ResultadoPaso.Fallo(despues.Mensaje);
            return ResultadoPaso.Ok(despues.Mensaje);
        }
    }

    // 2. Respaldo de la instalación anterior. Si falla, no se instala nada.
    internal class PasoRespaldo : PasoBase
    {
        public PasoRespaldo(OrbitXCliente ox) : base(ox) { }
        public override string Id => "respaldo";
        public override string Titulo => "Respaldo de la instalación anterior";
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            // Configuración de un PilotX previo: copia de seguridad, el zip nuevo no la pisa.
            if (Directory.Exists(Pasos.Destino))
            {
                Directory.CreateDirectory(Pasos.Rescate);
                var cfgZip = Path.Combine(Pasos.Rescate, $"pilotx-config-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                using (var z = ZipFile.Open(cfgZip, ZipArchiveMode.Create))
                    foreach (var f in Directory.GetFiles(Pasos.Destino, "*.json", SearchOption.AllDirectories)
                                 .Where(f => f.IndexOf(@"\wwwroot\", StringComparison.OrdinalIgnoreCase) < 0))
                        z.CreateEntryFromFile(f, f.Substring(Pasos.Destino.Length + 1));
                ctx.Reporte.Linea("Configuración anterior de PilotX respaldada en " + cfgZip);
            }

            if (!Equipo.InstalacionAnteriorDetectada())
                return ResultadoPaso.Ok("No hay instalación anterior con lotes.");

            var script = Kit.Extraer("Rescatar-AOG.ps1");
            var desde = DateTime.Now;
            int codigo = await PowerShell.CorrerScriptAsync(script, "", ctx.Reporte.Linea, ct);
            var zip = new DirectoryInfo(Pasos.Rescate).GetFiles("rescate-*.zip")
                .Where(f => f.LastWriteTime >= desde.AddMinutes(-1)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (zip == null) return ResultadoPaso.Fallo($"El respaldo no generó el ZIP (código {codigo}). No se instala nada.");

            List<string> entradas;
            using (var z = ZipFile.OpenRead(zip.FullName)) entradas = z.Entries.Select(e => e.FullName).ToList();
            if (!Rescate.TieneDatos(entradas)) return ResultadoPaso.Fallo("El ZIP del respaldo no tiene lotes ni vehículos. No se instala nada.");

            ctx.Estado.ZipRescate = zip.FullName;
            ctx.Estado.LotesRespaldo = Rescate.ContarLotes(entradas);
            return ResultadoPaso.Ok($"{ctx.Estado.LotesRespaldo} lotes respaldados en {zip.FullName}");
        }
    }

    // 3. Código de vinculación — lo aprueba Agro Parallel en OrbitX.
    internal class PasoVinculacion : PasoBase
    {
        static readonly TimeSpan Vida = TimeSpan.FromMinutes(9); // OrbitX: 10 min
        public PasoVinculacion(OrbitXCliente ox) : base(ox) { }
        public override string Id => "vinculacion";
        public override string Titulo => "Aprobación en OrbitX";
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var e = ctx.Estado;
            e.DeviceId = e.DeviceId ?? IdEquipo.Calcular(Equipo.Macs());
            if (e.DeviceId == null) return ResultadoPaso.Fallo("No se encontró ninguna placa de red con MAC.");
            Ox.DeviceId = e.DeviceId;

            using (var rng = RandomNumberGenerator.Create())
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool vencido = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.PairDesde > Vida.TotalMilliseconds;
                if (e.PairCode == null || e.PairSecret == null || vencido)
                {
                    e.PairCode = CodigoVinculacion.NuevoCodigo(rng);
                    e.PairSecret = CodigoVinculacion.NuevoSecreto(rng);
                    e.PairDesde = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    e.Guardar();
                    await Ox.PairInitAsync(new PairInit
                    {
                        code = e.PairCode, device_secret = e.PairSecret, device_id = e.DeviceId,
                        hostname = Equipo.Hostname, version = "instalador", origen = "instalador",
                        resumen = await Resumen(e, ct),
                    }, ct);
                }
                ctx.Reporte.MostrarCodigo(e.PairCode);

                var st = await Ox.PairStatusAsync(e.PairCode, e.PairSecret, ct);
                if (st.status == "claimed" && !string.IsNullOrEmpty(st.token))
                {
                    e.Token = st.token;
                    Ox.Token = st.token;
                    e.PairCode = null; e.PairSecret = null;
                    ctx.Reporte.MostrarCodigo(null);
                    await Avisar(ctx, $"aprobada por Agro Parallel: {st.estab_slug}", "instalando");
                    return ResultadoPaso.Ok("Aprobada para " + st.estab_slug);
                }
                if (st.status == "expired") e.PairDesde = 0; // fuerza código nuevo
                await Task.Delay(3000, ct);
            }
        }

        static async Task<ResumenEquipo> Resumen(Estado e, CancellationToken ct)
        {
            List<Core.Red.Adaptador> ads;
            try { ads = (await RedWindows.MedirAsync(ct)).ads; } catch { ads = new List<Core.Red.Adaptador>(); }
            return new ResumenEquipo
            {
                hostname = Equipo.Hostname, windows = Equipo.VersionWindows(),
                instalacion_anterior = e.ZipRescate != null, lotes = e.LotesRespaldo,
                adaptadores = ads.Select(a => new ResumenAdaptador { nombre = a.nombre, tipo = a.tipo, ip = a.ip, internet = a.internet_ip }).ToList(),
            };
        }
    }

    // 4. Perfil — cliente, versión, kiosko y claves (una sola vez).
    internal class PasoPerfil : PasoBase
    {
        public PasoPerfil(OrbitXCliente ox) : base(ox) { }
        public override string Id => "perfil";
        public override string Titulo => "Configuración desde la nube";
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var nuevo = await Ox.PerfilAsync(ct);
            var previo = ctx.Estado.Perfil;
            if (string.IsNullOrEmpty(nuevo.soporte_pass))
            {
                if (previo == null || string.IsNullOrEmpty(previo.soporte_pass))
                    return ResultadoPaso.Fallo("OrbitX ya entregó las claves de esta instalación y acá no quedaron. Volvé a aprobar la pantalla en OrbitX.");
                nuevo.soporte_pass = previo.soporte_pass;
                nuevo.rustdesk_pass = previo.rustdesk_pass;
            }
            ctx.Estado.Perfil = nuevo;
            await Avisar(ctx, $"perfil: {nuevo.cliente}, PilotX {nuevo.version}, kiosko {(nuevo.kiosko ? "sí" : "no")}");
            return ResultadoPaso.Ok($"{nuevo.cliente} — PilotX {nuevo.version}");
        }
    }

    // 5. PilotX — baja del OTA, verifica sha256, extrae y escribe orbitX.json.
    internal class PasoPilotX : PasoBase
    {
        public PasoPilotX(OrbitXCliente ox) : base(ox) { }
        public override string Id => "pilotx";
        public override string Titulo => "Instalación de PilotX";
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var p = ctx.Estado.Perfil;
            Directory.CreateDirectory(Kit.Dir);
            var zip = Path.Combine(Kit.Dir, $"PilotX_v{p.version}.zip");
            if (!File.Exists(zip) || Hash.Sha256Archivo(zip) != p.sha256)
            {
                await Avisar(ctx, $"bajando PilotX {p.version} ({p.tamano_bytes / 1048576} MB)");
                int ultimo = -1;
                await Ox.DescargarAsync("PilotX", p.version, zip, new Progress<long>(b =>
                {
                    int pct = p.tamano_bytes > 0 ? (int)(b * 100 / p.tamano_bytes) : 0;
                    if (pct / 10 != ultimo) { ultimo = pct / 10; ctx.Reporte.Linea($"  {pct}%"); }
                }), ct);
                if (Hash.Sha256Archivo(zip) != p.sha256)
                {
                    File.Delete(zip);
                    return ResultadoPaso.Fallo("El paquete bajado no coincide con el de OrbitX (sha256). Reintentá.");
                }
            }

            foreach (var pr in Process.GetProcesses().Where(x => x.ProcessName.StartsWith("PilotX", StringComparison.OrdinalIgnoreCase)
                     || x.ProcessName.StartsWith("AgroParallel", StringComparison.OrdinalIgnoreCase) || x.ProcessName == "msedgewebview2"))
                try { if (pr.Id != Process.GetCurrentProcess().Id) pr.Kill(); } catch { }

            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    var dst = Path.Combine(Pasos.Destino, e.FullName.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    e.ExtractToFile(dst, true);
                }

            var orbit = $"{{\n  \"enabled\": true,\n  \"server_url\": {Json.Serializar("https://orbitx.agroparallel.com")},\n  \"device_token\": {Json.Serializar(ctx.Estado.Token)},\n  \"master_token\": \"vx-device-token\",\n  \"device_id\": {Json.Serializar(ctx.Estado.DeviceId)},\n  \"estab_slug\": {Json.Serializar(p.estab_slug)}\n}}\n";
            File.WriteAllText(Path.Combine(Pasos.Destino, "Engine", "orbitX.json"), orbit, new System.Text.UTF8Encoding(false));
            await Avisar(ctx, $"PilotX {p.version} instalado en {Pasos.Destino}");
            return ResultadoPaso.Ok();
        }
    }

    // 6. Equipo — usuarios, firewall, energía, marca, runtimes, WinRM (script probado).
    internal class PasoEquipo : PasoBase
    {
        public PasoEquipo(OrbitXCliente ox) : base(ox) { }
        public override string Id => "equipo";
        public override string Titulo => "Preparación del equipo";
        public override bool Critico => false; // lo crítico (usuarios, firewall) lo verifica el self-test
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var p = ctx.Estado.Perfil;
            var script = Kit.Extraer("Provision-Pantalla.ps1");
            var brand = Path.Combine(Kit.Dir, "Branding");
            Directory.CreateDirectory(brand);
            foreach (var f in new[] { "logo.png", "logo-fondo-blanco.png" })
            {
                var src = Path.Combine(Pasos.Destino, "Branding", f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(brand, f), true);
            }
            var fondo = Path.Combine(Pasos.Destino, @"AgroParallel\wwwroot\img\fondo.png");
            if (File.Exists(fondo)) File.Copy(fondo, Path.Combine(brand, "fondo.png"), true);

            using (var http = new HttpClient())
            {
                await Bajar(http, "https://aka.ms/vs/17/release/vc_redist.x64.exe", Path.Combine(Kit.Dir, "vc_redist.x64.exe"), ct);
                await Bajar(http, "https://go.microsoft.com/fwlink/p/?LinkId=2124703", Path.Combine(Kit.Dir, "MicrosoftEdgeWebview2Setup.exe"), ct);
            }

            var args = $"-Cliente {Pasos.Cita(p.cliente)} -Cuit {Pasos.Cita(p.cuit)} -SoportePass {Pasos.Cita(p.soporte_pass)} -SinKiosko";
            // La clave nunca va al log ni a la pantalla.
            int codigo = await PowerShell.CorrerScriptAsync(script, args,
                l => { if (string.IsNullOrEmpty(p.soporte_pass) || !l.Contains(p.soporte_pass)) ctx.Reporte.Linea(l); }, ct);

            var lan = Path.Combine(Pasos.Destino, "setup_pilotx_lan.bat");
            if (File.Exists(lan)) await PowerShell.CorrerExeAsync("cmd.exe", "/c " + PowerShell.Arg(lan), ctx.Reporte.Linea, ct);

            await Avisar(ctx, $"equipo preparado (código {codigo})");
            return codigo == 0 ? ResultadoPaso.Ok() : ResultadoPaso.Fallo($"Provision-Pantalla terminó con código {codigo}");
        }

        static async Task Bajar(HttpClient http, string url, string destino, CancellationToken ct)
        {
            if (File.Exists(destino)) return;
            try
            {
                using (var r = await http.GetAsync(url, ct)) { r.EnsureSuccessStatusCode(); File.WriteAllBytes(destino, await r.Content.ReadAsByteArrayAsync()); }
            }
            catch { /* Provision-Pantalla avisa si falta el runtime */ }
        }
    }

    // 7. RustDesk — soporte remoto contra el servidor de Agro Parallel.
    internal class PasoRustDesk : PasoBase
    {
        public PasoRustDesk(OrbitXCliente ox) : base(ox) { }
        public override string Id => "rustdesk";
        public override string Titulo => "Soporte remoto";
        public override bool Critico => false;
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var p = ctx.Estado.Perfil;
            if (p.rustdesk == null) return ResultadoPaso.Saltado("OrbitX no tiene RustDesk configurado.");
            var exe = Path.Combine(Kit.Dir, "rustdesk-instalador.exe");
            if (!File.Exists(exe) || Hash.Sha256Archivo(exe) != p.rustdesk.sha256)
                await Ox.DescargarAsync("RustDesk", p.rustdesk.version, exe, null, ct);
            if (Hash.Sha256Archivo(exe) != p.rustdesk.sha256) return ResultadoPaso.Fallo("El instalador de RustDesk no coincide (sha256).");

            var script = Kit.Extraer("RustDesk-Configurar.ps1");
            string ultima = null;
            int codigo = await PowerShell.CorrerScriptAsync(script,
                $"-Instalador {Pasos.Cita(exe)} -Servidor {Pasos.Cita(p.rustdesk.host)} -Clave {Pasos.Cita(p.rustdesk.key)} -Password {Pasos.Cita(p.rustdesk_pass)}",
                l => { if (!string.IsNullOrWhiteSpace(l)) ultima = l.Trim(); }, ct);
            if (codigo != 0 || string.IsNullOrEmpty(ultima) || !ultima.All(char.IsDigit))
                return ResultadoPaso.Fallo("RustDesk no devolvió su ID: " + ultima);
            ctx.Estado.RustdeskId = ultima;
            await Avisar(ctx, "RustDesk ID " + ultima);
            return ResultadoPaso.Ok("ID " + ultima);
        }
    }

    // 8. Red persistente — tarea SYSTEM que corre "--red --auto" al arrancar y
    // cada vez que Windows cambia de red.
    internal class PasoRedPersistente : PasoBase
    {
        public PasoRedPersistente(OrbitXCliente ox) : base(ox) { }
        public override string Id => "red-persistente";
        public override string Titulo => "Red: revisión automática";
        public override bool Critico => false;
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var exe = Path.Combine(Pasos.Destino, "PilotX-Instalador.exe");
            var yo = Process.GetCurrentProcess().MainModule.FileName;
            if (!string.Equals(yo, exe, StringComparison.OrdinalIgnoreCase)) File.Copy(yo, exe, true);
            var xml = TareaXml(exe);
            var tmp = Path.Combine(Path.GetTempPath(), "PilotXRed.xml");
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            int codigo = await PowerShell.CorrerExeAsync("schtasks.exe", "/Create /TN PilotXRed /XML " + PowerShell.Arg(tmp) + " /F", ctx.Reporte.Linea, ct);
            File.Delete(tmp);
            return codigo == 0 ? ResultadoPaso.Ok("Tarea PilotXRed registrada") : ResultadoPaso.Fallo($"schtasks terminó con código {codigo}");
        }

        static string TareaXml(string exe) => $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>PilotX: revisa y repara la salida a internet (Ethernet USB + WiFi)</Description></RegistrationInfo>
  <Triggers>
    <BootTrigger><Delay>PT30S</Delay></BootTrigger>
    <EventTrigger>
      <Subscription>&lt;QueryList&gt;&lt;Query Id=""0"" Path=""Microsoft-Windows-NetworkProfile/Operational""&gt;&lt;Select Path=""Microsoft-Windows-NetworkProfile/Operational""&gt;*[System[(EventID=10000)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
      <Delay>PT30S</Delay>
    </EventTrigger>
  </Triggers>
  <Principals><Principal id=""Author""><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{System.Security.SecurityElement.Escape(exe)}</Command><Arguments>--red --auto</Arguments></Exec></Actions>
</Task>";
    }

    // 9. Devolución de lotes y vehículos (script probado).
    internal class PasoDevolucion : PasoBase
    {
        public PasoDevolucion(OrbitXCliente ox) : base(ox) { }
        public override string Id => "devolucion";
        public override string Titulo => "Devolución de lotes y vehículos";
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var zip = ctx.Estado.ZipRescate;
            if (string.IsNullOrEmpty(zip)) return ResultadoPaso.Ok("No había lotes para devolver.");
            var script = Kit.Extraer("Restaurar-AOG.ps1");
            int codigo = await PowerShell.CorrerScriptAsync(script, $"-Zip {Pasos.Cita(zip)} -Instalacion {Pasos.Cita(Pasos.Destino)}", ctx.Reporte.Linea, ct);
            if (codigo != 0) return ResultadoPaso.Fallo($"La devolución terminó con código {codigo}. El respaldo sigue en {zip}.");
            await Avisar(ctx, $"{ctx.Estado.LotesRespaldo} lotes devueltos");
            return ResultadoPaso.Ok();
        }
    }

    // 10. Self-test — si falla, no hay kiosko.
    internal class PasoSelfTest : PasoBase
    {
        public PasoSelfTest(OrbitXCliente ox) : base(ox) { }
        public override string Id => "selftest";
        public override string Titulo => "Pruebas";
        public override bool SiempreCorre => true;
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            var r = await SelfTest.CorrerAsync(SondasWindows.Todas(Ox, ctx.Estado), ct);
            foreach (var c in r) ctx.Reporte.Linea($"{(c.ok ? "✔" : "✘")} {c.nombre}{(string.IsNullOrEmpty(c.detalle) ? "" : " — " + c.detalle)}");
            var fallas = r.Where(c => !c.ok).Select(c => c.nombre).ToList();
            await Avisar(ctx, "pruebas: " + (fallas.Count == 0 ? "todo OK" : "fallan " + string.Join(", ", fallas)), fallas.Count == 0 ? null : "fallo");
            return fallas.Count == 0 ? ResultadoPaso.Ok("Todo OK") : ResultadoPaso.Fallo("Fallan: " + string.Join(", ", fallas));
        }
    }

    // 11. Kiosko — último, solo si todo lo anterior pasó.
    internal class PasoKiosko : PasoBase
    {
        public PasoKiosko(OrbitXCliente ox) : base(ox) { }
        public override string Id => "kiosko";
        public override string Titulo => "Modo kiosko";
        public override async Task<ResultadoPaso> EjecutarAsync(Contexto ctx, CancellationToken ct)
        {
            if (!ctx.Estado.Perfil.kiosko) { await Avisar(ctx, "instalación terminada (sin kiosko)", "instalado"); return ResultadoPaso.Saltado("Aprobada sin kiosko."); }
            var k = Path.Combine(Pasos.Destino, "PilotX-KioskSetup.exe");
            if (!File.Exists(k)) return ResultadoPaso.Fallo("Falta PilotX-KioskSetup.exe en el paquete.");
            await PowerShell.CorrerExeAsync(k, "/yes", ctx.Reporte.Linea, ct);
            using (var w = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
            {
                var shell = w?.GetValue("Shell") as string ?? "";
                if (shell.IndexOf("PilotX", StringComparison.OrdinalIgnoreCase) < 0) return ResultadoPaso.Fallo("El kiosko no quedó configurado (Shell = " + shell + ").");
            }
            await Avisar(ctx, "instalación terminada: falta reiniciar", "instalado");
            return ResultadoPaso.Ok();
        }
    }
}
```

- [ ] **Step 4: `Windows/SondasWindows.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using PilotX.Instalador.Core;
using PilotX.Instalador.Core.Red;

namespace PilotX.Instalador.Windows
{
    internal class Sonda : ISonda
    {
        public string Nombre { get; }
        readonly Func<CancellationToken, Task<(bool ok, string detalle)>> _f;
        public Sonda(string nombre, Func<CancellationToken, Task<(bool, string)>> f) { Nombre = nombre; _f = f; }
        public async Task<Chequeo> ProbarAsync(CancellationToken ct)
        {
            var (ok, det) = await _f(ct).ConfigureAwait(false);
            return new Chequeo { nombre = Nombre, ok = ok, detalle = det };
        }
    }

    internal static class SondasWindows
    {
        public static IEnumerable<ISonda> Todas(OrbitXCliente ox, Estado e) => new ISonda[]
        {
            new Sonda("PilotX arranca y el broker escucha", PilotXArranca),
            new Sonda("Firewall", async ct => {
                var (c, s) = await PowerShell.CorrerComandoAsync("$r=Get-NetFirewallRule -DisplayName 'PilotX*' -ErrorAction SilentlyContinue; \"$(@($r|?{$_.Action -eq 'Allow'}).Count) $(@($r|?{$_.Action -eq 'Block'}).Count)\"", null, ct);
                var p = s.Trim().Split(' ');
                bool ok = p.Length == 2 && int.TryParse(p[0], out var a) && a > 0 && p[1] == "0";
                return (ok, $"permitidas {p.ElementAtOrDefault(0)}, bloqueos {p.ElementAtOrDefault(1)}");
            }),
            new Sonda("Usuarios pilotx y soporte", async ct => {
                var (c, s) = await PowerShell.CorrerComandoAsync("$a=Get-LocalUser pilotx -EA 0; $b=Get-LocalUser soporte -EA 0; $adm=Get-LocalGroupMember -SID S-1-5-32-544 -EA 0 | ?{$_.Name -like '*\\soporte'}; \"$([bool]$a) $([bool]$b) $([bool]$adm)\"", null, ct);
                return (s.Trim() == "True True True", s.Trim());
            }),
            new Sonda("Lotes devueltos", ct => Task.FromResult(LotesDevueltos(e))),
            new Sonda("OrbitX", async ct => {
                var r = await ox.HeartbeatAsync(new Heartbeat { hostname = Equipo.Hostname, version = e.Perfil?.version, rustdesk_id = e.RustdeskId }, ct);
                return (r.asignado, r.asignado ? "asignada a " + r.estab_slug : "la pantalla no figura asignada");
            }),
            new Sonda("Internet por el adaptador correcto", async ct => {
                var (ads, ruta) = await RedWindows.MedirAsync(ct);
                var d = Diagnosticador.Diagnosticar(ads, ruta);
                return (d.Codigo == CodigoRed.Ok, d.Mensaje);
            }),
            new Sonda("Soporte remoto", ct => {
                if (e.Perfil?.rustdesk == null) return Task.FromResult((true, "no configurado en OrbitX"));
                try { using (var s = new ServiceController("RustDesk")) return Task.FromResult((s.Status == ServiceControllerStatus.Running, "ID " + e.RustdeskId)); }
                catch (Exception ex) { return Task.FromResult((false, ex.Message)); }
            }),
        };

        static async Task<(bool, string)> PilotXArranca(CancellationToken ct)
        {
            var eng = Directory.GetFiles(@"C:\PilotX\Engine", "PilotX.GuidanceEngine.exe").FirstOrDefault();
            if (eng == null) return (false, "no está PilotX.GuidanceEngine.exe");
            var pr = Process.Start(new ProcessStartInfo(eng, "--webhost --corex") { WorkingDirectory = @"C:\PilotX\Engine", UseShellExecute = false, CreateNoWindow = true });
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                    for (int i = 0; i < 30; i++)
                    {
                        await Task.Delay(2000, ct);
                        try
                        {
                            (await http.GetAsync("http://127.0.0.1:5180/api/aog/state", ct)).EnsureSuccessStatusCode();
                            using (var tcp = new TcpClient())
                            {
                                var t = tcp.ConnectAsync("127.0.0.1", 1883);
                                bool broker = await Task.WhenAny(t, Task.Delay(3000, ct)) == t && tcp.Connected;
                                return (broker, broker ? "responde en 5180 y 1883" : "responde en 5180 pero el broker no escucha en 1883");
                            }
                        }
                        catch (HttpRequestException) { }
                        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                    }
                return (false, "no respondió en 60 s");
            }
            finally { try { if (!pr.HasExited) pr.Kill(); } catch { } }
        }

        static (bool, string) LotesDevueltos(Estado e)
        {
            if (string.IsNullOrEmpty(e.ZipRescate)) return (true, "no había lotes");
            var candidatos = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS", "Fields"),
                @"C:\Users\pilotx\Documents\AgOpenGPS\Fields",
            };
            int hay = candidatos.Where(Directory.Exists).Select(d => Directory.GetDirectories(d).Length).DefaultIfEmpty(0).Max();
            return (hay >= e.LotesRespaldo, $"{hay} de {e.LotesRespaldo}");
        }
    }
}
```

Agregar `<Reference Include="System.ServiceProcess" />` al `ItemGroup` de referencias de `PilotX.Instalador.csproj`.

- [ ] **Step 5: Compilar**

Run: `dotnet build SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj -c Release`
Expected: `Build succeeded`. (El nombre `PilotX.GuidanceEngine.exe` sale de `CLAUDE.md` — verificar que exista en `Build\Engine\`; si el binario se llama distinto, corregir la sonda.)

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador/Pasos SourceCode/AgroParallel/Tools/PilotX.Instalador/Kit SourceCode/AgroParallel/Tools/PilotX.Instalador/Windows/SondasWindows.cs SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj
git commit -m "feat(instalador): pasos de instalación, RustDesk y self-test"
```

---

### Task 10: UI (`Tema` + `MainForm`)

**Files:**
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Tema.cs`
- Create: `SourceCode/AgroParallel/Tools/PilotX.Instalador/MainForm.cs`

**Interfaces:**
- Consumes: `Motor`, `IReportero`, `Contexto`, `Estado`, `Pasos.Todos`, `RedWindows.RevisarYRepararAsync`.
- Produces: `MainForm(bool soloRed)`; implementa `IReportero` (thread-safe con `BeginInvoke`).

- [ ] **Step 1: `Tema.cs`**

```csharp
using System.Drawing;

namespace PilotX.Instalador
{
    // Un solo lugar para colores y fuentes (skill pilotx-ui-winforms).
    internal static class Tema
    {
        public static readonly Color Fondo = ColorTranslator.FromHtml("#F5F7F4");
        public static readonly Color Superficie = Color.White;
        public static readonly Color Acento = ColorTranslator.FromHtml("#4BA63F");
        public static readonly Color Texto = ColorTranslator.FromHtml("#2B2B2B");
        public static readonly Color Gris = ColorTranslator.FromHtml("#8A8F87");
        public static readonly Color Ok = ColorTranslator.FromHtml("#4BA63F");
        public static readonly Color Aviso = ColorTranslator.FromHtml("#D9A21B");
        public static readonly Color Error = ColorTranslator.FromHtml("#C8423B");
        public static readonly Font Titulo = new Font("Segoe UI Semibold", 20f);
        public static readonly Font Normal = new Font("Segoe UI", 12f);
        public static readonly Font Codigo = new Font("Consolas", 72f, FontStyle.Bold);
        public static readonly Font Log = new Font("Consolas", 10f);
        public const int AltoBoton = 56;
    }
}
```

- [ ] **Step 2: `MainForm.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PilotX.Instalador.Core;
using PilotX.Instalador.Windows;

namespace PilotX.Instalador
{
    internal class MainForm : Form, IReportero
    {
        public const string RutaEstado = @"C:\PilotX\instalador-estado.json";
        public const string RutaLog = @"C:\PilotX\instalador-log.txt";
        const string Servidor = "https://orbitx.agroparallel.com";

        readonly bool _soloRed;
        readonly FlowLayoutPanel _pasos = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 360, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Tema.Superficie, Padding = new Padding(12) };
        readonly Dictionary<string, Label> _etiquetas = new Dictionary<string, Label>();
        readonly Label _codigo = new Label { Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter, Font = Tema.Codigo, ForeColor = Tema.Acento };
        readonly Label _ayudaCodigo = new Label { Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter, Font = Tema.Normal, ForeColor = Tema.Texto,
            Text = "Pasale este código a Agro Parallel para que apruebe la pantalla en OrbitX." };
        readonly TextBox _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Tema.Log, BackColor = Tema.Superficie, BorderStyle = BorderStyle.None };
        readonly Button _btnRed = Boton("Revisar red");
        readonly Button _btnReintentar = Boton("Reintentar");
        readonly Button _btnReiniciar = Boton("Reiniciar la pantalla");
        CancellationTokenSource _cts;

        public MainForm(bool soloRed)
        {
            _soloRed = soloRed;
            Text = "PilotX — Instalador";
            WindowState = FormWindowState.Maximized;
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            Font = Tema.Normal;

            var titulo = new Label { Dock = DockStyle.Top, Height = 64, Text = soloRed ? "PilotX — Red e internet" : "PilotX — Instalación", Font = Tema.Titulo, Padding = new Padding(16, 12, 0, 0) };
            var botones = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Tema.AltoBoton + 24, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
            botones.Controls.AddRange(new Control[] { _btnReiniciar, _btnReintentar, _btnRed });
            _btnReiniciar.Visible = false; _btnReintentar.Visible = false;
            _btnRed.Click += async (s, e) => await RevisarRed();
            _btnReintentar.Click += async (s, e) => await Instalar();
            _btnReiniciar.Click += (s, e) => Process.Start("shutdown.exe", "/r /t 5");

            var derecha = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            derecha.Controls.Add(_log);
            derecha.Controls.Add(_ayudaCodigo);
            derecha.Controls.Add(_codigo);

            Controls.Add(derecha);
            Controls.Add(_pasos);
            Controls.Add(botones);
            Controls.Add(titulo);
            Shown += async (s, e) => { if (_soloRed) await RevisarRed(); else await Instalar(); };
        }

        static Button Boton(string t) => new Button { Text = t, Height = Tema.AltoBoton, Width = 260, FlatStyle = FlatStyle.Flat, BackColor = Tema.Acento, ForeColor = Color.White, Font = Tema.Normal, Margin = new Padding(8, 0, 0, 0) };

        async Task Instalar()
        {
            _btnReintentar.Visible = false;
            _cts = new CancellationTokenSource();
            var estado = Estado.Cargar(RutaEstado, new ProtectorDpapi());
            var ox = new OrbitXCliente(Servidor) { DeviceId = estado.DeviceId, Token = estado.Token };
            var pasos = Pasos.Pasos.Todos(ox);
            _pasos.Controls.Clear(); _etiquetas.Clear();
            foreach (var p in pasos)
            {
                var l = new Label { Text = "○  " + p.Titulo, AutoSize = false, Width = 330, Height = 40, ForeColor = Tema.Gris, Font = Tema.Normal };
                _etiquetas[p.Id] = l; _pasos.Controls.Add(l);
            }
            var ctx = new Contexto(estado, this);
            bool ok;
            try { ok = await Task.Run(() => new Motor().CorrerAsync(pasos, ctx, _cts.Token)); }
            catch (OperationCanceledException) { ok = false; }
            if (ok)
            {
                Linea("✔ Instalación terminada. Reiniciá la pantalla.");
                _btnReiniciar.Visible = true;
            }
            else
            {
                Linea("✘ Quedó sin terminar:");
                foreach (var p in ctx.Problemas) Linea("   · " + p);
                Linea("Corregí lo que falta y tocá Reintentar: sigue desde donde quedó.");
                _btnReintentar.Visible = true;
            }
        }

        async Task RevisarRed()
        {
            _btnRed.Enabled = false;
            try
            {
                var (antes, despues, informe) = await Task.Run(() => RedWindows.RevisarYRepararAsync(Linea, CancellationToken.None));
                Linea(despues.Codigo == Core.Red.CodigoRed.Ok ? "✔ " + despues.Mensaje : "✘ " + despues.Mensaje);
                var estado = Estado.Cargar(RutaEstado, new ProtectorDpapi());
                if (!string.IsNullOrEmpty(estado.Token))
                    try { await new OrbitXCliente(Servidor) { DeviceId = estado.DeviceId, Token = estado.Token }.RedAsync(Json.Serializar(informe), CancellationToken.None); } catch { }
            }
            finally { _btnRed.Enabled = true; }
        }

        // ── IReportero (llamado desde otro hilo) ──
        void EnUi(Action a) { if (IsHandleCreated) BeginInvoke(a); }

        public void Paso(string id, string titulo, int nro, int total) => EnUi(() =>
        {
            if (_etiquetas.TryGetValue(id, out var l)) { l.Text = "●  " + titulo; l.ForeColor = Tema.Acento; }
            Escribir($"── {nro}/{total} {titulo}");
        });

        public void Linea(string texto) => EnUi(() => Escribir(texto));

        public void Resultado(string id, ResultadoPaso r) => EnUi(() =>
        {
            if (!_etiquetas.TryGetValue(id, out var l)) return;
            var t = l.Text.Substring(3);
            switch (r.Estado)
            {
                case Core.Resultado.Ok: l.Text = "✔  " + t; l.ForeColor = Tema.Ok; break;
                case Core.Resultado.Saltado: l.Text = "–  " + t; l.ForeColor = Tema.Gris; break;
                default: l.Text = "✘  " + t; l.ForeColor = Tema.Error; break;
            }
            if (!string.IsNullOrEmpty(r.Mensaje)) Escribir("   " + r.Mensaje);
        });

        public void MostrarCodigo(string codigo) => EnUi(() =>
        {
            _codigo.Text = codigo ?? "";
            _codigo.Height = codigo == null ? 0 : 140;
            _ayudaCodigo.Height = codigo == null ? 0 : 40;
        });

        void Escribir(string t)
        {
            var linea = $"{DateTime.Now:HH:mm:ss}  {t}";
            _log.AppendText(linea + Environment.NewLine);
            try { Directory.CreateDirectory(Path.GetDirectoryName(RutaLog)); File.AppendAllText(RutaLog, linea + Environment.NewLine); } catch { }
        }
    }
}
```

- [ ] **Step 3: Compilar** — Run: `dotnet build SourceCode/AgroParallel/Tools/PilotX.Instalador/PilotX.Instalador.csproj -c Release` → `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador/Tema.cs SourceCode/AgroParallel/Tools/PilotX.Instalador/MainForm.cs
git commit -m "feat(instalador): pantalla táctil con avance, código de vinculación y revisión de red"
```

---

### Task 11: `Program.cs` (modos) + build

**Files:**
- Modify: `SourceCode/AgroParallel/Tools/PilotX.Instalador/Program.cs`
- Modify: `build.ps1` (compilar y copiar a `Build\`, junto al bloque del Updater L39 y L173-178)

**Interfaces:**
- Produces: `PilotX-Instalador.exe` (UI instalación), `--red` (UI de red), `--red --auto` (sin UI: repara y reporta; exit 0 OK / 1 sin internet).

- [ ] **Step 1: `Program.cs`**

```csharp
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using PilotX.Instalador.Core;
using PilotX.Instalador.Windows;

namespace PilotX.Instalador
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            bool red = args.Contains("--red"), auto = args.Contains("--auto");

            if (red && auto) return RedAuto();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(soloRed: red));
            return 0;
        }

        // Lo corre la tarea SYSTEM PilotXRed al arrancar y al cambiar de red.
        static int RedAuto()
        {
            try
            {
                var (antes, despues, informe) = RedWindows.RevisarYRepararAsync(l => Log(l), CancellationToken.None).GetAwaiter().GetResult();
                var e = Estado.Cargar(MainForm.RutaEstado, new ProtectorDpapi());
                if (!string.IsNullOrEmpty(e.Token) && antes.Acciones.Count > 0)
                    try { new OrbitXCliente("https://orbitx.agroparallel.com") { DeviceId = e.DeviceId, Token = e.Token }.RedAsync(Json.Serializar(informe), CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                return despues.Codigo == Core.Red.CodigoRed.SinInternet ? 1 : 0;
            }
            catch (Exception ex) { Log("ERROR " + ex.Message); return 1; }
        }

        static void Log(string t)
        {
            try { System.IO.File.AppendAllText(@"C:\PilotX\red-auto.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {t}{Environment.NewLine}"); } catch { }
        }
    }
}
```

- [ ] **Step 2: `build.ps1`**

Después de la línea del build del Updater (L39) agregar:

```powershell
dotnet build "$root\SourceCode\AgroParallel\Tools\PilotX.Instalador\PilotX.Instalador.csproj" -c $Config -v q $verArg
if ($LASTEXITCODE -ne 0) { throw "Falló el build de PilotX-Instalador" }
```

y después del bloque que copia el Updater a `Build\` (L173-178):

```powershell
$instBin = "$root\SourceCode\AgroParallel\Tools\PilotX.Instalador\bin\$Config\net48"
Copy-Item "$instBin\PilotX-Instalador.exe" -Destination $OutDir -Force
```

- [ ] **Step 3: Build completo y tests**

Run: `powershell -ExecutionPolicy Bypass -File build.ps1 -Config Release` (o el mismo comando que se usa hoy para armar el paquete)
Expected: termina OK y existe `Build\PilotX-Instalador.exe`.
Run: `dotnet test SourceCode/AgroParallel/Tools/PilotX.Instalador.Tests -c Release` → `Passed: 38`.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Tools/PilotX.Instalador/Program.cs build.ps1
git commit -m "feat(instalador): modos --red y --red --auto, build en build.ps1"
```

---

### Task 12: Prueba manual de red (sin OrbitX)

**Files:** ninguno.

- [ ] **Step 1:** En una tablet de prueba con Ethernet USB (IP fija `192.168.5.10/24` **con** gateway `192.168.5.1`) y WiFi con internet, correr `PilotX-Instalador.exe --red`.
Expected: diagnóstico "El cable USB se está llevando el tráfico de internet y no tiene salida.", aplica las acciones y termina en "✔ Internet sale por "Wi-Fi"."; `route print 0.0.0.0` muestra la ruta por el WiFi.
- [ ] **Step 2:** Volver a correr `--red`. Expected: "✔ Internet sale por …" sin acciones.
- [ ] **Step 3:** Con el instalador completo (Task 13) instalado, desconectar y reconectar el WiFi. Expected: a los ~30 s aparece una línea nueva en `C:\PilotX\red-auto.log`.

---

### Task 13: Prueba integrada de punta a punta (requiere Parte A desplegada)

**Files:** ninguno.

- [ ] **Step 1: Publicar** el exe en OrbitX: panel `/firmwares` → producto `PilotXInstalador`, versión = la de `Installer\VERSION`, archivo `Build\PilotX-Instalador.exe`. Verificar `curl -sI https://orbitx.agroparallel.com/instalador` → `200`.
- [ ] **Step 2: Pantalla nueva** (Windows limpio): bajar de `/instalador`, ejecutar. Expected: chequeo y red OK, "No hay instalación anterior", aparece el código. En OrbitX → Dispositivos → "Pantallas esperando aprobación": aparece con hostname/red → Aprobar (org de prueba, última versión, kiosko). Expected: la pantalla sigue sola hasta "Instalación terminada", con todos los chequeos ✔; en OrbitX el avance aparece en "Instalaciones recientes".
- [ ] **Step 3: Pantalla con instalación anterior** (con lotes): mismo flujo. Expected: "N lotes respaldados", y en Pruebas "Lotes devueltos N de N".
- [ ] **Step 4: Corte a mitad:** desenchufar internet durante "Instalación de PilotX", reconectar, tocar Reintentar. Expected: retoma desde ese paso, sin pedir código.
- [ ] **Step 5: Owner intentando aprobar** el código con su cuenta en "Vincular por código": Expected `403 Las pantallas del instalador las aprueba solo Agro Parallel.`
- [ ] **Step 6:** Registrar el resultado de cada caso. Lo que no se pudo probar en cabina queda como "en prueba", no como cerrado.
