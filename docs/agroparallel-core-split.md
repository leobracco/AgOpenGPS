# Agro Parallel Core split

This branch starts the migration path to separate the engine from the visual layer.

## Goal

Keep the existing AgOpenGPS calculations, communication, hardware processing and safety logic intact while adding a clean contract that a new Agro Parallel UI can consume.

The first stage does not rewrite guidance, autosteer, sections, GPS, AgIO, OpenGL or field processing. It only introduces contracts and boundaries.

## Target architecture

```text
Agro Parallel UI
Windows / WebView2 / Android / Browser / ViewX
        |
        | snapshots + commands
        v
Agro Parallel Core contracts
RuntimeState / Commands / Configuration / Bridge
        |
        | adapter today, headless engine tomorrow
        v
AgOpenGPS legacy engine
FormGPS / AgIO / GPS / Autosteer / Sections / Field / OpenGL
```

## New project

`SourceCode/AgOpenGPS.AgroParallel.Core`

This project is intentionally UI-agnostic and targets `net8.0`.

It should not reference:

- WinForms
- WPF
- WebView2
- OpenGL controls
- serial-port implementations directly
- hardware-specific UI controls

## Contracts added

- `AgpRuntimeState`: read model consumed by any UI.
- `AgpCommand`: command envelope sent by UI clients.
- `IAgpEngineBridge`: adapter boundary between current engine and future UI.
- `AgpVehicleConfiguration`, `AgpGpsConfiguration`, `AgpImuConfiguration`, `AgpImplementConfiguration`: initial configuration DTOs.

## Migration phases

### Phase 1: Read-only snapshots

Add a WinForms-side adapter that reads state from `FormGPS` and exposes `AgpRuntimeState` without changing engine behavior.

### Phase 2: Safe commands

Allow the UI to send only whitelisted commands such as:

- Toggle autosteer
- Start headland turn
- Center map
- Zoom in/out
- Cycle line
- Save configuration

Commands should call existing safe methods or existing button handlers. They should not mutate internal fields directly.

### Phase 3: Local API

Expose snapshots and commands through a local API:

- WebSocket for live state
- HTTP for configuration and commands
- Optional MQTT later for OrbitX telemetry

### Phase 4: Headless engine

Gradually move engine services out of `FormGPS` into UI-independent services.

Suggested services:

- GpsService
- GuidanceService
- AutosteerService
- SectionService
- FieldService
- MachineModuleService
- OrbitXSyncService

## Rule

The UI is the face. The Core is the brain.

The visual layer should never calculate, send hardware messages directly, or modify engine variables directly.
