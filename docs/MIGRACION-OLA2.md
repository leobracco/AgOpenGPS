# Migración ola 2 — los paneles que todavía tenían una pestaña en HTML

Rama `codex/pilotx-ui-new` · cierre 2026-08-16

La **ola 1** portó los monitores (QuantiX, VistaX, FlowX, SectionX, StormX,
CoreX-ECU, Cámaras, Hub, Sistema, Datos GPS, Datos del lote, Actualizar…). Todos
quedaron nativos **salvo el botón "Configurar"**, que seguía haciendo
`NavigateTo("pages/<producto>.html")` y despertaba Chromium entero para editar
un formulario.

La **ola 2** es exactamente ese hueco: los editores. Este documento cierra la ola
y deja escrito lo que quedó pendiente, para que no viva sólo en el scratchpad de
una sesión.

Guía de porteo: [`PORTING-AVALONIA.md`](PORTING-AVALONIA.md).

---

## 1. Estado de la ola

Verificación del cierre (2026-08-16):

- `dotnet build SourceCode\AgOpenGPS.sln -c Release` → **0 errores**, 11
  advertencias (todas preexistentes: nullabilidad en `DireccionPanel`,
  `CrashHandler`, `TecladoLinux` y dos APIs obsoletas de OpenTK en
  `MapGlSurface`).
- `dotnet test SourceCode\AgOpenGPS.sln -c Release --no-build` → **374/374
  verdes, 0 con error**:
  - `AgroParallel.Services.Tests` 261
  - `AgOpenGPS.Core.Tests` 53
  - `BenchX.Tests` 36
  - `PilotX.Cockpit.Bars.Tests` 21
  - `AgLibrary.Tests` 3

| Pantalla | Panel nativo | Estado | Commits |
|---|---|---|---|
| Dirección (ex `FormSteer` / `direccion.html`) | `Views/DireccionPanel.cs` | **Cerrado con pendientes** — paridad contra el FormSteer histórico | `2b92a1a4`, `a5c964ef`, `42392740`, `0872393e` |
| QuantiX — editor (Siembra, Motores, Shape, PID, Calibración, Prueba) | `Views/QuantiXEditorPanel.axaml(.cs)` + `Views/QuantiXEditor/*` | **Cerrado con pendientes** | `fe1c1224`, `49d74433` |
| VistaX — editor (Insumo & calibración, Implemento, Config) | `Views/VistaXEditorPanel.axaml(.cs)` + `Views/VistaXEditor/*` | **Cerrado con pendientes** | `e3f81500`, `6a16c096`, `648f8ef1` |
| FlowX — editor (Nodo activo, Reguladoras, Cortes, Electroválvulas, Firmware, Nodos) | `Views/FlowXEditorPanel.axaml(.cs)` + `Views/FlowXEditor/*` | **Cerrado con pendientes** | `dd4f4be9`, `80096d29` |
| Nodos (lista + curado + diagnóstico MQTT) | `Views/NodosPanel.axaml(.cs)` | **Cerrado con pendientes** | `736f6ac9`, `12f69448` |
| Sonidos / alarmas de cabina | `Views/SonidosPanel.cs` | **Cerrado con pendientes** | `e5222b31`, `fc9eedaa` |

**6 pantallas cerradas, las 6 con pendientes anotados. Ninguna probada en
cabina** — todas van al tablero como `Prueba:`, nunca `Cierra:`.

### Lo que la ola 2 NO tocó

Se levantaron specs de porteo para 31 pantallas (`spec-00` … `spec-30`) pero la
ola sólo ejecutó las 6 de arriba. Quedan sin arrancar, con spec escrita:

- **`config.html` y sus 15 pestañas** (`spec-00` … `spec-14`, `spec-26`):
  Resumen, Vehículo (config/dimensiones/antena), Herramienta
  (config/enganche/offset/pivote/ajustes/secciones/switches), Máquina, Rumbo,
  Rolido, Giro en U, Tramlines. **Es la pieza más grande que queda**: 1086
  líneas de HTML + 1881 de JS, y además es el contenedor de 25 submódulos
  (`data-mod=`).
- **`wifi.html`** (`spec-16`).
- **Editores del mapa**: `contorno.html` (`spec-17`), `cabecera.html`
  (`spec-18`), `cabecera-lineas.html` (`spec-19`), `recpath.html` (`spec-21`),
  `tramline.html` (`spec-22`), `tramlines.html` (`spec-23`).
- **Widgets de una acción**: `suavizar-ab.html` (`spec-24`),
  `corregir-posicion.html` (`spec-25`), `lote.html?do=kml` (`spec-20`).

---

## 2. Pendientes por pantalla

Nada de esto bloquea el cierre de la ola: son decisiones de producto, cosas que
sólo se ven con fierro, o deuda preexistente que el porteo destapó. Lo que sí
está prohibido es marcarlas como "anda".

### 2.1 QuantiX (`fe1c1224` + `49d74433`)

1. **El editor nativo casi no se puede abrir en cabina, y el camino real sigue
   siendo Chromium.** El commit cerró el par monitor→Configurar, pero
   `QuantiXPanel` sólo se abre desde `HubPanel` y desde `OnNavQuantiX`
   (`BottomToolbar`, `IsVisible=False` en modo cockpit). El camino que existe
   hoy es **Configuración › Módulos › QuantiX**, que es `config_form` →
   `pages/config.html` en WebView, y ahí `data-mod="quantix.html"` abre la
   PÁGINA. Los casos `"quantix_editor"` / `"prescripciones"` de
   `RouteCockpitCommand` no los emite ninguna barra: son código muerto hasta
   que alguien enganche un botón. **Decisión de producto pendiente**: (a) botón
   propio en el panel Herram. de la barra de la pasada, o (b) que el submenú
   Módulos de `config_form` rutee a los paneles nativos. Afecta igual a VistaX
   y a Nodos — conviene resolverlo una sola vez.
2. **`ayuda.html` documenta una ruta que no existe.** Se agregó
   `Barra de la pasada › Herram. › QuantiX › Configurar`, pero en el panel
   Herram. real no hay QuantiX. Se corrige en el mismo commit que cierre el
   punto 1 (hoy la ruta honesta sería "Configuración › Módulos › QuantiX", que
   abre la página).
3. **`MotoresDelNodo` CREA motores habilitados en la config.**
   `QxEditorCtx.MotoresDelNodo` rellena hasta 2 motores con
   `QxMotorConfig.Habilitado = true` por defecto; el JS sólo armaba un array
   temporal para dibujar. Con sólo ENTRAR a Motores/PID/Calibración/Prueba, un
   nodo con 0 ó 1 motores queda con 2 en `state`, y el primer guardado —que
   puede ser automático— los persiste en `quantiX_motores.json`, los publica al
   nodo y los sube a OrbitX. Riesgo acotado (nacen con `dosis_fija = 0`), pero
   contradice el DTO del backend. **Toca si un canal aplica producto ⇒ va con
   validación.** Salidas: materializar el canal recién al guardar, o crearlo con
   `Habilitado = false`.
4. **Falta banco**: detección de meta de calibración (polling 500 ms, guard de
   1,5 s), "Medir Max Hz" (rampa 400→4095), rampa de PWM mínimo, stop al cerrar
   con el motor girando. **`MainWindow.Closed` no llama `Detach()`**: apagar la
   pantalla a mitad de una rampa deja el nodo girando (agujero heredado del
   HTML, ahora cerrable enganchando `Closed` → `CloseQuantiXEditor()`).
5. Menores aceptados: el stepper `Int` snapea al múltiplo del paso (mejor que el
   HTML); al cerrar el editor se vuelve al mapa y no al monitor.

### 2.2 VistaX (`e3f81500` + `6a16c096` + `648f8ef1`)

1. **El monitor nativo muestra sem/min, no sem/m — y el backend ya manda
   sem/m.** `VistaXPanel` pinta `s.Spm` con etiqueta "sem/min"; el DTO cliente
   `VistaXSurcoLive` no modela `sem_m`, `seccion_cortada` ni `last_seen_iso`, y
   `VistaXLiveSnapshot` no modela `distancia_entre_surcos` — los cuatro los
   manda el backend. **Riesgo en campo real**: sem/min depende de la velocidad
   tanto como de la dosis; a 6 y a 9 km/h la misma siembra da dos números y el
   operario lee "subió la dosis". Cierre: agregar los 4 campos (aditivo) y pasar
   celda/barra/detalle a sem/m + sem/10 m + sem/ha. Probar en cabina.
2. **Desde la UI nativa no se puede silenciar un sensor ni fijar su objetivo.**
   Faltan `POST /api/vistax/sensor/mute` y `POST /api/vistax/sensor/config` en
   `VistaXClient` y sus controles en `VistaXPanel`. No es regresión del commit
   (el monitor nunca los tuvo), pero el camino de un toque era
   `pages/vistax.html`. El silenciado es la válvula de escape de las alarmas: si
   cuesta llegar, el operario apaga TODAS y se queda ciego.
3. **`objetivo_fuente` se edita pero el backend no lo guarda.**
   `VistaXController.PutImplemento()` no copia `ObjetivoFuente` en el merge. Bug
   preexistente, idéntico en HTML y nativo. Es carril back-end (Santiago): una
   línea, pero hay que confirmar que nadie dependa de que ese campo no persista.
4. **El PUT del implemento sale del DTO cacheado, sin re-leer.** El backend
   mergea del lado servidor, así que lo que el panel no manda no se pierde;
   **lo que sí queda expuesto es `mapeo_sensores`, que se reemplaza entero**. La
   solución honesta es detectar el conflicto y avisar, no re-leer a ciegas (eso
   tiraría lo que el operario tiene sin guardar).
5. Menores aceptados: `N° de torres` pasó a solo lectura a propósito (el input
   nunca persistió); "UI update (ms)"/"Timeout sensor (ms)"/"Log a Field Record"
   quedan textuales por paridad; el tab "Nodos" no se porteó porque la grilla ya
   está en `VistaXPanel`.

### 2.3 FlowX (`dd4f4be9` + `80096d29`)

1. **El buscador de PWM mínimo perdió la entrada directa del valor.** El HTML
   tenía un `input type=number`; el `AgpStepper` sólo tiene −/+ con paso 5: de
   200 a 2000 son 360 toques (~29 s de autorepeat) con líquido circulando.
   Arreglo propuesto: `TextBox` numérico con el teclado propio, o un segundo par
   de botones de paso grueso (±100). Cambia el layout del overlay: verlo en 10".
2. **Cambiar la cantidad de cortes rearma la pestaña en cada toque.**
   `FxCortesTab.CambiarCortes()` termina en `RebuildTab` → `Children.Clear()`,
   que desengancha el stepper que el operario está apretando: es el único campo
   del editor sin autorepeat. Arreglo: actualizar el mapa de cortes y el combo
   de master in-place.
3. **`Traductor.Aplicar` revierte los textos dinámicos de controles con
   nombre** (`PwmTitulo`, `EstadoText`, `EstadoPillText`): `Traductor.Uno()`
   cachea el texto ORIGINAL del XAML y lo reescribe en cada `Aplicar`. Hoy es
   cosmético (nada dispara `Aplicar` con el overlay abierto), pero con dos
   reguladoras el título puede mentir sobre cuál se está moviendo.
4. **Dos editores sobre el mismo `flowX.json` — last-write-wins.** El POST no va
   precedido de un GET fresco: si alguien guardó desde el celular, Guardar lo
   pisa sin avisar. Mitigación barata: GET antes del POST y avisar en el pie.
5. **El monitor `FlowXPanel` sigue con la paleta oscura de tema.** Igual que
   StormX, SectionX y QuantiX: migrar los monitores a card clara es una tanda
   propia, no deuda de este porteo.
6. **Sin fierro**: calibración, auto-tune, barrido, OTA y el heartbeat del PWM
   manual compilan pero mueven una bomba.

### 2.4 Nodos (`736f6ac9` + `12f69448`)

1. **Falta validación en pantalla 10"** (riesgo R11 de la spec). La card es
   1040×660 y la tabla adentro tiene `MinWidth=960`: en 1080×720 se comprime y
   scrollea. Hay que ver si las 7 columnas + hasta 4 botones de acción entran sin
   scroll horizontal, y si **`Eliminar` (rojo, destructivo) queda a menos de un
   dedo de `Renombrar`** con la máquina en movimiento.
2. **Volver del detalle del nodo cae en el mapa, no en la lista.** Tocar una
   fila navega a `pages/nodo-detalle.html` por WebView y la flecha "←" cierra a
   MAPA: hay que rehacer Hub › Nodos. Se resuelve solo cuando se portee
   `nodo-detalle.html` como pantalla interna del panel.
3. **Cambio de idioma con el panel abierto**: `MainWindow.AplicarIdiomaAsync`
   hace `Aplicar(this)` sobre toda la ventana y las pills vuelven un instante a
   los textos del XAML. Se corrigen solas en ≤3 s. Caso de laboratorio.
4. **Revisar el mismo patrón de `Traductor` en los otros paneles `.axaml`**
   (QuantiX/FlowX/VistaX): cualquier control declarado en XAML que se actualice
   por código desde un polling tiene el bug que se acaba de arreglar acá.
5. Decisiones, no deudas: el banner del panel no beepea (el beep es exclusivo de
   `CabinaAlarmasOverlay`, que corre siempre — con los dos sonando se escuchaba
   doble); `pages/nodos.html` sigue viva para el Hub remoto y la PWA.

### 2.5 Sonidos (`e5222b31` + `fc9eedaa`)

1. **El `.wav` nuevo no lo toma el que hace sonar la cabina hasta reiniciar
   PilotX.** `SoundAlarmPoller` cachea los wav por nombre en un
   `Dictionary<string,byte[]>` de instancia que **nunca se invalida**. Si el
   operario sube un wav propio con un nombre existente (el server sobrescribe
   sin avisar), el ▶ del panel toca el nuevo y la alarma real sigue tocando el
   viejo. Preexistente, pero ahora la subida está a un toque desde la cabina.
   Arreglo propuesto: registro estático de pollers vivos +
   `SoundAlarmPoller.InvalidarCache(nombre)`. **No se aplicó en una revisión
   porque toca el componente que hace sonar la cabina: alarma que deja de sonar
   es peor que alarma con el sonido viejo.** Ver en banco.
2. **Quedan dos puertas a la misma pantalla y la ayuda cuenta una sola.** El
   módulo `🔔 Sonidos` sigue listado en `config.html`, que en cabina se abre por
   WebView. Mismo wire, pero el último PUT gana sin merge. Decisión de producto:
   o se saca el `data-mod` de la config que se sirve a la cabina, o la ayuda
   menciona las dos puertas.
3. **Sin prueba en cabina**: el picker de sonido sobre el mapa GL (es
   `Border` + `IsVisible` + backdrop, pero el backdrop cubre sólo el área de la
   card); el file-picker del SO (`StorageProvider.OpenFilePickerAsync`) sobre una
   ventana con compositor GL; y que la card entre en 1080×720.
4. **`Guardar` no revalida contra el server** (correcto: releer pisaría lo
   tipeado). Si otro cliente guardó en el medio, el PUT lo pisa entero.
5. **`ShowCoreXEcu` no cierra Sonidos/Nodos/Actualizar/Cámaras** — preexistente.
   La regla "un solo overlay a la vez" está copiada a mano en 15 métodos y se
   desincroniza en cada port: vale una pasada de limpieza.

### 2.6 Dirección (`2b92a1a4`, `a5c964ef`, `42392740`, `0872393e`)

Paridad contra el `FormSteer` histórico documentada en
`paridad-direccion.md` (scratchpad). Cerrado el "Poner en cero" explícito del
sensor de ángulo, el guardado al cerrar, el cero del WAS con la escala en
pantalla y el límite de funciones de guiado a 40 km/h. **Falta cabina.**

---

## 3. Lo que todavía mantiene vivo a WebView2

Referencia de paquete: `SourceCode/PilotX.Desktop/PilotX.Desktop.csproj`
líneas 68-69 (`WebView.Avalonia` + `WebView.Avalonia.Desktop`), detrás de
`Services/IWebViewHost.cs` y `DesktopWebViewHost.cs`. `PilotX.UI` **no**
referencia ningún paquete de WebView.

**Respuesta corta: no, WebView2 no se puede sacar todavía.** Faltan ~30
pantallas alcanzables desde la pantalla del tractor, y una de ellas
(`config.html`) es el camino normal a toda la configuración de la máquina.

### 3.1 Configuración y sus módulos — el bloqueante grande

| Entrada | Destino |
|---|---|
| `config_form`, `directorios`, `asistente_direccion` | `pages/config.html` |
| `ayuda` | `pages/config.html?mod=ayuda.html` |
| Cámaras › Configurar (`MainWindow.axaml.cs:522` y `:2761`) | `pages/config.html?mod=camaras.html` |
| VistaX editor › "Config central" (`:623`) | `pages/config.html?tab=tsections` |

`config.html` es a la vez la config de vehículo/implemento **y** el contenedor
de 25 submódulos (`data-mod=`), entre ellos QuantiX, VistaX y Sonidos —
o sea: **mientras `config.html` sea HTML, los editores que la ola 2 acaba de
portar siguen teniendo una puerta a su versión Chromium.**

### 3.2 Diálogos `OpenDialogPage` desde `RouteCockpitCommand`

`ajustes-todos.html`, `colores.html`, `colores-secciones.html`,
`perfiles.html`, `grafico-direccion.html`, `grafico-rumbo.html`,
`grafico-xte.html`, `grafico-correccion.html`, `suavizar-ab.html`,
`corregir-posicion.html`, `eventos.html`, `vistax-prueba.html`,
`calculadora-siembra.html`, `banderas.html`, `contorno.html` (**único con
`mapaVivo: true`**), `cabecera.html`, `cabecera-lineas.html`, `tramline.html`,
`tramlines.html`, `recpath.html`, `sim-coords.html`, `lote.html?do=kml`.

### 3.3 Dos regresiones baratas: nativo que se abre por HTML

| Comando de barra | Abre | Pero ya existe |
|---|---|---|
| `datos_gps` (`:3724`) | `pages/datos-gps.html` | `GpsDataPanel` (`OnNavDatosGps` → `ShowGpsData()`) |
| `lote_datos` (`:3725`) | `pages/datos-lote.html` | `FieldDataPanel` (`OnFieldToolsClick` → `ShowFieldData()`) |

El mismo dato se ve nativo desde el menú y por Chromium desde la barra. **Son
dos líneas: sacan dos WebView del camino de labor sin portar nada.**

### 3.4 Paneles nativos que todavía delegan en HTML

| Panel | Qué delega | Dónde |
|---|---|---|
| SectionX | Configurar (mapeo surcos→secciones, test de relés, debug MQTT) | `:593` → `pages/sectionx.html` |
| Cámaras | Configurar (IP/usuario/clave por cámara) | `:522` → `config.html?mod=camaras.html` |
| CoreX-ECU | pestaña Configurar — **WebView embebido dentro de la card** | `AbrirEcuConfig` → `corex-ecu.html?widget=1` |
| Nodos | detalle del nodo / asistente de primera vez | `:535` `nodo-detalle.html`, `:536` `setup.html` |
| VistaX editor | catálogo de insumos | `:622` → `pages/insumos.html` |

### 3.5 Menú SISTEMA

`OnNavFirmwares` → `firmwares.html`, `OnNavOrbitX` → `orbitx.html`,
`OnNavDebug` → `debug.html` (`:4723-4725`).

### 3.6 Fuera de `wwwroot`

- **Dashboard de CoreX** (`AbrirCoreXAsync` → `OpenDialogUrl("http://127.0.0.1:5181/")`):
  Serial / NTRIP / Red-IP / Módulos, servido por `CoreXEnginePanel`. No es una
  página del Hub: sale del WebView sólo si se portea ese panel.
- **Modo widget flotante** (`:667`, `ShowWebView(App.TargetUrl)`): la ventana
  entera ES la página. No es la pantalla de cabina, pero usa el mismo host.

### 3.7 Deuda ajena a esta ola

`Views/MainView.axaml.cs` (la vista single-view que monta `PilotX.Android` y
`--singleview`) todavía rutea a HTML cosas que `MainWindow` tiene nativas:
`lote_menu`, `pick`, `datos_gps`, `hub`, `camaras`, `corex_ecu`, `direccion`…
**Es deuda del head Android, no de la pantalla del tractor.**

### 3.8 Orden sugerido para poder borrar la referencia

1. Las dos líneas de §3.3 (`datos_gps`, `lote_datos`) — costo cero.
2. Resolver el punto 1 de QuantiX (§2.1): que el submenú Módulos de
   Configuración rutee a los paneles nativos ya portados. Sin esto, la ola 2 no
   se nota en cabina.
3. **`config.html` + sus 15 pestañas** (specs `00`-`14`, `26`). Es el trabajo
   grande y el que desbloquea todo lo demás; conviene partirla en (a) config de
   vehículo/implemento nativa y (b) shell de módulos.
4. Los editores del mapa (contorno, cabecera, tramlines, recpath): necesitan
   mapa vivo, que hoy es la excepción y nativo deja de serlo.
5. Los diálogos chicos de §3.2 restantes + los cinco delegados de §3.4.
6. Recién ahí: borrar `WebView.Avalonia*` del `.csproj` y `DesktopWebViewHost`.
   `pages/*.html` **no se borra nunca** — la PWA del celular y el Hub remoto la
   siguen sirviendo.
