# Overlay QuantiX — grilla de N motores — 2026-08-20

## Problema
El overlay de QuantiX sobre el mapa (`QuantiXMapOverlay.cs`) muestra un motor
por **fila horizontal** (● desvío · nombre · dosis grande · /obj · MAN). Con 14
motores son ~380 px de alto: tapa media pantalla. La venta de esta semana lleva
14 motores; el listado no escala.

## Decisión (aprobada)
La lista de filas pasa a una **grilla de celdas numeradas**.

### Grilla
- Cada motor = celda con **solo su número** (1…N, orden de `MotoresVisibles()`),
  pintada por desvío: verde ±5 %, ámbar ±15 %, rojo más allá (mismo criterio de
  `FilaMotor`). Apagado/sin dosis = gris (`TextoDim`); offline = gris más apagado.
- **NO** se muestra el MANUAL en la grilla (queda solo número + color).
- Celda tocada → se resalta (borde `Acento`) y su motor pasa al detalle de abajo.
  Tocar de nuevo deselecciona (toggle, como hoy).
- Táctil: celda ~40×36 px, gap 4.

### Reparto automático (N variable)
- Máximo 7 columnas, filas balanceadas: `filas = ceil(N/7)`,
  `cols = ceil(N/filas)`. Ejemplos: 3→1×3, 8→2×4, 10→2×5, **14→2×7**, 15→3×5.
- El ancho del overlay crece con las columnas (es arrastrable, el mapa se ve).

### Detalle (sin cambios)
- El motor elegido: dosis real grande + `/ obj` + color + **MAN** si aplica + la
  barra de control `[−] [+]`. Sin selección, la barra afuera.

### Header
- `QuantiX  ● N motores`.

## Alcance
Solo `SourceCode/PilotX.UI/Views/QuantiXMapOverlay.cs`: el bloque que arma
`_filas` (Render + FilaMotor) pasa de StackPanel de filas a una grilla. Se
conserva intacto: selección (`_selUid`/`_selIdx`), la barra horizontal, el
arrastre (`HabilitarArrastre`/`OnMovido`), el polling, `MotoresVisibles()`, el
formato de dosis y las unidades (nunca pps). Sin cambios de datos ni de servidor.

## Fuera de alcance
El overlay de FlowX (un solo nodo, no aplica). Config de columnas manual (la
regla es automática). Marcar manual/offline con símbolos en la grilla.
