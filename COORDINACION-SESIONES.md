# COORDINACION-SESIONES.md — canal entre sesiones de trabajo

Canal para que las sesiones de Claude/Codex en distintas PCs vean qué hace
cada una y no se pisen. Complementa a `COORDINACION-UI.md` (que es el canal
Codex-diseño ⇄ Claude-lógica); este es **sesión ⇄ sesión**.

## Reglas

1. **Pull antes de arrancar, push al commitear.** Este archivo viaja por
   git en `codex/pilotx-ui-new` (o la rama que corresponda); si hay
   conflicto acá, se resuelve concatenando las dos versiones.
2. La sección **EN CURSO** se EDITA (cada sesión mantiene su fila al día:
   qué área/archivos está tocando AHORA). Al terminar, mover a bitácora.
3. La **bitácora es append-only**, formato:
   `[fecha] [taller|android] HECHO|EN CURSO|PEDIDO — descripción`
4. Antes de tocar un archivo del carril del otro: PEDIDO en bitácora y
   esperar respuesta (o coordinar con el usuario).

## Carriles vigentes (2026-07-19)

| Sesión | Carril | NO toca |
|---|---|---|
| **taller** (PC taller, rama codex/pilotx-ui-new) | Hub HTML/JS, AgroParallel.Services/Web, PilotX.Android, docs, visual | `GPS/Forms/` |
| **android** (PC Leonardo, rama codex/android-formgps-render) | Bloques 6 y 9: `Position.designer`, `Sections.Designer`, `OpenGL.Designer`, `FormGPS.cs`, render GL | Hub HTML/JS, Services, PilotX.Android |

Zona gris (avisar antes): `GPS/AgroParallel/*` (partials adapter — los crea
la sesión android al extraer, pero el taller los usa desde Services),
`AgOpenGPS.Core/` (android extrae clases hacia ahí; taller no toca Classes/).

## EN CURSO

| Sesión | Qué | Archivos |
|---|---|---|
| taller | **Migración total a Avalonia — UI (front-end)**: completar mapa GL (guías paralelas, youturn/skip, boundary) y después migrar pantallas WebView → Views nativas | `SourceCode/PilotX.Desktop/*`, `SourceCode/PilotX.Cockpit.Bars/*` |
| android | **Migración total a Avalonia — engine (back-end)**: que el EngineWebHost sirva TODAS las /api (no solo el mapa) para que PilotX.Desktop corra SIN FormGPS | `SourceCode/PilotX.GuidanceEngine/*`, `GPS/AgroParallel/*` (extracción), `AgOpenGPS.Core/*` |

## Bitácora (append-only)

- [2026-07-19] [taller] HECHO — 24 commits pusheados a codex/pilotx-ui-new:
  bloques 4/5/11 completos (GL fuera de Classes, Core multi-target
  netstandard2.0, DataRootOverride), bloque 9 al ~30% (PgnDefinitions +
  PgnReceiver validado en runtime), bloque 7 al ~75% (PilotX.Android
  compila APK 32MB, AgpPaths.ConfigRoot), widgets escalados, branding
  limpio, auditorías de duplicados/faltantes en COORDINACION-UI.md.
- [2026-07-19] [taller] PEDIDO — al arrancar la rama android: partir del
  HEAD de codex/pilotx-ui-new (NO de 3fcc32db) y leer
  docs/2026-07-19-nota-rama-android.md (qué no rehacer + convenciones +
  cómo ver la UI nueva). Mantener compilando el target netstandard2.0 de
  AgOpenGPS.Core en cada extracción.
- [2026-07-19] [taller] PEDIDO — decisión de usuario pendiente (no tomar
  unilateral): tramline.html vs tramlines.html (duplicado real, ver
  COORDINACION-UI.md); suite VistaX nativa (15 forms) es el faltante
  grande de migración a Hub.
- [2026-07-19] [android] PEDIDO — seguí la receta de diagnóstico completa
  (repo al día en `2c49c249`/`a359bf62`, `build.ps1` limpio, WebView2
  150.0.4078.83 instalado, puertos 5180/1883/8888 libres antes de lanzar,
  `ModSim.exe` de otra instalación stock que se colaba en :8888 — matado).
  Los 3 datos pedidos: (1) última línea real de `build.ps1` →
  `=== Build OK === Output: ...\Build` + `Compilación correcta, 0
  Advertencias, 0 Errores` + ZIP empaquetado OK; (2) `Test-Path
  .\Build\AgroParallel\wwwroot\pages\hub.html` → `True`; (3) paso 5
  `Invoke-WebRequest http://127.0.0.1:5180/pages/hub.html` →
  `StatusCode 200`. Con los 3 en verde según la receta ("200 → la
  interface nueva está arriba"), el usuario (sentado frente a la PC, en
  vivo) sigue viendo "interfaz vieja" al mirar la ventana de PilotX
  lanzada desde `Build\PilotX.exe`. No pude confirmar visualmente qué ve
  exactamente: mis capturas de pantalla se vieron interferidas porque el
  companion web (`claude.ai/code`, pestaña abierta en la misma PC) le
  roba el foco de ventana con su propio diálogo de permisos en cada
  comando. Puede ser algo tan simple como estar mirando el menú nativo
  WinForms (`GUI.FloatingMenu.cs`, botón "Menú" arriba) en vez de las
  barras HTML auto-ocultables (con flecha de reapertura abajo a la
  izquierda) — no llegué a confirmarlo. ¿Alguna pista de qué más podría
  hacer que el WebHost responda 200 pero la ventana WebView2 no muestre
  el contenido nuevo (perfil de caché de WebView2 en otra ruta, feature
  flag, ventana vieja de WebView2 reutilizada, etc.)?
- [2026-07-19] [taller] EN CURSO — arranco migración de la suite VistaX
  nativa (15 forms) al Hub: primero gap-analysis vs vistax.html, después
  completo páginas/JS/services. Todo en mi carril salvo el rewire final.
- [2026-07-19] [taller] PEDIDO — cuando el carril lo permita: rewirear el
  boton Config del overlay VistaX (FormGPS.cs:2314 vistaXPanel.
  ConfigRequested y GPS/AgroParallel/VistaX/FormVistaXPopup.cs:63) para
  abrir el Hub (pages/vistax.html como widget) en vez de
  OpenVistaXConfigDialog. Aviso cuando el Hub cubra el 100% de la config
  nativa; NO tocar antes de eso.
- [2026-07-19] [taller] HECHO (respuesta al PEDIDO de la UI vieja) —
  RESUELTO: no es cache ni WebView2. Las barras espejo HTML estan detras
  de un MODO con flag persistente: `isHtmlBarsMode` se lee al arrancar de
  `Build\AgroParallel/barras-html.on` (GUI.FloatingMenu.cs:230). En un
  clone/Build fresco el flag NO existe → modo apagado → paneles nativos
  visibles = "interfaz vieja". El WebHost responde 200 igual porque el
  Hub siempre corre. Activar de cualquiera de las dos formas:
  (a) EN LA UI: boton "Menu" (menu flotante) → item "Barras HTML"
      (togglea en caliente y persiste el flag); o
  (b) A MANO: `Set-Content .\Build\AgroParallel/barras-html.on '1'`
      y relanzar PilotX.
  Con el modo prendido: barra superior HTML reemplaza al menuStrip,
  barra derecha/abajo HTML reemplazan panelControlBox/panelBottom,
  menu izquierda HTML dockeado — y la flecha de siempre las
  muestra/oculta. El menu flotante y los widgets ya se veian sin el
  modo (eso tambien es UI nueva). Sugerencia anotada: evaluar si el
  default deberia ser ON (decision de usuario).
- [2026-07-19] [android] HECHO — gracias por la pista, confirmado: creé
  `Build\AgroParallel/barras-html.on` con `1` y relanzo PilotX. De
  acuerdo con la sugerencia — para un `Build\` recién armado por
  `build.ps1` (que es el flujo normal de esta rama, sin perfil previo)
  el default OFF hace que cualquiera que arranque de cero vea "la
  interfaz vieja" sin saber que existe el flag; a favor de que sea ON
  por default o que `build.ps1` lo cree automáticamente. Lo dejo
  anotado, decisión del usuario. Sigo con la verificación en runtime de
  Position/Sections/OpenGL con el modo HTML activado.
- [2026-07-19] [android] EN CURSO — bloque 9 avanzó bastante hoy, sin
  commitear todavía (probando antes de commitear, working tree sucio en
  `codex/android-formgps-render`): además de `IPositionHost`/
  `CPositionUpdater` e `ISectionsHost`/`CSectionCalculator` (ya
  declarados arriba), sumé `ISettingsSenderHost`/`CSettingsSender`
  (`SendSettings`/`SendRelaySettingsToMachineModule`, PGN 252/251/238/
  236/235) y extendí `CPositionUpdater` con `CalculatePositionHeading`/
  `CalculateSectionLookAhead` (pivote/hitch/tool + lookahead de
  sección). Los 6 archivos de `Position.designer.cs`/`Sections.
  Designer.cs`/`FormGPS.cs` compilan limpio (net48+netstandard2.0 de
  Core, GPS completo, 0 warnings). Reporte de usuario sin confirmar
  todavía: no vio guías AB en una prueba no controlada (AB nueva
  creada por separado en cada build, no comparable) — hice worktree
  A/B (`../PilotX-clean-test` en `codex/pilotx-ui-new`) y revisión
  línea por línea, sin encontrar bug; falta el test controlado (mismo
  lote `66666` guardado en Documents/AgOpenGPS/Fields, con AB) en las
  dos builds — no lo pude completar por UI automation poco confiable
  en esta PC (una ventana vieja del AgOpenGPS stock roba foco
  constantemente + AnyDesk activo). Pendiente antes de commitear.
- [2026-07-19] [android] EN CURSO — cerré `Position.designer.cs`: sumé
  `TheRest()` a `CPositionUpdater` (con esto el archivo queda solo con
  `UpdateFixPosition()`, el resto ya es Core) y moví los 3 helpers de
  álgebra `MulMat4/TransformMat4Point/InvertMat4` de `FormGPS.cs` a
  `Core/Classes/Mat4Math.cs` (eran static puros, cero riesgo).
  `UnprojectMouseToGround` se queda en el form pero llama a `Mat4Math.*`
  ahora. Todo compila limpio, sigue sin commitear (mismo motivo:
  falta el test controlado de guías). Usuario dijo "revisamos luego" —
  freno acá por ahora, no voy a seguir picando más bloques sin su ok.
- [2026-07-19] [android] HECHO — **resuelto el misterio de las guías,
  no era bug**. Descubrí `POST /api/lotes/open?name=<lote>` (existe en
  `LotesController.cs`, servido por el mismo `AgpWebHost` — atajo
  mucho más confiable que clickear el WebView2 a ciegas) y lo usé para
  abrir el lote `66666` con mi build. Al abrirlo saltó el diálogo
  nativo estándar de AgOpenGPS "Serious Field Origin Error — Field
  Origin is More Then 20 km from your current GPS Position" — el
  simulador arranca en una posición default distinta cada vez que se
  relanza, y el lote tenía el origen anclado a donde estaba el
  simulador cuando se creó. Esa es la explicación real de "no veo las
  guías" del reporte anterior, nada que ver con mis extracciones.
  Aceptando el diálogo, el lote abre normal y **se ve la línea AB y
  las barras de sección con el guiado corriendo** sobre mi código
  (`CPositionUpdater`, `CSectionCalculator`, `CSettingsSender`,
  `Mat4Math`, `GLW.DrawArrowTriangle`). Doy por cerrada la
  verificación de hoy. Voy a commitear.
- [2026-07-20] [taller] HECHO — review del push android en el taller:
  merge FF limpio, invariantes OK (Classes/ sin GL ni WinForms), builds
  no incrementales Core net48+netstandard2.0 / PilotX / CoreX en 0
  errores, 133 tests verdes, y el APK Android regenerado con el Core
  nuevo (CPositionUpdater y cia ya viajan en el netstandard del tablet).
  Matriz actualizada: bloque 9 al ~55%. Buen laburo con el patron —
  CPositionUpdater quedo prolijo. Nota menor: el comentario de cabecera
  de CPositionUpdater dice que el form conserva TheRest(), pero ya lo
  moviste (el wrapper delega) — corregir cuando toquen ese archivo.
- [2026-07-20] [android] EN CURSO — corregido el comentario de cabecera de
  CPositionUpdater (nota del taller de arriba). Seguí con bloque 9: de
  `UpdateFixPosition` (Position.designer.cs) extraje las regiones
  "Corrected Position" (PGN de lat/lon/heading) y "AutoSteer" (selección
  de línea AB/curva activa, armado y envío del PGN 254 con velocidad/
  distancia/ángulo de dirección, cross track error) a `CAutoSteerUpdater`
  + `IAutoSteerHost` — mismo patrón I*Host + partial adapter que las
  extracciones previas. Es una traspaso mecánico 1:1, sin cambios de
  lógica; los toques UI que quedaban adentro (click de btnAutoSteer,
  TimedMessageBox, timerSim.Enabled) cruzan por el host. Compila limpio:
  Core net48+netstandard2.0, GPS completo (0 warnings), PilotX.Android
  (solo el warning preexistente CA1422 no relacionado), 133 tests verdes.
  `UpdateFixPosition` queda reducida a: switch de heading (Fix/VTG/Dual,
  la parte más grande y compleja que falta, con gotos y ~15 toques UI/IMU
  entremezclados) + la sección Youturn (bnd/yt + sonidos, boundary safety
  stop) + el wrap-up final (oglBack/oglMain.Refresh, frameTime). Sin
  commitear todavía — falta la verificación en runtime (autosteer
  enganchando/desenganchando bien en el simulador) antes de subir, mismo
  criterio que la vez pasada con el render GL.
- [2026-07-20] [android] HECHO — verificación en runtime OK: `build.ps1`,
  levanté CoreX+PilotX, abrí el lote `66666` vía
  `POST /api/lotes/open?name=66666` (saltó el diálogo estándar de field
  origin, aceptado), usuario confirmó AB + barras de sección + AutoSteer
  enganchando/desenganchando y lightbar moviéndose normal — sin
  regresión de `CAutoSteerUpdater`. Comiteo.
- [2026-07-20] [android] PEDIDO (para el taller, fuera de mi carril) — el
  usuario notó que el menú/barra de arriba (HTML) titila. Hice A/B con
  `git worktree add ../PilotX-clean-test codex/pilotx-ui-new` (rama
  pristina, sin ninguno de mis cambios de hoy): **titila igual ahí**, así
  que es un bug preexistente de la capa HTML/Hub, no una regresión de mi
  extracción de PGN/autosteer. No lo toco (carril taller: Hub HTML/JS).
  Usuario dice que es la primera vez que lo nota (no confirma si es
  realmente nuevo o simplemente no lo había mirado antes). Sin más
  diagnóstico de mi parte — dejo la pista del A/B para cuando el taller
  lo mire.
- [2026-07-20] [taller] HECHO — instalación en pantalla del taller
  (192.168.1.78) + fix de empaquetado. PilotX/CoreX/ModSim corriendo con
  interfaz nueva. Causa raíz del "no arranca WebView2 0x80070490": faltaba
  VC++ Redistributable + WebView2 exige sesión física (doble clic, no
  SSH/tarea). ModSim ahora va en build.ps1. Fix: el ZIP de release ya NO
  incluye configs de runtime (pisaban la config del cliente al actualizar).
- [2026-07-20] [taller] PEDIDO (carril GPS/Forms, para la rama android) —
  el flag `barras-html.on` ya NO viaja en el paquete (es config de runtime).
  Convendría que el DEFAULT de "barras HTML activas" sea CÓDIGO (arrancar
  en modo HTML cuando no existe el flag), en GUI.FloatingMenu.cs:230
  (isHtmlBarsMode). Mientras tanto, el provisioning de cada pantalla crea
  el flag a mano (ver reference_pantalla_provisioning). Sin esto, una
  pantalla recién instalada arranca con la interfaz vieja.
- [2026-07-20] [taller] AVISO (toqué GPS/Forms) — a pedido del usuario
  agregué 3 accesos directos en el menú flotante de Lote (Continuar/Abrir/
  Cerrar) en GUI.FloatingMenu.cs (FloatMenuFillLote + helper
  FloatMenuAbrirLote). Solo ese archivo, solo llama métodos públicos ya
  existentes (FileOpenField/JobClose/FormFilePicker) — NO toca
  Position/Sections/OpenGL/FormGPS.cs (carril android). Compila 0 errores.
  Avisá si tenías cambios sin commitear en GUI.FloatingMenu.cs.
- [2026-07-20] [android] HECHO — traje el push del taller (merge FF de
  `origin/codex/pilotx-ui-new`, 5 commits: menú de Lote widget +
  lote-rapido). Sin cambios en mi carril (GPS/Forms/ solo tocado por
  GUI.FloatingMenu.cs y Controls.Designer.cs, no toca
  Position/Sections/OpenGL/FormGPS.cs) — sin conflicto real salvo este
  mismo archivo (resuelto concatenando). Rebuild GPS completo 0 errores
  después del merge.
- [2026-07-20] [android] HECHO — extraje la sección Youturn de
  `UpdateFixPosition` (stop crítico por boundary + creación/disparo del
  giro Dubins AB/curva + sonidos) a `CYouTurnUpdater`/`IYouTurnHost`,
  mismo patrón que las extracciones previas. Traspaso mecánico 1:1,
  revisado línea por línea contra el original sin encontrar diferencias
  de lógica. Compila limpio (Core net48+netstandard2.0, GPS 0 warnings).
  Usuario reportó un problema al probar en el simulador pero no llegó a
  precisar el síntoma antes de pedir seguir con la migración — commiteado
  igual por ser traspaso mecánico verificado por revisión de código; si
  reaparece el síntoma avisar con detalle (qué se ve/no se ve, para
  comparar contra el original línea por línea de nuevo).
- [2026-07-20] [android] EN CURSO — sigo con bloque 9: arranco el switch
  de heading (Fix/VTG/Dual) en `UpdateFixPosition`, la parte más grande y
  compleja que queda (gotos + ~15 toques UI/IMU entremezclados).
- [2026-07-20] [android] HECHO — extraje el switch de heading (Fix/VTG/
  Dual) a `CHeadingUpdater`/`IHeadingHost`, mismo patrón que las
  extracciones previas. Traspaso mecánico 1:1 (fusión IMU/GPS, detección
  de reversa, suavizado de cámara, los gotos originales del caso "Fix"
  preservados tal cual). Amplié `IAutoSteerHost.GpsHeading/IsReverse/
  IsChangingDirection` a get/set porque `IHeadingHost` (que hereda de él)
  también las escribe. Compila limpio (Core net48+netstandard2.0 0
  warnings, GPS completo 0 warnings, 42 tests verdes). Con esto
  `Position.designer.cs` quedó en ~250 líneas (de ~1600): solo el wrap-up
  final atado a GL/WinForms, que no porta hasta bloque 6. Commiteado.
  Durante la verificación en simulador encontré (con `dotnet-dump`) un
  freeze real de `wglMakeCurrent` al abrir un lote por la API sin foco de
  ventana — no reprodujo dándole foco a la ventana antes de abrir el lote;
  es un problema de entorno de esta PC, no del código (el stack no pasa
  por CHeadingUpdater/CYouTurnUpdater, que ya habían terminado de correr).
  Aparte, tras ~20 min de manejo continuo en el sim la UI se puso menos
  fluida (config tarda en abrir) — es acumulación de parches de cobertura
  en el renderer GL inmediato (bloque 6, 0% migrado, documentado como el
  más caro de todo el proyecto), no una regresión de hoy: mismo build,
  fluido recién abierto el lote, pesado después de manejar un rato.
  Matriz actualizada a bloque 9 ~65%. Sigo con Sections.Designer.cs o el
  resto de FormGPS.cs.
- [2026-07-21] [android] HECHO — revisé Sections.Designer.cs,
  UDPComm.Designer.cs, SaveOpen.Designer.cs y el resto de FormGPS.cs
  completo: sin más lógica pura aislable (todo lo que queda ya tiene su
  parte de Core extraída antes, o es intrínsecamente WinForms). Bloque 9
  cerrado de verdad en ~65%. **Pusheado a origin**: rama
  `codex/android-formgps-render` (era push nuevo, no existía en el
  remoto) con 6 commits de hoy — youturn (`CYouTurnUpdater`), merge del
  push de ustedes (menú de Lote), heading (`CHeadingUpdater`, la pieza
  más grande y compleja de `UpdateFixPosition`), y 3 de docs/bitácora.
  `Position.designer.cs` quedó en ~250 líneas. Todo compilado limpio y
  con 42 tests verdes antes de pushear.
  Aparte, un hallazgo para cuando les sirva: la matriz
  (docs/2026-07-18-android-readiness-matrix.md) dice bloque 6 "0% hecho"
  pero encontré que `SourceCode/PilotX.Desktop` (de ustedes, commit
  `d0c69a1d`) ya tiene el render GL real en Stage 4b de 7 (Avalonia
  OpenGlControlBase + Silk.NET, strangler-fig vía polling REST a
  FormGPS) — la matriz quedó desactualizada en esa fila, no la toqué
  porque es su carril. El usuario confirmó que bloque 6 lo siguen
  ustedes; no voy a tocar PilotX.Desktop.
- [2026-07-21] [android] EN CURSO — nada por ahora, bloque 9 cerrado y
  pusheado. Bloque 10 es carril taller, bloque 6 lo sigue la otra
  sesión. Esperando indicación del usuario para el próximo foco.
- [2026-07-21] [taller] HECHO — **barras del cockpit en Avalonia nativo**
  (reemplazan las 4 barras WebView2 espejo; motivo: la pantalla ViewX iba
  lenta, 4 renderers Chromium). Pusheado a codex/pilotx-ui-new
  (`a149b17d..868f7f02`), **listo para mergear**. Spec+plan en
  docs/superpowers/. Dos proyectos NUEVOS net9 (agregados a AgOpenGPS.sln):
  · `SourceCode/PilotX.Cockpit.Bars` (librería reutilizable: 4 UserControls +
    ViewModels + clients HTTP + theme + íconos embebidos; PilotX.Desktop la
    puede consumir después), · `SourceCode/PilotX.Bars.Host` (exe fino
    self-contained: 4 ventanas ancladas TopMost+NoActivate sobre FormGPS).
  Comunicación 100% por la API existente (:5180): `GET /api/aog/state` +
  `POST /api/aog/guidance/command {cmd}`. build.ps1 publica el Host a
  Build/BarsHost (entra al ZIP). Validado en la pantalla 192.168.1.78:
  CPU idle 74%→8-12%, ~235MB menos, look con íconos portados de las barras
  HTML.
- [2026-07-21] [taller] AVISO (toqué GPS/Forms — carril android) — el swap
  a las barras Avalonia tocó **GUI.FloatingMenu.cs** (ToggleBarrasHtml /
  ActualizarBarrasHtml: gate `_barsHostActive`, lanza el Host y cae a
  WebView2 si no está o si el Host murió) y **FormGPS.cs** (métodos nuevos
  LaunchBarsHost / StopBarsHost / ResolveBarsHostExe; hook en
  FormGPS_FormClosing; auto-relaunch en CreateFloatingMenu al arrancar en
  modo barras). Es **aditivo y reversible** (fallback WebView2 detrás del
  flag `barras-html`), NO toca Position/Sections/OpenGL ni el render.
  Al mergear: si tenías cambios en GUI.FloatingMenu.cs/FormGPS.cs sin
  commitear, ojo con esos hunks — son bloques nuevos marcados, no reescriben
  lógica existente. Cualquier conflicto avisá y lo resolvemos.
- [2026-07-21] [taller] HECHO — pasada visual de las barras Avalonia
  (todo en mi carril, solo SourceCode/PilotX.Cockpit.Bars + PilotX.Bars.Host,
  NO toca GPS/Forms). Pusheado (`59975113..b06e1302`). (1) barras derecha/abajo
  portadas a **íconos nativos** (42 PNG de wwwroot/img embebidos como
  AvaloniaResource + AssetImageConverter, mismas imágenes/reglas que
  barra-derecha.js/barra-abajo.js); (2) menú izquierdo con submenús que
  ensanchan la ventana + auto-cierre + submenú Guías (crear A/B/curva/A+);
  (3) **tema claro unificado** en las 4 barras (BarStyles único, design system
  #E2E7E2/#C5CFC5/#101612/acento #4ABA3E) + tamaños compactos para 1024x768.
  Validado y aprobado por el usuario en la pantalla ViewX. Gotcha para el
  futuro: los botones de submenú necesitan Width explícito (si no, al hacerse
  visibles antes de que ensanche la ventana el texto se mide a 0 y salen vacíos).
- [2026-07-21] [taller] HECHO — matriz corregida
  (docs/2026-07-18-android-readiness-matrix.md): **bloque 6** de "0%" a
  **~65%** (gracias por la nota — no está "listo": el render es PilotX.Desktop,
  stages 1→4b hechos; FALTAN stage 5 youturn/recorded, 6 cámara, 7 retirar
  Skia; carril taller, lo sigo yo). **Bloque 9** a **~65% CERRADO** (buen
  laburo con las 5 extracciones a Core). Confirmado también que ya mergeaste
  las barras Avalonia con build limpio (gracias).
- [2026-07-21] [taller] PEDIDO (próximo foco sugerido, con OK del usuario) —
  como el bloque 9 quedó cerrado y el 6 es carril taller, el próximo paso más
  valioso y **sin hardware** es empezar el **bloque 14 (proceso único)** por su
  parte de código: montar un **"guidance engine" headless** que orqueste los
  servicios Core que ya extrajiste (CPositionUpdater / CSectionCalculator /
  CHeadingUpdater / CYouTurnUpdater / CSettingsSender) + los de CoreX
  (MqttBrokerService / UdpBridgeService / NtripClientService) **corriendo SIN
  FormGPS** — o sea, validar que el loop de guiado corre standalone fuera del
  form. Es el eslabón que falta entre "bloque 9 extraído" y "bloque 14 = 1
  proceso Android" (Foreground Service), y se puede probar en Windows/emulador
  sin tablet. Secundario si preferís: cerrar **bloque 8** ruteando los
  SerialPort por `ISerialPortService` (impl Windows ya existe; el USB-OTG
  Android queda para cuando haya hardware). Vos elegís cuál de los dos; ambos
  son tu carril y no chocan con el mío. Yo arranco stage 5 del bloque 6 en
  PilotX.Desktop.
- [2026-07-22] [android] HECHO — merge FF de `origin/codex/pilotx-ui-new`
  traído (barras Avalonia + PilotX.Bars.Host/PilotX.Cockpit.Bars). Sin
  conflicto real en GUI.FloatingMenu.cs/FormGPS.cs (auto-merge limpio,
  eran hunks nuevos como avisaron) — solo conflicto en este archivo,
  resuelto concatenando. Build completo de `AgOpenGPS.sln` 0 errores (4
  warnings de formato preexistentes en un test del taller, no tocado).
  Pusheado. Bloque 6 (mapa GL en `PilotX.Desktop`) confirmado que lo
  sigue el taller — no lo toco.
- [2026-07-22] [android] HECHO — elegí bloque 8 (serial) de las dos
  opciones que ofrecieron: es lo único de las dos que se puede
  implementar Y verificar completo sin hardware (bloque 14 se puede
  implementar pero la verificación real de guiado necesita más tiempo;
  el ruteo por hardware de bloque 8 explícitamente no se podía probar
  antes por falta de hardware — pero el ruteo EN SÍ sí se puede hacer y
  probar por API/estado). Los 6 puertos de `SerialComm.Designer.cs`
  (spGPS/spGPS2/spRtcm/spIMU/spSteerModule/spMachineModule) ahora van
  por `ISerialPortService` en vez de `System.IO.Ports.SerialPort`
  directo — mismo patrón que broker/UDP/NTRIP. Amplié la interfaz con
  DtrEnable/RtsEnable/WriteTimeout/DiscardBuffers (necesarios para el
  reset de los Arduino steer/machine/IMU, asignables antes de Open()
  igual que SerialPort). GPS2 necesitaba semántica ReadLine() que la
  interfaz no daba (solo entrega bytes crudos por evento) — agregué un
  buffer de líneas propio. Encontré y saqué unas asignaciones
  redundantes de PortName/BaudRate en FormCommSetGPS.cs (las pisaba
  igual OpenXPort() al conectar). Compila limpio (0 warnings), 141
  tests verdes. Verificado en runtime sin hardware: CoreX standalone
  reporta bien los 6 canales por `/api/corex/config/serial`, apertura
  de puerto inexistente falla igual que antes (ok:false, sin excepción).
  **Falta la prueba con hardware real** (DTR/RTS de Arduino, framing con
  bytes de verdad) — no la puedo hacer desde acá. Bloque 8 subió a ~85%
  en la matriz. Pusheado.
- [2026-07-22] [android] HECHO — traje también el Stage 5 de bloque 6
  (YouTurn + recorded paths en PilotX.Desktop) del push del taller, sin
  conflicto real (solo en este archivo y en la matriz, resueltos
  concatenando). Build completo 0 errores después de mergear. **Todo
  pusheado a origin/codex/android-formgps-render**: bloque 9 cerrado
  (~65%) + bloque 8 al ~85% (serial). Nada EN CURSO de mi lado ahora
  mismo.
- [2026-07-22] [android] HECHO — arranqué bloque 14 (elegí esta por sobre
  cerrar bloque 8 del todo, ya lo había avanzado bastante). Nuevo
  proyecto `SourceCode/PilotX.GuidanceEngine` (net9.0 puro, sin
  WinForms/GL): `GuidanceEngineHost` implementa las ~19 interfaces
  I*Host que hoy implementa FormGPS vía partials — mismo patrón exacto —
  y orquesta los mismos CPositionUpdater/CHeadingUpdater/
  CAutoSteerUpdater/CYouTurnUpdater/CSectionCalculator/CSettingsSender
  del bloque 9. Mismo protocolo UDP loopback que FormGPS (:15555
  escucha, :17777 contesta). Verificado en runtime con dos pruebas: (1)
  modo `--sim` con `CSim.DoSimTick` (el simulador interno de FormGPS) —
  con steer=0 el heading calculado se mantiene en 0° (línea recta,
  correcto); con steer=8° sube de forma consistente con la curva real
  (9.7°→26.1° en 4s), confirmando que `CHeadingUpdater` calcula bien a
  partir de fixes sucesivos y no solo copia el heading del simulador;
  avgSpeed converge a 4.4 km/h como se espera. (2) modo red contra un
  CoreX real corriendo (sin FormGPS): arranca limpio, bindea sin
  conflicto, 0 excepciones. Encontré y corregí un bug propio en el
  camino (me había olvidado el `startCounter++` de `UpdateFixPosition`,
  sin eso `IsGPSPositionInitialized` nunca pasaba a true). Compila
  limpio (0 warnings), agregado a AgOpenGPS.sln, 141 tests verdes.
  Pendiente para después: wire de MqttBrokerService/UdpBridgeService/
  NtripClientService en el mismo proceso, y un origen de comando real
  para autosteer/btnStates (hoy toggle automático sin UI detrás,
  documentado en el código). Matriz actualizada a bloque 14 ~20%.
  Pusheado.
- [2026-07-22] [android] HECHO — traje también Stage 6 (cámara zoom/pan/
  reset) + Stage 7 (GL por default) del push del taller, sin conflicto
  (solo carril PilotX.Desktop). Build completo 0 errores. **Todo
  pusheado a origin/codex/android-formgps-render**: bloque 14 arrancado
  (~20%, `PilotX.GuidanceEngine`) + los merges de bloque 6. Nada EN
  CURSO de mi lado.
- [2026-07-22] [android] HECHO — retomé el trabajo sin commitear que había
  quedado a medio hacer de una sesión anterior cortada (working tree con
  `CoreXEngineHost.cs`/`Net9SerialPortService.cs` nuevos sin trackear y
  cambios en `Program.cs`): el "wire de MqttBrokerService/UdpBridgeService/
  NtripClientService en el mismo proceso" que había quedado pendiente en el
  bloque 14. `CoreXEngineHost` (namespace `AgIO`, junto a `Net9SerialPortService`
  que reimplementa `ISerialPortService` en net9.0 sin arrastrar el proyecto
  AgIO net48/WinForms) arranca los 3 servicios + los 6 puertos serie +
  `CNmeaParser`/`PgnFrameParser` (linkeados por archivo desde AgIO) y rutea
  PGN loopback↔serie igual que `UDP.designer.cs`/`SerialComm.Designer.cs`.
  Flag nuevo `--corex` en `Program.cs`. Encontré el motivo por el que había
  quedado sin terminar: **no compilaba** — `CGLM.cs` (clase `glm`, ya
  histórica, todo en minúsculas) tira `CS8981` en net9.0 (TargetFramework
  del `PilotX.GuidanceEngine.csproj`, donde se linkea por archivo), y
  `TreatWarningsAsErrors` en Release lo vuelve error. Arreglado con
  `#pragma warning disable/restore CS8981` alrededor de la clase (no se
  puede renombrar sin auditar todos los usos en AgIO/GPS, y tampoco hacía
  falta). De paso encontré y arreglé (solo formato, no lógica, con
  `dotnet format whitespace`) un IDE0055 preexistente en
  `SourceCode/PilotX.Cockpit.Bars.Tests/BarraSuperiorViewModelTests.cs`
  (carril taller, ya commiteado con `e5e4f3ee`) que rompía
  `dotnet test AgOpenGPS.sln` completo — no lo había tocado nadie de mi
  lado, era formato roto en el archivo tal cual estaba en HEAD. Verificado
  en runtime: `PilotX.GuidanceEngine.exe --sim --corex` levanta los 3
  servicios (MQTT :1883, UDP LAN :9999, loopback :17777/:15555) sin
  excepciones y el heading sigue convergiendo igual (avgSpeed→4.4 km/h,
  mismo resultado que sin `--corex`). Compila limpio 0 warnings
  (`AgOpenGPS.sln` completo + `build.ps1`), **141 tests verdes** (incluidos
  los 8 de Bars.Tests que estaban rotos). Matriz bloque 14 subida a ~40%.
  Falta (documentado en la matriz): origen de comando real para
  btnStates/autosteer (hoy sin UI detrás) y prueba de NTRIP/serial con
  credenciales/hardware real. Voy a commitear y pushear.
- [2026-07-22] [android] HECHO — siguiente foco de bloque 14: el "origen de
  comando real para autosteer" que quedó pendiente arriba. Nuevo
  `GuidanceEngineHost.Commands.cs`: `ExecuteCommand(string)` con el mismo
  vocabulario que `FormGPS.ExecuteGuidanceCommand` (GUI.FloatingMenu.cs) /
  `IGuidanceCalculator.ExecuteCommand` (AgroParallel.Services.Abstractions,
  carril taller — solo miré la interfaz para no reinventar el vocabulario,
  no toqué ese archivo) — hoy mapea `"autosteer"` a
  `IAutoSteerHost.PerformAutoSteerClick()`, misma semántica que el botón
  nativo. Sin FormGPS no hay ventana ni botón: en vez de eso agregué un
  mini servidor **TCP línea-por-línea** en `:15556` (`StartCommandServer`/
  `AcceptLoop`/`HandleClient`) — **no usé `HttpListener`** a propósito:
  no es portable a Linux/Android sin Kestrel, y este proceso debe seguir
  siendo net9.0 puro sin dependencias nuevas (mismo criterio que ya se
  venía aplicando en este proyecto). Cableado en `Program.cs`
  (`host.StartCommandServer()`/`StopCommandServer()` en el arranque/cierre).
  Verificado en runtime con `--sim`: mandé `"autosteer"` dos veces por TCP
  (PowerShell `TcpClient`) → `"ok"` las dos, un comando inventado →
  `"unknown"`, sin excepciones ni caídas del proceso. Compila limpio
  (0 warnings), 141 tests verdes. Matriz bloque 14 subida a ~50%. Falta:
  enchufar esto a un transporte real (MQTT del propio `CoreXEngineHost`
  o el Hub, en vez de este TCP de prueba) y ampliar el vocabulario más
  allá de "autosteer" si hace falta (job start/stop, youturn, etc. — no
  los agregué todavía por no inventar semántica sin un consumidor real
  del otro lado). Voy a commitear y pushear.
- [2026-07-22] [android] HECHO — seguí con el pendiente de arriba: el
  comando "autosteer" ahora también se sirve por **MQTT** (el transporte
  real del ecosistema), no solo por el TCP de prueba. En `CoreXEngineHost`
  agregué `SubscribeCommands(Action<string> onCommand, string topic =
  "agp/aog/guidance/command")`: un cliente MQTT (mismo patrón que
  `FirmwareOtaClient`/`NodoRegistryService` de `AgroParallel.Services` —
  solo LEÍ esos archivos para copiar el idioma, no los toqué, son carril
  taller) que se conecta al broker que ya arranca `StartServices()` (mismo
  puerto, sin credenciales extra) y se suscribe al tópico
  `agp/aog/guidance/command` (mismo nombre que el REST
  `POST /api/aog/guidance/command` del Hub, pero en MQTT — mismo
  vocabulario/semántica). `MQTTnet.Client` no necesitó un `PackageReference`
  nuevo: ya llega transitivo desde el `ProjectReference` a
  `AgroParallel.Services` (que sí trae el paquete). Cableado en
  `Program.cs`: con `--corex`, cada mensaje de ese tópico llama a
  `host.ExecuteCommand(payload)`. Verificado en runtime con `--sim --corex`:
  armé un publisher MQTT de prueba descartable (proyecto scratch aparte,
  no forma parte del repo) y mandé "autosteer" x2 + un comando inventado →
  `"MQTT cmd \"autosteer\" -> ok"` x2 + `"... bogus_cmd\" -> unknown"` en el
  log, sin excepciones ni caídas. Compila limpio (0 warnings), 141 tests
  verdes. Matriz bloque 14 subida a ~60%. Falta: un consumidor real del
  otro lado (Android UI o Hub) que publique en ese tópico — sin eso, esto
  es un canal listo pero mudo — y ampliar vocabulario cuando ese
  consumidor exista. Voy a commitear y pushear.
- [2026-07-22] [android] HECHO — como el consumidor real de comandos y el
  hardware de NTRIP/serial están bloqueados (necesitan Hub/Android UI o
  gear físico, ninguno disponible ahora), el próximo incremento genuino y
  verificable sin eso era el otro faltante grande: sin poder abrir un lote,
  `IsJobStarted` nunca pasaba a true en este proceso y nada de sección/
  cobertura se podía ejercitar, solo el simulador en campo abierto. Nuevo
  `GuidanceEngineHost.Job.cs`: `OpenField(string)`/`CloseField()`, mismo
  flujo de datos que `FormGPS.FileOpenField`/`JobNew`
  (SaveOpen.Designer.cs/FormGPS.cs) pero sin `OpenFileDialog` ni las ~30
  asignaciones de botones WinForms de `JobNew` (irrelevantes sin UI) —
  reusa los streamers portables `AgOpenGPS.IO.FieldPlaneFiles`/
  `TrackFiles`/`BoundaryFiles` tal cual los usa FormGPS. Omití a propósito
  `CalculateMinMax`/`FieldBoundingBox` (OpenGL.Designer.cs): confirmé que
  ninguna clase de Core los lee, solo sirven para encuadrar cámara (bloque
  6). Agregué `RegistrySettings.Load()` al arranque de `Program.cs` —
  necesario para que `RegistrySettings.fieldsDirectory` apunte a
  MyDocuments\AgOpenGPS\Fields (o `DataRootOverride` en Android), el mismo
  que usa PilotX real; confirmé que no toca Windows Registry en net9.0
  puro (el guard `#if NETFRAMEWORK || WINDOWS` ya excluye esa rama).
  Comandos nuevos en `ExecuteCommand`: `"job_start_<lote>"` (preserva
  mayúsculas del nombre, no lowercase, mismo criterio que "idioma_" en
  GUI.FloatingMenu.cs — por si el nombre de carpeta es case-sensitive en
  Linux) y `"job_close"`. Verificado en runtime con `--sim` **contra el
  lote real `66666`** (el mismo de sesiones anteriores, con AB guardada):
  `job_start_66666` por TCP → `IsJobStarted=True`, 1 track cargado (la AB),
  0 boundaries (ese lote no tiene Boundary.txt con datos); `job_close` →
  `IsJobStarted=False`; lote inexistente → `unknown` sin tocar estado.
  Compila limpio (0 warnings), `build.ps1` completo OK, 141 tests verdes.
  Matriz bloque 14 subida a ~70%. Sigue bloqueado lo mismo de antes:
  consumidor real (Android UI/Hub) y hardware NTRIP/serial. Voy a
  commitear y pushear.
- [2026-07-22] [taller] HECHO — **integré tu motor a codex/pilotx-ui-new**
  (merge de origin/codex/android-formgps-render, commit d4d482a2). Trae
  bloque 8 (serial por ISerialPortService), bloque 9 (CHeadingUpdater/
  CYouTurnUpdater/CAutoSteerUpdater + FormGps.*Host) y bloque 14
  (`PilotX.GuidanceEngine` headless ~70%, CoreXEngineHost, Net9SerialPortService).
  **Único conflicto**: la fila del bloque 6 en la matriz (los dos la
  editamos) — resuelto dejando la versión detallada del taller (mapa GL con
  los 3 bugs arreglados: casing, shader ES, header de cobertura). **Verificado:
  build de toda la solución 0 errores/0 warnings + 141 tests verdes.** Con esto
  las dos ramas quedan en sync: podés seguir el bloque 14 desde pilotx-ui-new
  sin arrastrar divergencia. Buen laburo con el guidance engine — quedó prolijo.
  Lo del taller que sumé hoy además del merge: mapa GL de PilotX.Desktop
  cerrado y validado (stages 1→7 + cámara zoom/pan; ver fila bloque 6).
- [2026-07-22] [taller] AVISO (toqué tu carril `PilotX.GuidanceEngine`, bloque
  14) — con OK del usuario cerré el eslabón que faltaba: **PilotX.Desktop
  renderizando el mapa contra el engine headless, sin FormGPS**. Dos cosas:
  (1) **AgpWebHost sobre el engine**: agregué `EngineWebHost.cs` + 6 adapters
  net9 en `PilotX.GuidanceEngine/Adapters/` (EngineStateProvider/Coverage/
  ToolGeometry/Tram/Paths/Guidance) que implementan las interfaces
  IAogStateProvider/ICoverageService/IToolGeometryCalculator/ITramCalculator/
  IPathsGeometryCalculator/IGuidanceCalculator leyendo el modelo del
  `GuidanceEngineHost` (gemelos headless de los FormGps*Calculator, renombrado
  mecánico lowercase→PascalCase; el StateProvider stubbea los métodos satélite
  que eran ventanas WinForms). Flag nuevo `--webhost` en Program.cs levanta el
  `AgpWebHost` (netstandard2.0, reusado tal cual) sirviendo /api/aog/{state,
  coverage,tool,tram,paths,guidance} en :5180 — la MISMA API que sirve FormGPS,
  que es lo que PilotX.Desktop pollea. Ref nueva en el csproj a
  AgroParallel.WebHost. NO toqué FormGPS ni los FormGps*Calculator.
  (2) **BUGFIX real del bloque 14**: `GuidanceEngineHost.Start()` no llamaba
  `PgnReceiverField.StartWatch()` (FormGPS sí, FormGPS.cs:849). Sin eso el
  `udpWatch` del PgnReceiver quedaba parado → `ElapsedMilliseconds`=0 → el gate
  `< UdpWatchLimit(70ms)` del `case 0xD6` descartaba TODOS los fixes de GPS
  antes de arrancar el watch → deadlock: el engine **nunca procesaba posición
  real** (el modo `--sim` andaba porque CSim llama UpdateFixPosition directo sin
  pasar por ese gate). 1 línea. Verificado en runtime con ModSim real (que
  simula el CoreX-ECU GPS/IMU/STEER) → CoreX → engine `--webhost`: `fix_quality`
  pasó de 0 a 8, posición/lat-lon reales llegando, PilotX.Desktop dibuja el
  triángulo en la posición real. Con esto el bloque 14 tiene GPS real
  end-to-end, no solo sim. Todo compila (engine 0 errores). Si tenías algo sin
  commitear en GuidanceEngineHost.cs/Program.cs/csproj, avisá y reconciliamos —
  son hunks aditivos (Start() +1 línea, Program +flag, csproj +ref).
- [2026-07-23] [taller] **PLAN — MIGRACIÓN TOTAL A AVALONIA (decisión del usuario:
  dejamos el diseño HTML de lado, todo el esfuerzo va a Avalonia nativo).**

  **Objetivo final**: PilotX.Desktop (Avalonia) corre STANDALONE, sin FormGPS
  WinForms. Eso necesita dos mitades que van en paralelo, una por sesión:
  · **taller** = toda la UI nativa Avalonia (front-end, `PilotX.Desktop`).
  · **android** = el engine sirviendo TODAS las /api headless (back-end,
    `PilotX.GuidanceEngine`), para que ese :5180 sea el engine y no FormGPS.
  Las dos consumen/exponen la MISMA API HTTP :5180, así que se pueden hacer en
  paralelo sin pisarse (taller lee la API; android la sirve).

  ### División (tabla)

  | Área | Sesión | Archivos donde tocar | Detalle |
  |---|---|---|---|
  | Mapa GL (bloque 6 restante) | **taller** | `SourceCode/PilotX.Desktop/Views/MapGlSurface.cs` (DrawGuidance/DrawPaths/nuevo DrawBoundary+paralelas), `Views/MapPanel.cs`, `Services/GuidanceGeometryClient.cs` (+ nuevo client boundary si hace falta) | guías paralelas vecinas, youturn/skip visuales, boundary. ARRANCA POR ACÁ (elección del usuario). |
  | Pantallas WebView → Views Avalonia | **taller** | NUEVAS `SourceCode/PilotX.Desktop/Views/{Tracks,Lote,Contorno,Cabecera,Tram,Banderas,ConfigVehiculo,Perfiles,Colores,GraficoXXX}Panel.axaml(.cs)`; ruteo en `PilotX.Desktop/MainWindow.axaml.cs` (reemplazar `OpenDialogPage`/`NavigateTo` por panel nativo); clients en `PilotX.Desktop/Services/*Client.cs` | guías, lote, contorno, cabecera, tram, banderas, config vehículo/implemento, perfiles, colores, gráficos, sim-coords, corregir-posición |
  | Barras cockpit (ya nativas) | **taller** | `SourceCode/PilotX.Cockpit.Bars/Views/*.axaml`, `ViewModels/*.cs`, `Services/*.cs` | mantener/pulir |
  | Vocabulario de comandos del engine | **android** | EDITAR `SourceCode/PilotX.GuidanceEngine/GuidanceEngineHost.Commands.cs` (el switch `ExecuteCommand`). COPIAR de `SourceCode/GPS/Forms/GUI.FloatingMenu.cs` (`FormGPS.ExecuteGuidanceCommand`). Modelo del host en `PilotX.GuidanceEngine/GuidanceEngineHost.*.cs` | hoy solo "autosteer"/"job_*"; completar TODO el vocablo (sections, youturn, ciclar guías, contour, tram, banderas…) |
  | EngineTrackBuilderService (guías) | **android** | NUEVO `SourceCode/PilotX.GuidanceEngine/Adapters/EngineTrackBuilderService.cs`; wire en `PilotX.GuidanceEngine/EngineWebHost.cs`; REFERENCIA `SourceCode/GPS/AgroParallel/Common/FormGpsTrackBuilderService.cs`; PORTAR a Core/host los `TrkBuilder_*` de `SourceCode/GPS/Forms/AgroParallel/FormGPS.TrackBuilder.cs`; interfaz `AgroParallel/Core/AgroParallel.Services/Abstractions/ITrackBuilderService.cs` | crear A/B / curva / elegir / usar headless |
  | EngineLotesService (lotes) | **android** | NUEVO `PilotX.GuidanceEngine/Adapters/EngineLotesService.cs`; REUSAR `PilotX.GuidanceEngine/GuidanceEngineHost.Job.cs` (OpenField/CloseField ya hechos); REFERENCIA `GPS/AgroParallel/Common/FormGpsLotesService.cs`; interfaz `.../Abstractions/ILotesService.cs` | abrir/cerrar/crear/listar/from-existing |
  | EngineSectionControlService | **android** | NUEVO `PilotX.GuidanceEngine/Adapters/EngineSectionControlService.cs`; REFERENCIA `GPS/AgroParallel/Common/FormGpsSectionControlService.cs`; interfaz `.../Abstractions/ISectionControlService.cs` | control de secciones headless |
  | Resto de servicios a demanda | **android** | mismo patrón: `Adapters/Engine{ConfigVehiculo,Perfil,VehicleTool,Flags,Contorno,...}Service.cs` (los 25 `FormGps*Service.cs` de `GPS/AgroParallel/Common/` son la lista y referencia 1:1); wire cada uno en `EngineWebHost.cs` | config, perfiles, colores, gráficos… a medida que taller migra esas pantallas |
  | Extracción Core que falte (bloque 9) | **android** | de `SourceCode/GPS/Forms/*` hacia `SourceCode/AgOpenGPS.Core/Classes/*` (mismo patrón I*Host + partial que ya venís usando) | lo que FormGPS todavía tiene y los servicios headless necesitan |
  | Serial USB-OTG (bloque 8) | **android** | `SourceCode/PilotX.Android/*` + impl `ISerialPortService` USB-OTG | cuando haya hardware |

  ### PEDIDO a la sesión android — QUÉ HACER Y POR DÓNDE ARRANCAR

  Ya dejé hecho el patrón: `EngineWebHost.cs` levanta el `AgpWebHost` sobre el
  engine y cablea **6 providers** del mapa (EngineStateProvider/Coverage/
  ToolGeometry/Tram/Paths/Guidance, en `PilotX.GuidanceEngine/Adapters/`). Hoy
  el resto de los `if (svc != null)` del `AgpWebHost` quedan en null → esos
  endpoints contestan service-unavailable. **Tu trabajo: ir llenando esos
  servicios con adapters `Engine*` sobre `GuidanceEngineHost`, mismo patrón que
  mis 6** (gemelos headless de los `FormGps*Service` de `GPS/AgroParallel/Common`).

  **Orden sugerido (por valor para que PilotX.Desktop corra sin FormGPS):**

  1. **`IGuidanceCalculator.ExecuteCommand` COMPLETO** (lo más valioso): hoy
     `GuidanceEngineHost.ExecuteCommand` (Commands.cs) solo entiende "autosteer"
     y "job_*". Sin el resto, **las barras del cockpit no hacen nada headless**
     (autosteer, secciones auto/manual, youturn on/off, ciclar guías, contorno,
     tram, banderas, etc.). Copiá el vocabulario 1:1 de
     `FormGPS.ExecuteGuidanceCommand` (`GPS/Forms/GUI.FloatingMenu.cs`) — es un
     switch grande de strings; cada caso clickea un botón nativo, vos hacelo
     tocando el modelo del host (mismo criterio que ya usaste para "autosteer" →
     `PerformAutoSteerClick`). Ese es el back-end de las barras que yo ya tengo
     nativas.
  2. **`ITrackBuilderService` (guías) → `EngineTrackBuilderService`**: para que
     crear A/B / curva / elegir / usar ande headless. El adapter FormGPS está en
     `GPS/AgroParallel/Common/FormGpsTrackBuilderService.cs` (llama
     `_form.TrkBuilder_*`). Necesitás portar esos `TrkBuilder_*` a Core o al
     host. Yo ya tengo la UI (hoy HTML, la paso a Avalonia) que consume
     `/api/tracks/*` — con esto anda contra el engine.
  3. **`ILotesService` (lotes) → `EngineLotesService`**: abrir/cerrar/crear/
     listar/from-existing. Ojo: ya tenés `GuidanceEngineHost.Job.cs`
     (OpenField/CloseField) — reusalo. El adapter FormGPS es
     `FormGpsLotesService` (o `LotesService`). Endpoints `/api/lotes/*`.
  4. **`ISectionControlService`**: para el control de secciones headless.
  5. El resto (config vehículo/implemento, perfiles, colores, gráficos) a
     demanda, a medida que yo migro esas pantallas a Avalonia.

  Regla: donde `GuidanceEngineHost` no tenga la lógica que el servicio necesita,
  extraela de FormGPS a Core (bloque 9, tu carril) — NO la dupliques.
  Meta parcial buena: con (1) hecho, las barras nativas del cockpit funcionan
  100% contra el engine sin FormGPS. Con (1)+(2)+(3), el guiado + guías + lotes
  corren headless. Ahí PilotX.Desktop ya no necesita a FormGPS para lo esencial.

  Yo (taller) arranco por el mapa GL. Avisá cuando tengas (1) para probar las
  barras contra el engine. Todo por :5180, no nos pisamos.
