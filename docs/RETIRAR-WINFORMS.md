# Lista para retirar WinForms

Qué falta para que `SourceCode/GPS/` (la app WinForms, `Build\PilotX.exe`) deje
de hacer falta y PilotX.Desktop quede solo.

**La regla: no se tacha por declaración, se tacha por evidencia.** El tablero de
cierre decía "HECHO" sobre 20 ítems que daban 404 — se descubrió comparando qué
servicios inyecta el motor, no leyendo el tablero. Cada tachado de acá tiene que
poder mostrar la llamada a la API que responde.

## Cómo se porta cada uno

El patrón ya está probado cuatro veces (banderas, contorno, cabecera,
cabecera-líneas):

1. La lógica vive como `partial class FormGPS` — atada a WinForms aunque no
   tenga nada de WinForms adentro. Medir primero: `grep -cE
   "MessageBox|ShowDialog|new Form[A-Z]|OpenFileDialog|GL\."`. Hasta ahora dio
   **cero** en todos.
2. Mover tal cual a `AgOpenGPS.Core/Classes/XxxEditor.cs`, y lo que toca del
   host pasa a campos y callbacks. No reescribir: es geometría que ya funciona.
3. `EngineXxxService` en el motor + inyectarlo en `EngineWebHost`.
4. Compilar los TRES proyectos y verificar la API contra un lote real.

Mientras WinForms exista, el partial queda como delegación fina para que los
dos stacks corran el mismo código. **Cuando se retire, se borra la delegación y
listo — el trabajo de extracción ya está hecho.**

Referencia para portar: `G:\AgroParallel\Productos\AgOpenGPS\Software\App_PC\
AgOpenGPS-6.8.5\` (el original limpio, mejor fuente que nuestro fork).

## Servicios que el motor todavía no inyecta

| Servicio | Líneas | Deps UI | Íconos que destraba | Estado |
|---|---:|---:|---|---|
| ~~CabeceraLineas~~ | 633 | 0 | HeadlandSlice | ✅ `fdb0a38e` |
| ~~TramLine~~ | 584 | 0 | TramAll, TramMulti, TramOff, TramOuter | ✅ `42451234` |
| ~~QuickAb~~ | 394 | 3 | ABLatLonHeading, ABLatLonLatLon | ✅ `b85780c1` |
| ~~TramSimple~~ | 234 | 1 | Con_TramMenu, ConT_* (5) | ✅ `3ea51f21` |
| ~~Nudge~~ | 181 | 1 | ABSnapNudgeMenu, ABSnapNudgeMenuRef, SnapLeftHalf, SnapRightHalf | ✅ `3ea51f21` |
| ~~RecPath~~ | 108 | 1 | RecPath | ✅ (este commit) |
| Shapefile | 215 | — | (overlay de lote) | ⚠️ NO es solo un servicio: la carga termina en `shapefileLayer`, una capa de DIBUJO del mapa WinForms. El mapa de PilotX.Desktop no tiene capa de shapefile — portar el upload solo guardaría el archivo sin mostrarlo. Va como función de mapa, no acá. |

## Botones que la UI manda y no atiende nadie

Verificado cruzando `CommandParameter` de las barras contra el vocabulario de
`ExecuteCommand` y el ruteo de `MainWindow`. Hoy no hacen absolutamente nada:

- ⬜ `isobus`
- ⬜ `kiosco`
- ⬜ `simulador`
- ⬜ `reset_all`
- ⬜ `ruta_grabada` (queda cubierto al portar RecPath)
- ⬜ `tram_multi` (queda cubierto al portar TramLine)

## Funciones sin equivalente todavía

- ⬜ **Free Drive** (5 íconos: SteerDriveOn/Off, SteerLeft/Right, WizSteerDot) —
  mover la dirección a mano sin guía. Toca el camino del PGN de autosteer, o sea
  que es lo más delicado de la lista: hay que hacerlo con el mismo cuidado que
  el giro manual (límite de velocidad, guard explícito).
- ⬜ **Importar KML / Google Earth / desde tracks** (3 íconos) — hoy devuelven
  `no-disponible-sin-ui` porque abren diálogo nativo de archivo. Necesitan que
  la pantalla suba el archivo, no que el motor abra un explorador.

## Lo que NO bloquea retirar WinForms

48 íconos marcados `fuera`: AgShare, GitHub, foro, QR, subida automática. No se
usan en PilotX.

## Antes de borrar `SourceCode/GPS/`

1. Todo lo de arriba tachado con evidencia.
2. PilotX.Desktop validado **en la cabina**, no en el escritorio. Hoy el fallback
   es lo único que se probó en máquina real.
3. Sacar `AgOpenGPS.csproj` de `build.ps1` y de la solución.
4. Confirmar que `Build\PilotX.exe` ya no lo referencie nada (accesos directos,
   tarea de arranque, `PilotX-KioskSetup`).
