# VistaX — Listado de componentes (BOM)

Consolidado del rediseño RC15 → monitor de siembra. Códigos **LCSC reales**
tomados de `reference/BOM-RC15.csv` donde la pieza se conserva o se reusa;
las piezas nuevas llevan especificación + candidato y `LCSC: a confirmar`
(no se inventan códigos).

Parámetro de escala: **N = canales de sensor**. Por defecto **N = 20**
(S1…S14 + M1A/M1B/M2A/M2B + Flow1/Flow2). De esos, 2 (Flow) ya están
equipados con PC817 (U1/U4); **canales nuevos a equipar = 18**.

---

## 1. CONSERVAR (de la RC15, sin cambios)

| Función | Ref | Valor / Parte | Footprint | LCSC |
|---|---|---|---|---|
| MCU | U15 | ESP32-WROOM-32U | XCVR_ESP32-WROOM-32U | C701344 |
| USB-UART | U3 | CH340C | SOIC-16 | C84681 |
| LDO 3V3 | IC1, IC4 | AZ1117 | SOT-223 | C92102 |
| Buck | IC2 | XL2596 | TO263-5L | C74190 |
| Inductor buck | L1 | SMDRI127-330MT | TMPA1265 | C9400 |
| Ethernet | U7 | (C9943) | 21-0041B_8_MXM | C9943 |
| Level-shift I2C | IC3 | PCA9306DCUR | PCA9306DCUR | C33196 |
| ADC (opcional) | U16 | ADS1115IDGS | TSSOP-10 | C37593 |
| Opto Flow (modelo a replicar) | U1, U4 | PC817 | SMD-4 | C66405 |
| Conector Ampseal | J2 | 776087-1 | 7760871 | C378893 |
| Conector | J1 | 105133-0011 | MOLEX_105133-0011 | C586226 |
| Bornera | KF3 | KF142V-5.0-6P | KF142V-5.08-6P | C475138 |
| Bornera tornillo | J8 | Screw_Terminal_01x03 | TerminalBlock PT-1,5-3 | C5188441 |
| Header 1x06 | J7, J10 | Conn_01x06_Socket | PinSocket 1x06 | C40877 |
| Qwiic I2C | J9 | I2C | Qwic_vertical | C160390 |
| Header 1x02 | J3 | Conn_01x02_Pin | PinHeader 1x02 | C7494857 |
| Power pin | J5, J6 | 12V / Ground | PowerPin | — |
| Portafusible entrada | F1 | C492610 | Fuse_Holder | C492610 |
| Diodo | D2 | SS36 | SMA | C2903825 |
| Diodo | D4 | M7 | DO-214AC | C95872 |
| Diodo | D3 | S10MC | D_SMC | C169472 |
| Clamp dual | D5–D8 | BAV99 | SOT-23 | C2500 |
| MOSFET nivel (Flow) | Q1, Q2 | BSS138 | SOT-23 | C255592 |
| LED estado | D1 | LED | LED_0603 | C84263 |
| R 200K | R26 | 200K | R_0603 | C105574 |
| R 2.7K | R1, R2 | 2.7K | R_0603 | C13167 |
| R 2.2K | R5, R6 | 2.2K | R_0603 | C4190 |
| R 500 | R3 | 500 | R_0603 | C22808 |
| R 341 | R24, R25 | 341 | R_0603 | C23138 |
| R 217 | R27, R28 | 217 | R_0603 | C25811 |
| R 120 | R7 | 120 | R_0603 | C22787 |
| R 10K | R4, R22 | 10K | R_0603 | C25804 |
| C 0.1uF | (15×) | 0.1uF | C_0603 | C14663 |
| C 1uF | C4, C8 | 1uF | C_0805 | C28323 |
| C 10uF | C7, C14 | 10uF | C_0805 | C15850 |
| C 22uF | C5, C11, C13 | 22uF | C_0603 | C59461 |
| C 47uF | (varias) | 47uF | CP_Elec | C2836440 |
| C 330uF | C12 | 330uF | CP_Elec 10x10.5 | C3032175 |
| C 470uF | C10 | 470uF | CP_Elec 10x10.5 | C310845 |

> Nota: del grupo 47uF (C2836440) y desacoplos asociados, **conservar sólo**
> los de la entrada/alimentación general; ver "QUITAR".

---

## 2. QUITAR (etapa de salida RC15)

| Función | Ref | Parte | LCSC | Cant. |
|---|---|---|---|---|
| Generador PWM I2C | U6 | PCA9685PW | C2678753 | 1 |
| Driver puente-H | U2, U5, U8, U9, U10, U11, U12, U13, U14 | DRV8870DDA | C86590 | 9 |
| Bulk/decoupling **exclusivos de los drivers** (subconjunto 47uF/0.1uF en rieles de driver) | varias | 47uF / 0.1uF | C2836440 / C14663 | criterio |

---

## 3. AGREGAR — front-end de entradas (sección 2–5 del DESIGN)

### 3.1 Por canal  (× N canales nuevos; por defecto ×18)

| Función | Valor / Parte | Footprint | LCSC | Cant. (N=18) |
|---|---|---|---|---|
| Optoacoplador entrada | PC817 (o array PC847 ×4) | SMD-4 | C66405 (igual que U1/U4) | 18 |
| R limitadora LED | ~1.2 kΩ, 0.25 W (1206 o 2×0603) | R_1206 | a confirmar (0603/1206) | 18 |
| Pull-up colector | 10 kΩ | R_0603 | C25804 (reuso) | 18 |
| Clamp/protección entrada | BAV99 | SOT-23 | C2500 (reuso) | 18 |
| C anti-rebote (opcional) | 1–10 nF | C_0603 | a confirmar | 18 |

### 3.2 Lectura I2C (fijo)

| Función | Parte candidata | LCSC | Cant. |
|---|---|---|---|
| Expansor de entrada I2C #1 | PCF8575 (o MCP23017) | a confirmar | 1 |
| Expansor de entrada I2C #2 (por presupuesto de bits, §6.3) | PCF8575 / MCP23017 (dir. distinta) | a confirmar | 1 |
| Decoupling expansores | 0.1uF | C14663 (reuso) | 2 |

---

## 4. AGREGAR — protección y diagnóstico (DESIGN §6)

| Función | Especificación / candidato | LCSC | Cant. |
|---|---|---|---|
| eFuse / smart high-side switch 12 V con FAULT | Vop ≥ 16 V, I-limit ajustable, salida diagnóstico, autorrecuperable. Cand.: TI TPS1HB08-Q1 / TPS2H160-Q1, Infineon Profet BTS500x | a confirmar | 1 (rama 12 V) |
| eFuse rama 5 V (si hay sensores 5 V) | TI TPS2553 / TPS25200 | a confirmar | 0–1 |
| PTC polyfuse respaldo | 1812, Vmax ≥ 30 V, I_hold ≥ Σ sensores, I_trip < límite eFuse | a confirmar | 1–2 |
| R bias por canal | 10 kΩ–22 kΩ | C25804 (10K, reuso) | × N (≈20) |
| Pull-up FAULT a +3V3 | 10 kΩ | C25804 (reuso) | 1 por flag (≈2) |

---

## 5. Resumen de cantidades (N = 20, 18 canales nuevos)

| Ítem | Cant. |
|---|---|
| PC817 nuevos | 18 (+2 existentes U1/U4) |
| R 1.2 kΩ (LED) | 18 |
| R 10 kΩ (pull-up colector) | 18 |
| R 10–22 kΩ (bias) | 20 |
| BAV99 (clamp entrada) | 18 (+ reuso D5–D8) |
| C 1–10 nF (debounce, opc.) | 18 |
| Expansor I2C | 2 |
| eFuse 12 V c/ FAULT | 1 |
| eFuse 5 V (opc.) | 0–1 |
| PTC 1812 | 1–2 |
| Pull-up FAULT 10 kΩ | ~2 |
| **QUITAR**: PCA9685 | 1 |
| **QUITAR**: DRV8870DDA | 9 |

---

## 6. Pendiente de selección antes de fabricar

Definir según los **sensores de siembra reales** (tensión y consumo):

1. `R_LED` exacta y potencia (recalcular con la tensión de sensor real).
2. Modelo de **eFuse** según corriente total de sensores y tensión (12/24 V).
3. **PTC** (I_hold/I_trip) coherente con el eFuse y con `F1`.
4. **Expansor** (PCF8575 vs MCP23017) y si parte de los canales van a
   **PCNT** nativo del ESP32 por alta tasa de pulso.
5. Códigos **LCSC** definitivos de las piezas nuevas (0603/1206, eFuse, PTC,
   expansor) al cerrar el esquemático en KiCad.

> Este listado es de diseño. La BOM/CPL fabricable se regenera desde el
> esquemático/PCB ya modificados en KiCad (ver `DESIGN.md` §5 y §6.5).
