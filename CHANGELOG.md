# Changelog â€” PilotX (AgOpenGPS â€” Agro Parallel)

Todos los cambios relevantes a la app PC del tractor se anotan acÃ¡.

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y versionado [SemVer](https://semver.org/lang/es/) (MAJOR.MINOR.PATCH).

La fuente de verdad de la versiÃ³n es `Installer/VERSION`. El build
(`build.ps1` / `build-installer.ps1`) la lee de ahÃ­ y la estampa en
el ensamblado vÃ­a `-p:Version=` para que `PilotXSelfUpdate` la pueda
detectar en runtime y compararla contra el catÃ¡logo OTA.

---

## [1.0.54] â€” 2026-09-06

### Fixed
- **El piso de PWM grabado en el nodo duraba 2 segundos.** El puente de FlowX
  le manda al nodo el `pwm_min` de `flowX.json` en cada target (cada 2 s) y
  pisaba cualquier valor grabado directo en el nodo. Se descubrio en campo:
  la reguladora quedaba un 36% corta de dosis porque el piso efectivo volvia
  a 800 (a ese PWM la valvula casi no se mueve) por mas que se grabara 1000 o
  1500 en el nodo. PilotX pasa a ser la unica fuente de verdad del piso
  positivo: `flowx_pisos` ahora tambien lo escribe en la config de PilotX.

### Added
- **`flowx_config`**: ajustar por el canal de soporte la configuracion de FlowX
  que vive en PilotX: `pwm_min`, `dosis_lha`, `modo_manual`, `manual_lmin`,
  `meter_cal`. Solo esos cinco campos, con rangos validados; escribe
  `flowX.json` y el puente lo aplica solo en el proximo ciclo. Es lo que
  faltaba para afinar la regulacion a distancia sin pedirle a nadie que toque
  la pantalla.

---

## [1.0.53] â€” 2026-09-06

### Added
- **Soporte remoto de FlowX, acotado.** Cuatro acciones nuevas en el canal de
  diagnostico, para operar la regulacion desde el panel sin dictarle comandos
  por telefono a quien este en el tractor. Cada una hace UNA cosa nombrada
  contra la API local del Engine (loopback); no reciben rutas ni comandos
  libres, solo parametros validados. Se eligio esto sobre un proxy abierto a
  proposito: si el token de un equipo se filtra, el dano posible queda
  enumerado, no es "cualquier cosa".
  - `flowx_diag` â€” caudal, PWM, objetivo, config y estado de secciones (lectura).
  - `flowx_pwm` â€” mueve la valvula a un PWM (-4095..4095) por 1..30 s, con
    corte automatico al terminar: nunca queda clavada.
  - `flowx_pisos` â€” graba los pwm_min de arranque por sentido (0..4095).
  - `secciones_manual` â€” prende/apaga el maestro de secciones en manual.
  Nacio de la sesion de campo del 2026-09-06 (ver [[flowx-regulacion-campo]]):
  domar la reguladora Raven llevo horas de comandos a mano por consola porque
  el canal no podia operar nada, solo leer.

---

## [1.0.52] â€” 2026-09-06

> **Primera versiÃ³n que se puede instalar como parche.** Si el equipo tiene la
> 1.0.51, esto son ~5 MB en vez de 193.

### Added
- **Canal de diagnÃ³stico remoto.** La pantalla le pregunta a OrbitX cada 20
  segundos si hay algÃºn diagnÃ³stico pendiente, corre la funciÃ³n y devuelve el
  texto. Con eso se puede ver quÃ© pasa en una mÃ¡quina del campo sin dictarle
  comandos por telÃ©fono a quien estÃ© parado adelante.
  - **No abre ningÃºn puerto en el tractor.** Todo sale de adentro hacia afuera,
    sobre la misma autenticaciÃ³n por dispositivo que ya usa el sync. Sin
    conexiÃ³n no pasa nada: reintenta espaciando hasta 5 minutos.
  - **SÃ³lo se pueden pedir las ocho funciones de sÃ³lo lectura** que ya estaban
    escritas: estado, sistema, red, puertos, procesos, firewall, logs y nodos.
    Lo que llega del cloud es una clave de diccionario, no un comando: si no
    estÃ¡ en el catÃ¡logo se rechaza. No hay ninguna ruta por la que un texto
    arbitrario llegue a ejecutarse.
  - La salida se sanitiza (tokens y contraseÃ±as) y se corta en 256 KB. Timeout
    por funciÃ³n, tope 300 segundos, y de a una por vez.
  - En una pantalla sin vincular el servicio duerme: sin identidad no consulta.

### Lado servidor (ya desplegado, no requiere esta versiÃ³n)
- `routes/soporte.js` en OrbitX, con `GET /api/soporte/pendientes` y
  `POST /api/soporte/resultado` para el equipo, y `POST /api/soporte/comando`,
  `GET /api/soporte/comandos` y `GET /api/soporte/catalogo` para el panel.
- **Agujero encontrado al probar y cerrado**: `auth.required` tambiÃ©n autentica
  equipos, asÃ­ que un token de dispositivo podÃ­a encolar diagnÃ³sticos sobre
  **cualquier** mÃ¡quina de la flota y leer la salida. Los tres endpoints de
  panel ahora rechazan tokens de dispositivo, igual que hace `routes/devices.js`.
  Verificado: 403 al encolar sobre sÃ­ mismo, 403 sobre otro equipo, 403 al leer
  el historial, y 200 al pedir los pendientes propios.

---

## [1.0.51] â€” 2026-09-05

> **Esta es la versiÃ³n que habilita las actualizaciones chicas.** A partir de
> acÃ¡, una correcciÃ³n se manda como un parche de ~5 MB en vez de un paquete de
> 193 MB. Para llegar hasta acÃ¡ sÃ­ hace falta el paquete completo, una Ãºltima
> vez.

### Added
- **El actualizador entiende parches y los valida.** Un parche trae sÃ³lo lo que
  cambiÃ³ (unos 5 MB contra 193) y se aplica encima sin borrar nada. Antes de
  tocar el disco comprueba que la instalaciÃ³n sea exactamente la versiÃ³n base
  para la que se armÃ³, comparando contra la versiÃ³n real del ejecutable
  instalado. Si no coincide, **aborta sin tocar nada**: no cierra procesos, no
  respalda, no extrae, y explica que hace falta el paquete completo.
  - Esa comprobaciÃ³n es la que faltaba cuando se mandaron DLL sueltas mal
    versionadas y una pantalla dejÃ³ de arrancar. Los ensamblados se referencian
    por versiÃ³n exacta: mezclar versiones no da un error entendible, da una app
    que no abre.
- **`instalar-pilotx.ps1` acepta las dos cosas y distingue sola cuÃ¡l es.**
  - Paquete completo: reemplaza `Desktop`, `Engine` y `BarsHost` enteras,
    respaldando con un rename instantÃ¡neo.
  - Parche: aplica encima y respalda **sÃ³lo los archivos que va a pisar**, para
    poder deshacer con precisiÃ³n. Si el parche no corresponde a la versiÃ³n
    instalada, lo rechaza antes de empezar.
  - En los dos casos el Ãºltimo paso es abrir la pantalla; si no abre, deshace.

### CÃ³mo se saca un parche
```
.uild.ps1                      compila y verifica la versiÃ³n nueva
.uild-parche.ps1 -Base 1.0.51  arma el parche contra esa base
```
Se publica en OrbitX como cualquier versiÃ³n. El equipo lo descarga igual que
siempre; la diferencia es que baja 5 MB en vez de 193.

---

## [1.0.50] â€” 2026-09-05

> Reemplaza a la 1.0.49, que saliÃ³ **sin RustDesk**. Si ya instalaste la 1.0.49
> y la pantalla anda, no es urgente; sÃ­ conviene antes de entregar un equipo
> nuevo.

### Fixed
- **RustDesk volviÃ³ al paquete.** La 1.0.49 saliÃ³ sin Ã©l y con eso se perdÃ­a el
  soporte remoto sin instalaciÃ³n. La carpeta `Desktop\RustDesk\` nunca fue
  parte del build: sobrevivÃ­a en `Build\` de casualidad porque nada la
  borraba, y al empezar a limpiar antes de publicar (1.0.49) desapareciÃ³ sin
  que ninguna prueba lo notara.
  - Ahora vive en `Tools\RustDesk\` y `build.ps1` la copia, avisando en rojo
    si falta. **No se versiona en git**: el nombre del ejecutable lleva el
    servidor y la clave del relay propio, y este repositorio es pÃºblico.
  - En una pantalla que ya tiene RustDesk instalado no cambiaba nada; el
    cÃ³digo sale sin hacer nada si la carpeta no estÃ¡. El problema era para
    equipos nuevos.

### Added
- **El build ahora tambiÃ©n prueba el Engine.** Lo levanta con `--webhost
  --corex` y verifica que responda la API en `127.0.0.1:5180`. El Engine se
  publica con ReadyToRun sin composite, que es exactamente la combinaciÃ³n que
  dejÃ³ sin arrancar a `PilotX.Desktop`; una pantalla que abre sin Engine no
  sirve para trabajar.

### Verificado en esta revisiÃ³n
- El paquete **no** lleva credenciales, tokens ni configuraciÃ³n de la mÃ¡quina
  de desarrollo. DejÃ³ de viajar `Engine/GuidanceEngineData/ultimo_lote.txt`,
  que era estado local metiÃ©ndose en la instalaciÃ³n del cliente.
- `System.IO.Ports.dll` y `System.Management.dll` estÃ¡n donde corresponde. Las
  copias que desaparecieron bajo `Engine/runtimes/` eran restos de builds
  viejos para Android, Linux y macOS que no se usaban.

---

## [1.0.49] â€” 2026-09-05

> **La 1.0.48 NO ARRANCA. No instalarla.** Se retirÃ³ del catÃ¡logo de OrbitX.
> Esta versiÃ³n la reemplaza y corrige lo que la rompiÃ³.

### Fixed
- **La 1.0.48 morÃ­a al arrancar** con `FailFast` (0xC0000602) y sin ningÃºn
  mensaje, en cualquier mÃ¡quina y aunque la instalaciÃ³n fuera limpia. Se
  comprobÃ³ comparando las dos versiones extraÃ­das en la misma PC: la 1.0.47
  abre, la 1.0.48 muere. LlegÃ³ a instalarse en el equipo de un cliente.
  - **Causa raÃ­z: `build.ps1` nunca limpiaba las carpetas de publicaciÃ³n.**
    `dotnet publish -o` no borra lo que habÃ­a: escribe encima y convive con
    los archivos del build anterior. `Build\Desktop` llegÃ³ a tener 172
    archivos de un build y 53 de otro al mismo tiempo. La 1.0.48 se publicÃ³
    con ReadyToRun apagado pero quedaron las DLL **con** ReadyToRun de la
    1.0.47, y esa mezcla compila y empaqueta sin una sola advertencia.
  - Ahora `Desktop`, `Engine` y `BarsHost` se borran antes de cada publish.
    No se toca `AgroParallel\wwwroot` (lo espeja robocopy), ni `Branding`,
    ni `Fonts`, ni `config-captures`.
- **`PilotX.Desktop` se publica sin ReadyToRun.** La combinaciÃ³n
  ReadyToRun activado **sin** composite produce un binario que no arranca.
  Apagado del todo funciona, y ademÃ¡s cada ensamblado queda chico, que era el
  objetivo de sacar el composite. Costo medido del arranque en frÃ­o: 3777 ms
  contra 3042 ms del composite.

### Added
- **El build prueba que la aplicaciÃ³n abre antes de empaquetar.** Lanza
  `PilotX.Desktop.exe` y falla el build si muere en los primeros 25 segundos.
  Compilar sin errores no probaba nada: la 1.0.48 compilÃ³ limpia, se empaquetÃ³,
  se subiÃ³ al cloud y se instalÃ³ en un tractor sin que nadie la hubiera
  ejecutado una sola vez. Se saltea con `-SkipSmoke` en mÃ¡quinas sin sesiÃ³n
  grÃ¡fica.
- **`setup_pilotx_lan.bat` ahora viaja en el paquete.** VivÃ­a sÃ³lo en el repo,
  asÃ­ que la regla de UDP 9999 que necesita el ToolX nunca llegaba a una
  mÃ¡quina de cliente. El sÃ­ntoma era engaÃ±oso: el nodo aparecÃ­a CONECTADO por
  MQTT mientras su PGN se descartaba en silencio y la herramienta no pintaba.

### Verificado antes de publicar
- 1.0.49 limpia: abre, 1566 ms.
- 1.0.47 con 1.0.49 extraÃ­da encima (el camino real de actualizaciÃ³n): abre,
  1483 ms, incluso con el `PilotX.Desktop.r2r.dll` huÃ©rfano de 90 MB todavÃ­a
  en la carpeta. Sin ReadyToRun ya nadie lo carga; queda como peso muerto y se
  puede borrar a mano.

---

## [1.0.48] â€” 2026-09-05

### Fixed
- **La configuraciÃ³n de FlowX se borraba sola en el tractor** (caso de campo:
  `flowX.json` volvÃ­a a `{"enabled":true,"nodos":[],"ignorados":[]}` y el
  cliente perdÃ­a los nodos ya configurados). `AtomicJson.Read` podÃ­a caer justo
  en la ventana del fallback de escritura, donde hay un `Delete` seguido de un
  `Move` y por un instante el archivo NO existe. El `Load()` interpretaba ese
  null como "no hay config" y escribÃ­a los defaults ENCIMA de la buena. Con el
  puente releyendo cada 2 segundos, la loterÃ­a se jugaba todo el tiempo.
  - `AtomicJson.Read` ahora reintenta (3 Ã— 40 ms) antes de rendirse, y sÃ³lo
    reintenta si hay algo en disco.
  - Nuevo `AtomicJson.Existe(path)`: mira el archivo y su `.bak`.
  - Los seis `Load()` (FlowX, OrbitX, QuantiX, SectionX, StormX, VistaX) sÃ³lo
    crean el archivo de defaults cuando de verdad no hay nada en disco. Nunca
    mÃ¡s se pisa una configuraciÃ³n que existe pero no se pudo leer en ese
    instante.
- **El puente de FlowX quedaba mudo para siempre** si el broker MQTT todavÃ­a no
  estaba levantado cuando arrancÃ³. `FlowXBridge.StartAsync()` conectaba una sola
  vez, y si fallaba lo dejaba anotado en el log y no reintentaba nunca: las
  secciones no abrÃ­an y no habÃ­a ninguna seÃ±al de por quÃ©. Ahora hay un
  watchdog (primer intento a los 2 s, despuÃ©s cada 15 s) igual al que ya tenÃ­an
  QuantiX y el CutDispatcher.

### Changed
- **Las actualizaciones pasan a ser livianas y parcheables por archivo.** Se
  apagÃ³ `PublishReadyToRunComposite`. Con composite, todo el cÃ³digo nativo de
  todos los ensamblados vivÃ­a en un Ãºnico `PilotX.Desktop.r2r.dll` de 94,3 MB:
  cambiar una lÃ­nea obligaba a regenerarlo entero, asÃ­ que el paquete mÃ­nimo de
  un fix era de ~90 MB y era imposible mandar una DLL suelta. Sin composite
  cada ensamblado lleva su propio cÃ³digo ReadyToRun adentro, el arranque sigue
  precompilado (no se vuelve a JIT puro) y un fix puntual se manda como esa
  sola DLL.
  - Recordatorio operativo que costÃ³ caro: una DLL suelta SÃ“LO sirve si se
    compilÃ³ con el `-p:Version=` exacto de la instalaciÃ³n destino. Los
    ensamblados se referencian por versiÃ³n exacta y mezclar versiones impide
    que la app arranque.
- `setup_pilotx_lan.bat` ahora abre tambiÃ©n **UDP 9999** (ademÃ¡s de TCP 5180 y
  5181). Sin esa regla un nodo ToolX aparecÃ­a online por MQTT mientras su PGN
  se descartaba en silencio y la herramienta no pintaba.

---

## [1.0.47] â€” 2026-09-05

> Las versiones 1.0.45 y 1.0.46 se publicaron desde otra sesiÃ³n y no dejaron
> entrada acÃ¡; sus cambios no estÃ¡n documentados en este archivo.

### Added
- **AsignaciÃ³n manual de cortes en el editor de FlowX** (pestaÃ±a "Cortes y
  secciones"). AdemÃ¡s del reparto automÃ¡tico, ahora hay un desplegable por
  secciÃ³n para elegir a mano quÃ© corte (la salida S1, S2â€¦ del nodo) la abre,
  o dejarla sin asignar. Varias secciones pueden compartir un corte. El mapa
  se actualiza en el momento, sin rebuild, para no cerrar el desplegable ni
  perder el scroll.
  - La lista de secciones sale de lo que reporta PilotX en vivo, sin ninguna
    cantidad fija: si el implemento cambia de 7 a 24 secciones aparecen 24
    filas, la grilla envuelve y el bloque scrollea. El Ãºnico techo sigue
    siendo el del protocolo del nodo (16 cortes: el bitmask que viaja al
    firmware es de 16 bits).
  - El mapa avisa lo que antes no se veÃ­a: secciones sin corte asignado,
    cortes sin usar, y si el corte elegido como master ademÃ¡s tiene secciones.

### Changed
- **La cantidad de cortes se persiste** (`cortes` en `flowX.json`, campo
  aditivo). Antes se deducÃ­a contando los cables asignados, y con asignaciÃ³n
  manual eso se rompÃ­a: usar S1, S2 y S5 son cinco salidas, no tres, y al
  reentrar se perdÃ­an las de arriba. Como respaldo para configuraciones
  anteriores y para las que guarda la PWA, ahora se infiere del corte mÃ¡s
  alto asignado en vez de contar los distintos.

### Fixed
- **`setup_pilotx_lan.bat` no abrÃ­a UDP 9999**, el puerto por donde entra el
  PGN de todos los mÃ³dulos que hablan por WiFi (GPS/NMEA, AutoSteer,
  CoreX-ECU y el switch ToolX). Como el broker MQTT (TCP 1883) sÃ­ tenÃ­a
  regla, el sÃ­ntoma era mudo y confuso: el nodo aparecÃ­a conectado en el
  panel y su dato no llegaba nunca al motor. Diagnosticado en campo con
  ToolX (2026-09-04). Se agregan ademÃ¡s TCP 5180 (Hub y API, el 8080 es el
  panel viejo de AgIO) y TCP 5181 (panel CoreX), y un aviso sobre la red
  WiFi marcada como pÃºblica, que bloquea igual.

---

## [1.0.44] â€” 2026-09-03

### Added
- **ToolX: switch de herramienta inalÃ¡mbrico (ESP32).** El nodo manda el
  work switch como PGN 253 por UDP :9999 con byte de origen `0x7C`
  (`CModuleComm.ToolXSource`). `PgnReceiver` reconoce ese origen y toma
  **solo el bit de trabajo**: Ã¡ngulo, heading, roll, bit de direcciÃ³n y PWM
  de esos frames se ignoran, asÃ­ conviven con el mÃ³dulo de direcciÃ³n real
  (que sigue mandando el switch de direcciÃ³n y "secciones al activar el
  piloto").
- **Fila "ToolX (switch inalÃ¡mbrico)" en Secciones â€º Switches**, hija de
  "Activar" en la carta de trabajo, en la pantalla nativa (`SwitchesTab`) y
  en la PWA del celular (`config.html`). Setting `setF_isToolXWorkSwitch`,
  JSON `work_toolx_enabled`; con la fila apagada el motor descarta los
  frames de ToolX enteros (un nodo ajeno en la LAN no puede tocar las
  secciones). El rÃ³tulo agrega "Â· conectado" cuando el motor recibe frames
  (`work_toolx_alive`, runtime, refrescado cada 3 s en las dos UIs).
- ToolX en el catÃ¡logo del Firmware Manager (`FirmwaresPanel`) para OTA.

### Changed
- **DueÃ±o del bit de trabajo.** Con ToolX habilitado y vivo (frames hace
  menos de 5 s, reloj monotÃ³nico) el mÃ³dulo de direcciÃ³n no pisa
  `workSwitchHigh`. Si ToolX se pierde, el bit queda en su Ãºltimo valor y el
  mÃ³dulo de direcciÃ³n solo lo escribe cuando **su propio** switch cambia:
  un microcorte WiFi del nodo ya no corta las secciones en medio de la
  pasada. La pÃ©rdida se loguea una vez en el EventLog.
- **Primer contacto aplica el nivel.** Al primer frame aceptado de ToolX
  (arranque con la herramienta ya abajo, o fila reciÃ©n habilitada) se fuerza
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
  solo toca el bit, polaridad, descarte con fila apagada, dueÃ±o del bit con
  el AIO vivo/perdido/flanco propio, primer contacto, reset de la fila,
  cambio de polaridad en caliente.

### Android (PilotX.Android) â€” paridad con Windows salvo hardware
- **Build reproducible**: `build-android.ps1` (gemelo de `build.ps1`) lee
  `Installer/VERSION`, estampa `versionName`/`versionCode` y deja
  `PilotX_android_v<version>.apk` + `.sha256` en la raÃ­z. El proyecto no
  se compilaba desde julio y dos interfaces se le habÃ­an adelantado
  (`ICoverageService.GetSnapshot(cursor)`, cast invÃ¡lido en
  `AndroidWebViewHost`).
- **Un solo cÃ³digo con Windows**: los adaptadores `Engine*` de
  `PilotX.GuidanceEngine/Adapters` se linkean por archivo en el csproj
  (wildcard, excluidos sÃ³lo los de sistema Windows/Linux y el Updater);
  se borraron las copias Android (`GuidanceEngineServices.cs`,
  `GuidanceEngineStateServices.cs`, `Fase1Stubs.cs`).
- **`HubBootstrap` gemelo de `EngineWebHost` + `CoreXEngineHost`**: cablea
  los 47 servicios del WebHost (perfiles, banderas, contorno, cabecera y
  lÃ­neas, tramlines, AB rÃ¡pido, nudge, recPath, geometrÃ­a, paths, tracks,
  direcciÃ³n, **config de vehÃ­culo** e IMU ya funcionan en la tablet),
  anti-solape de secciones, alarmas de cabina, FlowX, OrbitXSync con
  vigilante, QuantiXMotorBridge con vigilante, CutDispatcher + velocidad
  por secciÃ³n, **hello PGN 200 a 1 Hz** (los mÃ³dulos WiFi y ToolX aprenden
  la IP de la tablet), **NTRIP** (secciÃ³n `ntrip` de `corex-integrado.json`
  en el dataDir, RTCM por UDP :2233 a la subred) y comandos de guiado por
  MQTT (`agp/aog/guidance/command`). Anti-eco de PGN en el bridge LAN.
- **Plataforma**: `AndroidSistemaService` (brillo de ventana + sistema con
  WRITE_SETTINGS; reinicio si es device-owner; salir), `AndroidWifiService`
  (estado, escaneo, conectar/olvidar con la API clÃ¡sica en API < 29 y
  sugerencias + panel del sistema en 29+), `AndroidWavPlayer` (alarmas por
  MediaPlayer), `BootReceiver` (arranque automÃ¡tico) y lock task cuando
  estÃ¡ permitido (kiosko).
- OrbitX: producto `PilotXAndroid` en el catÃ¡logo de firmwares (self-update
  del APK).
- Queda por hardware: GPS/IMU/direcciÃ³n por USB-OTG (hoy sÃ³lo WiFi/UDP) y
  cÃ¡maras RTSP nativas. Sin probar en tablet en esta versiÃ³n.

---

## [1.0.23] â€” 2026-06-16

### Added
- **Ventana flotante de "Datos del lote"**. El Ã­cono de admiraciÃ³n (!) de la
  pantalla principal ahora abre los datos del lote como una ventana chica
  independiente (modo widget, 520Ã—620, sin la navegaciÃ³n del Hub) para que el
  operario siga viendo el mapa por detrÃ¡s.
- **Ãrea del lote por lindero**. Se expone `BoundaryAreaM2` en el snapshot de
  estado (`/api/aog/state`) y se muestra en hectÃ¡reas en la tarjeta "Ãrea del
  lote (lindero)". Mismo cÃ¡lculo que las Ã¡reas GUI nativas de AOG (boundary
  exterior menos exclusiones internas).

### Fixed
- **Casing del endpoint `/api/aog/state`**. `datos-lote.js` y `datos-gps.js`
  normalizan el JSON PascalCase del host; antes leÃ­an claves camelCase y
  mostraban todo en cero.

---

## [1.0.4] â€” 2026-05-16

### Fixed
- **`PublishSensorConfig` ahora habla el contrato real del firmware `vistax-node` v2.5.x**.
  Antes publicaba a `vistax/nodos/{UID}/config` con `{cable, tipo, modo:"digital"|"pulsos"}`,
  un schema que arrastraba del legacy `vistax-server` Node y que el firmware
  nunca leyÃ³ (suscribe a `vistax/nodos/{UID}/cables/config`). Resultado:
  cuando el operario asignaba un sensor de tipo `bajada_herramienta` o `tolva`
  desde el form de mapeo de VistaX, el nodo seguÃ­a contando pulsos en ese pin
  en vez de leer estado digital con debounce.
  - Topic correcto: `vistax/nodos/{UID}/cables/config`.
  - Payload correcto: `{"cables":[{"cable":N,"modo":"pulse"|"state","invertido":false}]}`.
  - Mapeo `tipo â†’ modo` alineado con la semÃ¡ntica del firmware:
    - `bajada_herramienta`, `tolva` â†’ `state`
    - `semilla`, `ferti_linea`, `ferti_costado`, `turbina` â†’ `pulse`
    (la versiÃ³n anterior mandaba turbina como `digital`, pero el firmware
    cuenta RPM como pulse â†’ ahora coincide).
  - `PublishAllSensorConfigs` agrupa por UID y manda **un** payload por nodo
    con todos sus cables (antes mandaba uno por sensor y solo para tipos
    "especiales", dejando el resto sin notificar).
  - Se mantiene retain flag para que el nodo reciba la config al reconectar
    tras un reboot.

---

## [1.0.3] â€” 2026-05-16

(VersiÃ³n transicional â€” el bump de VERSION quedÃ³ pendiente entre commits.)

---

## [1.0.2] â€” 2026-05-14

VistaX integrado en la pantalla principal y en el HUB piloto, con paleta
nueva y silenciado por sensor.

### Added
- **Mute por sensor** (silenciar fallas individuales sin frenar el monitoreo):
  - DTO `VistaXSensorConfigDto.Muted` + `VistaXSurcoStateDto.Muted`.
  - Endpoint REST `POST /api/vistax/sensor/mute` (body `{uid, cable, muted}`)
    persiste el cambio en `implemento.json` (`mapeo_sensores[].muted`).
  - `SeedMonitor.SetSensorMuted(uid, cable, muted)` â€” aplica el toggle a
    todos los surcos cubiertos por el sensor; descarta alertas en surcos
    silenciados.
  - `VistaXConfig.SaveImplemento(...)` â€” antes solo habÃ­a `Load`.
  - Doble-click sobre un tubo en `VistaXNativePanel` â†’ toggle de mute con
    persistencia inmediata vÃ­a evento `SensorMuteToggleRequested`.
  - HUB (`pages/vistax.html`): menÃº contextual click derecho con
    "Silenciar / reactivar sensor".
  - HUB piloto (`pages/piloto.html` widget de siembra): click sobre la
    celda toggle mute del sensor que la alimenta.
- **Paleta VistaX compartida** (`theme.css`): nuevos tokens `--vx-ok`,
  `--vx-tapado`, `--vx-exceso`, `--vx-no-data`, `--vx-muted` (con sus
  variantes `-dark` / `-border`). Misma paleta en GDI+ (`VistaXNativePanel`)
  y en CSS/JS del HUB.
- **Estado por surco mÃ¡s rico**: `SurcoState` ahora trae `Uid`, `Cable`,
  `Objetivo` y `Muted` para que la UI muestre real/objetivo y permita
  el silenciado sin lookups extra.

### Changed
- **VistaXNativePanel** (panel GDI+ embebido en `FormGPS`):
  - Paleta migrada de `#00e676 / #ff1744 / #ffea00` (verde fluo / rojo
    sangre / amarillo) a la paleta dark-cockpit del producto:
    verde Agro Parallel `#4BA63F`, azul `#3D8BFD` para exceso, gris
    `#3A3F44` para no-data, `#4A5055` para muted, negro `#050505`
    para tapado.
  - Estados de pildora rediseÃ±ados: `Ok | Bajo | Tapado | Exceso |
    NoData | Muted` (antes: `Ok | Desvio | Alerta | Tapado | SinSenal`).
    "Desvio" se separa en `Bajo` (degradÃ© negroâ†’verde por ratio) y
    `Exceso` (azul); `Alerta` puro se unifica con `Tapado`.
  - Muted se dibuja con gris desaturado + lÃ­nea horizontal central
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
  sensor estÃ¡ deliberadamente apagado.

---

## [1.0.1] â€” 2026-05-14

Primera versiÃ³n Agro Parallel-only del shell (legacy AOG quedÃ³ atrÃ¡s
en 1.2.31, este es el reset de numeraciÃ³n para el producto PilotX).

### Added
- `CHANGELOG.md` (este archivo) â€” registro de cambios por versiÃ³n.
- Stamping de versiÃ³n en `AgOpenGPS.exe` durante el build: `build.ps1`
  ahora acepta `-Version` y lo propaga a `dotnet build -p:Version=`,
  para que `AssemblyInformationalVersionAttribute` se llene y el
  auto-update (`PilotXSelfUpdate.DetectCurrentVersion`) compare bien.

### Changed
- **Pantalla de TÃ©rminos y Condiciones** (`FormTermsAndConditions`):
  traducida al espaÃ±ol, marca Agro Parallel, deslinde de responsabilidad
  explÃ­cito de Leonardo Bracco + Agro Parallel + distribuidores frente
  a daÃ±os del piloto automÃ¡tico. Texto GPL preservado intacto (requisito
  legal de la licencia).
- **Links externos** de la pantalla de bienvenida:
  - Sitio web â†’ `https://agroparallel.com`
  - Instagram â†’ `https://www.instagram.com/agro.parallel/`
  - BotÃ³n YouTube oculto.
- **HUB web (WebView2)**: removidas las menciones operario-visibles a
  "AOG" / "AgOpenGPS" en textos, tooltips y diÃ¡logos. Reemplazadas por
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
    sigue persistiÃ©ndose en el archivo de config, fuera de la vista del
    operario).

### Fixed
- **Auto-update fallaba con "OrbitX no estÃ¡ configurado (ServerUrl /
  DeviceId / DeviceToken)"** en instalaciones nuevas. `PilotXSelfUpdate`
  exigÃ­a un `DeviceToken` poblado, pero Ã©ste sÃ³lo se popula despuÃ©s del
  primer heartbeat de `OrbitXSync`. Si el sync estaba apagado o no habÃ­a
  corrido todavÃ­a, el botÃ³n Actualizar siempre tiraba ese error.
  - `CheckAsync` y `DownloadAsync` ahora validan Ãºnicamente
    `ServerUrl + DeviceId`.
  - El header `X-Auth-Token` usa `EffectiveToken(cfg)`, que cae al
    `MasterToken` cuando `DeviceToken` estÃ¡ vacÃ­o (mismo patrÃ³n que ya
    usaban `OrbitXSync.TryAutoRegister` y
    `OrbitXConfigService.TestConnectionAsync`).
  - El servidor reconoce el MasterToken y auto-registra el device en su
    primera llamada, asÃ­ que el flujo completo de check/download de OTA
    funciona desde el primer click, sin pasos manuales.

### Firmware relacionado
- QuantiX `2.1.2` â€” fix de colisiÃ³n de UIDs entre placas (ver
  `Productos/AGP-VR/Software/Firmware_Embebido/Quantix2Motors/CHANGELOG.md`).

---

## [1.2.31] â€” pre-Agro Parallel

Ãšltima versiÃ³n heredada del legacy AgOpenGPS upstream. Se mantiene como
referencia histÃ³rica; el versionado se reinicia en 1.0.0/1.0.1 para
marcar la lÃ­nea PilotX.




