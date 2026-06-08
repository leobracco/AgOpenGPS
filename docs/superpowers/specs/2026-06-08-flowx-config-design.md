# FlowX — config de cortes, master, 2/3 cables y 2 reguladoras (Fase 1)

Fecha: 2026-06-08
Estado: aprobado (diseño). Fase 1 = solo PC, sin flashear firmware.

## Problema

El operario necesita configurar FlowX de forma simple:
1. **Vincular secciones de PilotX con los cortes de FlowX** para que el corte
   se haga en la sección correcta.
2. **Elegir un corte como "master"** (válvula general): cuando se cierran todas
   las secciones, el master cierra la línea.
3. **Elegir si las electroválvulas son de 2 o 3 cables.**
4. Soportar **2 reguladoras** (motor o válvula) y **2 caudalímetros** por nodo.

Todo configurable, simple y amigable.

## Hallazgo clave (qué necesita firmware y qué no)

- **Mapeo corte→sección, master (las dos formas), 2/3 cables, inversiones** → NO
  necesitan firmware. El bridge ya manda bits por cable; el `MasterPin` dedicado
  ya lo maneja el firmware solo; 2/3 cables (`is3Wire` + `sectionIs3Wire[10]`) y
  inversiones (`invertRelay`/`invertMotor`) ya están completos en firmware+DTO.
- **2 reguladoras independientes** → SÍ necesita firmware. Hoy el firmware lee 2
  caudalímetros (`Sensor[2]`, 2 ISR, PID por-ID) pero el target MQTT solo actúa
  `Sensor[0]` (MQTT_Custom.cpp hardcodea Sensor[0] para pwm/pid; applyTargetLmin
  es de un producto). La 2da línea queda para Fase 2.

Decisión de scope: **Fase 1 = todo PC (sin flashear). Fase 2 = firmware dual-producto (con OK explícito para flashear).**

## Decisiones de definición (del usuario)

- **Master:** soportar las DOS formas por nodo — (A) salida dedicada (firmware) o
  (B) uno de los cortes hace de master. Lógica automática: master abierto si hay
  cualquier sección abierta, cerrado cuando todas cerradas.
- **Reguladoras + caudalímetros:** 2 líneas independientes (reg1+flow1 = producto 0,
  reg2+flow2 = producto 1), que comparten los 7 cortes. Calza con el modelo
  `Productos[]` ya existente en el DTO.
- **Tipo de reguladora:** cada una puede ser motor o válvula.

## Fase 1 — diseño (solo PC)

### 1. Modelo de datos (`FlowXConfigDto`, `flowX.json`)

Por nodo (`FxNodoConfigDto`):
- `master_cable` (int): `-1` = master en salida dedicada (firmware), `0` = sin
  master, `1..N` = ese corte hace de master.

Por producto/reguladora (`FxProductoDto`):
- `tipo` (string): `"valvula"` | `"motor"`.
- `flow_index` (int): `0` | `1` — cuál de los 2 caudalímetros lee.
- `invert_motor` (bool): invertir sentido de esa reguladora (hoy es por-nodo;
  pasa a por-producto; el nodo conserva `invert_motor` para compat de producto 0).

Se mantiene: `cables[].seccion_aog`, `is3wire` global, `section_is_3wire[10]`,
`invert_relay`, dosis/PID/modo-manual por producto.

### 2. Bridge (`FlowXBridge`)

Único cambio de comportamiento:
- **Master como corte:** al armar los bits, si `master_cable` apunta a un corte
  (`>=1`), ese bit = OR de todas las secciones abiertas del nodo. El firmware lo
  trata como un canal normal.
- **Master dedicada (`-1`) / sin master (`0`):** no toca nada (el firmware maneja
  su `MasterPin` con su propia lógica any-open).
- Sigue actuando solo la reguladora 1 (producto 0). La reg 2 se persiste pero se
  actúa en Fase 2.

### 3. UI (`flowx.html` / `flowx.js`)

- **Mapeo de cortes:** cada corte con selector de sección PilotX (reemplaza el
  texto read-only). Botón "auto-asignar" queda como atajo.
- **Master:** un desplegable: `Sin master · Salida dedicada · Corte 1 · Corte 2 …`.
- **Dos reguladoras:** dos tarjetas (Reg 1 / Reg 2): tipo (motor/válvula),
  caudalímetro (1/2), dosis, PID, invertir motor. Reg 2 marcada "se activa en Fase 2".
- **Electroválvulas:** relabel del check global a "Electroválvulas 2/3 cables
  (default)" (hoy dice "caudalímetro", confunde) + override por sección existente +
  invertir electros (NA/NC).
- Paleta PilotX, teclado virtual propio, sin atajos de teclado, nombres de
  producto (no de chip/hardware).

### 4. config-push

Sin cambios en Fase 1: sigue mandando producto 0 (meterCal, is3Wire, invertRelay,
invertMotor, sectionIs3Wire). Fase 2 lo extiende a por-producto.

## Fuera de alcance (Fase 2, con firmware + OK para flashear)

- Actuar la 2da reguladora real (target MQTT multi-producto + 2do lazo PID actuado).
- Que el firmware honre `tipo` (motor/válvula) y `flow_index` por producto.
- config-push por-producto (meterCal/PID de la reg 2).

## Testing

- Build PC con `build.ps1` (0 errores).
- Verificación en vivo: `agp/flow/{uid}/target` con master-como-corte poniendo el
  bit correcto (abrir/cerrar todas las secciones y observar el bit del master).
- UI: guardar config, salir, volver y confirmar que persiste (mismo bug histórico
  snake_case ya cubierto por JsonOutOpts en el controller).
