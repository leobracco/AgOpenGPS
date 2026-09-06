# Changelog — PilotX (AgOpenGPS — Agro Parallel)

Todos los cambios relevantes a la app PC del tractor se anotan acá.

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y versionado [SemVer](https://semver.org/lang/es/) (MAJOR.MINOR.PATCH).

La fuente de verdad de la versión es `Installer/VERSION`. El build
(`build.ps1` / `build-installer.ps1`) la lee de ahí y la estampa en
el ensamblado vía `-p:Version=` para que `PilotXSelfUpdate` la pueda
detectar en runtime y compararla contra el catálogo OTA.

---

## [1.0.52] — 2026-09-06

> **Primera versión que se puede instalar como parche.** Si el equipo tiene la
> 1.0.51, esto son ~5 MB en vez de 193.

### Added
- **Canal de diagnóstico remoto.** La pantalla le pregunta a OrbitX cada 20
  segundos si hay algún diagnóstico pendiente, corre la función y devuelve el
  texto. Con eso se puede ver qué pasa en una máquina del campo sin dictarle
  comandos por teléfono a quien esté parado adelante.
  - **No abre ningún puerto en el tractor.** Todo sale de adentro hacia afuera,
    sobre la misma autenticación por dispositivo que ya usa el sync. Sin
    conexión no pasa nada: reintenta espaciando hasta 5 minutos.
  - **Sólo se pueden pedir las ocho funciones de sólo lectura** que ya estaban
    escritas: estado, sistema, red, puertos, procesos, firewall, logs y nodos.
    Lo que llega del cloud es una clave de diccionario, no un comando: si no
    está en el catálogo se rechaza. No hay ninguna ruta por la que un texto
    arbitrario llegue a ejecutarse.
  - La salida se sanitiza (tokens y contraseñas) y se corta en 256 KB. Timeout
    por función, tope 300 segundos, y de a una por vez.
  - En una pantalla sin vincular el servicio duerme: sin identidad no consulta.

### Lado servidor (ya desplegado, no requiere esta versión)
- `routes/soporte.js` en OrbitX, con `GET /api/soporte/pendientes` y
  `POST /api/soporte/resultado` para el equipo, y `POST /api/soporte/comando`,
  `GET /api/soporte/comandos` y `GET /api/soporte/catalogo` para el panel.
- **Agujero encontrado al probar y cerrado**: `auth.required` también autentica
  equipos, así que un token de dispositivo podía encolar diagnósticos sobre
  **cualquier** máquina de la flota y leer la salida. Los tres endpoints de
  panel ahora rechazan tokens de dispositivo, igual que hace `routes/devices.js`.
  Verificado: 403 al encolar sobre sí mismo, 403 sobre otro equipo, 403 al leer
  el historial, y 200 al pedir los pendientes propios.

---

## [1.0.51] — 2026-09-05

> **Esta es la versión que habilita las actualizaciones chicas.** A partir de
> acá, una corrección se manda como un parche de ~5 MB en vez de un paquete de
> 193 MB. Para llegar hasta acá sí hace falta el paquete completo, una última
> vez.

### Added
- **El actualizador entiende parches y los valida.** Un parche trae sólo lo que
  cambió (unos 5 MB contra 193) y se aplica encima sin borrar nada. Antes de
  tocar el disco comprueba que la instalación sea exactamente la versión base
  para la que se armó, comparando contra la versión real del ejecutable
  instalado. Si no coincide, **aborta sin tocar nada**: no cierra procesos, no
  respalda, no extrae, y explica que hace falta el paquete completo.
  - Esa comprobación es la que faltaba cuando se mandaron DLL sueltas mal
    versionadas y una pantalla dejó de arrancar. Los ensamblados se referencian
    por versión exacta: mezclar versiones no da un error entendible, da una app
    que no abre.
- **`instalar-pilotx.ps1` acepta las dos cosas y distingue sola cuál es.**
  - Paquete completo: reemplaza `Desktop`, `Engine` y `BarsHost` enteras,
    respaldando con un rename instantáneo.
  - Parche: aplica encima y respalda **sólo los archivos que va a pisar**, para
    poder deshacer con precisión. Si el parche no corresponde a la versión
    instalada, lo rechaza antes de empezar.
  - En los dos casos el último paso es abrir la pantalla; si no abre, deshace.

### Cómo se saca un parche
```
.uild.ps1                      compila y verifica la versión nueva
.uild-parche.ps1 -Base 1.0.51  arma el parche contra esa base
```
Se publica en OrbitX como cualquier versión. El equipo lo descarga igual que
siempre; la diferencia es que baja 5 MB en vez de 193.

---

## [1.0.50] — 2026-09-05

> Reemplaza a la 1.0.49, que salió **sin RustDesk**. Si ya instalaste la 1.0.49
> y la pantalla anda, no es urgente; sí conviene antes de entregar un equipo
> nuevo.

### Fixed
- **RustDesk volvió al paquete.** La 1.0.49 salió sin él y con eso se perdía el
  soporte remoto sin instalación. La carpeta `Desktop\RustDesk\` nunca fue
  parte del build: sobrevivía en `Build\` de casualidad porque nada la
  borraba, y al empezar a limpiar antes de publicar (1.0.49) desapareció sin
  que ninguna prueba lo notara.
  - Ahora vive en `Tools\RustDesk\` y `build.ps1` la copia, avisando en rojo
    si falta. **No se versiona en git**: el nombre del ejecutable lleva el
    servidor y la clave del relay propio, y este repositorio es público.
  - En una pantalla que ya tiene RustDesk instalado no cambiaba nada; el
    código sale sin hacer nada si la carpeta no está. El problema era para
    equipos nuevos.

### Added
- **El build ahora también prueba el Engine.** Lo levanta con `--webhost
  --corex` y verifica que responda la API en `127.0.0.1:5180`. El Engine se
  publica con ReadyToRun sin composite, que es exactamente la combinación que
  dejó sin arrancar a `PilotX.Desktop`; una pantalla que abre sin Engine no
  sirve para trabajar.

### Verificado en esta revisión
- El paquete **no** lleva credenciales, tokens ni configuración de la máquina
  de desarrollo. Dejó de viajar `Engine/GuidanceEngineData/ultimo_lote.txt`,
  que era estado local metiéndose en la instalación del cliente.
- `System.IO.Ports.dll` y `System.Management.dll` están donde corresponde. Las
  copias que desaparecieron bajo `Engine/runtimes/` eran restos de builds
  viejos para Android, Linux y macOS que no se usaban.

---

## [1.0.49] — 2026-09-05

> **La 1.0.48 NO ARRANCA. No instalarla.** Se retiró del catálogo de OrbitX.
> Esta versión la reemplaza y corrige lo que la rompió.

### Fixed
- **La 1.0.48 moría al arrancar** con `FailFast` (0xC0000602) y sin ningún
  mensaje, en cualquier máquina y aunque la instalación fuera limpia. Se
  comprobó comparando las dos versiones extraídas en la misma PC: la 1.0.47
  abre, la 1.0.48 muere. Llegó a instalarse en el equipo de un cliente.
  - **Causa raíz: `build.ps1` nunca limpiaba las carpetas de publicación.**
    `dotnet publish -o` no borra lo que había: escribe encima y convive con
    los archivos del build anterior. `Build\Desktop` llegó a tener 172
    archivos de un build y 53 de otro al mismo tiempo. La 1.0.48 se publicó
    con ReadyToRun apagado pero quedaron las DLL **con** ReadyToRun de la
    1.0.47, y esa mezcla compila y empaqueta sin una sola advertencia.
  - Ahora `Desktop`, `Engine` y `BarsHost` se borran antes de cada publish.
    No se toca `AgroParallel\wwwroot` (lo espeja robocopy), ni `Branding`,
    ni `Fonts`, ni `config-captures`.
- **`PilotX.Desktop` se publica sin ReadyToRun.** La combinación
  ReadyToRun activado **sin** composite produce un binario que no arranca.
  Apagado del todo funciona, y además cada ensamblado queda chico, que era el
  objetivo de sacar el composite. Costo medido del arranque en frío: 3777 ms
  contra 3042 ms del composite.

### Added
- **El build prueba que la aplicación abre antes de empaquetar.** Lanza
  `PilotX.Desktop.exe` y falla el build si muere en los primeros 25 segundos.
  Compilar sin errores no probaba nada: la 1.0.48 compiló limpia, se empaquetó,
  se subió al cloud y se instaló en un tractor sin que nadie la hubiera
  ejecutado una sola vez. Se saltea con `-SkipSmoke` en máquinas sin sesión
  gráfica.
- **`setup_pilotx_lan.bat` ahora viaja en el paquete.** Vivía sólo en el repo,
  así que la regla de UDP 9999 que necesita el ToolX nunca llegaba a una
  máquina de cliente. El síntoma era engañoso: el nodo aparecía CONECTADO por
  MQTT mientras su PGN se descartaba en silencio y la herramienta no pintaba.

### Verificado antes de publicar
- 1.0.49 limpia: abre, 1566 ms.
- 1.0.47 con 1.0.49 extraída encima (el camino real de actualización): abre,
  1483 ms, incluso con el `PilotX.Desktop.r2r.dll` huérfano de 90 MB todavía
  en la carpeta. Sin ReadyToRun ya nadie lo carga; queda como peso muerto y se
  puede borrar a mano.

---

## [1.0.48] — 2026-09-05

### Fixed
- **La configuración de FlowX se borraba sola en el tractor** (caso de campo:
  `flowX.json` volvía a `{"enabled":true,"nodos":[],"ignorados":[]}` y el
  cliente perdía los nodos ya configurados). `AtomicJson.Read` podía caer justo
  en la ventana del fallback de escritura, donde hay un `Delete` seguido de un
  `Move` y por un instante el archivo NO existe. El `Load()` interpretaba ese
  null como "no hay config" y escribía los defaults ENCIMA de la buena. Con el
  puente releyendo cada 2 segundos, la lotería se jugaba todo el tiempo.
  - `AtomicJson.Read` ahora reintenta (3 × 40 ms) antes de rendirse, y sólo
    reintenta si hay algo en disco.
  - Nuevo `AtomicJson.Existe(path)`: mira el archivo y su `.bak`.
  - Los seis `Load()` (FlowX, OrbitX, QuantiX, SectionX, StormX, VistaX) sólo
    crean el archivo de defaults cuando de verdad no hay nada en disco. Nunca
    más se pisa una configuración que existe pero no se pudo leer en ese
    instante.
- **El puente de FlowX quedaba mudo para siempre** si el broker MQTT todavía no
  estaba levantado cuando arrancó. `FlowXBridge.StartAsync()` conectaba una sola
  vez, y si fallaba lo dejaba anotado en el log y no reintentaba nunca: las
  secciones no abrían y no había ninguna señal de por qué. Ahora hay un
  watchdog (primer intento a los 2 s, después cada 15 s) igual al que ya tenían
  QuantiX y el CutDispatcher.

### Changed
- **Las actualizaciones pasan a ser livianas y parcheables por archivo.** Se
  apagó `PublishReadyToRunComposite`. Con composite, todo el código nativo de
  todos los ensamblados vivía en un único `PilotX.Desktop.r2r.dll` de 94,3 MB:
  cambiar una línea obligaba a regenerarlo entero, así que el paquete mínimo de
  un fix era de ~90 MB y era imposible mandar una DLL suelta. Sin composite
  cada ensamblado lleva su propio código ReadyToRun adentro, el arranque sigue
  precompilado (no se vuelve a JIT puro) y un fix puntual se manda como esa
  sola DLL.
  - Recordatorio operativo que costó caro: una DLL suelta SÓLO sirve si se
    compiló con el `-p:Version=` exacto de la instalación destino. Los
    ensamblados se referencian por versión exacta y mezclar versiones impide
    que la app arranque.
- `setup_pilotx_lan.bat` ahora abre también **UDP 9999** (además de TCP 5180 y
  5181). Sin esa regla un nodo ToolX aparecía online por MQTT mientras su PGN
  se descartaba en silencio y la herramienta no pintaba.

---

## [1.0.47] — 2026-09-05

> Las versiones 1.0.45 y 1.0.46 se publicaron desde otra sesión y no dejaron
> entrada acá; sus cambios no están documentados en este archivo.

### Added
- **Asignación manual de cortes en el editor de FlowX** (pestaña "Cortes y
  secciones"). Además del reparto automático, ahora hay un desplegable por
  sección para elegir a mano qué corte (la salida S1, S2… del nodo) la abre,
  o dejarla sin asignar. Varias secciones pueden compartir un corte. El mapa
  se actualiza en el momento, sin rebuild, para no cerrar el desplegable ni
  perder el scroll.
  - La lista de secciones sale de lo que reporta PilotX en vivo, sin ninguna
    cantidad fija: si el implemento cambia de 7 a 24 secciones aparecen 24
    filas, la grilla envuelve y el bloque scrollea. El único techo sigue
    siendo el del protocolo del nodo (16 cortes: el bitmask que viaja al
    firmware es de 16 bits).
  - El mapa avisa lo que antes no se veía: secciones sin corte asignado,
    cortes sin usar, y si el corte elegido como master además tiene secciones.

### Changed
- **La cantidad de cortes se persiste** (`cortes` en `flowX.json`, campo
  aditivo). Antes se deducía contando los cables asignados, y con asignación
  manual eso se rompía: usar S1, S2 y S5 son cinco salidas, no tres, y al
  reentrar se perdían las de arriba. Como respaldo para configuraciones
  anteriores y para las que guarda la PWA, ahora se infiere del corte más
  alto asignado en vez de contar los distintos.

### Fixed
- **`setup_pilotx_lan.bat` no abría UDP 9999**, el puerto por donde entra el
  PGN de todos los módulos que hablan por WiFi (GPS/NMEA, AutoSteer,
  CoreX-ECU y el switch ToolX). Como el broker MQTT (TCP 1883) sí tenía
  regla, el síntoma era mudo y confuso: el nodo aparecía conectado en el
  panel y su dato no llegaba nunca al motor. Diagnosticado en campo con
  ToolX (2026-09-04). Se agregan además TCP 5180 (Hub y API, el 8080 es el
  panel viejo de AgIO) y TCP 5181 (panel CoreX), y un aviso sobre la red
  WiFi marcada como pública, que bloquea igual.

---

## [1.0.44] — 2026-09-03

### Added
- **ToolX: switch de herramienta inalámbrico (ESP32).** El nodo manda el
  work switch como PGN 253 por UDP :9999 con byte de origen `0x7C`
  (`CModuleComm.ToolXSource`). `PgnReceiver` reconoce ese origen y toma
  **solo el bit de trabajo**: ángulo, heading, roll, bit de dirección y PWM
  de esos frames se ignoran, así conviven con el módulo de dirección real
  (que sigue mandando el switch de dirección y "secciones al activar el
  piloto").
- **Fila "ToolX (switch inalámbrico)" en Secciones › Switches**, hija de
  "Activar" en la carta de trabajo, en la pantalla nativa (`SwitchesTab`) y
  en la PWA del celular (`config.html`). Setting `setF_isToolXWorkSwitch`,
  JSON `work_toolx_enabled`; con la fila apagada el motor descarta los
  frames de ToolX enteros (un nodo ajeno en la LAN no puede tocar las
  secciones). El rótulo agrega "· conectado" cuando el motor recibe frames
  (`work_toolx_alive`, runtime, refrescado cada 3 s en las dos UIs).
- ToolX en el catálogo del Firmware Manager (`FirmwaresPanel`) para OTA.

### Changed
- **Dueño del bit de trabajo.** Con ToolX habilitado y vivo (frames hace
  menos de 5 s, reloj monotónico) el módulo de dirección no pisa
  `workSwitchHigh`. Si ToolX se pierde, el bit queda en su último valor y el
  módulo de dirección solo lo escribe cuando **su propio** switch cambia:
  un microcorte WiFi del nodo ya no corta las secciones en medio de la
  pasada. La pérdida se loguea una vez en el EventLog.
- **Primer contacto aplica el nivel.** Al primer frame aceptado de ToolX
  (arranque con la herramienta ya abajo, o fila recién habilitada) se fuerza
  el flanco para que `CheckWorkAndSteerSwitch` aplique el estado una vez; ya
  no hay que subir y bajar la herramienta para que pinte.
- **Polaridad de ToolX independiente de "Activo con contacto cerrado".** El
  bit se normaliza en el motor (`abajo ^ isWorkSwitchActiveLow`); ese flag
  describe al switch cableado y no invierte a ToolX. Cambiarlo en caliente
  con ToolX vivo invierte bit y "old" juntos (`SetWorkSwitchActiveLow`) para
  no fabricar un flanco que pise un apagado manual del master.
- `Settings.Load()` resetea `setF_isToolXWorkSwitch` antes de leer el XML:
  un perfil guardado por un build anterior (sin el tag) no hereda el valor
  del perfil que estaba activo.

### Tests
- `AgOpenGPS.Core.Tests/ToolXWorkSwitchTests.cs` (15 tests): frame ToolX
  solo toca el bit, polaridad, descarte con fila apagada, dueño del bit con
  el AIO vivo/perdido/flanco propio, primer contacto, reset de la fila,
  cambio de polaridad en caliente.

### Android (PilotX.Android) — paridad con Windows salvo hardware
- **Build reproducible**: `build-android.ps1` (gemelo de `build.ps1`) lee
  `Installer/VERSION`, estampa `versionName`/`versionCode` y deja
  `PilotX_android_v<version>.apk` + `.sha256` en la raíz. El proyecto no
  se compilaba desde julio y dos interfaces se le habían adelantado
  (`ICoverageService.GetSnapshot(cursor)`, cast inválido en
  `AndroidWebViewHost`).
- **Un solo código con Windows**: los adaptadores `Engine*` de
  `PilotX.GuidanceEngine/Adapters` se linkean por archivo en el csproj
  (wildcard, excluidos sólo los de sistema Windows/Linux y el Updater);
  se borraron las copias Android (`GuidanceEngineServices.cs`,
  `GuidanceEngineStateServices.cs`, `Fase1Stubs.cs`).
- **`HubBootstrap` gemelo de `EngineWebHost` + `CoreXEngineHost`**: cablea
  los 47 servicios del WebHost (perfiles, banderas, contorno, cabecera y
  líneas, tramlines, AB rápido, nudge, recPath, geometría, paths, tracks,
  dirección, **config de vehículo** e IMU ya funcionan en la tablet),
  anti-solape de secciones, alarmas de cabina, FlowX, OrbitXSync con
  vigilante, QuantiXMotorBridge con vigilante, CutDispatcher + velocidad
  por sección, **hello PGN 200 a 1 Hz** (los módulos WiFi y ToolX aprenden
  la IP de la tablet), **NTRIP** (sección `ntrip` de `corex-integrado.json`
  en el dataDir, RTCM por UDP :2233 a la subred) y comandos de guiado por
  MQTT (`agp/aog/guidance/command`). Anti-eco de PGN en el bridge LAN.
- **Plataforma**: `AndroidSistemaService` (brillo de ventana + sistema con
  WRITE_SETTINGS; reinicio si es device-owner; salir), `AndroidWifiService`
  (estado, escaneo, conectar/olvidar con la API clásica en API < 29 y
  sugerencias + panel del sistema en 29+), `AndroidWavPlayer` (alarmas por
  MediaPlayer), `BootReceiver` (arranque automático) y lock task cuando
  está permitido (kiosko).
- OrbitX: producto `PilotXAndroid` en el catálogo de firmwares (self-update
  del APK).
- Queda por hardware: GPS/IMU/dirección por USB-OTG (hoy sólo WiFi/UDP) y
  cámaras RTSP nativas. Sin probar en tablet en esta versión.

---

## [1.0.23] — 2026-06-16

### Added
- **Ventana flotante de "Datos del lote"**. El ícono de admiración (!) de la
  pantalla principal ahora abre los datos del lote como una ventana chica
  independiente (modo widget, 520×620, sin la navegación del Hub) para que el
  operario siga viendo el mapa por detrás.
- **Área del lote por lindero**. Se expone `BoundaryAreaM2` en el snapshot de
  estado (`/api/aog/state`) y se muestra en hectáreas en la tarjeta "Área del
  lote (lindero)". Mismo cálculo que las áreas GUI nativas de AOG (boundary
  exterior menos exclusiones internas).

### Fixed
- **Casing del endpoint `/api/aog/state`**. `datos-lote.js` y `datos-gps.js`
  normalizan el JSON PascalCase del host; antes leían claves camelCase y
  mostraban todo en cero.

---

## [1.0.4] — 2026-05-16

### Fixed
- **`PublishSensorConfig` ahora habla el contrato real del firmware `vistax-node` v2.5.x**.
  Antes publicaba a `vistax/nodos/{UID}/config` con `{cable, tipo, modo:"digital"|"pulsos"}`,
  un schema que arrastraba del legacy `vistax-server` Node y que el firmware
  nunca leyó (suscribe a `vistax/nodos/{UID}/cables/config`). Resultado:
  cuando el operario asignaba un sensor de tipo `bajada_herramienta` o `tolva`
  desde el form de mapeo de VistaX, el nodo seguía contando pulsos en ese pin
  en vez de leer estado digital con debounce.
  - Topic correcto: `vistax/nodos/{UID}/cables/config`.
  - Payload correcto: `{"cables":[{"cable":N,"modo":"pulse"|"state","invertido":false}]}`.
  - Mapeo `tipo → modo` alineado con la semántica del firmware:
    - `bajada_herramienta`, `tolva` → `state`
    - `semilla`, `ferti_linea`, `ferti_costado`, `turbina` → `pulse`
    (la versión anterior mandaba turbina como `digital`, pero el firmware
    cuenta RPM como pulse → ahora coincide).
  - `PublishAllSensorConfigs` agrupa por UID y manda **un** payload por nodo
    con todos sus cables (antes mandaba uno por sensor y solo para tipos
    "especiales", dejando el resto sin notificar).
  - Se mantiene retain flag para que el nodo reciba la config al reconectar
    tras un reboot.

---

## [1.0.3] — 2026-05-16

(Versión transicional — el bump de VERSION quedó pendiente entre commits.)

---

## [1.0.2] — 2026-05-14

VistaX integrado en la pantalla principal y en el HUB piloto, con paleta
nueva y silenciado por sensor.

### Added
- **Mute por sensor** (silenciar fallas individuales sin frenar el monitoreo):
  - DTO `VistaXSensorConfigDto.Muted` + `VistaXSurcoStateDto.Muted`.
  - Endpoint REST `POST /api/vistax/sensor/mute` (body `{uid, cable, muted}`)
    persiste el cambio en `implemento.json` (`mapeo_sensores[].muted`).
  - `SeedMonitor.SetSensorMuted(uid, cable, muted)` — aplica el toggle a
    todos los surcos cubiertos por el sensor; descarta alertas en surcos
    silenciados.
  - `VistaXConfig.SaveImplemento(...)` — antes solo había `Load`.
  - Doble-click sobre un tubo en `VistaXNativePanel` → toggle de mute con
    persistencia inmediata vía evento `SensorMuteToggleRequested`.
  - HUB (`pages/vistax.html`): menú contextual click derecho con
    "Silenciar / reactivar sensor".
  - HUB piloto (`pages/piloto.html` widget de siembra): click sobre la
    celda toggle mute del sensor que la alimenta.
- **Paleta VistaX compartida** (`theme.css`): nuevos tokens `--vx-ok`,
  `--vx-tapado`, `--vx-exceso`, `--vx-no-data`, `--vx-muted` (con sus
  variantes `-dark` / `-border`). Misma paleta en GDI+ (`VistaXNativePanel`)
  y en CSS/JS del HUB.
- **Estado por surco más rico**: `SurcoState` ahora trae `Uid`, `Cable`,
  `Objetivo` y `Muted` para que la UI muestre real/objetivo y permita
  el silenciado sin lookups extra.

### Changed
- **VistaXNativePanel** (panel GDI+ embebido en `FormGPS`):
  - Paleta migrada de `#00e676 / #ff1744 / #ffea00` (verde fluo / rojo
    sangre / amarillo) a la paleta dark-cockpit del producto:
    verde Agro Parallel `#4BA63F`, azul `#3D8BFD` para exceso, gris
    `#3A3F44` para no-data, `#4A5055` para muted, negro `#050505`
    para tapado.
  - Estados de pildora rediseñados: `Ok | Bajo | Tapado | Exceso |
    NoData | Muted` (antes: `Ok | Desvio | Alerta | Tapado | SinSenal`).
    "Desvio" se separa en `Bajo` (degradé negro→verde por ratio) y
    `Exceso` (azul); `Alerta` puro se unifica con `Tapado`.
  - Muted se dibuja con gris desaturado + línea horizontal central
    (consistente con el indicador del HUB).
- **HUB VistaX** (`pages/vistax.html` + `js/vistax.js`):
  - Renderer del monitor por surco usa `colorForSurco(s)` para producir
    el mismo color que el panel nativo.
  - Contadores del header reportan los 6 estados nuevos.
- **HUB piloto** (`pages/piloto.html` + `js/piloto.js`):
  - Widget de siembra alineado con la paleta nueva, tooltip muestra
    `real / obj N`, click sobre la celda silencia el sensor.

### Fixed
- `SpmPromedio` y `FallasActivas` ya no contabilizan sensores silenciados
  (en HUB y en panel nativo), evitando alarmas espurias cuando un
  sensor está deliberadamente apagado.

---

## [1.0.1] — 2026-05-14

Primera versión Agro Parallel-only del shell (legacy AOG quedó atrás
en 1.2.31, este es el reset de numeración para el producto PilotX).

### Added
- `CHANGELOG.md` (este archivo) — registro de cambios por versión.
- Stamping de versión en `AgOpenGPS.exe` durante el build: `build.ps1`
  ahora acepta `-Version` y lo propaga a `dotnet build -p:Version=`,
  para que `AssemblyInformationalVersionAttribute` se llene y el
  auto-update (`PilotXSelfUpdate.DetectCurrentVersion`) compare bien.

### Changed
- **Pantalla de Términos y Condiciones** (`FormTermsAndConditions`):
  traducida al español, marca Agro Parallel, deslinde de responsabilidad
  explícito de Leonardo Bracco + Agro Parallel + distribuidores frente
  a daños del piloto automático. Texto GPL preservado intacto (requisito
  legal de la licencia).
- **Links externos** de la pantalla de bienvenida:
  - Sitio web → `https://agroparallel.com`
  - Instagram → `https://www.instagram.com/agro.parallel/`
  - Botón YouTube oculto.
- **HUB web (WebView2)**: removidas las menciones operario-visibles a
  "AOG" / "AgOpenGPS" en textos, tooltips y diálogos. Reemplazadas por
  "Piloto" / "el piloto". Identificadores internos (DOM IDs, claves JSON
  como `seccion_aog`, endpoints `/api/aog/*`) se mantienen para no
  romper compatibilidad con AgIO ni con el cloud OrbitX.
- **Form de Config OrbitX** (`pages/orbitx.html` + `js/orbitx.js`):
  - `Server URL` ahora es readonly (URL fija del producto).
  - `Device ID` readonly (MD5 de la MAC).
  - `Device Token` readonly (lo persiste el sistema en el primer
    heartbeat exitoso contra OrbitX).
  - `Establecimiento slug` readonly (lo trae el heartbeat).
  - Campo `Master Token` removido de la UI (es un secreto compartido;
    sigue persistiéndose en el archivo de config, fuera de la vista del
    operario).

### Fixed
- **Auto-update fallaba con "OrbitX no está configurado (ServerUrl /
  DeviceId / DeviceToken)"** en instalaciones nuevas. `PilotXSelfUpdate`
  exigía un `DeviceToken` poblado, pero éste sólo se popula después del
  primer heartbeat de `OrbitXSync`. Si el sync estaba apagado o no había
  corrido todavía, el botón Actualizar siempre tiraba ese error.
  - `CheckAsync` y `DownloadAsync` ahora validan únicamente
    `ServerUrl + DeviceId`.
  - El header `X-Auth-Token` usa `EffectiveToken(cfg)`, que cae al
    `MasterToken` cuando `DeviceToken` está vacío (mismo patrón que ya
    usaban `OrbitXSync.TryAutoRegister` y
    `OrbitXConfigService.TestConnectionAsync`).
  - El servidor reconoce el MasterToken y auto-registra el device en su
    primera llamada, así que el flujo completo de check/download de OTA
    funciona desde el primer click, sin pasos manuales.

### Firmware relacionado
- QuantiX `2.1.2` — fix de colisión de UIDs entre placas (ver
  `Productos/AGP-VR/Software/Firmware_Embebido/Quantix2Motors/CHANGELOG.md`).

---

## [1.2.31] — pre-Agro Parallel

Última versión heredada del legacy AgOpenGPS upstream. Se mantiene como
referencia histórica; el versionado se reinicia en 1.0.0/1.0.1 para
marcar la línea PilotX.
