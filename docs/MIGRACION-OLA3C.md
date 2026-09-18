# Migración ola 3c — cierre de las pestañas de `config.html`

Rama `codex/pilotx-ui-new` · cierre 2026-08-16

La [ola 3a](MIGRACION-OLA3A.md) dejó nativo el **contenedor** de Configuración y
las cuatro pestañas de Vehículo. La [ola 3b](MIGRACION-OLA3B.md) se llevó las
siete de Implemento y Secciones. La **ola 3c** cierra las **cinco que quedaban
en el menú** — Máquina, Rumbo, Rolido, U-Turn y Tram — y además **cierra el bug
de switches** que la ola 3b había dejado abierto como deuda del motor.

Resultado: **las 16 filas del menú de Configuración son nativas. Ninguna cae al
WebView.**

Sigue siendo **strangler fig, no big-bang**. `pages/config.html` y
`js/config.js` **no se tocan ni se borran**: los usa la PWA del celular. El
fallback `IrATabAsync → OnRequestHtml` se deja **intacto** aunque hoy ninguna
fila lo use: es la red del próximo porteo y de cualquier deep-link viejo.

Guía de porteo: [`PORTING-AVALONIA.md`](PORTING-AVALONIA.md).

---

## 1. Estado de la ola

Verificación del cierre (2026-08-16):

- `dotnet build SourceCode\AgOpenGPS.sln -c Release` → **0 errores**, 11
  advertencias. Las 11 son **las mismas preexistentes de la ola 3b** y ninguna
  cae en un archivo de esta ola: `PilotX.UI\Views\DireccionPanel.cs` (CS8625
  ×6), `PilotX.UI\Views\MapGlSurface.cs` (CS0618 ×3, API de OpenTK obsoleta),
  `PilotX.Desktop\CrashHandler.cs` (CS8604 ×2) y
  `PilotX.Desktop\TecladoLinux.cs` (CS8603).
- `dotnet test SourceCode\AgOpenGPS.sln -c Release --no-build` → **374/374
  verdes, 0 con error, 0 omitidos**:

  | Assembly | Pruebas |
  |---|---|
  | `AgroParallel.Services.Tests` (net8.0) | 261 |
  | `AgOpenGPS.Core.Tests` (net48) | 53 |
  | `BenchX.Tests` (net9.0) | 36 |
  | `PilotX.Cockpit.Bars.Tests` (net9.0) | 21 |
  | `AgLibrary.Tests` (net48) | 3 |

### 1.1 Los tests NO cubren nada de esta ola — hay que decirlo

**Ningún test toca el `ConfigPanel` ni una sola de las cinco pestañas
portadas.** Son exactamente los mismos 374 de la ola 3b: servicios, core, BenchX
y barras del cockpit. Este porteo es UI de Avalonia y **no agregó ni una
prueba**. El total ni siquiera se movió.

O sea: el "374/374 verdes" dice que **no se rompió nada de lo que ya estaba
probado**, y nada más. **No es evidencia de que las pestañas anden.** La
evidencia real está en §2 (persistencia medida contra disco y reinicio) y en §4
(fallback y módulos probados contra el motor). La prueba de cabina, con táctil y
teclado nativo, **falta en las cinco**.

Por eso las cinco fueron al tablero como `Prueba:`, **ninguna como `Cierra:`**.

### 1.2 Pestañas cerradas en la ola 3c

| Pestaña (menú) | `?tab=` | Clase nativa | API que escribe | Persistencia verificada **con reinicio del motor** | Commits |
|---|---|---|---|---|---|
| Secciones › Máquina | `amachine` | `ConfigEditor/MaquinaTab.cs` | `/api/aog/config/maquina` (→ PGN 238) | **SÍ — 9 de 9.** Probado con combinaciones asimétricas y los dos bits del bitfield por separado | `563d33c9`, `57b2a189` |
| GPS / IMU › Rumbo | `heading` | `ConfigEditor/RumboTab.cs` | `/api/aog/config/rumbo` | **PARCIAL — 9 de 11.** `is_rtk` e `is_rtk_kill_autosteer` **no llegan a disco**. Es un bug del motor, no del porteo → §5.1 | `c000e298`, `9c6feee9` |
| GPS / IMU › Rolido | `roll` | `ConfigEditor/RolidoTab.cs` | `/api/aog/config/rolido` + `/rolido/accion` | **SÍ — 3 de 3** (`roll_filter`, `invert_roll`, `roll_zero`). Dos rondas, una desde la pantalla nativa | `ad42676e`, `8c59b003` |
| Otros › U-Turn | `uturn` | `ConfigEditor/UturnTab.cs` | `/api/aog/config/uturn` | **SÍ — 4 de 4** (`radius`, `distance_from_boundary`, `extension_length`, `smoothing`) | `a6ec617a` |
| Otros › Tram | `tram` | `ConfigEditor/TramTab.cs` | `/api/aog/config/tram` | **SÍ — 3 de 3** (`tram_width`, `display_tram_control`, `outer_inverted`) | `9caece62`, `01c7e94c` |

**5 pestañas cerradas. Ninguna probada en cabina.**

Método de medición en las cinco (el atajo miente — ver §2):
`POST → valor EN DISCO → matar el motor → arranque limpio → GET`.

### 1.3 El bug de switches de la ola 3b: CERRADO

`ffe723bf` cierra el **§4.2 de la ola 3b** — la deuda que quedó marcada como
"del motor, no se toca desde el carril de UI".

Síntoma: el operario habilitaba el switch de trabajo, apagaba la pantalla, y al
día siguiente arrancaba **con el switch apagado y sin aviso**; sembraba creyendo
que el corte por switch estaba activo.

Causa raíz: `CModuleComm` nace con todo apagado y **nada en el arranque del
motor headless copiaba los `setF_*` del perfil a `Mc`** — eso lo hacía
`LoadSettings` de `FormGPS`, que en headless no corre. Por qué persistían 3 de 5
y no los otros 2: el snapshot es **asimétrico** — tres campos se leen de
`Settings` (sobreviven) y `work_enabled` / `steer_enabled` se leen del **runtime**
(`_engine.Mc.*`), que acababa de volver a los defaults del constructor.

Peor que el síntoma visible: `isRemoteWorkSystemOn` también volvía a `false`, y
`CheckWorkAndSteerSwitch` entra al bloque de trabajo/dirección **solo** con ese
flag en `true`. Tras cada arranque el switch físico quedaba **inerte** —ni bien
ni al revés— hasta el próximo guardado.

Arreglo: `GuidanceEngineHost.CargarSwitchesRemotos()`, llamado desde `Start()` y
desde `EnginePerfilService.RecargarVehiculo()` (que tenía el mismo agujero al
activar un perfil). `isRemoteWorkSystemOn` se **deriva** de los dos
habilitadores en vez de leerse del XML: un perfil viejo con el flag en `true`
pero los dos switches deshabilitados no revive el bloque — ante la duda, el
switch remoto no manda.

El snapshot **sigue leyendo `Mc` a propósito**: el panel muestra lo que el motor
tiene aplicado de verdad, no lo que dice el archivo. Si vuelven a divergir, el
panel lo delata en vez de taparlo.

De yapa cierra el derivado P2 de la ola 3b (el guardado que reenviaba
`work_enabled:false` y borraba el `True` del XML).

**Sigue faltando** la prueba de banco con un switch físico cableado (ola 3b §5,
`tswitches` P3). El arreglo se verificó con reinicio real y con prueba de
control, pero **no con un contacto real en la mano**.

### 1.4 Acumulado: 16 de 19 pestañas

`config.html` tiene **19 pestañas reales** (`data-tab=`, sin contar el host
`modulo`). Después de la ola 3c:

- **16 nativas** — las 4 de la ola 3a, las 7 de la 3b y las 5 de esta.
- **3 en HTML** — `relay`, `display` y `botones`, **las tres huérfanas**: no
  están en el NAV nativo porque tampoco están en el menú del HTML. Ver §3.

**Ninguna fila del menú cae al WebView.** El `NAV` de `ConfigPanel.axaml.cs`
tiene 16 filas y las 16 con `Nativa = true`.

---

## 2. Persistencia — el atajo que miente

`EngineConfigVehiculoService.BuildSnapshot` **lee `Settings.Default` en
memoria**. "Guardar, releer y comparar" devuelve siempre lo que uno acaba de
mandar, **aunque el disco no se haya tocado nunca**. Es un verde falso.

El único método que vale:

```
POST  ->  mirar el valor EN DISCO           (Vehicles\<perfil>.XML
                                             o GuidanceEngineData\tool.json)
      ->  matar el motor (Stop-Process)
      ->  arranque limpio
      ->  GET                                ← acá recién se sabe
```

El motor headless se lanza **desde `bin\Release\net9.0\`, nunca desde
`win-x64\`** (la subcarpeta RID rompe `ResolveWwwroot()`: el Hub entero da 404 y
el mapa queda sin fondo).

> **Trampa de PowerShell 5.1 al medir:** `/api/aog/config` responde con **BOM** y
> `ConvertFrom-Json` explota con «Primitivo JSON no válido». Hay que recortar
> hasta el primer `{` antes de parsear. Si no, el snapshot parece vacío y se
> concluye cualquier cosa.

### 2.1 Quién persiste: acá es el perfil, no `tool.json`

A diferencia de la geometría del implemento de la ola 3b (que baja por
`ToolGeometryStore` a `GuidanceEngineData\tool.json`), **los 30 campos de las
cinco pestañas de esta ola viven en `Settings` y bajan a
`Vehicles\<perfil>.XML`** por el `Settings.Default.Save()` del dispatcher, que
acá **sí escribe porque hay perfil de vehículo**.

Consecuencia buena: **ninguna de las cinco sufre el pisotón del implemento
central** (`MapToToolConfig`) que arruina Timing y Pivote — §5.2. Esos campos no
están en `tool.json`, así que no hay nada que pisar.

Consecuencia a tener presente: la red es **una sola** y es el perfil. Sin perfil
de vehículo real `Settings.Save()` es **no-op** y estas cinco pestañas no
persisten nada.

---

## 3. Las tres huérfanas — la decisión

`relay`, `display` y `botones` **no son alcanzables desde ninguna parte de la
UI desde el 2026-08-03**, cuando salieron del menú por pedido explícito. Desde
entonces solo se llega escribiendo `?tab=` a mano.

**Verificado en este cierre** (grep sobre todo `SourceCode/`): **ningún botón,
ruta, menú ni handler emite `?tab=relay`, `?tab=display` ni `?tab=botones`**.
Los únicos `?tab=` que emite la app son `config.html?tab=tsections` (desde el
editor de VistaX) y `quantix.html?tab=shape`.

Las tres pantallas están **enteras y vivas**: sirven 200 con la página completa
(65 900 bytes) y el snapshot trae sus tres secciones con datos reales
(`relay` 1 clave, `display` 16, `botones` 19).

### 3.1 Qué controla cada una, y si eso sigue haciendo algo

Esto es lo que decide, y se midió por consumidores reales en el código — no por
lo que dice el nombre de la pantalla:

| Huérfana | Setting | ¿Alguien lo consume hoy? |
|---|---|---|
| **`relay`** | `setRelay_pinConfig` (24 pines) | **SÍ — hardware.** `CSettingsSender.cs:67` y `PgnDefinitions.cs:291` lo parsean y lo mandan **en el PGN al módulo de máquina**. Es el mapa de qué salida maneja qué sección. |
| **`display`** | `is_metric` (`setMenu_isMetric`) | **SÍ — y es crítico.** Es el **ÚNICO conmutador métrico/imperial de todo el producto**. De él cuelgan las unidades, los factores de conversión **y los límites de validación** de **12 pestañas nativas** (Dimensiones, Antena, Distancias, Offset, Pivote, Secciones, Rumbo, U-Turn, Tram…). |
| **`display`** | `num_guide_lines` (`setAS_numGuideLines`) | **SÍ.** `CABLine.numGuideLines` → `CABCurve.BuildCurveGuidelines` y `GuidanceDrawExtensions`: cuántas guías extra se construyen y se dibujan. |
| **`display`** | los otros 14 toggles (`polygons`, `speedo`, `keyboard`, `brightness`, `svenn_arrow`, `start_full_screen`, `log_elevation`, `floor`, `grid`, `extra_guides`, `direction_markers`, `section_lines`, `headland_distance`, `line_smooth`) | **NO. Cero consumidores.** Verificado uno por uno sobre todo `SourceCode/`: solo los lee el propio `EngineConfigVehiculoService` para devolverlos en el snapshot. Eran flags del render OpenGL de `FormGPS`, que el cockpit Avalonia no usa. |
| **`botones`** | los 19 (`feature_*`, `sound_*`, `auto_start_corex`, `auto_off_corex`, `shutdown_no_power`, `hardware_messages`) | **NO. Cero consumidores, los 19.** Los `feature_*` prendían entradas de los menús WinForms que las barras del cockpit reemplazaron; los `sound_*` no tienen a quién sonarle en headless; y el arranque de CoreX **no** mira `setDisplay_isAutoStartAgIO` — lo decide el flag `--corex` más un chequeo de proceso (`App.axaml.cs:220`). |

> Ojo con un falso positivo al grepear: `setFeatures.isHeadlandOn` **no** es
> `_bnd.isHeadlandOn` (`CBoundary`/`CHead`). El segundo se usa muchísimo; el
> primero, nunca. Son campos distintos con el mismo nombre corto.

### 3.2 La decisión

**No se toca el menú en esta ola. Se documenta y se eleva.**

Las tres salieron del menú **por pedido explícito del 2026-08-03**. Devolverles
la puerta es revertir una decisión del usuario, y eso **no se hace desde un
commit de cierre**. Lo que sí corresponde es poner sobre la mesa la consecuencia
que probablemente no estaba a la vista cuando se pidió sacarlas:

- **`relay` y las unidades quedaron sin ninguna vía de acceso.** Hoy, un módulo
  de máquina recableado **no se puede configurar desde PilotX**, y **no hay
  forma de pasar la pantalla a imperial** — ni en el panel nativo ni en la PWA
  (el HTML también las perdió del menú).
- Es un **agujero heredado, no una regresión de esta ola**: existe desde el
  2026-08-03 y estaba anotado en la ola 3b §3.2.

**Recomendación para la ola 3d** (a confirmar con el usuario antes de tocar
nada):

1. **`relay` → portar nativa, con fila en el NAV.** Es configuración de hardware
   que viaja en un PGN; que no se pueda tocar es un riesgo real en el lote. Su
   semántica es «Enviar + Guardar» explícito, igual que Máquina — `MaquinaTab`
   ya tiene el patrón resuelto.
2. **`display` → portar una pestaña REDUCIDA**, con lo único que sigue vivo:
   **Unidades (`is_metric`) + Cantidad de líneas guía (`num_guide_lines`)**.
   Nombre honesto tipo «Unidades y guías». Los otros 14 toggles **no se portan**.
3. **`botones` → baja, y los 14 toggles muertos de `display` con ella.** Son 33
   settings sin un solo consumidor. Portarlas sería dibujar 33 controles que no
   hacen nada: peor que no tenerlas, porque el operario los toca y cree que
   cambió algo.

La baja implica **borrar código vivo** (secciones del HTML, ramas de
`GuardarBotones`/`GuardarDisplay`, campos del DTO y del snapshot). **No se
ejecuta sin el visto bueno del usuario** — queda propuesta, no hecha.

Mientras tanto, el `NAV` y el fallback `IrATabAsync` se dejan **intactos**: si
mañana se decide devolverles la puerta, es **una fila por pestaña** con
`Nativa = false` y anda por el WebView sin tocar nada más (verificado en §4).

---

## 4. «Módulos y más…» — sigue siendo la puerta, y funciona

**Verificado en banco** (motor headless `--webhost` lanzado desde
`bin/Release/net9.0/`, `wwwroot` resuelto correctamente):

- **Cadena en código:** `ConfigPanel.axaml.cs:410`
  (`mas.Click → OnRequestHtml("pages/config.html")`) →
  `MainWindow.axaml.cs:394-397` (`CloseConfig()` + `NavigateTo(ruta)`).
- **`pages/config.html` → HTTP 200.** Desde ahí se llega a los **25 `data-mod=`**
  (24 páginas únicas; `quantix.html` aparece dos veces, una con `?tab=shape`).
- **Las 24 páginas de módulo responden 200**, una por una:
  `hub` · `quantix` · `vistax` · `flowx` · `sectionx` · `linex` · `stormx` ·
  `nodos` · `camaras` · `insumos` · `mapas` · `calculadora-siembra` ·
  `pid-lab` · `pwm-diag` · `orbitx` · `firmwares` · `actualizar` · `pwa-qr` ·
  `wifi` · `sistema` · `eventos` · `debug` · `sonidos` · `ayuda`.
- **Los deep-links huérfanos siguen sirviendo** la página completa:
  `?tab=relay`, `?tab=display` y `?tab=botones` → 200, 65 900 bytes cada uno.
  O sea que el fallback del §3.2 está **probado**, no supuesto.

### 4.1 Pero NO es la única puerta al WebView — corrección al enunciado

«Módulos y más…» es la única puerta al **shell embebido** (la barra lateral de
`config.html` con los 24 módulos en iframe modo widget). **No** es la única
puerta al WebView de la app. El inventario completo de llamadas a `NavigateTo(`
en `PilotX.UI` son **9**:

| Origen | Ruta | Nota |
|---|---|---|
| `ConfigPanel` › «Módulos y más…» | `pages/config.html` | la puerta al shell embebido |
| Menú SISTEMA › Firmwares | `pages/firmwares.html` | módulo, standalone |
| Menú SISTEMA › OrbitX | `pages/orbitx.html` | módulo, standalone |
| Menú SISTEMA › Debug | `pages/debug.html` | módulo, standalone |
| `NodosPanel` › detalle | `pages/nodo-detalle.html?uid=…` | no está en la lista `data-mod` |
| `NodosPanel` › asistente | `pages/setup.html` | no está en la lista `data-mod` |
| `SectionXPanel` › Configurar | `pages/sectionx.html` | módulo, standalone |
| `VistaXEditorPanel` › Insumos | `pages/insumos.html` | módulo, standalone |
| `VistaXEditorPanel` › Config central | `pages/config.html?tab=tsections` | **ver abajo** |

**Hallazgo menor, sin arreglar:** el último abre `tsections` **en el WebView**
aunque esa pestaña **es nativa desde la ola 3b**. El operario que entra a
Secciones por el editor de VistaX ve la pantalla HTML; el que entra por el menú
de Configuración ve la nativa. Dos pantallas distintas para la misma
configuración. No es peligroso (la HTML sigue andando y postea a la misma API),
pero es una inconsistencia que conviene resolver ruteando esa llamada a
`ShowConfig("tsections")`. **No se cambió acá**: toca el flujo de VistaX y no
hay quien lo pruebe en cabina en este cierre.

---

## 5. Deudas de backend que esta ola NO cierra

### 5.1 `GuardarRumbo`: ifs anidados sin llaves — nuevo, y muerde

**Confirmado vivo en este cierre** (`EngineConfigVehiculoService.cs:798+`). Al
comentar las asignaciones «solo display» de RTK quedaron los `if` **sin cuerpo
propio**, y cada uno se comió al siguiente:

```csharp
if (b.IsRtk.HasValue)
    // solo display, headless no lo dibuja: … = b.IsRtk.Value;
    if (b.IsRtkKillAutosteer.HasValue)
        // solo display, headless no lo dibuja: … = b.IsRtkKillAutosteer.Value;
        if (b.JumpFixDistance.HasValue)
            s.setGPS_jumpFixAlarmDistance = Clamp(b.JumpFixDistance.Value, 0, 1000);
```

Dos consecuencias:

1. `is_rtk` e `is_rtk_kill_autosteer` **nunca se persisten** (el POST responde
   `ok:true` igual).
2. **La peor:** `jump_fix_distance` **solo se guarda si el body trae los tres
   juntos**. Medido en banco: `POST {"jump_fix_distance":99}` suelto devuelve
   `ok:true` y **no guarda nada**.

`jump_fix_distance` es la alarma de salto de fix del GPS. Un cliente que mande
solo ese campo —la PWA, un script, un porteo futuro— cree que lo configuró y no
configuró nada, **sin ningún error**.

Mitigación en la UI, **no tapado**: `RumboTab` manda **siempre el body completo
de 9 campos** y relee el snapshot después de guardar, así el toggle de RTK
vuelve solo a la vista del operario en vez de quedar en verde mintiendo.

**El arreglo es de backend y es carril ajeno** (`COORDINACION-SESIONES.md`):
poner las llaves y decidir si los dos flags de RTK se persisten o se sacan del
contrato.

### 5.2 `MapToToolConfig` sigue pisando la geometría del implemento

Sin cambios respecto de la ola 3b §4.1. **No afecta a ninguna pestaña de esta
ola** (§2.1), pero sigue revirtiendo solos a **Timing** y **Pivote** cada vez
que se guarda el implemento activo. Sigue siendo lo que impide declarar "anda"
en esas dos.

---

## 6. Qué mantiene vivo a WebView2 en TODA la app

Esta es la lista concreta, no solo la de Configuración.

**Dónde vive la dependencia.** `PilotX.UI` **no** referencia WebView2: solo
conoce la interfaz `IWebViewHost` / `IWebViewHandle` (`MainWindow.axaml.cs:82`).
Quien la implementa es `PilotX.Desktop\DesktopWebViewHost.cs`, con los paquetes
`WebView.Avalonia` y `WebView.Avalonia.Desktop` (que en Windows envuelven a
WebView2). El head Android trae su propio `AndroidWebViewHost`.

**La abstracción ya está hecha.** Sacar WebView2 = dejar de necesitar
`DesktopWebViewHost`, es decir **que no quede ninguna página HTML alcanzable**.

### 6.1 Páginas que YA tienen panel nativo (no bloquean)

De los 24 módulos, **11** tienen equivalente nativo en `PilotX.UI/Views/` y el
menú SISTEMA los abre nativos: `hub` (`HubPanel`), `quantix` (`QuantiXPanel` +
`QuantiXEditorPanel`), `vistax` (`VistaXPanel` + `VistaXEditorPanel`), `flowx`
(`FlowXPanel` + `FlowXEditorPanel`), `sectionx` (`SectionXPanel`), `stormx`
(`StormXPanel`), `nodos` (`NodosPanel`), `camaras` (`CamarasPanel`),
`actualizar` (`ActualizarPanel`), `sistema` (`SistemaPanel`), `sonidos`
(`SonidosPanel`).

Siguen existiendo como HTML porque **la PWA del celular los usa**, pero eso no
obliga al Desktop a instanciar un WebView.

### 6.2 Lo que SÍ bloquea — 15 páginas sin panel nativo

**Módulos sin equivalente nativo (13):**

| Página | Grupo del menú | Alcanzable por |
|---|---|---|
| `linex.html` | Módulos | «Módulos y más…» |
| `insumos.html` | Campo | «Módulos y más…» **+ `VistaXEditorPanel`** |
| `mapas.html` | Campo | «Módulos y más…» |
| `calculadora-siembra.html` | Herramientas | «Módulos y más…» |
| `pid-lab.html` | Herramientas | «Módulos y más…» |
| `pwm-diag.html` | Herramientas | «Módulos y más…» |
| `orbitx.html` | Cloud | «Módulos y más…» **+ menú SISTEMA** |
| `firmwares.html` | Cloud | «Módulos y más…» **+ menú SISTEMA** |
| `pwa-qr.html` | Cloud | «Módulos y más…» |
| `wifi.html` | Mantenimiento | «Módulos y más…» |
| `eventos.html` | Mantenimiento | «Módulos y más…» |
| `debug.html` | Mantenimiento | «Módulos y más…» **+ menú SISTEMA** |
| `ayuda.html` | Mantenimiento | «Módulos y más…» + menú SISTEMA (`?mod=ayuda.html`) |

**Páginas fuera de la lista `data-mod` (2):** `nodo-detalle.html?uid=…` y
`setup.html`, las dos desde `NodosPanel`.

**Pestañas de configuración (0).** Ninguna bloquea: las 16 del menú son nativas
y las 3 huérfanas no las emite nadie. Si se decide devolverles la puerta por
WebView (§3.2), **`relay` y `display` vuelven a la lista de bloqueantes** —
razón de más para portarlas en vez de reponer el botón.

### 6.3 A cuánto estamos

- **Configuración: cerrada.** 16/16 pestañas del menú nativas. Es la mitad
  grande del trabajo y ya no aporta al bloqueo.
- **Faltan 15 páginas.** Ninguna es de guiado ni de siembra en marcha: son
  herramientas de banco (`pid-lab`, `pwm-diag`, `calculadora-siembra`),
  cloud/mantenimiento (`orbitx`, `firmwares`, `pwa-qr`, `wifi`, `eventos`,
  `debug`, `ayuda`), campo (`insumos`, `mapas`), `linex` y las dos de nodos.
- **Mientras quede UNA sola alcanzable, WebView2 sigue instanciándose** y el
  Desktop sigue pagando su RAM (mitigado con la liberación a los 3 min ocioso,
  `--webview-ocioso`).
- **`config.html` y `config.js` NO se borran nunca**, se porte lo que se porte:
  los usa la PWA del celular. Lo que se retira es la **dependencia del Desktop**,
  no el HTML.

Esto es la meta de la **migración Avalonia total, diferida al 25/08** (alcance
aprobado; el plan vive fuera del repo, en el scratchpad de la sesión).

---

## 7. Resumen honesto

- **5 pestañas cerradas** (Máquina, Rumbo, Rolido, U-Turn, Tram) → **16 de 19**,
  y **ninguna fila del menú de Configuración cae al WebView**.
- **Build 0 errores. Tests 374/374** — pero **ningún test cubre lo portado**, y
  el total no se movió respecto de la ola 3b.
- **Persistencia real con reinicio: 28 de 30 campos.** Los 2 que faltan
  (`is_rtk`, `is_rtk_kill_autosteer`) son un **bug del motor** que además hace
  que `jump_fix_distance` se pierda en silencio si va solo — §5.1. Está
  declarado, no tapado.
- **Ninguna de las cinco probada en cabina.** Las cinco fueron al tablero como
  `Prueba:`.
- **Se cerró el bug de switches de la ola 3b** (`ffe723bf`), pero **falta la
  prueba con un switch físico cableado**.
- **Las tres huérfanas siguen sin puerta.** `relay` (pines del PGN) y las
  unidades (`is_metric`, de la que dependen 12 pestañas) **no se pueden tocar
  desde ninguna parte de PilotX**. Es un agujero **heredado del 2026-08-03**, no
  una regresión de esta ola, y **la decisión de reponerlas o darlas de baja es
  del usuario** — acá queda la evidencia de qué está vivo y qué está muerto
  (§3.1).
- **Para sacar WebView2 faltan 15 páginas**, ninguna de guiado ni de siembra.
