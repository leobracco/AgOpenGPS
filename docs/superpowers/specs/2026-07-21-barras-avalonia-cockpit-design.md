# Barras/overlays del cockpit en Avalonia — Diseño

> Estado: **DISEÑO APROBADO** (2026-07-21). Próximo paso: plan de implementación
> (skill writing-plans). Alcance elegido con el usuario: reemplazar las 4 barras
> WebView2 del cockpit WinForms por barras nativas Avalonia, con la UI de barras
> en una **librería reutilizable** para que PilotX.Desktop la consuma después.

## Problema

En la pantalla de prueba (ViewX, Intel i3-3217U de 2012, 3.9 GB RAM, 2 núcleos)
PilotX "va lento en todo". El diagnóstico encontró **7 renderers de Chromium
(WebView2), ~730 MB**, con picos de CPU al 74 % en 2 núcleos. Cuatro de esos
renderers son las **barras HTML espejo** (`barra-superior.html`,
`barra-derecha.html`, `barra-abajo.html`, `menu-izquierda.html`), que se
repintan en vivo (velocidad, secciones, etc.).

Como parche inmediato ya se volvió a las barras nativas WinForms (GDI, livianas)
desactivando el flag `barras-html.on`. Este diseño es la mejora de fondo: barras
**nativas Avalonia** — livianas como las WinForms pero con el look del design
system y aceleradas por GPU (Skia).

## Restricción decisiva: net48 vs net9

- La app WinForms (`AgOpenGPS.csproj`, `AssemblyName=PilotX`) es **net48**
  (`SourceCode/Directory.Build.props` → `<TargetFramework>net48</TargetFramework>`).
- Avalonia 11 / PilotX.Desktop es **net9.0-windows**.

**No se puede cargar Avalonia (net9) dentro del proceso WinForms (net48).** Por
lo tanto embeber controles Avalonia en `FormGPS` es imposible sin migrar toda la
app fuera de net48 (fuera de alcance).

**Consecuencia arquitectónica:** las barras Avalonia viven en un **proceso
separado** (net9) que el cockpit WinForms **lanza** — igual que hoy lanza
`Updater.exe` / `CoreX.exe`. La comunicación es 100 % por la API HTTP existente
(`http://127.0.0.1:5180`): lectura de estado vivo y disparo de acciones. No se
comparte memoria ni se embeben frameworks.

## Arquitectura

Tres piezas: una librería reutilizable, una cáscara descartable, y un cambio
chico y reversible en WinForms.

### 1. `PilotX.Cockpit.Bars` — librería Avalonia (net9) — **lo reutilizable**

El activo de valor. No sabe nada de ventanas ni de procesos.

- **UserControls** (uno por barra, espejo funcional de las 4 HTML):
  `BarraSuperior`, `BarraDerecha`, `BarraInferior`, `MenuIzquierda`.
- **ViewModels** (CommunityToolkit.Mvvm): propiedades observables con los datos
  vivos; `ICommand` por cada botón/acción.
- **`CockpitStateClient`**: polling HTTP de `/api/aog/state` (y de los endpoints
  extra que cada barra necesite). Mismo patrón que `HudPoller` de PilotX.Desktop
  (HttpClient fino, cadencia ~4 Hz, tolerante a WebHost caído).
- **`CockpitActions`**: helper de POST a los endpoints que ya usan las barras
  HTML hoy, para disparar acciones en FormGPS.
- **Theme**: tokens alineados al design system oficial (paleta
  `#4ABA3E / #E2E7E2 / #F5F7F4 / #C5CFC5 / #101612 / #535E54`, claro/suave/gris,
  verde solo de acento). Reusar/portar `PilotXTheme.axaml`.

Los ViewModels y clients son **unit-testables** sin UI ni ventanas.

### 2. `PilotX.Bars.Host` — exe fino (net9, WinExe) — **la cáscara descartable**

Lo único que sabe de ventanas y del sistema operativo.

- Lo lanza el cockpit WinForms al activar el modo barras.
- Crea **4 ventanas borderless** ancladas a los bordes de la pantalla primaria
  (arriba / derecha / abajo / izquierda), cada una hospedando el `UserControl`
  correspondiente de la librería.
- Estilos de ventana: `SystemDecorations=None`, `ShowInTaskbar=false`,
  `Topmost=true`, `ShowActivated=false` + `WS_EX_NOACTIVATE` (Win32) para que los
  toques sobre las barras **no le roben el foco** a FormGPS, que corre fullscreen
  detrás. El centro queda libre → los toques al mapa van directo a FormGPS (no
  hay capa transparente encima; se evita el hit-testing transparente, que es la
  parte Win32 más frágil).
- **Posicionamiento**: contra `Screens.Primary` (kiosco = FormGPS maximizado).
  Recalcula al cambiar la resolución/pantalla.
- **Ciclo de vida**: recibe `--parent-pid=<pid>` del launcher; cuando ese proceso
  muere, el Host se cierra. FormGPS además mata el Host al cerrar (belt &
  suspenders). Single-instance (si ya hay un Host, no abre otro).
- **Publish self-contained** (`win-x64`, R2R como PilotX.Desktop): no depende de
  que la pantalla tenga net9 instalado.

### 3. Cambio en el cockpit WinForms (net48) — chico y reversible

En `SourceCode/GPS/Forms/GUI.FloatingMenu.cs`, donde hoy la activación de barras
abre las 4 ventanas WebView2 (`OpenAgroParallelWidget("pages/barra-superior.html"…)`
y las otras tres), pasa a **lanzar `PilotX.Bars.Host.exe`** (`Process.Start` con
`--parent-pid`). Se conserva el camino WebView2 detrás del flag `barras-html`
como fallback. **No se toca el render OpenGL ni la lógica del piloto.** El riesgo
queda concentrado en el proyecto nuevo, aislado del productivo.

## Flujo de datos

```
FormGPS (net48, fullscreen)  ──lanza──►  PilotX.Bars.Host.exe (net9)
        ▲                                         │  hostea 4x UserControl
        │  HTTP POST acciones                     │  (PilotX.Cockpit.Bars)
        │  (:5180 endpoints existentes)           │
        │                                         ▼
   AgpWebHost :5180  ◄──── HTTP GET /api/aog/state (4 Hz, polling) ────┘
```

- **Lectura**: Host poll `/api/aog/state` → ViewModels → barras se repintan.
- **Acción**: toque en botón → `ICommand` → `CockpitActions` POST al endpoint que
  ya usa la barra HTML equivalente → FormGPS ejecuta.

## El pago de la reutilización

Cuando PilotX.Desktop pase a ser el cockpit nativo, **referencia
`PilotX.Cockpit.Bars`** y mete los 4 `UserControl` directo en su ventana Avalonia
sobre el mapa — sin proceso aparte, sin ventanas separadas, sin glue WinForms. Se
descartan `PilotX.Bars.Host` y la llamada `Process.Start` en WinForms; la UI de
las barras queda intacta. Cero reescritura del activo de valor.

## Riesgos a resolver en el plan de implementación

1. **Mapeo botón → endpoint (primera tarea del plan).** Auditar los 4 HTML
   (`barra-superior/derecha/abajo/izquierda.html` + `menu-izquierda.html`) y
   listar cada acción. Las que hoy pegan a `/api/...` se reusan tal cual; las que
   usan el puente JS/postMessage del WebView2 necesitan un endpoint HTTP nuevo en
   el WebHost. Sin este mapeo completo no arranca la implementación.
2. **NoActivate / Topmost sobre FormGPS fullscreen.** Validar en la pantalla real
   que el toque a una barra no minimiza ni desactiva FormGPS, y que el toque al
   centro pasa al mapa. Avalonia `ShowActivated=false` + estilo Win32
   `WS_EX_NOACTIVATE` vía `TopLevel`/`IWindowImpl` o `SetWindowLong`.
3. **Runtime en la pantalla.** Publish self-contained obligatorio (la pantalla no
   tiene net9). Verificar tamaño/arranque en el i3 débil.
4. **RAM.** Sumar un proceso net9 (~80-120 MB) sigue siendo mucho menos que los
   ~730 MB de Chromium, pero medir en la pantalla (3.9 GB, apretada) tras el
   cambio para confirmar la ganancia neta.

## Testing

- **Unit** (en la librería): alimentar `CockpitStateClient` con snapshots falsos y
  verificar que los ViewModels exponen los valores esperados; verificar que cada
  `ICommand` dispara el POST correcto (HttpClient mockeado). Seguir las
  convenciones de test existentes del repo.
- **Manual** (en la pantalla ViewX): posicionamiento de las 4 barras, passthrough
  de toques al mapa, foco de FormGPS, ciclo de vida (cierre del Host al cerrar
  FormGPS), y medición RAM/CPU vs. las barras WebView2.

## Fuera de alcance

- Migrar el Hub completo (config.html + módulos) a Avalonia — es otro proyecto.
- Migrar el render del mapa / OpenGL (eso es PilotX.Desktop, stages ya en curso).
- Reanudar PilotX.Desktop como cockpit productivo — sigue pausado; este diseño
  no lo toca, solo deja la librería lista para que él la consuma cuando se retome.

## Nota de máquina

Esta implementación es **aditiva y aislada**: proyectos nuevos + un cambio chico
y reversible en `GUI.FloatingMenu.cs`. No toca render ni lógica del piloto, en
línea con la consigna de mantener esta máquina en trabajo visual/bajo riesgo. La
validación en la pantalla se hace con el fallback WebView2 disponible por si algo
no cierra.
