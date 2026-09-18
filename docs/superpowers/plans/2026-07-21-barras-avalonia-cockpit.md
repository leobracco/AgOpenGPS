# Barras/overlays del cockpit en Avalonia — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reemplazar las 4 barras WebView2 del cockpit WinForms por barras nativas Avalonia, con la UI de barras en una librería reutilizable que PilotX.Desktop pueda consumir después.

**Architecture:** Librería Avalonia `PilotX.Cockpit.Bars` (UserControls + ViewModels + clients HTTP + theme) = el activo reutilizable. Exe fino `PilotX.Bars.Host` = cáscara que muestra las 4 barras como ventanas borderless ancladas a los bordes de la pantalla, encima de FormGPS fullscreen. El cockpit WinForms (net48) lanza el Host como proceso aparte. Toda la comunicación es HTTP contra el AgpWebHost existente (`http://127.0.0.1:5180`).

**Tech Stack:** Avalonia 11.2.3, CommunityToolkit.Mvvm 8.4.0, .NET 9 (`net9.0-windows`), NUnit 4, System.Text.Json.

## Global Constraints

- **Frameworks:** librería y Host son `net9.0-windows`. La app WinForms (`AgOpenGPS.csproj`, `AssemblyName=PilotX`) es **net48** y **no se modifica su target** — solo se le agrega un método de launch. No se puede cargar Avalonia net9 en el proceso net48; por eso el Host es proceso separado.
- **Comunicación:** solo HTTP contra `http://127.0.0.1:5180`, dos endpoints y nada más (verificado por auditoría de las 4 barras HTML). Lectura de estado: `GET /api/aog/state`. **Todas** las acciones de botón: `POST /api/aog/guidance/command` con body `{"cmd":"<verbo>"}` → `FormGPS.ExecuteGuidanceCommand` dispara el botón WinForms nativo. No se crea ningún endpoint nuevo. El único mecanismo no-HTTP de hoy (`postMessage 'resize:WxH'` para redimensionar el widget WebView2) **desaparece** en Avalonia (layout nativo) — no se porta.
- **JSON:** `/api/aog/state` se serializa en **snake_case** vía `AgpJson`/`AgpControllerBase` (ej. `avg_speed`, `is_job_started`, `fix_quality`). ⚠️ `PropertyNameCaseInsensitive` NO cubre underscores — los `[JsonPropertyName(...)]` deben ser el nombre snake_case EXACTO. (Ver memoria `project_embedio_serializer_casing`.)
- **Ventanas del Host:** `SystemDecorations=None`, `ShowInTaskbar=false`, `Topmost=true`, `ShowActivated=false`, y estilo Win32 `WS_EX_NOACTIVATE` aplicado en el handle — para que el toque a una barra no le robe el foco a FormGPS.
- **Publish:** el Host se publica **self-contained** `win-x64` (la pantalla no tiene net9 instalado). Reusar el tuning de runtime de `PilotX.Desktop.csproj` (Workstation GC, TieredCompilation, InvariantGlobalization, R2R en Release).
- **Theme:** reusar los tokens de `PilotXTheme.axaml` (nombres `PilotX*`), copiados dentro de la librería para que sea autocontenida. Verde `#4ABA3E` solo de acento.
- **Branding:** textos de cara al usuario en PilotX / Agro Parallel / CoreX (no AOG/AgOpenGPS/AgIO). Comentarios nuevos en castellano rioplatense; identificadores en inglés.
- **Unidades:** agronómicas al operario (km/h, ha, ha/h, sem/m…), nunca PPS.
- **Tests:** NUnit 4 + Microsoft.NET.Test.Sdk 17.12, proyecto de test `net9.0-windows` (para referenciar la librería net9). Los ViewModels y clients se testean sin instanciar UI.

---

### Task 1: Scaffold de la librería `PilotX.Cockpit.Bars`

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/PilotX.Cockpit.Bars.csproj`
- Create: `SourceCode/PilotX.Cockpit.Bars/Theme/BarsTheme.axaml`
- Modify: `SourceCode/AgOpenGPS.sln` (agregar el proyecto)

**Interfaces:**
- Produces: proyecto de librería Avalonia net9 compilable, con `BarsTheme.axaml` (ResourceDictionary con los brushes `PilotX*`).

- [ ] **Step 1: Crear el csproj de la librería**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <RootNamespace>PilotX.Cockpit.Bars</RootNamespace>
    <AssemblyName>PilotX.Cockpit.Bars</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>$(NoWarn);CS8618;IDE0055</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.3" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Copiar el theme como ResourceDictionary autocontenido**

Copiar el contenido de `SourceCode/PilotX.Desktop/Theme/PilotXTheme.axaml` a `SourceCode/PilotX.Cockpit.Bars/Theme/BarsTheme.axaml` (los `<Color>` y `<SolidColorBrush>` `PilotX*`). Así la librería no depende de PilotX.Desktop.

- [ ] **Step 3: Agregar el proyecto a la solución**

Run: `dotnet sln SourceCode/AgOpenGPS.sln add SourceCode/PilotX.Cockpit.Bars/PilotX.Cockpit.Bars.csproj`
Expected: "Project ... added to the solution."

- [ ] **Step 4: Compilar la librería vacía**

Run: `dotnet build SourceCode/PilotX.Cockpit.Bars/PilotX.Cockpit.Bars.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars SourceCode/AgOpenGPS.sln
git commit -m "feat(bars): scaffold libreria Avalonia PilotX.Cockpit.Bars + theme"
```

---

### Task 2: `CockpitSnapshot` + `CockpitStateClient` (polling de estado)

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/Services/CockpitSnapshot.cs`
- Create: `SourceCode/PilotX.Cockpit.Bars/Services/CockpitStateClient.cs`
- Create: `SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj`
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/CockpitStateClientTests.cs`

**Interfaces:**
- Produces:
  - `sealed class CockpitSnapshot` con las props que consumen las barras, todas con `[JsonPropertyName("<snake_case>")]` EXACTO (ver código Step 4). Campos: `is_job_started`, `avg_speed`, `heading`, `fix_quality`, `worked_area_total_m2`, `tracks_total`, `tracks_visible`, `track_idx`, `is_auto_steer_on`, `is_auto_snap_to_pivot`, `is_you_turn_on`, `is_section_auto_on`, `is_section_manual_on`, `isobus_alive`, `isobus_on`, `is_auto_track_on`, `is_contour_on`, `is_contour_locked`, `has_boundary`, `flag_color`, `is_nudge_on`, `has_headland`, `is_headland_on`, `is_section_controlled_by_headland`, `has_hyd_lift`, `is_hyd_lift_on`, `has_tram`, `tram_display_mode`, `you_skip_mode`, `row_skips_width`.
  - `sealed class CockpitStateClient` con `event Action<CockpitSnapshot>? SnapshotReceived`, `event Action<Exception>? PollFailed`, `void Start()`, `void Stop()`, ctor `(string baseUrl = "http://127.0.0.1:5180/", int intervalMs = 250)`, y `static CockpitSnapshot? Parse(string json)` para testear el mapeo sin red.

- [ ] **Step 1: Crear el proyecto de tests**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PilotX.Cockpit.Bars\PilotX.Cockpit.Bars.csproj" />
  </ItemGroup>
</Project>
```
Luego: `dotnet sln SourceCode/AgOpenGPS.sln add SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj`

- [ ] **Step 2: Escribir el test que falla (mapeo del JSON)**

```csharp
using NUnit.Framework;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.Tests;

public class CockpitStateClientTests
{
    [Test]
    public void Parse_MapsSnakeCaseJson()
    {
        // AgpJson emite snake_case; los JsonPropertyName deben matchear EXACTO
        // (PropertyNameCaseInsensitive no cubre underscores).
        const string json = """
        {"is_job_started":true,"avg_speed":8.5,"heading":1.57,"fix_quality":4,
         "worked_area_total_m2":12345.0,"tracks_total":23,"track_idx":10,
         "is_auto_steer_on":true,"has_headland":true,"tram_display_mode":2,
         "row_skips_width":3,"flag_color":1}
        """;
        var snap = CockpitStateClient.Parse(json);
        Assert.That(snap, Is.Not.Null);
        Assert.That(snap!.IsJobStarted, Is.True);
        Assert.That(snap.AvgSpeed, Is.EqualTo(8.5).Within(0.001));
        Assert.That(snap.FixQuality, Is.EqualTo(4));
        Assert.That(snap.TracksTotal, Is.EqualTo(23));
        Assert.That(snap.TrackIdx, Is.EqualTo(10));
        Assert.That(snap.IsAutoSteerOn, Is.True);
        Assert.That(snap.HasHeadland, Is.True);
        Assert.That(snap.TramDisplayMode, Is.EqualTo(2));
        Assert.That(snap.RowSkipsWidth, Is.EqualTo(3));
        Assert.That(snap.FlagColor, Is.EqualTo(1));
    }
}
```

- [ ] **Step 3: Verificar que falla**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: FAIL (no compila: `CockpitStateClient` / `CockpitSnapshot` no existen).

- [ ] **Step 4: Implementar `CockpitSnapshot` y `CockpitStateClient`**

`CockpitSnapshot.cs` (nombres snake_case EXACTOS del `AogStateSnapshot`):
```csharp
using System.Text.Json.Serialization;

namespace PilotX.Cockpit.Bars.Services;

/// <summary>Subset del AogStateSnapshot que consumen las barras del cockpit.
/// El WebHost emite snake_case (AgpJson); los JsonPropertyName lo matchean.</summary>
public sealed class CockpitSnapshot
{
    // Barra superior
    [JsonPropertyName("is_job_started")]      public bool IsJobStarted { get; set; }
    [JsonPropertyName("avg_speed")]           public double AvgSpeed { get; set; }   // km/h
    [JsonPropertyName("heading")]             public double Heading { get; set; }    // rad
    [JsonPropertyName("fix_quality")]         public int FixQuality { get; set; }    // 4=RTK FIJO,5=FLOAT,2=DGPS,1=GPS,8=SIM
    [JsonPropertyName("worked_area_total_m2")] public double WorkedAreaTotalM2 { get; set; }
    [JsonPropertyName("tracks_total")]        public int TracksTotal { get; set; }
    [JsonPropertyName("tracks_visible")]      public int TracksVisible { get; set; }
    [JsonPropertyName("track_idx")]           public int TrackIdx { get; set; }      // -1 = sin guía
    // Barra derecha
    [JsonPropertyName("is_auto_steer_on")]    public bool IsAutoSteerOn { get; set; }
    [JsonPropertyName("is_auto_snap_to_pivot")] public bool IsAutoSnapToPivot { get; set; }
    [JsonPropertyName("is_you_turn_on")]      public bool IsYouTurnOn { get; set; }
    [JsonPropertyName("is_section_auto_on")]  public bool IsSectionAutoOn { get; set; }
    [JsonPropertyName("is_section_manual_on")] public bool IsSectionManualOn { get; set; }
    [JsonPropertyName("isobus_alive")]        public bool IsobusAlive { get; set; }
    [JsonPropertyName("isobus_on")]           public bool IsobusOn { get; set; }
    [JsonPropertyName("is_auto_track_on")]    public bool IsAutoTrackOn { get; set; }
    [JsonPropertyName("is_contour_on")]       public bool IsContourOn { get; set; }
    [JsonPropertyName("is_contour_locked")]   public bool IsContourLocked { get; set; }
    [JsonPropertyName("has_boundary")]        public bool HasBoundary { get; set; }
    // Barra abajo
    [JsonPropertyName("flag_color")]          public int FlagColor { get; set; }     // 0 roja,1 verde,2 amarilla
    [JsonPropertyName("is_nudge_on")]         public bool IsNudgeOn { get; set; }
    [JsonPropertyName("has_headland")]        public bool HasHeadland { get; set; }
    [JsonPropertyName("is_headland_on")]      public bool IsHeadlandOn { get; set; }
    [JsonPropertyName("is_section_controlled_by_headland")] public bool IsSectionControlledByHeadland { get; set; }
    [JsonPropertyName("has_hyd_lift")]        public bool HasHydLift { get; set; }
    [JsonPropertyName("is_hyd_lift_on")]      public bool IsHydLiftOn { get; set; }
    [JsonPropertyName("has_tram")]            public bool HasTram { get; set; }
    [JsonPropertyName("tram_display_mode")]   public int TramDisplayMode { get; set; } // 0..3
    [JsonPropertyName("you_skip_mode")]       public int YouSkipMode { get; set; }     // 0..2
    [JsonPropertyName("row_skips_width")]     public int RowSkipsWidth { get; set; }   // 1..10
}
```

`CockpitStateClient.cs` (calcado de `PilotX.Desktop/Services/HudPoller.cs`, con `Parse` público para test):
```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Cockpit.Bars.Services;

public sealed class CockpitStateClient : IDisposable
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _url;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    public event Action<CockpitSnapshot>? SnapshotReceived;
    public event Action<Exception>? PollFailed;

    public CockpitStateClient(string baseUrl = "http://127.0.0.1:5180/", int intervalMs = 250)
    {
        var b = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
              : baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        _url = b + "api/aog/state";
        _interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    public static CockpitSnapshot? Parse(string json) =>
        JsonSerializer.Deserialize<CockpitSnapshot>(json, _opts);

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(_url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var snap = Parse(json);
                if (snap != null) SnapshotReceived?.Invoke(snap);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { PollFailed?.Invoke(ex); }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
```

- [ ] **Step 5: Verificar que pasa**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: PASS (1 test).

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars SourceCode/PilotX.Cockpit.Bars.Tests SourceCode/AgOpenGPS.sln
git commit -m "feat(bars): CockpitSnapshot + CockpitStateClient (polling /api/aog/state) + tests"
```

---

### Task 3: `GuidanceCommandClient` (POST de comandos al único endpoint de acciones)

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/Services/GuidanceCommandClient.cs`
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/GuidanceCommandClientTests.cs`

**Interfaces:**
- Consumes: nada de tasks previas (usa `HttpClient`).
- Produces: `sealed class GuidanceCommandClient` con ctor `(HttpClient http, string baseUrl = "http://127.0.0.1:5180/")` y `Task<bool> SendAsync(string cmd, CancellationToken ct = default)`. Hace `POST {baseUrl}api/aog/guidance/command` con body `{"cmd":"<cmd>"}` (JSON), devuelve `resp.IsSuccessStatusCode`. Tolerante a excepciones (devuelve `false`). Es el ÚNICO canal de acción — todos los botones de las 4 barras mandan su verbo por acá.

- [ ] **Step 1: Escribir el test que falla (con `HttpMessageHandler` fake)**

```csharp
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.Tests;

public class GuidanceCommandClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastUrl;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            LastBody = request.Content == null ? null
                     : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task SendAsync_PostsCommandToGuidanceEndpoint()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var client = new GuidanceCommandClient(http, "http://127.0.0.1:5180/");

        var ok = await client.SendAsync("autosteer");

        Assert.That(ok, Is.True);
        Assert.That(handler.LastUrl, Is.EqualTo("http://127.0.0.1:5180/api/aog/guidance/command"));
        Assert.That(handler.LastBody, Does.Contain("\"cmd\":\"autosteer\""));
    }
}
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: FAIL (`GuidanceCommandClient` no existe).

- [ ] **Step 3: Implementar `GuidanceCommandClient`**

```csharp
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Cockpit.Bars.Services;

/// <summary>Único canal de acción de las barras: POST /api/aog/guidance/command
/// {cmd}. El WebHost lo enruta a FormGPS.ExecuteGuidanceCommand, que dispara el
/// botón WinForms nativo correspondiente.</summary>
public sealed class GuidanceCommandClient
{
    private readonly HttpClient _http;
    private readonly string _url;

    public GuidanceCommandClient(HttpClient http, string baseUrl = "http://127.0.0.1:5180/")
    {
        _http = http;
        var b = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
              : baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        _url = b + "api/aog/guidance/command";
    }

    public async Task<bool> SendAsync(string cmd, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { cmd });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_url, content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
```

- [ ] **Step 4: Verificar que pasa**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars SourceCode/PilotX.Cockpit.Bars.Tests
git commit -m "feat(bars): GuidanceCommandClient (POST /api/aog/guidance/command {cmd}) + test"
```

---

### Task 4: `BarViewModelBase` + `BarraSuperiorViewModel`

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarViewModelBase.cs`
- Create: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraSuperiorViewModel.cs`
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/BarraSuperiorViewModelTests.cs`

**Interfaces:**
- Consumes: `CockpitSnapshot` (Task 2), `GuidanceCommandClient` (Task 3).
- Produces:
  - `abstract class BarViewModelBase : ObservableObject` con ctor `(GuidanceCommandClient cmd)`, campo protegido `_cmd`, `virtual void Apply(CockpitSnapshot s)`, y `[RelayCommand] Task Send(string cmd) => _cmd.SendAsync(cmd)` (comando `SendCommand` compartido por todas las barras).
  - `sealed class BarraSuperiorViewModel : BarViewModelBase` con `[ObservableProperty]`: `string speedText`, `string gpsText`, `string gpsDotColor` (hex), `string haText`, `bool loteEnabled`, `string lineBadge`, `bool lineVisible`, `string fechaText`. `Apply` mapea el snapshot (ver código).

- [ ] **Step 1: Escribir el test que falla**

```csharp
using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class BarraSuperiorViewModelTests
{
    private static BarraSuperiorViewModel Make() =>
        new(new GuidanceCommandClient(new HttpClient()));

    [Test]
    public void Apply_FormatsSpeedAndArea_AndRtkStatus()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot
        {
            AvgSpeed = 8.53, WorkedAreaTotalM2 = 12345, FixQuality = 4,
            IsJobStarted = true, TracksTotal = 23, TrackIdx = 10
        });
        Assert.That(vm.SpeedText, Is.EqualTo("8,5"));       // coma decimal, 1 dígito
        Assert.That(vm.HaText, Is.EqualTo("1,2"));          // 12345 m² → 1,2 ha
        Assert.That(vm.GpsText, Is.EqualTo("RTK FIJO"));    // fix 4
        Assert.That(vm.LoteEnabled, Is.True);
        Assert.That(vm.LineVisible, Is.True);
        Assert.That(vm.LineBadge, Is.EqualTo("11/23"));     // (idx+1)/total
    }

    [Test]
    public void Apply_NoFix_ShowsSinFix()
    {
        var vm = Make();
        vm.Apply(new CockpitSnapshot { FixQuality = 0, TrackIdx = -1 });
        Assert.That(vm.GpsText, Is.EqualTo("SIN FIX"));
        Assert.That(vm.LineVisible, Is.False);
    }
}
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: FAIL (VMs no existen).

- [ ] **Step 3: Implementar `BarViewModelBase`**

```csharp
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public abstract partial class BarViewModelBase : ObservableObject
{
    protected readonly GuidanceCommandClient _cmd;
    protected BarViewModelBase(GuidanceCommandClient cmd) => _cmd = cmd;

    /// <summary>Todos los botones bindean a este comando con su verbo como
    /// parámetro (CommandParameter="autosteer", etc.).</summary>
    [RelayCommand]
    protected Task Send(string cmd) => _cmd.SendAsync(cmd);

    /// <summary>Actualiza las propiedades observables desde el snapshot.</summary>
    public abstract void Apply(CockpitSnapshot s);

    // Formato con coma decimal, determinístico (compatible con
    // InvariantGlobalization=true): formateo invariante + swap '.'→','.
    protected static string Coma(double v, int dec) =>
        v.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
}
```

- [ ] **Step 4: Implementar `BarraSuperiorViewModel`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraSuperiorViewModel : BarViewModelBase
{
    public BarraSuperiorViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    [ObservableProperty] private string _speedText = "0,0";
    [ObservableProperty] private string _gpsText = "SIN FIX";
    [ObservableProperty] private string _gpsDotColor = "#E15A5A";
    [ObservableProperty] private string _haText = "0,0";
    [ObservableProperty] private bool _loteEnabled;
    [ObservableProperty] private string _lineBadge = "";
    [ObservableProperty] private bool _lineVisible;
    [ObservableProperty] private string _fechaText = "";

    public override void Apply(CockpitSnapshot s)
    {
        SpeedText = Coma(s.AvgSpeed, 1);
        HaText = Coma(s.WorkedAreaTotalM2 * 0.0001, 1);
        LoteEnabled = s.IsJobStarted;
        (GpsText, GpsDotColor) = s.FixQuality switch
        {
            4 => ("RTK FIJO",  "#4ABA3E"),
            5 => ("RTK FLOAT", "#E2B53E"),
            2 => ("DGPS",      "#E2B53E"),
            1 => ("GPS",       "#E2B53E"),
            8 => ("SIMULADOR", "#8FA092"),
            _ => ("SIN FIX",   "#E15A5A"),
        };
        LineVisible = s.TrackIdx > -1 && s.TracksTotal > 0;
        LineBadge = LineVisible ? $"{s.TrackIdx + 1}/{s.TracksTotal}" : "";
        FechaText = System.DateTime.Now.ToString("HH:mm");
    }
}
```

- [ ] **Step 5: Verificar que pasa**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars SourceCode/PilotX.Cockpit.Bars.Tests
git commit -m "feat(bars): BarViewModelBase + BarraSuperiorViewModel + tests"
```

---

### Task 5: UserControl `BarraSuperior` (XAML)

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/Views/BarraSuperior.axaml` (+ `.axaml.cs`)

**Interfaces:**
- Consumes: `BarraSuperiorViewModel` (Task 4).
- Produces: `UserControl BarraSuperior` con `DataContext` tipado. Botones bindean a `SendCommand` con `CommandParameter` = verbo. Es el patrón de referencia para las otras 3 barras.

- [ ] **Step 1: Escribir el UserControl**

`BarraSuperior.axaml` (usa los brushes `PilotX*` de `BarsTheme`; verbos exactos del mapeo: `lote_menu`, `datos_gps`, `lote_datos`, `track_prev`, `track_next`, `minimizar`, `maximizar`, `apagar`):
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:PilotX.Cockpit.Bars.ViewModels"
             x:Class="PilotX.Cockpit.Bars.Views.BarraSuperior"
             x:DataType="vm:BarraSuperiorViewModel"
             Height="64">
  <Border Background="{StaticResource PilotXBgMid}"
          BorderBrush="{StaticResource PilotXBorder}" BorderThickness="0,0,0,1">
    <DockPanel LastChildFill="False" Margin="6,0">
      <Button DockPanel.Dock="Left" Content="TRABAJO"
              Command="{Binding SendCommand}" CommandParameter="lote_menu"/>
      <Button DockPanel.Dock="Left" Content="GPS"
              Command="{Binding SendCommand}" CommandParameter="datos_gps"/>
      <Button DockPanel.Dock="Left" Content="LOTE" IsEnabled="{Binding LoteEnabled}"
              Command="{Binding SendCommand}" CommandParameter="lote_datos"/>
      <!-- chips de datos vivos -->
      <StackPanel DockPanel.Dock="Left" Orientation="Horizontal" Spacing="14" Margin="16,0">
        <TextBlock Text="{Binding SpeedText}" Foreground="{StaticResource PilotXTextHi}"/>
        <StackPanel Orientation="Horizontal" Spacing="4">
          <Ellipse Width="10" Height="10" Fill="{Binding GpsDotColor}"/>
          <TextBlock Text="{Binding GpsText}" Foreground="{StaticResource PilotXTextMid}"/>
        </StackPanel>
        <TextBlock Text="{Binding HaText}" Foreground="{StaticResource PilotXTextMid}"/>
        <TextBlock Text="{Binding LineBadge}" IsVisible="{Binding LineVisible}"
                   Foreground="{StaticResource PilotXAccent}"/>
        <TextBlock Text="{Binding FechaText}" Foreground="{StaticResource PilotXTextDim}"/>
      </StackPanel>
      <!-- ventana -->
      <Button DockPanel.Dock="Right" Content="✕"
              Command="{Binding SendCommand}" CommandParameter="apagar"/>
      <Button DockPanel.Dock="Right" Content="☐"
              Command="{Binding SendCommand}" CommandParameter="maximizar"/>
      <Button DockPanel.Dock="Right" Content="–"
              Command="{Binding SendCommand}" CommandParameter="minimizar"/>
      <Button DockPanel.Dock="Right" Content="›" IsVisible="{Binding LineVisible}"
              Command="{Binding SendCommand}" CommandParameter="track_next"/>
      <Button DockPanel.Dock="Right" Content="‹" IsVisible="{Binding LineVisible}"
              Command="{Binding SendCommand}" CommandParameter="track_prev"/>
    </DockPanel>
  </Border>
</UserControl>
```

`BarraSuperior.axaml.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PilotX.Cockpit.Bars.Views;

public partial class BarraSuperior : UserControl
{
    public BarraSuperior() => AvaloniaXamlLoader.Load(this);
}
```

- [ ] **Step 2: Verificar que compila la librería**

Run: `dotnet build SourceCode/PilotX.Cockpit.Bars/PilotX.Cockpit.Bars.csproj -c Debug`
Expected: Build succeeded (el XAML compila con CompiledBindings; `x:DataType` valida los bindings en build).

- [ ] **Step 3: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars
git commit -m "feat(bars): UserControl BarraSuperior (XAML) — patrón de referencia"
```

---

### Task 6: ViewModels + UserControls de `BarraDerecha` y `BarraAbajo`

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraDerechaViewModel.cs`
- Create: `SourceCode/PilotX.Cockpit.Bars/Views/BarraDerecha.axaml` (+ `.axaml.cs`)
- Create: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraAbajoViewModel.cs`
- Create: `SourceCode/PilotX.Cockpit.Bars/Views/BarraAbajo.axaml` (+ `.axaml.cs`)
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/BarraDerechaAbajoViewModelTests.cs`

**Interfaces:**
- Consumes: `BarViewModelBase` (Task 4).
- Produces: 2 VMs + 2 UserControls siguiendo el patrón de Task 4/5. Verbos y visibilidades del mapeo (abajo).

Mapeo de comandos (verbo exacto → propiedad de visibilidad derivada del snapshot):

**BarraDerecha** — botones: `autosteer`, `uturn`, `sec_auto`, `sec_manual`, `isobus`, `autotrack`, `track_prev`, `track_next`, `contour`, `contour_lock`. Visibilidades (props `bool` en el VM):
- `NoLoteVisible = !IsJobStarted`
- `UturnVisible = TrackIdx > -1 && !IsContourOn && HasBoundary`
- `IsobusVisible = IsobusAlive`
- `TrackNavVisible = TracksVisible > 1 && TrackIdx > -1 && !IsContourOn`
- `ContourLockVisible = IsContourOn`
- `PilotoActive = IsAutoSteerOn`, `SecAutoActive = IsSectionAutoOn`, `SecManualActive = IsSectionManualOn`, `UturnActive = IsYouTurnOn`, `AutoTrackActive = IsAutoTrackOn`, `ContourActive = IsContourOn`, `ContourLockActive = IsContourLocked` (para colorear el botón con `PilotXAccent` cuando activo).

**BarraAbajo** — botones: `pick`, `center`, `nudge_right`, `nudge_left`, `bandera`, `cabecera_onoff`, `cabecera_secciones`, `hidraulico`, `tram_vista`, `reset_herramienta`, `mapeo_color`, `uturn_skips`, y el selector `skips_<n>`. Visibilidades:
- `NoLoteVisible = !IsJobStarted`
- `NudgeVisible = TrackIdx > -1 && IsNudgeOn`
- `HeadlandVisible = HasHeadland`
- `HydVisible = HasHydLift && HasHeadland`, `HydEnabled = IsHeadlandOn`
- `TramVisible = HasTram`
- `YouSkipVisible = TrackIdx > -1`, `SkipsVisible = TrackIdx > -1`
- `FlagColorHex`: `FlagColor` 0→`#E15A5A`, 1→`#4ABA3E`, 2→`#E2B53E`
- `SkipsValue = RowSkipsWidth` (el `ComboBox` de skips manda `skips_<selectedIndex+1>` en `SelectionChanged`).

- [ ] **Step 1: Escribir el test que falla (visibilidades de ambos VMs)**

```csharp
using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class BarraDerechaAbajoViewModelTests
{
    private static GuidanceCommandClient Cmd() => new(new HttpClient());

    [Test]
    public void Derecha_UturnVisibility_RequiresTrackNoContourAndBoundary()
    {
        var vm = new BarraDerechaViewModel(Cmd());
        vm.Apply(new CockpitSnapshot { TrackIdx = 3, IsContourOn = false, HasBoundary = true });
        Assert.That(vm.UturnVisible, Is.True);
        vm.Apply(new CockpitSnapshot { TrackIdx = 3, IsContourOn = true, HasBoundary = true });
        Assert.That(vm.UturnVisible, Is.False);
    }

    [Test]
    public void Abajo_FlagColorAndHyd()
    {
        var vm = new BarraAbajoViewModel(Cmd());
        vm.Apply(new CockpitSnapshot { FlagColor = 1, HasHydLift = true, HasHeadland = true, IsHeadlandOn = false });
        Assert.That(vm.FlagColorHex, Is.EqualTo("#4ABA3E"));
        Assert.That(vm.HydVisible, Is.True);
        Assert.That(vm.HydEnabled, Is.False);
    }
}
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: FAIL (VMs no existen).

- [ ] **Step 3: Implementar `BarraDerechaViewModel`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraDerechaViewModel : BarViewModelBase
{
    public BarraDerechaViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    [ObservableProperty] private bool _noLoteVisible = true;
    [ObservableProperty] private bool _uturnVisible;
    [ObservableProperty] private bool _isobusVisible;
    [ObservableProperty] private bool _trackNavVisible;
    [ObservableProperty] private bool _contourLockVisible;
    [ObservableProperty] private bool _pilotoActive;
    [ObservableProperty] private bool _secAutoActive;
    [ObservableProperty] private bool _secManualActive;
    [ObservableProperty] private bool _uturnActive;
    [ObservableProperty] private bool _autoTrackActive;
    [ObservableProperty] private bool _contourActive;
    [ObservableProperty] private bool _contourLockActive;

    public override void Apply(CockpitSnapshot s)
    {
        NoLoteVisible = !s.IsJobStarted;
        UturnVisible = s.TrackIdx > -1 && !s.IsContourOn && s.HasBoundary;
        IsobusVisible = s.IsobusAlive;
        TrackNavVisible = s.TracksVisible > 1 && s.TrackIdx > -1 && !s.IsContourOn;
        ContourLockVisible = s.IsContourOn;
        PilotoActive = s.IsAutoSteerOn;
        SecAutoActive = s.IsSectionAutoOn;
        SecManualActive = s.IsSectionManualOn;
        UturnActive = s.IsYouTurnOn;
        AutoTrackActive = s.IsAutoTrackOn;
        ContourActive = s.IsContourOn;
        ContourLockActive = s.IsContourLocked;
    }
}
```

- [ ] **Step 4: Implementar `BarraAbajoViewModel`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class BarraAbajoViewModel : BarViewModelBase
{
    public BarraAbajoViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    [ObservableProperty] private bool _noLoteVisible = true;
    [ObservableProperty] private bool _nudgeVisible;
    [ObservableProperty] private bool _headlandVisible;
    [ObservableProperty] private bool _hydVisible;
    [ObservableProperty] private bool _hydEnabled;
    [ObservableProperty] private bool _tramVisible;
    [ObservableProperty] private bool _youSkipVisible;
    [ObservableProperty] private bool _skipsVisible;
    [ObservableProperty] private string _flagColorHex = "#E15A5A";
    [ObservableProperty] private int _skipsValue = 1;

    public override void Apply(CockpitSnapshot s)
    {
        NoLoteVisible = !s.IsJobStarted;
        NudgeVisible = s.TrackIdx > -1 && s.IsNudgeOn;
        HeadlandVisible = s.HasHeadland;
        HydVisible = s.HasHydLift && s.HasHeadland;
        HydEnabled = s.IsHeadlandOn;
        TramVisible = s.HasTram;
        YouSkipVisible = s.TrackIdx > -1;
        SkipsVisible = s.TrackIdx > -1;
        FlagColorHex = s.FlagColor switch { 1 => "#4ABA3E", 2 => "#E2B53E", _ => "#E15A5A" };
        SkipsValue = s.RowSkipsWidth;
    }
}
```

- [ ] **Step 5: Verificar que pasan los tests**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: PASS.

- [ ] **Step 6: Escribir los UserControls `BarraDerecha.axaml` y `BarraAbajo.axaml`**

Columna (derecha) / fila (abajo) de `Button` con `Command="{Binding SendCommand}"` + `CommandParameter="<verbo>"` (los verbos de la tabla de arriba), `IsVisible` bindeado a las props de visibilidad, y color de acento cuando `*Active`. Seguir exactamente el patrón XAML de `BarraSuperior.axaml` (Task 5), con `x:DataType` al VM correspondiente. El `ComboBox` de skips en BarraAbajo: `SelectionChanged` → `SendCommand` con `"skips_" + (SelectedIndex+1)`; hacerlo en el code-behind `BarraAbajo.axaml.cs` (llamando `((BarraAbajoViewModel)DataContext).SendCommand.Execute(...)`), no en el VM, para mantener el VM sin dependencias de UI.

- [ ] **Step 7: Build + commit**

Run: `dotnet build SourceCode/PilotX.Cockpit.Bars -c Debug`
```bash
git add SourceCode/PilotX.Cockpit.Bars SourceCode/PilotX.Cockpit.Bars.Tests
git commit -m "feat(bars): BarraDerecha + BarraAbajo (VMs + UserControls) + tests"
```

---

### Task 7: `MenuIzquierdaViewModel` + UserControl (con submenús nativos)

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/ViewModels/MenuIzquierdaViewModel.cs`
- Create: `SourceCode/PilotX.Cockpit.Bars/Views/MenuIzquierda.axaml` (+ `.axaml.cs`)
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/MenuIzquierdaViewModelTests.cs`

**Interfaces:**
- Consumes: `BarViewModelBase` (Task 4).
- Produces: `MenuIzquierdaViewModel` con `[ObservableProperty] string? openSubmenu` (cuál submenú está expandido, o null) y `[RelayCommand] void ToggleSubmenu(string name)` (abre/cierra; reemplaza el `resize:WxH` del WebView2 — puro toggle de layout local). Los ítems directos y de submenú usan `SendCommand` con su verbo.

Estructura del menú (del mapeo; NO hace polling de state):
- **Columna principal**: `Navegación`/`Config`/`Herramientas`/`Lote`/`Herr. lote` → `ToggleSubmenu`. Directos: `Dirección`→`direccion`, `CoreX`→`corex`.
- **Submenú Navegación**: `v2d, v3d, norte2d, grilla, dia_noche, brillo_up, brillo_dn, hub, navegacion`.
- **Submenú Config**: `config_form, direccion, todos_ajustes, colores, colores_sec, datos_gps, perfil_nuevo, perfil_cargar, directorios, ayuda`.
- **Submenú Herramientas**: `asistente_direccion, grafico_direccion, grafico_rumbo, grafico_xte, chequeo_roll, herr_limites, suavizar_ab, borrar_contornos, corregir_pos, visor_eventos, webcam`.
- **Submenú Lote**: `lote_continuar, lote_menu, lote_cerrar, lote_datos, bandera, bandera_latlon, borrar_aplicado, mapeo_color`.
- **Submenú Herr. lote**: `lindero, cabecera, cabecera_avanzada, cabecera_onoff, importar_guias, tram_crear, tram_vista`.

- [ ] **Step 1: Escribir el test que falla (toggle de submenú)**

```csharp
using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class MenuIzquierdaViewModelTests
{
    [Test]
    public void ToggleSubmenu_OpensAndCloses()
    {
        var vm = new MenuIzquierdaViewModel(new GuidanceCommandClient(new HttpClient()));
        Assert.That(vm.OpenSubmenu, Is.Null);
        vm.ToggleSubmenuCommand.Execute("navegacion");
        Assert.That(vm.OpenSubmenu, Is.EqualTo("navegacion"));
        vm.ToggleSubmenuCommand.Execute("navegacion");   // segundo toque cierra
        Assert.That(vm.OpenSubmenu, Is.Null);
        vm.ToggleSubmenuCommand.Execute("config");        // otro abre y reemplaza
        Assert.That(vm.OpenSubmenu, Is.EqualTo("config"));
    }
}
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: FAIL.

- [ ] **Step 3: Implementar `MenuIzquierdaViewModel`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.ViewModels;

public sealed partial class MenuIzquierdaViewModel : BarViewModelBase
{
    public MenuIzquierdaViewModel(GuidanceCommandClient cmd) : base(cmd) { }

    // Cuál submenú está expandido (null = ninguno). Reemplaza el resize del WebView2.
    [ObservableProperty] private string? _openSubmenu;

    [RelayCommand]
    private void ToggleSubmenu(string name) =>
        OpenSubmenu = OpenSubmenu == name ? null : name;

    public override void Apply(CockpitSnapshot s) { /* no hace polling de estado */ }
}
```

- [ ] **Step 4: Verificar que pasa**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -c Debug`
Expected: PASS.

- [ ] **Step 5: Escribir `MenuIzquierda.axaml`**

Columna de botones principales (`ToggleSubmenuCommand` con nombre; `SendCommand` para `direccion`/`corex`). Un panel expandible por submenú con `IsVisible="{Binding OpenSubmenu, Converter=...}"` (usar un `IValueConverter` `EqualsConverter` o comparar por índice); cada ítem de submenú es un `Button` con `SendCommand` + su verbo. Seguir el patrón de `BarraSuperior.axaml`. Crear el converter en `SourceCode/PilotX.Cockpit.Bars/Views/StringEqualsConverter.cs` (devuelve `true` si el binding == parámetro).

- [ ] **Step 6: Build + commit**

Run: `dotnet build SourceCode/PilotX.Cockpit.Bars -c Debug`
```bash
git add SourceCode/PilotX.Cockpit.Bars SourceCode/PilotX.Cockpit.Bars.Tests
git commit -m "feat(bars): MenuIzquierda (VM + UserControl con submenús nativos) + test"
```

---

### Task 8: `PilotX.Bars.Host` — ventanas ancladas + ciclo de vida

**Files:**
- Create: `SourceCode/PilotX.Bars.Host/PilotX.Bars.Host.csproj`
- Create: `SourceCode/PilotX.Bars.Host/Program.cs`
- Create: `SourceCode/PilotX.Bars.Host/App.axaml` (+ `.axaml.cs`)
- Create: `SourceCode/PilotX.Bars.Host/BarWindow.cs`
- Create: `SourceCode/PilotX.Bars.Host/Win32NoActivate.cs`
- Modify: `SourceCode/AgOpenGPS.sln`

**Interfaces:**
- Consumes: los 4 `UserControl` de la librería (Task 4-7) y `CockpitStateClient` (Task 2).
- Produces: exe `PilotX.Bars.Host.exe` que abre 4 `BarWindow` ancladas (top/right/bottom/left), aplica `WS_EX_NOACTIVATE`, y se cierra cuando muere el proceso `--parent-pid`.

- [ ] **Step 1: Crear el csproj del Host (self-contained, tuning tractor)**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace>PilotX.Bars.Host</RootNamespace>
    <AssemblyName>PilotX.Bars.Host</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
    <TieredCompilation>true</TieredCompilation>
    <TieredCompilationQuickJit>true</TieredCompilationQuickJit>
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>$(NoWarn);CS8618;IDE0055</NoWarn>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <PublishReadyToRun>true</PublishReadyToRun>
    <DebugType>none</DebugType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.3" />
    <PackageReference Include="Avalonia.Desktop" Version="11.2.3" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.3" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.2.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PilotX.Cockpit.Bars\PilotX.Cockpit.Bars.csproj" />
  </ItemGroup>
</Project>
```
Copiar `app.manifest` de `SourceCode/PilotX.Desktop/app.manifest`. Agregar a la solución con `dotnet sln add`.

- [ ] **Step 2: `Win32NoActivate.cs` — helper de estilo de ventana**

```csharp
using System;
using System.Runtime.InteropServices;

namespace PilotX.Bars.Host;

/// <summary>Aplica WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW al HWND para que la
/// ventana reciba clicks sin robar el foco a FormGPS (fullscreen detras).</summary>
internal static class Win32NoActivate
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void Apply(IntPtr hWnd)
    {
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
```

- [ ] **Step 3: `BarWindow.cs` — ventana borderless anclada**

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace PilotX.Bars.Host;

public enum BarEdge { Top, Right, Bottom, Left }

/// <summary>Ventana sin decoraciones anclada a un borde de la pantalla
/// primaria. Hostea un UserControl de PilotX.Cockpit.Bars.</summary>
public sealed class BarWindow : Window
{
    private readonly BarEdge _edge;
    private readonly double _thickness; // alto (top/bottom) o ancho (left/right), en px logicos

    public BarWindow(BarEdge edge, double thickness, Control content)
    {
        _edge = edge;
        _thickness = thickness;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        Background = null;
        Content = content;
        Opened += (_, _) =>
        {
            var h = TryGetPlatformHandle();
            if (h != null) Win32NoActivate.Apply(h.Handle);
            Reposition();
        };
    }

    public void Reposition()
    {
        var screen = Screens.Primary ?? Screens.All[0];
        var wa = screen.WorkingArea; // px fisicos
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int t = (int)(_thickness * scale);
        switch (_edge)
        {
            case BarEdge.Top:
                Position = wa.Position; Width = wa.Width / scale; Height = _thickness; break;
            case BarEdge.Bottom:
                Position = new PixelPoint(wa.X, wa.Y + wa.Height - t);
                Width = wa.Width / scale; Height = _thickness; break;
            case BarEdge.Left:
                Position = new PixelPoint(wa.X, wa.Y + t);
                Width = _thickness; Height = (wa.Height - 2 * t) / scale; break;
            case BarEdge.Right:
                Position = new PixelPoint(wa.X + wa.Width - t, wa.Y + t);
                Width = _thickness; Height = (wa.Height - 2 * t) / scale; break;
        }
    }
}
```

- [ ] **Step 4: `App.axaml`(.cs) + `Program.cs` — arranque, args, ciclo de vida**

`App.axaml` incluye `BarsTheme.axaml` de la librería en `Application.Resources` (`<ResourceInclude Source="avares://PilotX.Cockpit.Bars/Theme/BarsTheme.axaml"/>`) y un `<FluentTheme/>`.

`App.axaml.cs` en `OnFrameworkInitializationCompleted`: construye un `GuidanceCommandClient` y los 4 VMs, crea los 4 `UserControl` con su `DataContext`, los mete en 4 `BarWindow`, arranca un `CockpitStateClient` que en `SnapshotReceived` hace `Dispatcher.UIThread.Post` para llamar `Apply` en los VMs que consumen estado (superior/derecha/abajo), y registra el watcher del parent-pid para cerrar la app.

```csharp
public override void OnFrameworkInitializationCompleted()
{
    var http = new HttpClient();
    var cmd = new GuidanceCommandClient(http, Program.BaseUrl);
    var vmSup = new BarraSuperiorViewModel(cmd);
    var vmDer = new BarraDerechaViewModel(cmd);
    var vmAba = new BarraAbajoViewModel(cmd);
    var vmIzq = new MenuIzquierdaViewModel(cmd);

    var top    = new BarWindow(BarEdge.Top,    64, new BarraSuperior  { DataContext = vmSup });
    var right  = new BarWindow(BarEdge.Right,  74, new BarraDerecha   { DataContext = vmDer });
    var bottom = new BarWindow(BarEdge.Bottom, 74, new BarraAbajo     { DataContext = vmAba });
    var left   = new BarWindow(BarEdge.Left,   94, new MenuIzquierda  { DataContext = vmIzq });

    var poller = new CockpitStateClient(Program.BaseUrl);
    poller.SnapshotReceived += s => Dispatcher.UIThread.Post(() =>
    {
        vmSup.Apply(s); vmDer.Apply(s); vmAba.Apply(s);
        // right/bottom se ocultan cuando no hay lote (visibilidad a nivel ventana)
        right.IsVisible = s.IsJobStarted;
        bottom.IsVisible = s.IsJobStarted;
    });

    top.Show(); left.Show(); right.Show(); bottom.Show();
    poller.Start();
    Program.WatchParent(() => Dispatcher.UIThread.Post(() =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()));
    base.OnFrameworkInitializationCompleted();
}
```

> Nota: no se setea `desktop.MainWindow` (no hay ventana principal — son 4 overlays). El `IClassicDesktopStyleApplicationLifetime` con `ShutdownMode` default cierra cuando se cierran todas las ventanas; para overlays sin MainWindow, setear `desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown` en `Program` antes de `Start`, y cerrar vía `Shutdown()` en el watcher.

`Program.cs`:
```csharp
using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;

namespace PilotX.Bars.Host;

internal static class Program
{
    public static int ParentPid = -1;
    public static string BaseUrl = "http://127.0.0.1:5180/";

    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--parent-pid=") && int.TryParse(a.Substring(13), out var p)) ParentPid = p;
            else if (a.StartsWith("--base-url=")) BaseUrl = a.Substring(11);
        }
        // Single-instance: si ya hay un Host, salir.
        bool created;
        using var mtx = new Mutex(true, "PilotX.Bars.Host.Singleton", out created);
        if (!created) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    // Cierra el Host cuando muere FormGPS (parent).
    public static void WatchParent(Action onParentExit)
    {
        if (ParentPid <= 0) return;
        try
        {
            var proc = Process.GetProcessById(ParentPid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => onParentExit();
        }
        catch { onParentExit(); } // parent ya no existe
    }
}
```

- [ ] **Step 5: Build + smoke run manual**

Run: `dotnet build SourceCode/PilotX.Bars.Host/PilotX.Bars.Host.csproj -c Debug`
Expected: Build succeeded.
Run manual (con AgpWebHost/PilotX corriendo): `dotnet run --project SourceCode/PilotX.Bars.Host -- --parent-pid=<pid_de_PilotX>`
Expected: aparecen 4 barras ancladas a los bordes; el toque al centro va al mapa; cerrar PilotX cierra el Host.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.Bars.Host SourceCode/AgOpenGPS.sln
git commit -m "feat(bars): PilotX.Bars.Host — 4 ventanas ancladas NoActivate + watch parent-pid"
```

---

### Task 9: Lanzar el Host desde el cockpit WinForms (net48)

**Files:**
- Modify: `SourceCode/GPS/Forms/GUI.FloatingMenu.cs` (metodo `ToggleBarrasHtml` / `ActualizarBarrasHtml`, ~líneas 75-128)
- Modify: `SourceCode/GPS/Forms/FormGPS.cs` (agregar `LaunchBarsHost` + `ResolveBarsHostExe`, calcados de `LaunchAvaloniaWidget`/`ResolveAvaloniaWidgetExe` ~líneas 1968-2023)

**Interfaces:**
- Consumes: `PilotX.Bars.Host.exe` (Task 8).
- Produces: al activar el modo barras, el cockpit lanza el Host una vez y lo mata al cerrar; el camino WebView2 queda como fallback detrás del flag.

- [ ] **Step 1: Agregar `LaunchBarsHost` + `ResolveBarsHostExe` en FormGPS.cs**

Calcar el patrón de `LaunchAvaloniaWidget`/`ResolveAvaloniaWidgetExe`. `LaunchBarsHost` arma `--parent-pid=<Process.GetCurrentProcess().Id>` y `--base-url=http://127.0.0.1:5180/`, guarda el `Process` en un campo `_barsHostProc` para poder matarlo. `ResolveBarsHostExe` busca `BarsHost/PilotX.Bars.Host.exe` junto al exe de PilotX (prod) y el bin de dev subiendo desde baseDir (igual que el resolver existente).

```csharp
private System.Diagnostics.Process _barsHostProc;

private bool LaunchBarsHost()
{
    try
    {
        string exe = ResolveBarsHostExe();
        if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return false;
        if (_barsHostProc != null && !_barsHostProc.HasExited) return true; // ya corriendo
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            Arguments = "--parent-pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                      + " --base-url=http://127.0.0.1:5180/",
        };
        _barsHostProc = System.Diagnostics.Process.Start(psi);
        return true;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine("[BarsHost] Launch: " + ex.Message);
        return false;
    }
}

private void StopBarsHost()
{
    try { if (_barsHostProc != null && !_barsHostProc.HasExited) _barsHostProc.Kill(); }
    catch { }
    _barsHostProc = null;
}

private static string ResolveBarsHostExe()
{
    string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
    string prod = System.IO.Path.Combine(baseDir, "BarsHost", "PilotX.Bars.Host.exe");
    if (System.IO.File.Exists(prod)) return prod;
    try
    {
        var dir = new System.IO.DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string cand = System.IO.Path.Combine(dir.FullName,
                "SourceCode", "PilotX.Bars.Host", "bin", "Debug", "net9.0-windows",
                "PilotX.Bars.Host.exe");
            if (System.IO.File.Exists(cand)) return cand;
            cand = cand.Replace(@"\Debug\", @"\Release\");
            if (System.IO.File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
    }
    catch { }
    return null;
}
```

- [ ] **Step 2: Cablear en `ToggleBarrasHtml` con fallback**

En `ToggleBarrasHtml()`: cuando `isHtmlBarsMode` pasa a `true`, intentar `LaunchBarsHost()`. Si devuelve `true`, NO abrir las barras WebView2 (dejar `ActualizarBarrasHtml` en no-op para las 4 páginas). Si devuelve `false` (exe no encontrado), caer al camino WebView2 actual. Al pasar a `false`, `StopBarsHost()` + cerrar las barras WebView2 si estaban. Guardar un campo `bool _barsHostActive` para que `ActualizarBarrasHtml` sepa qué camino sigue.

- [ ] **Step 3: Matar el Host al cerrar FormGPS**

En el handler de cierre de FormGPS (buscar `FormGPS_FormClosing`), llamar `StopBarsHost()` (belt & suspenders — el Host igual se autocierra por el watch del parent-pid).

- [ ] **Step 4: Build de la app WinForms**

Run: `pwsh -File build.ps1` (o el build de AOG del repo). Expected: compila net48 sin errores nuevos.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/GPS/Forms/GUI.FloatingMenu.cs SourceCode/GPS/Forms/FormGPS.cs
git commit -m "feat(bars): cockpit WinForms lanza PilotX.Bars.Host con fallback a barras WebView2"
```

---

### Task 10: Publish del Host en build.ps1 + validación en pantalla

**Files:**
- Modify: `build.ps1` (agregar publish self-contained del Host a `Build/BarsHost/`)

**Interfaces:**
- Consumes: `PilotX.Bars.Host` (Task 8).
- Produces: `Build/BarsHost/PilotX.Bars.Host.exe` self-contained, resuelto por `ResolveBarsHostExe`.

- [ ] **Step 1: Agregar el publish al build.ps1**

Tras el build de PilotX, agregar:
```powershell
dotnet publish SourceCode/PilotX.Bars.Host/PilotX.Bars.Host.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -o Build/BarsHost
```

- [ ] **Step 2: Verificar el output**

Run: `pwsh -File build.ps1`
Expected: existe `Build/BarsHost/PilotX.Bars.Host.exe`.

- [ ] **Step 3: Validación manual en la pantalla ViewX (192.168.1.78)**

Con el fallback WebView2 disponible por si algo falla:
1. Copiar `Build/BarsHost/` a `C:\AgroParallel\BarsHost\`.
2. Abrir PilotX (doble clic), activar modo barras.
3. Verificar: 4 barras ancladas, toque al mapa pasa (FormGPS no pierde foco), datos vivos correctos, acciones funcionan.
4. Medir RAM/CPU (script de diagnóstico) vs. barras WebView2 — confirmar que bajan renderers Chromium y picos de CPU.

- [ ] **Step 4: Commit**

```bash
git add build.ps1
git commit -m "build(bars): publish self-contained de PilotX.Bars.Host a Build/BarsHost"
```

---

## Notas de máquina / riesgo

- Todo el peso está en proyectos nuevos aislados; el único cambio en el productivo (FormGPS/GUI.FloatingMenu) es aditivo y reversible, con fallback WebView2. Alineado con "esta máquina = visual/bajo riesgo".
- Validación siempre con el fallback disponible.
