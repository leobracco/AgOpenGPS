# Análisis · Traspaso de PilotX / Agro Parallel a Android

> Fecha: 2026-07-08 · Autor: análisis técnico sobre el repo `AgOpenGPS` (PilotX)
> Alcance: qué está hecho hoy, qué falta, y cómo debería encararse el portado
> del sistema completo (guiado + productos X-*) para correr en una tablet Android.

---

## 1. Resumen ejecutivo

El sistema hoy es una app **Windows** (WinForms .NET 4.8 + OpenGL de escritorio)
con un ecosistema de servicios .NET alrededor. **No existe un camino directo**
WinForms→Android: el portado exige migrar la capa de UI y render a un stack
multiplataforma (**Avalonia + .NET 8/10** es el camino recomendado, y ya existe
el upstream **AgValoniaGPS** que hace exactamente eso).

La buena noticia: **la mitad "Agro Parallel" del sistema ya es portable**.
`AgroParallel.Services`, `AgroParallel.Models` y `AgroParallel.WebHost` son
**netstandard2.0** (corren en Android sin tocar), la UI del Hub es **HTML/JS**
(se renderiza en cualquier WebView) y los nodos ESP32 hablan **MQTT por LAN**,
que es agnóstico de la plataforma de la PC/tablet.

Lo que ancla a Windows es: FormGPS (WinForms + OpenTK GLControl), AgIO/CoreX
(WinForms + Serial + broker embebido), WebView2, los `.exe` separados que se
invocan entre sí, el instalador Inno Setup y el self-update con `Updater.exe`.

---

## 2. Qué está hecho hoy (inventario y portabilidad)

### 2.1 Núcleo de guiado (PilotX.exe)

| Componente | Estado | Portable a Android |
|---|---|---|
| FormGPS (UI WinForms, paneles, menú flotante, auto-hide) | ✅ funcionando | ❌ WinForms no existe en Android |
| Render del mapa (OpenTK **GLControl**, OpenGL desktop, GL inmediato) | ✅ | ❌ requiere OpenGL **ES** / ANGLE / Skia |
| Lógica de guiado (AB lines, contour, U-turn, secciones, CTrack, CYouTurn…) | ✅ madura (upstream AOG) | ⚠️ portable con refactor: es C# puro pero está **acoplada a FormGPS** (`mf.`) |
| Lotes en `<AOG>/Fields/<Nombre>/` (boundary, sections, recpath) | ✅ | ✅ archivos planos, solo cambia el path base |
| Menú flotante táctil + Material Icons TTF | ✅ (esta rama) | ⚠️ concepto reutilizable, implementación WinForms |

### 2.2 CoreX (ex AgIO)

| Componente | Estado | Portable |
|---|---|---|
| Bridge UDP↔Serial↔MQTT | ✅ | ⚠️ UDP sí; **Serial requiere USB-OTG** en Android |
| **Broker MQTT embebido (MQTTnet, :1883)** | ✅ | ✅ MQTTnet es netstandard; ⚠️ necesita *foreground service* |
| CoreXWebHost (:5181) + FormWebShell (WebView2) | ✅ | ⚠️ host sí (EmbedIO), WebView2 no (usar WebView Android) |
| NTRIP client, PGN routing | ✅ | ✅ sockets puros |

### 2.3 Capa Agro Parallel (lo más avanzado y lo más portable)

| Componente | Estado | Portable |
|---|---|---|
| `AgroParallel.Services` (**netstandard2.0**): VistaX, QuantiX, SectionX, FlowX, StormX, LineX, Cámaras, OTA, OrbitXSync, prescripciones, insumos, nodos, overlay auto-open | ✅ Bloque A completado (AgpLog/AgpJson/AgpControllerBase/MqttLiveServiceBase, 67 tests) | ✅ **directo** |
| `AgroParallel.Models` (DTOs snake_case) | ✅ | ✅ directo |
| `AgroParallel.WebHost` (EmbedIO :5180, controllers REST + WS) | ✅ | ✅ EmbedIO corre en Android (o migrar a Kestrel) |
| `AgroParallel.WebUI` (wwwroot HTML/JS/CSS, teclado virtual propio, paleta PilotX) | ✅ | ✅ **directo** — es web, la renderiza cualquier WebView |
| `AgroParallel.Shell` (Hub WebView2, net48) | ✅ | ❌ reemplazar por WebView nativo Android |
| OTA ESP32: catálogo cloud, SHA-256, FirmwareLanServer, coordinator, anti-downgrade, watchdog | ✅ | ✅ (el HTTP server LAN corre igual) |
| PilotX self-update (ZIP + Updater.exe) | ✅ | ❌ Android usa APK (Play/MDM/sideload) |
| Sync OrbitX (heartbeat, aog/sync, tracking, VistaX NDJSON/SHP) | ✅ | ✅ HTTPS puro |

### 2.4 Fuera de la tablet (no cambia con Android)

- **OrbitX cloud** (Node/Express/CouchDB): sin cambios.
- **Firmwares ESP32** (QuantiX, VistaX, FlowX, StormX…): sin cambios — hablan
  MQTT contra "la IP del broker", les da igual que sea Windows o Android.
- **Cámaras Hikvision**: RTSP en LAN; cambia solo el reproductor del lado tablet.

---

## 3. Qué falta hacer (independiente de Android)

Pendientes ya identificados en el proyecto que conviene cerrar **antes** de
encarar el portado (todo lo que se termine en la capa portable es trabajo que
no se hace dos veces):

1. **Firmware SectionX-like / pendientes de firmware**: rework de búsqueda de
   PWM mínimo en QuantiX (diferido, confirmar metodología primero).
2. **Extracción de servicios CoreX** (plan `docs/superpowers/plans/2026-07-06-corex-services-extraction.md`,
   diferido): sacar la lógica de bridge/broker de las Forms de AgIO hacia
   clases servicio netstandard. **Este pendiente es la piedra angular del
   portado de CoreX** — hacerlo ya con target netstandard2.0.
3. Páginas mockup de CoreX web sin backend + favicon 404 + `wwwroot-corex`
   en el installer.
4. Viewer cloud de mapas VistaX + descarga local de shapefiles + capas extra.
5. Bridges/JS/controllers FlowX·StormX completos (hoy scaffold) + firmware StormX MQTT.
6. Repo limpio `Agro-Parallel/PilotX` (migración diferida).
7. Commits pendientes de esta rama (menú flotante, Material Icons, peek arrows,
   FormConfig directo, sidebar Hub con datos reales).

---

## 4. Estrategia de traspaso a Android

### 4.1 Principio rector

**No portar WinForms: portar la lógica y reescribir la cáscara.**
El sistema ya está (parcialmente) partido en tres capas:

```
[ UI nativa + render mapa ]   ← se REESCRIBE (Avalonia)
[ Lógica de guiado C# ]       ← se EXTRAE de FormGPS (refactor)
[ Servicios AgroParallel ]    ← se REUSA tal cual (netstandard2.0)
[ Web UI del Hub (HTML/JS) ]  ← se REUSA tal cual (WebView)
[ ESP32 + OrbitX cloud ]      ← NO CAMBIA
```

### 4.2 Camino recomendado: Avalonia + .NET 8/10

- **Por qué Avalonia y no MAUI**: ya existe **AgValoniaGPS** (rewrite upstream
  100% Avalonia 12 + .NET 10, sin WinForms, GPL-3.0, estructura
  `Shared/Platforms`) que resuelve el problema más caro: **el render del mapa
  y la UI de guiado multiplataforma**. Sumarse/derivar de ese esfuerzo evita
  reescribir CTrack/CYouTurn/render desde cero. Además nuestro spike
  `PilotX.Desktop` (pausado) ya validó Avalonia/Skia como dirección.
- **Render**: OpenGL inmediato de OpenTK GLControl → `OpenGlControlBase` de
  Avalonia (GL ES en Android) o render Skia. Es el trabajo técnico más grande.
- **Un solo proceso**: en Android no hay `PilotX.exe` + `CoreX.exe` + `Updater.exe`
  que se lanzan entre sí. Todo pasa a ser **una app con servicios internos**:
  - CoreX = servicio interno (el plan de extracción del punto 3.2 lo habilita).
  - Broker MQTT (MQTTnet) = dentro de un **Foreground Service** (notificación
    persistente) para sobrevivir a Doze/battery optimization.
  - Updater = mecanismo APK (ver 4.5).

### 4.3 Matriz componente → acción

| Componente | Acción para Android |
|---|---|
| FormGPS UI | Reescribir en Avalonia (basarse en AgValoniaGPS; UI táctil tipo menú flotante actual) |
| Lógica guiado | Refactor: romper dependencia `mf.` → clases con interfaces (idealmente contribuir/tomar de AgValonia `Shared/`) |
| Render OpenGL | Portar a GL ES vía `OpenGlControlBase` (shaders/VBO en lugar de GL inmediato) |
| AgIO/CoreX bridge | Completar extracción a servicios netstandard; UDP igual; **Serial → `UsbManager` + driver CDC/FTDI** (plugin `UsbSerialForAndroid`) o eliminar serial (módulos por UDP/WiFi, que ya es el caso dominante) |
| Broker MQTTnet | Igual, dentro de Foreground Service; puerto 1883; la tablet pasa a ser la IP que los ESP32 configuran como broker |
| WebHost :5180/:5181 (EmbedIO) | Corre igual (netstandard). Evaluar migración a Kestrel si EmbedIO da problemas en Android |
| Hub (WebView2) | `Android WebView` apuntando a `http://127.0.0.1:5180`. El puente `ShellBridge` (hostObjects/postMessage) se reimplementa con `addJavascriptInterface` — **definir una interfaz JS única** para no bifurcar el wwwroot |
| wwwroot (HTML/JS) | Sin cambios (ya trae teclado virtual propio — clave en Android) |
| OrbitXSync / OTA / FirmwareLanServer | Sin cambios de código; revisar paths (`Path.Combine` sobre `Context.getExternalFilesDir`) |
| Fields/ y configs | Mover raíz de datos a almacenamiento de la app (scoped storage); exponer export por USB/SAF |
| Self-update | APK firmado servido por OrbitX (`/api/ota` ya versiona binarios) + instalación con `REQUEST_INSTALL_PACKAGES`, o distribución MDM/Play privado |
| Cámaras RTSP | Reproductor nativo (LibVLC/ExoPlayer) en vez de MediaMTX+web player |
| Installer Inno Setup | Muere; lo reemplaza el empaquetado APK/AAB |

### 4.4 Cosas específicas de Android a resolver

1. **GPS/NTRIP**: hoy entra por serial/UDP desde módulos. En tablet puede
   seguir igual (receptor por WiFi/UDP) — **no usar el GPS interno de la
   tablet** para guiado (precisión insuficiente); solo como fallback de UI.
2. **Red**: la tablet debe estar en la LAN del tractor (AP del router del
   tractor o hotspot de la propia tablet). Documentar que si la tablet es el
   hotspot, su IP es fija (192.168.43.1) y simplifica el broker.
3. **Energía**: pantalla siempre encendida (`FLAG_KEEP_SCREEN_ON`), app
   exenta de battery optimization, todo lo crítico en Foreground Service.
4. **Ciclo de vida**: Activity puede morir/rotar; la lógica de guiado y el
   broker viven en el Service, la UI se re-adjunta (otra razón para separar
   lógica de UI).
5. **Permisos**: red local sin permiso especial; USB-OTG pide permiso por
   dispositivo; instalación de APK pide permiso una vez.

### 4.5 Fases propuestas

- **Fase 0 — Preparación (en Windows, ya empezada)**
  Cerrar extracción de servicios CoreX a netstandard; mantener TODO código
  nuevo de lógica en `AgroParallel.Services`/netstandard (regla ya vigente);
  congelar contratos REST/WS/MQTT (ya snake_case + AgpEnvelope).
- **Fase 1 — Hub Android (bajo riesgo, valor inmediato)**
  App Android mínima: Foreground Service con WebHost + broker MQTT + servicios
  AgroParallel + WebView del Hub. **Sin guiado.** Sirve como monitor de
  siembra/nodos (VistaX/QuantiX/FlowX) en tablet. Valida broker, OTA, sync y
  WebView con hardware real.
- **Fase 2 — Guiado (el trabajo grande)**
  Adoptar/derivar AgValoniaGPS para la pantalla de guiado en Avalonia-Android;
  portar los módulos Agro Parallel que hoy viven en FormGPS (overlays live,
  QuantiXMotorBridge, CutDispatcher/SectionX, sections_speed 5 Hz).
- **Fase 3 — Integración y paridad**
  CoreX interno completo (NTRIP, PGN), cámaras con player nativo, self-update
  APK vía OrbitX, hardening (arranque automático, kiosk mode/lock task para
  la pantalla del tractor).

### 4.6 Riesgos principales

| Riesgo | Mitigación |
|---|---|
| Render GL: reescritura de GL inmediato a GL ES es largo y sutil | Apoyarse en AgValoniaGPS; hacerlo una sola vez para desktop+Android |
| Lógica de guiado acoplada a `mf.` (FormGPS) | Extraer por módulos con tests de caracterización antes de mover |
| Broker MQTT muerto por Doze → nodos ciegos | Foreground service + watchdog + LWT ya definidos en la doctrina MQTT |
| Serial USB (marcas de chips varias) | Preferir módulos por UDP/WiFi (ya es la norma del ecosistema) |
| Divergencia Windows/Android durante la transición | Toda lógica nueva en netstandard; UI como capa fina en ambos lados |
| GPL-3.0 de AgValoniaGPS | Igual que AOG actual — el modelo de licenciamiento no cambia |

---

## 5. Conclusión

El traspaso es viable y el diseño actual ya apunta en la dirección correcta:
**todo lo que se construyó como servicio netstandard + web UI se reusa intacto**
(productos X-*, OTA, sync, Hub), los ESP32 y el cloud no se tocan, y el costo
real se concentra en dos frentes: (a) terminar de despegar la lógica de
guiado/bridge de las Forms, y (b) la pantalla de guiado Avalonia con render
GL ES — donde AgValoniaGPS es el atajo estratégico. La Fase 1 (Hub Android
sin guiado) permite tener valor en tablet en corto plazo y validar el 70% de
la plataforma antes de encarar el guiado.
