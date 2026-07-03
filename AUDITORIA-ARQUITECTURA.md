# Auditoría de arquitectura — Ecosistema Agro Parallel

> Fecha: 2026-07-02 · Complementa a `AUDITORIA-HUB.md` (endpoints/pantallas).
> Cobertura: backend .NET (PilotX), frontend Hub (JS/HTML), firmwares ESP32, cloud OrbitX-Server.
> Estados: ✅ LISTO · 🔨 EN DESARROLLO · ❌ FALTANTE · ➖ DE MÁS

---

## Resumen ejecutivo (las 10 cosas que importan)

| # | Hallazgo | Capa | Gravedad |
|---|---|---|---|
| 1 | ~313 `catch {}` vacíos — excepciones tragadas sin log | Backend | 🔴 crítico |
| 2 | Cero tests automatizados en las 4 capas (backend, frontend, firmware*, cloud) | Todas | 🔴 crítico |
| 3 | Sin backups automatizados de CouchDB — crash = pérdida total | Cloud | 🔴 crítico |
| 4 | ~65% de código duplicado entre los 5 firmwares (MQTT/OTA/provisioning clonados) | Firmware | 🟠 alto |
| 5 | Serialización JSON mixta (Swan PascalCase vs S.T.Json snake_case) → workarounds tolerantes en backend + ~662 `??`/lowercase en JS | Backend+Front | 🟠 alto |
| 6 | Bridges MQTT (QuantiX/FlowX/Cut) ~60% duplicados, sin clase base | Backend | 🟠 alto |
| 7 | Helpers frontend duplicados: `escapeHtml` en 28 archivos, `fmt` en 16, `toast` en 4 | Frontend | 🟠 alto |
| 8 | Sin DI ni logging estructurado — bootstrap manual gigante, diagnóstico en campo a ciegas | Backend | 🟠 alto |
| 9 | Sin monitoreo/alerting en cloud (solo console.log) | Cloud | 🟠 alto |
| 10 | StormX firmware SÍ publica MQTT (v0.2.0) — el bloqueo real es que le falta safe-mode y a la UI el editor de umbrales | Firmware+Hub | 🟡 corrección a auditoría anterior |

**Discrepancias a verificar con vos:**
- El agente no encontró firmware **SectionX** como producto separado ni firmware **CoreX** (carpeta `CoreX/Software/Firmware_Embebido/` vacía — pero sabemos que CoreX ECU v1.14 existe con WAS source selector; probablemente vive en otro path/repo).

---

## 1. Backend .NET (PilotX)

### ✅ Lo que está
- **Capas correctas**: Models (netstandard2.0) ← Services ← WebHost (EmbedIO :5180) ← Shell (WebView2) ← GPS (net48). Sin violaciones graves.
- **Servicios por módulo bien segregados**: VistaX (config/live/calib/SeedMonitor), QuantiX (config/bridge/dose resolver), SectionX→CutDispatcher, FlowX, LineX, StormX, OrbitX (sync + OTA completo: OtaClient/Coordinator/Mirror/LanServer/SelfUpdate/CloudReporter), NodoRegistry, Implemento (SSOT recién centralizado), Camaras (Hikvision ISAPI).
- **Ciclo de vida**: `AgpWebHostBootstrap.EnsureStarted` desde FormGPS_Load, idempotente, con Stop limpio.

### 🔨 En desarrollo
- SectionX: refactor Bridge → CutDispatcher con referencias legacy colgando.
- FlowX: bridge cableado parcial (reg 2 no actúa).
- CamarasRemoteRelay: stub de relay, sin captura real.

### ❌ Lo que falta
- **Logging estructurado** (hoy: Debug.WriteLine + logs a disco ad-hoc tipo `qx_bridge.log`).
- **Tests** (cero en AgroParallel.Services; los bridges MQTT y parsers son los más riesgosos).
- **DI container** — todo instanciado a mano en el bootstrap; controllers no testeables.
- **Validación de config al guardar** (se puede persistir JSON inválido).
- Resiliencia MQTT (si el broker cae, reconexión ad-hoc por servicio).

### ➖ De más
- ~313 `catch {}` vacíos (muchos deliberados, pero sin siquiera un trace).
- Duplicación entre bridges (timer + lock + last-payload dict + log a disco, repetido 3-4 veces).
- Workarounds de casing (`GetBool(el, "enabled", "Enabled")` en OrbitXController) que existirían solo por la mezcla Swan/S.T.Json.
- Timers sin Dispose garantizado en algunos bridges.

---

## 2. Frontend Hub (wwwroot)

### ✅ Lo que está
- Vanilla JS + IIFE, cero deps salvo Leaflet. ~16k líneas, 22+ páginas. Liviano y apto WebView2.
- `api.js` centraliza fetch + WebSocket telemetría. `keyboard.js` (teclado virtual) completo.
- `theme.css` con variables exhaustivas, paleta consistente; `layout.css` sidebar 240px.
- PWA `/m/` con service worker correcto (app-shell cacheado, `/api` network-only).

### 🔨 En desarrollo
- Tres patrones de datos live conviviendo: setInterval crudo / WebSocket / one-shot fetch. Sin pausar en `visibilitychange`.
- PWA limitada a `/m/` — el Hub principal no tiene manifest.

### ❌ Lo que falta
- Manejo uniforme de errores de red (solo corex-ecu tiene `showError`; el resto falla en silencio).
- Loading states en botones de acción (el operario no sabe si el tap entró).
- Accesibilidad táctil: botones <44px en varias páginas (operario con guantes → mínimo 48px).
- Retry/backoff en polling.

### ➖ De más
- `escapeHtml` redefinido en **28 archivos**, `fmt` en 16, `toast` en 4, `$()` en 30+. ~8-12% del JS es duplicación evitable con un `ui-helpers.js`.
- Estilos `<style>` locales de 150-200 líneas en prescripciones/cabina-alarmas/sectionx que duplican `.card`/`.pill`.
- Stubs de 7 surcos fake en sectionx.js/linex.js.
- ~662 lugares defendiéndose del doble casing de la API (síntoma del problema #5 del backend).

---

## 3. Firmwares ESP32

| Firmware | Versión | Board | Estado | Notas |
|---|---|---|---|---|
| QuantiX | 2.3.0 | ESP32 devkit | ✅ | envelope cmdId+ttl+ack, buffer 2048, portal MDLnetwork. ➖ fallback broker hardcoded 192.168.1.12 |
| VistaX | 3.0.0 | ESP32-S3 | ✅ | único con schema MQTT versionado; safe-mode NVS |
| FlowX | 1.9.2 | ESP32 devkit | ✅ | ⚠ buffer 1024 justo (drops silenciosos conocidos) |
| StormX | 0.2.0 | ESP32-S3 | 🔨 | **SÍ publica MQTT** (announcement + status_live + cmd/OTA). ❌ sin safe-mode; LCD sin watchdog |
| LineX | 1.0.0 | ESP32 devkit | ✅ | safe-mode, buffer 3072, tests nativos (sin CI) |
| CoreX ECU | ? | — | ❓ | carpeta Firmware_Embebido vacía — el fw v1.14 real vive en otro lado (verificar) |
| SectionX | — | — | ❓ | no existe como firmware separado en el árbol (¿secciones = relays de CoreX ECU?) |

### ❌ Lo que falta / ➖ de más (transversal)
- **Sin librería común**: MQTT_Custom.cpp y OTA.cpp clonados ~90% entre productos → **~65% de las ~8.400 líneas duplicadas**. Recomendación: lib PlatformIO compartida (MQTT + OTA + provisioning + safe-mode).
- Provisioning WiFi: 5 implementaciones distintas (MDLnetwork struct vs NVS crudo).
- Safe-mode inconsistente: 3/5 lo tienen, con persistencia distinta (NVS vs SPI flash).
- Topics MQTT como strings hardcodeados (sin enum/const compartida); schema sin versionar salvo VistaX.
- OTA bloqueante en loop en QuantiX/FlowX/LineX (VistaX alimenta watchdog durante stream).

---

## 4. Cloud OrbitX-Server

### ✅ Lo que está
- Express + Socket.IO + CRON, 22 routers segmentados, auth multinivel (JWT panel / device tokens / pairing flow tipo RFC 8628), hardening A+B aplicado (fail-fast prod, CORS allowlist, IDOR fix, revocación token_version, rate limit login, upsert 409, tracking buckets 1min, índices Mango).
- Panel EJS: 18 páginas funcionales (dashboard, lotes, tracking live, vistax, firmwares OTA, usuarios/roles/orgs, integraciones SMTP/Telegram/WhatsApp, cámaras WebRTC, auditoría).

### 🔨 En desarrollo
- `vistax-mapas.ejs`: viewer con TODO explícito "render heatmap (Polygon parser pendiente)".

### ❌ Lo que falta
- **Backups CouchDB automatizados** (crítico).
- **Tests** (cero) y **monitoreo/alerting** (solo console.log).
- Descarga de lotes desde el panel.
- Validación de entrada exhaustiva (rangos geo, etc.), rate limit en uploads, revocación de sockets vivos.
- Purga/retención de tracking viejo; optimización de views (by_tipo emite todo).

### ➖ De más
- Duplicación de validación device-auth entre `auth.js` y `middleware/auth.js`; JWT validation duplicada panel.js/routes.
- Stub no-op en integraciones.js "por compatibilidad con código viejo".
- Faltan en package.json: helmet, express-validator, pino/winston (no es "de más" pero es deuda de deps).

---

## 5. Backlog maestro propuesto

### Bloque A — Confiabilidad en campo (backend PilotX)
- A1. Logging estructurado + barrida de `catch {}` (reemplazar por log con contexto). 🔴
- A2. Unificar serialización JSON de controllers (matar el doble casing en la raíz). 🟠
- A3. Clase base para bridges MQTT (QuantiX/FlowX/Cut) + Dispose garantizado. 🟠
- A4. Tests de los parsers/bridges más críticos (dosis, secciones, envelope). 🔴
- A5. Validación de configs al guardar. 🟡

### Bloque B — Frontend Hub
- B1. `ui-helpers.js` compartido (escapeHtml/fmt/toast/$) + limpiar 28 duplicados. 🟠
- B2. Manejo uniforme de errores de red + loading states en acciones. 🟠
- B3. Poller central con pausa en visibilitychange + retry/backoff. 🟡
- B4. Sacar stubs fake sectionx/linex; targets táctiles ≥48px. 🟡
- B5. Los 3 críticos de AUDITORIA-HUB.md: confirmación power, auto-reload actualizar, editor umbrales StormX. 🔴

### Bloque C — Firmwares
- C1. Lib PlatformIO compartida `agp-node-core` (MQTT + OTA + provisioning + safe-mode). 🟠 (esfuerzo alto, gran retorno)
- C2. Safe-mode en StormX + watchdog LCD. 🟠
- C3. Buffer FlowX 1024→2048 + sacar broker hardcodeado QuantiX. 🟡 (rápido)
- C4. Versionar schema MQTT en todos (como VistaX). 🟡
- C5. Aclarar CoreX ECU / SectionX firmware (dónde viven, estado real). ❓

### Bloque D — Cloud OrbitX
- D1. Backups CouchDB diarios automatizados. 🔴 (esfuerzo bajo)
- D2. Monitoreo + alerting básico. 🟠
- D3. Viewer VistaX heatmap en cloud + descarga de lotes desde panel. 🟡
- D4. Tanda C hardening: validación entrada, rate limit uploads, socket revocación. 🟡
