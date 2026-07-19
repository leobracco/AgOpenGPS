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
| taller | Migración VistaX nativo → Hub (gap grande de faltantes) | wwwroot/pages/vistax*.html, js/vistax*.js, AgroParallel.Services VistaX* |
| android | Extracción I*Host de Position.designer/Sections.Designer (bloque 9) + primer call site GL→GLW en OpenGL.Designer (bloque 6); probando en runtime (build.ps1 + simulador) antes de commitear | `Position.designer.cs`, `Sections.Designer.cs`, `OpenGL.Designer.cs`, `FormGPS.cs`, `AgOpenGPS.Core/Interfaces/{IPositionHost,ISectionsHost}.cs`, `AgOpenGPS.Core/Classes/{CPositionUpdater,CSectionCalculator}.cs`, `AgOpenGPS.Core/DrawLib/GLW.Primitives.cs`, `GPS/AgroParallel/Common/FormGps.{PositionHost,SectionsHost}.cs` |

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
