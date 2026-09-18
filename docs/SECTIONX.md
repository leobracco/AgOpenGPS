# SectionX — Corte automático de secciones

> Doc de producto · 2026-07-03 · Complementa al design doc
> `docs/superpowers/specs/2026-06-05-cut-dispatcher-design.md`.

## Qué es (y qué NO es)

**SectionX es software del lado de la PC**, no un firmware ni un hardware
propio. Es el traductor que toma el estado de corte de secciones que calcula
PilotX (auto section control) y lo publica por MQTT a los nodos de la LAN,
que accionan los relés físicos.

Aclaraciones frecuentes:

- **No existe un "firmware SectionX"**: los relés los maneja el firmware
  **Quantix2Motors** (nodo QuantiX), que además de los motores de dosis
  escucha el topic de secciones y comanda GPIO/PCA9685.
- **No usa PGN**: aunque PilotX sigue emitiendo los PGN UDP clásicos de
  PilotX (p_235/p_236 → CoreX por UDP :17777), SectionX no pasa por ahí.
  Lee el estado de secciones directo de la memoria de PilotX (mismo proceso),
  sin salto de red intermedio. Los PGN quedan como canal legacy paralelo para
  el hardware clásico vía CoreX.

## Flujo del dato

```
FormGPS  section[i].sectionOnRequest      ← estado DESEADO del auto section control
   │  FormGpsStateProvider.GetSnapshot()     (SourceCode/GPS/AgroParallel/Common/)
   ▼
CutDispatcher                              ← tick 100 ms; dedup por payload +
   │                                          heartbeat 1 s (el firmware tiene
   │                                          comm_timeout ~3 s de seguridad)
   │  SectionXCutAdapter.ComputePublishes()
   │    · mapeo cable → sección (SectionXConfig)
   │    · desfase de tren trasero (PositionHistory, por distancia_entre_trenes)
   ▼
MQTT  agp/quantix/{uid}/sections           payload: [1,0,1,0,...]  (índice = cable-1)
   ▼
Firmware Quantix2Motors                    ← MQTT_Custom.cpp parsea el array,
                                              CheckRelays() → GPIO/PCA9685
```

Al hacer `Stop()` el dispatcher manda **all-off** a todos los nodos
configurados (seguridad).

## Componentes y archivos

| Pieza | Path | Rol |
|---|---|---|
| CutDispatcher | `SourceCode/AgroParallel/Core/AgroParallel.Services/Cut/CutDispatcher.cs` | Transporte MQTT, timing, dedup, heartbeat, test de relés, stats/debug. Único para todos los productos de corte. |
| SectionXCutAdapter | `.../Cut/SectionXCutAdapter.cs` | Traduce snapshot → bitmask de relés por nodo (lo específico de SectionX). |
| LineXCutAdapter | `.../Cut/LineXCutAdapter.cs` | Ídem para LineX (corte surco por surco, servo/embrague, `agp/linex/{uid}/sections`). |
| SectionXConfig | `.../SectionX/SectionXConfig.cs` | Config persistida en `sectionX.json` (junto al exe). |
| SectionsSpeedPublisher | `.../SectionX/SectionsSpeedPublisher.cs` | Canal aparte: `agp/aog/sections_speed` @5 Hz con km/h por sección (lo consumen QuantiX/FlowX/VistaX para dosis variable). No es corte. |
| SectionXController | `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/SectionXController.cs` | REST del Hub: config (con validación AGP-CFG-001), status, debug, test de relés. |
| UI Hub | `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/sectionx.js` + `pages/sectionx.html` | Configuración de nodos/cables y test. |
| Firmware consumidor | `G:\agroparallel\productos\AGP-VR\Software\Firmware_Embebido\Quantix2Motors\` (`MQTT_Custom.cpp`, `Relays.cpp`) | Suscribe `agp/quantix/{UID}/sections` y acciona los relés. |

## Configuración (`sectionX.json`)

```json
{
  "enabled": true,
  "nodos": [
    {
      "uid": "AB12CD",
      "nombre": "Nodo SectionX",
      "habilitado": true,
      "distancia_entre_trenes": 6.5,
      "cables": [
        { "cable": 1, "seccion_aog": 1, "tren": 0 },
        { "cable": 2, "seccion_aog": 2, "tren": 1 }
      ]
    }
  ],
  "ignorados": []
}
```

- `cable`: salida física del PCA9685 (1-14 = SA1A..SA7A, SA1B..SA7B; hasta 16).
- `seccion_aog`: sección de PilotX que controla (1-based, 0 = sin asignar).
- `tren`: 0 = delantero (estado actual), 1 = trasero (estado desfasado por
  `distancia_entre_trenes` metros usando el historial de posición — el tren
  trasero pasa por donde el delantero estuvo hace X metros).
- Los nodos se agregan **solo automáticamente** vía announcement MQTT
  (regla del ecosistema — nunca tipear UIDs a mano).

## Test de relés

Desde el Hub: secuencia "un cable a la vez" (`RunRelayTestAsync`), con
`step_ms` entre 100 y 5000. Mientras corre, el tick automático saltea ese UID
para no pisar la secuencia; termina con all-off.

## Límites conocidos / deuda

1. **Solo estado deseado**: se publica lo que PilotX quiere; el firmware no
   reporta los relés reales (no hay desired/reported ni detección de drift,
   punto pendiente de la doctrina MQTT industrial).
2. Log propio a disco (`cut_dispatcher.log`) fuera del ring buffer de AgpLog.
3. Schema del payload sin versionar (a diferencia de VistaX).
