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
| taller | idle — última tanda: auditorías de duplicados/faltantes UI | — |
| android | (completar al arrancar) | — |

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
