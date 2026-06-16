# Changelog — PilotX (AgOpenGPS — Agro Parallel)

Todos los cambios relevantes a la app PC del tractor se anotan acá.

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y versionado [SemVer](https://semver.org/lang/es/) (MAJOR.MINOR.PATCH).

La fuente de verdad de la versión es `Installer/VERSION`. El build
(`build.ps1` / `build-installer.ps1`) la lee de ahí y la estampa en
el ensamblado vía `-p:Version=` para que `PilotXSelfUpdate` la pueda
detectar en runtime y compararla contra el catálogo OTA.

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
