# Borrar lotes, linderos cruzados, piloto y legibilidad del dock

Fecha: 2026-09-14
Repos: PilotX (`CentriX-Spark/.../AgOpenGPS`) y OrbitX-Server (`OrbitX/.../OrbitX-Server`)

Cuatro trabajos independientes que salieron de un mismo pedido. Van en specs de
implementación y commits separados, en el orden de abajo: el ítem 1 es un bug
reportado en cabina y es el más barato; el 2 es la feature más cara porque toca
los dos repos. La sección 5 no es un trabajo: es la regla de manual que aplica
a todos.

---

## 0. Contexto que hay que tener a mano

Los lotes creados en OrbitX **sí llegan al tractor**, pero no por
`/api/aog/pendientes-descarga` (ese endpoint no lo consume nadie desde que se
deprecó el agente `OrbitX-Sync` standalone). Viajan por
`GET /api/prescripciones/pendientes`, que entrega **todos** los
`aog_descarga_pendiente` del device, no sólo prescripciones.
`OrbitXSync.GuardarArchivoDeLote` (`OrbitXSync.cs:944`) los detecta por el
prefijo `Fields/` de `ruta_rel` y los escribe en el directorio de lotes.

De los tres archivos que manda el server (`Field.txt`, `Boundary.txt`,
`boundary.kml`) sólo se usa el **KML**: los otros dos tienen formato
server-side que los readers del motor no digieren, y se descartan a propósito.
El KML se convierte con `EngineLotesService.CrearLoteDesdeKmlSinAbrir`
(`EngineLotesService.cs:435`), que reconstruye `Field.txt` y `Boundary.txt` con
los writers propios.

---

## 1. Linderos de otros lotes (bug reportado)

### Síntoma

El operario creó un lote en PilotX y le apareció un lindero que había dibujado
en OrbitX.

### Causa

Colisión de nombres sin aviso, por dos caminos que terminan en el mismo lugar.

**Camino A — crear sobre un lote que ya existe.**
`EngineLotesService.CreateFieldAsync` (`EngineLotesService.cs:180`) hace:

```csharp
string dir = Path.Combine(root, clean);
if (Directory.Exists(dir)) return Task.FromResult(false);
```

No crea nada y devuelve `false`. Pero `lote.js:145` hace:

```js
await jpost('/api/lotes/create?name=' + encodeURIComponent(name));
closeWin();
```

**No mira el `ok`.** La ventana se cierra igual, el operario cree que creó su
lote, y lo que queda en disco es el lote que bajó de OrbitX con ese nombre —
con su lindero.

**Camino B — el sync pisa un lote local.**
`CrearLoteDesdeKmlSinAbrir` conserva el origen del plano si el lote ya existe,
pero después hace `BoundaryFiles.Save(dir, lista)` **siempre**: el lindero que
el operario recorrió en cabina se reemplaza por el del cloud, sin aviso.

### Decisión

El trabajo del operario no se pisa nunca. El lote del cloud entra aparte.

### Diseño

Se introduce un **marcador de origen**: `Fields/<lote>/.orbitx`, un JSON con
el nombre del lote en el cloud y el SHA-256 del KML que lo generó. Hoy no hay
forma de distinguir "este lote es espejo de OrbitX" de "este lo hizo el
operario", y sin esa distinción "guardar aparte" degenera: cada ciclo de sync
crearía `<nombre> (OrbitX)`, `(OrbitX 2)`, `(OrbitX 3)`…

`CrearLoteDesdeKmlSinAbrir` pasa a resolver el destino así:

| Estado del destino | Acción |
|---|---|
| No existe | Crear normal + escribir `.orbitx` |
| Existe **con** `.orbitx` del mismo lote cloud | Es el espejo: actualizar el lindero **sólo si cambió el SHA** |
| Existe **sin** `.orbitx` | Es del operario: no tocar. Ir a `<nombre> (OrbitX)` y reaplicar la tabla |
| Está abierto | `false`, como hoy (el caller ya avisa) |

El salto al nombre con sufijo se hace **una sola vez**: si
`<nombre> (OrbitX)` también existe sin `.orbitx` — el operario creó a mano un
lote con ese nombre — se numera `<nombre> (OrbitX 2)`, `(OrbitX 3)`… hasta
encontrar uno libre, con tope de 20 intentos. Nada de recursión sobre el
sufijo: `<nombre> (OrbitX) (OrbitX)` no es un nombre que nadie quiera ver en la
pantalla de lote.

El SHA además corta el reescribir-siempre de hoy: si el lindero del cloud no
cambió, no se toca el disco.

### Arreglos que van con esto

1. **`lote.js` deja de ignorar el `ok`.** Si `create` devuelve `false`, mostrar
   "Ya existe un lote con ese nombre" y **no** cerrar la ventana. Es el arreglo
   del síntoma reportado; lo demás es evitar que vuelva por otro lado.
2. **`CreateFieldAsync` distingue el motivo.** Hoy `false` tapa "ya existe",
   "sin `fieldsDirectory`" y "excepción". La UI no puede decir nada útil con
   eso. El contrato pasa a devolver un resultado con motivo, y el controller lo
   serializa como `{ ok, motivo }`.
3. **`Job.cs:80` — el `Clear()` va antes del `Load()`.**

   ```csharp
   var boundaries = BoundaryFiles.Load(dir);
   Bnd.bndList.Clear();          // ← nunca corre si Load() tira
   ```

   Con un `Boundary.txt` corrupto quedan vivos los linderos del lote anterior.
   Mismo patrón con `Trk.gArr` y `TrackFiles.Load` unas líneas más arriba.
   Es defensivo: `CloseField` normalmente ya limpió. No es la causa del
   síntoma reportado, pero es la misma familia de bug y sale gratis.

### Cómo se verifica

- Crear en PilotX un lote con el nombre de uno que ya existe → la ventana no se
  cierra y avisa.
- Bajar de OrbitX un lote cuyo nombre ya existe local sin `.orbitx` → aparece
  `<nombre> (OrbitX)` y el lindero local queda intacto.
- Correr el sync dos veces seguidas sin cambiar el lindero en el cloud → no se
  crea ningún duplicado y no se reescribe `Boundary.txt`.

---

## 2. Borrar lotes desde PilotX

### Decisión

Borra local **y avisa a OrbitX**. Pero no bloquea: en el lote no hay WiFi.

### Diseño

> **Corregido el 2026-09-15.** La versión original de esta sección decía que la
> UI iba en `lote.html`/`lote.js`. Es falso: la cabina del PC abre el panel
> **nativo** `PilotX.UI/Views/LotePanel.cs` (`MainWindow.axaml.cs:6523`,
> `case "lote_menu"`). `lote.html` es el head web/Android
> (`MainView.axaml.cs:222`). Es el mismo error que costó una ronda de revisión
> en el trabajo de linderos.

**El borrado ya está escrito, y es inalcanzable.** `wwwroot/js/lote-rapido.js`
tiene borrado de a uno (`:120`) y borrado masivo (`:156`), con modal propio
(`AgpModal.confirm`, porque `confirm()` nativo está muerto en WebView2) y
manejo de errores. Pero **nadie abre `lote-rapido.html`**: no hay una sola
referencia fuera de su propio `<script>`. Es el tercer caso del mismo patrón en
este repo, junto con el lightbar GL que nunca se dibujaba y los settings
huérfanos de la barra guía.

Así que el trabajo no es escribir el borrado: es **conectarlo a la pantalla que
el operario abre de verdad**.

**UI** — el botón va en la lista de lotes de **`LotePanel.cs`**, la pantalla
nativa. Toque → confirmación con el nombre del lote, usando el mecanismo de
mensajes que ese panel ya tiene (el mismo que se usó para el cartel de "Ya
existe un lote con ese nombre"). **Nada de diálogos modales del sistema**: en el
WebView bloquean el host, y en el panel nativo no hacen falta.

El lote abierto se lista **sin** botón de borrar. `DeleteFieldAsync` ya se niega
a borrarlo, y un botón que siempre falla es peor que no tenerlo.

**Borrado masivo** ("borrar todos menos el abierto"): entra, con **doble
confirmación** y diciendo cuántos lotes se van a borrar. Decisión del usuario,
2026-09-15. Es la herramienta para preparar una pantalla nueva o limpiar un
equipo de demo sin ir por el explorador de Windows.

`lote-rapido.html` **se deja como está**: sigue huérfano pero no molesta, y
sacarlo le rompería el acceso a cualquiera que lo tenga por URL directa.

**Backend local**: no hay nada que escribir. `POST /api/lotes/delete?name=`
(`LotesController.cs:67`) → `EngineLotesService.DeleteFieldAsync`
(`EngineLotesService.cs:222`) ya borra la carpeta con el guard del lote abierto.
Único agregado: invalidar la entrada del `_boundaryCache` del lote borrado, que
hoy queda colgada.

**Aviso al cloud**: se borra la carpeta al instante y el aviso se encola en
`lotes_borrados.json`. `OrbitXSync` la drena en su tick, con el mismo tope de
reintentos (`MaxIntentosPorItem`) que ya usa la cola de subida. Hasta que el
cloud confirme, el nombre queda en la lista de tombstones local para que el
sync no lo vuelva a bajar.

**El tombstone no es opcional, y ahora se sabe exactamente por qué.** El
trabajo de linderos (2026-09-15) dejó a `ResolutorLoteCloud` decidiendo el
destino de cada lote que baja: si la carpeta **no existe**, devuelve `Crear`. O
sea que borrar un lote que es espejo del cloud, sin más, hace que **el sync lo
reponga en el ciclo siguiente** — el operario lo borra y reaparece solo.

Por eso el chequeo del tombstone va **antes** de llamar al importador, en
`OrbitXSync.GuardarArchivoDeLote`: si el lote está en la lista de borrados, el
pendiente se ackea sin escribir nada. El tombstone se levanta cuando el cloud
confirma el borrado, que es el momento en que deja de haber nada que reponer.

Hay una segunda vía de resurrección, preexistente y anotada: `_lastHashes` en
`OrbitXSync` es un diccionario **en memoria**. Si el operario borra un lote,
reinicia PilotX y vuelve a abrir uno con ese nombre, los archivos se re-suben.
El tombstone del cloud cubre el caso normal; ése queda fuera de alcance.

**OrbitX-Server**: dos rutas, porque los esquemas de auth no se mezclan.

- `DELETE /api/lotes-maestro/:nombre` — panel web, JWT +
  `requirePermiso("lotes","delete")`. Hoy `/api/lotes-maestro` está montado sólo
  con `auth.required` (`server.js:189`), sin permisos: **un `viewer` puede crear
  lotes**. Se engancha la matriz `PERMS` que ya existe (`middleware/auth.js:169`).
- Ruta device-friendly bajo `/api/aog/` con `deviceAuth`. El CLAUDE.md del
  server es explícito: los endpoints de device no pasan por `requirePermiso`,
  se valida `dev.estab_slug` a mano.

Ambas borran `lote_maestro` + los `aog_archivo` del lote + los `lote_capa`, y
dejan un tombstone para que `/api/prescripciones/pendientes` no lo re-encole.
Las mutaciones llaman `cacheInvalidate(slug)` — hoy sólo `/crear` lo hace, así
que el `PUT` de metadata y el `POST /capa` tardan hasta 60 s en verse.

### Cómo se verifica

- Borrar un lote sin conexión → desaparece de la lista al instante y el aviso
  queda encolado.
- Al volver la señal → el lote desaparece del panel de OrbitX y no vuelve a
  bajar en el ciclo siguiente.
- Intentar borrar el lote abierto → no hay botón.

---

## 3. Piloto on/off (auditoría, sin cambios todavía)

Revisión preventiva: no hay un síntoma reportado. Se audita y se reportan
hallazgos **antes** de tocar nada. Tres cosas a verificar:

1. **El toggle es ciego.** `PerformAutoSteerClick`
   (`GuidanceEngineHost.Heading.cs:54`) es `isBtnAutoSteerOn = !isBtnAutoSteerOn`
   y nada más. El comando `"autosteer"` (`Commands.cs:184`) lo llama sin
   validar nada.
2. **El guard de la UI no coincide con el manual.**
   `PilotoEnabled = hayGuia || contour` (`BarraDerechaViewModel.cs:194`) no
   exige lote abierto ni GPS, pero `ayuda.html` dice que el piloto pide
   GPS + lote + guía. Uno de los dos miente.
3. **El estado viene por polling del snapshot** (`IsAutoSteerOn` en
   `CockpitSnapshot`). Hay que confirmar que el botón no rebote entre el toque
   y el siguiente poll.

Los arreglos se deciden con los hallazgos en la mano. Si el guard tiene que
cambiar, es cambio de comportamiento de una máquina que siembra: va detrás de
verificación en banco, no se declara cerrado sin probar.

---

## 4. Legibilidad del dock

### Problema

`TextBlock.cap` (`MainWindow.axaml:288`) son las etiquetas del dock de la
pasada — "Piloto", "Guías", "Lindero", "Automático"… Hoy: `FontSize="10"`,
`Foreground="#535E54"` sobre fondo `#F5F7F4`. Eso da ~4.9:1, el mínimo de AA
para texto chico, a 10 px, con sol de frente y la cabina vibrando.

### Diseño

| | Hoy | Propuesto |
|---|---|---|
| FontSize | 10 | 12 |
| Foreground | `#535E54` (≈4.9:1) | `#2A3329` (≈11:1) |
| FontWeight | normal | SemiBold |

La geometría no se toca: el botón es 64×56 y el contenido pasa de 26+14 a
26+16 px.

Se suman los rótulos del cluster del piloto — "A LA LÍNEA", "SALTEA", "GIRO"
(`MainWindow.axaml:564,588,939`), 10-11 px sobre fondo oscuro — que son los que
el operario mira manejando.

Los estilos derivados ya existentes (`Button.on TextBlock.cap`,
`Button:disabled TextBlock.cap`, `Button.on:disabled TextBlock.cap`) se
revisan para que sigan diferenciándose con el nuevo peso base: hoy el `.on` se
distingue por pasar a Bold, y con SemiBold de base ese salto se achica.

### Fuera de alcance

El barrido de los ~25 `FontSize` entre 10 y 12 repartidos por el resto de los
paneles. Es un refactor de tema (números sueltos → tokens) y merece su propio
trabajo; meterlo acá haría irrevisable todo lo demás.

---

## 5. Qué se actualiza sí o sí

`SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`, en
el mismo commit que el cambio, por la regla del CLAUDE.md del repo:

- borrar lote es una función nueva que el operario ve → entra en el manual con
  su ruta real;
- si el guard del piloto cambia, cambia el requisito que el manual promete.

Las rutas salen de la UI real (`MainWindow.axaml` para la barra de la pasada,
`lote.html` para la pantalla de lote), verificadas en el archivo, no de memoria.
