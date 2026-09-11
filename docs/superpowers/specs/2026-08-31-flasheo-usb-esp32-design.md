# Flasheo de módulos ESP32 por USB desde PilotX — diseño

Fecha: 2026-08-31
Estado: aprobado (brainstorming), pendiente de plan de implementación

## Objetivo

Agregar a PilotX un sector para **flashear un módulo ESP32 (FlowX y demás
productos X-*) por cable USB/serial**, sin depender del OTA por MQTT. Sirve
para nodos vírgenes (recién soldados, sin WiFi todavía) y para recuperar un
nodo que no levanta la red. Caso disparador: mañana se flashea un FlowX por USB.

## Alcance

Entra:
- Bundlear el flasher oficial `esptool.exe` (standalone win-x64) con PilotX.
- Bundlear los **drivers USB-serial** (CP210x + CH340) e instalarlos desde el
  propio panel. Todo viaja en el build: la pantalla no baja NADA de internet.
- Backend REST para listar puertos COM, instalar el driver y lanzar/monitorear
  un flasheo.
- Dos modos de flasheo: **completo** (chip virgen) y **solo app** (actualizar).
- UI nativa (Avalonia) dentro del `FirmwaresPanel` que ya existe.
- El firmware sale del **cache local de firmwares** que ya existe; ese cache se
  alimenta tanto de upload local (pendrive por `/firmwares`) como de descargas
  de **OrbitX** (flujo OTA cloud existente). El panel USB no distingue el
  origen: lista lo que haya en el cache.

No entra (YAGNI por ahora):
- Flasheo de Teensy (CoreX-ECU usa otro flujo, HEX + Teensy Loader).
- Flasheo masivo/multi-puerto simultáneo.
- Compilar firmware desde PilotX (los `.bin` vienen ya compilados).

## Arquitectura

### 1. Binario esptool + drivers (todo bundleado, cero internet)
- `esptool.exe` (PyInstaller standalone, ~10 MB, sin Python) se versiona en
  `Tools/esptool/esptool.exe` del repo.
- Los **drivers USB-serial** se versionan en `Tools/usb-drivers/`:
  - `cp210x/` — Silicon Labs CP210x VCP (el del esp32doit-devkit-v1 / FlowX).
  - `ch340/`  — WCH CH340/CH341 (placas ESP32 clon).
  Cada uno con sus `.inf`/`.cat`/`.sys` (paquete redistribuible del fabricante).
- `build.ps1` copia `Tools/esptool/` y `Tools/usb-drivers/` a
  `Build/Engine/tools/` (junto al Engine, que hostea el WebHost/backend).
- esptool se invoca con `--chip auto` para cubrir ESP32 clásico (FlowX) y
  variantes (S3/C3) sin cambiar código.

### 2. Backend — `UsbFlashService` (AgroParallel.Services) + `UsbFlashController` (AgroParallel.WebHost)

Contratos (snake_case, como el resto del wire del proyecto):

- `GET /api/usb/puertos`
  → `[{ "port": "COM5", "descripcion": "Silicon Labs CP210x (COM5)" }, ...]`
  Lista puertos serie (`SerialPort.GetPortNames` + descripción por WMI/registro).
  La UI la refresca con un botón.

- `POST /api/usb/flash`
  body: `{ "producto": "flowx", "version": "1.0.0", "puerto": "COM5",
           "modo": "completo" | "app", "borrar_antes": false }`
  → `{ "ok": true }` o error con código AGP.
  Reglas:
  - Un solo flasheo a la vez (lock en el servicio; si hay uno en curso → 409).
  - Valida `producto`/`version` con el mismo regex anti-traversal que
    `FirmwaresController` (`^[a-zA-Z][a-zA-Z0-9]{1,31}$` / `^[a-zA-Z0-9][\w.-]{0,31}$`).
  - Valida que el puerto exista en `GetPortNames`.
  - Resuelve el/los `.bin` en el cache (ver §3). Si falta el artefacto del modo
    pedido → error claro (no flashea).

- `POST /api/usb/driver/instalar`
  body: `{ "driver": "cp210x" | "ch340" | "ambos" }`
  → `{ "ok": true }` o error.
  Instala el/los driver(s) bundleados de `tools/usb-drivers/` con
  `pnputil /add-driver <ruta>\*.inf /install /subdirs`. Requiere elevación:
  el servicio lanza pnputil elevado (`ProcessStartInfo.Verb = "runas"`); si el
  usuario rechaza el UAC → error claro. Idempotente (reinstalar no rompe).
  La instalación de un driver modifica el sistema y SIEMPRE la dispara el
  operario con un botón + consentimiento UAC; nunca es automática.

- `GET /api/usb/flash/estado`
  → `{ "en_curso": true, "fase": "escribiendo", "pct": 42,
       "resultado": null | "ok" | "fail", "codigo": null | "AGP-USB-00X",
       "log": "<stdout crudo de esptool>" }`
  La UI lo polea cada 500 ms. El servicio corre esptool como proceso hijo,
  captura stdout línea a línea y parsea el progreso
  (`Writing at 0x0000abcd... (42 %)`), la fase (conectando / borrando /
  escribiendo / verificando / hard reset) y el resultado final.

### 3. Artefactos en el cache y direcciones de flasheo

El cache de firmwares local guarda, por producto/versión, hasta dos artefactos:
- `firmware.bin` — la **app** (lo que hoy baja/usa el OTA). Modo **app**.
- `factory.bin` — merge de bootloader+particiones+boot_app0+app. Modo **completo**.

Comandos esptool:
- **completo**: `esptool --chip auto --port COMx --baud 921600 write_flash 0x0 factory.bin`
- **app**:      `esptool --chip auto --port COMx --baud 921600 write_flash 0x10000 firmware.bin`
- **borrar_antes** (opcional): `erase_flash` previo.

El `factory.bin` de FlowX se arma con `esptool merge_bin` sobre el build de
PlatformIO (`esp32doit-devkit-v1`, particiones `min_spiffs.csv`):
`0x1000 bootloader.bin  0x8000 partitions.bin  0xe000 boot_app0.bin  0x10000 firmware.bin`.
Se sube al cache (o a OrbitX, de donde el cache lo baja) como artefacto
`factory.bin` de la versión. Para mañana se deja el `factory.bin` de FlowX ya
disponible.

Si una versión NO tiene `factory.bin` (solo el app), el modo completo queda
deshabilitado en la UI para esa versión, con nota "subí el factory.bin para
flasheo completo". El modo app siempre está disponible.

### 4. UI — sección "Flashear por USB" en `FirmwaresPanel` (Avalonia nativo)

- Selector producto + versión (del cache).
- Selector de puerto COM + botón refrescar. Si no aparece ningún puerto, se
  muestra el botón **"Instalar driver USB"** (llama a `/api/usb/driver/instalar`
  con `ambos`); tras instalar y reconectar el cable, el puerto aparece.
- Radio de modo: **Completo (chip nuevo)** [default] / **Solo app (actualizar)**.
  El radio "Completo" se deshabilita si la versión no tiene `factory.bin`.
- Check "Borrar chip antes" (opcional).
- Botón **Flashear** → barra de progreso + fase textual; log crudo de esptool
  en un `<details>` colapsable.
- Botón **Borrar chip** suelto (erase_flash) para dejar un nodo limpio.
- Mientras flashea: botones bloqueados, no se puede lanzar otro.

## Manejo de errores

Mapear las fallas típicas de esptool a mensaje claro + código AGP
(`AgpErrorMapper`), con el stdout crudo en el `<details>`:
- Puerto ocupado / no se puede abrir → "El puerto COMx está en uso (¿otra app
  lo tiene abierto?)". `AGP-USB-001`.
- `Failed to connect` / chip no entra en boot → "El módulo no respondió: mantené
  BOOT y reintentá / revisá el cable". `AGP-USB-002`.
- Timeout de escritura → `AGP-USB-003`.
- Artefacto faltante / hash inválido → `AGP-USB-004`.
- esptool no encontrado en `tools/esptool/` → `AGP-USB-005` (build incompleto).
- Instalación de driver falló / UAC rechazado → `AGP-USB-006`.

## Seguridad

- Sin ejecución de binarios arbitrarios: solo se lanza `esptool.exe` del install
  dir, con argumentos armados por el servicio (nunca strings del cliente
  concatenados en shell — usar `ProcessStartInfo.ArgumentList`).
- Producto/versión validados por regex antes de tocar el filesystem.
- El endpoint es LAN-local (mismo WebHost que el resto del Hub).

## Testing

- Unit test del **parser de progreso** de esptool (líneas de stdout → fase/%).
- Unit test del **armado de argumentos** por modo (completo vs app, con/sin
  borrar_antes, baud, chip auto).
- El flasheo real contra hardware es **prueba de banco** (mañana, FlowX por USB)
  — no se declara "anda" hasta verificarlo en el nodo.

## Manual

Es un flujo nuevo que el operario ve → se actualiza `ayuda.html` en el mismo
commit (regla del proyecto), con la ruta real del sector dentro de PilotX.

## Riesgos / pendientes

- **Driver USB-serial**: si el puerto no aparece es driver faltante; el panel
  ofrece instalarlo desde los drivers bundleados (botón "Instalar driver USB").
  Documentar en el manual el paso "instalar driver → reconectar cable".
- **Insumo a conseguir**: los paquetes redistribuibles de CP210x (Silicon Labs)
  y CH340 (WCH) hay que descargarlos una vez de los fabricantes y ponerlos en
  `Tools/usb-drivers/` antes del build. No se bajan en runtime.
- **`esptool.exe` firmado**: pnputil puede pedir que el driver esté firmado
  (los oficiales lo están). esptool.exe no es driver, no aplica.
- **Generar el `factory.bin` de FlowX**: requiere el build de PlatformIO del
  FlowX (disponible en `G:\AgroParallel\Productos\FlowX\...\FlowXNode`). Se
  genera con `esptool merge_bin` y se deja en el cache/OrbitX antes de mañana.
- **Baud**: 921600 por default (el del platformio.ini). Si un cable/módulo no
  banca, fallback a 460800/115200 — configurable si hace falta (no MVP).
```
