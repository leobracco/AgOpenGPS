# Módulo WAS TM171 por CAN (PCB propia) — diseño de sistema

Fecha: 2026-08-31
Estado: aprobado (brainstorming), pendiente de descomposición en planes por subsistema

## Objetivo

Reemplazar el WAS analógico Honeywell (120°, difícil de instalar mecánicamente)
por un sensor de ángulo de rueda basado en el **IMU industrial SYD Dynamics
TM171**, montado en la rueda **sin acople mecánico**. La comunicación a cabina
es por **CAN**, sobre una **PCB propia** (producto Agro Parallel, escalable a
varias máquinas) que hace de puente **TM171(UART) → CAN**.

## Por qué (contexto)

- El Honeywell es un Hall sin contacto (confiable eléctricamente) pero el
  **acople mecánico a la mangueta** es su punto flojo. El IMU se pega a la rueda
  y elimina ese linkage.
- El **TM171** tiene yaw drift de solo 2,6°/25 min (muy superior al BNO085) — es
  el IMU que hace viable usar yaw como ángulo de rueda, con auto-cero de software.
- La máquina ya usa **motor Keya** (CAN). El encoder del Keya mide el motor, no
  el ángulo real de rueda; ese ángulo lo da el WAS de mangueta.

## Decisiones tomadas (brainstorming)

1. **Sensor**: TM171 (comercial, se conecta; no se diseña el IMU).
2. **Comunicación a cabina**: CAN.
3. **PCB propia** (no comprar el adaptador AIO; producto propio, escalable).
4. **PCB "tonta"**: manda yaw/roll/pitch CRUDOS por CAN. El **auto-cero y el
   ángulo de rueda los calcula el CoreX-ECU** (reusa la lógica `bno_was` que ya
   tiene, y que ya cuenta con velocidad y heading del GPS).
5. **Mismo bus CAN que el Keya** (CAN3, 250 kbps), con ID propio.
6. **Micro: ESP32** (TWAI/CAN nativo + UART + WiFi).
7. **Con WiFi/OTA**: el módulo se actualiza/configura por WiFi (integrado al
   ecosistema X-*, OTA por MQTT en la LAN del tractor). En operación va por CAN.

## Datos técnicos verificados

- **Keya CAN** (firmware CoreX `zCANBUS.ino`): bus **CAN3**, **250 kbps**,
  IDs extendidos: comando `0x06000001`, heartbeat/estado `0x07000001`.
- **TM171**: salida UART TTL (3.3/5 V), **115200 bps** (mismo baud que el BNO
  RVC actual, `SerialIMU->begin(115200)`), protocolo binario **EasyProfile**
  (SYD Dynamics publica librería C++ de ejemplo; hay parser `TM171.ino` en la
  comunidad AgOpenGPS, GPL v3, con fix de frames del PR lansalot#1).
- El firmware AIO ya maneja el IMU en décimas de grado: `yawX10`, `rollX10`,
  `pitchX10` (int16). Reusamos esa escala.

## Arquitectura del sistema

```
   RUEDA (gira)                         CABINA
 ┌────────────────────┐            ┌──────────────────────────┐
 │  TM171 (IMU 9ejes) │            │  CoreX-ECU (Teensy 4.1)  │
 │   UART 115200 ─────┼──┐         │   · zCANBUS: Keya +      │
 │                    │  │         │     WAS TM171 (CAN3)     │
 │  PCB propia:       │  │  CAN    │   · fuente WAS           │
 │   ESP32 + TWAI     │  │ 250kbps │     "tm171_can"          │
 │   + CAN xceiver ───┼──┼─────────┼──▶· auto-cero (ya existe)│
 │   + 12V→5/3V3      │  │  bus    │   · PGN UDP → PilotX     │
 │   + WiFi (OTA)     │  │ compart.│                          │
 │   Deutsch DT, IP67 │  │  c/Keya │  ┌────────────────────┐  │
 └────────────────────┘  │         │  │  Motor Keya (CAN)  │  │
                         (mismo bus)│  │  0x06.. / 0x07..   │  │
                                    │  └────────────────────┘  │
                                    └──────────┬───────────────┘
                                               │ Ethernet PGN UDP
                                    ┌──────────▼───────────────┐
                                    │  PilotX (PC/pantalla)    │
                                    │  selector fuente WAS:    │
                                    │  Keya / WAS / YAW /      │
                                    │  TM171-CAN (nuevo)       │
                                    └──────────────────────────┘
```

## Protocolo CAN del WAS (nuevo)

- **Bus**: CAN3, 250 kbps (el del Keya). **ID extendido `0x18FF7201`** (rango
  proprietary tipo J1939, no colisiona con `0x06/0x07000001` del Keya). El ID se
  fija como constante compartida entre el firmware del módulo y el CoreX-ECU.
- **Frame** (8 bytes, ~100 Hz):
  | Bytes | Campo        | Tipo   | Escala        |
  |-------|--------------|--------|---------------|
  | 0-1   | yaw          | int16  | °×10          |
  | 2-3   | roll         | int16  | °×10          |
  | 4-5   | pitch        | int16  | °×10          |
  | 6     | status       | uint8  | bit0=dato_fresco, bit1=tm171_ok |
  | 7     | seq/counter  | uint8  | 0..255 rota   |
- **Fail-safe**: si el TM171 deja de responder (timeout), el módulo sigue
  emitiendo el frame con `status.bit0=0` (dato NO fresco) — el CoreX-ECU al ver
  el bit bajo NO usa el ángulo (evita quedarse con un valor viejo trabado).

## Subsistema 1 — Firmware del módulo (ESP32, PlatformIO)

- Lee UART del TM171 (parser EasyProfile: portar `TM171.ino` de la comunidad,
  incluido el fix de frames oversize del PR lansalot#1; verificar el `+2` de
  footer contra el protocolo real).
- Extrae yaw/roll/pitch, arma el frame CAN (`0x18FF7201`) y lo emite por TWAI a
  ~100 Hz.
- Watchdog del TM171 → `status` fail-safe.
- WiFi: OTA + config integrados al ecosistema X-* (announce/OTA por MQTT en LAN;
  reusar el patrón de los nodos X-* existentes). En operación normal, el WiFi no
  es necesario para el WAS.
- Modo seguro: sin TM171 válido, no inventar ángulo.

## Subsistema 2 — PCB (hardware, se materializa en KiCad)

Esta spec define el circuito; el layout/Gerbers se hacen en KiCad (fuera del
alcance de código). Componentes:
- **MCU**: ESP32 (WROOM-32 o C3; C3 alcanza y es más barato — TWAI + UART + WiFi).
- **CAN**: transceiver SN65HVD230 (3.3 V) o TJA1051 (5 V), terminación 120 Ω
  conmutable por jumper (el módulo puede ser fin de bus).
- **TM171**: header/conector para su cable UART + alimentación (5 V). No se
  aloja el chip; el TM171 es módulo comercial con su caja.
- **Alimentación**: 12 V (24 V-tolerante) → 5 V (buck, p/ TM171 y transceiver
  si TJA1051) → 3.3 V (LDO/buck p/ ESP32). Protección: fusible, diodo/ideal-diode
  reversa, TVS en power y en CAN_H/CAN_L, filtro EMI.
- **Conector**: Deutsch DT sellado (power + CAN_H + CAN_L + GND). Caja IP67.
- **Debug**: header UART/USB para flasheo/serial en banco.
- **BOM** objetivo: bajo costo, componentes JLCPCB-stock para ensamblado.

## Subsistema 3 — Firmware CoreX-ECU (Teensy)

- En `zCANBUS`: además del Keya, filtrar/recibir el ID `0x18FF7201`, decodificar
  yaw/roll/pitch + status.
- Nueva **fuente WAS `tm171_can`**: alimenta el mismo pipeline de ángulo +
  auto-cero que hoy usa `bno_was` (que ya toma velocidad/heading del GPS). Si
  `status` dice dato no-fresco o el frame no llega (stale), degradar seguro.
- Persistir la fuente en EEPROM (como las otras) y exponerla por `/api/wassrc`
  (`options` ya es dinámico — sumar el valor).

## Subsistema 4 — UI PilotX (C#/Avalonia)

- `CoreXEcuConfigDto.WasSource`: aceptar `"tm171_can"` (doc del enum).
- `CoreXEcuPanel`: opción en el segmented de fuente WAS (etiqueta operable, ej.
  "TM171"). DTOs/service ya son tolerantes a fuentes nuevas vía `options[]`.

## Estrategia: banco primero, cobre después

Orden recomendado para no fabricar una PCB sobre supuestos:
1. **Validar en banco** con ESP32 devkit + transceiver CAN barato + TM171 real:
   TM171 → ESP32 → CAN → CoreX-ECU → auto-cero → autosteer. Ajustar el protocolo
   CAN, la escala, y observar cómo se comporta el auto-cero.
2. **Congelar** el diseño de la PCB recién con el flujo validado.
3. **Fabricar** (JLCPCB) y probar el módulo real en la máquina.

## Descomposición en sub-proyectos (cada uno su plan)

- **P1 — Firmware módulo ESP32** (banco): parser TM171 + frame CAN. Ejecutable
  por Claude (PlatformIO). *Primer plan.*
- **P2 — Firmware CoreX-ECU**: recibir CAN + fuente `tm171_can` + auto-cero.
  Ejecutable por Claude (verificar acceso al fuente del CoreX-ECU actual).
- **P3 — UI PilotX**: opción en el selector WAS. Ejecutable por Claude.
- **P4 — PCB**: esquemático + BOM + layout + Gerbers. **Se hace en KiCad**
  (Claude especifica y arma el esquemático conceptual/BOM; el layout lo hace un
  diseñador o una pasada de KiCad). No ejecutable como código puro.
- **P5 — WiFi/OTA del módulo**: integración al ecosistema X-* (segunda etapa).

P1+P2+P3 forman el MVP de banco (validación end-to-end sin PCB). P4 sigue una
vez validado. P5 es incremental.

## Riesgos / pendientes

- **Protocolo TM171 exacto**: confirmar el frame EasyProfile (offsets de
  yaw/roll/pitch, footer/checksum) con el sensor en mano o la librería SYD.
- **Auto-cero con implemento**: el recentrado del cero puede errar con ruedas a
  ángulo residual (implemento que empuja de costado). Calibrar en banco/campo;
  es lo que hay que vigilar antes de declarar "anda".
- **Montaje mecánico en la rueda**: llevar 12 V + CAN a una pieza que gira
  (ruteo con servicio de cable). Es el desafío físico, no el electrónico.
- **Acceso al fuente del CoreX-ECU**: la nota de proyecto dice "firmware 1.00,
  no flashear más", pero los DTOs hablan de v1.14+. Aclarar qué corre hoy y
  dónde está el fuente antes de tocar P2.
- **Orientación del TM171 en la rueda**: definir qué eje del sensor mapea al
  ángulo de dirección según el montaje (kingpin casi vertical → yaw).
```
