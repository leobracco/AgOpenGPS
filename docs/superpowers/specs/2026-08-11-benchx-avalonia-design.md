# BenchX — ModSim reescrito en Avalonia con estilo Agro Parallel

Fecha: 2026-08-11
Estado: aprobado (diseño conversado con Leonardo)

## Qué es

Reescribir ModSim — el simulador de banco que emula los módulos autosteer,
máquina e IMU más un receptor GPS NMEA por UDP — de WinForms (.NET Framework,
`SourceCode/ModSim/Source`) a Avalonia net9.0, con la estética oficial de
Agro Parallel y nombre nuevo: **BenchX** (sigue el patrón X-* del ecosistema).
El WinForms viejo se borra en el mismo cambio.

## Por qué

- Es la única herramienta del ecosistema que sigue en WinForms crudo con la
  UI de AOG upstream (gris Windows, strings binarios como "00010000").
- La lógica UDP/NMEA/PGN vive mezclada en partial classes del Form: no hay
  tests posibles y ya nos mordió dos veces (recepción muerta por 10054,
  subred perdida al migrar Properties.Settings en v1.0.25).

## Alcance

Mejorado para banco, sin funciones nuevas de simulación:

- Misma lógica de simulación y wire protocol (NMEA + PGN idénticos).
- UI reorganizada en cards con la paleta Agro Parallel.
- Default de subred **127.255.255** (banco loopback, ver memoria del proyecto).
- Config en JSON junto al exe (reemplaza Properties.Settings).
- Estado de red visible (IPs, subred, scan reply, contadores RX/TX).

Fuera de alcance: serial, MQTT, nuevas sentencias o PGN, grabación de
recorridos.

## Arquitectura

Proyecto nuevo `SourceCode/BenchX/` — net9.0-windows, Avalonia 11.2.3,
mismo stack y estilo de csproj que `PilotX.Bars.Host` (WinExe, compiled
bindings, Inter font). AssemblyName y exe: `BenchX`.

| Unidad | Responsabilidad | Depende de |
|---|---|---|
| `Sim/NmeaBuilder.cs` | Armar GGA/VTG/AVR/HDT/RMC/PAOGI/PANDA/KSXT + checksum NMEA. Puro. | — |
| `Sim/SimuladorVehiculo.cs` | Cinemática: heading desde ángulo de dirección, posición desde rumbo+distancia, velocidad, roll, switches. Puro. | — |
| `Sim/PgnProcessor.cs` | Parsear PGN entrantes (254, 252, 251, 200, 201, 202, 239, 238, 229) y armar respuestas (253, hellos 126/123/121, scan reply 203). Puro: recibe bytes, devuelve bytes + eventos de estado. | — |
| `Red/UdpLink.cs` | Socket UDP: bind :8888, envío broadcast a `<subred>.255:9999`. Conserva el re-arme del BeginReceiveFrom tras excepción (fix 10054 del 2026-08-05 — el comentario viaja con el código). | — |
| `Config/BenchXConfig.cs` | Carga/guarda `benchx.json` junto al exe: subred (default 127.255.255), lat/lon inicial, sentencias activas. | — |
| `MainWindow.axaml` + `MainViewModel` | Timer 10 Hz (`DispatcherTimer` 100 ms), binding del estado, todo push de red a UI vía `Dispatcher.UIThread.Post`. | todo lo anterior |

Flujo: timer 10 Hz → `SimuladorVehiculo` avanza → `NmeaBuilder` arma las
sentencias tildadas → `UdpLink` las manda. Al revés: `UdpLink` recibe →
`PgnProcessor` parsea → respuestas salen por `UdpLink`, estado va al
ViewModel por Dispatcher.

## UI

Una ventana, cards sobre fondo `#F5F7F4`, cards blancas con borde `#C5CFC5`,
acento `#4ABA3E`, texto `#101612`/`#535E54`, fuente Inter, controles grandes
(herramienta de escritorio pero misma identidad que el Hub). Título:
"BenchX — Agro Parallel".

- **Vehículo**: sliders velocidad / dirección (WAS) / roll con valor grande y
  tap en el valor para volver a cero; heading y lat/lon actuales.
- **GPS**: toggles por sentencia NMEA; lat/lon de arranque editables + Guardar.
- **Dirección**: badge de guiado activo (verde/gris), setpoint vs actual, y la
  config que manda PilotX (Kp, high/low/min PWM, counts, offset WAS, Ackerman,
  flags de 251) en grilla de solo lectura.
- **Máquina**: relés 1–16 y zonas 1–8 como hileras de puntos prendidos/apagados
  (no strings binarios); uTurn, hydLift, tram; switches de trabajo y dirección.
- **Red**: IPs locales, subred activa, badge scan reply, contadores RX/TX.
  El cambio de subred por PGN 201 sigue: guarda JSON, avisa y reinicia el
  proceso.

Textos de UI en castellano; "PilotX" en vez de AOG/AgIO donde aplique.

## Manejo de errores

- Excepción en recepción UDP ≠ muerte de la escucha: re-armar siempre
  (regla heredada del fix 10054).
- `benchx.json` ausente o corrupto → defaults (127.255.255) y se reescribe.
- Falla de bind :8888 (puerto tomado) → card Red en rojo con el mensaje, la
  app no se cae.

## Tests

Proyecto `SourceCode/BenchX.Tests/` (xunit, como los demás):

- `NmeaBuilder`: cada sentencia contra salidas conocidas del WinForms
  (mismos inputs → mismo string, checksum incluido).
- `PgnProcessor`: parseo de 254/252/251/239 desde tramas reales; armado del
  253 y del scan reply con checksum correcto.

## Deploy y borrado

- Publica a `Build\BenchX\` (carpeta propia, framework-dependent net9).
- Se borran `SourceCode/ModSim/` (WinForms) y `Build\ModSim.exe` +
  `Build\ModSim.exe.config` viejos.
- Se agregan `BenchX` y `BenchX.Tests` a `SourceCode/AgOpenGPS.sln`; se saca
  `ModSim` de la solución.

## Criterio de éxito

En banco loopback (127.255.255): PilotX recibe GPS simulado y BenchX
responde al scan, refleja PGN 254/252/251 y muestra relés/zonas — igual que
el ModSim de hoy, pero con la UI nueva.
