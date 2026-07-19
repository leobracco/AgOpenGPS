# Matriz de preparación Android — PilotX / Agro Parallel

> Última actualización: 2026-07-19 · 15 commits de portabilidad en esta sesión:
> migración HTML (7 forms), limpieza Core (PresentationCore eliminada, Registry #if),
> extracción CoreX (MqttBrokerService, UdpBridgeService, NtripClientService,
> ISerialPortService + WindowsSerialPortService), structs portables AgpPoint/AgpSize.

---

## Resumen ejecutivo

| Métrica | Valor |
|---|---|
| Archivos .cs netstandard2.0 (portables sin tocar) | **282** (AgroParallel.*) |
| Archivos .cs AgOpenGPS.Core (parcialmente portable) | **240** (12 con System.Drawing) |
| Archivos .cs GPS/ (WinForms, requiere rewrite) | **218** |
| Archivos .cs AgIO/CoreX (WinForms, requiere extracción) | **73** |
| Páginas HTML del Hub (portables) | **68** |
| Archivos JS del Hub (portables) | **70** |
| Forms WinForms nativos activos | **~46** (de los cuales ~30 ya migrados a HTML) |
| Partials FormGPS.* adapter (puente portable↔nativo) | **11** |

**Portabilidad estimada: ~55% del código fuente ya corre en Android sin cambios.**

---

## Tabla de bloques — ordenados por importancia

| # | Bloque | % Hecho | Dificultad | Archivos a modificar | Descripción |
|---|---|---|---|---|---|
| **1** | **Servicios AgroParallel (netstandard2.0)** | **100%** | — | 0 | Models, Services, WebHost, WebUI — ya portables. 282 archivos, 67 tests, snake_case, AgpJson/AgpControllerBase. EmbedIO corre en Android. |
| **2** | **UI web del Hub (HTML/JS/CSS)** | **100%** | — | 0 | 68 páginas + 70 JS + theme.css + keyboard.css. Funciona en cualquier WebView. Teclado virtual propio (no depende del OS). |
| **3** | **MQTT/OTA/Sync (infraestructura LAN+cloud)** | **100%** | — | 0 | MQTTnet (netstandard), FirmwareLanServer, OrbitXSync, OTA SHA-256, AgpEnvelope. Todo corre sin tocar. |
| **4** | **Lógica de guiado pura (clases C#)** | **40%** | Alta | ~60 archivos en AgOpenGPS.Core + GPS/Classes/ | CTrack, CABLine, CABCurve, CYouTurn, CBoundary, CTool, etc. El código es C# puro pero está **acoplado a FormGPS** vía `mf.` (1500+ referencias). Necesita: (a) extraer a interfaces/servicios, (b) eliminar dependencias a System.Drawing de los 12 archivos en Core. |
| **5** | **AgOpenGPS.Core → netstandard** | **70%** | Media | 12 archivos .cs | 12 archivos usan `System.Drawing.Color` o `Properties.Settings`. Reemplazar: `Color` → `ColorRgba` (ya existe), `Settings` → config propia. El resto (228 archivos) ya es portable. |
| **6** | **Render del mapa (OpenGL → GL ES/Skia)** | **0%** | **Muy alta** | ~30 archivos (OpenGL.Designer.cs, Visuals/, DrawLib/) | GL inmediato (glBegin/glEnd/glVertex) → shaders + VBO para GL ES, o migrar a Skia/SkiaSharp. Es **el bloque más caro**. AgValoniaGPS (upstream) ya lo resuelve con Avalonia `OpenGlControlBase`. |
| **7** | **Shell Android (WebView host)** | **0%** | Baja | 3 archivos nuevos (Activity + Service + Manifest) | Reemplazar AgroParallel.Shell (net48/WebView2) por una Activity con Android WebView apuntando a 127.0.0.1:5180. `addJavascriptInterface` para el puente `close-hub`/postMessage. **Fase 1 del port.** |
| **8** | **CoreX extracción a servicios** | **20%** | Alta | ~40 archivos en SourceCode/AgIO/ | Bridge UDP↔Serial↔MQTT + broker embebido + NTRIP. Hoy todo vive en Forms de AgIO. Plan de extracción existe (`docs/superpowers/plans/2026-07-06`), no ejecutado. Serial → USB-OTG (`UsbSerialForAndroid`). Broker MQTTnet → Foreground Service. |
| **9** | **FormGPS → desacoplamiento** | **15%** | **Muy alta** | ~124 archivos en GPS/Forms/ | FormGPS.cs es el monolito central (~4000 líneas) + 46 forms + partials. Los 11 partials AgroParallel son el puente ya hecho. Falta: extraer la lógica de timer1 (GPS loop), secciones, PGN, simulation, de los Forms a clases servicio. |
| **10** | **Migración Forms → HTML (widgets)** | **85%** | Baja-Media | ~10 forms restantes | 30+ forms ya migrados a HTML. Quedan nativos por decisión: FormSteerWiz (1321 ln), FormSteer (1268), FormBndTool (1095), FormGrid (173), FormColorPicker (166). Estos quedan nativos en Windows; en Android se reescriben en Avalonia o se migran a HTML después. |
| **11** | **Almacenamiento / paths** | **90%** | Baja | ~5 archivos (RegistrySettings, SaveOpen.Designer) | `Fields/`, configs XML, `.rec` → cambiar `Application.StartupPath` y `RegistrySettings.fieldsDirectory` a `Context.getExternalFilesDir()`. Los archivos son planos (txt/xml/json), no hay nada Windows-específico en el formato. |
| **12** | **Self-update** | **0%** | Media | ~3 archivos nuevos | Reemplazar Updater.exe + ZIP por APK servido por OrbitX `/api/ota`. Usar `REQUEST_INSTALL_PACKAGES` o MDM. No bloquea la Fase 1. |
| **13** | **Cámaras RTSP** | **80%** | Baja | 1 archivo (reproductor) | Hikvision ISAPI probe es HTTP puro (portable). Cambiar MediaMTX+web player por ExoPlayer/LibVLC nativo Android. |
| **14** | **Proceso único (merge PilotX+CoreX)** | **0%** | Media | ~10 archivos de bootstrapping | En Android no hay 2 .exe. Todo dentro de 1 app: Activity (UI) + Foreground Service (broker + bridge + guiado). Depende de bloques 8 y 9. |

---

## Ruta crítica (qué hacer primero, en orden)

### Fase 0 — Preparación en Windows (ya empezada)
| Tarea | Estado | Bloquea |
|---|---|---|
| Migrar forms a HTML | ✅ 85% | Fase 1 |
| AgOpenGPS.Core: eliminar System.Drawing de 12 archivos | ❌ pendiente | Fase 1 |
| Extraer servicios CoreX a netstandard | ❌ pendiente | Fase 1 (broker) |
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
| `GPS/Forms/Position.designer.cs` | ~1600 | GPS loop → servicio | Crítico |
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
