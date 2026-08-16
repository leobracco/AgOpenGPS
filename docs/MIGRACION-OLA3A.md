# Migración ola 3a — shell de Configuración + las pestañas de vehículo

Rama `codex/pilotx-ui-new` · cierre 2026-08-16

La [ola 2](MIGRACION-OLA2.md) cerró los editores de los productos X-* y dejó
escrito, en su §3.8, cuál era el trabajo grande que quedaba:

> **`config.html` + sus 15 pestañas** (specs `00`-`14`, `26`). Es el trabajo
> grande y el que desbloquea todo lo demás; conviene partirla en (a) config de
> vehículo/implemento nativa y (b) shell de módulos.

La **ola 3a** es el primer mordisco de eso: el **contenedor** (menú lateral en
acordeón, pestañas, footer con perfil/ancho/unidades, botón Guardar) y las
**cuatro pestañas de vehículo** — Resumen, Tipo, Dimensiones y Antena.

Es **strangler fig, no big-bang**: las pestañas que todavía no están portadas se
abren desde el MISMO menú nativo, en el WebView, con el deep-link que la página
ya entendía (`config.html?tab=…`). El operario no perdió ni un acceso.
`pages/config.html` y `js/config.js` **no se tocan ni se borran**: los usa la
PWA del celular.

Guía de porteo: [`PORTING-AVALONIA.md`](PORTING-AVALONIA.md).

---

## 1. Estado de la ola

Verificación del cierre (2026-08-16):

- `dotnet build SourceCode\AgOpenGPS.sln -c Release` → **0 errores**, 0
  advertencias en la pasada incremental del cierre.
- `dotnet test SourceCode\AgOpenGPS.sln -c Release --no-build` → **374/374
  verdes, 0 con error**:
  - `AgroParallel.Services.Tests` 261
  - `AgOpenGPS.Core.Tests` 53
  - `BenchX.Tests` 36
  - `PilotX.Cockpit.Bars.Tests` 21
  - `AgLibrary.Tests` 3
- **Ningún test cubre el `ConfigPanel` ni las pestañas.** Los 374 son los mismos
  de la ola 2: este porteo es UI de Avalonia y no agregó pruebas. Lo que sí se
  verificó, contra el motor headless y contra el disco, está en §3.

### 1.1 Pestañas

| Pestaña (menú) | `?tab=` | Clase nativa | Estado | Persistencia verificada | Commits |
|---|---|---|---|---|---|
| **Shell de Configuración** (menú + footer + Guardar) | — | `Views/ConfigPanel.axaml(.cs)`, `ConfigEditor/CfgCtx.cs`, `ConfigEditor/CfgUi.cs` | **Cerrado con pendientes** | n/a (no persiste nada) | `8c16d310`, `90fe14ca` |
| Resumen | `summary` | `ConfigEditor/ResumenTab.cs` | **Cerrado con pendientes** | n/a — **solo lectura**, `TieneGuardar => false` | `8c16d310`, `90fe14ca` |
| Vehículo › Tipo | `vconfig` | `ConfigEditor/VehiculoTab.cs` | **Cerrado con pendientes** | **SÍ** — `vehicle_type` sobrevive al reinicio del motor | `200c0b63`, `0e3b49cc` |
| Vehículo › Dimensiones | `vdimensions` | `ConfigEditor/DimensionesTab.cs` | **Cerrado con pendientes** | **SÍ** — los 3 campos sobreviven al reinicio | `cd979e3d`, `3c6bcc13` |
| Vehículo › Antena | `vantenna` | `ConfigEditor/AntenaTab.cs` | **Cerrado con pendientes** | **SÍ** — los 3 campos, con signo, sobreviven al reinicio | `5ac55fc4` |

**4 pestañas cerradas + el shell. Ninguna probada en cabina** — las cuatro van
al tablero como `Prueba:`, nunca `Cierra:`.

Archivos nuevos de la ola: `Views/ConfigPanel.axaml(.cs)`,
`Views/ConfigEditor/{CfgCtx,CfgUi,ResumenTab,VehiculoTab,DimensionesTab,AntenaTab}.cs`,
13 PNG en `Assets/config/` y `Tools/rasterizar-iconos-vehiculo.py`.

### 1.2 Cómo se agrega la próxima pestaña

Está escrito en la cabecera de `ConfigPanel.axaml.cs` y son tres líneas:

1. crear `XxxTab : ConfigTab` en `Views/ConfigEditor/`;
2. devolverla en `CrearTab()` con su clave (la MISMA que usa el HTML en `?tab=`);
3. poner `Nativa = true` en su fila de `NAV`.

Mientras `Nativa` sea `false`, esa entrada del menú abre el HTML. No hay que
tocar `MainWindow` ni el router.

---

## 2. El fallback a WebView — verificado, el operario no se queda sin config

Es el punto que más caro sale equivocar: si el menú nativo se comiera una
entrada, el operario perdería acceso a su configuración sin que nada avise.

**Cadena verificada en código** (`ConfigPanel.IrATabAsync` →
`MainWindow.axaml.cs:393`):

```
menú nativo → IrATabAsync(tab) → !EsNativa(tab)
            → OnRequestHtml("pages/config.html?tab=" + tab)
            → CloseConfig() + NavigateTo(ruta)
            → full += (full.IndexOf('?') >= 0 ? "&" : "?") + "widget=1"
```

`NavigateTo` respeta el `?` que ya trae la ruta y agrega `&widget=1` — no lo
pisa. Del otro lado, `config.js:1875` lee el tab con
`new URLSearchParams(location.search).get('tab') || 'summary'`, que ignora el
`widget=1` de más.

**Verificado en banco** (motor headless `PilotX.GuidanceEngine.exe --webhost`,
lanzado desde `bin/Release/net9.0/`, NO desde `win-x64/`): las 15 URLs de
deep-link responden **200**.

| Grupo del menú nativo | `?tab=` | Nativa | Fallback probado |
|---|---|---|---|
| Implemento | `tconfig`, `thitch`, `tooloffset`, `toolpivot`, `tsettings` | no | 200 |
| Secciones | `tsections`, `tswitches`, `amachine` (Máquina) | no | 200 |
| GPS / IMU | `heading` (Rumbo), `roll` (Rolido) | no | 200 |
| Otros | `uturn`, `tram` | no | 200 |
| — fuera del menú — | `relay` (Pines relé), `display` (Pantalla), `botones` (Botones) | no | 200 |

Además, el botón **«Módulos y más…»** al pie del menú nativo abre
`pages/config.html` entera: desde ahí se llega a los 22 módulos embebidos
(`data-mod=`) — Hub, QuantiX, VistaX, FlowX, SectionX, LineX, StormX, Nodos,
Cámaras, Insumos, Mapas, Prescripciones, Calculadora, Lab PID, Diagnóstico PWM,
OrbitX, Firmwares, Actualizar, Conectar celular, Red WiFi, Sistema, Eventos,
Debug, Ayuda, Sonidos. **Sin ese botón, el operario que entra al panel nativo
perdería todo eso.**

### 2.1 Relé / Pantalla / Botones: no hay regresión, pero tampoco hay puerta

`relay`, `display` y `botones` **no están en el menú nativo porque tampoco están
en el menú del HTML**: los sacaron el 2026-08-03 (comentarios en
`config.html:281` y `:293`) y desde entonces solo se llega escribiendo `?tab=`.
Un `grep` sobre todo `SourceCode/` confirma que **ningún botón, menú ni ruta
emite esas URLs**: las tres pantallas están vivas, con su lógica entera, y son
inalcanzables desde la UI.

Esto **es anterior a la ola 3a** y el panel nativo lo replica tal cual (no
inventa una puerta que el HTML no tiene). Queda anotado acá porque el ítem del
cierre pedía comprobar justamente que esas tres siguieran accesibles: **no lo
están, ni antes ni ahora**. Si se las quiere de vuelta, es una fila más en `NAV`
(o en el `#menu` del HTML), no un porteo.

---

## 3. Persistencia — verificada contra el disco y contra un reinicio real

La trampa conocida del repo dice: **`Settings.Save()` es no-op si no hay perfil
cargado**; `tool.json` cubre la geometría del implemento, pero
dirección/antena/IMU/relés/u-turn/tram **no persisten**. Tres de las cuatro
pestañas de esta ola son de esa familia, así que no se creyó ningún reporte: se
verificó de nuevo en el cierre.

**Procedimiento** (motor headless de banco, sobre el perfil real de la máquina):

1. `POST` a las tres secciones con valores testigo — antena `2.61 / −0.44 / 0.17`,
   dimensiones `2.41 / 1.93 / 1.11`, vehículo `vehicle_type 2`;
2. lectura de `<Documentos>\AgOpenGPS\Vehicles\PilotX.XML` **en disco**: los 7
   campos escritos (`setVehicle_antennaHeight 2.61`, `antennaPivot −0.44`,
   `antennaOffset 0.17`, `wheelbase 2.41`, `trackWidth 1.93`,
   `hitchLength 1.11`, `vehicleType 2`) — con signo;
3. **`taskkill /F` al motor** y arranque limpio;
4. `GET /api/aog/config` tras el reinicio: **los 7 valores volvieron enteros**;
5. banco restaurado al estado original (tipo 0, `2 / 2.05 / 1`, antena `3 / 0 / 0`)
   y confirmado contra el XML. No se dejó basura.

**Conclusión: el «Guardado ✔» de las tres pestañas editables NO miente.**

### 3.1 Por qué persiste, y por qué eso es frágil

Persiste porque `PilotX.GuidanceEngine/Program.cs:117` **se crea el perfil
«PilotX» al arrancar** cuando `RegistrySettings.vehicleFileName` está vacío ⇒
en el motor de hoy siempre hay perfil ⇒ `Settings.Default.Save()` no es no-op.

Camino a disco de cada campo:

| Campo | Persiste por | ¿Red de seguridad? |
|---|---|---|
| `vehicle_type` | perfil `Vehicles\PilotX.XML` | **no** |
| `wheelbase`, `track_width` | perfil `Vehicles\PilotX.XML` | **no** |
| `hitch_length` | perfil **y** `GuidanceEngineData\tool.json` | sí |
| `antenna_height`, `antenna_pivot`, `antenna_offset` | perfil `Vehicles\PilotX.XML` | **no** — `ToolGeometryStore` no guarda NADA de antena |

**Si alguien saca el bootstrap del perfil en `Program.cs`, seis de esos siete
campos vuelven a fábrica en cada arranque y las tres pestañas pasan a cantar un
«Guardado ✔» que dura hasta el reinicio — sin que se rompa ningún test.**
Altura y pivote de antena mal = corrección de rolido y punto de pivote mal: es
error de plata en el lote, y silencioso.

Recomendado (carril motor, no UI): un test de regresión que postee, fuerce un
`Load()` y falle si el valor no vuelve; o sumar `wheelbase`, `track_width` y la
antena a `ToolGeometryStore`.

### 3.2 «Guardar, releer y comparar» NO detecta la pérdida — no lo intenten

`EngineConfigVehiculoService.BuildSnapshot()` arma el snapshot leyendo
`Settings.Default` **en memoria** (líneas 87-106), no el XML del disco. Cuando
`Settings.Save()` es no-op el valor en memoria igual quedó cambiado, así que el
`GET` siguiente devuelve el valor nuevo y la comparación **siempre da OK**.

Lo único que revela la pérdida es **reiniciar el motor**. Cualquier verificación
post-guardado hecha desde la UI es teatro. Está anotado acá porque el pendiente
del shell (§4.1) lo había propuesto como arreglo y no sirve.

### 3.3 La otra trampa: `/api/tool` vs `/api/implemento`

Ninguna de las cuatro pestañas de esta ola toca `/api/tool` ni
`/api/implemento`. Las tres editables van a
`POST /api/aog/config/{vehiculo|dimensiones|antena}` —
`ConfigVehiculoController`, el mismo endpoint que usa `config.js` — y Resumen
solo hace `GET /api/aog/config`. La desincronización de esas dos configuraciones
de implemento **no las roza**. La próxima pestaña (Implemento › Enganche) sí
entra en ese terreno: verificar cuál usa antes de escribir una línea.

---

## 4. Pendientes por pestaña

Nada de esto bloquea el cierre de la ola: son decisiones de producto, cosas que
solo se ven con fierro, o deuda preexistente que el porteo destapó. Lo que sí
está prohibido es marcarlas como "anda".

Vienen de las revisiones adversariales de cada unidad (los
`pendiente-config-*.md` del scratchpad de la sesión, volcados acá para que no
vivan en una carpeta temporal).

### 4.1 Shell + Resumen (`8c16d310` + `90fe14ca`)

1. **El botón Guardar nunca vuelve al estado neutro.** `MarcarSucio()` lo pone
   en «Guardar» y nadie lo devuelve a «Sin cambios» tras un guardado exitoso ni
   al cambiar de pestaña (`MostrarTabAsync` solo toca `IsVisible`). La spec pedía
   tres estados (neutro / dirty / ok-1,5 s). **No miente al revés**: tocarlo sin
   cambios responde «Sin cambios» y no postea nada (`OnGuardarClick` chequea
   `HayCambios`), así que no hay riesgo de creer guardado algo que no se guardó.
   Es del shell y lo comparten las cuatro pestañas: sale en una pasada propia.
2. **`Detach()` guarda a ciegas al cerrar.**
   `_ = t.AlSalirAsync()` fire-and-forget, y el CTS se cancela en la línea
   siguiente. Si el operario elige «Cosechadora» y cierra con la ✕, el POST sale
   pero si falla **nadie se entera**: la card ya se cerró y el `C.Estado` con el
   error no se ve. Peor: con un NUD en rojo, cerrar el panel **descarta lo
   tipeado sin aviso** (la spec pide "no cierra + marca en rojo"). Lo llaman ~15
   `Show*` de `MainWindow`. Es paridad con el `visibilitychange` del HTML —
   allá el usuario seguía en la página y veía el `#estado` en rojo. Arreglo
   natural: que el camino de cierre devuelva algo y el host muestre un toast.
   **Atenuante**: navegar entre pestañas SÍ respeta el contrato (`IrATabAsync`
   no navega si `AlSalirAsync` devuelve false), que es el camino frecuente.
3. **Guardar navegando no deja rastro.** En `IrATabAsync` la pestaña que se va
   postea y pone «Guardado ✔», y acto seguido `MostrarTabAsync` hace
   `SetEstado("", "")` y lo borra (`ConfigPanel.axaml.cs:530`). El operario que
   corrige la altura de antena y toca la pestaña siguiente **no ve ninguna
   confirmación**: indistinguible de «no se guardó». El guardado SÍ ocurre
   (verificado); lo que falta es el aviso.
4. **Landmine de `Traductor.Aplicar`.** `Traductor.Uno()` cachea el texto
   ORIGINAL de cada control la primera vez que lo ve y lo reescribe en cada
   pasada. Ya se reordenó el shell (Aplicar primero, textos dinámicos después,
   con el porqué al lado), pero **una pestaña futura que NO recree sus
   `TextBlock` en `Rebuild()` y pinte valores en `Live()` va a ver esos valores
   restaurados al original en el próximo cambio de pestaña.** Las cuatro
   actuales se salvan porque `Rebuild()` hace `Children.Clear()`. Conviene
   dejarlo escrito en `PORTING-AVALONIA.md`.
5. **Dos puertas distintas a la misma configuración.** `RouteCockpitCommand`
   manda `config_form` al panel nativo, pero `"directorios"` y
   `"asistente_direccion"` (`MainWindow.axaml.cs` ~4012 y ~4035) siguen abriendo
   la página HTML entera. El operario tiene dos entradas que muestran UIs
   distintas para lo mismo. Redirigirlos cuando haya más pestañas portadas.
6. **Falta «Sonidos» en el grupo Otros** (decisión, no olvido). `config.html`
   lo tiene como `data-mod` dentro de «Otros»; el `NAV` nativo no. No es pérdida
   de acceso — hay panel nativo en **Barra de la pasada › Herram. › Sonidos** (y
   `ayuda.html` ya lo dice así) y además se llega por «Módulos y más…». Anotado
   por si se quiere paridad 1:1 del menú.
7. **Tamaño de la card sin validar en 10".** `MaxWidth="940" MaxHeight="660"`.
   En 1080×720 entra, pero apretado, con el menú lateral de 196 px + grilla de
   dos columnas. Es el ítem a mirar antes de declarar `Settings48` como "anda".
8. Cosmético aceptado: `CfgCtx.NumJs` usa `"0.####"` y el HTML imprime el número
   crudo de JS. Con los valores reales del Lookahead (1 decimal, rango 0.2-22)
   coinciden; recién diferirían con más de 4 decimales, que la UI no genera.

### 4.2 Vehículo › Tipo (`200c0b63` + `0e3b49cc`)

1. **Divergencia de la memoria «pulverizadora» — asumida.** El `sub` no existe
   en el wire (el motor solo conoce 0/1/2). El HTML lo guarda en `localStorage`
   del WebView y el panel nativo en `Documentos\AgOpenGPS\pilotx_ui_prefs.json`.
   La cabina puede resaltar «Pulverizadora» y el celular «Rígido». Es estético;
   unificarlo pediría un endpoint nuevo = cambio de contrato.
2. **Las 4 cards no envuelven en ventana angosta.** `ConfigPanel.axaml` mete el
   contenido en un `ScrollViewer` con `HorizontalScrollBarVisibility="Auto"`, así
   que el `WrapPanel` de `CfgUi.Grilla()` se mide con ancho infinito y nunca
   envuelve. A 1080 px entran holgadas (4 × 162 = 648 px); en una ventana más
   angosta —o con una quinta opción— la Pulverizadora queda fuera de pantalla.
   Es del shell: afecta a toda pestaña que use `Grilla()`.
3. **Rótulo distinto en cabina y en el celular.** El menú nativo dice «Tipo»;
   `config.html` sigue diciendo «Tipo y marca». El nombre viejo miente (la
   elección de marca murió el 2026-08-10), así que el nativo está bien; la
   divergencia queda hasta que alguien toque el menú del HTML, que en este
   porteo está **prohibido** tocar por la PWA.
4. **Sin datos, la card de Rígido queda igual resaltada.** Con el Hub caído el
   tipo se asume 0. Las cards están apagadas (opacidad 0.55, sin hit-test) y
   arriba se lee «PilotX no responde…», así que no se puede elegir a ciegas,
   pero el resaltado afirma algo que no se sabe. El HTML es peor (revienta en
   `snap.vehiculo`). Arreglo barato si molesta en cabina: no resaltar ninguna.

### 4.3 Vehículo › Dimensiones (`cd979e3d` + `3c6bcc13`)

1. Hereda los puntos 1 y 2 del shell (§4.1): botón que no vuelve a neutro y
   cierre que descarta lo tipeado con un campo en rojo.
2. **`wheelbase` / `track_width` cuelgan de UN solo mecanismo** (el bootstrap
   del perfil). Ver §3.1. Recomendado: sumarlos a `ToolGeometryStore` como
   cinturón y tiradores, igual que ya se hizo con la geometría del implemento.
3. Corregido en `3c6bcc13`: la cabecera del archivo decía que no persistían
   —era falso— y si el que quedaba en rojo era el enganche con su fila oculta
   (implemento fijo o frontal), el operario leía «Revisa los valores marcados en
   rojo» **sin ver nada rojo** y el panel no dejaba guardar ni navegar. Ahora la
   fila se destapa cuando su validación falla.
4. **Falta cabina**: legibilidad del diagrama, que el teclado propio no tape el
   NUD del enganche y el ancho de la etiqueta larga.

### 4.4 Vehículo › Antena (`5ac55fc4`)

1. Hereda los tres puntos del shell (§4.1 · 1, 2 y 3).
2. **Los NUD están copiados, no compartidos — y ya divergieron.** `Nud()`,
   `FilaNud()`, `LeerNud()`, `SetTexto()`, `Invalido()`, `BgNud` y `BgNudMal`
   están duplicados byte a byte entre `DimensionesTab.cs` y `AntenaTab.cs`; el
   `LeerNud` de Dimensiones no tiene el parámetro `conSigno` (allá ningún campo
   lleva signo, acá el pivote sí). Un arreglo al parseo o al teclado llega a una
   sola copia. Con la próxima pestaña con NUD (`thitch`, `tooloffset`,
   `toolpivot`, `tsettings`…) son media docena de copias. **Recomendado: subir
   la familia a `CfgUi` en una pasada propia**, revisable como refactor y no
   escondida adentro de un porteo.
3. **La antena no tiene la red de `tool.json`.** Ver §3.1. Es el campo donde el
   no-op silencioso saldría más caro.
4. **Con la pestaña abierta, la antena no se repinta sola.** `Live()` solo
   reconstruye si cambió el estado de conexión, mientras el shell sí refresca el
   snapshot cada 3 s. Si `is_metric` cambia desde otra pantalla, el rótulo sigue
   diciendo «cm»; si alguien edita la antena desde el celular, la cabina muestra
   los valores viejos y al guardar los pisa. Es la misma semántica del HTML, y
   **no reconstruir bajo el dedo es correcto**: rearmar el árbol tira el foco y
   cierra el teclado nativo. Se deja como está.
5. **El campo dice cm y alguien va a escribir metros.** Heredado del HTML y del
   `FormConfig` original: un operario que piense en metros y escriba «2,75»
   guarda **3 cm** de altura de antena (2,75 → abs → clamp → redondeo → 3), sin
   ningún aviso. El rótulo «cm» es toda la defensa. No se cambia acá porque
   hacerlo en la cabina y no en el celular deja las dos pantallas validando
   distinto contra el mismo motor. Forma barata si se decide: mostrar el
   equivalente en metros al lado del campo (solo lectura), sin tocar el wire.
6. **Falta cabina**: que el teclado propio no tape el NUD de offset (es el más
   abajo de la segunda carta), la legibilidad del diagrama a 420 px y que los
   tres radios-imagen de 108×122 entren sin scroll horizontal.

### 4.5 Quirks del HTML que las tres pestañas editables NO replican (a propósito)

`config.js` pone `dirty = false` **antes** del POST, así que un guardado fallido
deja el segundo intento sin mandar nada y el botón canta «Guardado» sin haber
guardado. En las tres pestañas nativas el `dirty` se limpia **solo si el POST
salió bien**.

Y el botón flotante del HTML dice «Guardado ✔» aunque no haya cambios; el shell
nativo responde «Sin cambios» y no postea (arreglado en `0e3b49cc`, cuando
apareció la primera pestaña con `TieneGuardar = true`).

---

## 5. Qué falta de `config.html`

`pages/config.html` son 1086 líneas y `js/config.js` 1881. La ola 3a portó
**4 de 19 pestañas** — en volumen, unas 105 de las ~714 líneas de `<section
data-tab=…>` (~15 %) y unas 187 de las ~1550 de los handlers `tabs.*` (~12 %).

### 5.1 Pestañas pendientes

| Grupo | `?tab=` | Pantalla | Spec | Notas |
|---|---|---|---|---|
| Implemento | `tconfig` | Enganche | `spec-04` | **Primera que pisa la trampa `/api/tool` vs `/api/implemento`** |
| Implemento | `thitch` | Distancias | `spec-05` | NUD — subir la familia a `CfgUi` antes (§4.4.2) |
| Implemento | `tooloffset` | Offset | `spec-06` | **Signo**: `tool_offset` es al revés que el de antena |
| Implemento | `toolpivot` | Pivote | `spec-07` | |
| Implemento | `tsettings` | Timing | `spec-08` | |
| Secciones | `tsections` | Secciones | `spec-09` | **La más grande**: 103 líneas de HTML + 170 de JS |
| Secciones | `tswitches` | Switches | `spec-10` | |
| Secciones | `amachine` | Máquina | `spec-11` | Familia que **no** persiste sin perfil |
| GPS / IMU | `heading` | Rumbo | `spec-12` | Familia que **no** persiste sin perfil |
| GPS / IMU | `roll` | Rolido | `spec-13` | Familia que **no** persiste sin perfil |
| Otros | `uturn` | U-Turn | `spec-14` | Familia que **no** persiste sin perfil |
| Otros | `tram` | Tram | `spec-26` | Familia que **no** persiste sin perfil |
| *(fuera del menú)* | `relay` | Pines relé | — | Sin puerta desde la UI (§2.1) |
| *(fuera del menú)* | `display` | Pantalla | — | Sin puerta desde la UI (§2.1) |
| *(fuera del menú)* | `botones` | Botones | — | Sin puerta desde la UI (§2.1) |

Las cinco de la familia «no persiste» (`amachine`, `heading`, `roll`, `uturn`,
`tram`) son las que hay que verificar **con reinicio del motor** antes de
escribir un «Guardado ✔» — y recordar que comparar el `GET` post-guardado no
sirve (§3.2).

### 5.2 El shell de módulos (`data-tab="modulo"`)

La pestaña `modulo` de `config.html` es el contenedor `<iframe>` de **22
módulos** (`data-mod=`). Hoy el panel nativo la resuelve con el botón «Módulos y
más…», que abre la página entera en el WebView. Es funcional, pero implica que
**la ola 2 no se nota en cabina**: el camino real a QuantiX/VistaX/Nodos sigue
siendo Configuración › Módulos → `data-mod="quantix.html"` → la PÁGINA, no el
panel nativo que ya existe (ver `MIGRACION-OLA2.md` §2.1.1).

**Decisión de producto pendiente, y es la que más rinde**: que el submenú
Módulos rutee a los paneles nativos ya portados. Es una tabla de traducción
`data-mod` → comando de `RouteCockpitCommand`, no un porteo.

### 5.3 Orden sugerido para la ola 3b

1. **Pasada de shell** (`ConfigPanel`): los puntos 1, 2 y 3 de §4.1 — botón a
   neutro, cierre que no descarta en silencio, y confirmación que sobrevive a
   la navegación. Las cuatro pestañas actuales se benefician y las siguientes
   nacen bien.
2. **Refactor de los NUD a `CfgUi`** (§4.4.2), antes de que sean seis copias.
3. **Implemento completo** (`tconfig`, `thitch`, `tooloffset`, `toolpivot`,
   `tsettings`): son cinco pestañas de la misma familia, comparten diagrama y
   NUD, y no arrastran la trampa de persistencia (`tool.json` las cubre). Ojo
   con `/api/tool` vs `/api/implemento` y con el signo del offset.
4. **`tsections` + `tswitches` + `amachine`**: la más cara del lote.
5. **`heading`, `roll`, `uturn`, `tram`**: las que no persisten sin perfil.
   Verificar con reinicio, sí o sí.
6. **Ruteo del submenú Módulos a los paneles nativos** (§5.2) — se puede hacer
   en cualquier momento y es lo que hace visible la ola 2.
7. Recién con todo eso: `config_form` deja de necesitar el WebView, y se pueden
   redirigir `"directorios"` y `"asistente_direccion"` (§4.1.5).

`pages/config.html` y `js/config.js` **no se borran nunca**: la PWA del celular
y el Hub remoto los siguen sirviendo.

---

## 6. Nota sobre `ayuda.html`

`ayuda.html` se actualizó en `200c0b63` con la ruta verificada contra el menú
real del `ConfigPanel` (**Configuración › Vehículo › Tipo**), incluido el aviso
de que elegir cosechadora pasa el implemento a frontal y que el cambio recién
queda con Guardar.

Los otros commits de la ola **no la tocaron, y con razón**: Dimensiones y
Antena están donde siempre estuvieron (`Configuración › Vehículo › …`). Cambió
quién dibuja esa pantalla, no el camino del operario. Este documento tampoco la
toca: es documentación interna, no algo que el operario vea.
