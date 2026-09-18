# Marcar giro — límites de U-turn sin recorrer el lote

**Fecha:** 2026-08-10 · **Estado:** diseño aprobado (Leonardo) · **Pendiente:** PEDIDO a Santiago (motor)

## Problema

Hoy el U-turn automático exige lindero: sin recorrer todo el perímetro no hay
línea de giro y el piloto no dobla solo. En lotes cuadrados (o cuando el
operario quiere fijar la cabecera en un punto arbitrario) recorrer el lote es
tiempo perdido.

## Qué se construye

Un botón **"Marcar giro"** en el menú de abajo (al lado de Guías, visible con
lote y guía activa). Al tocarlo, se genera una **línea perpendicular a la guía
activa** por la posición actual del tractor (pivote). Esa línea vale a lo ancho
de todas las pasadas y es el límite de giro de ese extremo. Se marca una por
extremo. El U-turn automático dispara contra esas líneas.

### Reglas de uso

- **Marcar**: un toque = línea perpendicular a la guía en la posición actual.
  El extremo (A o B) se determina por la posición sobre la guía: cada marca
  pertenece al lado del lote donde se hizo.
- **Corregir**: re-marcar del mismo lado PISA la marca anterior. No hay edición.
- **Borrar**: opción "Borrar marcas" en el mismo menú (borra las dos).
- **Con o sin lindero**: donde hay marca, manda la marca; donde no hay, sigue
  valiendo el lindero (si existe).

## Cómo gira (integración con el motor)

Las marcas NO agregan lógica de giro: se materializan como **cabecera virtual
que entra por el circuito existente del U-turn** (mismos ajustes de radio,
distancia y skips de siempre).

- **Sin lindero**: las dos líneas perpendiculares + dos costados lejanos
  (±2 km sobre la dirección de la guía) forman un **rectángulo virtual** que
  hace de línea de giro. Para CYouTurn es un lindero más.
- **Con lindero**: la línea de giro derivada del lindero se **recorta** con el
  semiplano de cada marca (la marca acerca la cabecera; nunca la aleja del
  lindero real — no se puede girar afuera del lote).
- **Sin marcas: cero cambio de comportamiento.** El camino nuevo no corre.
  Esta es la garantía de "no rompe nada".
- Como la marca entra como LINDERO virtual, las secciones también cortan
  pasando la marca (mismo comportamiento que un lindero real). Coherente con
  "se comporta como lindero". La cabecera de secciones real
  (`isSectionControlledByHeadland`) no se toca.
- El lindero virtual NUNCA se guarda en `Boundary.txt` ni se dibuja como
  lindero (solo se dibujan las dos líneas marcadas). Un flag
  `isVirtualTurnBoundary` lo excluye de guardado, snapshot y estadísticas.

## Persistencia

Archivo nuevo en la carpeta del lote, junto a `Boundary.txt`:
`TurnMarks.txt` — formato texto plano estilo AOG:

```
$TurnMarks
<easting_A>,<northing_A>,<heading_guia_A>
<easting_B>,<northing_B>,<heading_guia_B>
```

(una línea por marca; 0, 1 o 2 marcas). Se carga en `OpenField`, se limpia en
`CloseField`, y entra en el sync a OrbitX como el resto de los archivos del
lote (`EnqueueAOGFiles` lo levanta por estar en la carpeta).

## API (Hub :5180, snake_case)

- `GET  /api/aog/turnmarks` → `{ marks: [{e, n, heading}], activo: bool }`
- `POST /api/aog/turnmarks/marcar` → marca en la posición actual (el motor
  decide el extremo). Respuesta: la lista resultante.
- `POST /api/aog/turnmarks/borrar` → borra ambas.

La UI nativa (PilotX.Desktop) usa estos endpoints; el mapa dibuja las líneas
desde el mismo `GET` (misma fuente que el resto de la geometría de guiado).

## UI

- Botón "Marcar giro" + "Borrar marcas" en el menú de abajo junto a Guías
  (PilotX.Cockpit.Bars / MainWindow según dónde viva ese menú hoy).
- Guards: requiere lote abierto + guía activa + GPS. Sin eso, deshabilitado.
- Feedback: al marcar, la línea aparece en el mapa (color de cabecera).
- `ayuda.html` se actualiza EN EL MISMO commit que agregue el botón.

## Validación

1. Banco con ModSim: marcar ambos extremos, verificar que el U-turn dispara en
   la línea en los dos sentidos; con y sin lindero; releer el lote y verificar
   persistencia.
2. Cabina/campo antes de declararlo `anda` en el tablero.

## Carriles

Cambio 2026-08-10 (pedido de Leonardo): **todo por este carril** — motor, API,
persistencia y UI. Sin PEDIDO a Santiago.

## Riesgos

- Recorte de la línea de giro con lindero: geometría con casos borde (marca
  afuera del lindero, lindero cóncavo). Regla: la marca solo ACERCA la
  cabecera; si el recorte da vacío, se ignora la marca y se loguea.
- Marca con guía luego cambiada (otra AB con otro rumbo): las marcas quedan
  atadas al rumbo con el que se crearon; si la guía activa cambia de rumbo
  más de ~20°, las marcas se muestran pero el motor las ignora (aviso en UI).
