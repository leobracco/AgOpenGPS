# Porteo HTML → panel Avalonia nativo (PilotX.UI)

Guía definitiva para portar una página del Hub (`wwwroot/pages/*.html`) a un
panel nativo de PilotX.UI. Doctrina *strangler fig*: se porta SOLO lo
cabin-critical (monitor live); la configuración/CRUD sigue en la página HTML y
se abre desde el panel con "Configurar". **Las páginas HTML NUNCA se borran**
(siguen sirviendo para Hub remoto / celular).

Ejemplares de referencia (leerlos antes de arrancar):

- `SourceCode/PilotX.UI/Views/CoreXEcuPanel.axaml` + `.cs` — card clara flotante, tabs Live/Configurar
- `SourceCode/PilotX.UI/Views/GuiasPanel.cs` — panel 100% code-only, multi-pantalla, teclado nativo
- `SourceCode/PilotX.UI/Services/CoreXEcuClient.cs` — client HTTP mínimo
- `SourceCode/PilotX.UI/MainWindow.axaml.cs` — hosting, `RouteCockpitCommand`

## 1. Archivos y nombres

| Qué | Dónde |
|---|---|
| Panel con XAML | `Views/XxxPanel.axaml` + `Views/XxxPanel.axaml.cs` |
| Panel code-only | `Views/XxxPanel.cs` (`public sealed class XxxPanel : Border`) |
| Client HTTP + DTOs | `Services/XxxClient.cs` (un archivo por producto, DTOs adentro) |

OJO: el `x:Class` del XAML es `PilotX.Desktop.Views.XxxPanel` — el namespace es
`PilotX.Desktop.Views` aunque el csproj se llame PilotX.UI. **No cambiarlo.**

En el XAML va el esqueleto estático (header, KPI strip, hosts vacíos con
`Name="CardsHost"`); las filas/cards dinámicas se construyen en C# con
`host.Children.Clear()` + rebuild completo en cada render (con decenas de
controles el costo es despreciable).

Comentario de cabecera obligatorio en cada archivo: qué quedó nativo, qué sigue
en HTML y por qué.

## 2. Estilo visual: card clara sobre el mapa vivo

**El mapa NUNCA se apaga.** Los takeovers oscuros son el patrón viejo — los
paneles nuevos son cards flotantes claras con el mapa vivo detrás.

Paleta (brushes estáticos hardcodeados, idénticos a CoreXEcuPanel/GuiasPanel):

```csharp
BgPanel   = "#FAFBFA"   // fondo de la card
BgFila    = "#FFFFFF"   // superficies internas
BgFilaSel = "#DCEFD8"   // fila seleccionada
Borde     = "#C5CFC5"
Texto     = "#101612"
TextoMuted= "#535E54"
TextoDim  = "#7A857B"
Verde     = "#4ABA3E"   // SOLO acento, jamás fondo
Ok        = "#3D9A33";  Warn = "#B98A2E";  Err = "#D0504A";  Dim = "#8A958B"
// chip de error: fondo "#FBECEC", borde "#D0504A", código "#B33F3A"
```

Contenedor raíz:

```xml
<Border Background="#FAFBFA" BorderBrush="#C5CFC5" BorderThickness="1"
        CornerRadius="14" Padding="14" BoxShadow="0 8 26 0 #33101612"
        MaxWidth="920" MaxHeight="640" VerticalAlignment="Center" Margin="20,0">
```

Detalles: números en `FontFamily="Consolas, Courier New, monospace"`; pills de
estado = `Border CornerRadius="999"` + `Ellipse` 8-10 px + texto; labels de KPI
en MAYÚSCULAS FontSize 10 SemiBold dim; tabs a mano con dos Buttons (activo =
fondo blanco + `BorderThickness="0,0,0,3"` verde; inactivo = Transparent +
texto `#535E54`), no TabControl.

**Prohibido**: MenuFlyout/Flyout sobre el mapa GL (no se dibujan y no logean
error) — overlays internos siempre con `Border` + `IsVisible` + ZIndex, backdrop
que cierra en `PointerPressed` y card interna que absorbe con `e.Handled = true`.

## 3. Táctil

- Botones grandes: **mínimo ~40x40 px**, típico 64x56 a 124x108.
- **Cero atajos de teclado** — todo lo importante en botón visible.
- Click/touch en controles compuestos: evento `Tapped` (no PointerPressed) +
  `cell.Cursor = new Cursor(StandardCursorType.Hand)`.
- Unidades agronómicas siempre (kg/ha, sem/m, km/h, rpm — nunca PPS) y nombres
  de producto (CoreX-ECU, VistaX), nunca hardware.

## 4. Apertura: comando → MainWindow → panel

Los ViewModels de las barras mandan strings de comando a
`MainWindow.RouteCockpitCommand(string cmd)` (router central, se registra como
`_cockpitCmd.LocalHandler`). Si el switch lo maneja devuelve `true`; si devuelve
`false` el comando sigue al motor por `POST /api/aog/guidance/command`.

Pasos para colgar un panel nuevo:

1. **Slot en `MainWindow.axaml`**:
   ```xml
   <vw:XxxPanel x:Name="XxxHost" Grid.Row="1" ZIndex="10"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                IsVisible="False"/>
   ```
2. **En el ctor de `MainWindow.axaml.cs`**: `_xxxHost = this.FindControl<XxxPanel>("XxxHost");`
   más wiring de callbacks:
   `_xxxHost.OnRequestCerrar = () => CloseXxx();`
   `_xxxHost.OnRequestConfigurar = () => NavigateTo("pages/xxx.html");`
3. **Par `ShowXxx()/CloseXxx()`**. `ShowXxx()` aplica "solo un overlay a la
   vez": cierra (`Detach()` + `IsVisible = false`) TODOS los demás hosts, cierra
   el WebView, lazy-init del client (una sola vez, se reusa):
   ```csharp
   _xxxClient ??= new XxxClient(DeriveOrigin(App.TargetUrl));
   _xxxHost.Attach(_xxxClient);
   _xxxHost.IsVisible = true;
   if (_mapHost != null && App.WindowMode != "float") _mapHost.IsVisible = true; // el mapa se reenciende
   ```
   `CloseXxx()` = `Detach(); IsVisible = false;` + restaurar mapa.
4. **Caso en el router**: `case "xxx": ShowXxx(); return true;`

Los paneles **no navegan solos**: exponen `Action?`/`event` y el host decide.
Avisos al operario via `event Action<string>? Aviso` → `MostrarToast`, **nunca
modal** (ShowDialog trababa la cabina). Al abrir páginas HTML desde nativo,
SIEMPRE con `?widget=1` (esconde el sidebar del Hub) — `NavigateTo` ya lo hace.

Si el panel embebe un WebView por pestaña (estilo CoreX-ECU): el host lo crea
dentro del slot al entrar y lo **destruye** al salir (`wv.Destroy()`) —
re-parentar un NativeControlHost mata Chromium; jamás mover el `_webView` global.

## 5. Ciclo de vida + polling seguro (el patrón que NO congela la UI)

```csharp
private XxxClient? _client;
private CancellationTokenSource? _cts;
private XxxStatus? _live;   // snapshot; null = sin conexión

public void Attach(XxxClient client)
{
    _client = client;
    if (_cts != null) return;              // guard anti doble-Attach
    _cts = new CancellationTokenSource();
    _ = RunLoopAsync(_cts.Token);
}
public void Detach() { try { _cts?.Cancel(); } catch { } _cts = null; }

private async Task RunLoopAsync(CancellationToken ct)
{
    await TickAsync(ct).ConfigureAwait(false);   // primer tick inmediato
    while (!ct.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        await TickAsync(ct).ConfigureAwait(false);
    }
}
private async Task TickAsync(CancellationToken ct)
{
    if (_client != null)
        _live = await _client.GetStatusAsync(ct).ConfigureAwait(false);
    await Dispatcher.UIThread.InvokeAsync(Render);   // TODO toque de UI en el UI thread
}
```

Reglas duras:

- Pushes a controles **SIEMPRE** vía `Dispatcher.UIThread` (los cross-thread ya
  congelaron el mapa una vez).
- `TaskCanceledException` HEREDA de `OperationCanceledException`: si algún catch
  filtra por tipo, incluir `when (ct.IsCancellationRequested)` para no confundir
  timeout con cancelación y dejar la UI clavada en defaults.
- Frecuencias: paneles 2 Hz (500 ms); barras cockpit 250 ms; nodos 3 s.
- `Render()` resuelve con `this.FindControl<T>("Nombre")` null-safe y pinta tres
  estados: `s == null` → dim "Hub no responde"; `!s.Ok` → err + chip de error
  con código AGP-XXX-nnn (fallback `"AGP-NET-201"`); ok → verde.
- Formatters estáticos con `CultureInfo.InvariantCulture` y `"--"` para
  null/NaN; `YesNo` = "Sí"/"No".
- Timers puntuales de UI: `DispatcherTimer`, parado con `try { _t?.Stop(); } catch { }`.

## 6. Client HTTP + DTOs (wire snake_case)

```csharp
public sealed class XxxClient
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly string _baseUrl;

    public XxxClient(string baseUrl = "http://127.0.0.1:5180/")
        => _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                    : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");

    public async Task<XxxStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "api/xxx/status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<XxxStatus>(json, _jsonOpts);
        }
        catch { return null; }   // el panel pinta "sin datos", jamás explota
    }
}
```

- DTOs `sealed class` con `[JsonPropertyName("snake_case")]` **explícito en CADA
  propiedad**: `PropertyNameCaseInsensitive` NO cubre underscores
  (`spm_promedio` no matchea `SpmPromedio` sin atributo).
- Numéricos nullable (`double?`, `long?`) si el back puede omitirlos.
- No cambiar contratos de API existentes: el panel consume los mismos endpoints
  que ya usaba la página HTML.
- Aprovechar que los POST devuelven el estado nuevo para ahorrarse el GET
  siguiente (ver GuiasPanel).
- Para flujos con estado, traza propia a `%TEMP%\pilotx-xxx.log` envuelta en
  `try { } catch { }` — en Release no hay Debug.WriteLine y los catch mudos
  ciegan el diagnóstico.

## 7. Teclado nativo (TecladoWindow)

No es automático por foco: cada TextBox lo pide a mano con la misma señal HTTP
que mandan las páginas:

```csharp
txt.GotFocus  += (_, __) => _ = TecladoAsync(true);
txt.LostFocus += (_, __) => _ = TecladoAsync(false);
// TecladoAsync(true):  POST {base}api/teclado/abrir  body {"numerico":false,"titulo":"Nombre de la guía"}
// TecladoAsync(false): POST {base}api/teclado/cerrar body {}
```

Las teclas entran solas (la ventana del teclado es NOACTIVATE y el TextBox
conserva el foco). Al cerrar el panel o cambiar de pantalla interna, cerrar el
teclado. Catch mudo a propósito: sin teclado nativo el campo sigue editable con
teclado físico.

## 8. i18n (Traductor)

Textos nacen **siempre en castellano** en el origen.

- `Traductor.T("texto castellano")` para strings armados en código (toasts,
  labels dinámicos).
- `Traductor.Aplicar(this)` traduce el árbol lógico (incluye `IsVisible=false`)
  — es idempotente pero hay que **re-llamarla sobre toda UI construida en
  runtime** (al final de `Abrir()`, de cada `Mostrar(pantalla)` y de cada
  rebuild de lista).
- Datos del operario (nombres de guías/lotes) NO se traducen jamás.

## 9. Manual de cabina

Si el porteo mueve/agrega/saca algo que el operario ve, actualizar
`wwwroot/pages/ayuda.html` **en el mismo commit**, con rutas verificadas contra
los `.axaml` reales — no de memoria.

## 10. Checklist de cierre

1. `dotnet build SourceCode\PilotX.UI\PilotX.UI.csproj -c Release` → **0 errores**.
2. `dotnet build SourceCode\AgOpenGPS.sln -c Release` → **0 errores**.
3. La página HTML original queda intacta (celular/PWA la siguen usando).
4. `ayuda.html` actualizada si cambió algo visible.
5. Commit detallado en castellano. Si el porteo toca un ícono del tablero, va
   línea `Prueba:` (queda "en prueba" hasta confirmarse en cabina) — **nunca**
   `Cierra:` sin prueba real en cabina.
6. Sin `git push` ni deploy al taller salvo pedido explícito.
