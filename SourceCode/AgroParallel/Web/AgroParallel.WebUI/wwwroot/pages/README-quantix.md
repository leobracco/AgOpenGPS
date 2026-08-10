# QuantiX — Interfaz web (Hub PilotX)

Control de N motores eléctricos de siembra/fertilización con PID por MQTT. Cada
motor dosifica (PID sobre PPS) y corta surco por surco.

**Archivos:** `pages/quantix.html` + `js/quantix.js`
**Backend:** `QuantiXController.cs` → `QuantiXMotorBridge.cs`
**Persistencia:** `quantiX_motores.json` (trenes + nodos + motores)

> Los nodos QuantiX se descubren **solo** por MQTT (`agp/quantix/{uid}/announcement`).
> Está prohibido agregarlos a mano en la UI.

---

## Qué hace la pantalla

La página tiene 5 pestañas (`showTab`):

### 1. Siembra
- **Monitor de surcos (tira)** — `renderStrip` / `paintSurco`: cada surco se pinta
  con el color del motor que lo alimenta. Pintás un surco para asignárselo a un
  motor (consume `cortes[]` de cada motor). `surcoOwner` resuelve a qué motor
  pertenece cada surco; `surcosHuerfanos` + `updateOrphanWarn` avisan los surcos
  sin asignar.
- **Lista de motores** — `renderMotorList`: una fila por motor con:
  - Color, nombre **editable inline** (`qxNombre`), surcos asignados (`fmtCortes`).
  - Selector de **tren físico** (`trenSelectHtml`) cuando el implemento define ≥2 trenes.
  - **Dosis fija** + toggle de unidad (`kg_ha` ↔ `sem_m`).
  - En `sem_m`, selector de **tipo de dosificador** (`qxTipoDosif` →
    `tipo_dosificacion`): **placa neumática** = el campo sem/vuelta se carga
    directo como **alvéolos** de la placa; **a calibrar** (rodillo u otro) =
    botón `qxIrCal` que salta a la pestaña Calibración (contar/pesar).
  - Selector **Mapa / Dosis fija** (`mapaSelectHtml`): elige columna de
    prescripción (`campo_dosis`) o deja dosis fija. Lista columnas con
    `shapeDoseFields` desde el shapefile activo.
  - Botón eliminar motor.
- **Agregar motor** — `addMotor`: tope **24 motores** (`allMotors`).
- **Vista Planter / Tabla** — `setSiembraView` + `renderTabla`: alterna entre el
  layout de sembradora y una tabla.
- **Auto-reparto** — `autoReparto`: reparte surcos entre motores automáticamente.

### 2. Shape (dosis variable)
- Sube/borra archivos de shapefile (`shapeUpload` / `shapeRemove`,
  `shapeAddFiles`, `shapeBytesToBase64`). `refreshShapeActive` muestra el shape
  activo y sus columnas numéricas, que alimentan el selector Mapa de cada motor.

### 3. PID (live)
- `renderPid` + `pidTuneCard`: edita Kp/Ki/Kd, PWM mín/máx, max_hz, etc. por motor.
- `updatePidLive`: telemetría en vivo (real vs objetivo, PWM, estado PID).
- `pidPushHandler`: empuja config PID al nodo (`cmd?verb=config&retain=true`).
- `pidMaxHzHandler`: ajusta el techo de frecuencia.
- `pidAutoTuneHandler`: lanza auto-tune (`verb=cmd`) y sondea `/{uid}/autotune`.

### 4. Calibración
- `renderCalibrar` + `calCard`: calibra el dosificador. **Detecta la unidad del
  motor:**
  - `kg_ha` → calcula **MeterCal** (gramos por pulso).
  - `sem_m` → calcula **sem/vuelta** (`semillas_vuelta`) a partir de semillas
    contadas y PPR (dientes de engranaje).
- `updateCalibrarPulses` / `updateMetaPulsos` / `rebuildSurcoInputs`: cuenta
  pulsos reales del nodo durante la calibración.
- Guarda con PUT `/motores` + POST `/{uid}/send`.

### 5. Prueba
- `renderPrueba` + `pruebaCard`: hace girar el motor a un PWM o pulsos fijos
  (`sendTest` / `sendCal`) para verificar sentido y entrega.
- `startRamp` / `stopRamp`: rampa de PWM controlada para pruebas.

---

## Datos en vivo

- `pollLive` (2 Hz) → `GET /api/quantix/live`: estado de cada motor (real, objetivo,
  PWM, cortado/activo). `liveMotor` / `getLiveMotor` indexan por `uid`+motor.
- `computeEnMarcha` / `applyMarchaChrome`: detecta si la siembra está en marcha y
  bloquea edición. `motorAllCut` = motor parado solo cuando **todos** sus surcos
  están cortados (modelo eje solidario).
- `refreshAogLiveState`, `loadImplCentral`, `loadAogSections`, `loadShapeFields`:
  traen estado del implemento, secciones y shape desde PilotX.

---

## Endpoints consumidos

| Método | Ruta | Uso |
|---|---|---|
| GET  | `/api/quantix/live` | telemetría 2 Hz |
| GET/PUT | `/api/quantix/motores` | leer/guardar config completa |
| POST | `/api/quantix/{uid}/send` | empujar config al nodo |
| POST | `/api/quantix/{uid}/cmd?verb=…&retain=` | config / cmd / cal / test |
| GET  | `/api/quantix/{uid}/autotune` | resultado de auto-tune |

## MQTT (vía CoreX/AgIO, prefijo `agp/quantix/`)

| Topic | Sentido |
|---|---|
| `{uid}/announcement` | ESP→PC (descubrimiento) |
| `{uid}/status_live` | ESP→PC (telemetría) |
| `{uid}/target` | PC→ESP (`{id, pps, seccion_on}`) |
| `{uid}/cmd/...` | PC→ESP (config, calibración, test, OTA) |

## Unidades visibles al operario
Siempre kg/h, sem/m, sem/10m, sem/ha, rpm. **Nunca PPS.**
