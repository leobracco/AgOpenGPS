# CLAUDE.md — PilotX / AgOpenGPS

Instrucciones para cualquier sesión de Claude Code que trabaje en este repo.
Complementa a `G:\AgroParallel\Productos\CLAUDE.md` (mapa del ecosistema), que
sigue valiendo: **respondé siempre en castellano rioplatense**, y en UI/logs usá
los nombres de producto (PilotX, Agro Parallel, CoreX), no AOG/AgOpenGPS/AgIO.

## Tablero de cierre — declarar en el commit qué ícono se cierra

Hay un tablero con los **382 íconos de 6.8.5** y su estado de cierre para el
**15 de agosto**. Vive en una sola base, servida por Node:

- tablero: `http://localhost:6002` (o `CIERRE_URL` si corre en otra máquina)
- datos: `G:\AgroParallel\Productos\Cierre-15Ago\items.json`
- fuera del repo a propósito: es herramienta de oficina, no software de cabina

**El tablero no adivina qué toca cada commit: el mensaje tiene que declararlo.**
Cuando un commit termina, arregla o descarta el trabajo de un ícono, agregá una
línea propia al mensaje:

```
Cierra: ABDraw, Boundary, btnAutoSteer     -> queda "anda"
Prueba: TramLines                          -> queda "en prueba"
Roto:   YouTurnU                           -> queda "roto"
Fuera:  bing1                              -> "fuera de alcance"
```

Reglas:

- Se acepta el nombre del ícono (`ABDraw`) o el del control (`btnABDraw`).
- Varios por línea, separados por coma.
- **Las líneas que contienen `->` se ignoran**: son documentación. Por eso los
  ejemplos de arriba no se auto-aplican.
- Si no hay nada que cerrar, no pongas la línea. Un commit normal no toca el
  tablero.
- No inventes cierres. Marcar "anda" algo que no se probó en cabina es peor que
  dejarlo pendiente: el tablero se usa para decidir qué falta para el 15/8.
  Si escribiste el código pero no lo probó nadie, va `Prueba:`.

Lo aplica el hook `post-commit` llamando a `Tools/cierre/sync-tabla.ps1`. Los
hooks no viajan en git, así que **cada clon lo instala una vez**:

```
powershell -ExecutionPolicy Bypass -File Tools\cierre\instalar-hook.ps1
```

El hook nunca frena un commit: si el tablero no está disponible avisa y sigue.
Queda registro de quién cerró qué y con qué commit (últimos 5 por ítem, más un
historial global en el tablero).

## Estado del ítem, en criollo

| Estado | Cuándo |
|---|---|
| `sin_probar` | nadie lo miró todavía |
| `en_prueba` | hay código, falta confirmarlo en cabina |
| `anda` | probado y funcionando |
| `parcial` | funciona a medias, con qué falta anotado |
| `roto` | se probó y falla — estos bloquean el cierre |
| `fuera` | no se usa más en PilotX, no entra en el alcance |

## Verificación antes de decir que algo anda

Este repo maneja una máquina que siembra: un error cuesta plata en el lote.

- Compilá y corré los tests (`AgroParallel.Services.Tests`) antes de afirmar
  que algo funciona.
- Lo que no se pudo verificar, decilo. No lo declares cerrado.
- Los cambios que deciden si una sección aplica producto van **detrás de un
  flag apagado por defecto** hasta validarse en lote real (ver
  `--antisolape`).
