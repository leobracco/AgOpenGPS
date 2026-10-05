# Despachador de corte unificado (CutDispatcher)

**Fecha:** 2026-06-05
**Estado:** Aprobado para planificación
**Autor:** Leonardo Bracco + Claude

## Problema

El corte de secciones/surcos de PilotX se publica hoy con **bridges separados por
producto**, cada uno leyendo por su cuenta `AogStateSnapshot.SectionOnRequest`:

- `SectionXBridge` → `agp/quantix/{uid}/sections` (bitmask on/off, mapeo cable→sección, desfase tren trasero).
- `FlowXBridge` → `agp/flow/{uid}/target` (L/min + bits; necesita velocidad y dosis).
- `QuantiXMotorBridge` → dosis por motor.
- `SectionsSpeedPublisher` → `agp/aog/sections_speed` (broadcast de velocidad, 5 Hz).

Agregar LineX (corte surco por surco, servo o embrague magnético) con un cuarto
bridge separado duplicaría casi exacto la lógica de corte del `SectionXBridge`.
El corte on/off es **un solo concepto** que debe despacharse a cualquier nodo que
lo consuma (LineX, SectionX/relays, y futuros productos de corte puro).

## Objetivo

Un **único despachador** que lee el corte de PilotX una sola vez por tick y lo
despacha a cada nodo según su producto, publicando al topic correcto con el
payload correcto. Reemplaza al `SectionXBridge` y suma LineX. Los bridges de
dosis (`FlowXBridge`, `QuantiXMotorBridge`) y el `SectionsSpeedPublisher`
**no se tocan** en esta iteración.

## Alcance de la 1ª iteración

- El dispatcher maneja **corte on/off puro**: nodos SectionX (relays → `agp/quantix/.../sections`) y nodos LineX (→ `agp/linex/.../sections`).
- FlowX y QuantiX-motor (dosis/PID) siguen con su bridge actual, intactos.
- Productos de corte puro futuros se enchufan agregando un adapter.

## Decisiones de diseño tomadas

1. **Dispatcher unificado** (no bridges separados, no fusión total con dosis).
2. **Agrega los configs por producto**: el dispatcher itera los configs existentes
   (`sectionX.json`, `lineX.json`). Cada página de producto sigue siendo dueña de
   su mapeo salida→sección. Cero migración de datos, las UIs actuales no se tocan.
3. **Reemplaza `SectionXBridge`** y agrega LineX. FlowX/QuantiX-motor sin tocar.

## Hallazgo clave del firmware

`LineX firmware MQTT_Custom.cpp::handleSections` (líneas 47-56) acepta **dos
formatos** en `agp/linex/{uid}/sections`:

- `{"lo":<byte>,"hi":<byte>}` (bitmask, surco i = bit i), o
- array `[1,0,1,...]` (índice = surco).

SectionX/QuantiX-relay ya usa el array `[1,0,...]` (índice = cable). **El mismo
shape de array sirve para ambos productos**; solo cambian el topic y el mapeo
salida→sección (que ya vive en cada config).

`invert` y `failsafe_open` los resuelve el **firmware** LineX
(`Sections[i].Invert` / `FailsafeOpen`). El dispatcher envía la intención cruda
de apertura por sección; NO duplica esa lógica.

## Arquitectura

```
PilotX (SectionOnRequest, posición, velocidad)
   └─ FormGpsStateProvider.GetSnapshot()
        └─ CutDispatcher.OnTick (100 ms)
             ├─ PositionHistory.Record(...)
             ├─ SectionXCutAdapter.ComputePublishes → agp/quantix/{uid}/sections
             └─ LineXCutAdapter.ComputePublishes   → agp/linex/{uid}/sections
                  └─ dedup (changed || >1 s heartbeat) → MQTT publish
```

### Componentes

**`ICutAdapter`** — un adapter por producto, sin estado de transporte:

- `string Product { get; }` — `"sectionx"` / `"linex"`. Clave para status/test.
- `void Reload()` — recarga su config de disco.
- `int NodeCount { get; }`
- `IEnumerable<CutCommand> ComputePublishes(AogStateSnapshot snap, PositionHistory hist)`
  — por cada nodo habilitado devuelve `{ Uid, Topic, Bits[], Payload }`. **No
  publica ni deduplica**: solo calcula la intención del tick.
- `IEnumerable<CutCommand> OffCommands()` — secciones en cero, usado en `Stop`.

**`CutCommand`** (DTO): `Uid`, `Topic`, `Payload` (string), `Bits` (int[]).

**`SectionXCutAdapter`** — mueve la lógica de `SectionXBridge.OnTick`:
- Mapeo cable→sección por nodo; `tren==1` usa `PositionHistory.GetSectionsAtDistanceBack(distancia_entre_trenes)`.
- Ancho del array = `max(maxCable, 8)`. Topic `agp/quantix/{uid}/sections`. Payload `[1,0,…]`.
- También expone la secuencia de **test de relés** (lógica de `RunRelayTestAsync`).

**`LineXCutAdapter`** — lee `lineX.json`:
- Por cada surco con `seccion_aog>0`: `bits[surco.idx] = secAOG[seccion_aog-1]`.
- `seccion_aog==0` → surco sin asignar → bit 0 (cerrado).
- Ancho del array = `section_count`. Topic `agp/linex/{uid}/sections`. Payload `[1,0,…]`.
- `invert`/`failsafe` quedan en el firmware; el adapter manda intención cruda.

**`CutDispatcher`** — dueño único de:
- Cliente MQTT (broker desde `VistaXConfig`, igual que los bridges actuales).
- Timer de tick 100 ms + timer de reload 2 s (`adapter.Reload()` en cada uno).
- `PositionHistory` compartido (lo usa SectionX; LineX lo ignora).
- Dedup + heartbeat por-UID (republica si cambió o si pasaron ≥1 s — criterio
  por-tiempo de `FlowXBridge`, NO el `MessagesSent % 10` viejo y buggy).
- `_lastInfo` por-UID (topic/payload/bits/ts) para el panel debug, con clave de
  producto para filtrar.
- `static Current` para que los controllers lleguen sin pasar por FormGPS.
- `_inTest` (HashSet de UIDs): mientras un nodo corre el test de relés, el
  dispatcher **saltea publicar sus comandos automáticos** para no pisar la
  secuencia (paridad con `SectionXBridge._inTest`).
- API pública: `GetStatus(string product)`, `GetDebugSnapshot(string product)`,
  `RunRelayTestAsync(uid, cables, stepMs)`, `ReloadNow()`.

**Test de relés (preservado):** `CutDispatcher.RunRelayTestAsync` marca el UID en
`_inTest`, le pide la secuencia de pasos al `SectionXCutAdapter` (un cable activo
a la vez), publica cada paso por el cliente MQTT del dispatcher con el `stepMs`
configurado, apaga todo al final y libera el UID. `SectionXController` lo invoca
fire-and-forget como hoy.

### Wiring (FormGPS)

- El campo `sectionXBridge` (tipo `SectionXBridge`) se reemplaza por
  `cutDispatcher` (tipo `CutDispatcher`), construido con
  `new CutDispatcher(stateProvider, new ICutAdapter[]{ new SectionXCutAdapter(), new LineXCutAdapter() })`.
- **Arranca siempre**, sin el gate `enabled && nodos>0`: cada adapter se
  auto-filtra por su config. Esto **elimina el bug** (observado 2026-05-19) que
  el handler `ConfigSaved` venía parchando: el bridge quedaba clavado con config
  vieja (típicamente null si arrancó con `nodos:[]`) y nunca publicaba.
- `ReloadSectionXBridge()` → `cutDispatcher.ReloadNow()`. El handler `ConfigSaved`
  de SectionX y uno nuevo para el save de LineX llaman a `ReloadNow()` para
  refresco inmediato; el reload periódico de 2 s es la red de seguridad.
- `SectionXController` pasa a consultar `CutDispatcher.Current` en vez de
  `SectionXBridge.Current` (status/debug/test).
- `SectionXBridge.cs` se elimina (su lógica vive en `SectionXCutAdapter` +
  `CutDispatcher`).
- `SectionsSpeedPublisher`, `FlowXBridge`, `QuantiXMotorBridge`: sin tocar.

### LineX live (sin cambios)

La página LineX ya tiene su `LineXLiveService` que lee `status_live` y el endpoint
`/linex/live`. El dispatcher solo **envía** `sections`; la lectura de telemetría
es independiente. No se agregan endpoints de live para esta iteración.

## Manejo de errores

- Cada `OnTick` envuelto en try/catch — nunca rompe el timer (paridad con los bridges actuales).
- Fallo de conexión MQTT en `StartAsync`: loguea y queda idle. Sin auto-reconnect (paridad; fuera de alcance).
- `Stop`: cada adapter emite `OffCommands()` (secciones en cero) antes de desconectar el cliente MQTT.

## Testing

- **Seam testeable:** la computación pura del adapter —
  `ComputePublishes(snapshot_fake, config_fake)` → asserts sobre topic + bits,
  sin MQTT. Cubre SectionX (mapeo cable→sección, tren trasero) y LineX
  (mapeo surco→sección, `seccion_aog==0` cerrado).
- Durante el plan: verificar si existe un proyecto de tests C# en la solución; si
  no, queda un harness mínimo en vez de framework.
- **Transporte (publish real):** validación end-to-end con el nodo físico
  `LX-7453CB215788` (ya descubierto y reportando telemetría) + un nodo SectionX
  relay para confirmar que el topic `agp/quantix/.../sections` no cambió.

## Fuera de alcance

- Fusionar la dosis/PID de FlowX/QuantiX-motor.
- Config unificado nuevo (se agregan los configs por producto existentes).
- Auto-reconnect MQTT del dispatcher.
- Variante de embrague magnético de LineX (el firmware actual es servo; el topic
  `sections` es el mismo, así que el adapter ya queda listo para esa placa).
```
