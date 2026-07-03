# Auditoría del Hub — módulos, endpoints y estado

> Fecha: 2026-07-02 · Rama: `codex/pilotx-ui-new`
> Relevamiento completo de las ~35 pantallas del Hub (`AgroParallel.WebUI/wwwroot/pages/`)
> y sus 32 controllers (`AgroParallel.WebHost/Controllers/`).
> Estados: ✅ LISTO · 🔨 EN DESARROLLO · ❌ FALTANTE

---

## Resumen ejecutivo

**Críticos (arreglar primero):**
1. **StormX umbrales solo-lectura** — la pantalla muestra umbrales pero no se pueden editar, y el firmware StormX todavía no publica MQTT. El módulo entero está bloqueado por firmware.
2. **Acciones de power sin confirmación** — reboot/shutdown de la PC desde `sistema.html` ejecutan directo, sin modal "¿estás seguro?". Riesgo alto en campo.
3. **`actualizar.html` no detecta el restart** — tras aplicar el update de PilotX el operario tiene que refrescar a mano; falta polling post-apply.

**Duplicaciones a limpiar:**
- `QxTrenConfigDto` duplica `TrenDto` del implemento central → `quantiX_motores.json` se puede desincronizar de la geometría central (mismo bug que ya arreglamos en VistaX).
- `GET /nodos` vs `GET /nodos/unified` — dos endpoints para casi lo mismo.
- Campos muertos: `semillas_vuelta` (QuantiX), `distancia_entre_trenes` (SectionX), campos MQTT en `VistaXConfigDto` que la UI nunca usa.

**Transversales:**
- Serialización JSON inconsistente entre controllers (algunos PascalCase Swan, otros snake_case manual, corex-ecu con rewriter camelCase frágil).
- Stubs con datos fake en `sectionx.js` / `linex.js` (7 surcos inventados si no hay config).
- Polling-heavy en varias pantallas donde ya hay snapshot in-process disponible.
- Falta re-sync de QuantiX/LineX cuando cambia la geometría central del implemento (VistaX ya quedó resuelto).

---

## 1. Siembra

### 1.1 VistaX (`vistax.html` + live/stats/densidad/maquina/semilla)

**Propósito:** monitoreo de siembra — config del implemento (ahora derivada del central), mapeo de sensores, calibración, insumo activo, vistas live/stats/mapas.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET `/vistax/implemento` | — | `{path, implemento}` con geometría **mergeada del central** | ✅ |
| PUT `/vistax/implemento` | setup VistaX-only (densidad, tolerancia, factor K, mapeo) | `{ok}` — ya NO acepta geometría | ✅ |
| GET `/vistax/live` | — | snapshot sensores + densidad + alarmas | ✅ |
| GET `/vistax/stats` | `lote`, rango | agregados por surco/tren | ✅ |
| POST `/vistax/calibrar` | `{sensor, modo}` (5s objetivo/saturado) | resultado calibración | ✅ |
| GET/PUT `/vistax/insumo-activo` | id insumo | `{ok}` | ✅ |
| GET `/vistax/export/*` | lote | NDJSON / SHP puntos / SHP heatmap | ✅ |

- **De más:** campos MQTT en `VistaXConfigDto` sin uso en UI.
- **Faltante:** ❌ reload de calibración en caliente (hoy requiere reiniciar live); ❌ viewer de mapas en cloud + descarga local de capas extra.

### 1.2 QuantiX (`quantix.html` + `widget-quantix.html`)

**Propósito:** control PID de motores de siembra — config motores/trenes, dosis objetivo, calibración, live.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET/POST `/quantix/config` | motores, trenes, dosis | config / `{ok}` | ✅ |
| GET `/quantix/live` | — | rpm, semillas/m, estado PID por motor | ✅ |
| POST `/quantix/calibrar` | `{motor, vueltas}` | resultado | ✅ |
| POST `/quantix/dosis` | `{tren, valor, unidad}` | `{ok}` | ✅ |

- **De más:** `QxTrenConfigDto` duplica `TrenDto` central (riesgo desync); campo muerto `semillas_vuelta`.
- **Faltante:** ❌ endpoint de rangos PID (la UI hardcodea min/max); ❌ re-sync automático cuando cambia geometría central.

### 1.3 SectionX (`sectionx.html`)

**Propósito:** control de secciones automático — asignación secciones↔relays, delays, modo.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET/POST `/sectionx/config` | secciones, relays, delays | config / `{ok}` | ✅ |
| GET `/sectionx/live` | — | estado on/off por sección + velocidad | ✅ |

- **De más:** campo muerto `distancia_entre_trenes`; stub de 7 surcos fake en `sectionx.js`.
- **Confuso:** wording cable/sección/surco mezclado en la pantalla — unificar terminología.
- **Faltante:** ❌ test de cobertura de secciones (verificar que relays responden antes de salir al lote).

### 1.4 LineX (`linex.html`)

**Propósito:** corte surco por surco (row-by-row).

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET/POST `/linex/config` | surcos, mapeo | config / `{ok}` | ✅ |
| GET `/linex/live` | — | estado por surco | ✅ |

- **De más:** stub de surcos fake en `linex.js`.
- **Faltante:** ❌ re-sync con geometría central; 🔨 firmware LineX en desarrollo.

---

## 2. Líquidos y clima

### 2.1 FlowX (`flowx.html`)

**Propósito:** dosificación líquida — válvula motorizada puente H, caudalímetro, regulaciones.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET/POST `/flowx/config` | nodos, regulaciones, ancho barra | config / `{ok}` | ✅ |
| GET `/flowx/live` | — | caudal, presión, apertura válvula | ✅ |
| POST `/flowx/target` | `{lts_ha}` | `{ok}` (publica MQTT) | ✅ |

- **De más:** pestaña "Próximamente" vacía.
- **Bug menor:** 🔨 regulación 2 se guarda pero no se actúa (solo reg 1 activa).
- **Faltante:** ❌ gráfico de caudal live; ❌ editor de umbrales PWM (hoy solo firmware).

### 2.2 StormX (`stormx.html`)

**Propósito:** estación meteo — viento, temperatura, humedad, umbrales de aplicación.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET `/stormx/config` | — | config + umbrales | ✅ |
| GET `/stormx/live` | — | telemetría meteo | 🔨 (firmware no publica MQTT aún) |
| PUT umbrales | — | — | ❌ solo lectura |

- **BLOQUEANTE:** editor de umbrales es solo-lectura y el firmware no emite datos — el módulo entero es placeholder funcional.
- **Faltante:** ❌ histórico 24h; ❌ bloqueo automático de FlowX por condiciones climáticas (la integración estrella del módulo).

---

## 3. Nodos, OTA y sistema

### 3.1 Nodos (`nodos.html`, `nodo-detalle.html`)

**Propósito:** registro de nodos ESP32 descubiertos por MQTT announcement, estado online/offline, pin por implemento.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET `/nodos` | — | lista nodos + estado | ✅ |
| GET `/nodos/unified` | — | lista con merge de config | ✅ (redundante con el anterior) |
| POST `/nodos/{uid}/aceptar` | — | `{ok}` | ✅ |
| POST `/nodos/{uid}/renombrar` | `{nombre}` | `{ok}` | ✅ (solapa con aceptar) |
| POST `/nodos/{uid}/cmd` | `{cmd}` (reboot, safe-mode…) | `{ok}` | ✅ |

- **De más:** `/nodos` vs `/nodos/unified`; renombrar vs aceptar (unificar).
- **Faltante:** ❌ `clear_boot_reason`; ❌ auto-reset de safe-mode tras N min OK; 🔨 timeout offline configurable por tipo de nodo (alarmas).

### 3.2 Firmwares / OTA (`firmwares.html`)

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET `/firmwares` | — | catálogo local + cloud | ✅ |
| POST `/firmwares` (upload .bin) | multipart, cap 8 MB | `{ok}` | ✅ |
| DELETE `/firmwares/{prod}/{ver}` | — | `{ok}` | ✅ |
| POST OTA a nodo | `{uid, version}` | progreso vía live | ✅ (SHA-256 + anti-downgrade) |

- **Faltante:** ❌ validación semver de downgrade también en UI (hoy solo backend); ❌ vista de historial OTA por nodo.

### 3.3 Actualizar PilotX (`actualizar.html`)

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET `/actualizar/estado` | — | versión actual/disponible | ✅ |
| POST `/actualizar/aplicar` | — | lanza Updater.exe | ✅ |

- **CRÍTICO:** ❌ tras aplicar, la página no detecta el restart de PilotX — el operario queda mirando una pantalla muerta. Falta polling + auto-reload.

### 3.4 Sistema / Debug / Setup / PWA (`sistema.html`, `debug.html`, `setup.html`, `pwa-qr.html`)

| Función | Estado |
|---|---|
| Info de sistema, red, discos | ✅ |
| Reboot/shutdown PC | ✅ pero ❌ **SIN modal de confirmación** (crítico) |
| Log viewer debug | ✅ — ❌ falta búsqueda/filtro/export |
| Setup wizard inicial | ✅ — ❌ falta "reiniciar wizard" |
| QR PWA | ✅ — ❌ no se regenera si cambia la IP LAN |

### 3.5 OrbitX cloud (`orbitx.html`)

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET/POST `/orbitx/config` | enabled, vínculo (URL fija readonly) | config / `{ok}` | ✅ |
| GET `/orbitx/estado` | — | último sync, pendientes | ✅ |

- **Faltante:** ❌ sync-log visible (qué archivos subieron/fallaron y cuándo).

### 3.6 CoreX-ECU (`corex-ecu.html`)

**Propósito:** firmware de la ECU CoreX — versiones, flasheo, WAS source, verificación de reboot.

- Endpoints completos, flujo flash + espera reboot real: ✅
- **Frágil:** rewriter camelCase manual en el controller — consolidar con el resto de la serialización.

---

## 4. Campo y geometría

### 4.1 Implemento (`herramienta.html`)

**Propósito:** **SSOT de geometría** — CRUD multi-slug, catálogos de sembradoras/máquinas, write-back a Registry (ToolConfig) con dirty-check.

| Endpoint | Parámetros | Respuesta | Estado |
|---|---|---|---|
| GET `/implemento` (+lista, activo) | — | dto central / lista | ✅ |
| POST/PUT `/implemento` | dto completo | `{ok}` + SyncToolIfChanged | ✅ |
| POST `/implemento/activar` | `{slug}` | `{ok}` (+auto-open overlays) | ✅ |
| GET catálogos sembradoras/máquinas | — | listas | ✅ |

- **Faltante:** ❌ validaciones de rango en UI (ancho > 0, surcos coherentes con secciones); ❌ export/import de implemento (JSON) para pasar entre PCs.

### 4.2 Vehículo (`vehiculo.html`)

- CRUD config vehículo: ✅
- **Faltante:** ❌ presets de vehículo (tractores comunes).

### 4.3 Lotes / Datos (`lote.html`, `datos-lote.html`, `datos-gps.html`)

- Lista/abrir lote, stats del lote, datos GPS live: ✅
- **Faltante:** ❌ DELETE/rename de lote desde el Hub; ❌ export CSV/PNG de stats.

### 4.4 Mapas / Prescripciones (`mapas.html`, `prescripciones.html`)

- GeoJSON + heatmap, prescripciones con dose-at-point: ✅
- **Faltante:** ❌ overlay de prescripción en `piloto.html`.

### 4.5 Piloto (`piloto.html`)

- Canvas de guiado 10 Hz, overlays por producto: ✅
- **Faltante:** ❌ overlay prescripción (ver arriba).

### 4.6 Cámaras (`camaras.html` + widget)

- Hikvision-only, probe ISAPI, RTSP→MediaMTX: ✅

### 4.7 Insumos (`insumos.html`)

- Catálogo compartido VistaX/QuantiX: ✅

### 4.8 Hub landing (`hub.html`)

- Grid de módulos con estado: ✅
- **Faltante:** ❌ badges de alertas por módulo en la landing (nodo offline, safe-mode, update pendiente).

### 4.9 Cabina / Alarmas (`cabina-alarmas.html`)

- Alarmas filtradas por `nodos_uids` del implemento activo, códigos AGP-*: ✅
- **Faltante:** 🔨 timeout offline configurable por tipo.

---

## 5. Plan sugerido (por prioridad)

| # | Ítem | Módulo | Esfuerzo |
|---|---|---|---|
| 1 | Modal de confirmación en reboot/shutdown | sistema | bajo |
| 2 | Auto-reload post-update en actualizar.html | actualizar | bajo |
| 3 | Unificar QxTrenConfigDto con TrenDto central + re-sync geometría | quantix | medio |
| 4 | Sacar stubs fake de sectionx.js/linex.js | sectionx/linex | bajo |
| 5 | Endpoint rangos PID (sacar hardcode UI) | quantix | bajo |
| 6 | Editor umbrales StormX + firmware MQTT | stormx | alto (bloq. firmware) |
| 7 | Bloqueo FlowX por clima (StormX→FlowX) | flowx/stormx | medio |
| 8 | Unificar /nodos y /nodos/unified; renombrar=aceptar | nodos | bajo |
| 9 | Limpiar campos muertos (semillas_vuelta, distancia_entre_trenes, MQTT VistaX) | varios | bajo |
| 10 | Validaciones de rango UI implemento | herramienta | bajo |
| 11 | Sync-log OrbitX visible | orbitx | medio |
| 12 | Consolidar serialización JSON entre controllers | transversal | medio |
