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
`ExecuteCommand` y el ruteo de `MainWindow`. **Los 6 atendidos** (commit de
2026-07-30, falta el toque manual en pantalla salvo donde se indica):

- ✅ `isobus` — motor: `Isobus.RequestSectionControlEnabled(!enabled)` (port de
  `btnIsobusSC_Click`). Es un REQUEST por PGN: el estado real vuelve del monitor
  en `isobus_on`. Verificado el comando por HTTP; el efecto necesita un monitor
  ISOBUS real.
- ✅ `kiosco` — MainWindow: toggle pantalla completa ↔ ventana con bordes
  (para taller/escritorio; la cabina ya arranca a pantalla completa).
- ✅ `simulador` — MainWindow: abre/cierra **ModSim.exe** (el sim de este stack
  es ese proceso externo; el interno del motor no aplica). Busca el exe al lado
  del deploy (`Build\ModSim.exe`).
- ✅ `reset_all` — confirmación nativa en MainWindow (botón rojo) → comando al
  motor: `RegistrySettings.Reset()` + **el motor sale** (si siguiera vivo, su
  config en memoria podría re-guardar lo borrado). Guard verificado en vivo:
  con lote abierto rechaza y no borra; sin lote borra y sale (probado, sin
  querer, contra el motor real).
- ✅ `ruta_grabada` → ventana-diálogo `pages/recpath.html` (el servicio ya
  estaba portado; faltaba el ruteo).
- ✅ `tram_multi` → ventana-diálogo `pages/tramlines.html` (ídem).

## Funciones sin equivalente todavía

- 🟡 **Free Drive** (5 íconos: SteerDriveOn/Off, SteerLeft/Right, WizSteerDot) —
  mover la dirección a mano sin guía. HECHO, falta cabina. Vive en el servicio
  compartido (`AgroParallel/Adapters/SteerConfigService.cs`, lo comen los dos
  hosts) + `/api/steer/freedrive[/angle|/zero]` + tarjeta en la tab Dirección
  del Hub. Tres candados, porque es lo único de esa pantalla que mueve el
  volante de verdad:
  1. no se deja prender por encima del límite de velocidad de guiado — ni con
     el host sin informar velocidad (falla cerrado);
  2. `CAutoSteerUpdater` lo apaga solo si el tractor arranca (corre en cada
     PGN, cubre los dos stacks y también al FormSteer nativo);
  3. watchdog de latido: prendido desde una pantalla remota, si esa pantalla
     deja de consultar (~3 s) se apaga. Lo prendido desde el FormSteer nativo
     queda exento (`freeDriveWatchdog = −1`).
  Verificado: 16 tests (`AgOpenGPS.Core.Tests/FreeDriveTests.cs`) y los 4
  endpoints contra el motor real + la tarjeta en pantalla. **Falta en cabina**:
  que el volante se mueva de verdad y que los candados 2 y 3 corten — los dos
  descuentan en el camino del PGN, que sin GPS/sim no corre.
- 🟡 **Importar KML** — HECHO el upload sin diálogo nativo, verificado
  end-to-end (pantalla + motor real). Parseo compartido en
  `AgOpenGPS.Core/IO/KmlBoundaryReader.cs`; `ImportKmlUpload` en
  `IContornoService` con impl en los dos hosts; endpoint
  `POST /api/contorno/import-kml-upload?multi=` (body = KML crudo); en
  `contorno.html` dos botones: "Importar KML" (multi, reemplaza TODO con
  doble-tap de confirmación si había algo) y "Agregar desde KML" (suma el
  primer polígono como exclusión). El import de LOTE desde KML ya estaba
  (`/api/lotes/import-kml` con upload). Trampa conocida: el binder de EmbedIO
  revienta la conexión con `?multi=1` a un parámetro bool — va string y se
  parsea a mano.
- ⬜ **Google Earth / desde tracks** (2 íconos) — siguen `no-disponible-sin-ui`.
  Google Earth no existe en la pantalla del tractor (era "exportar KML y abrir
  GE"); "desde tracks" necesita su propia pantalla (FormBuildBoundaryFromTracks).

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
