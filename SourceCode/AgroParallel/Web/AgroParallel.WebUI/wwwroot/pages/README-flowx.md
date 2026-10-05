# FlowX — Interfaz web (Hub PilotX)

Control de corte de secciones y dosis líquida para pulverizadoras. La dosis es
una **válvula motorizada bidireccional** (puente H, abre/cierra), no una bomba;
el PID solo abre y `pwm_min` es la deadband. Cada reguladora = un caudalímetro +
una válvula con PID propio (el firmware soporta 2 reguladoras; hoy actúa la reg. 1).

**Archivos:** `pages/flowx.html` + `js/flowx.js`
**Backend:** `FlowXController.cs` → `FlowXLiveService.cs`
**Persistencia:** config FlowX (CRUD por `/api/flowx/config`)

> Los nodos se descubren por MQTT (`agp/flow/{uid}/announcement`). Hay un botón
> "Agregar descubierto" para importar a la config un nodo visto en LAN.

---

## Qué hace la pantalla

### KPIs en vivo (cabecera)
`renderLive` pinta: caudal real (L/ha), objetivo, dosis, velocidad, PWM bomba,
estado PID (ok / saturado / sin_pulsos / off), secciones activas/total, ancho de barra.

### Insumo ahorrado por corte automático
Tarjeta con área neta, área repintada (overlap), % solapamiento y litros ahorrados
= `(área repintada) × dosis`. Es lo que el corte de secciones ahorra vs una bomba
que tira siempre.

### Nodos FlowX
`renderNodos`: lista nodos LAN con online/uid/ip/firmware/uptime. `pollNodos`
(`/api/flowx/nodos`) los refresca cada 3 s; `findNodo` / `activeNodo` resuelven el
nodo en edición.

### Configuración (6 pestañas — `showTab`)
1. **Nodo activo** — datos generales (uid, nombre, habilitado, eliminar),
   ancho de barra (`btnAnchoFromAog` lo toma de PilotX), modo (auto L/ha vs manual
   L/min), y **PID/actuador por reguladora**: meter_cal (pulsos/L), pwm_min/max,
   Kp/Ki/Kd, caudal fijo manual, pasos ± de botón, invertir motor. Acciones:
   - **Detectar PWM mínimo** → modal manual (`openPwmManual`, `pwmApplyNow`,
     `pwmSetDir`, `pwmRenderLive`, `pwmSave`): el operario aplica PWM, observa
     caudal/pulsos en vivo y guarda el mínimo. Sin nada automático.
   - **Calibrar caudalímetro** (`runCalibrar`), **Auto-tune PID** (`runAutotune`),
     **Barrido auto** avanzado (`runCaracterizar`).
   - Electroválvulas: 3 hilos por defecto, invertir NA/NC, invertir motor válvula.
2. **Productos / Reguladoras** — `renderProductos` / `selectProducto` /
   `readSalidaDetail`: tabla de reguladoras (id, nombre, tipo, caudalímetro,
   dosis L/ha, manual). `+ Agregar reguladora`.
3. **Cortes y secciones** — `renderCortes` / `renderCortesMap` / `autoAssignCortes`
   / `inferNumCortes`: un corte = una válvula física (salida PCA9685). Definís
   cuántos cortes hay y PilotX los reparte entre las secciones. `renderMaster`:
   válvula master (dedicada / corte N / sin master).
4. **Override por sección** — `renderSec3w` / `normalizeSec3w`: hasta 10 secciones
   con tipo de electroválvula (global / 2 cables / 3 cables) distinto al global.
   Botón **Sincronizar config al firmware** (`pushConfigToFirmware`) + **Ping**.
5. **Firmware y herramientas** — OTA: URL del `.bin`, versión, SHA-256 opcional,
   **Push OTA**. El firmware verifica SHA-256 y aborta si hay calibración activa.
6. **Próximamente** — gráfico live, reguladora 2 (dual-producto), prescripciones VR.

### Guardado
`commitEditorToCfg` vuelca el editor a `cfg`; `saveCfg` (POST `/api/flowx/config`)
persiste. Barra sticky con Recargar / Guardar.

---

## Datos en vivo

- `pollLive` → `GET /api/flowx/live`: snapshot por nodo (caudal, PWM, pulsos,
  estado PID, secciones). Alimenta KPIs y el modal de PWM manual.
- `startPolling` / `stopPolling`: pausan al ocultar la pestaña.
- `pollResult` sondea el resultado de calibración/auto-tune/caracterización.
- Modales HTML propios (`askConfirm`, `askText`, `showAlert`) porque WebView2
  desactiva `window.prompt/confirm`.

---

## Endpoints consumidos

| Método | Ruta | Uso |
|---|---|---|
| GET  | `/api/flowx/live` | telemetría |
| GET/POST | `/api/flowx/config` | leer/guardar config |
| GET  | `/api/flowx/nodos` | descubrimiento LAN |
| POST | `/api/flowx/{uid}/cmd?verb=…` | comandos |
| POST | `/api/flowx/{uid}/config-push` | sincronizar config al firmware |
| POST | `/api/flowx/{uid}/caracterizar` | barrido PWM |
| GET  | `/api/flowx/{uid}/{kind}` | resultado calibrar/autotune |

## MQTT (vía CoreX, prefijo `agp/flow/`)

| Topic | Sentido |
|---|---|
| `{uid}/announcement` | ESP→PC |
| `{uid}/status_live` | ESP→PC |
| `{uid}/config` | PC→ESP (meterCal, is3Wire, invertRelay, sectionIs3Wire) |
| `{uid}/cmd/...` | PC→ESP (target, cal, autotune, OTA) |
