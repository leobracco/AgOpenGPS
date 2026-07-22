# Matriz de preparación Android — PilotX / Agro Parallel

> Última actualización: 2026-07-22 · Bloque **9 CERRADO ~65%** (sesión
> android: toda la lógica pura aislable de FormGPS extraída a Core), bloque
> **6 corregido y avanzando** (estaba mal en 0%: el render GL se resuelve vía
> PilotX.Desktop, ahora ~72% con stages 1→5 hechos — grid/coverage/guías/
> secciones/tram/youturn+recorded paths; carril taller) y bloque **8 al
> ~85%** (sesión android: los 6 puertos serie de CoreX ruteados por
> ISerialPortService, falta solo la prueba con hardware real). Antes:
> bloques 4/5/11 completos (Draw GL a DrawLib, Core multi-target
> net48+netstandard2.0, DataRootOverride), barras del cockpit a Avalonia
> (carril taller), migración HTML, extracción CoreX.

---

## Resumen ejecutivo

| Métrica | Valor |
|---|---|
| Archivos .cs netstandard2.0 (portables sin tocar) | **282** (AgroParallel.*) |
| Archivos .cs AgOpenGPS.Core (netstandard2.0 salvo DrawLib/Visuals/Drawing) | **240** |
| Archivos .cs GPS/ (WinForms, requiere rewrite) | **218** |
| Archivos .cs AgIO/CoreX (WinForms, requiere extracción) | **73** |
| Páginas HTML del Hub (portables) | **68** |
| Archivos JS del Hub (portables) | **70** |
| Forms WinForms nativos activos | **~46** (de los cuales ~30 ya migrados a HTML) |
| Partials FormGPS.* adapter (puente portable↔nativo) | **11** |

**Portabilidad estimada: ~65% del código fuente ya corre en Android sin cambios**
(AgroParallel.* + Core netstandard + AgLibrary + Hub HTML/JS; queda render GL,
FormGPS/forms nativos y el runtime de CoreX).

---

## Tabla de bloques — ordenados por importancia

| # | Bloque | % Hecho | Dificultad | Archivos a modificar | Descripción |
|---|---|---|---|---|---|
| **1** | **Servicios AgroParallel (netstandard2.0)** | **100%** | — | 0 | Models, Services, WebHost, WebUI — ya portables. 282 archivos, 67 tests, snake_case, AgpJson/AgpControllerBase. EmbedIO corre en Android. |
| **2** | **UI web del Hub (HTML/JS/CSS)** | **100%** | — | 0 | 68 páginas + 70 JS + theme.css + keyboard.css. Funciona en cualquier WebView. Teclado virtual propio (no depende del OS). |
| **3** | **MQTT/OTA/Sync (infraestructura LAN+cloud)** | **100%** | — | 0 | MQTTnet (netstandard), FirmwareLanServer, OrbitXSync, OTA SHA-256, AgpEnvelope. Todo corre sin tocar. |
| **4** | **Lógica de guiado pura (clases C#)** | **100%** | — | 0 | Completado 2026-07-19: el desacople de FormGPS ya estaba hecho (`mf` tipado como interfaces `I*Host`, FormGPS las implementa vía partials adapter) y ahora **ninguna clase de Classes/ usa OpenTK**: todo el Draw GL embebido (CYouTurn, CTram, CRecordedPath, CContour, CFence/CBoundary, CTool, CVehicle, CABCurve y CABLine —esta última dibujaba vía wrappers GLW—) se movió a DrawLib/GuidanceDrawExtensions.cs como métodos de extensión. Las clases de guiado son matemática pura portable; el render queda concentrado en DrawLib/Visuals (bloque 6). |
| **5** | **AgOpenGPS.Core → netstandard** | **100%** | — | 0 | Completado 2026-07-19: **AgOpenGPS.Core y AgLibrary multi-targetean `net48;netstandard2.0`**. El target netstandard compila toda la parte portable (Classes, Models, Interfaces, Presenters, ViewModels, Streamers, IO, Protocols, Settings) excluyendo la capa de render (`DrawLib/`, `Visuals/`, `Drawing/` — net48-only con OpenTK, se va con el bloque 6). Color/Point/Size vía System.Drawing.Primitives. Las interfaces I*Host no exponen tipos DrawLib (assets en ITextFontHost/IToolTexturesHost/IVehicleTexturesHost). Consumidores actuales (GPS, AgIO, WpfViews, tests) siguen en net48 sin cambios; Android referencia el target netstandard2.0 directo. |
| **6** | **Render del mapa (OpenGL → GL ES)** | **~90%** | **Muy alta** | SourceCode/PilotX.Desktop/Views/MapGlSurface.cs | **NO se porta el GL inmediato de OpenGL.Designer.cs** — se reemplaza por el render Avalonia de **PilotX.Desktop** (`OpenGlControlBase` + Silk.NET, strangler-fig por polling REST a FormGPS). **Stages 1→7 HECHOS** (2026-07-22): grid, coverage triangulado, guías AB/curva/contorno, secciones/implemento, tramlines, youturn + recorded paths, **cámara (zoom/pan/reset, Stage 6)**, **y GL por default reemplazando MapSkiaSurface (Stage 7)**. Carril taller. Queda: validación visual/uso real en cabina y limpieza final del toggle `--gl` legacy. |
| **7** | **Shell Android (WebView host)** | **60%** | Baja | SourceCode/PilotX.Android/ | **Compila y empaqueta APK (2026-07-19)**: MainActivity + WebView a 127.0.0.1:5180, HubForegroundService con broker MQTTnet embebido + AgpWebHost (servicios netstandard reales; stubs Fase 1 para lo FormGPS-backed), wwwroot como assets (560 entradas, APK 32 MB), DataRootOverride a getExternalFilesDir. Hecho también `AgpPaths.ConfigRoot` (61 usos de BaseDirectory en Services redirigidos; en Windows default idéntico, en Android FilesDir — guardar configs ya funciona). Falta: probar en tablet real y puente JS nativo (close-hub). |
| **8** | **CoreX extracción a servicios** | **~85%** | Alta | ~30 archivos en SourceCode/AgIO/ | Broker MQTT: extraído Y wired (MqttBrokerService). Bridge UDP: extraído Y wired (UdpBridgeService — UDP.designer.cs es wrapper delgado, sockets loopback :17777↔:15555 + LAN :9999↔:8888 en netstandard). NTRIP: extraído Y wired (NtripClientService — NTRIPComm.Designer.cs es wrapper; el form conserva solo UI/metering + radio/serial-pass). Framing PGN serie: extraído a PgnFrameParser portable (estaba triplicado en SerialComm; 7 tests). **Serial: hecho 2026-07-22** — los 6 puertos de `SerialComm.Designer.cs` (spGPS/spGPS2/spRtcm/spIMU/spSteerModule/spMachineModule) ruteados por `ISerialPortService` (ampliada con DtrEnable/RtsEnable/WriteTimeout/DiscardBuffers); `WindowsSerialPortService` sigue siendo la impl real en Windows. Verificado sin hardware (apertura fallida, estado por API, 141 tests verdes) — falta la prueba con hardware real conectado (reset DTR/RTS de Arduino, framing con bytes reales). Queda solo: Serial → USB-OTG (`UsbSerialForAndroid`) en Android, cuando haya hardware. |
| **9** | **FormGPS → desacoplamiento** | **~65% (CERRADO)** | Alta | GPS/Forms/ (rama android) | Cerrado 2026-07-21 (sesión android, rama codex/android-formgps-render): toda la lógica pura aislable extraída a Core con I*Host + partials — CPositionUpdater, CSectionCalculator, CSettingsSender (PGN 252/251/238/236/235), CYouTurnUpdater, CHeadingUpdater (Fix/VTG/Dual), Mat4Math; verificado en runtime (AB + secciones + autosteer en sim). Position.designer.cs de ~1600→~250 ln. **Lo que queda en FormGPS.cs (~4400 ln) y *.Designer.cs es intrínsecamente WinForms (UI/GL entremezclada)** — no se aísla más, se reemplaza entero con el shell Avalonia (bloque 6/PilotX.Desktop). |
| **10** | **Migración Forms → HTML (widgets)** | **85%** | Baja-Media | ~10 forms restantes | 30+ forms ya migrados a HTML. Quedan nativos por decisión: FormSteerWiz (1321 ln), FormSteer (1268), FormBndTool (1095), FormGrid (173), FormColorPicker (166). Estos quedan nativos en Windows; en Android se reescriben en Avalonia o se migran a HTML después. |
| **11** | **Almacenamiento / paths** | **100%** | — | 0 | Completado 2026-07-19: `RegistrySettings.DataRootOverride` — en Android se setea a `Context.getExternalFilesDir()` antes de `Load()` y Fields/Vehicles/Logs cuelgan de ahí (en Windows null → MyDocuments, histórico). `AppBasePath` ya cubría el JSON de settings; Registry legacy detrás de `#if NETFRAMEWORK`. SaveOpen/streamers ya van todos por `fieldsDirectory`. Los `Application.StartupPath` restantes son del shell WinForms (lanzar exes, assets), que en Android se reemplaza entero (bloque 7). |
| **12** | **Self-update** | **0%** | Media | ~3 archivos nuevos | Reemplazar Updater.exe + ZIP por APK servido por OrbitX `/api/ota`. Usar `REQUEST_INSTALL_PACKAGES` o MDM. No bloquea la Fase 1. |
| **13** | **Cámaras RTSP** | **80%** | Baja | 1 archivo (reproductor) | Hikvision ISAPI probe es HTTP puro (portable). Cambiar MediaMTX+web player por ExoPlayer/LibVLC nativo Android. |
| **14** | **Proceso único (merge PilotX+CoreX)** | **~60%** | Media | ~10 archivos de bootstrapping | En Android no hay 2 .exe. Todo dentro de 1 app: Activity (UI) + Foreground Service (broker + bridge + guiado). Primer paso (2026-07-22): `SourceCode/PilotX.GuidanceEngine` (net9.0 puro, sin WinForms/GL) — `GuidanceEngineHost` implementa las ~19 interfaces I*Host que hoy implementa FormGPS, orquestando los mismos CPositionUpdater/CHeadingUpdater/CAutoSteerUpdater/CYouTurnUpdater/CSectionCalculator/CSettingsSender del bloque 9. Segundo paso (2026-07-22): `CoreXEngineHost` + `Net9SerialPortService` — arranca MqttBrokerService/UdpBridgeService/NtripClientService/los 6 puertos serie en el mismo proceso que `GuidanceEngineHost` (flag `--corex`), mismo protocolo loopback (CoreX escucha :17777, contesta :15555). Tercer paso (2026-07-22): `GuidanceEngineHost.ExecuteCommand(string)` — mismo vocabulario que `FormGPS.ExecuteGuidanceCommand`/`IGuidanceCalculator.ExecuteCommand` ("autosteer" hoy), servido primero por un TCP de prueba en :15556. **Cuarto paso hecho 2026-07-22**: ese comando ahora se sirve también por el canal real del ecosistema — `CoreXEngineHost.SubscribeCommands()` (cliente MQTT sobre el mismo broker que ya arranca `--corex`, mismo patrón que `FirmwareOtaClient`/`NodoRegistryService` de AgroParallel.Services) suscripto al tópico `agp/aog/guidance/command` (mismo nombre que el REST `/api/aog/guidance/command` del Hub, pero en MQTT). Verificado en runtime con `--sim --corex`: publiqué "autosteer"/"autosteer"/"bogus_cmd" con un publisher MQTT de prueba → `ok`/`ok`/`unknown` en el log, sin excepciones. Compila limpio (0 warnings), 141 tests verdes. Falta: consumidor real del otro lado (Android UI o Hub) que publique en ese tópico, ampliar vocabulario (job start/stop, youturn) cuando exista ese consumidor, y probar NTRIP/serial con credenciales/hardware real. |

---

## Ruta crítica (qué hacer primero, en orden)

### Fase 0 — Preparación en Windows (ya empezada)
| Tarea | Estado | Bloquea |
|---|---|---|
| Migrar forms a HTML | ✅ 85% | Fase 1 |
| AgOpenGPS.Core → netstandard2.0 (multi-target) | ✅ hecho 2026-07-19 | Fase 1 |
| Extraer servicios CoreX a netstandard | ✅ broker/UDP/NTRIP/PGN (serial diferido) | Fase 1 (broker) |
| Congelar contratos REST/WS/MQTT | ✅ hecho (AgpJson, snake_case, AgpEnvelope) | Fase 1 |

### Fase 1 — Hub Android (sin guiado) — **valor inmediato**
| Tarea | Dificultad | Bloques |
|---|---|---|
| App Android: Activity + WebView → 127.0.0.1:5180 | Baja | 7 |
| Foreground Service con WebHost + broker MQTTnet | Media | 7, 8 |
| Puente JS (addJavascriptInterface ↔ postMessage) | Baja | 7 |
| Paths → scoped storage | Baja | 11 |

**Resultado**: tablet Android como monitor de siembra (VistaX/QuantiX/FlowX), config de nodos, OTA, sync cloud. Sin guiado pero con el 100% del Hub HTML.

### Fase 2 — Guiado Android (el trabajo grande)
| Tarea | Dificultad | Bloques |
|---|---|---|
| Adoptar/derivar AgValoniaGPS para render | Muy alta | 6 |
| Extraer lógica guiado de FormGPS a servicios | Muy alta | 4, 9 |
| CoreX bridge interno (UDP + Serial USB-OTG) | Alta | 8 |

### Fase 3 — Paridad completa
Self-update APK, cámaras nativo, kiosk mode, arranque automático.

---

## Archivos clave a modificar (top 20 por impacto)

| Archivo | Líneas | Qué hacer | Impacto |
|---|---|---|---|
| `GPS/Forms/FormGPS.cs` | ~4000 | Extraer lógica a servicios | Crítico |
| `GPS/Forms/Position.designer.cs` | ~250 (era ~1600) | GPS loop → servicio — **hecho 2026-07-20**, solo queda wrap-up GL | Bajo |
| `GPS/Forms/OpenGL.Designer.cs` | ~3000 | Render → GL ES/Skia | Crítico |
| `GPS/Forms/Controls.Designer.cs` | ~2100 | Ya 85% migrado a HTML | Medio |
| `AgIO/Source/Forms/FormLoop.cs` | ~500 | Broker+bridge → servicio | Alto |
| `AgIO/Source/Forms/MQTT.Designer.cs` | ~300 | Broker → servicio | Alto |
| `AgOpenGPS.Core/Classes/CGLM.cs` | ~200 | Quitar System.Drawing.Color | Medio |
| `AgOpenGPS.Core/Classes/CTool.cs` | ~400 | Quitar System.Drawing | Medio |
| `AgOpenGPS.Core/Classes/CTram.cs` | ~300 | Quitar System.Drawing | Medio |
| `AgOpenGPS.Core/Properties/Settings.cs` | ~100 | Quitar Settings.Default | Medio |
| `AgroParallel.Shell/AgpWebHostBootstrap.cs` | ~200 | Android: init sin WinForms | Medio |
| `GPS/Forms/GUI.Designer.cs` | ~600 | LoadSettings → servicio config | Medio |

---

## Conclusión

**El 55% del código ya es portable.** El bloque más caro es el render OpenGL (0% hecho, dificultad muy alta) — pero AgValoniaGPS lo resuelve. La Fase 1 (Hub Android sin guiado) es alcanzable con trabajo de dificultad baja-media porque solo necesita: shell Android con WebView + Foreground Service + limpiar las 12 dependencias System.Drawing del Core. Todo el Hub HTML (68 páginas), los servicios (282 archivos netstandard) y la infraestructura MQTT/OTA/sync ya corren sin cambios.
