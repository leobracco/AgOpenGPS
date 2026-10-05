# Implemento unificado: una sola configuración, trenes y corte exacto

**Fecha:** 2026-08-01 · **Estado:** aprobado por el usuario (sesión de banco/lote)

## Problema

La configuración del implemento vive repartida y duplicada:

| Dato | Dónde vive hoy | Quién lo consume |
|---|---|---|
| Ancho de labor, secciones, enganche | `/api/tool` (pantalla "Implemento PilotX") | guiado, pintado, anti-solape, dosis kg/ha |
| Surcos, distancia entre hileras | `/api/implemento` (pantalla "Implemento") — editable | sem/ha en pantalla, calculadora |
| Trenes (lista + distancia) | `/api/implemento`, editable, **nadie lo consume** | — |
| Distancia entre trenes p/ corte | `sectionX.json` **por nodo** | `SectionXCutAdapter` |
| Distancia entre trenes p/ dosis | `quantiX_motores.json` **por nodo** + `tren` por motor | bridge QuantiX |

Consecuencias ya sufridas: sem/ha 2,7× más alto por un implemento desfasado
(2026-08-01), y la distancia entre trenes cargable en tres lugares con tres
valores potencialmente distintos.

## Decisiones tomadas (con el usuario)

1. **Una sola pantalla.** "Implemento PilotX" (`config-implemento.html`) absorbe
   trenes + asignación surco→tren. La pantalla "Implemento"
   (`herramienta.html`) sale del menú.
2. **N trenes**, no un par fijo delantero/trasero. El modelo actual
   (`trenes[]` con `distancia_m`) ya lo soporta.
3. **La cobertura no cambia** por ahora: el mapa pinta con la geometría única
   de PilotX. El corte por tren se aplica al dosificador/relés, que es donde
   importa. Capa de pintado por tren queda para más adelante.

## Diseño

### Modelo (sin campos nuevos, con dueños nuevos)

- `/api/tool` — **dueño de la geometría de labor**: `width`, `numSections`,
  `sectionWidths`, enganche, lookahead. Nada nuevo acá; no se toca el núcleo.
- `/api/implemento` (`ImplementoDto`) — **dueño de la sembradora**:
  - `trenes[]`: `{ id, nombre, distancia_m }`. El tren `id=1` es el delantero,
    `distancia_m = 0` fijo. Los demás: metros HACIA ATRÁS del delantero.
    Validación: `0 ≤ distancia_m ≤ 20`.
  - `surcos[]`: `{ numero, tren_id }`. La cantidad de surcos ES
    `tool.numSections` (una sección = un surco): al cambiar las secciones en
    la misma pantalla, `surcos[]` se regenera conservando `tren_id` por índice.
  - `numero_surcos` y `distancia_entre_surcos_m` pasan a ser **derivados**
    (espejo de `tool`): se siguen escribiendo en el JSON por compatibilidad,
    pero ninguna pantalla los edita. (Ya implementado hoy.)

### Derivación del tren (elimina las copias por nodo)

- **QuantiX**: el tren de un motor = tren del/los surcos en `motor.cortes[]`
  (reemplaza el campo `tren` manual por motor). Quien aplica el retardo sigue
  siendo la cadena de corte (CutDispatcher/SectionX); el motor solo necesita
  saber a qué tren pertenece para que su estado de sección llegue retardado.
- **SectionX**: el tren de un cable = tren del surco (`SeccionAOG`) que maneja.
  `nodo.DistanciaEntreTrenes` deja de ser fuente.
- **Conflicto** (motor o cable con surcos de trenes distintos): warning visible
  en la pantalla del producto + se usa el tren del primer surco. No se promedia.
- **Fallback fase 1**: si el implemento no tiene trenes cargados (o un solo
  tren), se sigue usando el valor por-nodo existente para no romper configs
  en producción. Log al arrancar diciendo cuál fuente se usó.

### El corte exacto no se toca

`SectionXCutAdapter.ComputePublishes` y
`PositionHistory.GetSectionsAtDistanceBack(d)` quedan como están: reproducen
qué secciones estaban abiertas cuando el tractor estaba `d` metros atrás
(corte por posición, no por tiempo). Solo cambia el origen de `d`.

### Pantalla (config-implemento.html)

Sección nueva "Trenes de siembra" debajo de las secciones:

- Lista de trenes: nombre + distancia (el delantero fijo en 0, no editable).
  Botones agregar/quitar (mínimo 1, máximo 4).
- Tira de surcos (mismo componente visual del planter QuantiX): pincel por
  tren, pintás qué surcos van a cada tren. Con 1 tren la tira no se muestra.
- Guardar escribe: geometría → `PUT /api/tool`, sembradora →
  `PUT /api/implemento`, en ese orden; si falla la segunda se avisa y NO se
  revierte la primera (el guiado siempre queda consistente).
- `herramienta.html` sale de `sidebar.js`. El archivo queda (deep-links,
  rollback fácil) con un aviso "esta pantalla se movió".

Los trenes/cables de las pantallas QuantiX y SectionX pasan a solo-lectura
mostrando el valor derivado + link "se configura en Implemento".

### Fases

- **Fase 1** (esta): modelo derivado + pantalla unificada + fallback a las
  copias por nodo. Reversible sin pérdida.
- **Fase 2** (tras validar en lote): eliminar `DistanciaEntreTrenes` de
  `sectionX.json`/`quantiX_motores.json` y el campo `tren` por motor.

### Testing

- Unit: derivación surco→tren→distancia (motor con surcos de un tren, de dos
  trenes → warning, sin trenes → fallback), regeneración de `surcos[]` al
  cambiar `numSections` (crece, achica, conserva asignación).
- Los tests existentes de `SectionXCutAdapter` y `QxRuntimeBuilder` no deben
  cambiar de resultado con implemento de 1 tren (garantía de no-regresión).
- En cabina (usuario): con 14 secciones y 2 trenes, verificar que el tren
  trasero corta N metros después del delantero sobre lo pintado.

### Guardas de seguridad

- Todo cambio que altere qué sección aplica producto queda detrás del
  fallback fase 1 hasta validarse en lote (regla CLAUDE.md).
- Validación al guardar: ningún surco sin tren, distancias en rango,
  suma de anchos de sección ≈ ancho total (±5 cm) — se avisa, no se bloquea.
