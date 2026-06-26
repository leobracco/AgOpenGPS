# CoreX-ECU — Actualización de firmware desde el Hub

Fecha: 2026-06-08
Estado: Aprobado (diseño)

## Problema

El operario no tiene forma de actualizar el firmware del CoreX-ECU (autosteer ECU
sobre Teensy 4.1) desde la pantalla. El pipeline OTA existente es solo para nodos
ESP32 (descarga `.bin` + publica MQTT), y el CoreX-ECU no habla MQTT: está en
Ethernet (IP por defecto 192.168.5.126) y se actualiza por HTTP.

## Hallazgo clave (viabilidad confirmada por código)

El firmware del CoreX-ECU **ya tiene OTA por red completo** (FlasherX):

- `POST /api/firmware` ← recibe un Intel-**HEX** completo en el body, lo streamea
  al flash (staging), verifica `FLASH_ID`, hace `flash_move()` y rebootea.
- `GET /api/firmware` ← estado/progreso (`receiving`/`ok`/`error`, bytes, líneas).
- Guards del firmware: `409` si el guiado está activo (`watchdogTimer < 100`),
  `409` si ya hay un OTA en curso, `411` sin `Content-Length`, `413` si es muy
  grande, `400` si el HEX está corrupto o el `FLASH_ID` no matchea.
- No se puede brickear con un `.hex` malo: no toca la zona activa hasta el
  `flash_move()` final. Recovery último recurso = USB físico una vez.
- El `.hex` compilado existe en `build/CoreX-ECU_v1.16.0.hex` (~1.3 MB).

Fuente del firmware:
`G:/AgroParallel/Productos/CentriX-Spark/Software/Firmware_Embebido/AIO_Keya_WasKeyaFiltre/`
(endpoint en `zOta.ino`, ruteado en `zApi.ino` apiLoop, short-circuit del POST).

**Por qué no hay botón hoy:** el endpoint existe en la unidad pero el Hub no lo
proxea (el `CoreXEcuController` no tiene ruta de flasheo), y el Firmware Manager
del Hub maneja `.bin` para ESP32.

## Decisión de producto

- El `.hex` entra por **dos vías ya existentes**: subida local desde el tractor
  (`firmwares.html` → `POST /api/firmwares/upload`) y sync desde OrbitX
  (`FirmwareMirror.SyncAsync`). El blob se guarda en el cache como producto
  `corex-ecu` (archivo `firmware.bin`, contenido = Intel-HEX intacto).
- El botón de actualizar vive en la **página del CoreX-ECU** (Enfoque A), pestaña
  Config. Es el lugar contextual: el operario ya está mirando esa unidad y su
  estado de guiado.
- Las pantallas **no mencionan "ESP32" ni "Teensy"** — solo nombres de producto
  (doctrina de branding del ecosistema). Comentarios de código y namespaces/clases
  quedan intactos.

## Flujo

1. El `.hex` ya está en el cache como `corex-ecu/<version>/firmware.bin`.
2. Pestaña Config del CoreX-ECU → sección "Actualizar firmware": versión actual,
   `<select>` de versiones disponibles, botón "Actualizar a vX".
3. Confirmar → `POST` al Hub → el Hub lee el blob y lo streamea con `Content-Length`
   a `http://<unidad>/api/firmware` → el firmware verifica `FLASH_ID`, flashea,
   rebootea.
4. La UI muestra progreso indeterminado y, tras el reboot, **verifica releyendo
   `/status`** hasta que la versión sea la nueva (éxito) o timeout.

## Diseño

### Backend (`AgroParallel.Services` + `AgroParallel.WebHost`)

- **DTOs** (`CoreXEcuDtos`):
  - `CoreXEcuFlashRequestDto { string Version }`
  - `CoreXEcuFlashResultDto { bool Ok, string ErrorCode, string Error, string Detail, long BytesSent, string Version }`
- **`ICoreXEcuService` + `CoreXEcuService`** → `FlashFirmwareAsync(string version)`:
  - Resuelve `FirmwareMirror.PathBin(cacheDir, "corex-ecu", version)`; valida que
    exista (si no → AGP-* "firmware no encontrado en el cache").
  - `FileStream` → `StreamContent` con `Content-Length = FileInfo.Length` y
    `Content-Type: application/octet-stream`.
  - `POST` a `BaseUrl(cfg) + "/api/firmware"` con **CTS de timeout largo (~120 s)**
    (no el de 3 s; el flash bloquea unos segundos).
  - Mapeo de respuestas del firmware: `200` → Ok; `409` → AGP-NET-409
    ("no se puede actualizar con el guiado activo"); `400/413` → HEX inválido /
    muy grande; resto → AGP-NET-101. Timeout → AGP-NET-002.
- **`CoreXEcuController`**: `POST /corex-ecu/firmware/flash` (body `{version}`) →
  `_svc.FlashFirmwareAsync(version)`. Reusa el patrón de motor/test, zero, reboot.

Decisión: progreso **indeterminado** ("Actualizando… ~30 s, no apagues la unidad")
+ verificación post-reboot, en vez de barra con %. Motivo: durante el `POST` el
firmware no atiende un segundo request (su loop HTTP queda consumido), así que un
% real exigiría que el Hub publique su propio estado de streaming y la UI lo
poolee — más código y más frágil para poca ganancia. (Mejora opcional futura.)

### UI (`corex-ecu.html`, pestaña Config)

Sección "Actualizar firmware": versión actual, `<select>` de versiones disponibles
(de `GET /api/firmwares` filtrando `corex-ecu`), botón "Actualizar a vX", bloque de
estado (spinner + texto). Botón deshabilitado si el guiado está activo (mismo lock
que el motor manual) y si no hay versiones en el cache (con link a la página de
firmwares para subir/sincronizar).

### JS (`corex-ecu.js`)

- `loadFirmwareVersions()` → llena el `<select>`.
- `flashFirmware()` → confirma, deshabilita controles, `POST /api/corex-ecu/firmware/flash`,
  muestra estado; ante `ok` arranca `pollUntilRebooted()` que reconsulta `/status`
  hasta ver la versión nueva o timeout (~60 s), con mensaje claro de éxito/fallo.
- Respeta el lock de guiado (igual que `updateMotorLock`).

### Limpieza de wording (solo texto de pantalla)

- `corex-ecu.html`: "Teensy 4.1" → "CoreX-ECU"; "IP del Teensy" (×2) →
  "IP de la unidad" / "IP del CoreX-ECU"; "Conexión con el Teensy" →
  "Conexión con el CoreX-ECU".
- `firmwares.html`: ".bin (ESP32) · .hex (Teensy/CoreX-ECU)" → sin chip; optgroup
  "Nodos ESP32 (.bin)" → "Nodos (.bin)"; "CoreX-ECU (Teensy)" → "CoreX-ECU".
- `flowx.html`: "LittleFS del ESP32" → "memoria del nodo".
- `nodos.html`: "Si tu ESP32 no aparece…" → "Si tu nodo no aparece…".

## Sincronización de archivos (importante)

Toda edición de HTML/JS hay que hacerla en **dos lugares**: la fuente
(`SourceCode/.../AgroParallel.WebUI/wwwroot/...`) y la copia de runtime que sirve
el Hub (`Build/AgroParallel/wwwroot/...`). El backend se compila a la DLL y se
copia a `Build/AgroParallel.Services.dll`.

## Pruebas

- Build limpio de `AgroParallel.Services` + WebHost.
- Flash real contra la unidad en 192.168.5.126 con el `.hex` 1.16.0 del cache;
  verificar reboot + versión nueva en `/status`.
- Guard de guiado: con guiado activo, botón bloqueado y backend devuelve 409 mapeado.
- Caso sin versiones en cache: botón deshabilitado + link a firmwares.

## Fuera de alcance

- % de progreso real durante el streaming (mejora opcional).
- Integrar el `.hex` del CoreX-ECU al pipeline OTA MQTT de ESP32 (no aplica: la
  unidad no habla MQTT).
