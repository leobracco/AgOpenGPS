# PCB Design Brief — Agro Parallel "WAS-TM171 CAN Bridge" Module

**Client:** Agro Parallel (Leonardo Bracco)
**Date:** 2026-08-31
**Rev:** A (initial brief)
**Contact for questions:** Leonardo Bracco

---

## 1. Project overview

We are building a **wheel angle sensor (WAS) module** for agricultural
autosteer. A commercial industrial IMU (**SYD Dynamics TM171**) is mounted on
the steering knuckle of a tractor wheel. This PCB is the **bridge**: it reads
the TM171 over UART and transmits its orientation data over **CAN bus** to the
steering ECU in the cabin.

The PCB does **no math** — it forwards raw yaw/roll/pitch. All calibration
happens in the receiving ECU. Think of it as a rugged UART→CAN gateway with
Wi-Fi for firmware updates.

This is a **product** (not a one-off): it will be installed on multiple
machines, so design for repeatable manufacturing (JLCPCB fab + SMT assembly).

## 2. System context

```
   WHEEL (rotating with steering)              CABIN
 ┌───────────────────────────────┐       ┌─────────────────────────┐
 │  TM171 IMU (commercial unit)  │       │  Steering ECU           │
 │  UART TTL 115200 ────────┐    │       │  (Teensy 4.1, existing) │
 │                          │    │  CAN  │  listens to this module │
 │  ┌────────────────────┐  │    │ 250k  │  + Keya steering motor  │
 │  │ THIS PCB           │◄─┘    │ shared│  on the same bus        │
 │  │ ESP32 + CAN xcvr   │───────┼───────┤                         │
 │  │ 12/24V input       │       │  bus  │                         │
 │  └────────────────────┘       │       └─────────────────────────┘
 └───────────────────────────────┘
```

- The CAN bus is **shared with a Keya steering motor** (extended IDs
  0x06000001 / 0x07000001, **250 kbps**). Our module transmits on extended ID
  **0x18FF7201** at ~100 Hz (one 8-byte frame). Bus load is trivial.
- The module lives **on the axle/wheel area** of a farm tractor: vibration,
  dust, water, mud, 12 V or 24 V electrical system with automotive transients.

## 3. Functional requirements

| # | Requirement |
|---|---|
| F1 | Read TM171 via UART (TTL, 115200 8N1; sensor is 3.3 V / 5 V logic compatible) |
| F2 | Transmit on CAN 2.0B (extended ID), 250 kbps, ~100 frames/s |
| F3 | Wi-Fi (station mode) for OTA firmware updates & configuration — normal operation does NOT need Wi-Fi |
| F4 | Power from tractor battery: 12 V nominal, must tolerate 24 V systems → design input range 9–32 V |
| F5 | Power the TM171 sensor from this board: 5 V, 80 mA typical (budget 150 mA) |
| F6 | Status LEDs visible through enclosure: power / CAN activity / IMU data / Wi-Fi |
| F7 | Field-serviceable programming: UART program header (BOOT/EN accessible) |

## 4. Electrical design requirements

### 4.1 MCU — ESP32

- **ESP32-WROOM-32E** module (16 MB not needed; 4 MB flash fine).
  Rationale: built-in TWAI (CAN) controller, UART, Wi-Fi, and it is the
  standard MCU across the rest of our product line (shared firmware tooling).
  If you have a strong reason to prefer ESP32-C3 (cost/size), flag it — C3 is
  acceptable (it also has TWAI), but WROOM-32E is the default.
- Standard support circuitry: EN RC reset, BOOT button + EN button (or pads),
  strapping-pin care (GPIO0/2/12/15), 3.3 V decoupling per Espressif hardware
  design guidelines.
- **Antenna keep-out** respected per WROOM datasheet — see §7 enclosure note
  (plastic enclosure, antenna area away from ground pour and cable glands).

### 4.2 CAN interface

- Transceiver: **TJA1051T/3** (3.3 V VIO variant, 5 V supply) preferred for
  automotive robustness; **SN65HVD230** (3.3 V) acceptable alternative.
- **120 Ω split termination** (2×60 Ω + cap) selectable by solder jumper or
  2.54 mm jumper — the module may or may not be the end of the bus.
- Protection on CAN_H/CAN_L: dedicated CAN TVS (e.g. PESD1CAN or NUP2105L).
- Optional but welcome: common-mode choke footprint (DNP by default).

### 4.3 Power supply

- Input **9–32 V DC** (12 V/24 V tractor systems).
- Protection chain (automotive environment, ISO 7637-2 style transients):
  - resettable polyfuse or fuse,
  - reverse polarity protection (P-FET preferred over series diode),
  - input TVS (e.g. SMBJ33A) + bulk capacitance,
  - LC/ferrite input filter for EMI.
- Rails:
  - **5 V buck** (≥500 mA): feeds TM171 (150 mA budget) + transceiver (if TJA1051).
    A sync buck (MP2315/TPS54331 class) — avoid linear from 24 V.
  - **3.3 V** (≥600 mA peak — ESP32 Wi-Fi bursts): buck or LDO from the 5 V rail
    (LDO from 5 V is fine: ~0.5 W worst case).
- Power budget: ESP32 Wi-Fi burst ~450 mA @3.3 V + TM171 150 mA @5 V +
  transceiver ~70 mA ⇒ ~2.5 W absolute worst case, <1 W typical (Wi-Fi idle).

### 4.4 TM171 sensor interface

- Connector on our PCB: **4-pin** → 5 V, GND, UART_TX (ESP32→TM171),
  UART_RX (TM171→ESP32).
- TM171 facts (verify against SYD Dynamics TM171 datasheet before finalizing):
  - UART TTL, **3.3 V and 5 V compatible** (per manufacturer) → direct 3.3 V
    connection to ESP32 expected to be fine; if the datasheet shows 5 V-only
    drive, add a level shifter footprint (DNP default).
  - Power: **80 mA @ 5 V typical**. Supply range to confirm in datasheet —
    if it accepts 3.3 V we still feed it 5 V for margin.
  - Data rate configurable 1–800 Hz; we will run 100 Hz.
- Connector type: see §6 (sealed).

### 4.5 Debug / programming

- **UART0 program header**: 6-pin 2.54 mm (3V3, GND, TX0, RX0, EN, IO0), or
  our preferred: header compatible with a standard USB-UART adapter with
  DTR/RTS auto-download (footprint for the classic 2-transistor auto-boot
  circuit if a USB bridge is included).
- Optional: on-board USB-C + CP2102N/CH340K for bench convenience — nice to
  have, not required (adds cost; the header is the requirement).
- 4–6 test points: 3V3, 5V, GND, CAN_H, CAN_L, TM171_RX.

### 4.6 LEDs

| LED | Color suggestion | Driven by |
|---|---|---|
| PWR | green | 3.3 V rail |
| CAN | yellow | GPIO (firmware blinks on TX) |
| IMU | blue | GPIO (blinks on valid TM171 frames) |
| WIFI | white | GPIO |

Low-current (2 mA) LEDs, visible through enclosure (light pipes or clear lid).

## 5. Proposed GPIO map (ESP32-WROOM-32E)

Firmware is ours; this map is a proposal — adjust for layout convenience but
avoid strapping pins for outputs and keep UART2 free of boot noise:

| Signal | GPIO | Note |
|---|---|---|
| TWAI TX (CAN) | GPIO 4 | |
| TWAI RX (CAN) | GPIO 5 | |
| TM171 UART2 RX | GPIO 16 | |
| TM171 UART2 TX | GPIO 17 | |
| LED CAN | GPIO 25 | |
| LED IMU | GPIO 26 | |
| LED WIFI | GPIO 27 | |
| Term-sense (optional, reads jumper) | GPIO 34 | input-only pin, optional |

## 6. Connectors & mechanical

- **Field connector (power + CAN): Deutsch DT04-4P** (or DT13-4P receptacle):
  pins = +BATT, GND, CAN_H, CAN_L. If a panel-mount DT is impractical for the
  enclosure, a cable pigtail through an IP68 gland with an inline DT connector
  is acceptable — state which you choose.
- **TM171 connector**: sealed 4-pin, smaller (e.g. M8 4-pin, or JST-GH pigtail
  through a gland). The TM171 ships with its own cable; we will crimp to match.
- **Board size target:** ≤ 70 × 50 mm, 4× M3 mounting holes.
- **Enclosure:** IP67 **plastic** (Wi-Fi must radiate — no metal box), e.g.
  Hammond 1554/1555 series or equivalent. PCB antenna area oriented away from
  mounting plate. If you prefer an external antenna (u.FL + bulkhead), propose
  it — acceptable if the gland is IP67.
- Vibration: this rides on an axle. No tall unsupported parts, heavy parts
  (inductors, connectors) with generous pads/mechanical anchoring, consider
  conformal coating (we can coat after assembly; leave connectors/antenna masked).

## 7. Environmental & compliance targets

- Operating temp: **−20 °C … +70 °C** (cabinless axle mount, summer sun).
- Automotive transients on supply (ISO 7637-2 pulses as design guidance;
  no formal certification required).
- EMC: good layout practice (CAN pair routed differentially, buck loop area
  minimized); no formal CE/EMC lab run for prototypes.

## 8. CAN protocol (context, firmware side — for your reference only)

8-byte frame, extended ID 0x18FF7201 @ 100 Hz:
bytes 0-1 yaw (int16, °×10) · 2-3 roll · 4-5 pitch · 6 status bits · 7 rolling counter.
This does not affect the PCB beyond "CAN 2.0B extended, 250 kbps".

## 9. Manufacturing

- **Fab + SMT: JLCPCB.** Please choose JLCPCB-stocked (preferably "Basic")
  parts where reasonable; flag any Extended/out-of-stock part in the BOM.
- 2-layer is acceptable if EMC layout is clean; 4-layer welcome if it makes
  the buck + CAN layout better (cost delta is small).
- Deliverables should be directly orderable: Gerbers + BOM + CPL in JLCPCB
  format.
- Prototype run: **5 boards assembled**.

## 10. Deliverables requested

1. **Schematic** — KiCad source + PDF.
2. **Layout** — KiCad source.
3. **Gerbers + drill** ready for JLCPCB.
4. **BOM** (with LCSC part numbers) + **CPL** (pick&place) for JLCPCB SMT.
5. Short **design notes**: power budget check, protection rationale, antenna
   placement note, anything you deviated from this brief and why.
6. (After our review) order files or the order itself for 5 assembled protos.

## 11. Open questions for Alok

1. TJA1051T/3 vs SN65HVD230 — your call with rationale (supply rails differ).
2. Panel-mount Deutsch vs pigtail+gland — which fits the enclosure best?
3. PCB antenna vs external u.FL antenna through the lid?
4. USB-C debug bridge on-board: worth the ~$1.5, or header only?
5. Any concern with 2-layer for the buck at 24 V input?
6. Confirm TM171 supply range from its datasheet (we could not confirm the
   min/max supply — only "80 mA @ 5 V typical").

## 12. References

- TM171 product page: https://www.syd-dynamics.com/transducerm_tm151-tm171/
  (UART TTL 3.3/5 V compatible, 80 mA @ 5 V, 40×34×12.6 mm, M3 holes,
  1–800 Hz output, yaw drift 2.6°/25 min)
- Commercial adapter we are replacing with our own design (context/inspiration):
  TM171 + AIO 4/5 adapter — navisklep.pl product "TM171 transductor dinámico
  SYD AHRS IMU con adaptador AIO 4/5 plug&play"
- CAN bus peer device: Keya CAN steering motor, 250 kbps, ext IDs 0x06000001 /
  0x07000001 (our module must coexist; our TX ID 0x18FF7201).
- ESP32 hardware design guidelines (Espressif) — WROOM-32E antenna keep-out.
- Similar community module (firmware reference only, no PCB): lansalot's
  AgOpenGPS TM171-as-WAS repos on GitHub.

---

*Documento interno Agro Parallel — spec de sistema completa en*
`docs/superpowers/specs/2026-08-31-was-tm171-can-modulo.md`.
