# Batería en barra superior + IP en Sistema — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chip de batería siempre visible en la barra superior del cockpit y sección "Red" (IPs + batería en texto) en el panel Sistema.

**Architecture:** La lectura de batería es local (P/Invoke `GetSystemPowerStatus`) y vive en `PilotX.UI/Services/BateriaLector.cs`. La presentación (texto/colores/visibilidad) vive en `BarraSuperiorViewModel` (testeable en `PilotX.Cockpit.Bars.Tests`). MainWindow ya tiene `_vmSup` y le empuja la lectura con un `DispatcherTimer` cada 10 s. Las IPs se listan local con `NetworkInterface` en `SistemaPanel`.

**Tech Stack:** Avalonia 11, CommunityToolkit.Mvvm (`[ObservableProperty]`), NUnit.

## Global Constraints

- Responder/comentar/commitear en castellano rioplatense.
- Nada tapa el mapa; sin popups ni sonidos — la alerta es el color del chip.
- Sin batería (PC escritorio) o en Linux → el chip NO aparece.
- Paleta clara BarraSuperior: texto `#101612`, borde `#D9E0D9`, ámbar `#B36A00`/`#E2B53E`, rojo `#C0261F`.
- `ayuda.html` se actualiza en el MISMO commit que agrega UI visible.
- IMPORTANTE: el chip va en `BarraSuperior` (cockpit) — el `HudBar` de MainWindow está SIEMPRE oculto (lo reemplazó el cockpit). Corregir el spec en la tarea 5.

---

### Task 1: Presentación de batería en `BarraSuperiorViewModel` (TDD)

**Files:**
- Modify: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraSuperiorViewModel.cs`
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/BarraSuperiorViewModelTests.cs`

**Interfaces:**
- Produces: `void AplicarBateria(bool tiene, int pct, bool cargando, bool enchufada)` + props observables `BateriaVisible` (bool), `BateriaText` (string), `BateriaColorTexto` (string hex), `BateriaColorBorde` (string hex). Las consumen Task 2 (XAML) y Task 3 (MainWindow).

- [ ] **Step 1: Test que falla** — agregar a `BarraSuperiorViewModelTests.cs`:

```csharp
[Test]
public void AplicarBateria_SinBateria_OcultaElChip()
{
    var vm = Make();
    vm.AplicarBateria(tiene: false, pct: 0, cargando: false, enchufada: false);
    Assert.That(vm.BateriaVisible, Is.False);
}

[Test]
public void AplicarBateria_EnchufadaCargando_TextoNormalConRayito()
{
    var vm = Make();
    vm.AplicarBateria(tiene: true, pct: 85, cargando: true, enchufada: true);
    Assert.That(vm.BateriaVisible, Is.True);
    Assert.That(vm.BateriaText, Is.EqualTo("85%⚡"));
    Assert.That(vm.BateriaColorTexto, Is.EqualTo("#101612"));
    Assert.That(vm.BateriaColorBorde, Is.EqualTo("#D9E0D9"));
}

[Test]
public void AplicarBateria_ABateria_PintaAmbar()
{
    var vm = Make();
    vm.AplicarBateria(tiene: true, pct: 60, cargando: false, enchufada: false);
    Assert.That(vm.BateriaText, Is.EqualTo("60%"));
    Assert.That(vm.BateriaColorTexto, Is.EqualTo("#B36A00"));
    Assert.That(vm.BateriaColorBorde, Is.EqualTo("#E2B53E"));
}

[Test]
public void AplicarBateria_ABateriaCritica_PintaRojo()
{
    var vm = Make();
    vm.AplicarBateria(tiene: true, pct: 12, cargando: false, enchufada: false);
    Assert.That(vm.BateriaColorTexto, Is.EqualTo("#C0261F"));
    Assert.That(vm.BateriaColorBorde, Is.EqualTo("#C0261F"));
}
```

- [ ] **Step 2: Verificar que falla**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -v q`
Expected: FAIL (no compila: `AplicarBateria` no existe).

- [ ] **Step 3: Implementación mínima** — en `BarraSuperiorViewModel.cs`, después de `_debugText`:

```csharp
    // Batería de la pantalla. La lectura viene de PilotX.UI (BateriaLector,
    // GetSystemPowerStatus local) — acá solo se decide qué mostrar.
    // Sin batería (PC de escritorio) el chip no existe. La alerta es SOLO
    // color (regla: nada de popups ni sonidos sobre el mapa).
    [ObservableProperty] private bool _bateriaVisible;
    [ObservableProperty] private string _bateriaText = "--";
    [ObservableProperty] private string _bateriaColorTexto = "#101612";
    [ObservableProperty] private string _bateriaColorBorde = "#D9E0D9";

    public void AplicarBateria(bool tiene, int pct, bool cargando, bool enchufada)
    {
        BateriaVisible = tiene;
        if (!tiene) return;
        BateriaText = cargando ? $"{pct}%⚡" : $"{pct}%";
        if (enchufada)
        {
            (BateriaColorTexto, BateriaColorBorde) = ("#101612", "#D9E0D9");
        }
        else if (pct < 15)
        {
            (BateriaColorTexto, BateriaColorBorde) = ("#C0261F", "#C0261F");
        }
        else
        {
            (BateriaColorTexto, BateriaColorBorde) = ("#B36A00", "#E2B53E");
        }
    }
```

- [ ] **Step 4: Verificar que pasa**

Run: `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -v q`
Expected: PASS (todos, incluidos los preexistentes).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraSuperiorViewModel.cs SourceCode/PilotX.Cockpit.Bars.Tests/BarraSuperiorViewModelTests.cs
git commit -m "feat(cockpit): estado de bateria en BarraSuperiorViewModel"
```

---

### Task 2: Chip de batería en `BarraSuperior.axaml`

**Files:**
- Modify: `SourceCode/PilotX.Cockpit.Bars/Views/BarraSuperior.axaml` (StackPanel de telemetría, Grid.Column="5")

**Interfaces:**
- Consumes: `BateriaVisible`, `BateriaText`, `BateriaColorTexto`, `BateriaColorBorde` (Task 1) y el converter `StringToBrush` ya declarado en el UserControl.

- [ ] **Step 1: Agregar el chip** — dentro del StackPanel `Grid.Column="5"`, después del botón HA (línea ~229), antes del cierre `</StackPanel>`:

```xml
        <!-- Batería de la pantalla: solo existe si la máquina tiene batería
             (tablet/notebook). Enchufada = gris; a batería = ámbar; <15% a
             batería = rojo. El rayito ⚡ va en el texto cuando carga. -->
        <Border Background="#FFFFFF" BorderThickness="1" CornerRadius="10"
                Padding="8,2" MinWidth="56"
                IsVisible="{Binding BateriaVisible}"
                BorderBrush="{Binding BateriaColorBorde, Converter={StaticResource StringToBrush}}">
          <StackPanel VerticalAlignment="Center">
            <TextBlock Classes="televal" FontSize="13" Text="{Binding BateriaText}"
                       Foreground="{Binding BateriaColorTexto, Converter={StaticResource StringToBrush}}"/>
            <TextBlock Classes="telelbl" Text="BAT"/>
          </StackPanel>
        </Border>
```

- [ ] **Step 2: Verificar que compila**

Run: `dotnet build SourceCode/PilotX.Cockpit.Bars -v q`
Expected: Build succeeded, 0 errores.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars/Views/BarraSuperior.axaml
git commit -m "feat(cockpit): chip de bateria en la barra superior"
```

---

### Task 3: `BateriaLector` + timer en MainWindow

**Files:**
- Create: `SourceCode/PilotX.UI/Services/BateriaLector.cs`
- Modify: `SourceCode/PilotX.UI/MainWindow.axaml.cs` (después de crear `_vmSup`, línea ~6106-6111)

**Interfaces:**
- Produces: `BateriaLector.Leer()` → `(bool tiene, int pct, bool cargando, bool enchufada)`. La consume también Task 4 (SistemaPanel).

- [ ] **Step 1: Crear `BateriaLector.cs`**:

```csharp
// BateriaLector.cs
//
// Estado de carga de la batería de la pantalla, leído local con
// GetSystemPowerStatus (kernel32) — la UI corre en la misma máquina, no
// hace falta pasar por el Engine. En Linux o sin batería devuelve
// tiene=false y el que consume oculta el dato (nunca inventa números).

using System;
using System.Runtime.InteropServices;

namespace PilotX.Desktop.Services;

public static class BateriaLector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 = a batería, 1 = enchufada, 255 = ?
        public byte BatteryFlag;        // bit 8 = cargando, bit 128 = sin batería
        public byte BatteryLifePercent; // 0..100, 255 = desconocido
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static (bool tiene, int pct, bool cargando, bool enchufada) Leer()
    {
        if (!OperatingSystem.IsWindows()) return (false, 0, false, false);
        try
        {
            if (!GetSystemPowerStatus(out var s)) return (false, 0, false, false);
            if ((s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100)
                return (false, 0, false, false);
            return (true, s.BatteryLifePercent,
                    (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1);
        }
        catch
        {
            return (false, 0, false, false);
        }
    }
}
```

- [ ] **Step 2: Timer en MainWindow** — en `MainWindow.axaml.cs`, justo después de `if (_barSuperior != null) _barSuperior.DataContext = _vmSup;` (~línea 6111):

```csharp
        // Batería de la pantalla → chip de la barra superior. Poll de 10 s:
        // el dato cambia lento y la lectura es una syscall barata.
        void EmpujarBateria()
        {
            var (tiene, pct, cargando, enchufada) = PilotX.Desktop.Services.BateriaLector.Leer();
            _vmSup?.AplicarBateria(tiene, pct, cargando, enchufada);
        }
        EmpujarBateria();
        var bateriaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        bateriaTimer.Tick += (_, _) => EmpujarBateria();
        bateriaTimer.Start();
```

(Si el `using PilotX.Desktop.Services;` ya está en el archivo, usar `BateriaLector.Leer()` a secas.)

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build SourceCode/PilotX.UI -v q`
Expected: Build succeeded, 0 errores.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/PilotX.UI/Services/BateriaLector.cs SourceCode/PilotX.UI/MainWindow.axaml.cs
git commit -m "feat(ui): lectura de bateria local y push al chip del cockpit"
```

---

### Task 4: Sección "Red" en SistemaPanel

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/SistemaPanel.axaml` (entre Brillo y Energía, línea ~157)
- Modify: `SourceCode/PilotX.UI/Views/SistemaPanel.axaml.cs`

**Interfaces:**
- Consumes: `BateriaLector.Leer()` (Task 3).

- [ ] **Step 1: XAML** — insertar entre el cierre del card de Brillo (`</Border>`, línea ~156) y el título "Energia":

```xml
                    <!-- ============ Red ============ -->
                    <TextBlock Text="Red"
                               Foreground="{StaticResource PilotXPanelText}"
                               FontSize="16"
                               FontWeight="SemiBold"
                               Margin="0,8,0,0"/>
                    <Border Background="{StaticResource PilotXPanelSurface}"
                            BorderBrush="{StaticResource PilotXPanelBorderHigh}"
                            BorderThickness="1"
                            CornerRadius="10"
                            Padding="18,14,18,14"
                            MaxWidth="720"
                            HorizontalAlignment="Left">
                        <StackPanel Spacing="6">
                            <TextBlock Name="RedIps"
                                       Text="Cargando..."
                                       Foreground="{StaticResource PilotXPanelText}"
                                       FontSize="15"
                                       FontWeight="SemiBold"
                                       TextWrapping="Wrap"/>
                            <TextBlock Name="RedBateria"
                                       Text=""
                                       Foreground="{StaticResource PilotXPanelTextDim}"
                                       FontSize="12"/>
                        </StackPanel>
                    </Border>
```

- [ ] **Step 2: Code-behind** — en `SistemaPanel.axaml.cs` agregar usings `System.Net.NetworkInformation`, `System.Net.Sockets`, `System.Text`; y en la clase:

```csharp
    // Refresco de Red: solo mientras el panel está a la vista (el operario
    // lo abre para leer la IP; de fondo no gasta nada).
    private DispatcherTimer? _redTimer;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefrescarRed();
        _redTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _redTimer.Tick += (_, _) => RefrescarRed();
        _redTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _redTimer?.Stop();
        _redTimer = null;
    }

    private void RefrescarRed()
    {
        var ips = this.FindControl<TextBlock>("RedIps");
        var bat = this.FindControl<TextBlock>("RedBateria");
        if (ips != null) ips.Text = ListarIps();
        if (bat != null)
        {
            var (tiene, pct, cargando, enchufada) = BateriaLector.Leer();
            bat.Text = !tiene ? "Sin bateria"
                : $"Bateria: {pct} % — " + (cargando ? "cargando"
                    : enchufada ? "enchufada" : "a bateria");
        }
    }

    private static string ListarIps()
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue; // APIPA: no sirve para nada
                    sb.AppendLine($"{ni.Name} — {ip}");
                }
            }
        }
        catch { /* sin permisos de red: cae al "Sin red conectada" */ }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "Sin red conectada";
    }
```

Nota: los textos van sin tilde ("Bateria", "Sin bateria") igual que el resto del archivo ("Configuracion", "Energia") — el codebase evita no-ASCII en strings de .NET (trampa conocida de headers/encoding).

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build SourceCode/PilotX.UI -v q`
Expected: Build succeeded, 0 errores.

- [ ] **Step 4: Commit** (junto con ayuda.html — ver Task 5: si se ejecuta seguido, un solo commit para respetar "el manual viaja con el cambio").

---

### Task 5: Manual + corrección del spec + commit final

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`
- Modify: `docs/superpowers/specs/2026-08-26-bateria-ip-pantalla-design.md`

- [ ] **Step 1: ayuda.html** — buscar la sección que documenta la barra superior (grep "barra superior" / "Barra superior") y agregar: el chip `BAT` muestra la batería de la pantalla con ⚡ cuando carga, ámbar a batería y rojo con menos de 15 %; solo aparece en equipos con batería. Buscar la sección de Sistema (grep "Sistema") y agregar: `SISTEMA › Sistema` ahora muestra la sección **Red** con la IP de cada interfaz conectada y el estado de batería. Verificar las rutas contra los .axaml reales antes de escribirlas.

- [ ] **Step 2: Spec** — en el spec, reemplazar la mención a "HudBar (`MainWindow.axaml`)" por "BarraSuperior (`PilotX.Cockpit.Bars`)": el HudBar nativo está siempre oculto, la barra visible es la del cockpit.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html docs/superpowers/specs/2026-08-26-bateria-ip-pantalla-design.md SourceCode/PilotX.UI/Views/SistemaPanel.axaml SourceCode/PilotX.UI/Views/SistemaPanel.axaml.cs
git commit -m "feat(sistema): seccion Red con IPs y bateria + manual"
```

---

## Verificación final

- `dotnet test SourceCode/PilotX.Cockpit.Bars.Tests -v q` → PASS.
- `dotnet build SourceCode/PilotX.UI -v q` → 0 errores.
- En la PC de desarrollo (sin batería): el chip BAT no aparece; `SISTEMA › Sistema › Red` muestra las IPs reales y "Sin bateria".
- En tablet/notebook queda **Prueba:** (no declarar "anda" sin cabina).
