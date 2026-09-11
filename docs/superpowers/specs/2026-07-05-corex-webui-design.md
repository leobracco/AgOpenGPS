# CoreX Web UI — separación backend/frontend de AgIO

**Fecha:** 2026-07-05
**Estado:** aprobado en conversación (opción 3: UI web estilo Hub)

## Objetivo

Reemplazar la UI WinForms de CoreX (AgIO) por una UI web con el design system
de PilotX (theme.css / layout.css / modal.js), sin reescribir la lógica de
comunicación. La lógica (Serial, UDP, NTRIP, MQTT) se extrae a servicios
UI-agnósticos; la ventana pasa a ser un WebView2 a un EmbedIO local.

## Contexto (estado actual)

- FormLoop es un god-object de ~5.700 líneas repartido en 6 partials:
  `FormLoop.cs` (timers/orquestación), `SerialComm.Designer.cs` (6 SerialPorts
  + PGN), `NMEA.Designer.cs` (parseo NMEA), `NTRIPComm.Designer.cs` (cliente
  NTRIP), `UDP.designer.cs` (loopback AOG + broadcast módulos),
  `MQTT.Designer.cs` (broker MQTTnet :1883).
- El acoplamiento a WinForms es superficial: sockets async y Tasks propios;
  la UI se toca solo en puntos concretos (`lblWatch.Text=`,
  `btnMQTT.BackColor=`, etc.) disparados por `oneSecondLoopTimer` o
  `BeginInvoke`.
- Estado: `Properties.Settings` (registro) + campos públicos de FormLoop
  (`traffic`, `scanReply`, `isConnected*`, lat/lon).
- ~28 forms secundarios: mayoría editores de Settings; FormNtrip/FormUDP/
  FormISOBUS tienen lógica embebida.
- AgIO es net48; `AgroParallel.WebHost` (EmbedIO 3.5.2) y
  `AgroParallel.Services` (AgpJson/AgpControllerBase) son netstandard2.0 →
  referenciables desde AgIO.

## Arquitectura objetivo

```
CoreX.exe (net48, mismo proceso)
├── Services/ (nuevos, UI-agnósticos)
│   ├── SerialComService    — 6 SerialPorts + parseo PGN
│   ├── UdpNetworkService   — loopback AOG (17777→15555) + broadcast :8888
│   ├── NtripClientService  — cliente NTRIP + watchdog + GGA
│   ├── MqttBrokerService   — wrapper del MQTTnet Server :1883 (ya async)
│   └── CoreXState          — snapshot POCO thread-safe (lo que hoy son labels)
├── Web/
│   └── CoreXWebHost        — EmbedIO en 127.0.0.1:5181 (el :5180 es del Hub)
│       ├── GET  /api/corex/status      — snapshot JSON @1Hz de polling
│       ├── POST /api/corex/ntrip/...   — on/off + config caster
│       ├── POST /api/corex/serial/...  — open/close/config puertos
│       ├── POST /api/corex/udp/...     — config red
│       └── estáticos: wwwroot-corex/ (copia liviana del design system)
├── FormLoop (reducido)     — host: timer 1s → services.Tick(), sin controles
└── FormWebShell (nuevo)    — WebView2 fullscreen → http://127.0.0.1:5181/
```

### Decisiones

1. **Puerto :5181** fijo, solo loopback. El Hub de PilotX sigue en :5180.
2. **Snapshot pattern** (igual que VistaXLiveService): los servicios escriben
   a `CoreXState`; el frontend hace polling de `/api/corex/status` cada 1 s.
   Sin WebSocket en la v1 — el refresco de 1 s es el mismo que hoy tiene el
   timer de WinForms.
3. **Extracción mecánica, no rediseño**: la lógica se mueve tal cual está;
   donde hoy escribe a un label/botón, escribe al snapshot. Los PGN, checksums
   y flujos no se tocan.
4. **Convivencia**: los partials viejos siguen compilando durante la
   migración. FormLoop conserva el ciclo de vida (Load/Closing) delegando a
   los servicios. Los forms secundarios WinForms siguen abriéndose hasta que
   exista su página web equivalente.
5. **Design system compartido**: las páginas de CoreX usan theme.css,
   layout.css, keyboard.js y modal.js del wwwroot de PilotX (copiados al
   `wwwroot-corex/` en build; misma fuente de verdad en
   `AgroParallel.WebUI/wwwroot/`).
6. **Serialización**: AgpJson (snake_case) vía referencia a
   `AgroParallel.Services`, para mantener el wire unificado del ecosistema.
7. **Branding**: la UI dice CoreX/PilotX/Agro Parallel; namespaces y clases
   internas quedan como están (AgIO.*).

## Fases

**Fase 1 — Backend (esta spec cubre 1 y 2):**
- Crear `CoreXState` + los 4 servicios extrayendo lógica de los partials.
- FormLoop delega en servicios; los labels viejos se alimentan del snapshot
  (UI vieja sigue funcionando como validación A/B).
- EmbedIO :5181 con `GET /api/corex/status` + endpoints de NTRIP y Serial.

**Fase 2 — Dashboard web:**
- Página principal: estado GPS (lat/lon/Hz), NTRIP (estado/bytes/edad),
  MQTT (clientes/tópicos), módulos (IMU/Steer/Machine hello), botones on/off.
- FormWebShell con WebView2 reemplaza la vista de FormLoop (FormLoop queda
  invisible como host).

**Fase 3 — Configuración (incremental, fuera de esta spec):**
- Páginas web para puertos serie, NTRIP caster, red UDP; se retiran los
  WinForms de config de a uno.

## Riesgos

- **Threading**: los callbacks async hoy usan `BeginInvoke` para tocar UI;
  al escribir a `CoreXState` se reemplaza por lock/campos volátiles. Cuidado
  con los contadores compartidos (`traffic`).
- **Settings en registro**: los endpoints POST deben pisar
  `Properties.Settings` con el mismo merge-sobre-Load que usa el Hub
  (lección AGP: save-config es MERGE, nunca replace).
- **WebView2 runtime**: ya es dependencia de PilotX en el tractor, no agrega
  requisito nuevo.

## Criterio de éxito

- CoreX levanta, el dashboard web muestra en vivo lo mismo que hoy muestran
  los labels (GPS, NTRIP, MQTT, módulos) y los botones on/off funcionan.
- PGN loopback AOG↔CoreX y broker :1883 sin regresiones (nodos siguen
  anunciándose).
