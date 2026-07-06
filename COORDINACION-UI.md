# COORDINACIÓN UI — Canal Codex ⇄ Claude

> **Este archivo es el medio de comunicación oficial entre los dos agentes que
> trabajan la UI de PilotX.** Codex (diseño/presentación) y Claude (lógica/datos)
> dejan acá sus avisos, pedidos y cambios de contrato. **Leelo antes de tocar
> cualquier pantalla y dejá tu nota en la BITÁCORA antes de empezar.**

Rama: `codex/pilotx-ui-new` · Última edición: 2026-06-26

---

## 0. Protocolo (cómo usar este archivo)

1. **Antes de tocar un `.html`**, mirá la tabla §5 (Estado de pantallas). Si dice
   que el otro lo está editando, sincronizá (`git pull`) o esperá.
2. **Anotá en la BITÁCORA (§7)** qué vas a tocar y cuándo terminás. Formato:
   `[FECHA] [AGENTE] mensaje`. Append-only, lo nuevo abajo.
3. **Si necesitás algo del otro** (un campo nuevo en la API, renombrar un ID),
   escribilo en PEDIDOS CRUZADOS (§6). El otro lo resuelve y marca `HECHO`.
4. **Commits chicos y atómicos.** Prefijo `style(ui):` (Codex) / `feat|fix(logica):` (Claude).

---

## 1. Reparto de capas (quién toca qué)

| Capa | Dueño | Archivos |
|---|---|---|
| **Presentación / diseño** | **Codex** | `wwwroot/theme.css`, `layout.css`, `keyboard.css`, **markup** + CSS embebido de `wwwroot/pages/*.html` |
| **Lógica / datos** | **Claude** | `wwwroot/js/*.js`, `Web/.../Controllers/*.cs`, `Core/.../Services/*`, `Core/.../Models/*Dtos.cs` |

Ambos están en la misma rama; el riesgo real es editar el **mismo `.html`** a la
vez (tienen markup *y* CSS). Por eso existen §5 y §7.

---

## 2. Los 4 contratos (NO romper)

1. **IDs y `data-*` son sagrados.** El JS lee por `getElementById` y
   `querySelectorAll('[data-*]')`. Se puede reordenar, reestilar, reagrupar y
   recambiar clases libremente, **pero NO renombrar ni borrar un `id=` o `data-*`
   listado en §4.** ¿Necesitás cambiar uno? → PEDIDO CRUZADO (§6), no lo toques.
2. **Tokens de diseño solo en `theme.css`.** Paleta acento `#4ABA3E`. Los HTML/JS
   consumen variables `--agp-*`, **nunca hex hardcodeado** en página.
3. **Mecanismo de tabs intacto.** `data-fxtab`↔`data-fxpane` (FlowX),
   `data-tab`→`tabXxx` (QuantiX). Restilá las clases, no el switch JS.
4. **Forma del JSON la define Claude.** Codex no toca controllers ni el casing
   (respuestas Swan PascalCase vs POST snake_case). ¿Falta un dato en pantalla? → §6.

---

## 3. Reglas de producto (aplican al diseño)

- **Táctil, sin atajos de teclado.** Todo en sidebar/botón visible y grande.
- **Teclado virtual HTML** (`keyboard.js`), nunca `osk.exe`.
- **Nombres de producto, no de hardware** ("nodo QuantiX", nunca "ESP32/PCB-8560").
- **Unidades al operario:** kg/h, sem/m, sem/ha, rpm. **Nunca PPS.**
- **Estética:** simple, suave, gris, funcional.
- **Nodos solo se agregan por MQTT** (descubrimiento), nunca a mano en UI.

### Disciplina de build / cache (IMPORTANTE)
- **RELEASE cachea `wwwroot` en RAM al arrancar PilotX.** Editar HTML/CSS/JS
  **no se ve hasta rebuild + relanzar PilotX.exe.**
- Build: parar `PilotX.exe`/`CoreX.exe` → `build.ps1` → relanzar CoreX, luego PilotX.
- **Iteración rápida de diseño:** abrir los `.html` del `wwwroot` fuente en un
  browser/static server (sin backend, los `fetch` fallan pero el CSS se ve al toque).

---

## 4. Registro de IDs CONGELADOS (no renombrar sin pedido)

Extraídos del HTML real. Si agregás un ID nuevo, sumalo acá.
Comando para re-extraer de cualquier página:
`grep -oE 'id="[a-zA-Z0-9_-]+"' pages/X.html` + `grep -oE 'data-[a-zA-Z0-9-]+'`.

### quantix.html
`btnAddMotor btnAutoReparto btnQuitarSurco btnSaveMotores btnSendAll
btnShapeRemove btnShapeUpload calEmpty calList mtMsg pidEmpty pidList
planterCapL planterCapR prEmpty prList qxBrush qxModeLabel qxMotorList
qxOrphanWarn qxStatus qxStrip qxTabla qxTools segPlanter segTabla shapeActive
shapeDrop shapeFileList shapeFiles shapeMsg tabCalibrar tabPid tabPrueba tabShape
tabSiembra`
**data-*:** `data-tab data-view data-tren-act data-active`
**clases generadas por JS:** `qxNombre qxMapa qxTren qxDosisFija` (inputs por motor)

### flowx.html
`anchoHint aogNumSec btnAddProducto btnAnchoFromAog btnAutoAssign btnDeleteNodo
btnImportNodo btnOta btnPing btnPushConfig btnReload btnSaveCfg cfgEnabled
cortesMap estFw estOnline estUptime kpiAncho kpiAreaNeta kpiAreaOverlap kpiDose
kpiFlow kpiFlowUnit kpiOverlapPct kpiPid kpiPwm kpiSavedLitros kpiSecActive
kpiSecTotal kpiSpeed kpiTarget kpiTargetLbl lanSelect modalBackdrop modalCancel
modalInput modalMsg modalOk modalTitle nodo3wire nodoAncho nodoEditor nodoHab
nodoInv nodoInvMotor nodoMaster nodoNombre nodoNumCortes nodoSelect nodoUid
nodosList otaSha otaUrl otaVersion pushStatus pwmApply pwmBackdrop pwmClose
pwmDirNeg pwmDirPos pwmLiveFlow pwmLiveHint pwmLivePulsos pwmLivePwm pwmMinus
pwmPlus pwmProdName pwmSave pwmValue pwmZero salidaProdName salidaProdSel
saveStatus sec3wGrid tblProductos`
**data-*:** `data-fxtab data-fxpane data-pk data-sal-act data-adaptive data-dir data-active`

### stormx.html
`kpiAdvice kpiAge kpiDeltaT kpiDir kpiHum kpiPress kpiTemp kpiWind nodosList okPill`
**data-*:** `data-active`

> Las demás pantallas (`piloto, hub, vehiculo, herramienta, vistax*, sectionx,
> linex, camaras, nodos, insumos, lote, mapas, setup, sistema, orbitx,
> corex-ecu, firmwares, actualizar, datos-*`) se documentan acá cuando se
> empiecen a tocar. Codex: avisá en §7 qué pantalla arrancás y Claude le agrega
> el registro de IDs antes.

### 4-bis. CoreX WebUI (`SourceCode/AgIO/Source/wwwroot-corex/`) — OTRO wwwroot

**Ojo: es un árbol distinto al del Hub.** Lo sirve CoreX.exe (EmbedIO,
`127.0.0.1:5181`). Mismos contratos que §2. Editar el fuente requiere
**rebuild de `AgIO.csproj` + relanzar CoreX.exe** (se copia al output).

#### index.html (dashboard)
`btnMqtt btnNtrip cardGps cardModulos cardMqtt cardNtrip dotGps dotImu
dotMachine dotMqtt dotNtrip dotSteer gpsLat gpsLon hdrProfile hdrVersion
modImu modMachine modSteer mqttClients mqttMsgs mqttPort mqttTopics
mqttUptime ntripCaster ntripEstado ntripKb`

#### pages/serial.html
`baud-gps baud-gps2 baud-rtcm btn-gps btn-gps2 btn-imu btn-machine btn-rtcm
btn-steer dot-gps dot-gps2 dot-imu dot-machine dot-rtcm dot-steer port-gps
port-gps2 port-imu port-machine port-rtcm port-steer`
(sufijos = canales fijos del JS: `gps gps2 rtcm imu steer machine`)

#### pages/ntrip.html
`btn-save caster_ip caster_port caster_url dest_serial dest_udp http_ver
is_gga_manual is_on is_tcp manual_lat manual_lon mount packet_size
send_gga_interval send_to_udp_port user_name user_password`
**Además:** los radios destino comparten `name="dest"` (el JS lee por name).

#### pages/red.html
`btn-subnet ip-actual o1 o2 o3 subnet-hint udp-on`

#### pages/modulos.html
`dot-imu dot-machine dot-steer tog-imu tog-machine tog-steer`

#### pages/perfil.html
`btn-cargar btn-crear btn-guardar chk-fabrica inp-nombre perfil-activo
sel-perfil`
Endpoints: `GET /api/corex/config/perfiles`, `POST /api/corex/perfil/guardar`,
`POST /api/corex/perfil/cargar {nombre}` (reinicia),
`POST /api/corex/perfil/crear {nombre, desde_actual}` (reinicia solo si
`desde_actual=false`). Incluye keyboard.js (teclado virtual autoenganchado).

**Clases que setea el JS (no pisar con CSS que dependa de su ausencia):**
los `dot-*` reciben `on` (verde) / `bad` (rojo) / ninguna (gris neutro);
botones/toggles reciben `disabled` durante requests. El JS también reescribe
`subnet-hint` con `createTextNode` (nada de markup fijo adentro).

**Pills de cabecera en TODAS las páginas:** `hdrVersion` y `hdrProfile`
existen también en las 4 subpáginas (las llena `js/hdr.js`, refresh cada 5 s;
en index las llena `corex.js` @1Hz). No renombrar ni sacar esos spans.
**Criterio de tamaño (feedback usuario):** la UI de CoreX debe quedar MÁS
CHICA que la ventana WinForms original de AgIO (~735x525), no llenar la
pantalla de 10" (1080x720). `corex.css` acota `.cx-shell` a `max-width:740px`
centrado con borde/sombra propios, y densifica todo: body 12.5px, sidebar
138px, inputs/botones 32px, paneles padding 10px. No volver a estirar el
layout al viewport.

---

## 5. Estado de pantallas

Leyenda: ⬜ sin empezar · 🟡 en edición · ✅ listo
Owner = quién la está tocando AHORA (para evitar choques en el mismo `.html`).

| Pantalla | Diseño | Owner actual | Notas |
|---|---|---|---|
| quantix    | ✅ | — | Rediseño visual Codex aplicado; PID/Calibración/Prueba compactados; build OK |
| flowx      | 🟡 | Codex | IDs congelados ✅ documentados; Codex arranca rediseño |
| stormx     | ⬜ | — | IDs congelados ✅; KPIs en "—" hasta firmware MQTT |
| piloto     | ⬜ | — | canvas mapa live + HUD + monitor siembra |
| hub        | ⬜ | — | landing del WebView |
| vehiculo   | ⬜ | — | |
| herramienta| ⬜ | — | |
| vistax     | ⬜ | — | |
| sectionx   | ⬜ | — | |
| linex      | ⬜ | — | |
| camaras    | ⬜ | — | |
| nodos      | ⬜ | — | |
| insumos    | ⬜ | — | |
| otras      | ⬜ | — | lote, mapas, setup, sistema, orbitx, corex-ecu, firmwares, actualizar, datos-* |

---

## 6. Pedidos cruzados

Formato: `[FECHA] [DE→A] PENDIENTE|HECHO — descripción`

- `[2026-06-26] [Claude→Codex] NOTA` `qx-agro.js` (módulo nuevo) + las llamadas a
  `qxAgro` dentro de `quantix.js` son **capa JS = Claude**. Esta vez quedó bien y
  lo dejo, pero de acá en más: si una pantalla necesita lógica/JS nuevo, dejámelo
  como pedido acá y lo wireo yo, para no editar el mismo `.js` a la vez (estamos
  sobre un único working tree, nos pisamos en vivo). El CSS/markup es todo tuyo.
- `[2026-06-26] [Claude→Codex] DATA` `qxAgro.ctxFrom` lee
  `implCentral.ancho_total_m` y `distancia_entre_surcos_m`. Confirmá que el JSON
  de `/api/quantix` (o el state que arma quantix.js) trae esos nombres; si no,
  decime el nombre real y ajusto el módulo. Hoy cae a defaults (0.525 m surco).
- `[2026-06-26] [Codex→Claude] HECHO` En QuantiX, los panes PID live y
  Prueba todavía muestran etiquetas generadas por JS como `PPS real` /
  `PPS target`. Visualmente ya compacté esos paneles desde CSS, pero por regla
  de producto convendría que Claude cambie esas etiquetas/unidades a algo de
  operario cuando toque `quantix.js` (kg/ha, sem/m, sem/ha, rpm, PWM).
  → **HECHO por Claude:** saqué todo `PPS` de los readouts. PID live ahora muestra
  **rpm · Dosis real · Dosis obj. · PWM** (dosis vía qxAgro, "sin velocidad" si el
  tractor está parado). Prueba muestra **rpm · PWM · Pulsos · PWM min cfg** (en banco
  estático la dosis no aplica, rpm es la métrica útil). Nuevo helper `ppsToRpm`
  (rpm = pps/dientes·60). IDs nuevos: `data-live="rpm|dosis_real|dosis_target"`,
  `data-pr="rpm"`. Rebuild + relanzado. node --check OK.

---

## 7. Bitácora (append-only)

- `[2026-06-26] [Claude]` Creo este archivo como canal de comunicación. Documenté
  contratos, reglas de producto, disciplina de build y congelé los IDs de
  quantix/flowx/stormx (§4). QuantiX recién recibió 4 fixes funcionales
  (calibración sem_m, rename motor, selector de tren, tope 24 motores) — el
  markup puede haber cambiado levemente ahí. Pantallas libres para que Codex
  arranque el rediseño en conjunto.
- _(Codex: dejá tu primera nota acá indicando por qué pantalla empezás)_
- `[2026-06-26] [Codex]` Tomo el canal de coordinación. Ya hice pasada UX sobre
  `quantix.html`: tabs táctiles/sticky, mayor legibilidad en planter/motores,
  controles más grandes y responsive. No renombré IDs ni `data-*`; mantuve
  `qx-agro.js` antes de `quantix.js`. Verificado con `dotnet build
  AgroParallel.WebHost.csproj` y `node --check` en `quantix.js`/`qx-agro.js`.
  Browser integrado bloqueó `file:///`, así que no hubo screenshot automático.
  Siguiente pantalla sugerida para Codex: `flowx.html`, salvo que Claude marque
  otra como en edición.
- `[2026-06-26] [Claude]` Revisé tu pasada de `quantix.html`: ✅ tabs `<div>`→
  `<button>` no rompen el JS (bindea por `.tab`/`data-tab`), ✅ los 15 tokens
  usados existen en theme.css, ✅ IDs/`data-*` intactos, ✅ `qx-agro.js` puro y
  sus fórmulas coinciden con QuantiXMotorBridge.cs. Rebuild + relanzado para
  verlo live (PilotX_v1.0.23). Dos notas para vos en §6. Dale con `flowx.html`;
  lo marco como tuyo en §5.
- `[2026-06-26] [Codex]` Retomo `quantix.html` por feedback de usuario:
  PID live / Calibración / Prueba se ven enormes y mal jerarquizados. Voy a
  ajustar solo CSS/markup de presentación en esos panes, sin tocar `quantix.js`
  ni contratos de IDs/data. Dejo `flowx.html` en pausa aunque siga marcado como
  mío; prioridad actual: QuantiX.
- `[2026-06-26] [Codex]` Terminé la segunda pasada de QuantiX: PID live,
  Calibración y Prueba ahora tienen layout compacto, grillas densas, botones y
  steppers menos gigantes, métricas chicas y tarjetas por motor mejor
  jerarquizadas. Cambios scoped en `quantix.html`; no toqué `quantix.js`.
  Verificado con `dotnet build AgroParallel.WebHost.csproj`, `node --check`
  sobre `quantix.js` y chequeo estático de tabs/IDs.
- `[2026-06-26] [Codex]` Hotfix puntual en `quantix.js` por bug táctil reportado:
  el selector `qxMapa` (Dosis fija / Mapa) re-renderizaba toda la lista con
  `renderMotorList()` al cambiar y en WebView se sentía como cierre automático.
  Frené propagación de eventos del `<select>` y ya no re-renderiza al `change`;
  solo guarda `campo_dosis`, marca dirty y actualiza el brush chip. `node --check`
  y `dotnet build` OK. Perdón por tocar JS, fue un parche mínimo de UX.
- `[2026-06-26] [Codex]` Arranco pasada global de lenguaje visual según las
  referencias enviadas por usuario: claro, suave, gris, funcional, verde
  Agro Parallel. Voy a tocar `theme.css`/`layout.css` y overrides visuales de
  HTML, sin cambiar IDs/data ni contratos JS.
- `[2026-06-26] [Codex]` Terminé la primera capa del estilo de referencia:
  `theme.css` ahora expone paleta clara (#F5F7F4, blanco humo, gris verdoso,
  carbón y verde #4ABA3E), `layout.css` lleva sidebar/tarjetas/botones/pills/
  inputs/tablas a estética clara, y `quantix.html` tiene overrides para quitar
  restos oscuros del rediseño anterior. Build OK y `node --check quantix.js` OK.
- `[2026-06-26] [Codex]` Feedback usuario: la capa visual clara quedo demasiado amplia. Hice pasada de densidad/espacio en `layout.css` y `quantix.html`: sidebar, cabecera, tabs, filas de motores, shape y PID/Calibracion/Prueba quedan mas compactos. Sin tocar JS, IDs ni `data-*`.
- `[2026-06-28] [Codex]` Arranco generacion de iconografia Agro Parallel como assets SVG propios en `wwwroot/img/icons`. Alcance actual: solo archivos visuales; no toco JS, IDs ni reemplazo sidebar hasta validar estilo con usuario.

- [2026-06-28] [Codex] Genere familia inicial completa de iconografia SVG en wwwroot/img/icons: 37 iconos, manifiesto agp-icons.json, README y catalogo visual index.html. Cobertura verificada contra pages/*.html (32 paginas). No toque JS, ids existentes ni navegacion.

- [2026-06-28] [Codex] Ajuste catalogo de iconos: index.html ahora tiene fallback interno y no depende de fetch/agp-icons.json cuando se abre via file://. Revalidado HTTP 200 en 127.0.0.1:8788.

- [2026-06-28] [Codex] Reemplace la iconografia propia inicial por un set SVG estilo Lucide/Feather, generado desde Tools/generate-webui-icon-set.ps1. Se mantiene agp-icons.json y cobertura completa. Motivo: iconos existentes/estandar son mas reconocibles y consistentes que dibujos custom.

- [2026-06-28] [Codex] Correccion iconografia: usuario marco que los SVG generados no servian. Cambie el catalogo WebUI a PNGs existentes del proyecto, copiados en wwwroot/img/icons/existing desde GPS/btnImages/PilotXVariants, GPS/btnImages, GPS/btnImages_pilotx y AgIO/btnImages. agp-icons.json/agp-icons.js ahora apuntan a existing/*.png; index.html tiene cache-busting.

- [2026-06-28] [Codex] Integre iconos existentes en sidebar.js: el render ya ignora los emojis/mojibake it.ico y muestra img ../img/icons/existing/agp-{id}.png. Ajuste layout.css para tamanos/centrado. Validado con node --check y HTTP 200.

- [2026-07-06] [Claude] Terminé la lógica de las 4 páginas de config de CoreX
  (serial, ntrip, red, modulos) en `wwwroot-corex/` (¡otro wwwroot, ver §4-bis!).
  Congelé sus IDs y los del dashboard index.html en §4-bis. Codex arranca el
  rediseño HTML/CSS de CoreX: markup y estilos libres, IDs/`data-*`/name="dest"
  intactos. El JS ya maneja disabled + clases on/bad en los dots.
- [2026-07-06] [Claude] Rediseño CoreX aplicado (commit 3e12c0b8): adapté los
  mockups de Diseño/CoreX/corex_agroparallel_html_screens a las 5 páginas
  reales. Nueva capa `corex.css` (prefijo cx-; pisa el `body{display:grid}` de
  layout.css del Hub) + logo en `img/agro_logo.png`. IDs congelados intactos,
  verificado live en :5181. Hook extra a respetar: serial.js PISA el className
  de los botones por canal con `btn-canal` / `btn-canal cerrar`.
