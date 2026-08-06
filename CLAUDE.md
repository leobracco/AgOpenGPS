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

## El manual se actualiza CON el cambio, no después

`SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html` es el
manual de cabina: dice **dónde se hace cada cosa** (rutas tipo
`Barra de la pasada › Piloto`). Un manual que miente es peor que no tener
manual — el operario toca donde dice y no está.

**Regla: si un cambio mueve, agrega o saca algo que el operario ve, la ayuda se
actualiza EN EL MISMO COMMIT.** No es tarea aparte ni "lo hacemos después".
Dispara la regla cualquiera de estos:

- mover un botón de barra/menú a otro lado (ej. Guías de la barra derecha a la
  barra de la pasada, el zoom a la esquina),
- sacar una entrada de menú (ej. "Herr. lote", "Nuevo desde KML", los ítems de
  SISTEMA) — si la función desapareció de la UI, sale del manual,
- agregar una pantalla o un flujo nuevo (ej. el menú de entrada de Guías),
- cambiar un requisito o un guard que el operario sufre (ej. "Borrar pintado"
  solo con secciones apagadas; el piloto pide GPS + lote + guía),
- renombrar algo visible.

Cómo mantenerla honesta: las rutas salen de la UI REAL
(`PilotX.Cockpit.Bars/Views/*.axaml` para barras y menú, `MainWindow.axaml`
para la barra de la pasada, `pages/config.html` para los módulos de
Configuración). Antes de escribir una ruta, verificala en esos archivos — no
de memoria. Si un commit toca UI y no toca `ayuda.html`, hay que poder
justificar por qué (cambio interno que el operario no ve).

## Verificación antes de decir que algo anda

Este repo maneja una máquina que siembra: un error cuesta plata en el lote.

- Compilá y corré los tests (`AgroParallel.Services.Tests`) antes de afirmar
  que algo funciona.
- Lo que no se pudo verificar, decilo. No lo declares cerrado.
- Los cambios que deciden si una sección aplica producto van **detrás de un
  flag apagado por defecto** hasta validarse en lote real. Una vez validados,
  el flag pasa a default y queda la salida de emergencia para apagarlo en
  cabina sin recompilar.
- **Anti-solape: ya validado, ENCENDIDO por defecto** (2026-08-01). No sembrar
  dos veces lo mismo es el comportamiento normal de la máquina; el manual/auto
  lo maneja el operario con los botones de sección. Para apagarlo:
  `PilotX.GuidanceEngine.exe --sin-antisolape`. El arranque loguea
  "Anti-solape de secciones: ACTIVO".
