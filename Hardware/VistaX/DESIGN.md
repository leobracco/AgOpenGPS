# VistaX — Rediseño RC15 → monitor de siembra (entradas por optoacoplador)

Objetivo: reutilizar la RC15 como **nodo de monitoreo de siembra**. Se
**elimina la etapa de salida** (PWM + drivers de motor que mueven válvulas
de sección/caudal) y se reemplaza por **entradas digitales aisladas por
optoacoplador** para contar pulsos de sensores de semilla.

---

## 1. RC15 tal cual (lo que se reemplaza)

Extraído del esquemático/BOM de la RC15:

- **MCU**: `ESP32-WROOM-32U` (U15).
- **Etapa de salida** (a quitar):
  - `PCA9685PW` (U6): generador PWM I2C de 16 canales. Bus `SDA`/`SCL`
    (con level-shifter `PCA9306` IC3). Net `OutputEnable` = `/OE` del PCA9685.
  - `DRV8870DDA` ×9 (U2, U5, U8, U9, U10, U11, U12, U13, U14): puentes H
    que generan las salidas del conector:
    - Secciones: `S1A/S1B … S7A/S7B` (modo *paired*) o `S1…S14` (modo
      *single*, ver `reference/RC15-pinout.pdf`).
    - Motores de válvula: `M1A/M1B`, `M2A/M2B`, comandados por
      `M1_IN1/M1_IN2`, `M2_IN1/M2_IN2`.
- **Front-end de sensor aislado YA existente** (modelo a replicar):
  - `PC817` ×2 (U1, U4) en la hoja raíz: `FlowPin1/FlowPin2` (pines de
    conector tolerantes hasta 12 V) → opto → `FlowSensor1/FlowSensor2`
    hacia el ESP32. **Esta es exactamente la celda de entrada que VistaX
    necesita, instanciada N veces.**
- **Se conserva**: ESP32 (U15), alimentación (`XL2596` IC2, `AZ1117`
  IC1/IC4, fusible F1, protección D2/D4), Ethernet (`W5500` U7 + magnética),
  USB (`CH340C` U3), `PCA9306` IC3 (sigue haciendo falta para el bus I2C),
  ruta analógica `ADS1115` (U16, opcional para tasa/presión/eje), conector
  **Ampseal TE 770680-1** (23 pines), y las celdas PC817 de Flow (U1/U4).

---

## 2. Arquitectura VistaX (monitor de siembra)

Sin actuadores. Cada pin del Ampseal que antes era salida de sección/motor
pasa a ser **entrada de sensor de semilla aislada**.

```
 Sensor de semilla (óptico/inductivo, NPN open-collector o pulso 12V)
   │  (pin del conector Ampseal: S1..S14, M1A/M1B, M2A/M2B)
   ▼
 [R_LED] ──►├ LED PC817 ──┤◄ (cátodo a retorno/GND, BAV99 protección inversa)
                 ╎  (aislación galvánica)
            ┌────╎───────────────┐
            │  Fototransistor PC817
   +3V3 ──[10k]──┬── COLECTOR ───────►  bit de expansor I2C (o GPIO ESP32)
                 │  EMISOR ── GND
            (opcional C 1–10 nF + R serie = debounce/EMI)
            └────────────────────┘
```

- **Aislación**: idéntica topología al front-end Flow de la RC15 (PC817).
  Pulso activo-bajo open-collector; conteo/inversión en el firmware
  (firmware propio del usuario).
- **Lectura**: el ESP32 no tiene ~16 GPIO libres. Dos opciones:
  1. **Expansor I2C de entrada** (`PCF8575` o `MCP23017`, 3V3) sobre el bus
     `SDA`/`SCL` ya existente (a través del `PCA9306`). Línea `INT` del
     expansor → un GPIO libre del ESP32 (reutilizar el net `OutputEnable`
     que queda libre al sacar el PCA9685). Conteo por interrupción de
     cambio. Suficiente para sembradoras a velocidad normal.
  2. **GPIO nativos + PCNT**: para tasas de pulso altas, llevar un
     subconjunto de canales a GPIO del ESP32 y usar las unidades PCNT
     (pulse counter) por hardware. Combinable con la opción 1.

### Dimensionamiento por canal

- `R_LED`: con sensor de 12 V e `I_F ≈ 8–10 mA` →
  `R_LED ≈ (12 − 1.2 V) / 0.009 ≈ 1.2 kΩ`. Disipación a 12 V continuos
  ≈ 0.1 W → usar 1206 / 0.25 W (o 2× 0603 en serie). Para sensores de 5 V,
  `R_LED ≈ 330–470 Ω`.
- Pull-up colector: `10 kΩ` a +3V3 (valor ya presente en la BOM, clase
  R4/R22).
- Protección de entrada: `BAV99` (ya en BOM, C2500) en clamp / antiparalelo
  para sensores bipolares o transientes; opcional R serie 100–470 Ω.
- Anti-rebote: `C 1–10 nF` colector-GND + filtrado por firmware
  (ventana de tiempo / Schmitt).

---

## 3. BOM delta (vs `reference/BOM-RC15.csv`)

**QUITAR**

| Ref | Componente | LCSC |
|---|---|---|
| U6 | PCA9685PW | C2678753 |
| U2, U5, U8, U9, U10, U11, U12, U13, U14 | DRV8870DDA ×9 | C86590 |
| Desacoplos/bulk exclusivos de los drivers (p. ej. 47 µF C44/C45… en rieles de driver) | conservar sólo el bulk de entrada de la placa | — |

**AGREGAR**

| Componente | Sugerencia | Nota |
|---|---|---|
| PC817 | hasta ×16/18 (o arrays PC847) | mismo footprint SMD-4 que U1/U4 (C66405) |
| Expansor I2C | PCF8575 o MCP23017, 3V3 | sobre `SDA`/`SCL` (vía PCA9306), `INT`→GPIO libre |
| `R_LED` ~1.2 kΩ 0.25 W | 1 por canal | 1206 o 2×0603 |
| Pull-up 10 kΩ | 1 por canal | ya en BOM (C25804) |
| C debounce 1–10 nF | opcional, 1 por canal | — |

**CONSERVAR**: U15 (ESP32), IC2/IC1/IC4/F1/D2/D4 (alimentación+protección),
U7 (Ethernet)+magnética, U3 (CH340C/USB), IC3 (PCA9306), U16 (ADS1115,
opcional), conector Ampseal, U1/U4 (PC817 Flow — quedan como 2 canales más).

---

## 4. Mapeo de conector (Ampseal TE 770680-1, 23 pines)

Base: `reference/RC15-pinout.pdf`, columna **"PCA9685 Single"**. Los pines
de sección/motor se reasignan a canales de sensor de semilla; potencia y
Flow no cambian.

| Pin RC15 (señal) | Función RC15 | Función VistaX | Canal sensor |
|---|---|---|---|
| S1 … S14 | salida sección | entrada sensor aislada | CH1 … CH14 |
| M1A, M1B, M2A, M2B | salida motor válvula | entrada sensor aislada | CH15 … CH18 |
| Flow1, Flow2 | entrada caudal (PC817 U1/U4) | entrada sensor/eje | CH19, CH20 |
| 5V, 12V, GND | alimentación | sin cambios | — |

Cada CHn ↔ un PC817 ↔ un bit del expansor I2C (o un GPIO+PCNT del ESP32).
Mantener la asignación de bits documentada para el firmware propio.

---

## 5. Implementación en KiCad (completar el rediseño parcial)

1. Abrir `kicad/VistaX.kicad_pro` en **KiCad 7+**.
2. `DRVs.kicad_sch`: borrar **U6** (PCA9685) y **U2/U5/U8/U9/U10/U11/U12/
   U13/U14** (DRV8870) más sus desacoplos exclusivos; eliminar los nets PWM
   que quedan colgando.
3. Nueva hoja `Inputs.kicad_sch`: copiar la celda PC817 de la hoja raíz
   (U1/U4) y replicarla por canal. Lado LED ← etiqueta global del pin
   correspondiente (`S1..S14`, `M1A/M1B`, `M2A/M2B`) ahora como **entrada**;
   lado fototransistor → bit del expansor; pull-ups a `+3V3`.
4. Agregar el símbolo del expansor (PCF8575/MCP23017): cablear `SDA`/`SCL`
   (etiquetas globales existentes) a través del `PCA9306`; `INT` → net
   `OutputEnable` (queda libre) o un GPIO de reserva; 0.1 µF de desacoplo.
5. Hoja raíz: incluir la nueva hoja; quitar el uso de `OutputEnable` como
   `/OE`; conservar ESP32, alimentación, Ethernet, USB.
6. Anotar, correr **ERC**, asignar footprints (PC817 = SMD-4 ya en libs;
   expansor TSSOP).
7. `VistaX.kicad_pcb`: borrar los footprints eliminados, colocar la matriz
   PC817 + expansor, **re-rutear**, re-verter zonas, correr **DRC**.
8. Regenerar **gerbers/CPL/BOM** para VistaX (no reutilizar los de la RC15).
9. Actualizar serigrafía de revisión y exportar el nuevo pinout PDF.

## 6. Protección y diagnóstico (fusible reseteable + detección de corto)

Esquema elegido: **dos capas complementarias**. La capa de grupo (eFuse +
PTC) protege el hardware y detecta el corto "duro" de alimentación; la capa
por canal (bias + firmware) dice **qué fila** está abierta o en corto. Son
complementarias por una restricción física: el Ampseal tiene un único pin
12 V / 5 V / GND → la protección de potencia es **por grupo**, no por fila.

### 6.1 Capa de grupo — eFuse con flag (primario) + PTC (respaldo)

- **eFuse / smart high-side switch** en la salida de 12 V hacia los sensores:
  límite de corriente ajustable + salida de diagnóstico (FAULT/SENSE) +
  autorrecuperable. Requisitos: `V_op ≥ 16 V` (12 V + margen), límite
  ajustable, flag digital. Candidatos: TI `TPS2H160-Q1` / `TPS1HB08-Q1`
  (4–40 V, current-limit + diagnóstico) o Infineon Profet `BTS500x/BTS3xxx`.
  Para la rama de 5 V (si hay sensores de 5 V): `TPS2553` / `TPS25200`.
- **PTC polyfuse de respaldo** en el mismo riel (defensa en profundidad,
  por si el eFuse falla): serie 1812, `V_max ≥ 30 V`, `I_hold ≥ Σ` corriente
  de régimen de los sensores, `I_trip` por debajo del límite del eFuse y del
  fusible de entrada `F1` existente.
- **FAULT** (open-drain, activo-bajo) → pull-up a `+3V3` → a un **bit libre
  del expansor I2C** (o GPIO de reserva del ESP32). 1 bit por grupo de
  alimentación (~2 bits: rama 12 V y rama 5 V). Latcheado/auto-retry según
  el componente; el firmware lo expone en la UI.

### 6.2 Capa por canal — bias + clasificación en firmware

Una resistencia de bias por canal (10 k–22 k) referida a la `V+` de sensor
(post-eFuse) fija el estado de reposo, de modo que abierto y corto den
firmas distintas y el firmware clasifique cada fila:

| Estado de la línea | ¿Pulsos? | FAULT eFuse | Interpretación |
|---|---|---|---|
| Conmuta alto↔bajo | sí | no | Sensor **OK** |
| Fijo en `V+` | no | no | **Abierto**: cable cortado / sensor desconectado |
| Fijo en `0 V` | no | no | **Corto a GND** (lado señal) o sensor saturado |
| Cualquiera | — | **sí** | **Corto/sobrecorriente** en la alimentación del grupo |

Ambigüedad residual (honesto): "fijo en bajo sin FAULT" no separa de forma
100 % fiable un corto de señal de un sensor pegado en conducción; mitigar
evaluando sólo durante siembra activa y apoyándose en el FAULT del eFuse
para el corto de alimentación. La polaridad de la tabla depende del tipo de
sensor (NPN/PNP) y debe coincidir con la celda PC817; se ajusta en firmware.

### 6.3 Impacto en el presupuesto de bits del expansor

20 canales (S1..S14 + M1/M2 + Flow1/2) + ~2 bits de FAULT → un solo
`PCF8575` (16 bits) **no alcanza**. Opciones: **2× PCF8575** (32 bits, deja
reserva), o `PCF8575` + `MCP23017`, o llevar los canales de alta tasa a
GPIO nativos del ESP32 con **PCNT** y dejar el resto + FAULT en el expansor.

### 6.4 BOM delta adicional (sobre la sección 3)

| Componente | Cant. | Nota |
|---|---|---|
| eFuse/smart high-side (12 V, c/ FAULT) | 1 (rama 12 V) | + 1 para rama 5 V si aplica |
| PTC polyfuse 1812, Vmax ≥ 30 V | 1–2 | respaldo por riel |
| R bias 10 k–22 k | 1 por canal | clasificación abierto/corto |
| R pull-up FAULT 10 k a +3V3 | 1 por flag | a bit de expansor |
| 2.º expansor I2C (o MCP23017) | 1 | por presupuesto de bits (6.3) |

### 6.5 Pasos KiCad adicionales

1. En la hoja de potencia/entrada: insertar el eFuse entre la `V+` de
   sensor del conector y la distribución a los PC817; ajustar `R_ILIM`
   según corriente total; añadir el PTC en serie como respaldo.
2. Llevar `FAULT` (con pull-up a `+3V3`) a un bit libre del 2.º expansor.
3. En `Inputs.kicad_sch`: agregar la R de bias por canal a `V+` post-eFuse.
4. Instanciar el 2.º expansor I2C en `SDA`/`SCL` (vía `PCA9306`); decoupling
   0.1 µF; dirección I2C distinta del primero.
5. Revisar caída de tensión total (eFuse R_on + PTC) sobre la `V+` de
   sensor, sobre todo para sensores de 5 V.
6. ERC/DRC y regenerar producción (igual que sección 5, pasos 6–9).

## 7. Límites (honestidad de ingeniería)

- El `.kicad_pcb` entregado es la RC15 **renombrada**, ruteo intacto: no se
  modificó eléctricamente. El cambio de netlist y el re-ruteo deben hacerse
  en KiCad por una persona; hacerlo por edición de texto del S-expression
  rompería el DRC y daría una placa no fabricable.
- Verificar tensiones reales de los sensores de siembra usados (5/12/24 V) y
  recalcular `R_LED`/protección antes de fabricar.
- Validar carga del bus I2C y velocidad máxima de pulso vs. método de
  lectura (expansor por interrupción vs. PCNT nativo).
- La detección de corto por canal (6.2) es **inferencia por firmware** con
  ambigüedad residual; sólo el FAULT del eFuse (6.1) detecta el corto de
  alimentación de forma inequívoca, y a nivel de **grupo**, no de fila
  (limitación del conector Ampseal de pin de potencia compartido).
