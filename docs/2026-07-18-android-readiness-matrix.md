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
| **6** | **Render del mapa (OpenGL → GL ES)** | **~95% (ANDA)** | **Muy alta** | SourceCode/PilotX.Desktop/Views/MapGlSurface.cs | Render Avalonia de **PilotX.Desktop** (`OpenGlControlBase` + Silk.NET, strangler-fig por polling REST). **Stages 1→7 HECHOS y VALIDADOS visualmente 2026-07-22**: grid, coverage, guías, secciones, tram, youturn+recorded (Stage 5), **cámara zoom/pan/reset (Stage 6)**, **GL por default `UseGl=true` (Stage 7)**. Tres bugs encontrados y arreglados en la validación: (1) **casing** — los clients esperaban camelCase y el server manda snake_case (`JsonNamingPolicy.SnakeCaseLower`); (2) **shader** — Avalonia en Windows da un contexto **OpenGL ES (ANGLE)** y los shaders `#version 330 core` no compilaban → mapa negro; fix = `#version 300 es`+precision según `GlVersion.Type`; (3) **cobertura** — el header (contador+color) de cada patch se dibujaba como coordenada → diagonal al togglear secciones; fix = geometría desde `[1]`. Cámara: input en MapPanel (OpenGlControlBase no recibía el wheel). Cobertura a ~3Hz. Verificado en UHD 630: mapa con lindero + cobertura + secciones + tractor + zoom/pan, huella siguiendo la trayectoria. **FALTA solo Stage 7 final**: retirar `MapSkiaSurface` legacy cuando se confirme en cabina. |
| **7** | **Shell Android (WebView host)** | **80%** | Baja | SourceCode/PilotX.Android/ | **Compila y empaqueta APK (2026-07-19)**: MainActivity + WebView a 127.0.0.1:5180, HubForegroundService con broker MQTTnet embebido + AgpWebHost (servicios netstandard reales; stubs Fase 1 para lo FormGPS-backed), wwwroot como assets (560 entradas, APK 32 MB), DataRootOverride a getExternalFilesDir. Hecho también `AgpPaths.ConfigRoot` (61 usos de BaseDirectory en Services redirigidos; en Windows default idéntico, en Android FilesDir — guardar configs ya funciona). **2026-07-22 (sesión android, con autorización directa del usuario — cruza a carril taller)**: de los ~9 stubs FormGPS-backed originales, **7 ya se reemplazaron por implementaciones reales** respaldadas por `GuidanceEngineHost` (bloque 14): `StubLotesService`, `StubGuidanceCalculator` (`GuidanceEngineServices.cs`) + `StubAogStateProvider`, `StubSectionControlService`, `StubVehicleToolService`, `StubCoverageService`, `StubQuantiXRuntimeService` (`GuidanceEngineStateServices.cs`). Ver detalle completo en fila bloque 14. Solo quedan 3 stubs, y los 3 son subsistemas distintos a propósito (no es que falte portar, es que no son datos de guiado): `StubShapefileService` (parseo de shapefile, capa que el motor no carga), `StubPilotXUpdateService` (self-update, bloque 12, feature aparte), `StubSistemaService` (brillo/apagado — APIs de Android, no del motor). Falta: probar en tablet real, puente JS nativo (close-hub). |
| **8** | **CoreX extracción a servicios** | **~85%** | Alta | ~30 archivos en SourceCode/AgIO/ | Broker MQTT: extraído Y wired (MqttBrokerService). Bridge UDP: extraído Y wired (UdpBridgeService — UDP.designer.cs es wrapper delgado, sockets loopback :17777↔:15555 + LAN :9999↔:8888 en netstandard). NTRIP: extraído Y wired (NtripClientService — NTRIPComm.Designer.cs es wrapper; el form conserva solo UI/metering + radio/serial-pass). Framing PGN serie: extraído a PgnFrameParser portable (estaba triplicado en SerialComm; 7 tests). **Serial: hecho 2026-07-22** — los 6 puertos de `SerialComm.Designer.cs` (spGPS/spGPS2/spRtcm/spIMU/spSteerModule/spMachineModule) ruteados por `ISerialPortService` (ampliada con DtrEnable/RtsEnable/WriteTimeout/DiscardBuffers); `WindowsSerialPortService` sigue siendo la impl real en Windows. Verificado sin hardware (apertura fallida, estado por API, 141 tests verdes) — falta la prueba con hardware real conectado (reset DTR/RTS de Arduino, framing con bytes reales). Queda solo: Serial → USB-OTG (`UsbSerialForAndroid`) en Android, cuando haya hardware. |
| **9** | **FormGPS → desacoplamiento** | **~65% (CERRADO)** | Alta | GPS/Forms/ (rama android) | Cerrado 2026-07-21 (sesión android, rama codex/android-formgps-render): toda la lógica pura aislable extraída a Core con I*Host + partials — CPositionUpdater, CSectionCalculator, CSettingsSender (PGN 252/251/238/236/235), CYouTurnUpdater, CHeadingUpdater (Fix/VTG/Dual), Mat4Math; verificado en runtime (AB + secciones + autosteer en sim). Position.designer.cs de ~1600→~250 ln. **Lo que queda en FormGPS.cs (~4400 ln) y *.Designer.cs es intrínsecamente WinForms (UI/GL entremezclada)** — no se aísla más, se reemplaza entero con el shell Avalonia (bloque 6/PilotX.Desktop). |
| **10** | **Migración Forms → HTML (widgets)** | **85%** | Baja-Media | ~10 forms restantes | 30+ forms ya migrados a HTML. Quedan nativos por decisión: FormSteerWiz (1321 ln), FormSteer (1268), FormBndTool (1095), FormGrid (173), FormColorPicker (166). Estos quedan nativos en Windows; en Android se reescriben en Avalonia o se migran a HTML después. |
| **11** | **Almacenamiento / paths** | **100%** | — | 0 | Completado 2026-07-19: `RegistrySettings.DataRootOverride` — en Android se setea a `Context.getExternalFilesDir()` antes de `Load()` y Fields/Vehicles/Logs cuelgan de ahí (en Windows null → MyDocuments, histórico). `AppBasePath` ya cubría el JSON de settings; Registry legacy detrás de `#if NETFRAMEWORK`. SaveOpen/streamers ya van todos por `fieldsDirectory`. Los `Application.StartupPath` restantes son del shell WinForms (lanzar exes, assets), que en Android se reemplaza entero (bloque 7). |
| **12** | **Self-update** | **0%** | Media | ~3 archivos nuevos | Reemplazar Updater.exe + ZIP por APK servido por OrbitX `/api/ota`. Usar `REQUEST_INSTALL_PACKAGES` o MDM. No bloquea la Fase 1. |
| **13** | **Cámaras RTSP** | **80%** | Baja | 1 archivo (reproductor) | Hikvision ISAPI probe es HTTP puro (portable). Cambiar MediaMTX+web player por ExoPlayer/LibVLC nativo Android. |
| **14** | **Proceso único (merge PilotX+CoreX)** | **~93%** | Media | ~10 archivos de bootstrapping | En Android no hay 2 .exe. Todo dentro de 1 app: Activity (UI) + Foreground Service (broker + bridge + guiado). Primer paso: `SourceCode/PilotX.GuidanceEngine` (net9.0 puro, sin WinForms/GL) — `GuidanceEngineHost` implementa las ~19 interfaces I*Host que hoy implementa FormGPS, orquestando CPositionUpdater/CHeadingUpdater/CAutoSteerUpdater/CYouTurnUpdater/CSectionCalculator/CSettingsSender del bloque 9. Segundo paso: `CoreXEngineHost` + `Net9SerialPortService` (flag `--corex`). Tercer paso: `GuidanceEngineHost.ExecuteCommand(string)` — mismo vocabulario que `FormGPS.ExecuteGuidanceCommand`/`IGuidanceCalculator.ExecuteCommand`, primero por TCP de prueba en :15556. Cuarto paso: ese comando también por MQTT real (tópico `agp/aog/guidance/command`, `CoreXEngineHost.SubscribeCommands()`, mismo patrón que `FirmwareOtaClient`/`NodoRegistryService`). Quinto paso: `GuidanceEngineHost.OpenField(string)`/`CloseField()` (nuevo `GuidanceEngineHost.Job.cs`) — mismo flujo de datos que `FormGPS.FileOpenField`/`JobNew` sobre `RegistrySettings.fieldsDirectory`. **Sexto paso hecho 2026-07-22**: 2 comandos más del mismo vocabulario real de FormGPS — `"uturn"` (`ToggleYouTurn()`) y `"pick"` (`SelectTrack()`, mismo nombre que `IGuidanceCalculator.ExecuteCommand("pick")`) — necesario porque sin "pick" `Trk.idx` se queda en -1 después de abrir un lote. Verificado en runtime con `--sim` contra el lote real `Lote 1`: las 4 ramas de guarda/toggle de `btnAutoYouTurn_Click` confirmadas una por una. **Séptimo paso — foco 3, integración real a PilotX.Android**: `GuidanceEngineHost` **dentro de la app Android** (no solo el .exe de consola). Requirió separar el proyecto: `System.IO.Ports` (que necesita `CoreXEngineHost`/`Net9SerialPortService`) no restaura para `net9.0-android` (`NETSDK1047`) — se creó **`PilotX.GuidanceEngine.Core`** (solo `GuidanceEngineHost*.cs`, sin System.IO.Ports ni AgroParallel.Services), del que `PilotX.Android` toma un `ProjectReference` directo; `PilotX.GuidanceEngine` (consola) referencia a `PilotX.GuidanceEngine.Core` en vez de duplicar archivos. En `HubBootstrap.cs`: se instancia un `GuidanceEngineHost` y se reemplazan 2 stubs por `GuidanceEngineLotesService`/`GuidanceEngineGuidanceCalculator`. **Octavo paso — resto de los stubs "de datos de guiado"**: `GuidanceEngineStateServices.cs` con 5 implementaciones más — `StubAogStateProvider`→`GuidanceEngineStateProvider` (`GetSnapshot()` completo: posición/velocidad/área/autosteer/secciones/track/youturn/contour/boundary/headland/hidráulico/tram + `GetEventLog()`; el resto de esa interfaz —all-settings, 4 gráficos en vivo, shift-pos, sim-coords, colores, shapefile— queda con el default vacío del stub, documentado como pendiente), `StubSectionControlService`, `StubCoverageService`, `StubVehicleToolService` (se sacó `readonly` de `GuidanceEngineHost.Vehicle`/`Tool` para recargarlos en caliente al guardar), `StubQuantiXRuntimeService` (reusa el `GuidanceEngineStateProvider`). De los 10 stubs originales de `Fase1Stubs.cs` quedan 3, a propósito (no son datos de guiado): `StubShapefileService`, `StubPilotXUpdateService` (bloque 12), `StubSistemaService` (APIs Android). **Noveno paso — verificado en runtime real, no solo compila**: AVD ya configurado en esta PC (`Medium_Phone_API_36.1`) — `dotnet build PilotX.Android.csproj -p:AndroidPackageFormat=apk`, `adb install`, app lanzada de verdad. `HubBootstrap.Start()` sin ninguna excepción; `curl` vía `adb forward` contra el Hub dentro del emulador: `/api/aog/state` con datos reales, `/api/lotes` → `[]` sin crash, **`POST /api/aog/guidance/command {"cmd":"autosteer"}` → `{"ok":true}` con `is_auto_steer_on:true` confirmado en el snapshot siguiente** — roundtrip HTTP→`ExecuteCommand`→estado→snapshot corriendo de verdad en Android. **Décimo paso hecho 2026-07-22 (sesión taller, en paralelo)**: `EngineWebHost.cs` + 6 adapters net9 (`PilotX.GuidanceEngine/Adapters/`: EngineStateProvider/Coverage/ToolGeometry/Tram/Paths/Guidance) implementando las mismas interfaces que consume `AgpWebHost`, leyendo el modelo de `GuidanceEngineHost` (gemelos headless de los FormGps*Calculator). Flag `--webhost` levanta `AgpWebHost` sirviendo /api/aog/{state,coverage,tool,tram,paths,guidance} en :5180 — la MISMA API que FormGPS. Con esto **PilotX.Desktop renderiza el mapa contra el engine headless, sin FormGPS**. **BUGFIX real e importante encontrado en el camino**: `GuidanceEngineHost.Start()` no llamaba `PgnReceiverField.StartWatch()` (FormGPS sí, FormGPS.cs:849) → `udpWatch` parado → el gate `< UdpWatchLimit(70ms)` del `case 0xD6` descartaba TODOS los fixes de GPS antes de arrancar el watch → deadlock: **el engine nunca procesaba posición real** (el modo `--sim` no pasa por ese gate y por eso todas las verificaciones con `--sim` de esta matriz siguieron siendo válidas, pero el camino con PGN real de CoreX/ModSim estaba roto hasta ahora). Verificado end-to-end con ModSim real → CoreX → engine `--webhost`: `fix_quality` 0→8, posición real llegando, PilotX.Desktop dibuja el triángulo en la posición real. **Con este fix, bloque 14 tiene GPS real end-to-end del lado Windows/PilotX.Desktop** (Android todavía sin fix real: falta CoreX/serial por USB-OTG, bloque 8, hardware pendiente — el fix de `StartWatch` ya está incorporado también del lado Android vía el merge). Falta: consumidor de comandos real del lado Hub/Desktop (autosteer/job/youturn disparados desde la UI), embeber `PilotX.Cockpit.Bars` en `PilotX.Desktop`, probar en tablet física real, y NTRIP/serial con hardware real. |

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
