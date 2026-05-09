# Agro Parallel UI Web

This folder contains the first standalone visualization shell for ViewX / CentriX / Spark.

It is intentionally separated from the WinForms engine. The UI should consume `AgpRuntimeState` snapshots and send `AgpCommand` messages to the Core bridge.

## Goal

- Run as a local browser UI during development.
- Later run inside WebView2 on Windows.
- Later run in Chromium kiosk on Linux/ViewX.
- Later run inside an Android WebView app.

## Current status

This is a static prototype with a mock runtime state. It is ready to be connected to:

- WebSocket live snapshots
- HTTP command endpoint
- WebView2 postMessage bridge

## Contract

The UI must not calculate guidance or write to hardware. It only:

1. Renders `AgpRuntimeState`.
2. Sends `AgpCommand` requests.
3. Displays command results and alerts.

## Development

Open `index.html` directly in a browser for now.

Later expected local endpoints:

```text
GET  /api/state
POST /api/command
WS   /ws/live
```

WebSocket state messages should contain the serialized `AgpRuntimeState` contract from `AgOpenGPS.AgroParallel.Core`.
