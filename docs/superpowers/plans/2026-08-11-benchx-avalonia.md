# BenchX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reemplazar ModSim (WinForms) por BenchX: el mismo simulador de banco (módulos autosteer/máquina/IMU + GPS NMEA por UDP) reescrito en Avalonia net9 con la estética Agro Parallel y la lógica extraída a clases puras testeables.

**Architecture:** Proyecto `SourceCode/BenchX/` (net9.0-windows + Avalonia 11.2.3, mismo stack que `PilotX.Bars.Host`). Lógica pura en `Sim/` (NmeaBuilder, SimuladorVehiculo, PgnProcessor), red en `Red/UdpLink.cs`, config JSON en `Config/`, UI de una ventana con cards. Tests NUnit en `SourceCode/BenchX.Tests/`.

**Tech Stack:** .NET 9, Avalonia 11.2.3, NUnit 4.3.2 (igual que `PilotX.Cockpit.Bars.Tests` — el spec decía xunit pero el repo usa NUnit; se sigue el repo), System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-08-11-benchx-avalonia-design.md`

## Global Constraints

- Textos de UI, comentarios y commits en castellano rioplatense. En UI decir "PilotX", nunca AOG/AgIO.
- Paleta oficial: fondo `#F5F7F4`, cards `#FFFFFF` con borde `#C5CFC5`, superficie suave `#E2E7E2`, acento `#4ABA3E`, texto `#101612` / secundario `#535E54`. Fuente Inter.
- **Wire protocol idéntico a ModSim**: mismas sentencias NMEA byte a byte (mismos formatos, constantes mágicas incluidas) y mismos PGN. La única excepción deliberada: `CultureInfo.InvariantCulture` en TODO formateo (el original tenía dos `ToString()` sin cultura — bug latente con coma decimal; el csproj además lleva `InvariantGlobalization=true`).
- Subred default **127.255.255** (banco loopback). Escucha :8888, envía broadcast a `<subred>.255:9999`.
- Nada de `BeginReceiveFrom` (en net9 el callback sincrónico recurre — lección 0e90d4ca); bucle async con catch-que-continúa (lección 10054 del 2026-08-05).
- Sin atajos de teclado; controles grandes.
- ModSim es carril de Santi: la tarea final agrega la nota de PULL en `COORDINACION-SESIONES.md`.
- El tablero de cierre NO se toca (ModSim no es uno de los 382 íconos): ninguna línea `Cierra:`/`Prueba:` en los commits.
- Comandos desde la raíz del repo: `G:\AgroParallel\Productos\CentriX-Spark\Software\App_PC\AgOpenGPS`.

---

### Task 1: Esqueleto del proyecto BenchX + tests + solución

**Files:**
- Create: `SourceCode/BenchX/BenchX.csproj`
- Create: `SourceCode/BenchX/Program.cs`
- Create: `SourceCode/BenchX/App.axaml`
- Create: `SourceCode/BenchX/App.axaml.cs`
- Create: `SourceCode/BenchX/MainWindow.axaml`
- Create: `SourceCode/BenchX/MainWindow.axaml.cs`
- Create: `SourceCode/BenchX.Tests/BenchX.Tests.csproj`
- Create: `SourceCode/BenchX.Tests/HumoTests.cs`
- Modify: `SourceCode/AgOpenGPS.sln` (vía `dotnet sln add`; ModSim NO está en la sln — verificado, nada que sacar)

**Interfaces:**
- Produces: proyecto `BenchX` (namespace raíz `BenchX`) y proyecto de tests que referencia a BenchX. Las tareas 2-6 agregan clases bajo `BenchX.Sim`, `BenchX.Red`, `BenchX.Config`.

- [ ] **Step 1: Crear `SourceCode/BenchX/BenchX.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace>BenchX</RootNamespace>
    <AssemblyName>BenchX</AssemblyName>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <DebugType>none</DebugType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.3" />
    <PackageReference Include="Avalonia.Desktop" Version="11.2.3" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.3" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.2.3" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Crear `SourceCode/BenchX/Program.cs`**

```csharp
using System;
using Avalonia;

namespace BenchX;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
```

- [ ] **Step 3: Crear `SourceCode/BenchX/App.axaml`** (paleta Agro Parallel + estilos de cards)

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="BenchX.App"
             RequestedThemeVariant="Light">
  <Application.Resources>
    <SolidColorBrush x:Key="FondoApp">#F5F7F4</SolidColorBrush>
    <SolidColorBrush x:Key="FondoCard">#FFFFFF</SolidColorBrush>
    <SolidColorBrush x:Key="BordeCard">#C5CFC5</SolidColorBrush>
    <SolidColorBrush x:Key="FondoSuave">#E2E7E2</SolidColorBrush>
    <SolidColorBrush x:Key="Acento">#4ABA3E</SolidColorBrush>
    <SolidColorBrush x:Key="TextoPrimario">#101612</SolidColorBrush>
    <SolidColorBrush x:Key="TextoSecundario">#535E54</SolidColorBrush>
    <SolidColorBrush x:Key="Alerta">#C94F3D</SolidColorBrush>
  </Application.Resources>
  <Application.Styles>
    <FluentTheme />

    <!-- Card: contenedor blanco de cada bloque -->
    <Style Selector="Border.card">
      <Setter Property="Background" Value="{StaticResource FondoCard}"/>
      <Setter Property="BorderBrush" Value="{StaticResource BordeCard}"/>
      <Setter Property="BorderThickness" Value="1"/>
      <Setter Property="CornerRadius" Value="10"/>
      <Setter Property="Padding" Value="16"/>
    </Style>

    <!-- Título de card -->
    <Style Selector="TextBlock.titulo">
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Foreground" Value="{StaticResource TextoSecundario}"/>
      <Setter Property="Margin" Value="0,0,0,10"/>
    </Style>

    <!-- Valor grande (velocidad, ángulo, etc.) -->
    <Style Selector="TextBlock.valor">
      <Setter Property="FontSize" Value="24"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Foreground" Value="{StaticResource TextoPrimario}"/>
    </Style>

    <!-- Etiqueta chica secundaria -->
    <Style Selector="TextBlock.dato">
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Foreground" Value="{StaticResource TextoSecundario}"/>
    </Style>

    <!-- Valor de dato de solo lectura -->
    <Style Selector="TextBlock.datoValor">
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Foreground" Value="{StaticResource TextoPrimario}"/>
    </Style>

    <!-- Hilera de puntos de relés/zonas -->
    <Style Selector="TextBlock.puntos">
      <Setter Property="FontSize" Value="15"/>
      <Setter Property="Foreground" Value="{StaticResource Acento}"/>
      <Setter Property="FontFamily" Value="Consolas,monospace"/>
    </Style>

    <!-- Badge de estado -->
    <Style Selector="Border.badge">
      <Setter Property="CornerRadius" Value="6"/>
      <Setter Property="Padding" Value="10,4"/>
      <Setter Property="Background" Value="{StaticResource FondoSuave}"/>
    </Style>
    <Style Selector="Border.badge.activo">
      <Setter Property="Background" Value="{StaticResource Acento}"/>
    </Style>
    <Style Selector="Border.badge > TextBlock">
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Foreground" Value="{StaticResource TextoPrimario}"/>
    </Style>
    <Style Selector="Border.badge.activo > TextBlock">
      <Setter Property="Foreground" Value="White"/>
    </Style>

    <Style Selector="Slider">
      <Setter Property="Margin" Value="0,2,0,2"/>
    </Style>
  </Application.Styles>
</Application>
```

- [ ] **Step 4: Crear `SourceCode/BenchX/App.axaml.cs`**

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace BenchX;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 5: Crear `SourceCode/BenchX/MainWindow.axaml` (shell provisorio — la Task 8 lo reemplaza)**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="BenchX.MainWindow"
        Title="BenchX — Agro Parallel"
        Width="1080" Height="720"
        Background="{StaticResource FondoApp}">
  <TextBlock Text="BenchX" Classes="valor" HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Window>
```

- [ ] **Step 6: Crear `SourceCode/BenchX/MainWindow.axaml.cs`**

```csharp
using Avalonia.Controls;

namespace BenchX;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 7: Crear `SourceCode/BenchX.Tests/BenchX.Tests.csproj`**

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
    <ProjectReference Include="..\BenchX\BenchX.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 8: Crear `SourceCode/BenchX.Tests/HumoTests.cs`** (prueba que el wiring de test funciona; se borra cuando haya tests reales — la Task 2 lo reemplaza)

```csharp
using NUnit.Framework;

namespace BenchX.Tests;

public class HumoTests
{
    [Test]
    public void El_proyecto_de_tests_compila_y_corre() => Assert.Pass();
}
```

- [ ] **Step 9: Agregar a la solución y compilar**

```powershell
dotnet sln SourceCode\AgOpenGPS.sln add SourceCode\BenchX\BenchX.csproj SourceCode\BenchX.Tests\BenchX.Tests.csproj
dotnet build SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Build succeeded` (0 warnings relevantes, 0 errores).

- [ ] **Step 10: Correr el test de humo**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 11: Commit**

```bash
git add SourceCode/BenchX SourceCode/BenchX.Tests SourceCode/AgOpenGPS.sln
git commit -m "feat(benchx): esqueleto Avalonia net9 con paleta Agro Parallel

Proyecto nuevo BenchX (reemplazo de ModSim WinForms, spec
2026-08-11-benchx-avalonia-design.md) + BenchX.Tests (NUnit), agregados
a la solución. Ventana shell; la lógica llega en tareas siguientes."
```

---

### Task 2: NmeaBuilder — sentencias NMEA byte a byte

**Files:**
- Create: `SourceCode/BenchX/Sim/GpsEstado.cs`
- Create: `SourceCode/BenchX/Sim/NmeaBuilder.cs`
- Create: `SourceCode/BenchX.Tests/NmeaBuilderTests.cs`
- Delete: `SourceCode/BenchX.Tests/HumoTests.cs`

**Interfaces:**
- Produces:
  - `BenchX.Sim.GpsEstado` — POCO con `string TimeNow; double LatNmea, LonNmea; char NS, EW; double Latitude, Longitude, HeadingDeg, SpeedKnots, RollDeg, Altitude; int HeadingImu, RollImu`.
  - `BenchX.Sim.NmeaBuilder` — estático: `string Checksum(string sentenciaHastaAsterisco)` y `string BuildGga/BuildVtg/BuildHdt/BuildAvr/BuildOgi/BuildNda/BuildRmc/BuildKsxt(GpsEstado g)`, cada una devuelve la sentencia completa con checksum y `\r\n`.

- [ ] **Step 1: Crear `SourceCode/BenchX/Sim/GpsEstado.cs`**

```csharp
namespace BenchX.Sim;

// Foto del fix que consumen las sentencias NMEA. La llena SimuladorVehiculo
// en cada tick; TimeNow la pone el ViewModel (hora real, no testeable adentro).
public sealed class GpsEstado
{
    public string TimeNow = "";        // "HHmmss.fff," — la coma final viaja, igual que en ModSim
    public double LatNmea, LonNmea;    // grados*100 + minutos (formato ddmm.mmmmmmm)
    public char NS = 'N', EW = 'W';
    public double Latitude, Longitude; // grados con signo (solo KSXT los usa crudos)
    public double HeadingDeg;
    public double SpeedKnots;
    public double RollDeg;
    public double Altitude = 300;      // solo KSXT; GGA/OGI/NDA llevan el "1000" fijo histórico
    public int HeadingImu, RollImu;    // grados*10, para PANDA
}
```

- [ ] **Step 2: Escribir los tests que fallan — `SourceCode/BenchX.Tests/NmeaBuilderTests.cs`** (y borrar `HumoTests.cs`)

```csharp
using System.Globalization;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

public class NmeaBuilderTests
{
    private static GpsEstado Fix() => new()
    {
        TimeNow = "123519.000,",
        LatNmea = 5323.1633840, NS = 'N',
        LonNmea = 11109.6028200, EW = 'W',
        Latitude = 53.4360564, Longitude = -111.160047,
        HeadingDeg = 87.65432, SpeedKnots = 4.5, RollDeg = 1.5,
        HeadingImu = 876, RollImu = 15,
    };

    // Recalcula el XOR entre '$' y '*' y lo compara con los 2 hex del final.
    private static void AssertChecksumValido(string s)
    {
        Assert.That(s, Does.EndWith("\r\n"));
        int ast = s.IndexOf('*');
        Assert.That(ast, Is.GreaterThan(0));
        int sum = 0;
        for (int i = 1; i < ast; i++) sum ^= s[i];
        Assert.That(s.Substring(ast + 1, 2), Is.EqualTo(sum.ToString("X2")));
    }

    [Test]
    public void Gga_lleva_lat_lon_y_constantes_historicas()
    {
        string s = NmeaBuilder.BuildGga(Fix());
        Assert.That(s, Does.StartWith(
            "$GPGGA,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,M,46.9,M,37.1,,*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Vtg_lleva_rumbo_y_velocidad_en_nudos_y_kmh()
    {
        string s = NmeaBuilder.BuildVtg(Fix());
        Assert.That(s, Does.StartWith("$GPVTG,87.65432,T,034.4,M,4.5,N,8.334,K*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Hdt_lleva_solo_el_rumbo()
    {
        string s = NmeaBuilder.BuildHdt(Fix());
        Assert.That(s, Does.StartWith("$GNHDT,87.65432,T*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Avr_lleva_rumbo_y_roll()
    {
        string s = NmeaBuilder.BuildAvr(Fix());
        Assert.That(s, Does.StartWith("$PTNL,AVR,123519.000,87.65432,Yaw,-2.1,Tilt,1.5,Roll,444.232,3,1.2,17*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Ogi_lleva_fix_velocidad_rumbo_y_roll()
    {
        string s = NmeaBuilder.BuildOgi(Fix());
        Assert.That(s, Does.StartWith(
            "$PAOGI,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,3.2,4.5,87.65432,1.5,0.12,359.9,T*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Nda_lleva_imu_en_decimas()
    {
        string s = NmeaBuilder.BuildNda(Fix());
        Assert.That(s, Does.StartWith(
            "$PANDA,123519.000,5323.1633840,N,11109.6028200,W,8,12,0.9,1000,3.2,4.5,876,15,32,298*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Rmc_lleva_fix_y_velocidad()
    {
        string s = NmeaBuilder.BuildRmc(Fix());
        Assert.That(s, Does.StartWith(
            "$GPRMC,123519.000,A,5323.1633840,N,11109.6028200,W,4.5,87.65432,230394,359.9*"));
        AssertChecksumValido(s);
    }

    [Test]
    public void Ksxt_usa_grados_crudos_y_checksum_fijo_historico()
    {
        // KSXT en ModSim nunca calculó checksum real: viaja el literal 3FCF0C9B.
        // El formato "0000.0000000" fuerza 4 dígitos enteros: -111.16 → "-0111.1600470".
        string s = NmeaBuilder.BuildKsxt(Fix());
        Assert.That(s, Does.StartWith("$KSXT,123519.000,-0111.1600470,0053.4360564,300,87.65432,22,35,4.5,1.5,3,3,13,-1075,-98,-8,,,,37,13,,*3FCF0C9B"));
        Assert.That(s, Does.EndWith("\r\n"));
    }

    [Test]
    public void Lat_lon_sur_oeste_van_en_valor_absoluto()
    {
        var g = Fix();
        g.LatNmea = -3359.9994600; g.NS = 'S';
        g.LonNmea = -6023.9997000; g.EW = 'W';
        string s = NmeaBuilder.BuildGga(g);
        Assert.That(s, Does.Contain(",3359.9994600,S,"));
        Assert.That(s, Does.Contain(",06023.9997000,W,"));
    }
}
```

- [ ] **Step 3: Correr los tests y verificar que fallan**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: FAIL — `NmeaBuilder` no existe (error de compilación CS0103/CS0246).

- [ ] **Step 4: Crear `SourceCode/BenchX/Sim/NmeaBuilder.cs`** (port fiel de `Controls.Designer.cs` — mismas constantes mágicas)

```csharp
using System.Globalization;
using System.Text;

namespace BenchX.Sim;

// Port fiel del generador NMEA de ModSim (Controls.Designer.cs). Las
// constantes raras (fix 8, 12 sats, "1000", "034.4,M", "444.232,3,1.2,17",
// "32,298", "230394,359.9") son las que SIEMPRE mandó ModSim y PilotX acepta:
// no "corregirlas", son parte del wire.
public static class NmeaBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // XOR de los chars entre '$' y '*', como hex de 2 dígitos.
    public static string Checksum(string sentencia)
    {
        int sum = 0;
        for (int i = 1; i < sentencia.Length && sentencia[i] != '*'; i++)
            sum ^= sentencia[i];
        return sum.ToString("X2", Inv);
    }

    private static string Cerrar(StringBuilder sb)
    {
        sb.Append(Checksum(sb.ToString()));
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static string Lat(GpsEstado g) => System.Math.Abs(g.LatNmea).ToString("0000.0000000", Inv);
    private static string LonAncho(GpsEstado g) => System.Math.Abs(g.LonNmea).ToString("00000.0000000", Inv);
    private static string LonCorto(GpsEstado g) => System.Math.Abs(g.LonNmea).ToString("0000.0000000", Inv);

    public static string BuildGga(GpsEstado g)
    {
        var sb = new StringBuilder("$GPGGA,");
        sb.Append(g.TimeNow)
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonAncho(g)).Append(',').Append(g.EW).Append(',')
          .Append("8,12,0.9,1000,M,46.9,M,37.1,,*");
        return Cerrar(sb);
    }

    public static string BuildVtg(GpsEstado g)
    {
        var sb = new StringBuilder("$GPVTG,");
        sb.Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",T,034.4,M,")
          .Append(g.SpeedKnots.ToString(Inv))
          .Append(",N,")
          .Append((g.SpeedKnots * 1.852).ToString(Inv))
          .Append(",K*");
        return Cerrar(sb);
    }

    public static string BuildHdt(GpsEstado g)
    {
        var sb = new StringBuilder("$GNHDT,");
        sb.Append(g.HeadingDeg.ToString("N5", Inv)).Append(",T*");
        return Cerrar(sb);
    }

    public static string BuildAvr(GpsEstado g)
    {
        var sb = new StringBuilder("$PTNL,AVR,");
        sb.Append(g.TimeNow)
          .Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",Yaw,-2.1,Tilt,")
          .Append(g.RollDeg.ToString(Inv)).Append(",Roll,")
          .Append("444.232,3,1.2,17*");
        return Cerrar(sb);
    }

    public static string BuildOgi(GpsEstado g)
    {
        var sb = new StringBuilder("$PAOGI,");
        sb.Append(g.TimeNow)
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonCorto(g)).Append(',').Append(g.EW).Append(',')
          .Append("8,12,0.9,1000,3.2,")
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.HeadingDeg.ToString("N5", Inv)).Append(',')
          .Append(g.RollDeg.ToString(Inv)).Append(",0.12,359.9,T*");
        return Cerrar(sb);
    }

    public static string BuildNda(GpsEstado g)
    {
        var sb = new StringBuilder("$PANDA,");
        sb.Append(g.TimeNow)
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonCorto(g)).Append(',').Append(g.EW).Append(',')
          .Append("8,12,0.9,1000,3.2,")
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.HeadingImu.ToString(Inv)).Append(',')
          .Append(g.RollImu.ToString(Inv)).Append(",32,298*");
        return Cerrar(sb);
    }

    public static string BuildRmc(GpsEstado g)
    {
        var sb = new StringBuilder("$GPRMC,");
        sb.Append(g.TimeNow).Append("A,")
          .Append(Lat(g)).Append(',').Append(g.NS).Append(',')
          .Append(LonCorto(g)).Append(',').Append(g.EW).Append(',')
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",230394,359.9*");
        return Cerrar(sb);
    }

    public static string BuildKsxt(GpsEstado g)
    {
        // ModSim nunca calculó el checksum del KSXT: manda el literal 3FCF0C9B.
        var sb = new StringBuilder("$KSXT,");
        sb.Append(g.TimeNow)
          .Append(g.Longitude.ToString("0000.0000000", Inv)).Append(',')
          .Append(g.Latitude.ToString("0000.0000000", Inv)).Append(',')
          .Append(g.Altitude.ToString(Inv)).Append(',')
          .Append(g.HeadingDeg.ToString("N5", Inv))
          .Append(",22,35,")
          .Append(g.SpeedKnots.ToString(Inv)).Append(',')
          .Append(g.RollDeg.ToString(Inv))
          .Append(",3,3,13,-1075,-98,-8,,,,37,13,,")
          .Append("*3FCF0C9B\r\n");
        return sb.ToString();
    }
}
```

- [ ] **Step 5: Correr los tests y verificar que pasan**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 9`

Nota si algún assert de formato falla: comparar contra el original en el commit borrado (`git show HEAD:SourceCode/ModSim/Source/Forms/Controls.Designer.cs` mientras exista) — la referencia es el WinForms, no el gusto propio. Ajustar la EXPECTATIVA solo si el original realmente produce otra cosa.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/BenchX/Sim SourceCode/BenchX.Tests
git commit -m "feat(benchx): NmeaBuilder — port fiel de las 8 sentencias de ModSim

GGA/VTG/HDT/AVR/PAOGI/PANDA/RMC/KSXT con las mismas constantes
históricas y checksum XOR. Todo InvariantCulture (el original tenía dos
ToString() sin cultura). Tests de caracterización por sentencia."
```

---

### Task 3: SimuladorVehiculo — cinemática del tractor

**Files:**
- Create: `SourceCode/BenchX/Sim/SimuladorVehiculo.cs`
- Create: `SourceCode/BenchX.Tests/SimuladorVehiculoTests.cs`

**Interfaces:**
- Consumes: `BenchX.Sim.GpsEstado` (Task 2).
- Produces: `BenchX.Sim.SimuladorVehiculo` con:
  - propiedades de entrada `double SpeedKmh, SteerAngleDeg, RollDeg` y de posición `double Latitude, Longitude, HeadingRad`;
  - `double HeadingDeg { get; }`, `double SpeedKnots { get; }`
  - `GpsEstado Estado { get; }` (se refresca en cada Avanzar; TimeNow NO se toca acá)
  - `void Avanzar()` — un tick de 100 ms.

- [ ] **Step 1: Escribir los tests que fallan — `SourceCode/BenchX.Tests/SimuladorVehiculoTests.cs`**

```csharp
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

public class SimuladorVehiculoTests
{
    private static SimuladorVehiculo Sim() => new()
    {
        Latitude = -33.0, Longitude = -60.0, HeadingRad = 0.0,
    };

    [Test]
    public void Derecho_al_norte_a_36kmh_avanza_1m_por_tick()
    {
        // 36 km/h = 10 m/s → 1 m por tick de 100 ms → ~8.9932e-6° de latitud (R=6371 km)
        var s = Sim();
        s.SpeedKmh = 36;
        s.Avanzar();
        Assert.That(s.Latitude, Is.EqualTo(-33.0 + 8.9932e-6).Within(1e-8));
        Assert.That(s.Longitude, Is.EqualTo(-60.0).Within(1e-9));
        Assert.That(s.HeadingRad, Is.EqualTo(0).Within(1e-12));
    }

    [Test]
    public void La_velocidad_nmea_sale_en_nudos_redondeada()
    {
        var s = Sim();
        s.SpeedKmh = 36;
        s.Avanzar();
        Assert.That(s.SpeedKnots, Is.EqualTo(19.4).Within(1e-9)); // 36 km/h ≈ 19.44 kn, round a 1 decimal
    }

    [Test]
    public void El_angulo_de_direccion_gira_el_rumbo_con_la_formula_de_modsim()
    {
        // heading += paso_m * tan(deg*0.02) / 2.5 — fórmula histórica de ModSim
        var s = Sim();
        s.SpeedKmh = 36; s.SteerAngleDeg = 30;
        s.Avanzar();
        Assert.That(s.HeadingRad, Is.EqualTo(System.Math.Tan(30 * 0.02) / 2.5).Within(1e-9));
    }

    [Test]
    public void El_rumbo_envuelve_en_2pi()
    {
        var s = Sim();
        s.HeadingRad = 6.28; s.SpeedKmh = 36; s.SteerAngleDeg = 30;
        s.Avanzar();
        Assert.That(s.HeadingRad, Is.LessThan(2 * System.Math.PI));
        Assert.That(s.HeadingRad, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void El_estado_gps_queda_listo_para_nmea()
    {
        var s = Sim();
        s.SpeedKmh = 36; s.RollDeg = -2.5;
        s.Avanzar();
        Assert.That(s.Estado.NS, Is.EqualTo('S'));
        Assert.That(s.Estado.EW, Is.EqualTo('W'));
        // -32.99999... → grados -32, minutos ≈ -59.99946
        Assert.That(System.Math.Abs(s.Estado.LatNmea), Is.EqualTo(3259.99946).Within(0.001));
        Assert.That(System.Math.Abs(s.Estado.LonNmea), Is.EqualTo(6000.0).Within(0.001));
        Assert.That(s.Estado.RollDeg, Is.EqualTo(-2.5));
        Assert.That(s.Estado.RollImu, Is.EqualTo(-25));
        Assert.That(s.Estado.HeadingImu, Is.EqualTo(0));
        Assert.That(s.Estado.TimeNow, Is.EqualTo("")); // la hora la pone el ViewModel
    }

    [Test]
    public void Marcha_atras_retrocede()
    {
        var s = Sim();
        s.SpeedKmh = -10;
        s.Avanzar();
        Assert.That(s.Latitude, Is.LessThan(-33.0));
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: FAIL — `SimuladorVehiculo` no existe.

- [ ] **Step 3: Crear `SourceCode/BenchX/Sim/SimuladorVehiculo.cs`** (port de `simTimer_Tick` + `CalculateNewPostionFromBearingDistance`)

```csharp
using System;

namespace BenchX.Sim;

// Cinemática del tractor simulado — port fiel del simTimer_Tick de ModSim.
// Un tick = 100 ms. La fórmula de giro (tan(deg*0.02)/2.5) no es física de
// verdad: es la histórica de ModSim y PilotX está afinado contra ella.
public sealed class SimuladorVehiculo
{
    private const double ToRadians = 0.01745329251994329576923690768489;
    private const double ToDegrees = 57.295779513082325225835265587528;

    public double Latitude, Longitude;   // grados
    public double HeadingRad;            // 0..2π
    public double SpeedKmh;              // entrada (slider)
    public double SteerAngleDeg;         // entrada (slider o setpoint de PilotX)
    public double RollDeg;               // entrada (slider)

    public double HeadingDeg => HeadingRad * ToDegrees;
    public double SpeedKnots { get; private set; }
    public GpsEstado Estado { get; } = new();

    public void Avanzar()
    {
        // metros por tick: kmh/3.6 * 0.1 s (en ModSim: tbar(=kmh*10)*0.02777*0.1)
        double paso = SpeedKmh * 0.027777777777;

        HeadingRad += paso * Math.Tan(SteerAngleDeg * 0.02) / 2.5;
        if (HeadingRad > 2.0 * Math.PI) HeadingRad -= 2.0 * Math.PI;
        if (HeadingRad < 0) HeadingRad += 2.0 * Math.PI;

        AvanzarPosicion(ToRadians * Latitude, ToRadians * Longitude, HeadingRad, paso / 1000.0);

        SpeedKnots = Math.Round(1.944 * paso / 0.1, 1);

        Estado.Latitude = Latitude;
        Estado.Longitude = Longitude;
        Estado.HeadingDeg = HeadingDeg;
        Estado.SpeedKnots = SpeedKnots;
        Estado.RollDeg = RollDeg;
        Estado.RollImu = (int)(RollDeg * 10);
        Estado.HeadingImu = (int)(HeadingDeg * 10);
    }

    // Port de CalculateNewPostionFromBearingDistance (distancia en km, R=6371).
    private void AvanzarPosicion(double lat, double lng, double rumbo, double distanciaKm)
    {
        double r = distanciaKm / 6371.0;

        double lat2 = Math.Asin((Math.Sin(lat) * Math.Cos(r)) + (Math.Cos(lat) * Math.Sin(r) * Math.Cos(rumbo)));
        double lon2 = lng + Math.Atan2(Math.Sin(rumbo) * Math.Sin(r) * Math.Cos(lat), Math.Cos(r) - (Math.Sin(lat) * Math.Sin(lat2)));

        Latitude = ToDegrees * lat2;
        Longitude = ToDegrees * lon2;

        double latMinu = Latitude, lonMinu = Longitude;
        double latDeg = (int)Latitude, lonDeg = (int)Longitude;
        latMinu -= latDeg; lonMinu -= lonDeg;
        latMinu = Math.Round(latMinu * 60.0, 7);
        lonMinu = Math.Round(lonMinu * 60.0, 7);

        Estado.LatNmea = latMinu + (latDeg * 100.0);
        Estado.LonNmea = lonMinu + (lonDeg * 100.0);
        Estado.NS = Latitude >= 0 ? 'N' : 'S';
        Estado.EW = Longitude >= 0 ? 'E' : 'W';
    }
}
```

- [ ] **Step 4: Correr y verificar que pasa**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 15`

- [ ] **Step 5: Commit**

```bash
git add SourceCode/BenchX/Sim/SimuladorVehiculo.cs SourceCode/BenchX.Tests/SimuladorVehiculoTests.cs
git commit -m "feat(benchx): SimuladorVehiculo — cinemática port de simTimer_Tick

Paso por tick, giro con la fórmula histórica tan(deg*0.02)/2.5,
posición por rumbo+distancia (R=6371) y armado del GpsEstado NMEA."
```

---

### Task 4: PgnProcessor — parseo y respuestas PGN

**Files:**
- Create: `SourceCode/BenchX/Sim/PgnProcessor.cs`
- Create: `SourceCode/BenchX.Tests/PgnProcessorTests.cs`

**Interfaces:**
- Produces: `BenchX.Sim.PgnProcessor`:
  - entrada del VM: `double SteerAngleActual; int WorkSwitch, SteerSwitch, RemoteSwitch` (todos arrancan en 1 = suelto, activo-bajo como ModSim); `byte Subred1, Subred2, Subred3` (para scan reply).
  - `Resultado Procesar(byte[] data)` — thread-safe de facto (se llama solo desde el hilo de red); `Resultado` tiene `List<byte[]> Respuestas` y `(byte S1, byte S2, byte S3)? NuevaSubred`.
  - estado recibido legible por el VM: `byte GuidanceStatus; double SteerAngleSetPoint, GpsSpeedPilotX; byte Relay, RelayHi, UTurn; double GpsSpeedMaquina; int HydLift, Tramline, RelayLoM, RelayHiM; byte[] Zonas` (8); `bool ScanRespondido;`
  - settings recibidos: `byte Kp, HighPwm, LowPwm, MinPwm; double SensorCounts; int WasOffset; double AckermanPct;` y flags `byte InvertWas, RelayActiveHigh, MotorDir, SingleInputWas, Cytron, SteerSwitchCfg, SteerButtonCfg, ShaftEncoder, PulseCountMax, Danfoss, PressureSensor, CurrentSensor, UseYAxis;`
  - máquina: `byte RaiseTime, LowerTime, EnableToolLift, RelayActiveHighM, User1, User2, User3, User4;`

- [ ] **Step 1: Escribir los tests que fallan — `SourceCode/BenchX.Tests/PgnProcessorTests.cs`**

```csharp
using System.Linq;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

public class PgnProcessorTests
{
    private static PgnProcessor Proc() => new() { Subred1 = 127, Subred2 = 255, Subred3 = 255 };

    // Trama PGN con header 0x80 0x81 0x7F + pgn + len + payload (sin checksum válido:
    // ModSim nunca validó el checksum entrante y BenchX mantiene eso).
    private static byte[] Trama(byte pgn, byte len, params byte[] payload)
    {
        var d = new byte[5 + payload.Length + 1];
        d[0] = 0x80; d[1] = 0x81; d[2] = 0x7F; d[3] = pgn; d[4] = len;
        payload.CopyTo(d, 5);
        return d;
    }

    private static void AssertCrc(byte[] r)
    {
        int ck = 0;
        for (int i = 2; i < r.Length - 1; i++) ck += r[i];
        Assert.That(r[^1], Is.EqualTo(unchecked((byte)ck)));
    }

    [Test]
    public void Pgn254_actualiza_guiado_y_responde_253_con_el_was()
    {
        var p = Proc();
        p.SteerAngleActual = 12.34;
        p.WorkSwitch = 0; p.SteerSwitch = 1; p.RemoteSwitch = 1;

        // speed=8.0 (80*0.1), guidance=1, setpoint=-5.00° (-500), xte=7, relay=0b101, relayHi=1
        short sp = -500;
        var res = p.Procesar(Trama(254, 8, 80, 0, 1, (byte)(sp & 0xFF), (byte)((sp >> 8) & 0xFF), 7, 0b101, 1, 0));

        Assert.That(p.GuidanceStatus, Is.EqualTo(1));
        Assert.That(p.SteerAngleSetPoint, Is.EqualTo(-5.0).Within(1e-6));
        Assert.That(p.GpsSpeedPilotX, Is.EqualTo(8.0).Within(1e-9));
        Assert.That(p.Relay, Is.EqualTo(0b101));
        Assert.That(p.RelayHi, Is.EqualTo(1));

        Assert.That(res.Respuestas, Has.Count.EqualTo(1));
        var r = res.Respuestas[0];
        Assert.That(r.Take(5), Is.EqualTo(new byte[] { 128, 129, 126, 253, 8 }));
        int sa = (short)(r[5] | (r[6] << 8));
        Assert.That(sa, Is.EqualTo(1234));                     // 12.34° * 100
        Assert.That(r[11], Is.EqualTo(0b110));                 // remote<<2 | steer<<1 | work
        Assert.That(r[12], Is.EqualTo(44));                    // pwmDisplay fijo histórico
        AssertCrc(r);
    }

    [Test]
    public void Pgn200_hello_responde_los_tres_modulos()
    {
        var p = Proc();
        p.SteerAngleActual = 1.0;
        var res = p.Procesar(Trama(200, 3, 56, 0, 0));
        Assert.That(res.Respuestas, Has.Count.EqualTo(3));
        Assert.That(res.Respuestas[0][3], Is.EqualTo(126));    // autosteer
        Assert.That(res.Respuestas[1][3], Is.EqualTo(123));    // machine
        Assert.That(res.Respuestas[2][3], Is.EqualTo(121));    // IMU
        int sa = (short)(res.Respuestas[0][5] | (res.Respuestas[0][6] << 8));
        Assert.That(sa, Is.EqualTo(100));
        // ModSim nunca recalculó el CRC de los hellos (viaja el 71 fijo): parity.
        Assert.That(res.Respuestas[0][^1], Is.EqualTo(71));
    }

    [Test]
    public void Pgn202_scan_responde_tres_scan_reply_con_la_subred()
    {
        var p = Proc();
        var res = p.Procesar(Trama(202, 3, 202, 202, 5));
        Assert.That(p.ScanRespondido, Is.True);
        Assert.That(res.Respuestas, Has.Count.EqualTo(3));
        foreach (var (r, modulo) in res.Respuestas.Zip(new byte[] { 126, 123, 121 }))
        {
            Assert.That(r[2], Is.EqualTo(modulo));
            Assert.That(r[3], Is.EqualTo(203));
            Assert.That(new[] { r[5], r[6], r[7] }, Is.EqualTo(new byte[] { 127, 255, 255 }));
            Assert.That(r[8], Is.EqualTo(modulo));
            AssertCrc(r);
        }
    }

    [Test]
    public void Pgn252_parsea_settings_de_direccion()
    {
        var p = Proc();
        // Kp=120, highPWM=160, lowPWM(ignorado, se recalcula)=30, minPWM=25,
        // counts=30, wasOffset=513 (1|2<<8), ackerman=95%
        p.Procesar(Trama(252, 8, 120, 160, 30, 25, 30, 1, 2, 95));
        Assert.That(p.Kp, Is.EqualTo(120));
        Assert.That(p.HighPwm, Is.EqualTo(160));
        Assert.That(p.MinPwm, Is.EqualTo(25));
        Assert.That(p.LowPwm, Is.EqualTo((byte)(25 * 1.2f)));  // ModSim pisa lowPWM con minPWM*1.2
        Assert.That(p.SensorCounts, Is.EqualTo(30));
        Assert.That(p.WasOffset, Is.EqualTo(513));
        Assert.That(p.AckermanPct, Is.EqualTo(95).Within(1e-6));
    }

    [Test]
    public void Pgn251_parsea_flags_de_config()
    {
        var p = Proc();
        // set0: invertWAS(b0)=1, relayHigh(b1)=0, motorDir(b2)=1, singleWAS(b3)=0,
        //       cytron(b4)=1, steerSwitch(b5)=0, steerButton(b6)=1, encoder(b7)=0 → 0b01010101
        // pulseMax=5, was_speed(ignorado)=0, set1: danfoss(b0)=1, presion(b1)=0, corriente(b2)=1, y-axis(b3)=0 → 0b0101
        p.Procesar(Trama(251, 8, 0b01010101, 5, 0, 0b0101));
        Assert.That(p.InvertWas, Is.EqualTo(1));
        Assert.That(p.RelayActiveHigh, Is.EqualTo(0));
        Assert.That(p.MotorDir, Is.EqualTo(1));
        Assert.That(p.SingleInputWas, Is.EqualTo(0));
        Assert.That(p.Cytron, Is.EqualTo(1));
        Assert.That(p.SteerSwitchCfg, Is.EqualTo(0));
        Assert.That(p.SteerButtonCfg, Is.EqualTo(1));
        Assert.That(p.ShaftEncoder, Is.EqualTo(0));
        Assert.That(p.PulseCountMax, Is.EqualTo(5));
        Assert.That(p.Danfoss, Is.EqualTo(1));
        Assert.That(p.PressureSensor, Is.EqualTo(0));
        Assert.That(p.CurrentSensor, Is.EqualTo(1));
        Assert.That(p.UseYAxis, Is.EqualTo(0));
    }

    [Test]
    public void Pgn239_parsea_datos_de_maquina()
    {
        var p = Proc();
        // uTurn=3, speed=25(→2.5), hydLift=2, tram=1, [9][10] libres, relayLo=0xF0, relayHi=0x0F
        p.Procesar(Trama(239, 8, 3, 25, 2, 1, 0, 0, 0xF0, 0x0F));
        Assert.That(p.UTurn, Is.EqualTo(3));
        Assert.That(p.GpsSpeedMaquina, Is.EqualTo(2.5).Within(1e-9));
        Assert.That(p.HydLift, Is.EqualTo(2));
        Assert.That(p.Tramline, Is.EqualTo(1));
        Assert.That(p.RelayLoM, Is.EqualTo(0xF0));
        Assert.That(p.RelayHiM, Is.EqualTo(0x0F));
    }

    [Test]
    public void Pgn229_guarda_las_8_zonas()
    {
        var p = Proc();
        p.Procesar(Trama(229, 8, 1, 2, 3, 4, 5, 6, 7, 8));
        Assert.That(p.Zonas, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Test]
    public void Pgn238_parsea_config_de_maquina()
    {
        var p = Proc();
        p.Procesar(Trama(238, 8, 2, 4, 1, 0b1, 11, 22, 33, 44));
        Assert.That(p.RaiseTime, Is.EqualTo(2));
        Assert.That(p.LowerTime, Is.EqualTo(4));
        Assert.That(p.EnableToolLift, Is.EqualTo(1));
        Assert.That(p.RelayActiveHighM, Is.EqualTo(1));
        Assert.That(p.User1, Is.EqualTo(11));
        Assert.That(p.User4, Is.EqualTo(44));
    }

    [Test]
    public void Pgn201_pide_cambio_de_subred_sin_respuestas()
    {
        var p = Proc();
        var res = p.Procesar(Trama(201, 5, 201, 201, 192, 168, 5));
        Assert.That(res.NuevaSubred, Is.EqualTo(((byte)192, (byte)168, (byte)5)));
        Assert.That(res.Respuestas, Is.Empty);
    }

    [Test]
    public void Header_invalido_o_trama_corta_se_ignoran()
    {
        var p = Proc();
        Assert.That(p.Procesar(new byte[] { 1, 2, 3 }).Respuestas, Is.Empty);
        Assert.That(p.Procesar(Trama(254, 8, 80)).Respuestas, Is.Empty); // payload corto → sin explotar
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: FAIL — `PgnProcessor` no existe.

- [ ] **Step 3: Crear `SourceCode/BenchX/Sim/PgnProcessor.cs`** (port de `UDP.designer.cs`, sin sockets)

```csharp
using System;
using System.Collections.Generic;

namespace BenchX.Sim;

// Port del ReceiveFromUDP de ModSim, sin sockets: entra la trama, salen las
// respuestas a mandar y el estado queda legible para la UI. Igual que ModSim,
// NO se valida el checksum entrante. Cualquier trama rota se ignora en silencio
// (el try/catch de afuera del original acá es el guard de longitud).
public sealed class PgnProcessor
{
    // --- lo setea el ViewModel (estado del vehículo simulado) ---
    public double SteerAngleActual;
    public int WorkSwitch = 1, SteerSwitch = 1, RemoteSwitch = 1; // activo-bajo, 1 = suelto
    public byte Subred1, Subred2, Subred3;

    // --- recibido de PilotX (PGN 254 / 239 / 229) ---
    public byte GuidanceStatus;
    public double SteerAngleSetPoint;
    public double GpsSpeedPilotX;
    public byte Xte, Relay, RelayHi;
    public byte UTurn;
    public double GpsSpeedMaquina;
    public int HydLift, Tramline, RelayLoM, RelayHiM;
    public byte[] Zonas { get; } = new byte[8];
    public bool ScanRespondido;

    // --- settings de dirección (PGN 252 / 251) ---
    public byte Kp = 120, HighPwm = 160, LowPwm = 30, MinPwm = 25;
    public double SensorCounts = 30;
    public int WasOffset;
    public double AckermanPct = 100;
    public byte InvertWas, RelayActiveHigh, MotorDir, SingleInputWas = 1, Cytron = 1,
                SteerSwitchCfg, SteerButtonCfg, ShaftEncoder, PulseCountMax = 5,
                Danfoss, PressureSensor, CurrentSensor, UseYAxis;

    // --- config de máquina (PGN 238) ---
    public byte RaiseTime = 2, LowerTime = 4, EnableToolLift, RelayActiveHighM,
                User1, User2, User3, User4;

    public sealed class Resultado
    {
        public List<byte[]> Respuestas { get; } = new();
        public (byte S1, byte S2, byte S3)? NuevaSubred;
    }

    public Resultado Procesar(byte[] data)
    {
        // OJO: los hellos (200) y el scan (202) son tramas de 9 bytes; el guard
        // general solo valida el header, la longitud se chequea por caso.
        var res = new Resultado();
        if (data.Length < 5 || data[0] != 0x80 || data[1] != 0x81 || data[2] != 0x7F)
            return res;

        switch (data[3])
        {
            case 254: // datos de guiado a 10 Hz
            {
                if (data.Length < 13) break;
                GpsSpeedPilotX = (data[5] | (data[6] << 8)) * 0.1;
                GuidanceStatus = data[7];
                SteerAngleSetPoint = (short)(data[8] | (data[9] << 8)) * 0.01;
                Xte = data[10];
                Relay = data[11];
                RelayHi = data[12];
                res.Respuestas.Add(ArmarPgn253());
                break;
            }
            case 252: // settings PID
            {
                if (data.Length < 13) break;
                Kp = data[5];
                HighPwm = data[6];
                MinPwm = data[8];
                LowPwm = (byte)(MinPwm * 1.2f); // ModSim pisa el lowPWM recibido
                SensorCounts = data[9];
                WasOffset = data[10] | (data[11] << 8);
                AckermanPct = data[12]; // se muestra *1 (el original guardaba *0.01 y mostraba *100)
                break;
            }
            case 251: // flags de config
            {
                if (data.Length < 13) break;
                int s0 = data[5];
                InvertWas       = (byte)((s0 >> 0) & 1);
                RelayActiveHigh = (byte)((s0 >> 1) & 1);
                MotorDir        = (byte)((s0 >> 2) & 1);
                SingleInputWas  = (byte)((s0 >> 3) & 1);
                Cytron          = (byte)((s0 >> 4) & 1);
                SteerSwitchCfg  = (byte)((s0 >> 5) & 1);
                SteerButtonCfg  = (byte)((s0 >> 6) & 1);
                ShaftEncoder    = (byte)((s0 >> 7) & 1);
                PulseCountMax = data[6];
                int s1 = data[8];
                Danfoss        = (byte)((s1 >> 0) & 1);
                PressureSensor = (byte)((s1 >> 1) & 1);
                CurrentSensor  = (byte)((s1 >> 2) & 1);
                UseYAxis       = (byte)((s1 >> 3) & 1);
                break;
            }
            case 200: // hello de CoreX → contestan los 3 módulos simulados
            {
                int sa = (int)(SteerAngleActual * 100);
                // El 71 final es el CRC congelado de ModSim (nunca lo recalculó): parity.
                var steer = new byte[] { 128, 129, 126, 126, 5,
                    unchecked((byte)sa), unchecked((byte)(sa >> 8)), 0, 0, (byte)SwitchByte(), 71 };
                var machine = new byte[] { 128, 129, 123, 123, 5,
                    (byte)RelayLoM, (byte)RelayHiM, 0, 0, 0, 71 };
                var imu = new byte[] { 128, 129, 121, 121, 5, 0, 0, 0, 0, 0, 71 };
                res.Respuestas.Add(steer);
                res.Respuestas.Add(machine);
                res.Respuestas.Add(imu);
                break;
            }
            case 201: // cambio de subred
            {
                if (data.Length < 10) break;
                if (data[4] == 5 && data[5] == 201 && data[6] == 201)
                    res.NuevaSubred = (data[7], data[8], data[9]);
                break;
            }
            case 202: // scan → un reply por módulo (steer/machine/imu)
            {
                if (data.Length < 7) break;
                if (data[4] == 3 && data[5] == 202 && data[6] == 202)
                {
                    ScanRespondido = true;
                    foreach (byte modulo in new byte[] { 126, 123, 121 })
                        res.Respuestas.Add(ArmarScanReply(modulo));
                }
                break;
            }
            case 239: // datos de máquina
            {
                if (data.Length < 13) break;
                UTurn = data[5];
                GpsSpeedMaquina = data[6] * 0.1;
                HydLift = data[7];
                Tramline = data[8];
                RelayLoM = data[11];
                RelayHiM = data[12];
                break;
            }
            case 229: // zonas de secciones
            {
                if (data.Length < 13) break;
                for (int i = 0; i < 8; i++) Zonas[i] = data[5 + i];
                break;
            }
            case 238: // config de máquina
            {
                if (data.Length < 13) break;
                RaiseTime = data[5];
                LowerTime = data[6];
                EnableToolLift = data[7];
                RelayActiveHighM = (byte)(data[8] & 1);
                User1 = data[9]; User2 = data[10]; User3 = data[11]; User4 = data[12];
                break;
            }
        }
        return res;
    }

    private int SwitchByte() => (RemoteSwitch << 2) | (SteerSwitch << 1) | WorkSwitch;

    // PGN 253: el "estado del autosteer" que PilotX espera de vuelta a 10 Hz.
    private byte[] ArmarPgn253()
    {
        int sa = (int)(SteerAngleActual * 100);
        var r = new byte[] { 128, 129, 126, 253, 8,
            unchecked((byte)sa), unchecked((byte)(sa >> 8)),
            unchecked((byte)9999), unchecked((byte)(9999 >> 8)),   // heading dummy histórico
            unchecked((byte)8888), unchecked((byte)(8888 >> 8)),   // roll dummy histórico
            (byte)SwitchByte(), 44,                                // 44 = pwmDisplay congelado
            0 };
        return ConCrc(r);
    }

    private byte[] ArmarScanReply(byte modulo)
    {
        var r = new byte[] { 128, 129, modulo, 203, 7,
            Subred1, Subred2, Subred3, modulo,
            Subred1, Subred2, Subred3, 0 };
        return ConCrc(r);
    }

    // CRC de PGN: suma de bytes [2..n-2] en el último byte.
    private static byte[] ConCrc(byte[] r)
    {
        int ck = 0;
        for (int i = 2; i < r.Length - 1; i++) ck += r[i];
        r[^1] = unchecked((byte)ck);
        return r;
    }
}
```

- [ ] **Step 4: Correr y verificar que pasa**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 25`

Nota: el scan reply original ponía `126`/`23` como placeholders en las posiciones 8/12 antes de pisarlas; el resultado final en el wire es el que fijan los tests (módulo repetido + CRC calculado) — verificado contra el flujo del original.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/BenchX/Sim/PgnProcessor.cs SourceCode/BenchX.Tests/PgnProcessorTests.cs
git commit -m "feat(benchx): PgnProcessor — PGN de ModSim sin sockets

Parsea 254/252/251/200/201/202/239/229/238 y arma 253, hellos y scan
reply con los mismos bytes que ModSim (incluidos los dummies 9999/8888,
el pwm 44 y el CRC 71 congelado de los hellos). Tests con tramas reales."
```

---

### Task 5: BenchXConfig — configuración JSON

**Files:**
- Create: `SourceCode/BenchX/Config/BenchXConfig.cs`
- Create: `SourceCode/BenchX.Tests/BenchXConfigTests.cs`

**Interfaces:**
- Produces: `BenchX.Config.BenchXConfig` con propiedades `byte Subred1 (127), Subred2 (255), Subred3 (255); double Latitud (53.4360564), Longitud (-111.160047); bool Gga, Vtg, Avr, Hdt, Rmc, Ogi, Nda (true), Ksxt;` y:
  - `static BenchXConfig Cargar(string ruta)` — ausente o corrupto → defaults y reescribe el archivo.
  - `void Guardar(string ruta)` — JSON indentado snake_case.
  - `static string RutaDefault` — `benchx.json` junto al exe (`AppContext.BaseDirectory`).

- [ ] **Step 1: Escribir los tests que fallan — `SourceCode/BenchX.Tests/BenchXConfigTests.cs`**

```csharp
using System.IO;
using BenchX.Config;
using NUnit.Framework;

namespace BenchX.Tests;

public class BenchXConfigTests
{
    private string _dir = "";

    [SetUp]
    public void CrearDir()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchx-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void BorrarDir() => Directory.Delete(_dir, true);

    private string Ruta => Path.Combine(_dir, "benchx.json");

    [Test]
    public void Sin_archivo_devuelve_defaults_de_banco_y_lo_crea()
    {
        var c = BenchXConfig.Cargar(Ruta);
        Assert.That((c.Subred1, c.Subred2, c.Subred3), Is.EqualTo(((byte)127, (byte)255, (byte)255)));
        Assert.That(c.Nda, Is.True);   // PANDA es la sentencia que usa PilotX
        Assert.That(c.Gga, Is.False);
        Assert.That(File.Exists(Ruta), Is.True);
    }

    [Test]
    public void Roundtrip_guardar_y_cargar()
    {
        var c = new BenchXConfig { Subred1 = 192, Subred2 = 168, Subred3 = 5, Latitud = -33.5, Longitud = -60.1, Gga = true, Nda = false };
        c.Guardar(Ruta);
        var c2 = BenchXConfig.Cargar(Ruta);
        Assert.That((c2.Subred1, c2.Subred2, c2.Subred3), Is.EqualTo(((byte)192, (byte)168, (byte)5)));
        Assert.That(c2.Latitud, Is.EqualTo(-33.5));
        Assert.That(c2.Gga, Is.True);
        Assert.That(c2.Nda, Is.False);
    }

    [Test]
    public void El_json_va_en_snake_case()
    {
        new BenchXConfig().Guardar(Ruta);
        Assert.That(File.ReadAllText(Ruta), Does.Contain("\"subred1\""));
    }

    [Test]
    public void Corrupto_vuelve_a_defaults_y_reescribe()
    {
        File.WriteAllText(Ruta, "{esto no es json");
        var c = BenchXConfig.Cargar(Ruta);
        Assert.That(c.Subred1, Is.EqualTo(127));
        Assert.That(File.ReadAllText(Ruta), Does.Contain("subred1"));
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: FAIL — `BenchXConfig` no existe.

- [ ] **Step 3: Crear `SourceCode/BenchX/Config/BenchXConfig.cs`**

```csharp
using System;
using System.IO;
using System.Text.Json;

namespace BenchX.Config;

// Config en JSON junto al exe. Reemplaza al Properties.Settings del WinForms,
// que perdió la subred al migrar de versión (v1.0.25) — un archivo visible y
// versionable no tiene ese problema.
public sealed class BenchXConfig
{
    public byte Subred1 { get; set; } = 127;   // default banco loopback
    public byte Subred2 { get; set; } = 255;
    public byte Subred3 { get; set; } = 255;
    public double Latitud { get; set; } = 53.4360564;   // arranque histórico de ModSim
    public double Longitud { get; set; } = -111.160047;
    public bool Gga { get; set; }
    public bool Vtg { get; set; }
    public bool Avr { get; set; }
    public bool Hdt { get; set; }
    public bool Rmc { get; set; }
    public bool Ogi { get; set; }
    public bool Nda { get; set; } = true;      // PANDA: la que usa PilotX
    public bool Ksxt { get; set; }

    public static string RutaDefault => Path.Combine(AppContext.BaseDirectory, "benchx.json");

    private static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static BenchXConfig Cargar(string ruta)
    {
        BenchXConfig config;
        try
        {
            config = JsonSerializer.Deserialize<BenchXConfig>(File.ReadAllText(ruta), Opciones)
                     ?? new BenchXConfig();
        }
        catch
        {
            // ausente o corrupto: defaults, y se reescribe para que quede sano
            config = new BenchXConfig();
        }
        try { config.Guardar(ruta); } catch { /* disco de solo lectura: se sigue en memoria */ }
        return config;
    }

    public void Guardar(string ruta) =>
        File.WriteAllText(ruta, JsonSerializer.Serialize(this, Opciones));
}
```

- [ ] **Step 4: Correr y verificar que pasa**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 29`

- [ ] **Step 5: Commit**

```bash
git add SourceCode/BenchX/Config SourceCode/BenchX.Tests/BenchXConfigTests.cs
git commit -m "feat(benchx): config JSON junto al exe con defaults de banco

benchx.json snake_case: subred 127.255.255 (loopback), PANDA activa.
Corrupto o ausente cae a defaults y se reescribe."
```

---

### Task 6: UdpLink — socket con escucha inmortal

**Files:**
- Create: `SourceCode/BenchX/Red/UdpLink.cs`
- Create: `SourceCode/BenchX.Tests/UdpLinkTests.cs`

**Interfaces:**
- Produces: `BenchX.Red.UdpLink : IDisposable`:
  - `UdpLink(byte s1, byte s2, byte s3, int puertoEscucha = 8888, int puertoDestino = 9999)` — bind a `0.0.0.0:puertoEscucha`, destino broadcast `s1.s2.s3.255:puertoDestino`. Los puertos parametrizados existen SOLO para los tests (efímeros); producción usa los defaults.
  - `event Action<byte[]>? DatagramaRecibido` — dispara en el hilo de red; el consumidor marshalea.
  - `void Enviar(byte[] datos)` / `void Enviar(string texto)` (ASCII).
  - `long Tx, Rx` — contadores; `string? ErrorBind` (null = ok); `bool Conectado => ErrorBind == null`.

- [ ] **Step 1: Escribir los tests que fallan — `SourceCode/BenchX.Tests/UdpLinkTests.cs`**

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BenchX.Red;
using NUnit.Framework;

namespace BenchX.Tests;

public class UdpLinkTests
{
    // Puertos altos poco probables; si algo los tiene tomados el test de bind lo delata.
    private const int Escucha = 48888;
    private const int Destino = 49999;

    [Test]
    public void Envia_broadcast_por_loopback_y_recibe()
    {
        // BenchX manda a 127.255.255.255:destino; un socket que escucha en
        // 0.0.0.0:destino lo recibe (mismo esquema que el banco loopback real).
        using var receptor = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receptor.Bind(new IPEndPoint(IPAddress.Any, Destino));
        receptor.ReceiveTimeout = 3000;

        using var link = new UdpLink(127, 255, 255, Escucha, Destino);
        Assert.That(link.Conectado, Is.True, link.ErrorBind);
        link.Enviar("$GPGGA,prueba*00\r\n");

        var buf = new byte[256];
        int n = receptor.Receive(buf);
        Assert.That(System.Text.Encoding.ASCII.GetString(buf, 0, n), Does.StartWith("$GPGGA"));
        Assert.That(link.Tx, Is.EqualTo(1));
    }

    [Test]
    public void Recibe_datagramas_en_el_puerto_de_escucha()
    {
        using var link = new UdpLink(127, 255, 255, Escucha, Destino);
        byte[]? recibido = null;
        using var señal = new ManualResetEventSlim();
        link.DatagramaRecibido += d => { recibido = d; señal.Set(); };

        using var emisor = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        emisor.SendTo(new byte[] { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, 0x47 },
            new IPEndPoint(IPAddress.Loopback, Escucha));

        Assert.That(señal.Wait(3000), Is.True, "no llegó el datagrama");
        Assert.That(recibido, Is.Not.Null);
        Assert.That(recibido![3], Is.EqualTo(200));
        Assert.That(link.Rx, Is.EqualTo(1));
    }

    [Test]
    public void Puerto_tomado_deja_ErrorBind_sin_tirar_excepcion()
    {
        using var primero = new UdpLink(127, 255, 255, Escucha, Destino);
        using var segundo = new UdpLink(127, 255, 255, Escucha, Destino);
        Assert.That(primero.Conectado, Is.True);
        Assert.That(segundo.Conectado, Is.False);
        Assert.That(segundo.ErrorBind, Is.Not.Null.And.Not.Empty);
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: FAIL — `UdpLink` no existe.

- [ ] **Step 3: Crear `SourceCode/BenchX/Red/UdpLink.cs`**

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace BenchX.Red;

// Socket UDP del simulador: escucha :8888 y manda broadcast a <subred>.255:9999.
//
// Dos lecciones heredadas que esta clase NO puede perder:
// 1) La escucha es INMORTAL. En el ModSim original una sola excepción (el
//    clásico WSAECONNRESET 10054 — ICMP port-unreachable cuando PilotX está
//    caído) mataba el BeginReceiveFrom y ModSim quedaba sordo para siempre
//    (2026-08-05). Acá el while re-arma pase lo que pase; solo termina con
//    el socket dispuesto de verdad.
// 2) Nada de BeginReceiveFrom en net9: el callback que completa sincrónico
//    recurre y vuela el stack (lección 0e90d4ca en el engine). Bucle async.
public sealed class UdpLink : IDisposable
{
    private readonly Socket? _socket;
    private readonly IPEndPoint _destino = new(IPAddress.None, 0);

    public event Action<byte[]>? DatagramaRecibido;   // hilo de red: el consumidor marshalea
    public long Tx, Rx;
    public string? ErrorBind { get; }
    public bool Conectado => ErrorBind == null;

    public UdpLink(byte s1, byte s2, byte s3, int puertoEscucha = 8888, int puertoDestino = 9999)
    {
        try
        {
            _destino = new IPEndPoint(IPAddress.Parse($"{s1}.{s2}.{s3}.255"), puertoDestino);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, puertoEscucha));
            _ = Task.Run(BucleRecepcion);
        }
        catch (Exception ex)
        {
            ErrorBind = ex.Message;
            _socket?.Dispose();
            _socket = null;
        }
    }

    private async Task BucleRecepcion()
    {
        var buffer = new byte[1024];
        EndPoint remoto = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            try
            {
                var r = await _socket!.ReceiveFromAsync(buffer, SocketFlags.None, remoto);
                if (r.ReceivedBytes > 0)
                {
                    var datos = new byte[r.ReceivedBytes];
                    Array.Copy(buffer, datos, r.ReceivedBytes);
                    Rx++;
                    DatagramaRecibido?.Invoke(datos);
                }
            }
            catch (ObjectDisposedException) { return; }          // Dispose real: fin
            catch (SocketException) { /* 10054 y afines: seguir escuchando */ }
            catch (Exception) { await Task.Delay(200); }         // raro: respirar y seguir
        }
    }

    public void Enviar(byte[] datos)
    {
        if (_socket == null || datos.Length == 0) return;
        try { _socket.SendTo(datos, _destino); Tx++; }
        catch { /* red caída: el próximo tick reintenta solo */ }
    }

    public void Enviar(string texto) => Enviar(Encoding.ASCII.GetBytes(texto));

    public void Dispose() => _socket?.Dispose();
}
```

- [ ] **Step 4: Correr y verificar que pasa**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 32`

- [ ] **Step 5: Commit**

```bash
git add SourceCode/BenchX/Red SourceCode/BenchX.Tests/UdpLinkTests.cs
git commit -m "feat(benchx): UdpLink — escucha :8888 inmortal, broadcast a subred.255:9999

Bucle async (nada de BeginReceiveFrom en net9) con catch-que-continúa:
el 10054 de PilotX caído no vuelve a dejar sordo al simulador. Bind
fallado queda en ErrorBind para la card Red, sin tirar la app."
```

---

### Task 7: MainViewModel — el hilo que une todo

**Files:**
- Create: `SourceCode/BenchX/MainViewModel.cs`

**Interfaces:**
- Consumes: `SimuladorVehiculo`, `NmeaBuilder`, `PgnProcessor`, `UdpLink`, `BenchXConfig` (Tasks 2-6).
- Produces: `BenchX.MainViewModel : INotifyPropertyChanged` — propiedades que la Task 8 bindea (nombres EXACTOS):
  - sliders two-way: `double VelocidadKmh, AnguloDireccion, Roll`
  - textos live: `string VelocidadTexto, MSegTexto, AnguloTexto, RollTexto, RumboTexto, LatActualTexto, LonActualTexto`
  - toggles NMEA two-way: `bool Gga, Vtg, Avr, Hdt, Rmc, Ogi, Nda, Ksxt`
  - posición inicial: `string LatInicial, LonInicial` + método `GuardarPosicion()`
  - switches: `bool SwitchTrabajo, SwitchDireccion` + método `BotonDireccionRemoto()`
  - dirección: `bool GuiadoActivo`; `string GuiadoTexto` ("guiado activo"/"guiado inactivo"); `string SetPointTexto, VelPilotXTexto, KpTexto, HighPwmTexto, LowPwmTexto, MinPwmTexto, CountsTexto, OffsetTexto, AckermanTexto, FlagsTexto`
  - máquina: `string RelesDireccion, RelesMaquina, Zona1..Zona8, UTurnTexto, HydLiftTexto, TramTexto, VelMaquinaTexto`
  - red: `string IpsLocales, SubredTexto, RxTexto, TxTexto`; `bool ScanRespondido, RedOk`; `string ScanTexto` ("respondido"/"sin responder"); `string? ErrorRed`
  - métodos de ciclo de vida: `void CeroVelocidad()`, `void CeroAngulo()`, `void CeroRoll()`, `void Cerrar()` (guarda config y dispone el link)

- [ ] **Step 1: Crear `SourceCode/BenchX/MainViewModel.cs`**

```csharp
using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using BenchX.Config;
using BenchX.Red;
using BenchX.Sim;

namespace BenchX;

// Une simulador + NMEA + PGN + UDP + config con la ventana. Regla de oro:
// el socket dispara en su hilo; TODO lo que toque propiedades bindeadas pasa
// por el tick del DispatcherTimer o por Dispatcher.UIThread.Post.
public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly BenchXConfig _config;
    private readonly SimuladorVehiculo _sim = new();
    private readonly PgnProcessor _pgn = new();
    private readonly UdpLink _link;
    private readonly DispatcherTimer _timer;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notificar([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // Pide reinicio con la subred nueva (PGN 201) — lo cablea MainWindow.
    public event Action<string>? ReinicioPedido;

    public MainViewModel() : this(BenchXConfig.RutaDefault) { }

    public MainViewModel(string rutaConfig)
    {
        _rutaConfig = rutaConfig;
        _config = BenchXConfig.Cargar(rutaConfig);
        _sim.Latitude = _config.Latitud;
        _sim.Longitude = _config.Longitud;
        _pgn.Subred1 = _config.Subred1; _pgn.Subred2 = _config.Subred2; _pgn.Subred3 = _config.Subred3;

        Gga = _config.Gga; Vtg = _config.Vtg; Avr = _config.Avr; Hdt = _config.Hdt;
        Rmc = _config.Rmc; Ogi = _config.Ogi; Nda = _config.Nda; Ksxt = _config.Ksxt;
        LatInicial = _config.Latitud.ToString("N7", Inv);
        LonInicial = _config.Longitud.ToString("N7", Inv);

        _link = new UdpLink(_config.Subred1, _config.Subred2, _config.Subred3);
        _link.DatagramaRecibido += AlRecibir;

        IpsLocales = LeerIpsLocales();
        SubredTexto = $"{_config.Subred1}.{_config.Subred2}.{_config.Subred3}.255:9999";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private readonly string _rutaConfig;

    // ------------------------- sliders / entradas -------------------------

    private double _velocidadKmh, _anguloDireccion, _roll;
    public double VelocidadKmh { get => _velocidadKmh; set { _velocidadKmh = value; Notificar(); Notificar(nameof(VelocidadTexto)); Notificar(nameof(MSegTexto)); } }
    public double AnguloDireccion { get => _anguloDireccion; set { _anguloDireccion = value; Notificar(); Notificar(nameof(AnguloTexto)); } }
    public double Roll { get => _roll; set { _roll = value; Notificar(); Notificar(nameof(RollTexto)); } }

    public string VelocidadTexto => VelocidadKmh.ToString("N1", Inv) + " km/h";
    public string MSegTexto => (VelocidadKmh * 0.27777777).ToString("N1", Inv) + " m/s";
    public string AnguloTexto => AnguloDireccion.ToString("N2", Inv) + "°";
    public string RollTexto => Roll.ToString("N2", Inv) + "°";

    public void CeroVelocidad() => VelocidadKmh = 0;
    public void CeroAngulo() => AnguloDireccion = 0;
    public void CeroRoll() => Roll = 0;

    public bool Gga { get; set; } public bool Vtg { get; set; } public bool Avr { get; set; }
    public bool Hdt { get; set; } public bool Rmc { get; set; } public bool Ogi { get; set; }
    public bool Nda { get; set; } public bool Ksxt { get; set; }

    private string _latInicial = "", _lonInicial = "";
    public string LatInicial { get => _latInicial; set { _latInicial = value; Notificar(); } }
    public string LonInicial { get => _lonInicial; set { _lonInicial = value; Notificar(); } }

    public void GuardarPosicion()
    {
        if (double.TryParse(LatInicial, NumberStyles.Float, Inv, out double lat) &&
            double.TryParse(LonInicial, NumberStyles.Float, Inv, out double lon) &&
            Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
        {
            _sim.Latitude = lat; _sim.Longitude = lon;
            _config.Latitud = lat; _config.Longitud = lon;
            _config.Guardar(_rutaConfig);
        }
    }

    // Switches activo-bajo (igual que ModSim): prendido en UI = 0 en el wire.
    private bool _switchTrabajo, _switchDireccion;
    public bool SwitchTrabajo { get => _switchTrabajo; set { _switchTrabajo = value; _pgn.WorkSwitch = value ? 0 : 1; Notificar(); } }
    public bool SwitchDireccion { get => _switchDireccion; set { _switchDireccion = value; _pgn.SteerSwitch = value ? 0 : 1; Notificar(); } }
    public void BotonDireccionRemoto() => _pgn.SteerSwitch = _pgn.SteerSwitch > 0 ? 0 : 1;

    // ------------------------- lecturas live -------------------------

    public string RumboTexto { get; private set; } = "0.00°";
    public string LatActualTexto { get; private set; } = "";
    public string LonActualTexto { get; private set; } = "";

    public bool GuiadoActivo { get; private set; }
    public string GuiadoTexto { get; private set; } = "guiado inactivo";
    public string SetPointTexto { get; private set; } = "—";
    public string VelPilotXTexto { get; private set; } = "—";
    public string KpTexto { get; private set; } = "—";
    public string HighPwmTexto { get; private set; } = "—";
    public string LowPwmTexto { get; private set; } = "—";
    public string MinPwmTexto { get; private set; } = "—";
    public string CountsTexto { get; private set; } = "—";
    public string OffsetTexto { get; private set; } = "—";
    public string AckermanTexto { get; private set; } = "—";
    public string FlagsTexto { get; private set; } = "—";

    public string RelesDireccion { get; private set; } = Puntos(0, 16);
    public string RelesMaquina { get; private set; } = Puntos(0, 16);
    public string Zona1 { get; private set; } = Puntos(0, 8);
    public string Zona2 { get; private set; } = Puntos(0, 8);
    public string Zona3 { get; private set; } = Puntos(0, 8);
    public string Zona4 { get; private set; } = Puntos(0, 8);
    public string Zona5 { get; private set; } = Puntos(0, 8);
    public string Zona6 { get; private set; } = Puntos(0, 8);
    public string Zona7 { get; private set; } = Puntos(0, 8);
    public string Zona8 { get; private set; } = Puntos(0, 8);
    public string UTurnTexto { get; private set; } = "—";
    public string HydLiftTexto { get; private set; } = "—";
    public string TramTexto { get; private set; } = "—";
    public string VelMaquinaTexto { get; private set; } = "—";

    public string IpsLocales { get; }
    public string SubredTexto { get; }
    public string RxTexto { get; private set; } = "0";
    public string TxTexto { get; private set; } = "0";
    public bool ScanRespondido { get; private set; }
    public string ScanTexto { get; private set; } = "sin responder";
    public string? ErrorRed => _link.ErrorBind;
    public bool RedOk => _link.Conectado;

    // ------------------------- ciclo -------------------------

    private void Tick()
    {
        // Con guiado activo el volante lo maneja PilotX: el slider sigue al setpoint.
        if (_pgn.GuidanceStatus != 0)
            AnguloDireccion = _pgn.SteerAngleSetPoint;

        _sim.SpeedKmh = VelocidadKmh;
        _sim.SteerAngleDeg = AnguloDireccion;
        _sim.RollDeg = Roll;
        _sim.Estado.TimeNow = DateTime.UtcNow.ToString("HHmmss.fff,", Inv);
        _sim.Avanzar();
        _pgn.SteerAngleActual = _sim.SteerAngleDeg;

        var g = _sim.Estado;
        if (Vtg) _link.Enviar(NmeaBuilder.BuildVtg(g));
        if (Avr) _link.Enviar(NmeaBuilder.BuildAvr(g));
        if (Hdt) _link.Enviar(NmeaBuilder.BuildHdt(g));
        if (Gga) _link.Enviar(NmeaBuilder.BuildGga(g));
        if (Rmc) _link.Enviar(NmeaBuilder.BuildRmc(g));
        if (Ogi) _link.Enviar(NmeaBuilder.BuildOgi(g));
        if (Nda) _link.Enviar(NmeaBuilder.BuildNda(g));
        if (Ksxt) _link.Enviar(NmeaBuilder.BuildKsxt(g));

        RefrescarLecturas();
    }

    private void RefrescarLecturas()
    {
        RumboTexto = _sim.HeadingDeg.ToString("N2", Inv) + "°";
        LatActualTexto = _sim.Latitude.ToString("N7", Inv);
        LonActualTexto = _sim.Longitude.ToString("N7", Inv);

        GuiadoActivo = _pgn.GuidanceStatus != 0;
        GuiadoTexto = GuiadoActivo ? "guiado activo" : "guiado inactivo";
        SetPointTexto = _pgn.SteerAngleSetPoint.ToString("N2", Inv) + "°";
        VelPilotXTexto = _pgn.GpsSpeedPilotX.ToString("N1", Inv) + " km/h";
        KpTexto = _pgn.Kp.ToString(Inv);
        HighPwmTexto = _pgn.HighPwm.ToString(Inv);
        LowPwmTexto = _pgn.LowPwm.ToString(Inv);
        MinPwmTexto = _pgn.MinPwm.ToString(Inv);
        CountsTexto = _pgn.SensorCounts.ToString(Inv);
        OffsetTexto = _pgn.WasOffset.ToString(Inv);
        AckermanTexto = _pgn.AckermanPct.ToString("N0", Inv) + "%";
        FlagsTexto =
            $"InvertWAS {_pgn.InvertWas} · RelayAlto {_pgn.RelayActiveHigh} · Motor {_pgn.MotorDir} · " +
            $"WAS único {_pgn.SingleInputWas} · Cytron {_pgn.Cytron} · Switch {_pgn.SteerSwitchCfg} · " +
            $"Botón {_pgn.SteerButtonCfg} · Encoder {_pgn.ShaftEncoder} · Danfoss {_pgn.Danfoss}";

        RelesDireccion = Puntos(_pgn.Relay | (_pgn.RelayHi << 8), 16);
        RelesMaquina = Puntos(_pgn.RelayLoM | (_pgn.RelayHiM << 8), 16);
        Zona1 = Puntos(_pgn.Zonas[0], 8); Zona2 = Puntos(_pgn.Zonas[1], 8);
        Zona3 = Puntos(_pgn.Zonas[2], 8); Zona4 = Puntos(_pgn.Zonas[3], 8);
        Zona5 = Puntos(_pgn.Zonas[4], 8); Zona6 = Puntos(_pgn.Zonas[5], 8);
        Zona7 = Puntos(_pgn.Zonas[6], 8); Zona8 = Puntos(_pgn.Zonas[7], 8);
        UTurnTexto = _pgn.UTurn.ToString(Inv);
        HydLiftTexto = _pgn.HydLift.ToString(Inv);
        TramTexto = _pgn.Tramline.ToString(Inv);
        VelMaquinaTexto = _pgn.GpsSpeedMaquina.ToString("N1", Inv) + " km/h";

        RxTexto = _link.Rx.ToString(Inv);
        TxTexto = _link.Tx.ToString(Inv);
        ScanRespondido = _pgn.ScanRespondido;
        ScanTexto = ScanRespondido ? "respondido" : "sin responder";

        Notificar(nameof(RumboTexto)); Notificar(nameof(LatActualTexto)); Notificar(nameof(LonActualTexto));
        Notificar(nameof(GuiadoActivo)); Notificar(nameof(GuiadoTexto));
        Notificar(nameof(SetPointTexto)); Notificar(nameof(VelPilotXTexto));
        Notificar(nameof(KpTexto)); Notificar(nameof(HighPwmTexto)); Notificar(nameof(LowPwmTexto));
        Notificar(nameof(MinPwmTexto)); Notificar(nameof(CountsTexto)); Notificar(nameof(OffsetTexto));
        Notificar(nameof(AckermanTexto)); Notificar(nameof(FlagsTexto));
        Notificar(nameof(RelesDireccion)); Notificar(nameof(RelesMaquina));
        Notificar(nameof(Zona1)); Notificar(nameof(Zona2)); Notificar(nameof(Zona3)); Notificar(nameof(Zona4));
        Notificar(nameof(Zona5)); Notificar(nameof(Zona6)); Notificar(nameof(Zona7)); Notificar(nameof(Zona8));
        Notificar(nameof(UTurnTexto)); Notificar(nameof(HydLiftTexto)); Notificar(nameof(TramTexto));
        Notificar(nameof(VelMaquinaTexto)); Notificar(nameof(RxTexto)); Notificar(nameof(TxTexto));
        Notificar(nameof(ScanRespondido)); Notificar(nameof(ScanTexto));
    }

    // Hilo de red: procesar y responder acá mismo (rápido), UI ni tocarla.
    private void AlRecibir(byte[] datos)
    {
        var res = _pgn.Procesar(datos);
        foreach (var r in res.Respuestas) _link.Enviar(r);

        if (res.NuevaSubred is { } s)
        {
            _config.Subred1 = s.S1; _config.Subred2 = s.S2; _config.Subred3 = s.S3;
            _config.Guardar(_rutaConfig);
            Dispatcher.UIThread.Post(() =>
                ReinicioPedido?.Invoke($"PilotX cambió la subred a {s.S1}.{s.S2}.{s.S3}. BenchX se reinicia para aplicarla."));
        }
    }

    public void Cerrar()
    {
        _timer.Stop();
        _config.Gga = Gga; _config.Vtg = Vtg; _config.Avr = Avr; _config.Hdt = Hdt;
        _config.Rmc = Rmc; _config.Ogi = Ogi; _config.Nda = Nda; _config.Ksxt = Ksxt;
        try { _config.Guardar(_rutaConfig); } catch { }
        _link.Dispose();
    }

    // bit 0 = sección 1, a la izquierda (mismo orden visual que el swapBits del original)
    private static string Puntos(int valor, int bits)
    {
        var sb = new StringBuilder(bits + 1);
        for (int i = 0; i < bits; i++)
        {
            if (i == 8) sb.Append(' ');
            sb.Append(((valor >> i) & 1) != 0 ? '●' : '○');
        }
        return sb.ToString();
    }

    private static string LeerIpsLocales()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    sb.AppendLine(ip.ToString());
            return sb.ToString().TrimEnd();
        }
        catch { return "—"; }
    }
}
```

- [ ] **Step 2: Compilar**

```powershell
dotnet build SourceCode\BenchX\BenchX.csproj -v q
```
Expected: `Build succeeded`.

- [ ] **Step 3: Correr los tests (nada debe romperse)**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed! - Failed: 0, Passed: 32`

- [ ] **Step 4: Commit**

```bash
git add SourceCode/BenchX/MainViewModel.cs
git commit -m "feat(benchx): MainViewModel — timer 10 Hz que une sim, NMEA, PGN y UDP

Guiado activo → el WAS sigue el setpoint de PilotX. Recepción en hilo
de red responde ahí mismo; la UI solo se toca en el tick o por
Dispatcher. PGN 201 guarda subred en JSON y pide reinicio."
```

---

### Task 8: MainWindow — UI de cards Agro Parallel

**Files:**
- Modify: `SourceCode/BenchX/MainWindow.axaml` (reemplazo completo del shell)
- Modify: `SourceCode/BenchX/MainWindow.axaml.cs` (reemplazo completo)

**Interfaces:**
- Consumes: TODAS las propiedades/métodos de `MainViewModel` con los nombres exactos de la Task 7.

- [ ] **Step 1: Reemplazar `SourceCode/BenchX/MainWindow.axaml`**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="using:BenchX"
        x:Class="BenchX.MainWindow"
        x:DataType="local:MainViewModel"
        Title="BenchX — Agro Parallel"
        Width="1180" Height="760" MinWidth="980" MinHeight="640"
        Background="{StaticResource FondoApp}">

  <ScrollViewer>
    <Grid ColumnDefinitions="7*,5*" Margin="16" ColumnSpacing="12">

      <!-- ============ Columna izquierda: Vehículo + GPS ============ -->
      <StackPanel Grid.Column="0" Spacing="12">

        <Border Classes="card">
          <StackPanel>
            <TextBlock Classes="titulo" Text="VEHÍCULO SIMULADO"/>
            <Grid ColumnDefinitions="150,*" RowDefinitions="Auto,Auto,Auto,Auto" RowSpacing="6">

              <Button Grid.Row="0" Grid.Column="0" Click="CeroVelocidad_Click"
                      Background="Transparent" Padding="0" HorizontalAlignment="Left">
                <StackPanel>
                  <TextBlock Classes="valor" Text="{Binding VelocidadTexto}"/>
                  <TextBlock Classes="dato" Text="{Binding MSegTexto}"/>
                </StackPanel>
              </Button>
              <Slider Grid.Row="0" Grid.Column="1" Minimum="-20" Maximum="50"
                      Value="{Binding VelocidadKmh}" TickFrequency="5" VerticalAlignment="Center"/>

              <Button Grid.Row="1" Grid.Column="0" Click="CeroAngulo_Click"
                      Background="Transparent" Padding="0" HorizontalAlignment="Left">
                <StackPanel>
                  <TextBlock Classes="valor" Text="{Binding AnguloTexto}"/>
                  <TextBlock Classes="dato" Text="Dirección (WAS)"/>
                </StackPanel>
              </Button>
              <Slider Grid.Row="1" Grid.Column="1" Minimum="-60" Maximum="60"
                      Value="{Binding AnguloDireccion}" TickFrequency="10" VerticalAlignment="Center"/>

              <Button Grid.Row="2" Grid.Column="0" Click="CeroRoll_Click"
                      Background="Transparent" Padding="0" HorizontalAlignment="Left">
                <StackPanel>
                  <TextBlock Classes="valor" Text="{Binding RollTexto}"/>
                  <TextBlock Classes="dato" Text="Roll"/>
                </StackPanel>
              </Button>
              <Slider Grid.Row="2" Grid.Column="1" Minimum="-20" Maximum="20"
                      Value="{Binding Roll}" TickFrequency="5" VerticalAlignment="Center"/>

              <StackPanel Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="2"
                          Orientation="Horizontal" Spacing="24" Margin="0,8,0,0">
                <StackPanel>
                  <TextBlock Classes="dato" Text="Rumbo"/>
                  <TextBlock Classes="datoValor" Text="{Binding RumboTexto}"/>
                </StackPanel>
                <StackPanel>
                  <TextBlock Classes="dato" Text="Latitud"/>
                  <TextBlock Classes="datoValor" Text="{Binding LatActualTexto}"/>
                </StackPanel>
                <StackPanel>
                  <TextBlock Classes="dato" Text="Longitud"/>
                  <TextBlock Classes="datoValor" Text="{Binding LonActualTexto}"/>
                </StackPanel>
              </StackPanel>
            </Grid>

            <StackPanel Orientation="Horizontal" Spacing="16" Margin="0,12,0,0">
              <ToggleSwitch OnContent="Switch trabajo" OffContent="Switch trabajo"
                            IsChecked="{Binding SwitchTrabajo}"/>
              <ToggleSwitch OnContent="Switch dirección" OffContent="Switch dirección"
                            IsChecked="{Binding SwitchDireccion}"/>
              <Button Content="Botón dirección (remoto)" Click="BotonDireccion_Click"/>
            </StackPanel>
          </StackPanel>
        </Border>

        <Border Classes="card">
          <StackPanel>
            <TextBlock Classes="titulo" Text="GPS — SENTENCIAS NMEA"/>
            <WrapPanel>
              <WrapPanel.Styles>
                <Style Selector="CheckBox">
                  <Setter Property="Margin" Value="0,0,14,4"/>
                </Style>
              </WrapPanel.Styles>
              <CheckBox Content="PANDA" IsChecked="{Binding Nda}"/>
              <CheckBox Content="PAOGI" IsChecked="{Binding Ogi}"/>
              <CheckBox Content="GGA" IsChecked="{Binding Gga}"/>
              <CheckBox Content="VTG" IsChecked="{Binding Vtg}"/>
              <CheckBox Content="RMC" IsChecked="{Binding Rmc}"/>
              <CheckBox Content="HDT" IsChecked="{Binding Hdt}"/>
              <CheckBox Content="AVR" IsChecked="{Binding Avr}"/>
              <CheckBox Content="KSXT" IsChecked="{Binding Ksxt}"/>
            </WrapPanel>
            <StackPanel Orientation="Horizontal" Spacing="8" Margin="0,12,0,0">
              <StackPanel>
                <TextBlock Classes="dato" Text="Latitud de arranque"/>
                <TextBox Width="170" Text="{Binding LatInicial}"/>
              </StackPanel>
              <StackPanel>
                <TextBlock Classes="dato" Text="Longitud de arranque"/>
                <TextBox Width="170" Text="{Binding LonInicial}"/>
              </StackPanel>
              <Button Content="Guardar posición" VerticalAlignment="Bottom" Click="GuardarPosicion_Click"/>
            </StackPanel>
          </StackPanel>
        </Border>

        <Border Classes="card">
          <StackPanel>
            <TextBlock Classes="titulo" Text="RED"/>
            <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto,Auto,Auto" ColumnSpacing="16" RowSpacing="4">
              <TextBlock Grid.Row="0" Grid.Column="0" Classes="dato" Text="IPs locales"/>
              <TextBlock Grid.Row="0" Grid.Column="1" Classes="datoValor" Text="{Binding IpsLocales}"/>
              <TextBlock Grid.Row="1" Grid.Column="0" Classes="dato" Text="Destino"/>
              <TextBlock Grid.Row="1" Grid.Column="1" Classes="datoValor" Text="{Binding SubredTexto}"/>
              <TextBlock Grid.Row="2" Grid.Column="0" Classes="dato" Text="Recibidos / Enviados"/>
              <StackPanel Grid.Row="2" Grid.Column="1" Orientation="Horizontal" Spacing="6">
                <TextBlock Classes="datoValor" Text="{Binding RxTexto}"/>
                <TextBlock Classes="dato" Text="/"/>
                <TextBlock Classes="datoValor" Text="{Binding TxTexto}"/>
              </StackPanel>
              <TextBlock Grid.Row="3" Grid.Column="0" Classes="dato" Text="Scan de PilotX"/>
              <Border Grid.Row="3" Grid.Column="1" Classes="badge" Classes.activo="{Binding ScanRespondido}"
                      HorizontalAlignment="Left">
                <TextBlock Text="{Binding ScanTexto}"/>
              </Border>
            </Grid>
            <Border Background="{StaticResource Alerta}" CornerRadius="6" Padding="10,6"
                    Margin="0,8,0,0" IsVisible="{Binding !RedOk}">
              <TextBlock Foreground="White" TextWrapping="Wrap" Text="{Binding ErrorRed}"/>
            </Border>
          </StackPanel>
        </Border>
      </StackPanel>

      <!-- ============ Columna derecha: Dirección + Máquina ============ -->
      <StackPanel Grid.Column="1" Spacing="12">

        <Border Classes="card">
          <StackPanel>
            <Grid ColumnDefinitions="*,Auto">
              <TextBlock Classes="titulo" Text="DIRECCIÓN (AUTOSTEER)"/>
              <Border Grid.Column="1" Classes="badge" Classes.activo="{Binding GuiadoActivo}">
                <TextBlock Text="{Binding GuiadoTexto}"/>
              </Border>
            </Grid>
            <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto,Auto"
                  ColumnSpacing="16" RowSpacing="3">
              <TextBlock Grid.Row="0" Grid.Column="0" Classes="dato" Text="Setpoint PilotX"/>
              <TextBlock Grid.Row="0" Grid.Column="1" Classes="datoValor" Text="{Binding SetPointTexto}"/>
              <TextBlock Grid.Row="1" Grid.Column="0" Classes="dato" Text="Velocidad según PilotX"/>
              <TextBlock Grid.Row="1" Grid.Column="1" Classes="datoValor" Text="{Binding VelPilotXTexto}"/>
              <TextBlock Grid.Row="2" Grid.Column="0" Classes="dato" Text="Kp"/>
              <TextBlock Grid.Row="2" Grid.Column="1" Classes="datoValor" Text="{Binding KpTexto}"/>
              <TextBlock Grid.Row="3" Grid.Column="0" Classes="dato" Text="PWM alto / bajo / mín"/>
              <StackPanel Grid.Row="3" Grid.Column="1" Orientation="Horizontal" Spacing="6">
                <TextBlock Classes="datoValor" Text="{Binding HighPwmTexto}"/>
                <TextBlock Classes="dato" Text="/"/>
                <TextBlock Classes="datoValor" Text="{Binding LowPwmTexto}"/>
                <TextBlock Classes="dato" Text="/"/>
                <TextBlock Classes="datoValor" Text="{Binding MinPwmTexto}"/>
              </StackPanel>
              <TextBlock Grid.Row="4" Grid.Column="0" Classes="dato" Text="Cuentas WAS / offset"/>
              <StackPanel Grid.Row="4" Grid.Column="1" Orientation="Horizontal" Spacing="6">
                <TextBlock Classes="datoValor" Text="{Binding CountsTexto}"/>
                <TextBlock Classes="dato" Text="/"/>
                <TextBlock Classes="datoValor" Text="{Binding OffsetTexto}"/>
              </StackPanel>
              <TextBlock Grid.Row="5" Grid.Column="0" Classes="dato" Text="Ackerman"/>
              <TextBlock Grid.Row="5" Grid.Column="1" Classes="datoValor" Text="{Binding AckermanTexto}"/>
              <TextBlock Grid.Row="6" Grid.Column="0" Classes="dato" Text="Config"/>
              <TextBlock Grid.Row="6" Grid.Column="1" Classes="dato" TextWrapping="Wrap" Text="{Binding FlagsTexto}"/>
            </Grid>
            <StackPanel Margin="0,10,0,0">
              <TextBlock Classes="dato" Text="Relés de dirección (1–16)"/>
              <TextBlock Classes="puntos" Text="{Binding RelesDireccion}"/>
            </StackPanel>
          </StackPanel>
        </Border>

        <Border Classes="card">
          <StackPanel>
            <TextBlock Classes="titulo" Text="MÁQUINA / SECCIONES"/>
            <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto,Auto,Auto,Auto" ColumnSpacing="16" RowSpacing="3">
              <TextBlock Grid.Row="0" Grid.Column="0" Classes="dato" Text="Relés máquina (1–16)"/>
              <TextBlock Grid.Row="0" Grid.Column="1" Classes="puntos" Text="{Binding RelesMaquina}"/>
              <TextBlock Grid.Row="1" Grid.Column="0" Classes="dato" Text="U-turn"/>
              <TextBlock Grid.Row="1" Grid.Column="1" Classes="datoValor" Text="{Binding UTurnTexto}"/>
              <TextBlock Grid.Row="2" Grid.Column="0" Classes="dato" Text="Hidráulico"/>
              <TextBlock Grid.Row="2" Grid.Column="1" Classes="datoValor" Text="{Binding HydLiftTexto}"/>
              <TextBlock Grid.Row="3" Grid.Column="0" Classes="dato" Text="Tram"/>
              <TextBlock Grid.Row="3" Grid.Column="1" Classes="datoValor" Text="{Binding TramTexto}"/>
              <TextBlock Grid.Row="4" Grid.Column="0" Classes="dato" Text="Velocidad módulo"/>
              <TextBlock Grid.Row="4" Grid.Column="1" Classes="datoValor" Text="{Binding VelMaquinaTexto}"/>
            </Grid>
            <TextBlock Classes="dato" Text="Zonas (1–8)" Margin="0,10,0,2"/>
            <Grid ColumnDefinitions="Auto,Auto" RowDefinitions="Auto,Auto,Auto,Auto" ColumnSpacing="24" RowSpacing="2">
              <StackPanel Grid.Row="0" Grid.Column="0" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="1"/><TextBlock Classes="puntos" Text="{Binding Zona1}"/>
              </StackPanel>
              <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="2"/><TextBlock Classes="puntos" Text="{Binding Zona2}"/>
              </StackPanel>
              <StackPanel Grid.Row="1" Grid.Column="0" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="3"/><TextBlock Classes="puntos" Text="{Binding Zona3}"/>
              </StackPanel>
              <StackPanel Grid.Row="1" Grid.Column="1" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="4"/><TextBlock Classes="puntos" Text="{Binding Zona4}"/>
              </StackPanel>
              <StackPanel Grid.Row="2" Grid.Column="0" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="5"/><TextBlock Classes="puntos" Text="{Binding Zona5}"/>
              </StackPanel>
              <StackPanel Grid.Row="2" Grid.Column="1" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="6"/><TextBlock Classes="puntos" Text="{Binding Zona6}"/>
              </StackPanel>
              <StackPanel Grid.Row="3" Grid.Column="0" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="7"/><TextBlock Classes="puntos" Text="{Binding Zona7}"/>
              </StackPanel>
              <StackPanel Grid.Row="3" Grid.Column="1" Orientation="Horizontal" Spacing="8">
                <TextBlock Classes="dato" Text="8"/><TextBlock Classes="puntos" Text="{Binding Zona8}"/>
              </StackPanel>
            </Grid>
          </StackPanel>
        </Border>
      </StackPanel>
    </Grid>
  </ScrollViewer>
</Window>
```

- [ ] **Step 2: Reemplazar `SourceCode/BenchX/MainWindow.axaml.cs`**

```csharp
using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace BenchX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.ReinicioPedido += Reiniciar;
        Closing += (_, _) => _vm.Cerrar();
    }

    private void CeroVelocidad_Click(object? s, RoutedEventArgs e) => _vm.CeroVelocidad();
    private void CeroAngulo_Click(object? s, RoutedEventArgs e) => _vm.CeroAngulo();
    private void CeroRoll_Click(object? s, RoutedEventArgs e) => _vm.CeroRoll();
    private void GuardarPosicion_Click(object? s, RoutedEventArgs e) => _vm.GuardarPosicion();
    private void BotonDireccion_Click(object? s, RoutedEventArgs e) => _vm.BotonDireccionRemoto();

    // PGN 201: la config ya quedó guardada; relanzar el proceso con la subred nueva.
    private void Reiniciar(string mensaje)
    {
        _vm.Cerrar();
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Close();
    }
}
```

- [ ] **Step 3: Compilar y correr tests**

```powershell
dotnet build SourceCode\BenchX\BenchX.csproj -v q
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: build OK, `Passed: 32`.

- [ ] **Step 4: Humo visual — lanzar BenchX y verificar contra la lista**

```powershell
dotnet run --project SourceCode\BenchX\BenchX.csproj
```
Verificar a ojo (y cerrar):
1. Ventana "BenchX — Agro Parallel", fondo gris verdoso claro, 5 cards blancas.
2. Mover el slider de velocidad → el valor grande cambia; tocar el valor → vuelve a 0.
3. Card Red muestra IPs locales y destino `127.255.255.255:9999`, contador Enviados sube solo (PANDA activa por default).
4. `benchx.json` apareció junto al binario (`SourceCode\BenchX\bin\Debug\net9.0-windows\`).
5. Si otro proceso tiene el :8888 tomado (CoreX corriendo), aparece la franja roja en la card Red — eso es la feature, no un bug del humo.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/BenchX/MainWindow.axaml SourceCode/BenchX/MainWindow.axaml.cs
git commit -m "feat(benchx): ventana de cards con la estética Agro Parallel

Vehículo (sliders con tap-a-cero), GPS (toggles NMEA + posición),
Dirección (badge de guiado + config de PilotX), Máquina (relés y zonas
como puntos), Red (IPs, destino, contadores, franja roja si el :8888
está tomado). Reinicio por PGN 201 relanzando el proceso."
```

---

### Task 9: build.ps1, borrar ModSim y aviso a Santi

**Files:**
- Modify: `build.ps1:45-49` (bloque build ModSim → publish BenchX)
- Modify: `build.ps1:140-147` (bloque copiar ModSim → eliminar)
- Modify: `build.ps1:181-188` (`$skipDirs` + `$skipFiles`)
- Delete: `SourceCode/ModSim/` (todo el árbol), `Build/ModSim.exe`, `Build/ModSim.exe.config`
- Modify: `COORDINACION-SESIONES.md` (nota PULL al principio de la sección de novedades, mismo formato que las existentes)

**Interfaces:**
- Consumes: proyecto BenchX completo (Tasks 1-8).

- [ ] **Step 1: En `build.ps1`, reemplazar el bloque de build de ModSim (líneas ~45-49)**

Antes:
```powershell
# ModSim: simulador de GPS/NMEA por UDP :8888 para probar guiado sin antena
# real. Viaja en el paquete para que cada pantalla pueda simular en banco.
Write-Host "`n=== Build ModSim ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\SourceCode\ModSim\Source\ModSim.csproj" -c $Config -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "ModSim FAILED" -ForegroundColor Red; exit 1 }
```

Después:
```powershell
# BenchX: simulador de banco (ex ModSim) — GPS/NMEA + módulos por UDP :8888.
# Se publica a Build\BenchX\ (framework-dependent net9); NO viaja en el ZIP
# de release (ver $skipDirs): con el CoreX embebido arma un lazo de eco UDP.
Write-Host "`n=== Publish BenchX ($Config) ===" -ForegroundColor Cyan
dotnet publish "$root\SourceCode\BenchX\BenchX.csproj" -c $Config -o "$OutDir\BenchX" -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "BenchX FAILED" -ForegroundColor Red; exit 1 }
```

(El comentario del ModSim original decía "viaja en el paquete" pero `$skipFiles` lo excluía — la exclusión era la verdad; BenchX la mantiene explícita.)

- [ ] **Step 2: En `build.ps1`, borrar el bloque "Copiar ModSim" (líneas ~140-147)**

Eliminar completo:
```powershell
# Copiar ModSim (simulador GPS — lo lanza el operario a mano cuando prueba)
$modSimBin = "$root\SourceCode\ModSim\Source\bin\$Config"
if (Test-Path $modSimBin) {
    Write-Host "Copiando ModSim..." -ForegroundColor Yellow
    Get-ChildItem $modSimBin -File -Filter "ModSim.exe*" | ForEach-Object {
        Copy-Item $_.FullName -Destination $OutDir -Force
    }
}
```
(No hace falta bloque nuevo: el publish del Step 1 ya deja BenchX en `$OutDir\BenchX`.)

- [ ] **Step 3: En `build.ps1`, actualizar exclusiones del ZIP (líneas ~181-188)**

- A `$skipDirs` agregarle `'BenchX'`:
```powershell
$skipDirs = @('Updates','Backups','WebView2Data','firmware-cache',
              'data','implementos','Fields','Vehicles','Logs','Profiles',
              'PilotXDesktop','BenchX')
```
- En el comentario y `$skipFiles`, reemplazar la mención de ModSim:
```powershell
# Exes que NO viajan a una pantalla: BenchX vía $skipDirs (simulador de banco;
# con el CoreX embebido del engine arma un lazo de eco UDP que infla el proceso
# a GBs) y createdump (herramienta de debug de .NET, puro peso).
$skipFiles = @('createdump.exe')
```

- [ ] **Step 4: Borrar el WinForms viejo**

```powershell
git rm -r SourceCode\ModSim
git rm Build\ModSim.exe Build\ModSim.exe.config
```
Si `Build\ModSim.exe*` no estuvieran trackeados (`git rm` falla con "did not match"), borrarlos con `Remove-Item Build\ModSim.exe, Build\ModSim.exe.config -Force -Confirm:$false`.

- [ ] **Step 5: Correr `build.ps1` completo y verificar**

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```
Expected: termina en `Build OK`, existe `Build\BenchX\BenchX.exe`, NO existe `Build\ModSim.exe`, y el ZIP `PilotX_v*.zip` NO contiene `BenchX/` (verificar con `Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead((Get-Item PilotX_v*.zip).FullName).Entries.FullName | Select-String BenchX` → vacío). Borrar el ZIP generado de prueba después (`Remove-Item PilotX_v*.zip -Confirm:$false`).

- [ ] **Step 6: Nota para Santi en `COORDINACION-SESIONES.md`**

ModSim es carril de Santi. Agregar al TOPE de la zona de novedades (mismo estilo de los bloques `## 🔴 SANTI — PULL del ...` existentes; mirar el formato del archivo antes de escribir):

```markdown
## 🔴 SANTI — PULL del 2026-08-11 · ModSim → BenchX (Avalonia)

**ModSim WinForms se borró; el reemplazo es `SourceCode/BenchX/` (net9 +
Avalonia, estilo Agro Parallel).** Mismo wire: escucha :8888, broadcast
`<subred>.255:9999`, mismas sentencias NMEA y PGN (253/hellos/scan reply
byte a byte, dummies históricos incluidos). Lo nuevo:

- Config en `benchx.json` junto al exe (chau Properties.Settings y la
  subred perdida de v1.0.25). Default de subred: **127.255.255** (banco).
- La lógica quedó en clases puras con tests (`BenchX.Tests`, 32 verdes):
  NmeaBuilder / SimuladorVehiculo / PgnProcessor / UdpLink.
- La escucha UDP es inmortal (tu fix del 10054 viaja portado) y sin
  BeginReceiveFrom (lección stack overflow net9).
- Sale a `Build\BenchX\BenchX.exe` vía build.ps1; excluido del ZIP de
  release igual que ModSim (lazo de eco con el CoreX embebido).
- El PGN 201 (cambio de subred) sigue andando: guarda JSON y se relanza.

Si tenías algo a medias sobre ModSim, avisá y lo portamos a BenchX.
```

- [ ] **Step 7: Correr TODOS los tests de la solución que tocamos**

```powershell
dotnet test SourceCode\BenchX.Tests\BenchX.Tests.csproj -v q
```
Expected: `Passed: 32`.

- [ ] **Step 8: Commit final**

```bash
git add build.ps1 COORDINACION-SESIONES.md
git commit -m "chore(modsim): borrar ModSim WinForms — lo reemplaza BenchX

build.ps1 publica BenchX a Build\BenchX\ y lo excluye del ZIP de
release (mismo motivo que ModSim: lazo de eco UDP con el CoreX
embebido). Nota de PULL para Santi en COORDINACION-SESIONES.md.
Verificado: build.ps1 completo OK, ZIP sin BenchX, 32 tests verdes."
```
(El `git rm` del Step 4 ya dejó los borrados en el índice; este commit los incluye.)

---

## Verificación final (fuera de tareas — es el criterio de éxito del spec)

La prueba de banco real (PilotX recibiendo GPS de BenchX por loopback y contestando el scan) requiere levantar el stack completo (`CoreX.exe` + engine); queda como validación manual de Leonardo o de la próxima sesión de banco. Hasta entonces BenchX queda **en prueba**, no "anda" — no declararlo cerrado.
