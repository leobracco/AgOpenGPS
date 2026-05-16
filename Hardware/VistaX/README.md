# VistaX node (hardware)

VistaX es un **nodo monitor de siembra** para AgOpenGPS, derivado del PCB
**RC15** del proyecto oficial de Rate Control
(`AgOpenGPS-Official/Rate_Control` → `ESP32/RC15`).

La idea: reutilizar la electrónica base de la RC15 (ESP32-WROOM-32U,
alimentación, Ethernet/USB, conector Ampseal de 23 pines, y el front-end de
sensor por **optoacoplador PC817** que ya trae para Flow1/Flow2) pero
**quitando la etapa de salida** (PCA9685 + 9× DRV8870) y reemplazándola por
**entradas aisladas por optoacoplador** para leer los sensores de semilla.

> Este repositorio es el **software de escritorio AgOpenGPS (C#/.NET)**.
> `Hardware/VistaX/` es una carpeta de hardware agregada; no afecta la
> compilación del software. El firmware del nodo es propio del usuario y
> queda fuera de alcance.

## Contenido

| Ruta | Qué es |
|---|---|
| `kicad/VistaX.kicad_pro/.kicad_prl` | Proyecto KiCad (RC15 renombrado a VistaX) |
| `kicad/VistaX.kicad_sch` | Hoja raíz (ESP32, PC817 Flow, jerarquía) |
| `kicad/DRVs.kicad_sch` | Etapa de salida RC15 **a reemplazar** (PCA9685 + DRV8870) |
| `kicad/Analog.kicad_sch` | Acondicionamiento analógico / ADS1115 (se conserva, opcional) |
| `kicad/Ethernet.kicad_sch` | Ethernet (W5500) — sin cambios |
| `kicad/VistaX.kicad_pcb` | PCB de la RC15 **renombrada** (ruteo intacto, ver límites) |
| `reference/` | PDFs de pinout/instalación/esquemático, foto y BOM **originales de la RC15** (sólo consulta) |
| `DESIGN.md` | Diseño del front-end de entradas por optoacoplador + guía de implementación en KiCad |

## Estado / alcance (importante, leer)

Hecho de forma confiable en este commit:

- Proyecto KiCad copiado y **rebrandeado** RC15 → VistaX: nombre de proyecto
  en todas las instancias de símbolo (212 ocurrencias), bloques de título
  del esquemático y del PCB, y serigrafía de la placa (`VistaX`).
- Documentación de ingeniería del rediseño (`DESIGN.md`): topología por
  canal, BOM delta, mapeo de conector y pines, y pasos en KiCad.
- Capa de protección/diagnóstico (`DESIGN.md` §6): fusible reseteable
  (eFuse con flag + PTC de respaldo, por grupo) y detección de
  abierto/corto por canal (bias + clasificación en firmware).
- PDFs/BOM originales de la RC15 conservados como referencia.

**NO** hecho (requiere KiCad y una persona — ver `DESIGN.md`):

- El `.kicad_pcb` (~1 MB, multicapa ya ruteado) **NO** fue modificado
  eléctricamente: sólo se rebrandeó. Quitar PCA9685/DRV8870, colocar la
  matriz de PC817 + expansor I2C y re-rutear/re-verter zonas debe hacerse
  en KiCad. Editar netlist/ruteo a mano corrompería la placa.
- El esquemático **no** fue re-cableado: se entrega el diseño y los pasos,
  no la modificación de netlist aplicada.
- Los gerbers/CPL de la RC15 **no** se incluyen: deben regenerarse desde el
  PCB ya modificado (los originales serían incorrectos para VistaX).

Ver `DESIGN.md` para el diseño completo y la guía paso a paso.
