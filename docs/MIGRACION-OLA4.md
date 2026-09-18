# Migración ola 4 — los diálogos de la labor y la puerta de Módulos

Rama `codex/pilotx-ui-new` · cierre 2026-08-16

Las olas [3a](MIGRACION-OLA3A.md), [3b](MIGRACION-OLA3B.md) y
[3c](MIGRACION-OLA3C.md) se llevaron **Configuración entera**: las 16 filas del
menú son nativas y ninguna cae al WebView.

La **ola 4** cambia de frente. Ya no es el menú de ajustes: son las **pantallas
que el operario abre CON LA MÁQUINA ANDANDO** — contorno, cabecera, huellas,
suavizar la guía, corregir la posición, repetir un camino grabado — más la
**grilla de Módulos**, que era la última puerta que abría una versión HTML de
pantallas que ya existían en Avalonia.

Estas pantallas son distintas de las de Configuración por un motivo concreto:
**se usan mirando el mapa**. Una ventana de Chromium parada encima del lote no
es una molestia estética — el operario marca un lindero o elige un camino
grabado *viendo dónde está el tractor*, y la ventana le tapaba justo eso.

Sigue siendo **strangler fig, no big-bang**. `pages/*.html` y `js/*.js` **no se
tocan ni se borran**: los usa la PWA del celular y el head Android. Lo que se
retira es la **dependencia del Desktop**, no el HTML.

Guía de porteo: [`PORTING-AVALONIA.md`](PORTING-AVALONIA.md).

---

## 1. Estado de la ola

Verificación del cierre (2026-08-16), corrida completa en este repo:

- `dotnet build SourceCode\AgOpenGPS.sln -c Release` → **0 errores**, 11
  advertencias.
- `dotnet test SourceCode\AgOpenGPS.sln -c Release --no-build` → **374/374
  verdes, 0 con error, 0 omitidos**:

  | Assembly | Pruebas |
  |---|---|
  | `AgroParallel.Services.Tests` (net8.0) | 261 |
  | `AgOpenGPS.Core.Tests` (net48) | 53 |
  | `BenchX.Tests` (net9.0) | 36 |
  | `PilotX.Cockpit.Bars.Tests` (net9.0) | 21 |
  | `AgLibrary.Tests` (net48) | 3 |

Las **11 advertencias son exactamente las mismas de las olas 3b y 3c** y
**ninguna cae en un archivo de esta ola**:

| Archivo | Advertencia |
|---|---|
| `PilotX.UI\Views\DireccionPanel.cs` | CS8625 ×6 |
| `PilotX.UI\Views\MapGlSurface.cs` | CS0618 ×3 (API de OpenTK obsoleta) |
| `PilotX.Desktop\CrashHandler.cs` | CS8604 ×2 |
| `PilotX.Desktop\TecladoLinux.cs` | CS8603 |

### 1.1 Los tests NO cubren nada de esta ola — hay que decirlo

**Ningún test toca uno solo de los nueve paneles portados.** Son exactamente los
mismos 374 de las olas 3b y 3c: servicios, core, BenchX y barras del cockpit.
Este porteo es UI de Avalonia y **no agregó ni una prueba**. El total no se
movió: 374 antes, 374 después.

O sea: el "374/374 verdes" dice que **no se rompió nada de lo que ya estaba
probado**, y nada más. **No es evidencia de que los nueve paneles anden.**

Y en esta ola eso pesa **más** que en las anteriores. Configuración se podía
verificar contra disco (POST → archivo → matar el motor → arranque limpio →
GET). Estas pantallas **no guardan un formulario: dibujan sobre el mapa vivo**.
Contorno, Cabecera, Tramlines y Rutas grabadas solo se pueden validar **mirando
el mapa con el tractor moviéndose**, y eso **no pasó**: no hay banco ni cabina
en este cierre.

Por eso las nueve fueron al tablero como `Prueba:`, **ninguna como `Cierra:`**.

### 1.2 Las nueve pantallas cerradas en la ola 4

| # | Pantalla | HTML que reemplaza | Panel nativo | Commit feat | Commit fix |
|---|---|---|---|---|---|
| 1 | **Módulos** (grilla de Configuración) | `config.html` (menú) | `Views\ConfigEditor\ModulosTab.cs` | `94069c7b` | `c7f1b48d`, `3b45498c` |
| 2 | **Contorno / lindero** | `contorno.html` | `Views\ContornoPanel.cs` | `71615e28` | `3b201bec` |
| 3 | **Cabecera** | `cabecera.html` | `Views\CabeceraPanel.cs` | `acefc973` | `bc504fcf` |
| 4 | **Cabecera por líneas** | `cabecera-lineas.html` | `Views\CabeceraLineasPanel.cs` + `CabeceraLineasCanvas.cs` | `cb9778c7` | `840d0541` |
| 5 | **Tramlines** (huellas) | `tramline.html` | `Views\TramSimplePanel.cs` | `6f73c217` | `d17bfcd8` |
| 6 | **Tramlines multi** (constructor) | `tramlines.html` | `Views\TramMultiPanel.cs` + `TramMultiLienzo.cs` | `ad8fe940` | `ea4daceb` |
| 7 | **Suavizar AB** (24º port) | `suavizar-ab.html` | `Views\SuavizarAbPanel.cs` | `e1ee858a` | `556bff11` |
| 8 | **Corregir posición** (25º port) | `corregir-posicion.html` | `Views\CorregirPosicionPanel.cs` | `8447a675` | `1c555798` |
| 9 | **Rutas grabadas** (26º port) | `recpath.html` | `Views\RecPathPanel.cs` | `b26472c8` | `722af699` |

Fuera de tabla, arrastre de la ola anterior:

| Pantalla | Qué se arregló | Commit |
|---|---|---|
| Dirección | el vúmetro del sensor de rueda ceraba al tocarlo | `38a01343` |

**20 commits en total: 9 `feat` + 11 `fix`.** La proporción no es casual y vale
anotarla: **cada porteo necesitó al menos un arreglo posterior**. Ninguno salió
bien de una. Los `fix` no son cosmética — son bugs que el HTML no tenía y que el
porteo introdujo:

- `722af699` — Rutas grabadas **borraba el `.rec` del lote equivocado**. El peor
  de la ola: pérdida de datos del usuario.
- `840d0541` — reabrir "Cabecera por líneas" **borraba las líneas ya marcadas**.
- `ea4daceb` — Tramlines multi **abría mintiendo** la pasada de inicio y el tram
  exterior: mostraba valores que no eran los del motor.
- `3b201bec` — Contorno **pisaba el Offset solo** y dejaba un poll colgado.
- `bc504fcf` — la Cabecera nativa **no mostraba estado ni avisos**: el
  `Traductor` pisaba el texto vivo.
- `d17bfcd8`, `556bff11` — paneles que **no se cerraban** al abrir otro o al
  cambiar de lote (dos pantallas dibujando sobre el mismo mapa).

Esto es el argumento más fuerte de §1.1: **si seis de nueve porteos tuvieron un
bug de datos o de estado que solo apareció usándolos, los que todavía no se
usaron en cabina no están cerrados.**

### 1.3 Módulos: se acabó la puerta duplicada

`94069c7b` merece su párrafo porque no es un porteo más. El botón «Módulos y
más…» abría `pages/config.html` y el operario elegía del menú HTML — pero **la
mitad de esos módulos ya eran pantallas nativas** (QuantiX, VistaX, FlowX,
SectionX, StormX, Nodos, Cámaras, Sistema, Sonidos, Actualizar, CoreX-ECU, Hub).
Entrando por ahí se abría **la versión HTML de una pantalla que ya existía en
Avalonia**: dos puertas a la misma cosa, y cuál veías dependía de por dónde
entraste.

`ModulosTab` es ahora **la única puerta**, y cada módulo abre lo que hay de
verdad. La regla quedó escrita en el encabezado del archivo:

- `Clave != null` → **panel nativo** (`CfgCtx.AbrirPanelNativo`, lo resuelve
  `MainWindow`). **13 módulos.**
- `Ruta != null` → **página del Hub embebida en la misma tarjeta**
  (`CfgCtx.AbrirHtmlEmbebido`). **13 módulos.**
- Nunca los dos. Si hay pantalla nativa, la HTML no se ofrece.

Y **la lista no miente**: si el equipo no tiene navegador embebido
(`App.WebViewHost == null`), los módulos que siguen siendo HTML se muestran
**apagados y con el motivo escrito** (`ModulosTab.cs:135`), en vez de ofrecer un
botón que no hace nada.

`c7f1b48d` cerró el otro agujero: los módulos HTML se abren **DENTRO de la
tarjeta**, con la ✕ y el menú al costado. Antes se iban a pantalla completa y el
operario quedaba **sin salida visible**.

`3b45498c` cerró el mismo patrón en VistaX: "Config central" abría el WebView de
Secciones cuando **Secciones es nativa desde la ola 3b** — dos pantallas
distintas para la misma config, con el riesgo de que una muestre lo que la otra
ya cambió.

---

## 2. Los `pendiente-*.md`

**No hay ninguno, y no es un olvido: es el estado real.**

Se buscó en el repo (rastreados y sin rastrear) y en el scratchpad de esta
sesión:

```
git ls-files | grep -i pendiente          → vacío
find . -iname "*pendiente*" (sin .git/obj/bin) → vacío
```

Los `pendiente-config-<tab>.md` que citan la
[ola 3a](MIGRACION-OLA3A.md) (§208) y la [ola 3b](MIGRACION-OLA3B.md) (§361)
vivían en `scratchpad/migracion/` de **aquellas** sesiones — scratchpad es
efímero y **esos archivos ya no existen**. Lo que se salvó de ellos es lo que
quedó volcado en los propios `MIGRACION-OLA3*.md`, que sí están en el repo.

**Lo que esta ola deja pendiente está en este documento (§3 y §4), no en
archivos sueltos.** Es a propósito: un `pendiente-*.md` en el scratchpad se
pierde en el próximo `/clear`; una sección de un doc commiteado, no.

---

## 3. INVENTARIO REAL — qué abre WebView desde la pantalla del tractor

Esta es la parte que hay que leer si la pregunta es *"¿cuánto falta para sacar
WebView2?"*.

### 3.0 Aviso de método: los cuatro términos del grep NO alcanzan

El inventario pedido era grep de `OpenDialogPage` / `OpenDialogUrl` /
`NavigateTo` / `MostrarHtmlEmbebido`. **Ese grep se pierde un WebView.**

`AbrirEcuConfig` (`MainWindow.axaml.cs:2845-2854`) crea su propio
`IWebViewHandle` y llama a `.Navigate()` directo, sin pasar por ninguno de los
cuatro. La pestaña "Configurar" de CoreX-ECU **es un Chromium** y no aparece en
esa búsqueda.

El inventario de abajo se hizo por el camino correcto: **grep de
`App.WebViewHost.Create` + `.Navigate(`**, que es la única forma de instanciar un
WebView en esta app. Es la lista completa.

### 3.1 Las cuatro superficies WebView del Desktop

`PilotX.UI` **no** referencia WebView2: solo conoce la interfaz `IWebViewHost` /
`IWebViewHandle` (`Services/IWebViewHost.cs`). Quien la implementa es
`PilotX.Desktop\DesktopWebViewHost.cs`, con `WebView.Avalonia` /
`WebView.Avalonia.Desktop` (que en Windows envuelven a WebView2). El head
Android trae su propio host.

**La abstracción ya está hecha.** Sacar WebView2 = que no quede **ninguna página
HTML alcanzable** desde el Desktop, y entonces `DesktopWebViewHost` deja de
registrarse (`PilotX.Desktop\Program.cs:77`).

Hoy hay **cuatro** superficies vivas en la pantalla del tractor (`MainWindow`):

| # | Campo | Dónde se crea | Cómo se alimenta | Forma |
|---|---|---|---|---|
| 1 | `_webView` | `MainWindow.axaml.cs:1420` / `:1641` | `NavigateTo()` → `ShowWebView()` | pantalla completa con flecha «←» |
| 2 | `_dialogWebView` | `MainWindow.axaml.cs:1920` | `OpenDialogPage()` / `OpenDialogUrl()` | ventana-diálogo 820×600 |
| 3 | `_ecuConfigWebView` | `MainWindow.axaml.cs:2850` | `AbrirEcuConfig()`, ruta **hardcodeada** | embebido en la card de CoreX-ECU |
| 4 | `_web` (ConfigPanel) | `ConfigPanel.axaml.cs:594` | `MostrarHtmlEmbebido()` | embebido en la tarjeta de Configuración |

Fuera de la pantalla del tractor, **no cuentan para este inventario** pero
existen:

- `MainView._webView` (`Views\MainView.axaml.cs:329`) — head **Android /
  single-view**. En Desktop solo se monta con el flag `UseSingleView`
  (`App.axaml.cs:176`); el arranque normal usa `MainWindow` (`:180`).
- **Modo widget flotante** (`MainWindow.axaml.cs:903`) — modo de lanzamiento
  donde la ventana entera *es* una página HTML. No es la pantalla del tractor.

### 3.2 Página por página: quién la abre

**25 archivos HTML distintos** siguen siendo alcanzables desde la pantalla del
tractor en **modo cockpit** (el modo real de cabina). Ordenados por dónde está
la puerta:

#### A · Menú HERRAMIENTAS (barra de la pasada) — `_dialogWebView`

Botones de `MainWindow.axaml:1021-1190`, cableados en `MainWindow.axaml.cs:711-718`.

| Botón | Comando | Página |
|---|---|---|
| Conteo de semillas | `conteo_semillas` | `vistax-prueba.html` |
| Calculadora | `calculadora` | `calculadora-siembra.html` |
| Eventos | `visor_eventos` | `eventos.html` |
| Gráfico dirección | `grafico_direccion` | `grafico-direccion.html` |
| Gráfico XTE | `grafico_xte` | `grafico-xte.html` |

Los otros botones del menú (Cámaras, Corregir posición, Suavizar AB, Sonidos,
CoreX-ECU) **ya abren panel nativo** — los dos últimos porteos de esta ola
salieron justamente de acá.

#### B · Menú izquierda (`MenuIzquierda.axaml`) — `_dialogWebView`

| Comando | Página | Nota |
|---|---|---|
| `grafico_rumbo` | `grafico-rumbo.html` | |
| `chequeo_roll` | `grafico-correccion.html` | |
| `grafico_direccion` | `grafico-direccion.html` | también en Herramientas |
| `grafico_xte` | `grafico-xte.html` | también en Herramientas |
| `visor_eventos` | `eventos.html` | también en Herramientas |
| `conteo_semillas` | `vistax-prueba.html` | también en Herramientas |

#### C · Barra derecha (`BarraDerecha.axaml:165`) — `_dialogWebView`

| Comando | Página |
|---|---|
| `bandera` | `banderas.html` |

#### D · Menú SISTEMA (`SistemaMenu`, `MainWindow.axaml:746`) — `_dialogWebView`

Éste es el menú SISTEMA de verdad: un `Border` + `IsVisible` (como manda la
regla de no usar Flyout sobre el mapa GL), colocado bajo su botón por
`UbicarSistemaMenu()`. Wiring en `MainWindow.axaml.cs:663-674`.

| Ítem | Comando | Página |
|---|---|---|
| Perfiles del vehículo | `perfil_gestion` | `perfiles.html` |
| Ayuda | `ayuda` | `config.html?mod=ayuda.html` |

El tercer ítem (Idioma → es/en/pt) **ya es nativo**: despliega in-place y llama a
`CambiarIdiomaAsync`.

#### D-bis · Botón Tools `[T]` — NO se ve en cabina

Acá hay que corregir algo, porque es fácil contarlo mal (yo lo conté mal en el
primer pase de este inventario).

`OnNavFirmwares` / `OnNavOrbitX` / `OnNavDebug` (`MainWindow.axaml.cs:5655-5657`)
son los únicos `NavigateTo` que quedan en el menú, y **son los tres que llevan a
`_webView` a pantalla completa**. Pero cuelgan de un `MenuFlyout` del botón
`BtnTools` (`MainWindow.axaml:1284-1312`), que vive dentro de
`Border Name="BottomToolbar"` (`:1238`).

**`BottomToolbar` está oculto en modo cockpit** —
`MainWindow.axaml.cs:944`, con el comentario *"Las 4 barras del cockpit
reemplazan el HudBar + BottomToolbar placeholder"*. En la pantalla del tractor
ese botón **no existe**. Y aunque se lo hiciera visible, es un `MenuFlyout`:
sobre el mapa GL **no se dibuja y no logea error** (regla del repo).

Conclusión: **`firmwares.html`, `orbitx.html` y `debug.html` NO son alcanzables
en cabina por esta puerta.** Siguen siéndolo por la grilla de Módulos (§3.2-F),
así que **no salen de la lista de bloqueantes** — pero el trabajo de portarlas
se acredita al paso de Módulos, no a uno propio.

#### E · Botones "Configurar" de paneles que YA son nativos

Este grupo es el más molesto: **el panel es nativo pero su botón de config
despierta Chromium igual.**

| Panel nativo | Callback | Página | Línea |
|---|---|---|---|
| `CamarasPanel` | `OnRequestConfigurar` | `config.html?mod=camaras.html` | `:749`, `:3067` |
| `NodosPanel` | `OnRequestDetalle` | `nodo-detalle.html?uid=…` | `:762` |
| `NodosPanel` | `OnRequestAsistente` | `setup.html` | `:763` |
| `SectionXPanel` | `OnRequestConfigurar` | `sectionx.html` | `:820` |
| `VistaXEditorPanel` | `OnRequestAbrirInsumos` | `insumos.html` | `:854` |
| `CoreXEcuPanel` | `OnConfigOpen` → `AbrirEcuConfig` | `corex-ecu.html?widget=1` | `:868`, `:2854` |

**Cámaras es un caso especial y hay que llamarlo por su nombre.** Desde la ola
3c, Configuración es 100% nativa (`ConfigPanel`) — pero el botón "Configurar" de
`CamarasPanel` abre **`pages/config.html`**, el HTML. Es **exactamente la puerta
duplicada que `ModulosTab` (§1.3) se creó para matar**, sobreviviendo en otro
lado. La causa de fondo: el formulario de IP/usuario/clave por cámara **no tiene
casa nativa** — Cámaras es un *módulo* (`Clave = "camaras"` → `CamarasPanel`), no
una pestaña de `ConfigPanel`, así que no hay `ShowConfig("camaras")` al que
apuntar. Se arregla portando ese formulario **adentro de `CamarasPanel`**, no
redirigiendo el botón.

#### F · Configuración › Módulos — `_web` del `ConfigPanel`, embebido

Las 13 filas de `ModulosTab.MODS` que todavía tienen `Ruta` (`ModulosTab.cs:57-127`):

| Grupo | Módulo | Página |
|---|---|---|
| Módulos | LineX | `linex.html` |
| Campo | Insumos | `insumos.html` |
| Campo | Mapas | `mapas.html` |
| Herramientas | Calculadora | `calculadora-siembra.html` |
| Herramientas | Lab PID | `pid-lab.html` |
| Herramientas | Diagnóstico PWM | `pwm-diag.html` |
| Cloud | OrbitX | `orbitx.html` |
| Cloud | Firmwares | `firmwares.html` |
| Cloud | Conectar celular | `pwa-qr.html` |
| Mantenimiento | Red WiFi | `wifi.html` |
| Mantenimiento | Eventos | `eventos.html` |
| Mantenimiento | Debug | `debug.html` |
| Mantenimiento | Ayuda | `ayuda.html` |

Los otros 13 módulos (`Clave != null`) abren panel nativo: Hub, QuantiX, VistaX,
FlowX, SectionX, StormX, CoreX-ECU, Nodos, Cámaras, Prescripciones, Actualizar,
Sistema, Sonidos.

#### G · `OpenDialogUrl` — no es una página del Hub

| Comando | URL | Qué es |
|---|---|---|
| `corex` | `http://127.0.0.1:5181/` | dashboard de CoreX servido por el **motor** |

**No se porta como las demás**: no es un `pages/*.html` del wwwroot, es el
`CoreXEnginePanel` del engine. O se le hace un panel nativo que consuma su API,
o se acepta que es la última razón para tener un navegador. Se marca aparte
porque **decidirlo es del usuario, no del porteo**.

### 3.3 Las 11 entradas MUERTAS del router

El `switch` de páginas (`MainWindow.axaml.cs:4345-4400`) tiene 11 entradas que
**ninguna barra ni menú nativo emite**. Se verificó grepeando cada comando en
`PilotX.Cockpit.Bars\Views\*.axaml` y en `MainWindow.axaml`, **y además
revisando el wiring por `Click` de los dos menús que no usan
`CommandParameter`** (`SistemaMenu` y `HerramientasMenu`) — que es donde se me
habían escapado dos:

`todos_ajustes`, `colores`, `colores_sec`, `mapeo_color`, `perfil_nuevo`,
`perfil_cargar`, `directorios`, `bandera_latlon`, `sim_coords`,
`asistente_direccion`, `lote_kml`.

**`perfil_gestion` y `ayuda` NO están muertos**, aunque el grep por
`CommandParameter` diga que sí: los emite el menú SISTEMA por `Click` +
`RouteCockpitCommand` (`MainWindow.axaml.cs:668`, `:673`). Lo mismo pasaba con
`calculadora`, que se emite desde el menú HERRAMIENTAS (`:712`). **Grepear solo
`CommandParameter=` en los `.axaml` da un inventario incompleto** — hay que
cruzarlo con los `Click` de `MainWindow.axaml.cs`.

`lote_kml` está confirmado muerto por partida doble: `LotePanel.cs:20` dice
explícitamente *"Sin KML ni ISO-XML en el menú: KML salió por pedido del
usuario"*.

**Pero NO se borran todavía, y el motivo importa.** Estas entradas **no son
código muerto probado**: son código *sin puerta conocida*. Una página HTML
abierta dentro del WebView puede emitir un comando al host, y ese camino **no se
auditó**. Borrarlas ahora es apostar a que nadie las llama. **Auditar los
emisores desde el lado JS es su propia tarea** (§4, paso 0) y hasta que se haga
**no cuentan como cerradas ni como bloqueantes**.

### 3.4 A cuánto estamos, sin maquillaje

- **25 archivos HTML alcanzables** desde la pantalla del tractor.
- **4 superficies WebView** vivas en `MainWindow`. Las cuatro son alcanzables en
  cockpit: `_dialogWebView` por Herramientas/menú izq./barra der./SISTEMA,
  `_webView` por los "Configurar" de Nodos/SectionX/VistaX, `_ecuConfigWebView`
  por CoreX-ECU y `_web` por Configuración › Módulos.
- **Mientras quede UNA sola alcanzable, WebView2 se sigue instanciando** y el
  Desktop paga su RAM (mitigado, no resuelto, por la liberación a los 3 min
  ocioso — `--webview-ocioso`).
- **Lo bueno:** de las 25, **ninguna es de guiado ni de siembra en marcha**. La
  ola 4 se llevó justamente las que sí lo eran (contorno, cabecera, huellas,
  rutas grabadas). Lo que queda son gráficos de diagnóstico, herramientas de
  banco, cloud y mantenimiento.
- **La corrección al conteo de la ola 3c:** §6.2 de `MIGRACION-OLA3C.md` decía
  «15 páginas bloqueantes». **Ese número estaba mal por defecto** — contaba los
  módulos y las dos de Nodos, pero **no contaba los diálogos de la labor** (que
  la ola 4 cerró), **ni `corex-ecu.html`** (que el grep de cuatro términos no ve,
  §3.0), **ni `perfiles.html`** (que solo aparece revisando los `Click`, §3.3).
  El número honesto de hoy es **25**, y es más alto que el de la ola anterior
  **no porque se haya retrocedido, sino porque recién ahora está bien contado.**

---

## 4. Qué falta EXACTAMENTE para sacar WebView2 del Desktop

Ordenado por **lo que destraba más por menos trabajo**. Cada paso dice qué
superficie WebView apaga.

### Paso 0 — Auditar quién emite comandos desde el JS *(prerrequisito)*

Sin esto **ningún paso posterior se puede declarar cerrado**, porque no se sabe
si una página abre otra. Concreto: grepear en `wwwroot/js` y `wwwroot/pages`
quién postea comandos al host, y cruzarlo contra las 11 entradas de §3.3.
Resultado esperado: o se borran las 13, o aparece una puerta que este inventario
no vio. **Es barato y es el que más incertidumbre saca.**

### Paso 1 — Los 6 gráficos de diagnóstico

`grafico-direccion.html`, `grafico-xte.html`, `grafico-rumbo.html`,
`grafico-correccion.html`, `vistax-prueba.html`, `banderas.html`.

Apaga: **Herramientas (A), menú izquierda (B) y barra derecha (C) por completo.**
Es el grupo más grande de puertas por página portada, y son **plots de series
que llegan por el poll que ya existe** — no hay backend nuevo. Patrón:
`Views\ConfigEditor\*` para el shell, y el lienzo custom ya resuelto en
`TramMultiLienzo.cs` / `CabeceraLineasCanvas.cs`.

### Paso 2 — `calculadora-siembra.html`

Apaga el botón Calculadora de Herramientas y una fila de Módulos. **Es cálculo
puro, sin estado ni motor** — el más simple de la lista y probablemente el
mejor primer porteo para alguien nuevo en el patrón.

### Paso 3 — Los 2 del menú SISTEMA

`perfiles.html` (`perfil_gestion`) y `config.html?mod=ayuda.html` (`ayuda`).

Apaga: **menú SISTEMA (D) por completo.** Con los pasos 1, 2 y 3 hechos,
**`_dialogWebView` (superficie 2) queda sin ningún emisor desde el tractor** —
es la superficie con más puertas de las cuatro.

`perfiles.html` es un CRUD de perfiles de vehículo (crear/cargar/borrar), y
`ayuda` es el manual (ver el aviso del paso 5).

### Paso 4 — Los 6 botones "Configurar" de paneles ya nativos

En orden de molestia:

1. **`corex-ecu.html`** → apaga `_ecuConfigWebView` (superficie 3) **entera, con
   una sola página.** Mejor relación de toda la lista.
2. **`config.html?mod=camaras.html`** → portar el formulario de IP/usuario/clave
   **adentro de `CamarasPanel`** (§3.2-E). Cierra la última puerta duplicada.
3. **`insumos.html`** → dos puertas de una (VistaX editor + Módulos › Campo).
4. **`sectionx.html`** → el mapeo surco→sección, test de relés y debug MQTT.
5. **`nodo-detalle.html`** y **`setup.html`** → las dos de `NodosPanel`.

Los cinco últimos usan `NavigateTo` → **apagan también `_webView` (superficie
1)**, que es la de pantalla completa: la que deja al operario sin mapa y con una
sola flecha «←» para volver.

### Paso 5 — Las 13 filas de Módulos que quedan

`linex`, `insumos`*, `mapas`, `calculadora-siembra`*, `pid-lab`, `pwm-diag`,
`orbitx`, `firmwares`, `pwa-qr`, `wifi`, `eventos`*, `debug`, `ayuda`*.

(`*` = ya portada en un paso anterior; solo hay que cambiarle la fila en `MODS`:
sacarle `Ruta`, ponerle `Clave`, y agregar el `case` en el dispatch de
`MainWindow`. Lo dice el propio encabezado de `ModulosTab.cs:26`.)

Netas nuevas: **9** — `linex`, `mapas`, `pid-lab`, `pwm-diag`, `pwa-qr`, `wifi`,
`orbitx`, `firmwares`, `debug`.

Ojo con las tres últimas: `firmwares.html` sube `.bin` desde USB (hasta 8 MB) y
`orbitx.html` maneja credenciales de la nube. **Son las dos con más lógica de
las 25**, no son formularios — aunque en cabina solo se llegue a ellas por acá
(§3.2-D-bis).

Apaga: **`_web` del `ConfigPanel` (superficie 4)** y con eso la última
superficie de la pantalla del tractor.

Dos avisos:

- **`ayuda.html` no se porta: se traduce.** Es el manual de cabina y `CLAUDE.md`
  obliga a mantenerlo actualizado con cada cambio de UI. Si pasa a nativo hay
  que decidir **dónde vive el manual** para que se siga pudiendo editar sin
  recompilar. **Esa decisión es del usuario.**
- **`pwa-qr.html` puede no valer la pena.** Su función es mostrar un QR para
  abrir PilotX en el celular; es un `<canvas>` y una URL. Se resuelve nativo en
  poco código, pero conviene confirmar que se sigue usando.

### Paso 6 — Decidir qué se hace con `corex` (:5181)

No es un `pages/*.html`: es el dashboard que sirve el **motor** (§3.2-G). O
panel nativo contra su API, o se acepta el navegador. **Decisión del usuario.**

### Paso 7 — Retirar la dependencia

Recién con 0..6 cerrados:

1. Sacar el registro de `App.WebViewHost = new DesktopWebViewHost()`
   (`PilotX.Desktop\Program.cs:77`).
2. Borrar `PilotX.Desktop\DesktopWebViewHost.cs`.
3. Sacar los paquetes `WebView.Avalonia` / `WebView.Avalonia.Desktop` del
   `.csproj` del Desktop.
4. **`IWebViewHost` / `IWebViewHandle` SE QUEDAN** en `PilotX.UI`: los usa
   `MainView` para el head **Android**.
5. **`pages/*.html` y `js/*.js` NO se borran nunca**: los usa la PWA del celular.

### Resumen numérico del camino

| Paso | Páginas netas | Superficie que apaga |
|---|---|---|
| 0 · auditar emisores JS | — | ninguna (prerrequisito) |
| 1 · gráficos | 6 | A, B, C |
| 2 · calculadora | 1 | — |
| 3 · SISTEMA | 2 | D · **→ superficie 2 (`_dialogWebView`)** |
| 4 · botones Configurar | 6 | E · **→ superficies 3 (`_ecuConfigWebView`) y 1 (`_webView`)** |
| 5 · Módulos restantes | 9 | F · **→ superficie 4 (`_web`)** |
| 6 · `corex` :5181 | 1 (decisión) | G |
| 7 · retirar paquetes | — | WebView2 fuera del Desktop |

**24 páginas netas para portar + 1 decisión.**

Comparado con las 9 de esta ola, es **entre dos y tres olas más de trabajo** —
pero de dificultad decreciente: ninguna de las 24 dibuja sobre el mapa vivo, que
es lo que hizo caro (y buguero, §1.2) el porteo de la ola 4.

Esto es la meta de la **migración Avalonia total, diferida al 25/08** (alcance
aprobado; el plan vive en el scratchpad de aquella sesión, **no en el repo** —
`.claude\plans\` no existe acá).

---

## 5. Resumen honesto

- **9 pantallas cerradas** en la ola 4, todas de las que se usan **mirando el
  mapa**: Módulos, Contorno, Cabecera, Cabecera por líneas, Tramlines, Tramlines
  multi, Suavizar AB, Corregir posición, Rutas grabadas.
- **Build 0 errores** (11 advertencias, todas preexistentes y fuera de esta ola).
  **Tests 374/374** — pero **ningún test cubre lo portado** y el total **no se
  movió**: 374 antes, 374 después.
- **Ninguna de las nueve se probó en cabina.** Las nueve fueron al tablero como
  `Prueba:`, ninguna como `Cierra:`. En esta ola eso pesa más que en las
  anteriores: estas pantallas **dibujan sobre el mapa vivo**, no guardan un
  formulario, así que ni siquiera se pueden verificar contra disco.
- **11 de los 20 commits son `fix`**, y seis de esos fix son bugs de datos o de
  estado que el HTML no tenía — incluido uno que **borraba el `.rec` del lote
  equivocado** (`722af699`). Los nueve porteos necesitaron arreglo posterior;
  ninguno salió bien de una.
- **Quedan 25 archivos HTML alcanzables** y **4 superficies WebView** vivas.
  Ninguna es de guiado ni de siembra en marcha. Faltan **24 porteos netos + 1
  decisión** para sacar WebView2 del Desktop (§4).
- **El conteo de la ola 3c (15) estaba mal por defecto.** No contaba los
  diálogos de la labor, ni `corex-ecu.html` (invisible al grep de cuatro
  términos, §3.0), ni `perfiles.html` (invisible al grep de `CommandParameter`,
  §3.3). 25 es el número bien contado, no un retroceso.
- **Dos trampas de método quedaron documentadas** porque me hicieron contar mal
  en el primer pase: los cuatro términos del grep **no ven** un WebView creado a
  mano (§3.0), y grepear `CommandParameter=` **no ve** los menús cableados por
  `Click` (§3.3). El inventario final se hizo por `App.WebViewHost.Create` +
  `.Navigate(`, que es exhaustivo.
- **`firmwares.html`, `orbitx.html` y `debug.html` no se alcanzan por el botón
  Tools en cabina**: ese botón vive en `BottomToolbar`, que el modo cockpit
  oculta (§3.2-D-bis). Siguen bloqueando por la grilla de Módulos.
- **No hay `pendiente-*.md`**: los de las olas 3a/3b vivían en scratchpad
  efímero y ya no existen. Lo pendiente de esta ola está en §3 y §4, commiteado.
- **Dos decisiones son del usuario, no del porteo**: dónde vive `ayuda.html` si
  se hace nativa, y qué se hace con el dashboard de CoreX en `:5181`.
