# Migración ola 3b — las pestañas de Implemento y Secciones

Rama `codex/pilotx-ui-new` · cierre 2026-08-16

La [ola 3a](MIGRACION-OLA3A.md) dejó nativo el **contenedor** de Configuración
(menú acordeón, footer, botón Guardar) y las **cuatro pestañas de vehículo**.
La **ola 3b** sigue con el mismo bisturí y se lleva las **siete pestañas que
definen la geometría del implemento y el corte de secciones**: Enganche,
Distancias, Offset, Pivote, Timing, Secciones y Switches.

Sigue siendo **strangler fig, no big-bang**: lo que todavía no está portado se
abre desde el MISMO menú nativo, en el WebView, con el deep-link que la página
ya entendía (`config.html?tab=…`). `pages/config.html` y `js/config.js` **no se
tocan ni se borran**: los usa la PWA del celular.

Guía de porteo: [`PORTING-AVALONIA.md`](PORTING-AVALONIA.md).

---

## 1. Estado de la ola

Verificación del cierre (2026-08-16):

- `dotnet build SourceCode\AgOpenGPS.sln -c Release` → **0 errores**, 11
  advertencias. Las 11 son **preexistentes** y ninguna cae en un archivo de la
  ola: `PilotX.UI\Views\DireccionPanel.cs` (CS8625 ×6),
  `PilotX.UI\Views\MapGlSurface.cs` (CS0618 ×3, API de OpenTK obsoleta),
  `PilotX.Desktop\CrashHandler.cs` (CS8604 ×2) y
  `PilotX.Desktop\TecladoLinux.cs` (CS8603).
- `dotnet test SourceCode\AgOpenGPS.sln -c Release --no-build` → **374/374
  verdes, 0 con error, 0 omitidos**:

  | Assembly | Pruebas |
  |---|---|
  | `AgroParallel.Services.Tests` (net8.0) | 261 |
  | `AgOpenGPS.Core.Tests` (net48) | 53 |
  | `BenchX.Tests` (net9.0) | 36 |
  | `PilotX.Cockpit.Bars.Tests` (net9.0) | 21 |
  | `AgLibrary.Tests` (net48) | 3 |

### 1.1 Los tests NO cubren nada de esta ola — hay que decirlo

**Ningún test toca el `ConfigPanel` ni una sola de las siete pestañas
portadas.** Los 374 son exactamente los mismos que ya estaban verdes al abrir la
ola: son de servicios, de core, de BenchX y de las barras del cockpit. Este
porteo es UI de Avalonia y **no agregó ni una prueba**.

O sea: el "374/374 verdes" es una señal de que **no se rompió nada de lo que ya
estaba probado**, y nada más. No es evidencia de que las pestañas anden. Lo que
sí es evidencia está en §2 (persistencia medida contra disco y reinicio) y en
§3 (fallback probado contra el motor). La prueba de cabina, con táctil y
teclado nativo, **falta en las siete**.

Por eso las siete van al tablero de cierre como `Prueba:`, **ninguna como
`Cierra:`**.

### 1.2 Pestañas cerradas en la ola 3b

| Pestaña (menú) | `?tab=` | Clase nativa | API que escribe | Persistencia verificada con reinicio | Commits |
|---|---|---|---|---|---|
| Implemento › Enganche | `tconfig` | `ConfigEditor/EngancheTab.cs` | `/api/aog/config/enganche_estilo` | **SÍ** — los 4 flags de estilo + el `hitch_length` corregido sobreviven | `30924ba1`, `d7a8205f` |
| Implemento › Distancias | `thitch` | `ConfigEditor/DistanciasTab.cs` | `/api/aog/config/enganche_dist` | **SÍ** — `hitch_length`, `trailing_hitch_length` y `tank_trailing_hitch_length` sobreviven | `5f4b7aff`, `9a56bfbc` |
| Implemento › Offset | `tooloffset` | `ConfigEditor/OffsetTab.cs` | `/api/aog/config/offset_implemento` | **SÍ** — `tool_offset` y `tool_overlap`, con signo, sobreviven | `63b6b3d9`, `80e3ffd4` |
| Implemento › Pivote | `toolpivot` | `ConfigEditor/PivoteTab.cs` | `/api/aog/config/pivote` | **SÍ** — `trailing_tool_to_pivot_length`, con signo, sobrevive | `9e88b497`, `cb5229e0` |
| Implemento › Timing | `tsettings` | `ConfigEditor/TimingTab.cs` | `/api/aog/config/timing` | **SÍ** — los 3 tiempos sobreviven al reinicio… **pero los pisa el implemento central**, ver §4.1 | `46c9636a`, `935a438c` |
| Secciones › Secciones | `tsections` | `ConfigEditor/SeccionesTab.cs` | `/api/aog/config/secciones` **+ `PUT /api/implemento`** | **SÍ** — modo, cantidad, anchos desiguales, zonas, cortes y los trenes sobreviven | `e1cf144b`, `69006864` |
| Secciones › Switches | `tswitches` | `ConfigEditor/SwitchesTab.cs` | `/api/aog/config/switches` | **PARCIAL — 3 de 5.** `work_enabled` y `steer_enabled` **NO vuelven** tras reiniciar el motor. Ver §4.2 | `53b3ce47`, `1008a107` |

**7 pestañas cerradas. Ninguna probada en cabina.**

Archivos nuevos de la ola:
`Views/ConfigEditor/{EngancheTab,DistanciasTab,OffsetTab,PivoteTab,TimingTab,SeccionesTab,SwitchesTab}.cs`
y 11 PNG en `Assets/config/`. Además se tocaron
`Views/ConfigPanel.axaml.cs` (filas de `NAV` + `CrearTab`),
`Services/ConfigVehiculoClient.cs`, `Services/ImplementoClient.cs`,
`Views/ConfigEditor/CfgCtx.cs` y
`wwwroot/pages/ayuda.html` (el manual, en el mismo commit que el cambio).

### 1.3 Acumulado: 11 de 19 pestañas

`config.html` tiene **19 pestañas reales** (`data-tab=`, sin contar el host
`modulo`). Después de la ola 3b:

- **11 nativas** — `summary`, `vconfig`, `vdimensions`, `vantenna` (ola 3a) +
  las 7 de esta ola.
- **8 en WebView** — `amachine`, `heading`, `roll`, `uturn`, `tram` (en el menú)
  y `relay`, `display`, `botones` (fuera del menú, ver §3.2).

---

## 2. Persistencia — cómo se midió y qué dio

### 2.1 El método, porque el atajo miente

`EngineConfigVehiculoService.BuildSnapshot` **lee `Settings.Default` en
memoria**. Entonces "guardar, releer y comparar" (round-trip) devuelve siempre
lo que uno acaba de mandar, **aunque el disco no se haya tocado nunca**. Es un
verde falso.

El único método que vale, y el que se usó en las siete pestañas:

```
POST  ->  mirar el valor EN DISCO           (GuidanceEngineData\tool.json
                                             o Vehicles\<perfil>.XML)
      ->  matar el motor (Stop-Process)
      ->  arranque limpio
      ->  GET                                ← acá recién se sabe
```

El motor headless se lanza **desde `bin\Release\net9.0\`, nunca desde
`win-x64\`** (la subcarpeta RID rompe `ResolveWwwroot()`).

### 2.2 Re-verificado de cero en el cierre, no copiado de los commits

En este cierre se volvieron a correr **dos ciclos completos con reinicio real**,
sin creerle al reporte de los commits — uno del lado que debía dar bien y otro
del lado que debía dar mal:

**a) Offset (el lado "sí persiste"):**

```
GET  /api/aog/config .offset            -> {tool_offset:0, tool_overlap:0}   (baseline)
POST /api/aog/config/offset_implemento  -> {"ok":true}
     {"tool_offset":-0.37,"tool_overlap":0.11}
tool.json en disco                      -> "tool_offset": -0.37, "tool_overlap": 0.11
Stop-Process PilotX.GuidanceEngine      -> motor muerto
arranque limpio + GET .offset           -> {tool_offset:-0.37, tool_overlap:0.11}   ✔ PERSISTE
```

**b) Switches (el lado "no persiste"):**

```
GET .switches   -> work_enabled:false, work_active_low:true,  work_manual:false,
                   steer_enabled:false, steer_manual:false          (baseline)
POST /api/aog/config/switches  {los 5 en true, salvo work_active_low:false} -> {"ok":true}
GET inmediato   -> devuelve los 5 como se mandaron   ← round-trip, NO prueba nada
Vehicles\PilotX.XML en disco:
    setF_isWorkSwitchEnabled              = True
    setF_isWorkSwitchManualSections       = True
    setF_isWorkSwitchActiveLow            = False
    setF_isSteerWorkSwitchManualSections  = True
    setF_isSteerWorkSwitchEnabled         = True
    setF_isRemoteWorkSystemOn             = True
Stop-Process + arranque limpio + GET .switches:
    work_active_low       = false   ✔ PERSISTE
    work_manual_sections  = true    ✔ PERSISTE
    steer_manual_sections = true    ✔ PERSISTE
    work_enabled          = false   ✘ SE PERDIÓ  (el XML dice True)
    steer_enabled         = false   ✘ SE PERDIÓ  (el XML dice True)
```

El hallazgo de los commits queda **confirmado de forma independiente**. El banco
se dejó exactamente como estaba (offset en 0/0, switches en el baseline, timing
en 1 / 0,5 / 0) y el motor apagado.

> **Trampa de PowerShell 5.1 al medir:** `/api/aog/config` responde con **BOM**,
> y `ConvertFrom-Json` explota con «Primitivo JSON no válido». Hay que recortar
> hasta el primer `{` antes de parsear. Si no, el snapshot parece vacío y se
> concluye cualquier cosa.

### 2.3 Quién persiste de verdad: `tool.json`, no el XML

En el banco headless `Settings.Default.Save()` **es no-op sin perfil de vehículo
real**. La geometría del implemento (Enganche, Distancias, Offset, Pivote,
Timing, Secciones) baja a disco por **`ToolGeometryStore.Guardar()`** y se
reaplica al arrancar con **`ToolGeometryStore.Cargar()`**, en
`GuidanceEngineData\tool.json`.

Consecuencia a tener presente: **la red es UNA sola**. Si alguien saca el store,
le cambia la carpeta o borra `tool.json`, **toda la geometría del implemento
vuelve al default en silencio**, aunque el perfil de vehículo esté elegido y su
XML tenga los números. No hay segunda red.

Los `switches` son el caso distinto: esos **sí** llegan al XML del perfil
(`Vehicles\PilotX.XML`, verificado arriba) — el problema es del lado de la
lectura, no de la escritura (§4.2).

---

## 3. El fallback a WebView — verificado, sigue vivo

Es el punto que más caro sale equivocar: si el menú nativo se comiera una
entrada, el operario perdería acceso a su configuración sin que nada avise.

**Cadena en código** (`ConfigPanel.axaml.cs`): `NAV` tiene 17 filas; las 5 que
faltan del menú (`amachine`, `heading`, `roll`, `uturn`, `tram`) siguen **sin**
`Nativa = true`, así que `IrATabAsync` cae en la rama de la línea 486:

```csharp
if (!EsNativa(tab))
{
    _ = _ctx.Client?.TecladoAsync(false);
    OnRequestHtml?.Invoke("pages/config.html?tab=" + tab);
    return;
}
```

**Verificado en banco** (motor headless `--webhost`, lanzado desde
`bin/Release/net9.0/`) — las 8 URLs de deep-link que quedan, más la página
entera:

| Grupo del menú nativo | `?tab=` | Nativa | HTTP |
|---|---|---|---|
| Secciones › Máquina | `amachine` | no | **200** (65 618 bytes) |
| GPS / IMU › Rumbo | `heading` | no | **200** |
| GPS / IMU › Rolido | `roll` | no | **200** |
| Otros › U-Turn | `uturn` | no | **200** |
| Otros › Tram | `tram` | no | **200** |
| — fuera del menú — | `relay` | no | **200** |
| — fuera del menú — | `display` | no | **200** |
| — fuera del menú — | `botones` | no | **200** |
| Botón «Módulos y más…» | *(sin tab)* | no | **200** |

Las 8 secciones siguen existiendo dentro de `config.html`
(`grep -o 'data-tab="…"'` las devuelve todas), así que no es un 200 vacío: la
página tiene el contenido.

### 3.1 Los módulos embebidos siguen alcanzables

El botón **«Módulos y más…»** al pie del menú nativo
(`ConfigPanel.axaml.cs:391` → `OnRequestHtml("pages/config.html")`) abre la
página entera. Desde ahí se llega a los **25 `data-mod=`** (24 páginas únicas;
`quantix.html` aparece dos veces, una con `?tab=shape`):

`hub` · `quantix` · `quantix?tab=shape` · `vistax` · `flowx` · `sectionx` ·
`linex` · `stormx` · `nodos` · `camaras` · `insumos` · `mapas` ·
`calculadora-siembra` · `pid-lab` · `pwm-diag` · `orbitx` · `firmwares` ·
`actualizar` · `pwa-qr` · `wifi` · `sistema` · `eventos` · `debug` · `sonidos` ·
`ayuda`

**Sin ese botón, el operario que entra al panel nativo perdería los 25.**

### 3.2 Relé / Pantalla / Botones: sin regresión, pero sin puerta

`relay`, `display` y `botones` **no están en el menú nativo porque tampoco
están en el menú del HTML**: los sacaron el 2026-08-03 y desde entonces solo se
llega escribiendo `?tab=` a mano. Las tres pantallas están vivas y responden
200, pero **ningún botón ni ruta de la UI las emite**. Es un pendiente heredado
de la ola 3a, no de esta.

---

## 4. Trampa 2 — las dos configuraciones de implemento

Conviven **dos** configuraciones que se llaman "implemento" y **no están
sincronizadas**:

| Superficie | Qué modela | Respaldo |
|---|---|---|
| `/api/aog/config/*` (secciones del snapshot) | geometría del guiado, corte de secciones, look-ahead | `GuidanceEngineData\tool.json` vía `ToolGeometryStore` |
| `/api/implemento` | implemento central: surcos, semillas/ha, trenes, nodos | `implementos\*.json` vía `ImplementoService` |
| `/api/tool` | *tercera* superficie (`ToolConfigDto`, camelCase) | la usa `QuantiXEditorClient`, **ninguna pestaña de config** |

**Cuál usa cada pestaña portada** — determinado leyendo `config.js` antes de
escribir código, no de memoria:

- **Enganche, Distancias, Offset, Pivote, Timing, Switches** → **solo**
  `/api/aog/config/*`. **No tocan `/api/implemento`.**
- **Secciones (`tsections`)** → es la **única** que toca las dos, y a propósito:
  `POST /api/aog/config/secciones` para la geometría y `PUT /api/implemento`
  **solo para los trenes de siembra**. En ese orden: la geometría manda, y
  recién si sale bien va el implemento. Si el PUT falla, la geometría ya quedó
  guardada y el reintento postea solo los trenes
  («Secciones guardadas, trenes NO: …»).

Esto es **exactamente** lo que hace `config.js`: usa `/api/aog/config` para todo
y `/api/implemento` únicamente en `trnCargar()` (línea 646) y en el guardado de
trenes (línea 842). No se mezcló nada nuevo.

### 4.1 Hallazgo: el implemento central PISA lo que guardan las pestañas

**Es de backend, es preexistente (el HTML sufre lo mismo) y NO se arregló.**

`ImplementoService.SyncToolIfChanged` → `MapToToolConfig` → `SaveTool` baja la
copia del implemento al motor **cada vez que se guarda el implemento activo**
(`PUT /api/implemento`, activar otro implemento, aplicar plantilla).
`MapToToolConfig` no propaga varios campos, así que los pisa con defaults.

**Timing — medido en el banco:**

```
POST /api/aog/config/timing {look_ahead_on:4.3, off:0, turn_off_delay:1.9} -> ok
tool.json                       -> 4.3 / 0 / 1.9
GET  /api/implemento            -> lookahead_on_s 1 / off_s 0.5 / turn_off_delay_s 0
PUT  /api/implemento con el MISMO objeto del GET (sin cambiar NADA) -> ok
GET  /api/aog/config .timing    -> 1 / 0.5 / 0      ← REVERTIDO
tool.json en disco              -> 1 / 0.5 / 0      ← REVERTIDO EN DISCO
```

**Costo en el lote:** el look-ahead manda el corte anticipado que después
ejecutan SectionX/QuantiX. Volver en silencio de 4,3 s a 1 s de encendido son
metros sin sembrar en cada entrada de cabecera; al revés, doble siembra. El
operario guarda Timing, toca la pantalla de implemento, y el número volvió al
viejo **sin ningún aviso**.

**Pivote:** mismo mecanismo, también medido (`-0.5` → `PUT /api/implemento` sin
cambiar nada → `0`).

**Secciones:** `MapToToolConfig` no manda `IsSectionsNotZones` (el DTO lo trae
en `true` por default) ni `SectionWidths`, y `numSec` viene capado a 16.
Resultado: guardar el implemento en modo **zonas** deja el modo en
"individuales" con `min(surcos, 16)` secciones, y los **anchos desiguales
quedan todos iguales** (`defW = Width / n`).

Lo único que hace la UI nativa, y el HTML no: **avisa por toast** en los dos
casos y **relee el snapshot después del PUT**, para que la pantalla no siga
mostrando "zonas, 24 secciones" con el motor ya cortando en "individuales, 16".

**Arreglo de verdad (pendiente, backend):** que `MapToToolConfig` propague
`IsSectionsNotZones`, `SectionWidths`, `SectionWidthMulti`, `Zones`,
`ZoneRanges` y los tres look-ahead desde el Tool **actual** cuando el implemento
no los modela — el mismo criterio que ya usa para `Width`
(`d.AnchoTotalM > 0 ? … : actual.Width`).

### 4.2 Hallazgo: los `enabled` del switch remoto no sobreviven al reinicio

**Es un agujero DEL MOTOR, no del porteo.** Medido y reproducido en §2.2.

`work_enabled` y `steer_enabled` se escriben bien en el XML del perfil, pero el
snapshot los lee del **runtime** (`_engine.Mc.isWorkSwitchEnabled` /
`isSteerWorkSwitchEnabled`) y **nadie copia esos `Settings` a `Mc` al arrancar
en headless** — eso lo hacía `LoadSettings` de `FormGPS`, que no corre. Grep de
lecturas fuera de `Settings.cs` / `EngineConfigVehiculoService` / `PilotX.UI`:
sin resultados.

Para el operario: **guardó el switch y "se apagó solo"** después de reiniciar.

Además, el runtime arranca con los defaults del constructor de `CModuleComm`
(`isRemoteWorkSystemOn = false`, `isWorkSwitchActiveLow = true`). Como
`isRemoteWorkSystemOn` también vuelve a `false`, tras un reinicio el switch
físico **queda inerte** hasta el próximo guardado — no invertido, inerte (el
mensaje del commit original decía "al revés"; se corrigió en `1008a107`, y la
diferencia importa para quien vaya a arreglarlo).

**Derivado (P2, también en el HTML):** si el motor arrancó con `True` en el XML,
el GET devuelve `false`, y cualquier guardado de la pestaña —aunque el operario
solo haya tocado "Activo con contacto cerrado"— manda `work_enabled: false` y
**borra el `True` del XML**. Se arregla solo cuando se arregle lo de arriba; no
se parchea en la UI porque mandar menos de los 5 campos rompería la réplica del
HTML.

El arreglo vive en `PilotX.GuidanceEngine` (aplicar esos `Settings` a `Mc` al
arrancar) y **no se toca desde el carril de UI**. La pestaña no lo disfraza:
pinta siempre lo que devuelve el GET y nunca canta "Guardado ✔" sin POST
exitoso.

---

## 5. Pendientes por pestaña

Cada porteo tiene su revisión adversarial en
`scratchpad/migracion/pendiente-config-<tab>.md`. Resumen de lo que **no** se
cerró:

### `tconfig` — Enganche
- **P1** persistencia confirmada por código y por el ciclo del implementador; el
  revisor no la re-corrió en banco.
- **P2** estilo desconocido en el snapshot ⇒ el panel resalta "De arrastre".
- **P3** re-tocar la card ya elegida no ensucia (el HTML sí, y postea).
- **P5** `ConfigPanel.Detach()` guarda "a ciegas" (preexistente, del shell).

### `thitch` — Distancias
- **P1** `hitch_length` admite **4000 cm** en «Vehículo › Dimensiones» y **3000**
  acá — es el MISMO setting (`setVehicle_hitchLength`) con dos límites. Un 3500
  cargado desde Dimensiones **se clampea a 3000 en silencio**, sin diálogo, la
  primera vez que se guarda esta pestaña. Calcado del original
  (`config.js:180` y `:319`).
- **P2** cerrar el panel con un campo inválido pierde lo tipeado (shell).
- **P3** `tank_trailing_hitch_length` cuelga **solo** de `tool.json`.
- Ya arreglado en la ola: `AlSalirAsync` usaba `ModoActual()` recalculado en vez
  de `_modoPintado`, y con el refresco de 3 s podía **guardar el campo de una
  fila que el operario no tenía a la vista**, con el valor viejo, descartando lo
  tipeado. Es una carrera que el HTML no tiene (la introduce el polling nativo).

### `tooloffset` — Offset
- **P1** la red de persistencia es **una sola** (`ToolGeometryStore`).
- **P2** dos editores en vivo (panel nativo + PWA), sin refresco de valores.
- **P3** signos y sentido de marcha: **falta cabina**.

### `toolpivot` — Pivote
- **P1 (GRAVE, backend)** el implemento central pisa el pivote — §4.1.
- **P4** quirk replicado a propósito: **sin radio marcado se guarda NEGATIVO**
  (`config.js:461-463`). No se le copió el auto-marcado de Offset/Antena: el
  mismo gesto guardaría el signo al revés y el implemento quedaría armado del
  otro lado.

### `tsettings` — Timing
- **P1 (bloqueante para cabina)** el implemento central pisa los tres tiempos —
  §4.1, con la medición.
- **P2** la persistencia cuelga de un solo hilo (`tool.json`).

### `tsections` — Secciones
- **P1 (backend)** el `PUT /api/implemento` pisa geometría del guiado — §4.1.
  Mitigado en la UI (dos toasts + relectura del snapshot), **no arreglado**.
- **P2** la pantalla colapsa `numero_surcos` y `distancia_entre_surcos_m` a la
  cantidad de **secciones** del guiado (medido: 14 surcos / 0,52 m / 7,28 m de
  ancho). Es el modelo de datos (1 surco ↔ 1 sección) y lo consumen VistaX,
  QuantiX y SectionX. El HTML hacía lo mismo: **se documenta, no se cambia por
  las buenas.**
- **P3** `section_off_when_out` se sincroniza al implemento — divergencia
  consciente vs el HTML.
- **P4** la carta de trenes no reintenta el GET si el Hub estaba caído al entrar
  (paridad con `trnCargar()`).
- **P5** `AlgunCampoConFoco()` no cubre las celdas dinámicas ni los campos de
  tren.

### `tswitches` — Switches
- **P1** los `enabled` no sobreviven al reinicio — §4.2.
- **P2** un POST puede pisar en el XML un `enabled` que estaba guardado — §4.2.
- **P3** **falta la prueba de banco con un switch físico cableado**, en este
  orden: (1) `work_active_low` con el contacto CERRADO tiene que dar
  "trabajando" —si queda al revés **son secciones prendidas con el implemento
  levantado**—; (2) `work_manual_sections` deja el master en Manual/Auto según
  lo elegido; (3) repetir todo después de un reinicio.
- Ya arreglado en la ola: sin la sección `switches` en el snapshot la pestaña
  quedaba **editable**, y el primer toque posteaba `work_active_low = false`
  —que vale `true` por default en el motor—, **dando vuelta la lectura del
  switch físico sin que nadie lo pidiera**. Ahora las filas quedan apagadas con
  el chip «PilotX no informó la configuración de los switches».

---

## 6. Qué falta para que `config.html` deje de necesitar WebView

Faltan **8 pestañas** y **el shell de módulos**. En orden de lo que conviene
atacar:

1. **Las 5 del menú** — `amachine` (Máquina), `heading` (Rumbo), `roll`
   (Rolido), `uturn` (U-Turn), `tram` (Tram). Tienen spec escrita:
   `spec-11`, `spec-12`, `spec-13`, `spec-14` y `spec-26`. `heading` y `roll`
   son las más pesadas (viven contra el IMU en vivo).
2. **Las 3 huérfanas** — `relay`, `display`, `botones`: además de portarlas hay
   que **decidir si vuelven al menú**, porque hoy no las emite nada (§3.2).
3. **Los 25 módulos embebidos** — es la otra mitad del trabajo y no es de esta
   ola: son 24 páginas del Hub (`hub`, `quantix`, `vistax`, `flowx`, `sectionx`,
   `linex`, `stormx`, `nodos`, `camaras`, `insumos`, `mapas`,
   `calculadora-siembra`, `pid-lab`, `pwm-diag`, `orbitx`, `firmwares`,
   `actualizar`, `pwa-qr`, `wifi`, `sistema`, `eventos`, `debug`, `sonidos`,
   `ayuda`). Mientras exista una sola, el WebView2 sigue instanciándose.

**Aunque se porten las 19 pestañas, `config.html` y `config.js` NO se borran:**
los usa la PWA del celular. Lo que se puede retirar, recién cuando no quede
ningún `data-mod` sin portar, es la **dependencia del Desktop** con WebView2 —
que es la meta de la migración Avalonia total, diferida al 25/08 (el alcance
está aprobado; el plan vive fuera del repo, en el scratchpad de la sesión).

Además, dos deudas que **no** se cierran desde el carril de UI y hoy bloquean el
cierre real de Implemento en cabina:

- **`MapToToolConfig` tiene que dejar de pisar** look-ahead, modo de secciones y
  anchos (§4.1). Mientras siga así, Timing y Secciones se revierten solos.
- **El motor tiene que aplicar los `Settings` de switches a `Mc` al arrancar**
  (§4.2). Mientras siga así, el switch remoto se apaga solo en cada reinicio.

---

## 7. Resumen honesto

- **7 pestañas cerradas**, build en **0 errores**, tests **374/374** — pero
  **ningún test cubre lo portado**.
- **Persistencia real**: 6 de 7 pestañas persisten entero con reinicio del motor.
  **Switches persiste 3 de 5 campos** y eso está declarado, no tapado.
- **Ninguna probada en cabina.** Las siete van al tablero como `Prueba:`.
- Los dos hallazgos gordos (§4.1 y §4.2) son **de backend/motor y
  preexistentes**: la página HTML sufre exactamente lo mismo. No son regresiones
  del porteo, pero sí son lo que impide declarar "anda" en Timing, Secciones y
  Switches.
